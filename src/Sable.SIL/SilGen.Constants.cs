using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     Compile-time evaluation (SYNTAX.md §21): each <c>const</c> computed,
///     and each <c>comptime</c> block run, while the program is compiled, in
///     the SIL interpreter.
/// </summary>
/// <remarks>
///     <para>
///         A generator of its own does it, before the program is lowered. It
///         lowers the code each constant's value needs and nothing else — its
///         initialiser as a function that stores it, and, as they are called,
///         the functions that calls — and runs it. A constant read by another
///         is computed first, whichever file it is in; one that needs itself
///         is an error. What the binder checked (CompileTimeRules) keeps this
///         code from reading, writing or waiting, so running it here is the
///         same as running it in the program, but once.
///     </para>
///     <para>
///         The program's own lowering then writes each constant where it is
///         read, as the literal it came to — a number, a string, a tuple or
///         struct of them, an enum case — so it has no storage and costs
///         nothing to read. A value no literal can say (an array, an object)
///         is kept in a global set when the program starts, from the same
///         code, which cannot do anything but compute it again.
///     </para>
///     <para>
///         What goes wrong is reported where it went wrong: a trap
///         (overflow, division by zero, too many steps) at the constant or
///         block (S3201, S3221), <c>compileError("...")</c> at the call (S3222).
///     </para>
/// </remarks>
public sealed partial class SilGen {
    /// <summary>Enough for a table any program would compute; a loop that never ends stops here.</summary>
    const int ConstantStepLimit = 20_000_000;

    /// <summary>What compile-time evaluation found: each constant's value, and what went wrong.</summary>
    sealed class ConstantTable {
        /// <summary>
        ///     Each constant computed, as the interpreter gives it: a number,
        ///     <see cref="bool" />, <see cref="Rune" />, a string, an array of
        ///     parts for a struct or tuple, an enum value — or
        ///     <see cref="SilInterpreter.Opaque" /> for one no literal can say.
        /// </summary>
        public Dictionary<FieldSymbol, object?> Values { get; } = [];

        public List<(Diagnostic Diagnostic, SyntaxTree Tree)> Diagnostics { get; } = [];
    }

    static readonly ConditionalWeakTable<Compilation, ConstantTable> s_constants = new();

    /// <summary>
    ///     Computes every constant and runs every <c>comptime</c> block: what
    ///     that reports, each with the file it is in. Done once per
    ///     compilation; lowering it uses what is found.
    /// </summary>
    public static IReadOnlyList<(Diagnostic Diagnostic, SyntaxTree Tree)> EvaluateConstants(Compilation compilation) =>
        ConstantsOf(compilation).Diagnostics;

    static ConstantTable ConstantsOf(Compilation compilation) =>
        s_constants.GetValue(compilation, static c => new SilGen(c) { _evaluation = new() }.Evaluate());

    /// <summary>In the program's lowering: what compile-time evaluation found.</summary>
    ConstantTable? _constants;

    /// <summary>In compile-time evaluation's own generator: its state.</summary>
    Evaluation? _evaluation;

    sealed class Evaluation {
        public ConstantTable Table { get; } = new();
        public HashSet<FieldSymbol> InProgress { get; } = [];
        public HashSet<FieldSymbol> Failed { get; } = [];
        public HashSet<string> Demanded { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, FunctionSymbol> ByName { get; } = new(StringComparer.Ordinal);
        public List<(SyntaxNode Site, SyntaxTree Tree)> CompileErrors { get; } = [];
        public SilInterpreter? Interpreter { get; set; }

        /// <summary>
        ///     Set when code being lowered reads a constant that could not be
        ///     computed: that has been reported, and what reads it is not
        ///     run, to say only that it found nothing there.
        /// </summary>
        public bool ReadAFailure { get; set; }
    }

    const string CompileErrorPrefix = "compile error";

    ConstantTable Evaluate() {
        var evaluation = _evaluation!;
        var constants = new List<(FieldSymbol Field, ConstDeclarationSyntax Syntax, SemanticModel Model)>();
        var blocks = new List<(SyntaxNode Syntax, FunctionSymbol Function, BoundBlock Body, SemanticModel Model)>();
        foreach (var tree in _compilation.SyntaxTrees) {
            var model = _compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.Root.DescendantNodes().OfType<ConstDeclarationSyntax>()) {
                // A literal is its own value: lowering writes it where it is read.
                if (_compilation.GetDeclaredSymbol(declaration) is FieldSymbol field && model.GetFieldInitializer(field) is not BoundLiteral) {
                    constants.Add((field, declaration, model));
                }
            }

            blocks.AddRange(model.GetComptimeBlocks().Select(b => (b.Syntax, b.Function, b.Body, model)));
        }

        if (constants.Count == 0 && blocks.Count == 0) {
            return evaluation.Table;
        }

        LowerAll();
        foreach (var (symbol, name) in _names) {
            evaluation.ByName.TryAdd(name, symbol);
        }

        evaluation.Interpreter = new SilInterpreter(_module, ConstantStepLimit);

        foreach (var (field, _, _) in constants) {
            EnsureEvaluated(field);
        }

        var count = 0;
        foreach (var (syntax, function, body, model) in blocks) {
            var name = $"$comptime.{count++}";
            if (Lower(name, function, body, model) && evaluation.Interpreter is { } interpreter) {
                Run(interpreter, name, syntax, model.SyntaxTree, "the 'comptime' block");
            }
        }

        // What lowering found wrong in code run here — a literal too large
        // for its type, a construct not lowered yet — is the program's
        // lowering's to report as well; here it is for 'sable check'.
        foreach (var diagnostic in _diagnostics) {
            evaluation.Table.Diagnostics.Add((diagnostic, TreeAt(diagnostic)));
        }

        return evaluation.Table;
    }

    /// <summary>The file a lowering diagnostic is in: the one whose code it was reported lowering.</summary>
    SyntaxTree TreeAt(Diagnostic diagnostic) => _lastLowered ?? _compilation.SyntaxTrees[0];

    SyntaxTree? _lastLowered;

    /// <summary>Lowers a function of no parameters that runs <paramref name="body" />; false when it cannot be.</summary>
    bool Lower(string name, FunctionSymbol symbol, BoundBlock body, SemanticModel model) {
        var evaluation = _evaluation!;
        var outer = evaluation.ReadAFailure;
        evaluation.ReadAFailure = false;
        try {
            return LowerNow(name, symbol, body, model) && !evaluation.ReadAFailure;
        } finally {
            evaluation.ReadAFailure = outer;
        }
    }

    bool LowerNow(string name, FunctionSymbol symbol, BoundBlock body, SemanticModel model) {
        var before = _diagnostics.Count;
        _lastLowered = model.SyntaxTree;
        var function = new SilFunction(name, new FunctionType([], SilType.Unit)) { SourceFile = model.SyntaxTree.Text.FileName };
        new FunctionLowering(this, symbol, function, model.SyntaxTree.Text, TypeSubstitution.Empty).Lower(body);
        function.RemoveUnreachableBlocks();
        _module.Add(function);
        LowerDemanded();
        return _diagnostics.Count == before;
    }

    /// <summary>Computes the constant, unless it has been: what reading it, here, needs first.</summary>
    void EnsureEvaluated(FieldSymbol field) {
        var evaluation = _evaluation!;
        if (evaluation.Table.Values.ContainsKey(field) || evaluation.Failed.Contains(field)) {
            return;
        }

        if (field.DeclaringSyntax is not [ConstDeclarationSyntax declaration] || ModelOf(declaration) is not { } model) {
            evaluation.Failed.Add(field);
            return;
        }

        if (!evaluation.InProgress.Add(field)) {
            evaluation.Failed.Add(field);
            evaluation.Table.Diagnostics.Add((Diagnostic.Error(
                ErrorCode.ERR_ConstantEvaluationFailed,
                declaration.Identifier.Span,
                $"the constant '{field.Name}' is defined in terms of itself"
            ), model.SyntaxTree));
            return;
        }

        try {
            if (model.GetFieldInitializer(field) is not { } initial || model.GetConstantFunction(field) is not { } symbol
                || GlobalOf(field) is not var (global, _)) {
                evaluation.Failed.Add(field);
                return;
            }

            // The value, stored where the constant lives in the interpreter;
            // reading it there is what any later constant does.
            var target = new BoundFieldAccess(initial.Syntax, null, field);
            var store = new BoundExpressionStatement(initial.Syntax, new BoundAssignment(initial.Syntax, target, initial));
            var name = $"$const.{global}";
            if (!Lower(name, symbol, new BoundBlock(declaration, [], [store]), model) || evaluation.Interpreter is not { } interpreter
                || !Run(interpreter, name, declaration.Initializer.Value, model.SyntaxTree, $"the constant '{field.Name}'")) {
                evaluation.Failed.Add(field);
                return;
            }

            evaluation.Table.Values[field] = interpreter.GlobalValue(global);
        } finally {
            evaluation.InProgress.Remove(field);
        }
    }

    SemanticModel? ModelOf(SyntaxNode syntax) {
        var root = syntax;
        while (root.Parent is { } parent) {
            root = parent;
        }

        var tree = _compilation.SyntaxTrees.FirstOrDefault(t => ReferenceEquals(t.Root, root));
        return tree is null ? null : _compilation.GetSemanticModel(tree);
    }

    /// <summary>Runs the function in the interpreter; false, having said why, when it traps.</summary>
    bool Run(SilInterpreter interpreter, string function, SyntaxNode at, SyntaxTree tree, string what) {
        try {
            interpreter.Run(function);
            return true;
        } catch (SilTrap trap) {
            var table = _evaluation!.Table;
            var compileError = trap.Message.StartsWith(CompileErrorPrefix + " #", StringComparison.Ordinal)
                ? trap.Message[(CompileErrorPrefix.Length + 2)..].Split(':', 2)
                : null;
            if (compileError is [var number, var message] && int.TryParse(number, CultureInfo.InvariantCulture, out var index)
                && index < _evaluation.CompileErrors.Count) {
                var (site, siteTree) = _evaluation.CompileErrors[index];
                table.Diagnostics.Add((Diagnostic.Error(ErrorCode.ERR_CompileError, site.Span, message.Trim()), siteTree));
                return false;
            }

            // Arithmetic that overflows or divides by zero is what the
            // binder's folding of plain arithmetic reports too (S3201): one
            // code, whether the arithmetic was written here or in a const fn.
            var arithmetic = trap.Message is "integer overflow" or "division by zero";
            var reason = trap.Message.Contains("step limit", StringComparison.Ordinal)
                ? $"computing it took more than {ConstantStepLimit:N0} steps"
                : trap.Message;
            table.Diagnostics.Add((Diagnostic.Error(arithmetic ? ErrorCode.ERR_ConstantOverflow : ErrorCode.ERR_ConstantEvaluationFailed, at.Span,
                $"{what} cannot be computed while compiling: {reason}"), tree));
            return false;
        }
    }

    /// <summary>
    ///     Lowers what the code lowered so far refers to and is not lowered
    ///     yet — functions it calls, instances of generic ones, the methods a
    ///     class it makes dispatches to — until nothing is missing.
    /// </summary>
    void LowerDemanded() {
        var evaluation = _evaluation!;
        bool progress;
        do {
            progress = false;
            while (_pending.Count > 0) {
                var (symbol, name, type, substitution) = _pending.Dequeue();
                var (syntax, model) = _declarations[symbol];
                LowerBody(symbol, syntax, model, name, type, substitution);
                progress = true;
            }

            var wanted = new List<string>();
            foreach (var function in _module.Functions.ToList()) {
                foreach (var block in function.Blocks) {
                    foreach (var instruction in block.Instructions) {
                        if (instruction is FunctionRefInst reference) {
                            wanted.Add(reference.Function);
                        }
                    }
                }
            }

            // A class's methods: a call may reach any of them through its vtable.
            foreach (var symbol in _classes.Values) {
                foreach (var method in symbol.OriginalDefinition.Members.OfType<FunctionSymbol>()) {
                    if (_names.TryGetValue(method, out var name)) {
                        wanted.Add(name);
                    }
                }
            }

            foreach (var name in wanted) {
                if (_module.Find(name) is not null || !evaluation.Demanded.Add(name)
                    || !evaluation.ByName.TryGetValue(name, out var symbol) || IsGeneric(symbol) || IsIntrinsic(symbol)
                    || !_declarations.TryGetValue(symbol, out var declaration)) {
                    continue;
                }

                if (SignatureOf(symbol, declaration.Syntax, TypeSubstitution.Empty) is { } type) {
                    LowerBody(symbol, declaration.Syntax, declaration.Model, name, type, TypeSubstitution.Empty);
                    progress = true;
                }
            }
        } while (progress);

        foreach (var type in _classes.Keys) {
            type.VTable.Clear();
        }

        BuildVTables();
    }

    // ---- The program's side ---------------------------------------------

    /// <summary>
    ///     What a read of the constant is lowered to, in the program: its
    ///     value, as computed; or its initialiser, when that is a literal;
    ///     or null, when it is read from its global. In compile-time
    ///     evaluation, it is computed first, and read from its global.
    /// </summary>
    internal object? ConstantRead(FieldSymbol field) {
        if (field.DeclaringSyntax is not [ConstDeclarationSyntax]) {
            return null;
        }

        if (_evaluation is { } evaluation) {
            EnsureEvaluated(field);
            if (evaluation.Failed.Contains(field)) {
                evaluation.ReadAFailure = true;
            }

            return null;
        }

        if (_constants is null || Lower(field.Type) is not { } type) {
            return null;
        }

        if (_constants.Values.TryGetValue(field, out var value)) {
            return IsLiteral(value, type) ? value ?? s_none : null;
        }

        return FieldInitializer(field) as BoundLiteral;
    }

    /// <summary>Stands for a computed value that is null: an enum case without a payload has one.</summary>
    static readonly object s_none = new();

    /// <summary>Whether lowering can write the value as a literal of the type.</summary>
    static bool IsLiteral(object? value, SilType type) =>
        (value, type) switch {
            (string, _) => type == SilType.String,
            (bool, _) => type == SilType.Bool,
            (Rune, _) => type == SilType.Char,
            (long or ulong, _) => SilType.IntegerInfo(type) is not null || type == SilType.Int,
            (double or float, _) => SilType.IsFloat(type),
            (object?[] parts, StructType structType) => parts.Length == structType.Fields.Count
                && parts.Select((p, i) => IsLiteral(p, structType.Fields[i].Type)).All(static ok => ok),
            (object?[] parts, TupleType tuple) => parts.Length == tuple.Elements.Length
                && parts.Select((p, i) => IsLiteral(p, tuple.Elements[i])).All(static ok => ok),
            (SilInterpreter.EnumValue e, EnumType enumType) => e.Case < enumType.Cases.Count
                && (enumType.Cases[e.Case].Payload is not { } payload ? e.Payload is null : IsLiteral(e.Payload, payload)),
            _ => false,
        };

    sealed partial class FunctionLowering {
        /// <summary>A constant's value, written where it is read.</summary>
        SilValue? Constant(object constant, SilType type, SyntaxNode at) =>
            constant switch {
                BoundLiteral literal => Expression(literal),
                _ => ConstantValue(ReferenceEquals(constant, s_none) ? null : constant, type),
            };

        SilValue ConstantValue(object? value, SilType type) {
            switch (value, type) {
                case (string text, _):
                    return Emit(new StringLiteralInst(text));
                case (bool flag, _):
                    return Emit(new IntegerLiteralInst(SilType.Bool, flag ? 1 : 0));
                case (Rune scalar, _):
                    return Emit(new IntegerLiteralInst(SilType.Char, scalar.Value));
                case (long number, _):
                    return Emit(new IntegerLiteralInst(type, number));
                case (ulong number, _):
                    return Emit(new IntegerLiteralInst(type, unchecked((long)number)));
                case (double or float, _):
                    return Emit(new FloatLiteralInst(System.Convert.ToDouble(value, CultureInfo.InvariantCulture), type));
                case (object?[] parts, StructType structType):
                    return Emit(new StructInst(structType, [..parts.Select((p, i) => ConstantValue(p, structType.Fields[i].Type))]));
                case (object?[] parts, TupleType tuple):
                    return Emit(new TupleInst(tuple, [..parts.Select((p, i) => ConstantValue(p, tuple.Elements[i]))]));
                case (SilInterpreter.EnumValue e, EnumType enumType):
                    return Emit(new EnumInst(enumType, e.Case,
                        enumType.Cases[e.Case].Payload is { } payload ? ConstantValue(e.Payload, payload) : null));
                default:
                    throw new InvalidOperationException($"a constant of type {type} is not a literal");
            }
        }

        /// <summary>
        ///     <c>compileError("...")</c>: a trap with the message. Run while
        ///     compiling, it is an error at this call; run in the program, in
        ///     a const fn called there, it stops it.
        /// </summary>
        SilValue? CompileError(BoundCall call) {
            var message = call.Arguments is [var argument] && Unconverted(argument) is BoundLiteral { Value: string text } ? text : "";
            string reason;
            if (generator._evaluation is { } evaluation) {
                reason = $"{CompileErrorPrefix} #{evaluation.CompileErrors.Count}: {message}";
                evaluation.CompileErrors.Add((call.Syntax, generator.ModelOf(call.Syntax)?.SyntaxTree ?? generator._compilation.SyntaxTrees[0]));
            } else {
                reason = $"{CompileErrorPrefix}: {message}";
            }

            Emit(new CondFailInst(Emit(new IntegerLiteralInst(SilType.Bool, 1)), reason));
            Jump(new UnreachableInst());
            return UnitValue.Instance;
        }
    }
}
