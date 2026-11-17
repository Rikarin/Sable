using System.Collections.Immutable;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     Lowers bound function bodies to SIL (docs/04 §2.2).
/// </summary>
/// <remarks>
///     <para>
///         This is the first subset: functions over <c>Int</c>, <c>Bool</c> and
///         <c>F64</c>; literals, arithmetic, comparison and logic; locals;
///         calls to functions in the module; <c>if</c>, <c>guard</c>,
///         <c>while</c>, <c>repeat</c>, labelled <c>break</c> and
///         <c>continue</c>, <c>return</c>, and <c>?:</c>. Anything else is
///         reported as not lowered yet, by construct, rather than lowered
///         wrong.
///     </para>
///     <para>
///         Every local gets a stack slot: <c>alloc_stack</c> in the entry
///         block, <c>store</c> to write it, <c>load</c> to read it, and
///         <c>dealloc_stack</c> for each, in reverse, before every
///         <c>return</c>. That is SIL's raw form, as Swift's SILGen produces
///         it; promoting slots to SSA values is a later pass, and keeping
///         them simple here keeps the lowering obviously right. Parameters are
///         immutable, so they are the entry block's arguments, used directly.
///     </para>
/// </remarks>
public sealed class SilGen {
    readonly Compilation _compilation;
    readonly DiagnosticBag _diagnostics = new();
    readonly SilModule _module = new();
    readonly Dictionary<FunctionSymbol, string> _names = [];

    SilGen(Compilation compilation) {
        _compilation = compilation;
    }

    public IReadOnlyList<Diagnostic> Diagnostics => [.._diagnostics];

    /// <summary>Lowers every function the compilation declares, or reports why it cannot.</summary>
    public static (SilModule Module, IReadOnlyList<Diagnostic> Diagnostics) Lower(Compilation compilation) {
        var generator = new SilGen(compilation);
        generator.LowerAll();
        return (generator._module, generator.Diagnostics);
    }

    void LowerAll() {
        var functions = new List<(FunctionSymbol Symbol, FunctionDeclarationSyntax Syntax, SemanticModel Model)>();

        foreach (var tree in _compilation.SyntaxTrees) {
            var model = _compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.Root.DescendantNodes().OfType<FunctionDeclarationSyntax>()) {
                if (_compilation.GetDeclaredSymbol(declaration) is not FunctionSymbol symbol) {
                    continue;
                }

                // Methods, local functions and generics come with the
                // lowerings for types, closures and specialisation.
                if (symbol.ContainingSymbol is not ModuleSymbol) {
                    continue;
                }

                functions.Add((symbol, declaration, model));
            }
        }

        foreach (var (symbol, _, _) in functions) {
            _names[symbol] = Mangle(symbol, functions.Count(f => f.Symbol.Name == symbol.Name) > 1);
        }

        // '@main' marks where a program starts; failing that, a function
        // called main does.
        var entry = functions.FirstOrDefault(static f =>
                f.Syntax.Attributes.Any(static a => a.Name.ToString().Trim() == "main")
            ).Symbol
            ?? functions.FirstOrDefault(static f => f.Symbol.Name == "main").Symbol;

        foreach (var (symbol, syntax, model) in functions) {
            if (SignatureOf(symbol, syntax) is not { } type) {
                continue;
            }

            if (ReferenceEquals(symbol, entry)) {
                _module.EntryPoint = _names[symbol];
            }

            if (model.GetBoundBody(syntax) is not { } body) {
                continue;
            }

            var function = new SilFunction(_names[symbol], type);
            new FunctionLowering(this, symbol, function).Lower(body);
            function.RemoveUnreachableBlocks();
            _module.Add(function);
        }
    }

    /// <summary>
    ///     <c>Module.name</c>, and when the name is overloaded the parameter
    ///     types too: <c>M.area$Int$Int</c>.
    /// </summary>
    static string Mangle(FunctionSymbol symbol, bool overloaded) {
        var name = $"{symbol.ContainingSymbol?.Name}.{symbol.Name}";
        return overloaded
            ? name + string.Concat(symbol.Parameters.Select(static p => "$" + p.Type.ToDisplayString()))
            : name;
    }

    FunctionType? SignatureOf(FunctionSymbol symbol, SyntaxNode at) {
        if (!symbol.TypeParameters.IsEmpty) {
            return Unsupported<FunctionType>(at, $"generic function '{symbol.Name}'");
        }

        if (symbol.IsAsync || symbol.ThrownType is not null || symbol.IsRethrows) {
            return Unsupported<FunctionType>(at, $"'{symbol.Name}', which is async or throws");
        }

        var parameters = ImmutableArray.CreateBuilder<SilType>();
        foreach (var parameter in symbol.Parameters) {
            if (parameter.Modifier != ParameterModifier.None) {
                return Unsupported<FunctionType>(at, $"the '{parameter.Modifier}' parameter '{parameter.Name}'");
            }

            if (Lower(parameter.Type) is not { } type) {
                return Unsupported<FunctionType>(at, $"a parameter of type '{parameter.Type.ToDisplayString()}'");
            }

            parameters.Add(type);
        }

        if (Lower(symbol.ReturnType) is not { } result) {
            return Unsupported<FunctionType>(at, $"a result of type '{symbol.ReturnType.ToDisplayString()}'");
        }

        return new FunctionType(parameters.ToImmutable(), result);
    }

    /// <summary>The SIL type for a language type, or null when this subset has none.</summary>
    SilType? Lower(TypeSymbol type) {
        var core = _compilation.Core;
        if (type.Equals(core.Int)) {
            return SilType.Int;
        }

        if (type.Equals(core.Bool)) {
            return SilType.Bool;
        }

        if (type.Equals(core.F64)) {
            return SilType.F64;
        }

        if (type.Equals(core.Void)) {
            return SilType.Unit;
        }

        if (type.Equals(core.String)) {
            return SilType.String;
        }

        return null;
    }

    T? Unsupported<T>(SyntaxNode at, string what) where T : class {
        _diagnostics.Error(ErrorCode.ERR_NotLoweredYet, at.Span, $"SIL lowering does not handle {what} yet");
        return null;
    }

    // =======================================================================

    sealed class FunctionLowering(SilGen generator, FunctionSymbol symbol, SilFunction function) {
        readonly Dictionary<ParameterSymbol, SilValue> _parameters = [];
        readonly Dictionary<LocalSymbol, AllocStackInst> _slots = [];
        readonly List<(string? Label, SilBlock Break, SilBlock Continue)> _loops = [];

        SilBlock? _current;
        bool _failed;

        CoreTypes Core => generator._compilation.Core;

        public void Lower(BoundBlock body) {
            var entry = function.AddBlock();
            for (var i = 0; i < symbol.Parameters.Length; i++) {
                _parameters[symbol.Parameters[i]] = entry.AddArgument(function.Type.Parameters[i]);
            }

            _current = entry;

            // Every local's slot, up front: one place to allocate, one
            // order to free them in.
            foreach (var local in LocalsIn(body)) {
                if (generator.Lower(local.Type) is not { } type) {
                    Fail(local.DeclaringSyntax.FirstOrDefault() ?? body.Syntax, $"a local of type '{local.Type.ToDisplayString()}'");
                    continue;
                }

                _slots[local] = Emit(new AllocStackInst(type));
            }

            Statement(body);

            // Falling off the end: fine for a function returning nothing.
            // For one with a result the binder has already said that every
            // path returns, so the end cannot be reached.
            if (_current is not null) {
                if (function.Type.Result == SilType.Unit) {
                    Return(null);
                } else {
                    Emit(new UnreachableInst());
                    _current = null;
                }
            }

            if (_failed) {
                // Leave a function that verifies, so the rest still can.
                function.Blocks.Clear();
                var stub = function.AddBlock();
                foreach (var parameter in function.Type.Parameters) {
                    stub.AddArgument(parameter);
                }

                stub.Append(new UnreachableInst());
            }
        }

        void Fail(SyntaxNode at, string what) {
            _failed = true;
            generator.Unsupported<object>(at, what);
        }

        T Emit<T>(T instruction) where T : SilInstruction {
            // After a jump nothing is reachable until a label; code there is
            // lowered into a block of its own, which is then dropped.
            _current ??= function.AddBlock();
            return _current.Append(instruction);
        }

        void Jump(SilInstruction terminator) {
            Emit(terminator);
            _current = null;
        }

        void Return(SilValue? value) {
            foreach (var slot in _slots.Values.Reverse()) {
                Emit(new DeallocStackInst(slot));
            }

            Jump(new ReturnInst(value));
        }

        // -------------------------------------------------------------------
        // Statements
        // -------------------------------------------------------------------

        void Statement(BoundStatement statement) {
            if (_failed) {
                return;
            }

            switch (statement) {
                case BoundBlock block:
                    foreach (var child in block.Statements) {
                        Statement(child);
                    }

                    return;

                case BoundLocalDeclaration declaration:
                    if (declaration.Initializer is { } initializer
                        && _slots.TryGetValue(declaration.Local, out var slot)
                        && Expression(initializer) is { } initial) {
                        // A declaration fills an empty slot; only a later
                        // assignment replaces a value. The same for trivial
                        // types, but not once a value has ownership.
                        Emit(new StoreInst(initial, slot, StoreKind.Init));
                    }

                    return;

                case BoundExpressionStatement expression:
                    Expression(expression.Expression);
                    return;

                case BoundReturn returnStatement:
                    if (returnStatement.Value is { } returned) {
                        if (Expression(returned) is { } value) {
                            Return(value);
                        }
                    } else {
                        Return(null);
                    }

                    return;

                case BoundIf ifStatement:
                    If(ifStatement);
                    return;

                case BoundWhile loop: {
                    var header = function.AddBlock();
                    var body = function.AddBlock();
                    var after = function.AddBlock();
                    Jump(new BranchInst(header));

                    _current = header;
                    Branch(loop.Condition, body, after);

                    _current = body;
                    InLoop(loop, after, header, () => Statement(loop.Body));
                    if (_current is not null) {
                        Jump(new BranchInst(header));
                    }

                    _current = after;
                    return;
                }

                case BoundRepeat loop: {
                    var body = function.AddBlock();
                    var test = function.AddBlock();
                    var after = function.AddBlock();
                    Jump(new BranchInst(body));

                    _current = body;
                    InLoop(loop, after, test, () => Statement(loop.Body));
                    if (_current is not null) {
                        Jump(new BranchInst(test));
                    }

                    _current = test;
                    Branch(loop.Condition, body, after);

                    _current = after;
                    return;
                }

                case BoundBreak jump when FindLoop(jump.Label) is { } breakLoop:
                    Jump(new BranchInst(breakLoop.Break));
                    return;

                case BoundContinue jump when FindLoop(jump.Label) is { } continueLoop:
                    Jump(new BranchInst(continueLoop.Continue));
                    return;

                default:
                    Fail(statement.Syntax, $"the statement '{Describe(statement.Syntax)}'");
                    return;
            }
        }

        void If(BoundIf ifStatement) {
            var after = function.AddBlock();

            // 'guard c else { body }': the body runs when c is false.
            if (ifStatement.Syntax is GuardStatementSyntax) {
                var elseBody = function.AddBlock();
                Branch(ifStatement.Condition, after, elseBody);
                _current = elseBody;
                Statement(ifStatement.Consequence);
                if (_current is not null) {
                    Jump(new BranchInst(after));
                }

                _current = after;
                return;
            }

            var consequence = function.AddBlock();
            var alternative = ifStatement.Alternative is null ? after : function.AddBlock();
            Branch(ifStatement.Condition, consequence, alternative);

            _current = consequence;
            Statement(ifStatement.Consequence);
            if (_current is not null) {
                Jump(new BranchInst(after));
            }

            if (ifStatement.Alternative is { } otherwise) {
                _current = alternative;
                Statement(otherwise);
                if (_current is not null) {
                    Jump(new BranchInst(after));
                }
            }

            _current = after;
        }

        void InLoop(BoundStatement loop, SilBlock breakTarget, SilBlock continueTarget, Action body) {
            var label = loop.Syntax.Parent is LabeledStatementSyntax { Label.Text: { Length: > 0 } text } ? text : null;
            _loops.Add((label, breakTarget, continueTarget));
            body();
            _loops.RemoveAt(_loops.Count - 1);
        }

        (string? Label, SilBlock Break, SilBlock Continue)? FindLoop(string? label) {
            for (var i = _loops.Count - 1; i >= 0; i--) {
                if (label is null || _loops[i].Label == label) {
                    return _loops[i];
                }
            }

            return null;
        }

        /// <summary>Evaluates a condition and branches on it, short-circuiting '&amp;&amp;', '||' and '!'.</summary>
        void Branch(BoundExpression condition, SilBlock whenTrue, SilBlock whenFalse) {
            switch (condition) {
                case BoundBinaryOperator { OperatorKind: BinaryOperatorKind.LogicalAnd } conjunction: {
                    var right = function.AddBlock();
                    Branch(conjunction.Left, right, whenFalse);
                    _current = right;
                    Branch(conjunction.Right, whenTrue, whenFalse);
                    return;
                }

                case BoundBinaryOperator { OperatorKind: BinaryOperatorKind.LogicalOr } disjunction: {
                    var right = function.AddBlock();
                    Branch(disjunction.Left, whenTrue, right);
                    _current = right;
                    Branch(disjunction.Right, whenTrue, whenFalse);
                    return;
                }

                case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Not } negation:
                    Branch(negation.Operand, whenFalse, whenTrue);
                    return;

                default:
                    if (Expression(condition) is { } value) {
                        Jump(new CondBranchInst(value, whenTrue, [], whenFalse, []));
                    }

                    return;
            }
        }

        // -------------------------------------------------------------------
        // Expressions
        // -------------------------------------------------------------------

        SilValue? Expression(BoundExpression expression) {
            if (_failed) {
                return null;
            }

            switch (expression) {
                case BoundLiteral literal:
                    return Literal(literal, literal.Type);

                case BoundConversion { ConversionKind: ConversionKind.Identity } conversion:
                    return Expression(conversion.Operand);

                // A literal taking the type the context asked for: 'let x: F64 = 1'.
                case BoundConversion { ConversionKind: ConversionKind.LiteralTyping, Operand: BoundLiteral literal } conversion:
                    return Literal(literal, conversion.Type);

                case BoundParameterAccess access when _parameters.TryGetValue(access.Parameter, out var parameter):
                    return parameter;

                case BoundLocalAccess access when _slots.TryGetValue(access.Local, out var slot):
                    return Emit(new LoadInst(slot));

                case BoundAssignment { Target: BoundLocalAccess target } assignment
                    when _slots.TryGetValue(target.Local, out var slot): {
                    if (Expression(assignment.Value) is not { } value) {
                        return null;
                    }

                    Emit(new StoreInst(value, slot, StoreKind.Assign));
                    return value;
                }

                case BoundBinaryOperator { OperatorKind: BinaryOperatorKind.LogicalAnd or BinaryOperatorKind.LogicalOr }:
                case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Not, Type: var notType } when notType.Equals(Core.Bool):
                    return BoolFromBranch(expression);

                case BoundBinaryOperator binary when binary.Method is null:
                    return Binary(binary);

                case BoundUnaryOperator unary:
                    return Unary(unary);

                case BoundConditional conditional: {
                    if (generator.Lower(conditional.Type) is not { } type) {
                        Fail(conditional.Syntax, $"a '?:' of type '{conditional.Type.ToDisplayString()}'");
                        return null;
                    }

                    var whenTrue = function.AddBlock();
                    var whenFalse = function.AddBlock();
                    var after = function.AddBlock();
                    var result = after.AddArgument(type);
                    Branch(conditional.Condition, whenTrue, whenFalse);

                    _current = whenTrue;
                    if (Expression(conditional.WhenTrue) is { } yes) {
                        Jump(new BranchInst(after, yes));
                    }

                    _current = whenFalse;
                    if (Expression(conditional.WhenFalse) is { } no) {
                        Jump(new BranchInst(after, no));
                    }

                    _current = after;
                    return result;
                }

                case BoundCall call:
                    return Call(call);

                default:
                    Fail(expression.Syntax, $"the expression '{Describe(expression.Syntax)}'");
                    return null;
            }
        }

        SilValue? Literal(BoundLiteral literal, TypeSymbol type) {
            if (type.Equals(Core.String) && literal.Value is string text) {
                return Emit(new StringLiteralInst(text));
            }

            if (type.Equals(Core.Bool) && literal.Value is bool flag) {
                return Emit(new IntegerLiteralInst(SilType.Bool, flag ? 1 : 0));
            }

            if (type.Equals(Core.Int) && literal.Value is long or int or ulong) {
                var value = Convert.ToDecimal(literal.Value);
                if (value > long.MaxValue) {
                    Fail(literal.Syntax, $"the integer '{literal.Value}', which does not fit in Int");
                    return null;
                }

                return Emit(new IntegerLiteralInst(SilType.Int, (long)value));
            }

            if (type.Equals(Core.F64) && literal.Value is double or long or int or ulong) {
                return Emit(new FloatLiteralInst(Convert.ToDouble(literal.Value)));
            }

            Fail(literal.Syntax, $"the literal '{Describe(literal.Syntax)}' of type '{type.ToDisplayString()}'");
            return null;
        }

        /// <summary>A Bool computed by branching: '&amp;&amp;', '||' and '!' as values.</summary>
        SilValue BoolFromBranch(BoundExpression condition) {
            var whenTrue = function.AddBlock();
            var whenFalse = function.AddBlock();
            var after = function.AddBlock();
            var result = after.AddArgument(SilType.Bool);
            Branch(condition, whenTrue, whenFalse);

            _current = whenTrue;
            Jump(new BranchInst(after, Emit(new IntegerLiteralInst(SilType.Bool, 1))));
            _current = whenFalse;
            Jump(new BranchInst(after, Emit(new IntegerLiteralInst(SilType.Bool, 0))));

            _current = after;
            return result;
        }

        SilValue? Binary(BoundBinaryOperator binary) {
            if (Expression(binary.Left) is not { } left || Expression(binary.Right) is not { } right) {
                return null;
            }

            var op = binary.OperatorKind switch {
                BinaryOperatorKind.Add => "add",
                BinaryOperatorKind.Subtract => "sub",
                BinaryOperatorKind.Multiply => "mul",
                BinaryOperatorKind.Divide => "div",
                BinaryOperatorKind.Remainder => "rem",
                BinaryOperatorKind.And => "and",
                BinaryOperatorKind.Or => "or",
                BinaryOperatorKind.ExclusiveOr => "xor",
                BinaryOperatorKind.LeftShift => "shl",
                BinaryOperatorKind.RightShift => "shr",
                BinaryOperatorKind.Equal => "cmp_eq",
                BinaryOperatorKind.NotEqual => "cmp_ne",
                BinaryOperatorKind.LessThan => "cmp_lt",
                BinaryOperatorKind.LessThanOrEqual => "cmp_le",
                BinaryOperatorKind.GreaterThan => "cmp_gt",
                BinaryOperatorKind.GreaterThanOrEqual => "cmp_ge",
                _ => null,
            };

            var name = op is null ? null : $"{op}_{left.Type}";
            if (name is null || SilBuiltins.Find(name) is not { } signature || left.Type != right.Type) {
                Fail(binary.Syntax, $"the operator in '{Describe(binary.Syntax)}' on '{binary.Left.Type.ToDisplayString()}'");
                return null;
            }

            return Emit(new BuiltinInst(name, signature.Result, left, right));
        }

        SilValue? Unary(BoundUnaryOperator unary) {
            if (unary.OperatorKind == UnaryOperatorKind.Plus) {
                return Expression(unary.Operand);
            }

            if (Expression(unary.Operand) is not { } operand) {
                return null;
            }

            var op = unary.OperatorKind switch {
                UnaryOperatorKind.Negate => "neg",
                UnaryOperatorKind.Complement => "not",
                _ => null,
            };

            var name = op is null ? null : $"{op}_{operand.Type}";
            if (name is null || SilBuiltins.Find(name) is not { } signature) {
                Fail(unary.Syntax, $"the operator in '{Describe(unary.Syntax)}'");
                return null;
            }

            return Emit(new BuiltinInst(name, signature.Result, operand));
        }

        SilValue? Call(BoundCall call) {
            // The core's print is a builtin: the runtime owns the writing.
            if (Core.Print.Contains(call.Function)) {
                if (Expression(call.Arguments[0]) is not { } printed) {
                    return null;
                }

                return Emit(new BuiltinInst($"print_{printed.Type}", SilType.Unit, printed));
            }

            if (call.Receiver is not null || !generator._names.TryGetValue(call.Function, out var name)) {
                Fail(call.Syntax, $"the call '{Describe(call.Syntax)}'");
                return null;
            }

            var callee = generator._module.Find(name)?.Type ?? generator.SignatureOf(call.Function, call.Syntax);
            if (callee is null) {
                _failed = true;
                return null;
            }

            var arguments = new List<SilValue>();
            foreach (var argument in call.Arguments) {
                if (Expression(argument) is not { } value) {
                    return null;
                }

                arguments.Add(value);
            }

            var reference = Emit(new FunctionRefInst(name, callee));
            return Emit(new ApplyInst(reference, callee.Result, [..arguments]));
        }

        static string Describe(SyntaxNode syntax) {
            var text = syntax.ToString().Trim();
            var line = text.Split('\n')[0];
            return line.Length > 40 ? line[..40] + "…" : line;
        }

        /// <summary>Every local the body declares, closures aside, in order.</summary>
        static IEnumerable<LocalSymbol> LocalsIn(BoundNode node) {
            if (node is BoundLocalDeclaration declaration) {
                yield return declaration.Local;
            }

            if (node is BoundClosure or BoundLocalFunction) {
                yield break;
            }

            foreach (var child in node.Children) {
                foreach (var local in LocalsIn(child)) {
                    yield return local;
                }
            }
        }
    }
}
