using System.Collections.Immutable;
using System.Globalization;

namespace Sable.SIL;

/// <summary>A malformed piece of SIL text, with the line it is on.</summary>
public sealed class SilParseException(string message, int line) : Exception($"line {line}: {message}") {
    public int Line { get; } = line;
}

/// <summary>
///     Reads the text <see cref="SilPrinter" /> writes, so that a SIL test can
///     be a <c>.sil</c> file rather than a builder program.
/// </summary>
/// <remarks>
///     Value names are local to a function. A use may come before its
///     definition in the text — the block that defines it may be printed
///     later — so a name used early is a placeholder until it is defined, and
///     a name never defined is an error at the end of the function.
/// </remarks>
public sealed class SilParser {
    readonly List<Token> _tokens;
    int _index;

    SilParser(string text) {
        _tokens = Tokenize(text);
    }

    public static SilModule Parse(string text) => new SilParser(text).ParseModule();

    // ---- Tokens -------------------------------------------------------------

    enum TokenKind {
        Word, // sil, integer_literal, bb0, init, Int
        Global, // @name
        Local, // %0
        Number,
        String,
        Punctuation,
        End,
    }

    readonly record struct Token(TokenKind Kind, string Text, int Line);

    static List<Token> Tokenize(string text) {
        var tokens = new List<Token>();
        var line = 1;
        var i = 0;

        static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '$';

        while (i < text.Length) {
            var c = text[i];

            if (c == '\n') {
                line++;
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c)) {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') {
                while (i < text.Length && text[i] != '\n') {
                    i++;
                }

                continue;
            }

            var start = i;

            if (c is '@' or '%') {
                i++;
                while (i < text.Length && IsNameChar(text[i])) {
                    i++;
                }

                tokens.Add(new(c == '@' ? TokenKind.Global : TokenKind.Local, text[(start + 1)..i], line));
                continue;
            }

            if (c == '"') {
                i++;
                while (i < text.Length && text[i] != '"' && text[i] != '\n') {
                    i++;
                }

                if (i >= text.Length || text[i] != '"') {
                    throw new SilParseException("unterminated string", line);
                }

                tokens.Add(new(TokenKind.String, text[(start + 1)..i], line));
                i++;
                continue;
            }

            if (char.IsDigit(c) || (c == '-' && i + 1 < text.Length && char.IsDigit(text[i + 1]))) {
                i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '.' or '+' or '-')
                                       && !(text[i] is '-' && text[i - 1] is not ('e' or 'E'))) {
                    i++;
                }

                tokens.Add(new(TokenKind.Number, text[start..i], line));
                continue;
            }

            if (char.IsLetter(c) || c == '_') {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) {
                    i++;
                }

                tokens.Add(new(TokenKind.Word, text[start..i], line));
                continue;
            }

            if (c == '-' && i + 1 < text.Length && text[i + 1] == '>') {
                tokens.Add(new(TokenKind.Punctuation, "->", line));
                i += 2;
                continue;
            }

            if ("(){}[]:,=$*".Contains(c, StringComparison.Ordinal)) {
                tokens.Add(new(TokenKind.Punctuation, c.ToString(), line));
                i++;
                continue;
            }

            throw new SilParseException($"unexpected character '{c}'", line);
        }

        tokens.Add(new(TokenKind.End, "", line));
        return tokens;
    }

    Token Current => _tokens[_index];

    Token Next() => _tokens[_index++];

    bool At(string punctuationOrWord) => Current.Kind is TokenKind.Punctuation or TokenKind.Word && Current.Text == punctuationOrWord;

    bool Accept(string text) {
        if (!At(text)) {
            return false;
        }

        _index++;
        return true;
    }

    void Expect(string text) {
        if (!Accept(text)) {
            throw Error($"expected '{text}' but found '{Describe(Current)}'");
        }
    }

    Token Expect(TokenKind kind, string what) {
        if (Current.Kind != kind) {
            throw Error($"expected {what} but found '{Describe(Current)}'");
        }

        return Next();
    }

    static string Describe(Token token) =>
        token.Kind switch {
            TokenKind.End => "end of input",
            TokenKind.Global => "@" + token.Text,
            TokenKind.Local => "%" + token.Text,
            _ => token.Text,
        };

    SilParseException Error(string message) => new(message, Current.Line);

    // ---- Module and function ------------------------------------------------

    SilModule ParseModule() {
        var module = new SilModule();
        while (Current.Kind != TokenKind.End) {
            var function = ParseFunction();
            if (module.Find(function.Name) is not null) {
                throw Error($"@{function.Name} is defined twice");
            }

            module.Add(function);
        }

        return module;
    }

    // Per function: names to values, names to blocks, and uses waiting for a definition.
    Dictionary<string, SilValue> _values = [];
    Dictionary<string, SilBlock> _blocks = [];
    HashSet<SilBlock> _definedBlocks = [];
    List<(SilInstruction User, int Operand, string Name, int Line)> _pending = [];
    SilFunction _function = null!;

    SilFunction ParseFunction() {
        Expect("sil");
        var name = Expect(TokenKind.Global, "a function name").Text;
        Expect(":");
        if (ParseType() is not FunctionType type) {
            throw Error($"@{name} must have a function type");
        }

        _function = new SilFunction(name, type);
        _values = new(StringComparer.Ordinal);
        _blocks = new(StringComparer.Ordinal);
        _definedBlocks = [];
        _pending = [];

        Expect("{");
        while (!At("}")) {
            ParseBlock();
        }

        Expect("}");

        foreach (var (block, label) in _blocks.Select(static p => (p.Value, p.Key))) {
            if (!_definedBlocks.Contains(block)) {
                throw Error($"{label} is used but never defined in @{name}");
            }
        }

        foreach (var (user, operand, valueName, line) in _pending) {
            if (!_values.TryGetValue(valueName, out var value)) {
                throw new SilParseException($"%{valueName} is used but never defined", line);
            }

            user.SetOperand(operand, value);
        }

        // Blocks were created in order of first mention; put them in the
        // order they are written.
        var order = _blocks.Values.ToDictionary(static b => b, b => _blockOrder[b]);
        _function.Blocks.Sort((a, b) => order[a].CompareTo(order[b]));
        _blockOrder.Clear();

        return _function;
    }

    readonly Dictionary<SilBlock, int> _blockOrder = [];

    SilBlock BlockNamed(string label) {
        if (!_blocks.TryGetValue(label, out var block)) {
            _blocks[label] = block = _function.AddBlock();
        }

        return block;
    }

    void ParseBlock() {
        var labelToken = Expect(TokenKind.Word, "a block label");
        var block = BlockNamed(labelToken.Text);
        if (!_definedBlocks.Add(block)) {
            throw new SilParseException($"{labelToken.Text} is defined twice", labelToken.Line);
        }

        _blockOrder[block] = _blockOrder.Count;

        if (Accept("(")) {
            do {
                var name = Expect(TokenKind.Local, "an argument name").Text;
                Expect(":");
                Define(name, block.AddArgument(ParseType()));
            } while (Accept(","));

            Expect(")");
        }

        Expect(":");

        while (Current.Kind != TokenKind.End && !At("}") && !IsBlockLabelAhead()) {
            block.Append(ParseInstruction());
        }
    }

    bool IsBlockLabelAhead() =>
        Current.Kind == TokenKind.Word
        && Current.Text.StartsWith("bb", StringComparison.Ordinal)
        && _tokens[_index + 1].Text is ":" or "(";

    void Define(string name, SilValue value) {
        if (!_values.TryAdd(name, value)) {
            throw Error($"%{name} is defined twice");
        }
    }

    // ---- Types --------------------------------------------------------------

    SilType ParseType() {
        Expect("$");
        return ParseTypeBody();
    }

    SilType ParseTypeBody() {
        if (Accept("*")) {
            return SilType.Address(ParseTypeBody());
        }

        if (Accept("(")) {
            var parts = ImmutableArray.CreateBuilder<SilType>();
            if (!At(")")) {
                do {
                    parts.Add(ParseTypeBody());
                } while (Accept(","));
            }

            Expect(")");

            if (Accept("->")) {
                return SilType.Function(parts.ToImmutable(), ParseTypeBody());
            }

            if (parts.Count == 0) {
                return SilType.Unit;
            }

            throw Error("a parenthesised type list must be a function's parameters, or '()'");
        }

        var name = Expect(TokenKind.Word, "a type").Text;
        return name switch {
            "Int" => SilType.Int,
            "Bool" => SilType.Bool,
            "F64" => SilType.F64,
            _ => throw Error($"unknown type '{name}'"),
        };
    }

    // ---- Instructions -------------------------------------------------------

    SilInstruction ParseInstruction() {
        string? result = null;
        if (Current.Kind == TokenKind.Local) {
            result = Next().Text;
            Expect("=");
        }

        var line = Current.Line;
        var opcode = Expect(TokenKind.Word, "an instruction").Text;
        var operands = new List<(int Index, string Name, int Line)>();

        SilValue Operand(int index) {
            var token = Expect(TokenKind.Local, "a value");
            if (_values.TryGetValue(token.Text, out var value)) {
                return value;
            }

            operands.Add((index, token.Text, token.Line));
            return Placeholder.Instance;
        }

        List<SilValue> OperandList(int firstIndex) {
            var list = new List<SilValue>();
            Expect("(");
            if (!At(")")) {
                do {
                    list.Add(Operand(firstIndex + list.Count));
                } while (Accept(","));
            }

            Expect(")");
            return list;
        }

        (SilBlock Block, List<SilValue> Arguments) Target(int firstIndex) {
            var label = Expect(TokenKind.Word, "a block label").Text;
            var arguments = At("(") ? OperandList(firstIndex) : [];
            return (BlockNamed(label), arguments);
        }

        SilInstruction instruction;
        switch (opcode) {
            case "integer_literal": {
                var type = ParseType();
                Expect(",");
                var text = Expect(TokenKind.Number, "an integer").Text;
                if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)) {
                    throw new SilParseException($"'{text}' is not an integer", line);
                }

                instruction = new IntegerLiteralInst(type, value);
                break;
            }

            case "float_literal": {
                ParseType();
                Expect(",");
                var text = Expect(TokenKind.Number, "a number").Text;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) {
                    throw new SilParseException($"'{text}' is not a number", line);
                }

                instruction = new FloatLiteralInst(value);
                break;
            }

            case "builtin": {
                var name = Expect(TokenKind.String, "a builtin name").Text;
                var arguments = OperandList(0);
                Expect(":");
                instruction = new BuiltinInst(name, ParseType(), [..arguments]);
                break;
            }

            case "alloc_stack":
                instruction = new AllocStackInst(ParseType());
                break;

            case "dealloc_stack":
                instruction = new DeallocStackInst(Operand(0));
                break;

            case "load":
                Expect("[");
                Expect("trivial");
                Expect("]");
                instruction = new LoadInst(Operand(0));
                break;

            case "store": {
                var value = Operand(0);
                Expect("to");
                Expect("[");
                var kind = Expect(TokenKind.Word, "'init' or 'assign'").Text switch {
                    "init" => StoreKind.Init,
                    "assign" => StoreKind.Assign,
                    var other => throw new SilParseException($"'{other}' is not a store kind", line),
                };
                Expect("]");
                instruction = new StoreInst(value, Operand(1), kind);
                break;
            }

            case "function_ref": {
                var name = Expect(TokenKind.Global, "a function name").Text;
                Expect(":");
                if (ParseType() is not FunctionType type) {
                    throw new SilParseException("function_ref needs a function type", line);
                }

                instruction = new FunctionRefInst(name, type);
                break;
            }

            case "apply": {
                var callee = Operand(0);
                var arguments = OperandList(1);
                Expect(":");
                instruction = new ApplyInst(callee, ParseType(), [..arguments]);
                break;
            }

            case "br": {
                var (block, arguments) = Target(0);
                instruction = new BranchInst(block, [..arguments]);
                break;
            }

            case "cond_br": {
                var condition = Operand(0);
                Expect(",");
                var (whenTrue, trueArguments) = Target(1);
                Expect(",");
                var (whenFalse, falseArguments) = Target(1 + trueArguments.Count);
                instruction = new CondBranchInst(condition, whenTrue, [..trueArguments], whenFalse, [..falseArguments]);
                break;
            }

            case "return":
                instruction = new ReturnInst(Current.Kind == TokenKind.Local ? Operand(0) : null);
                break;

            case "unreachable":
                instruction = new UnreachableInst();
                break;

            default:
                throw new SilParseException($"unknown instruction '{opcode}'", line);
        }

        foreach (var (index, name, useLine) in operands) {
            _pending.Add((instruction, index, name, useLine));
        }

        if (result is not null) {
            if (!instruction.HasResult) {
                throw new SilParseException($"'{opcode}' produces no value to name %{result}", line);
            }

            Define(result, instruction);
        } else if (instruction.HasResult && instruction is not ApplyInst) {
            throw new SilParseException($"the value '{opcode}' produces needs a name", line);
        }

        return instruction;
    }

    /// <summary>Stands in for a value used before it is defined, until the end of the function.</summary>
    sealed class Placeholder : SilValue {
        public static readonly Placeholder Instance = new();
        public override SilType Type => SilType.Unit;
    }
}
