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
        Word, // sil, integer_literal, bb0, init, Int, M.Point
        Global, // @name
        Member, // #x, #Circle
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

        // An operator's function is named for it, 'Sable.Core.Int.<': its
        // characters belong to the name when they follow a dot or each other.
        static bool IsOperatorName(string text, int at) =>
            IsOperatorChar(text[at]) && (text[at - 1] == '.' || IsOperatorChar(text[at - 1]));

        static bool IsOperatorChar(char c) => "<>=!+-*/%&|^~?".Contains(c, StringComparison.Ordinal);

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

            if (c is '@' or '%' or '#') {
                i++;
                while (i < text.Length && (IsNameChar(text[i]) || c == '@' && IsOperatorName(text, i))) {
                    i++;
                }

                var kind = c switch { '@' => TokenKind.Global, '%' => TokenKind.Local, _ => TokenKind.Member };
                tokens.Add(new(kind, text[(start + 1)..i], line));
                continue;
            }

            if (c == '"') {
                i++;
                var value = new System.Text.StringBuilder();
                while (i < text.Length && text[i] != '"' && text[i] != '\n') {
                    if (text[i] != '\\') {
                        value.Append(text[i++]);
                        continue;
                    }

                    if (i + 1 >= text.Length) {
                        break;
                    }

                    var escape = text[i + 1];
                    i += 2;
                    switch (escape) {
                        case '"': value.Append('"'); break;
                        case '\\': value.Append('\\'); break;
                        case 'n': value.Append('\n'); break;
                        case 't': value.Append('\t'); break;
                        case 'u' when i < text.Length && text[i] == '{': {
                            var close = text.IndexOf('}', i);
                            if (close < 0
                                || !int.TryParse(text[(i + 1)..close], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)
                                || !System.Text.Rune.IsValid(code)) {
                                throw new SilParseException("malformed \\u{…} escape", line);
                            }

                            value.Append(new System.Text.Rune(code).ToString());
                            i = close + 1;
                            break;
                        }

                        default:
                            throw new SilParseException($"unknown escape '\\{escape}'", line);
                    }
                }

                if (i >= text.Length || text[i] != '"') {
                    throw new SilParseException("unterminated string", line);
                }

                tokens.Add(new(TokenKind.String, value.ToString(), line));
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
                // A type's name may be qualified and mangled: M.Point, Optional$Int.
                while (i < text.Length && IsNameChar(text[i])) {
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

    /// <summary>A <c>[name]</c> flag, if one is next.</summary>
    bool AcceptFlag(string name) {
        if (!Accept("[")) {
            return false;
        }

        Expect(name);
        Expect("]");
        return true;
    }

    void Expect(string text) {
        if (!Accept(text)) {
            throw Error($"expected '{text}' but found '{Describe(Current)}'");
        }
    }

    /// <summary>A field's name: a word, or one the compiler made, '$' and a word ('$state', '$executor').</summary>
    string FieldName() => Accept("$") ? "$" + Expect(TokenKind.Word, "a field name").Text : Expect(TokenKind.Word, "a field name").Text;

    /// <summary>
    ///     A type's name: a word, or '$' and a word, as the frame of a function
    ///     the compiler made is named for it ('$task.run$Task$of$Int$frame').
    ///     Where a type is expected it is written '$$task.run…'.
    /// </summary>
    string TypeName(string what) => Accept("$") ? "$" + Expect(TokenKind.Word, what).Text : Expect(TokenKind.Word, what).Text;

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

    /// <summary>
    ///     A count, an index, a line or a column: digits that fit an int, as
    ///     anything else is malformed SIL rather than a number to read.
    /// </summary>
    int Count(string what) {
        var token = Expect(TokenKind.Number, what);
        return int.TryParse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new SilParseException($"expected {what} but found '{token.Text}'", token.Line);
    }

    // ---- Module and function ------------------------------------------------

    SilModule _module = null!;

    SilModule ParseModule() {
        var module = new SilModule();
        _module = module;

        // Every type's name first: a class or an array may name a type that
        // contains it, so no order of declarations would do.
        for (var i = 0; i + 1 < _tokens.Count; i++) {
            if (_tokens[i] is { Kind: TokenKind.Word, Text: "sil_struct" or "sil_enum" or "sil_class" } keyword
                && (i == 0 || _tokens[i - 1] is not { Kind: TokenKind.Punctuation, Text: "$" })) {
                // A name the compiler made is '$' and a word.
                var at = _tokens[i + 1] is { Kind: TokenKind.Punctuation, Text: "$" } ? i + 2 : i + 1;
                if (at >= _tokens.Count || _tokens[at].Kind != TokenKind.Word) {
                    continue;
                }

                var name = (at == i + 2 ? "$" : "") + _tokens[at].Text;
                if (module.FindType(name) is not null || name is "Int" or "Bool" or "F64" or "String" or "Char" or "Object" or "Region") {
                    throw new SilParseException($"the type {name} is declared twice", _tokens[at].Line);
                }

                module.AddType<NominalType>(keyword.Text switch {
                    "sil_struct" => new StructType(name),
                    "sil_enum" => new EnumType(name),
                    _ => new ClassType(name),
                });
            }
        }

        while (Current.Kind != TokenKind.End) {
            if (Accept("sil_struct")) {
                ParseStruct();
                continue;
            }

            if (Accept("sil_enum")) {
                ParseEnum();
                continue;
            }

            if (Accept("sil_class")) {
                ParseClass();
                continue;
            }

            if (Accept("sil_extern")) {
                var name = Expect(TokenKind.Global, "a function's name").Text;
                Expect(":");
                if (ParseType() is not FunctionType type) {
                    throw Error($"@{name} is a C function, so its type is a function type");
                }

                Expect("=");
                module.Foreign[name] = (Expect(TokenKind.String, "a C symbol").Text, type);
                if (Accept("variadic")) {
                    module.ForeignVariadic[name] = Count("the count of fixed parameters");
                }

                continue;
            }

            if (Accept("sil_export")) {
                var name = Expect(TokenKind.Global, "a function's name").Text;
                Expect("=");
                module.Exports[name] = Expect(TokenKind.String, "a C symbol").Text;
                continue;
            }

            if (Accept("sil_global")) {
                var global = Expect(TokenKind.Global, "a global's name").Text;
                Expect(":");
                module.Globals.Add((global, ParseType()));
                continue;
            }

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

    bool AcceptNoncopy() {
        if (Current is not { Kind: TokenKind.Global, Text: "noncopy" }) {
            return false;
        }

        Next();
        return true;
    }

    /// <summary>
    ///     The type a declaration names, which the first look over the tokens
    ///     made; one it did not make is malformed SIL, not a type to fill in.
    /// </summary>
    T Declared<T>(string what) where T : NominalType {
        var line = Current.Line;
        var name = TypeName(what);
        return _module.FindType(name) as T ?? throw new SilParseException($"expected {what} but found '{name}'", line);
    }

    void ParseStruct() {
        var type = Declared<StructType>("a struct name");
        type.IsNoncopyable = AcceptNoncopy();
        if (Current is { Kind: TokenKind.Global, Text: "inline" }) {
            Next();
            type.IsInline = true;
        }

        if (Current is { Kind: TokenKind.Global, Text: "packed" }) {
            Next();
            type.IsPacked = true;
        }

        if (Current is { Kind: TokenKind.Global, Text: "align" }) {
            Next();
            Expect("(");
            type.Alignment = Count("an alignment");
            Expect(")");
        }

        Expect("{");
        if (!At("}")) {
            do {
                var field = FieldName();
                Expect(":");
                type.Fields.Add((field, ParseType()));
            } while (Accept(","));
        }

        Expect("}");
        if (Accept("deinit")) {
            type.Deinit = Expect(TokenKind.Global, "the deinit function").Text;
        }
    }

    void ParseClass() {
        var type = Declared<ClassType>("a class name");
        if (Accept(":")) {
            type.Superclass = _module.FindType(TypeName("a superclass")) as ClassType
                ?? throw Error("a class's superclass must be a class");
        }

        Expect("{");
        if (!At("}")) {
            do {
                var field = FieldName();
                Expect(":");
                type.Fields.Add((field, ParseType()));
            } while (Accept(","));
        }

        Expect("}");
        if (Accept("vtable")) {
            Expect("{");
            if (!At("}")) {
                do {
                    var method = Expect(TokenKind.Word, "a method name").Text;
                    Expect(":");
                    type.VTable.Add((method, Expect(TokenKind.Global, "a function").Text));
                } while (Accept(","));
            }

            Expect("}");
        }

        if (Accept("deinit")) {
            type.Deinit = Expect(TokenKind.Global, "the deinit function").Text;
        }

        if (Accept("empty_from")) {
            type.EmptyFrom = Count("the first field that may start empty");
        }
    }

    void ParseEnum() {
        var type = Declared<EnumType>("an enum name");
        type.IsNoncopyable = AcceptNoncopy();
        Expect("{");
        if (!At("}")) {
            do {
                var @case = Expect(TokenKind.Word, "a case name").Text;
                SilType? payload = null;
                if (Accept("(")) {
                    payload = ParseType();
                    Expect(")");
                }

                type.Cases.Add((@case, payload));
            } while (Accept(","));
        }

        Expect("}");
    }

    SilFunction ParseFunction() {
        Expect("sil");
        var name = Expect(TokenKind.Global, "a function name").Text;
        Expect(":");
        if (ParseType() is not FunctionType type) {
            throw Error($"@{name} must have a function type");
        }

        _function = new SilFunction(name, type);
        if (Accept("roots")) {
            Expect("(");
            var roots = new List<int>();
            if (!At(")")) {
                do {
                    roots.Add(Count("a parameter index"));
                } while (Accept(","));
            }

            Expect(")");
            _function.ResultRoots = roots;
        }

        if (Accept("inline")) {
            Expect("(");
            _function.Inline = Accept("always") ? SilInline.Always
                : Accept("never") ? SilInline.Never
                : throw Error("expected 'always' or 'never'");
            Expect(")");
        }

        if (Accept("loc")) {
            _function.SourceFile = Expect(TokenKind.String, "a file name").Text;
            Expect(":");
            _function.Line = Count("a line");
        }

        _values = new(StringComparer.Ordinal);
        _blocks = new(StringComparer.Ordinal);
        _definedBlocks = [];
        _pending = [];
        _unresolvedCases = [];

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

        foreach (var (switched, named, line) in _unresolvedCases) {
            ResolveCases(switched, named, line);
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
                var guaranteed = Current is { Kind: TokenKind.Global, Text: "guaranteed" };
                if (guaranteed) {
                    Next();
                }

                Define(name, block.AddArgument(ParseType(), guaranteed));
                if (Accept("var")) {
                    _function.ParameterNames.Add(Expect(TokenKind.String, "a parameter name").Text);
                }
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
        if (Current is { Kind: TokenKind.Global, Text: "sil_weak" }) {
            Next();
            return ParseTypeBody() is EnumType optional
                ? new WeakStorageType(optional)
                : throw Error("@sil_weak needs an optional of a class");
        }

        if (Current is { Kind: TokenKind.Global, Text: "sil_unowned" }) {
            Next();
            return ParseTypeBody() is ClassType referent
                ? new UnownedStorageType(referent)
                : throw Error("@sil_unowned needs a class");
        }

        if (Current is { Kind: TokenKind.Global, Text: "thick" }) {
            Next();
            return ParseTypeBody() is FunctionType signature
                ? new ThickFunctionType(signature)
                : throw Error("@thick needs a function type");
        }

        if (Accept("*")) {
            if (Accept("inline")) {
                return new InlinePointerType(ParseTypeBody());
            }

            return SilType.Address(ParseTypeBody());
        }

        if (Current is { Kind: TokenKind.Global, Text: "inline_array" }) {
            Next();
            Expect("(");
            var element = ParseTypeBody();
            int? capacity = Accept(",") ? Count("a capacity") : null;
            Expect(")");
            return new InlineArrayType(element, capacity);
        }

        if (Current is { Kind: TokenKind.Global, Text: "relative" or "relative_optional" } relative) {
            Next();
            Expect("(");
            var target = ParseTypeBody() as StructType ?? throw Error("a relative reference needs an inline struct");
            Expect(",");
            var width = Count("a width");
            Expect(")");
            return new RelativeReferenceType(target, width, relative.Text == "relative_optional");
        }

        // '[T]' an array, '[T:]' a slice of one.
        if (Accept("[")) {
            var element = ParseTypeBody();
            var slice = Accept(":");
            Expect("]");
            return slice ? new SliceType(element) : new ArrayType(element);
        }

        if (Accept("(")) {
            var parts = ImmutableArray.CreateBuilder<SilType>();
            var owned = ImmutableArray.CreateBuilder<bool>();
            if (!At(")")) {
                do {
                    var isOwned = Current is { Kind: TokenKind.Global, Text: "owned" };
                    if (isOwned) {
                        Next();
                    }

                    owned.Add(isOwned);
                    parts.Add(ParseTypeBody());
                } while (Accept(","));
            }

            Expect(")");

            var isAsync = Accept("async");
            SilType? thrown = null;
            if (Accept("throws")) {
                Expect("(");
                thrown = ParseTypeBody();
                Expect(")");
            }

            if (Accept("->")) {
                return new FunctionType(parts.ToImmutable(), ParseTypeBody(), thrown, owned.ToImmutable(), isAsync);
            }

            if (thrown is not null || isAsync || owned.Contains(true)) {
                throw Error("'throws' and '@owned' belong to a function type");
            }

            // A parenthesised list is a tuple; the empty one is ().
            return SilType.Tuple(parts.ToImmutable());
        }

        var name = TypeName("a type");
        return name switch {
            "Int" => SilType.Int,
            "Bool" => SilType.Bool,
            "F64" => SilType.F64,
            "String" => SilType.String,
            "Char" => SilType.Char,
            "Object" => SilType.Object,
            "Region" => SilType.Region,
            "Metadata" => SilType.Metadata,
            "WitnessTable" => SilType.WitnessTable,
            _ when SilType.Numbers.FirstOrDefault(n => n.ToString() == name) is { } number => number,
            _ => _module.FindType(name) ?? throw Error($"unknown type '{name}'"),
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
                if (!Int128.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)) {
                    throw new SilParseException($"'{text}' is not an integer", line);
                }

                instruction = new IntegerLiteralInst(type, value);
                break;
            }

            case "float_literal": {
                var floatType = ParseType();
                Expect(",");
                var text = Expect(TokenKind.Number, "a number").Text;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) {
                    throw new SilParseException($"'{text}' is not a number", line);
                }

                instruction = new FloatLiteralInst(value, floatType);
                break;
            }

            case "string_literal":
                instruction = new StringLiteralInst(Expect(TokenKind.String, "a string").Text);
                break;

            case "metadata":
                instruction = new MetadataInst(ParseType());
                break;

            case "builtin": {
                var inBounds = Accept("[");
                if (inBounds) {
                    Expect("in_bounds");
                    Expect("]");
                }

                var name = Expect(TokenKind.String, "a builtin name").Text;
                var arguments = OperandList(0);
                Expect(":");
                instruction = new BuiltinInst(name, ParseType(), [..arguments]) { InBounds = inBounds };
                break;
            }

            case "alloc_stack": {
                var unmanaged = Accept("[");
                if (unmanaged) {
                    Expect("unmanaged");
                    Expect("]");
                }

                var type = ParseType();
                string? variable = null;
                if (Accept(",")) {
                    Expect("var");
                    variable = Expect(TokenKind.String, "a variable name").Text;
                }

                instruction = new AllocStackInst(type) { VariableName = variable, Unmanaged = unmanaged };
                break;
            }

            case "struct": {
                if (ParseType() is not StructType structType) {
                    throw new SilParseException("struct needs a struct type", line);
                }

                instruction = new StructInst(structType, [..OperandList(0)]);
                break;
            }

            case "struct_extract" or "struct_element_addr": {
                var value = Defined(Operand(0), opcode, line);
                Expect(",");
                var field = Expect(TokenKind.Member, "a field").Text;
                var structType = (opcode == "struct_extract" ? value.Type : (value.Type as AddressType)?.Pointee) as StructType
                    ?? throw new SilParseException($"{opcode} needs a struct {(opcode == "struct_extract" ? "value" : "address")}", line);
                var index = structType.FieldIndex(field);
                if (index < 0) {
                    throw new SilParseException($"{structType.Name} has no field #{field}", line);
                }

                instruction = opcode == "struct_extract"
                    ? new StructExtractInst(value, index)
                    : new StructElementAddrInst(value, index);
                break;
            }

            case "tuple": {
                // `$()` reads as Unit, the empty tuple's other spelling.
                if (ParseType() is not { } parsed || (parsed == SilType.Unit ? new TupleType([]) : parsed) is not TupleType tupleType) {
                    throw new SilParseException("tuple needs a tuple type", line);
                }

                instruction = new TupleInst(tupleType, [..OperandList(0)]);
                break;
            }

            case "tuple_extract" or "tuple_element_addr": {
                var value = Defined(Operand(0), opcode, line);
                Expect(",");
                var index = Count("an element index");
                instruction = opcode == "tuple_extract"
                    ? new TupleExtractInst(value, index)
                    : new TupleElementAddrInst(value, index);
                break;
            }

            case "enum": {
                if (ParseType() is not EnumType enumType) {
                    throw new SilParseException("enum needs an enum type", line);
                }

                Expect(",");
                var caseName = Expect(TokenKind.Member, "a case").Text;
                var index = enumType.CaseIndex(caseName);
                if (index < 0) {
                    throw new SilParseException($"{enumType.Name} has no case #{caseName}", line);
                }

                instruction = new EnumInst(enumType, index, Accept(",") ? Operand(0) : null);
                break;
            }

            case "switch_enum": {
                // The operand may be written further down: a block argument
                // of a block that dominates this one but comes later in the
                // text, as promotion leaves them. Its cases are then kept by
                // name until the function's end, when its type is known.
                var value = Operand(0);
                var named = new List<(string Case, SilBlock Target)>();
                SilBlock? fallback = null;
                while (Accept(",")) {
                    if (Accept("default")) {
                        fallback = BlockNamed(Expect(TokenKind.Word, "a block label").Text);
                        continue;
                    }

                    Expect("case");
                    var caseName = Expect(TokenKind.Member, "a case").Text;
                    Expect(":");
                    named.Add((caseName, BlockNamed(Expect(TokenKind.Word, "a block label").Text)));
                }

                var switched = new SwitchEnumInst(value, [], fallback);
                if (value is Placeholder) {
                    _unresolvedCases.Add((switched, named, line));
                } else {
                    ResolveCases(switched, named, line);
                }

                instruction = switched;
                break;
            }

            case "throw":
                instruction = new ThrowInst(Operand(0));
                break;

            case "cond_fail": {
                var condition = Operand(0);
                Expect(",");
                instruction = new CondFailInst(condition, Expect(TokenKind.String, "a message").Text);
                break;
            }

            case "try_apply": {
                var callee = Operand(0);
                var arguments = OperandList(1);
                Expect(":");
                var resultType = ParseType();
                Expect(",");
                Expect("normal");
                var normal = BlockNamed(Expect(TokenKind.Word, "a block label").Text);
                Expect(",");
                Expect("error");
                var error = BlockNamed(Expect(TokenKind.Word, "a block label").Text);
                instruction = new TryApplyInst(callee, resultType, [..arguments], normal, error);
                break;
            }

            case "dealloc_stack":
                instruction = new DeallocStackInst(Operand(0));
                break;

            case "load": {
                Expect("[");
                var kind = Expect(TokenKind.Word, "'trivial', 'copy' or 'take'").Text switch {
                    "trivial" => LoadKind.Trivial,
                    "copy" => LoadKind.Copy,
                    "take" => LoadKind.Take,
                    var other => throw new SilParseException($"'{other}' is not a load kind", line),
                };
                Expect("]");
                instruction = new LoadInst(Defined(Operand(0), opcode, line), kind);
                break;
            }

            case "partial_apply": {
                var function = Operand(0);
                var context = OperandList(1);
                Expect(":");
                if (ParseType() is not ThickFunctionType thick || context.Count > 1) {
                    throw new SilParseException("partial_apply takes at most a context, and gives a @thick function", line);
                }

                instruction = new PartialApplyInst(function, context.Count == 0 ? null : context[0], thick);
                break;
            }

            case "upcast": {
                var value = Operand(0);
                Expect(":");
                instruction = new UpcastInst(value, ParseType() as ClassType
                    ?? throw new SilParseException("upcast gives a class reference", line));
                break;
            }

            case "class_method": {
                var reference = Operand(0);
                Expect(",");
                var method = Expect(TokenKind.Member, "a method").Text;
                Expect(":");
                instruction = new ClassMethodInst(reference, method, ParseType() as FunctionType
                    ?? throw new SilParseException("class_method gives a function", line));
                break;
            }

            case "unchecked_ref_cast": {
                var value = Operand(0);
                Expect(":");
                instruction = new UncheckedRefCastInst(value, ParseType() as ClassType
                    ?? throw new SilParseException("unchecked_ref_cast gives a class reference", line));
                break;
            }

            case "load_weak":
                instruction = new LoadWeakInst(Defined(Operand(0), opcode, line));
                break;

            case "load_unowned":
                instruction = new LoadUnownedInst(Defined(Operand(0), opcode, line));
                break;

            case "store_weak" or "store_unowned": {
                var value = Operand(0);
                Expect("to");
                instruction = opcode == "store_weak" ? new StoreWeakInst(value, Operand(1)) : new StoreUnownedInst(value, Operand(1));
                break;
            }

            case "load_borrow":
                instruction = new LoadBorrowInst(Defined(Operand(0), opcode, line));
                break;

            case "end_borrow":
                instruction = new EndBorrowInst(Operand(0));
                break;

            case "copy_value":
                instruction = new CopyValueInst(Defined(Operand(0), opcode, line));
                break;

            case "weak_copy_value":
                instruction = new WeakCopyValueInst(Defined(Operand(0), opcode, line));
                break;

            case "unowned_copy_value":
                instruction = new UnownedCopyValueInst(Defined(Operand(0), opcode, line));
                break;

            case "strong_copy_weak_value":
                instruction = new StrongCopyWeakValueInst(Defined(Operand(0), opcode, line));
                break;

            case "strong_copy_unowned_value":
                instruction = new StrongCopyUnownedValueInst(Defined(Operand(0), opcode, line));
                break;

            case "destroy_value": {
                var uncounted = AcceptFlag("uncounted");
                instruction = new DestroyValueInst(Operand(0)) { Uncounted = uncounted };
                break;
            }

            case "destroy_addr": {
                var uncounted = AcceptFlag("uncounted");
                instruction = new DestroyAddrInst(Operand(0)) { Uncounted = uncounted };
                break;
            }

            case "alloc_ref": {
                var onStack = Accept("[");
                if (onStack) {
                    Expect("stack");
                    Expect("]");
                }

                instruction = new AllocRefInst(ParseType() as ClassType
                    ?? throw new SilParseException("alloc_ref needs a class type", line)) { OnStack = onStack };
                break;
            }

            case "ref_element_addr": {
                var reference = Defined(Operand(0), opcode, line);
                Expect(",");
                var field = Expect(TokenKind.Member, "a field").Text;
                var classType = reference.Type as ClassType
                    ?? throw new SilParseException("ref_element_addr needs a class reference", line);
                var index = classType.FieldIndex(field);
                if (index < 0) {
                    throw new SilParseException($"{classType.Name} has no field #{field}", line);
                }

                instruction = new RefElementAddrInst(reference, index);
                break;
            }

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

            case "global_addr": {
                var global = Expect(TokenKind.Global, "a global's name").Text;
                Expect(":");
                if (ParseType() is not AddressType address) {
                    throw new SilParseException("global_addr needs an address type", line);
                }

                instruction = new GlobalAddrInst(global, address.Pointee);
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
                // A name followed by '=' is the next instruction's result,
                // not the value returned: 'return' then '%3 = apply …'.
                instruction = new ReturnInst(
                    Current.Kind == TokenKind.Local && _tokens[_index + 1] is not { Kind: TokenKind.Punctuation, Text: "=" }
                        ? Operand(0)
                        : null
                );
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

        if (Accept("loc")) {
            var sourceLine = Count("a line");
            Expect(":");
            var column = Count("a column");
            instruction.Location = new SilLocation(sourceLine, column);
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

    /// <summary>The <c>switch_enum</c>s whose operand is written after them, with the cases they name.</summary>
    List<(SwitchEnumInst Switch, List<(string Case, SilBlock Target)> Named, int Line)> _unresolvedCases = [];

    /// <summary>A <c>switch_enum</c>'s named cases, as indices of its operand's enum.</summary>
    static void ResolveCases(SwitchEnumInst switched, List<(string Case, SilBlock Target)> named, int line) {
        var enumType = switched.Value.Type as EnumType
            ?? throw new SilParseException("switch_enum needs an enum value", line);
        foreach (var (caseName, target) in named) {
            var index = enumType.CaseIndex(caseName);
            if (index < 0) {
                throw new SilParseException($"{enumType.Name} has no case #{caseName}", line);
            }

            switched.Cases.Add((index, target));
        }
    }

    /// <summary>A value whose type an instruction needs while it is parsed, so it must already be defined.</summary>
    static SilValue Defined(SilValue value, string opcode, int line) =>
        value is Placeholder
            ? throw new SilParseException($"{opcode} needs its operand defined before it in the text", line)
            : value;

    /// <summary>Stands in for a value used before it is defined, until the end of the function.</summary>
    sealed class Placeholder : SilValue {
        public static readonly Placeholder Instance = new();
        public override SilType Type => SilType.Unit;
    }
}
