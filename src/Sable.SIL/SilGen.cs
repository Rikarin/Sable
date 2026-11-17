using System.Collections.Immutable;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     Lowers bound function bodies to SIL (docs/04 §2.2).
/// </summary>
/// <remarks>
///     <para>
///         What lowers so far: <c>Int</c>, <c>Bool</c>, <c>F64</c>, string
///         literals; structs, with their methods, <c>mutating</c> methods and
///         initialisers; tuples; unions, enums and optionals, with
///         <c>match</c>, <c>if let</c>, <c>if case</c>, <c>??</c> and
///         <c>!</c>; <c>throws</c>, <c>try</c>, <c>try?</c>, <c>try!</c>,
///         <c>try … catch</c>, <c>do</c>/<c>catch</c> and <c>throw</c>;
///         <c>defer</c>; <c>if</c>, <c>guard</c>, <c>while</c>,
///         <c>repeat</c>, <c>for</c> over a range, labelled jumps. Anything
///         else is reported as not lowered yet, by construct, rather than
///         lowered wrong.
///     </para>
///     <para>
///         Every local gets a stack slot: <c>alloc_stack</c> in the entry
///         block, <c>store</c> to write it, <c>load</c> to read it, and
///         <c>dealloc_stack</c> for each, in reverse, wherever the function
///         leaves. That is SIL's raw form, as Swift's SILGen produces it;
///         promotion to SSA values is a pass of its own.
///     </para>
///     <para>
///         <c>defer</c> is lowered by emitting each deferred body on every
///         path that leaves its scope — falling off the end, <c>return</c>,
///         <c>throw</c>, <c>break</c>, <c>continue</c>, an error on its way to
///         a <c>catch</c> — innermost first. Errors go to a handler: the
///         nearest <c>do</c>'s catches, a <c>try?</c> or <c>try!</c> or
///         <c>try … catch</c>, or the function's own <c>throw</c>.
///     </para>
/// </remarks>
public sealed class SilGen {
    readonly Compilation _compilation;
    readonly DiagnosticBag _diagnostics = new();
    readonly SilModule _module = new();
    readonly Dictionary<FunctionSymbol, string> _names = [];
    readonly Dictionary<TypeSymbol, SilType?> _types = [];

    SilGen(Compilation compilation) {
        _compilation = compilation;
    }

    CoreTypes Core => _compilation.Core;

    public IReadOnlyList<Diagnostic> Diagnostics => [.._diagnostics];

    /// <summary>Lowers every function the compilation declares, or reports why it cannot.</summary>
    public static (SilModule Module, IReadOnlyList<Diagnostic> Diagnostics) Lower(Compilation compilation) {
        var generator = new SilGen(compilation);
        generator.LowerAll();
        return (generator._module, generator.Diagnostics);
    }

    void LowerAll() {
        var functions = new List<(FunctionSymbol Symbol, SyntaxNode Syntax, SemanticModel Model)>();

        foreach (var tree in _compilation.SyntaxTrees) {
            var model = _compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.Root.DescendantNodes()) {
                if (declaration is not (FunctionDeclarationSyntax or InitDeclarationSyntax)) {
                    continue;
                }

                if (_compilation.GetDeclaredSymbol(declaration) is not FunctionSymbol symbol || !IsLowerable(symbol)) {
                    continue;
                }

                functions.Add((symbol, declaration, model));
            }
        }

        foreach (var (symbol, _, _) in functions) {
            var overloaded = functions.Count(f => f.Symbol.Name == symbol.Name
                && ReferenceEquals(f.Symbol.ContainingSymbol, symbol.ContainingSymbol)) > 1;
            _names[symbol] = Mangle(symbol, overloaded);
        }

        // '@main' marks where a program starts; failing that, a function
        // called main does.
        var entry = functions.FirstOrDefault(static f =>
                f.Syntax is FunctionDeclarationSyntax declaration
                && declaration.Attributes.Any(static a => a.Name.ToString().Trim() == "main")
            ).Symbol
            ?? functions.FirstOrDefault(static f => f.Symbol is { Name: "main", ContainingSymbol: ModuleSymbol }).Symbol;

        foreach (var (symbol, syntax, model) in functions) {
            if (SignatureOf(symbol, syntax) is not { } type) {
                continue;
            }

            if (ReferenceEquals(symbol, entry)) {
                _module.EntryPoint = _names[symbol];
            }

            if (model.GetBoundNode(syntax) is not BoundBlock body) {
                continue;
            }

            var source = model.SyntaxTree.Text;
            var function = new SilFunction(_names[symbol], type) {
                SourceFile = source.FileName,
                Line = source.GetLinePosition(syntax.Span.Start).Line,
            };

            if (HasSelf(symbol)) {
                function.ParameterNames.Add("self");
            }

            function.ParameterNames.AddRange(symbol.Parameters.Select(static p => p.Name));
            new FunctionLowering(this, symbol, function, source).Lower(body);
            function.RemoveUnreachableBlocks();
            _module.Add(function);
        }
    }

    /// <summary>
    ///     A function of the module, or a member of a struct, union or enum.
    ///     Local functions, class members and generics wait for closures,
    ///     reference counting and specialisation.
    /// </summary>
    static bool IsLowerable(FunctionSymbol symbol) =>
        symbol.ContainingSymbol switch {
            ModuleSymbol => true,
            NamedTypeSymbol { TypeKind: TypeKind.Struct or TypeKind.Union or TypeKind.Enum, TypeParameters.IsEmpty: true } => true,
            _ => false,
        };

    /// <summary>
    ///     <c>Module.name</c>, <c>Module.Type.name</c> for a member, and the
    ///     parameter types after it when the name is overloaded:
    ///     <c>M.area$Int$Int</c>.
    /// </summary>
    static string Mangle(FunctionSymbol symbol, bool overloaded) {
        var owner = symbol.ContainingSymbol switch {
            NamedTypeSymbol type => $"{type.ContainingSymbol?.Name}.{type.Name}",
            var other => other?.Name,
        };

        var name = $"{owner}.{symbol.Name}";
        return overloaded
            ? name + string.Concat(symbol.Parameters.Select(static p => "$" + Sanitise(p.Type.ToDisplayString())))
            : name;
    }

    static string Sanitise(string text) =>
        new([..text.Select(static c => char.IsLetterOrDigit(c) || c is '_' or '.' ? c : '$')]);

    /// <summary>True for a method that takes <c>self</c>, as opposed to a static one or an initialiser.</summary>
    static bool HasSelf(FunctionSymbol symbol) =>
        symbol.ContainingSymbol is NamedTypeSymbol && !symbol.IsStatic && symbol.FunctionKind != FunctionKind.Initializer;

    FunctionType? SignatureOf(FunctionSymbol symbol, SyntaxNode at) {
        if (!symbol.TypeParameters.IsEmpty) {
            return Unsupported<FunctionType>(at, $"generic function '{symbol.Name}'");
        }

        if (symbol.IsAsync || symbol.IsRethrows) {
            return Unsupported<FunctionType>(at, $"'{symbol.Name}', which is async or rethrows");
        }

        var parameters = ImmutableArray.CreateBuilder<SilType>();

        // 'self' comes first: by value, or by address when the method may change it.
        if (HasSelf(symbol)) {
            if (Lower(SelfType(symbol)) is not { } self) {
                return Unsupported<FunctionType>(at, $"a member of '{SelfType(symbol).ToDisplayString()}'");
            }

            parameters.Add(symbol.IsMutating ? SilType.Address(self) : self);
        }

        foreach (var parameter in symbol.Parameters) {
            if (parameter.Modifier != ParameterModifier.None) {
                return Unsupported<FunctionType>(at, $"the '{parameter.Modifier}' parameter '{parameter.Name}'");
            }

            if (Lower(parameter.Type) is not { } type) {
                return Unsupported<FunctionType>(at, $"a parameter of type '{parameter.Type.ToDisplayString()}'");
            }

            parameters.Add(type);
        }

        var resultSymbol = symbol.FunctionKind == FunctionKind.Initializer ? SelfType(symbol) : symbol.ReturnType;
        if (Lower(resultSymbol) is not { } result) {
            return Unsupported<FunctionType>(at, $"a result of type '{resultSymbol.ToDisplayString()}'");
        }

        SilType? thrown = null;
        if (symbol.ThrownType is { } thrownSymbol) {
            if (thrownSymbol.Equals(Core.AnyError) || Lower(thrownSymbol) is not { } thrownType) {
                return Unsupported<FunctionType>(at, $"'{symbol.Name}', which throws '{thrownSymbol.ToDisplayString()}'");
            }

            thrown = thrownType;
        }

        return new FunctionType(parameters.ToImmutable(), result, thrown);
    }

    static TypeSymbol SelfType(FunctionSymbol symbol) => (NamedTypeSymbol)symbol.ContainingSymbol!;

    /// <summary>The SIL type for a language type, or null when there is none yet.</summary>
    internal SilType? Lower(TypeSymbol type) {
        if (_types.TryGetValue(type, out var cached)) {
            return cached;
        }

        // A type that holds itself would need indirection; until there is
        // some, meeting it again on the way down means no SIL type.
        _types[type] = null;
        var lowered = LowerUncached(type);
        _types[type] = lowered;
        return lowered;
    }

    SilType? LowerUncached(TypeSymbol type) {
        if (type.Equals(Core.Int)) {
            return SilType.Int;
        }

        if (type.Equals(Core.Bool)) {
            return SilType.Bool;
        }

        if (type.Equals(Core.F64)) {
            return SilType.F64;
        }

        if (type.Equals(Core.Void)) {
            return SilType.Unit;
        }

        if (type.Equals(Core.String)) {
            return SilType.String;
        }

        switch (type) {
            case TupleTypeSymbol tuple: {
                var elements = ImmutableArray.CreateBuilder<SilType>();
                foreach (var element in tuple.ElementTypes) {
                    if (Lower(element) is not { } lowered) {
                        return null;
                    }

                    elements.Add(lowered);
                }

                return SilType.Tuple(elements.ToImmutable());
            }

            case OptionalTypeSymbol optional: {
                if (Lower(optional.UnderlyingType) is not { } wrapped) {
                    return null;
                }

                var name = $"Optional${Sanitise(wrapped.ToString())}";
                if (_module.FindType(name) is EnumType existing) {
                    return existing;
                }

                var enumType = new EnumType(name);
                enumType.Cases.Add(("Some", wrapped));
                enumType.Cases.Add(("None", null));
                return _module.AddType(enumType);
            }

            case NamedTypeSymbol { TypeParameters.IsEmpty: true, TypeKind: TypeKind.Struct } named: {
                var fields = new List<(string, SilType)>();
                foreach (var field in StoredFields(named)) {
                    if (Lower(field.Type) is not { } fieldType) {
                        return null;
                    }

                    fields.Add((field.Name, fieldType));
                }

                var structType = _module.AddType(new StructType(TypeName(named)));
                structType.Fields.AddRange(fields);
                return structType;
            }

            case NamedTypeSymbol { TypeParameters.IsEmpty: true, TypeKind: TypeKind.Union } named: {
                var cases = new List<(string, SilType?)>();
                foreach (var unionCase in named.Members.OfType<UnionCaseSymbol>()) {
                    SilType? payload = null;
                    if (unionCase.Payload.Length > 0) {
                        var parts = ImmutableArray.CreateBuilder<SilType>();
                        foreach (var field in unionCase.Payload) {
                            if (Lower(field.Type) is not { } part) {
                                return null;
                            }

                            parts.Add(part);
                        }

                        payload = parts.Count == 1 ? parts[0] : SilType.Tuple(parts.ToImmutable());
                    }

                    cases.Add((unionCase.Name, payload));
                }

                var enumType = _module.AddType(new EnumType(TypeName(named)));
                enumType.Cases.AddRange(cases);
                return enumType;
            }

            case NamedTypeSymbol { TypeKind: TypeKind.Enum } named: {
                var enumType = _module.AddType(new EnumType(TypeName(named)));
                foreach (var enumCase in named.Members.OfType<EnumCaseSymbol>()) {
                    enumType.Cases.Add((enumCase.Name, null));
                }

                return enumType;
            }

            default:
                return null;
        }
    }

    static string TypeName(NamedTypeSymbol type) => $"{type.ContainingSymbol?.Name}.{type.Name}";

    internal static IEnumerable<FieldSymbol> StoredFields(NamedTypeSymbol type) =>
        type.Members.OfType<FieldSymbol>().Where(static f => f is { IsStatic: false, IsComputed: false });

    T? Unsupported<T>(SyntaxNode at, string what) where T : class {
        _diagnostics.Error(ErrorCode.ERR_NotLoweredYet, at.Span, $"SIL lowering does not handle {what} yet");
        return null;
    }

    // =======================================================================

    sealed class FunctionLowering(SilGen generator, FunctionSymbol symbol, SilFunction function, SourceText source) {
        /// <summary>Marks an expression whose value is <c>()</c>: something to have done, nothing to use.</summary>
        sealed class UnitValue : SilValue {
            public static readonly UnitValue Instance = new();
            public override SilType Type => SilType.Unit;
        }

        sealed record Loop(string? Label, SilBlock Break, SilBlock Continue, int Scopes);

        /// <summary>Where an error goes: a block taking it, and how many defer scopes were open there.</summary>
        sealed record Handler(SilBlock Block, SilType Type, int Scopes);

        readonly Dictionary<ParameterSymbol, SilValue> _parameters = [];
        readonly Dictionary<LocalSymbol, AllocStackInst> _slots = [];
        readonly List<AllocStackInst> _allocations = [];
        readonly List<Loop> _loops = [];
        readonly List<Handler> _handlers = [];

        /// <summary>The deferred bodies of each open scope, innermost last.</summary>
        readonly List<List<BoundStatement>> _scopes = [];

        SilValue? _selfValue;
        SilValue? _selfAddress;
        SilBlock? _current;

        /// <summary>The source position instructions emitted now belong to.</summary>
        SilLocation? _location;

        void Locate(SyntaxNode syntax) {
            var position = source.GetLinePosition(syntax.Span.Start);
            _location = new SilLocation(position.Line, position.Column);
        }
        SilBlock _entry = null!;
        bool _failed;

        CoreTypes Core => generator.Core;

        public void Lower(BoundBlock body) {
            _entry = function.AddBlock();
            var parameterTypes = function.Type.Parameters;
            var next = 0;

            if (HasSelf(symbol)) {
                var self = _entry.AddArgument(parameterTypes[next++]);
                if (symbol.IsMutating) {
                    _selfAddress = self;
                } else {
                    _selfValue = self;
                }
            }

            foreach (var parameter in symbol.Parameters) {
                _parameters[parameter] = _entry.AddArgument(parameterTypes[next++]);
            }

            _current = _entry;

            // An initialiser builds 'self' in a slot of its own, field by field.
            if (symbol.FunctionKind == FunctionKind.Initializer) {
                _selfAddress = NewSlot(function.Type.Result, "self");
            }

            Statement(body);

            // Falling off the end: fine for a function returning nothing, and
            // for an initialiser, which returns what it built. For one with a
            // result the binder has said every path returns, so the end
            // cannot be reached.
            if (_current is not null) {
                if (symbol.FunctionKind == FunctionKind.Initializer) {
                    Return(Emit(new LoadInst(_selfAddress!)));
                } else if (function.Type.Result == SilType.Unit) {
                    Return(null);
                } else {
                    Jump(new UnreachableInst());
                }
            }

            // The exits, built last, so that they free every slot the body made.
            if (_returnBlock is { } returnBlock) {
                _current = returnBlock;
                _location = null;
                Deallocate();
                Jump(new ReturnInst(returnBlock.Arguments.Count == 0 ? null : returnBlock.Arguments[0]));
            }

            if (_throwBlock is { } throwBlock) {
                _current = throwBlock;
                Deallocate();
                Jump(new ThrowInst(throwBlock.Arguments[0]));
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

        SilBlock? _throwBlock;

        // -------------------------------------------------------------------
        // Emitting
        // -------------------------------------------------------------------

        void Fail(SyntaxNode at, string what) {
            if (!_failed) {
                generator.Unsupported<object>(at, what);
            }

            _failed = true;
        }

        T Emit<T>(T instruction) where T : SilInstruction {
            // After a jump nothing is reachable until a label; code there is
            // lowered into a block of its own, which is then dropped.
            _current ??= function.AddBlock();
            instruction.Location ??= _location;
            return _current.Append(instruction);
        }

        void Jump(SilInstruction terminator) {
            Emit(terminator);
            _current = null;
        }

        void Branch(SilBlock target, params SilValue[] arguments) => Jump(new BranchInst(target, arguments));

        /// <summary>A stack slot, allocated in the entry block so that one place frees them all.</summary>
        AllocStackInst NewSlot(SilType type, string? variable = null) {
            var slot = new AllocStackInst(type) { Block = _entry, VariableName = variable, Location = _location };
            _entry.Instructions.Insert(_allocations.Count, slot);
            _allocations.Add(slot);
            return slot;
        }

        AllocStackInst? SlotFor(LocalSymbol local, SyntaxNode at) {
            if (_slots.TryGetValue(local, out var slot)) {
                return slot;
            }

            if (generator.Lower(local.Type) is not { } type) {
                Fail(at, $"a local of type '{local.Type.ToDisplayString()}'");
                return null;
            }

            return _slots[local] = NewSlot(type, local.Name);
        }

        void Deallocate() {
            for (var i = _allocations.Count - 1; i >= 0; i--) {
                Emit(new DeallocStackInst(_allocations[i]));
            }
        }

        /// <summary>Runs the deferred bodies of every scope deeper than <paramref name="depth" />, innermost first.</summary>
        void RunDefers(int depth) {
            var saved = _scopes.ToList();
            for (var i = saved.Count - 1; i >= depth; i--) {
                // While a scope's defers run, it and those inside it are closed.
                _scopes.RemoveRange(i, _scopes.Count - i);
                foreach (var deferred in Enumerable.Reverse(saved[i])) {
                    Statement(deferred);
                }
            }

            _scopes.Clear();
            _scopes.AddRange(saved);
        }

        /// <summary>
        ///     The one block every return goes through. Its deallocations are
        ///     written last, when every slot is known: a slot made after some
        ///     return was lowered must still be freed on that return's path.
        /// </summary>
        SilBlock? _returnBlock;

        void Return(SilValue? value) {
            RunDefers(0);

            if (_returnBlock is null) {
                _returnBlock = function.AddBlock();
                if (function.Type.Result != SilType.Unit) {
                    _returnBlock.AddArgument(function.Type.Result);
                }
            }

            if (function.Type.Result == SilType.Unit) {
                Branch(_returnBlock);
            } else if (value is not null and not UnitValue) {
                Branch(_returnBlock, value);
            }
        }

        /// <summary>Sends an error to the innermost handler, running the defers on the way.</summary>
        void RouteError(SilValue error, SyntaxNode at) {
            var handler = _handlers.Count > 0 ? _handlers[^1] : FunctionHandler(error.Type, at);
            if (handler is null) {
                return;
            }

            if (handler.Type != error.Type) {
                Fail(at, $"an error of type {error.Type} converted to {handler.Type}");
                return;
            }

            RunDefers(handler.Scopes);
            Branch(handler.Block, error);
        }

        /// <summary>The function's own throw, as a handler.</summary>
        Handler? FunctionHandler(SilType errorType, SyntaxNode at) {
            if (function.Type.Thrown is not { } thrown) {
                Fail(at, "an error where nothing can handle it");
                return null;
            }

            if (_throwBlock is null) {
                _throwBlock = function.AddBlock();
                _throwBlock.AddArgument(thrown);
            }

            _ = errorType;
            return new(_throwBlock, thrown, 0);
        }

        // -------------------------------------------------------------------
        // Statements
        // -------------------------------------------------------------------

        void Statement(BoundStatement statement) {
            if (_failed) {
                return;
            }

            if (statement is not BoundBlock) {
                Locate(statement.Syntax);
            }

            switch (statement) {
                case BoundBlock block:
                    _scopes.Add([]);
                    foreach (var child in block.Statements) {
                        Statement(child);
                    }

                    if (_current is not null) {
                        RunDefers(_scopes.Count - 1);
                    }

                    _scopes.RemoveAt(_scopes.Count - 1);
                    return;

                case BoundDefer defer:
                    _scopes[^1].Add(defer.Body);
                    return;

                case BoundLocalDeclaration declaration:
                    if (declaration.Initializer is { } initializer
                        && SlotFor(declaration.Local, declaration.Syntax) is { } slot
                        && Expression(initializer) is { } initial) {
                        // A declaration fills an empty slot; only a later
                        // assignment replaces a value.
                        Emit(new StoreInst(initial, slot, StoreKind.Init));
                    }

                    return;

                case BoundPatternDeclaration declaration: {
                    if (Expression(declaration.Initializer) is not { } value) {
                        return;
                    }

                    // Irrefutable, so the failing branch cannot be taken.
                    var matched = function.AddBlock();
                    var failed = function.AddBlock();
                    Pattern(declaration.Pattern, value, matched, failed);
                    _current = failed;
                    Jump(new UnreachableInst());
                    _current = matched;
                    return;
                }

                case BoundExpressionStatement expression:
                    Expression(expression.Expression);
                    return;

                case BoundReturn returnStatement: {
                    if (returnStatement.Value is { } returned) {
                        if (Expression(returned) is { } value) {
                            Return(value);
                        }
                    } else if (symbol.FunctionKind == FunctionKind.Initializer) {
                        Return(Emit(new LoadInst(_selfAddress!)));
                    } else {
                        Return(null);
                    }

                    return;
                }

                case BoundThrow throwStatement:
                    if (Expression(throwStatement.Value) is { } error) {
                        RouteError(error, throwStatement.Syntax);
                    }

                    return;

                case BoundIf ifStatement:
                    If(ifStatement);
                    return;

                case BoundWhile loop: {
                    var header = function.AddBlock();
                    var body = function.AddBlock();
                    var after = function.AddBlock();
                    Branch(header);

                    _current = header;
                    Condition(loop.Condition, body, after);

                    _current = body;
                    InLoop(loop, after, header, () => Statement(loop.Body));
                    if (_current is not null) {
                        Branch(header);
                    }

                    _current = after;
                    return;
                }

                case BoundRepeat loop: {
                    var body = function.AddBlock();
                    var test = function.AddBlock();
                    var after = function.AddBlock();
                    Branch(body);

                    _current = body;
                    InLoop(loop, after, test, () => Statement(loop.Body));
                    if (_current is not null) {
                        Branch(test);
                    }

                    _current = test;
                    Condition(loop.Condition, body, after);

                    _current = after;
                    return;
                }

                case BoundForEach { Sequence: BoundRange range } loop:
                    ForRange(loop, range);
                    return;

                case BoundBreak jump when FindLoop(jump.Label) is { } breakLoop:
                    RunDefers(breakLoop.Scopes);
                    Branch(breakLoop.Break);
                    return;

                case BoundContinue jump when FindLoop(jump.Label) is { } continueLoop:
                    RunDefers(continueLoop.Scopes);
                    Branch(continueLoop.Continue);
                    return;

                case BoundDo doStatement:
                    Do(doStatement);
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
                Condition(ifStatement.Condition, after, elseBody);
                _current = elseBody;
                Statement(ifStatement.Consequence);
                if (_current is not null) {
                    Branch(after);
                }

                _current = after;
                return;
            }

            var consequence = function.AddBlock();
            var alternative = ifStatement.Alternative is null ? after : function.AddBlock();
            Condition(ifStatement.Condition, consequence, alternative);

            _current = consequence;
            Statement(ifStatement.Consequence);
            if (_current is not null) {
                Branch(after);
            }

            if (ifStatement.Alternative is { } otherwise) {
                _current = alternative;
                Statement(otherwise);
                if (_current is not null) {
                    Branch(after);
                }
            }

            _current = after;
        }

        /// <summary><c>for i in a..&lt;b</c> and <c>a...b</c>: a counting loop, with no range built.</summary>
        void ForRange(BoundForEach loop, BoundRange range) {
            if (range.Lower is null || range.Upper is null || loop.Element is null) {
                Fail(loop.Syntax, "a 'for' over an open range");
                return;
            }

            if (Expression(range.Lower) is not { } lower || Expression(range.Upper) is not { } upper) {
                return;
            }

            if (lower.Type != SilType.Int || SlotFor(loop.Element, loop.Syntax) is not { } element) {
                Fail(loop.Syntax, "a 'for' over a range that is not of Int");
                return;
            }

            var counter = NewSlot(SilType.Int);
            Emit(new StoreInst(lower, counter, StoreKind.Init));

            var header = function.AddBlock();
            var body = function.AddBlock();
            var step = function.AddBlock();
            var after = function.AddBlock();
            Branch(header);

            _current = header;
            var current = Emit(new LoadInst(counter));
            var compare = range.IsInclusive ? "cmp_le_Int" : "cmp_lt_Int";
            var inside = Emit(new BuiltinInst(compare, SilType.Bool, current, upper));
            Jump(new CondBranchInst(inside, body, [], after, []));

            _current = body;
            Emit(new StoreInst(Emit(new LoadInst(counter)), element, StoreKind.Assign));
            if (loop.Filter is { } filter) {
                var filtered = function.AddBlock();
                Condition(filter, filtered, step);
                _current = filtered;
            }

            InLoop(loop, after, step, () => Statement(loop.Body));
            if (_current is not null) {
                Branch(step);
            }

            // The last value of an inclusive range is the upper bound itself;
            // stopping there, rather than stepping past it, keeps 'a...Int.max'
            // from overflowing.
            _current = step;
            var last = Emit(new LoadInst(counter));
            var more = function.AddBlock();
            if (range.IsInclusive) {
                var atEnd = Emit(new BuiltinInst("cmp_eq_Int", SilType.Bool, last, upper));
                Jump(new CondBranchInst(atEnd, after, [], more, []));
            } else {
                Branch(more);
            }

            _current = more;
            var one = Emit(new IntegerLiteralInst(SilType.Int, 1));
            Emit(new StoreInst(Emit(new BuiltinInst("add_Int", SilType.Int, last, one)), counter, StoreKind.Assign));
            Branch(header);

            _current = after;
        }

        void InLoop(BoundStatement loop, SilBlock breakTarget, SilBlock continueTarget, Action body) {
            var label = loop.Syntax.Parent is LabeledStatementSyntax { Label.Text: { Length: > 0 } text } ? text : null;
            _loops.Add(new(label, breakTarget, continueTarget, _scopes.Count));
            body();
            _loops.RemoveAt(_loops.Count - 1);
        }

        Loop? FindLoop(string? label) {
            for (var i = _loops.Count - 1; i >= 0; i--) {
                if (label is null || _loops[i].Label == label) {
                    return _loops[i];
                }
            }

            return null;
        }

        void Do(BoundDo doStatement) {
            var after = function.AddBlock();

            if (doStatement.Catches.IsEmpty) {
                Statement(doStatement.Body);
                if (_current is not null) {
                    Branch(after);
                }

                _current = after;
                return;
            }

            // The catches all see one error type: what the body can throw.
            var errorType = ErrorTypeOf(doStatement);
            if (errorType is null) {
                Fail(doStatement.Syntax, "a 'do' whose catches take an error type that has no SIL type yet");
                return;
            }

            var dispatch = function.AddBlock();
            var error = dispatch.AddArgument(errorType);

            _handlers.Add(new(dispatch, errorType, _scopes.Count));
            Statement(doStatement.Body);
            _handlers.RemoveAt(_handlers.Count - 1);
            if (_current is not null) {
                Branch(after);
            }

            // Each catch in turn; one that does not match, or whose guard
            // fails, passes the error to the next, and the last to whatever
            // handles errors outside.
            _current = dispatch;
            foreach (var clause in doStatement.Catches) {
                var handler = function.AddBlock();
                var next = function.AddBlock();

                if (clause.Pattern is { } pattern) {
                    Pattern(pattern, error, handler, next);
                } else {
                    Branch(handler);
                }

                _current = handler;
                if (clause.ErrorLocal is { } local && SlotFor(local, clause.Syntax) is { } slot) {
                    Emit(new StoreInst(error, slot, StoreKind.Assign));
                }

                if (clause.Guard is { } guard) {
                    var guarded = function.AddBlock();
                    Condition(guard, guarded, next);
                    _current = guarded;
                }

                Statement(clause.Body);
                if (_current is not null) {
                    Branch(after);
                }

                _current = next;
            }

            // Nothing caught it: on to whatever handles errors outside — unless
            // the last catch takes everything, when nothing gets this far.
            var last = doStatement.Catches[^1];
            if (last.Pattern is null && last.Guard is null) {
                Jump(new UnreachableInst());
            } else {
                RouteError(error, doStatement.Syntax);
            }

            _current = after;
        }

        /// <summary>The error type a 'do' body throws, from its catches' implicit error local or patterns.</summary>
        SilType? ErrorTypeOf(BoundDo doStatement) {
            foreach (var clause in doStatement.Catches) {
                if (clause.ErrorLocal is { } local) {
                    return generator.Lower(local.Type);
                }

                if (clause.Pattern is { } pattern) {
                    return generator.Lower(pattern.InputType);
                }
            }

            return null;
        }

        // -------------------------------------------------------------------
        // Conditions and patterns
        // -------------------------------------------------------------------

        /// <summary>Evaluates a condition and branches on it, short-circuiting '&amp;&amp;', '||' and '!'.</summary>
        void Condition(BoundExpression condition, SilBlock whenTrue, SilBlock whenFalse) {
            switch (condition) {
                case BoundBinaryOperator { OperatorKind: BinaryOperatorKind.LogicalAnd } conjunction: {
                    var right = function.AddBlock();
                    Condition(conjunction.Left, right, whenFalse);
                    _current = right;
                    Condition(conjunction.Right, whenTrue, whenFalse);
                    return;
                }

                case BoundBinaryOperator { OperatorKind: BinaryOperatorKind.LogicalOr } disjunction: {
                    var right = function.AddBlock();
                    Condition(disjunction.Left, whenTrue, right);
                    _current = right;
                    Condition(disjunction.Right, whenTrue, whenFalse);
                    return;
                }

                case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Not } negation:
                    Condition(negation.Operand, whenFalse, whenTrue);
                    return;

                // 'if let x = e': matched when e holds a value, which x then is.
                case BoundConditionBinding { Local: { } local } binding: {
                    if (Expression(binding.Source) is not { } optional || SlotFor(local, binding.Syntax) is not { } slot) {
                        return;
                    }

                    var optionalType = optional.Type as EnumType;
                    var some = optionalType?.CaseIndex("Some") ?? -1;
                    if (optionalType is null || some < 0) {
                        Fail(binding.Syntax, "an 'if let' on something that is not optional");
                        return;
                    }

                    var bound = function.AddBlock();
                    var payload = bound.AddArgument(optionalType.Cases[some].Payload!);
                    Jump(new SwitchEnumInst(optional, [(some, bound)], whenFalse));
                    _current = bound;
                    Emit(new StoreInst(payload, slot, StoreKind.Assign));
                    Branch(whenTrue);
                    return;
                }

                // 'if case .Circle(let r) = shape'.
                case BoundConditionBinding { Pattern: { } pattern } binding: {
                    if (Expression(binding.Source) is { } value) {
                        Pattern(pattern, value, whenTrue, whenFalse);
                    }

                    return;
                }

                default:
                    if (Expression(condition) is { } flag) {
                        Jump(new CondBranchInst(flag, whenTrue, [], whenFalse, []));
                    }

                    return;
            }
        }

        /// <summary>
        ///     Tests a value against a pattern, binding what it names, and goes
        ///     to <paramref name="matched" /> or <paramref name="failed" />.
        /// </summary>
        void Pattern(BoundPattern pattern, SilValue value, SilBlock matched, SilBlock failed) {
            if (_failed) {
                return;
            }

            switch (pattern) {
                case BoundWildcardPattern:
                    Branch(matched);
                    return;

                case BoundBindingPattern binding:
                    if (SlotFor(binding.Local, binding.Syntax) is { } slot) {
                        Emit(new StoreInst(value, slot, StoreKind.Assign));
                        Branch(matched);
                    }

                    return;

                case BoundLiteralPattern { Value: null } when value.Type is EnumType optional && optional.CaseIndex("None") is var none and >= 0:
                    Jump(new SwitchEnumInst(value, [(none, matched)], failed));
                    return;

                case BoundLiteralPattern literal: {
                    if (LiteralValue(literal.Value, literal.IsNegative, value.Type, literal.Syntax) is not { } constant) {
                        return;
                    }

                    var equal = Emit(new BuiltinInst($"cmp_eq_{value.Type}", SilType.Bool, value, constant));
                    Jump(new CondBranchInst(equal, matched, [], failed, []));
                    return;
                }

                case BoundRangePattern { Lower: BoundLiteralPattern lower, Upper: BoundLiteralPattern upper } range: {
                    if (LiteralValue(lower.Value, lower.IsNegative, value.Type, range.Syntax) is not { } low
                        || LiteralValue(upper.Value, upper.IsNegative, value.Type, range.Syntax) is not { } high) {
                        return;
                    }

                    var aboveLow = function.AddBlock();
                    var atLeast = Emit(new BuiltinInst($"cmp_ge_{value.Type}", SilType.Bool, value, low));
                    Jump(new CondBranchInst(atLeast, aboveLow, [], failed, []));
                    _current = aboveLow;
                    var atMost = Emit(new BuiltinInst($"cmp_le_{value.Type}", SilType.Bool, value, high));
                    Jump(new CondBranchInst(atMost, matched, [], failed, []));
                    return;
                }

                case BoundTuplePattern tuple when value.Type is TupleType:
                    Sequence(
                        tuple.Elements.Select((element, i) => (element, (Func<SilValue>)(() => Emit(new TupleExtractInst(value, i))))),
                        matched,
                        failed
                    );
                    return;

                case BoundRecordPattern { TestsType: false } record when value.Type is StructType structType:
                    Sequence(
                        record.Fields.Select(f => (f.Pattern,
                            (Func<SilValue>)(() => Emit(new StructExtractInst(value, structType.FieldIndex(f.Field.Name)))))),
                        matched,
                        failed
                    );
                    return;

                case BoundUnionCasePattern unionCase when value.Type is EnumType enumType: {
                    var index = enumType.CaseIndex(unionCase.UnionCase.Name);
                    var target = function.AddBlock();
                    var payloadType = enumType.Cases[index].Payload;
                    var payload = payloadType is null ? null : target.AddArgument(payloadType);
                    Jump(new SwitchEnumInst(value, [(index, target)], failed));
                    _current = target;

                    if (payload is null || unionCase.Payload.IsEmpty) {
                        Branch(matched);
                    } else if (unionCase.Payload.Length == 1 && payloadType is not TupleType) {
                        Pattern(unionCase.Payload[0], payload, matched, failed);
                    } else {
                        Sequence(
                            unionCase.Payload.Select((sub, i) => (sub, (Func<SilValue>)(() => Emit(new TupleExtractInst(payload, i))))),
                            matched,
                            failed
                        );
                    }

                    return;
                }

                case BoundEnumCasePattern enumCase when value.Type is EnumType enumType:
                    Jump(new SwitchEnumInst(value, [(enumType.CaseIndex(enumCase.EnumCase.Name), matched)], failed));
                    return;

                case BoundOrPattern or: {
                    var right = function.AddBlock();
                    Pattern(or.Left, value, matched, right);
                    _current = right;
                    Pattern(or.Right, value, matched, failed);
                    return;
                }

                default:
                    Fail(pattern.Syntax, $"the pattern '{Describe(pattern.Syntax)}'");
                    return;
            }
        }

        /// <summary>Several sub-patterns that must all match, tested left to right.</summary>
        void Sequence(IEnumerable<(BoundPattern Pattern, Func<SilValue> Value)> parts, SilBlock matched, SilBlock failed) {
            var list = parts.ToList();
            if (list.Count == 0) {
                Branch(matched);
                return;
            }

            for (var i = 0; i < list.Count; i++) {
                var next = i == list.Count - 1 ? matched : function.AddBlock();
                Pattern(list[i].Pattern, list[i].Value(), next, failed);
                _current = next == matched ? null : next;
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

                case BoundConversion { ConversionKind: ConversionKind.OptionalWrap } conversion:
                    return Wrap(conversion);

                // 'x!': the value, or a trap.
                case BoundConversion { ConversionKind: ConversionKind.Explicit, Operand.Type: OptionalTypeSymbol } conversion:
                    return Unwrap(conversion);

                case BoundConversion { ConversionKind: ConversionKind.Explicit } conversion:
                    return NumericConversion(conversion);

                case BoundParameterAccess access when _parameters.TryGetValue(access.Parameter, out var parameter):
                    return parameter;

                case BoundLocalAccess access when _slots.TryGetValue(access.Local, out var slot):
                    return Emit(new LoadInst(slot));

                case BoundSelfAccess:
                    return _selfValue ?? (_selfAddress is { } self ? Emit(new LoadInst(self)) : Missing(expression, "'self' here"));

                case BoundAssignment assignment: {
                    if (Expression(assignment.Value) is not { } value || Address(assignment.Target) is not { } address) {
                        return null;
                    }

                    Emit(new StoreInst(value, address, StoreKind.Assign));
                    return value;
                }

                case BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } field }: {
                    if (Expression(receiver) is not { } value) {
                        return null;
                    }

                    var index = (value.Type as StructType)?.FieldIndex(field.Name) ?? -1;
                    return index < 0
                        ? Missing(expression, $"the member '{field.Name}'")
                        : Emit(new StructExtractInst(value, index));
                }

                case BoundTupleElementAccess element:
                    return Expression(element.Receiver) is { Type: TupleType } elementOf
                        ? Emit(new TupleExtractInst(elementOf, element.Index))
                        : Missing(expression, "this tuple element");

                case BoundTuple tuple: {
                    var elements = new List<SilValue>();
                    foreach (var element in tuple.Elements) {
                        if (Expression(element) is not { } value) {
                            return null;
                        }

                        elements.Add(value);
                    }

                    return Emit(new TupleInst(new TupleType([..elements.Select(static e => e.Type)]), [..elements]));
                }

                case BoundRecordExpression record:
                    return Record(record);

                case BoundUnionCaseConstruction construction:
                    return UnionCase(construction.Type, construction.UnionCase.Name, construction.Arguments, expression);

                case BoundUnionCaseGroup group:
                    return UnionCase(group.Type, group.UnionCase.Name, [], expression);

                case BoundBinaryOperator { OperatorKind: BinaryOperatorKind.LogicalAnd or BinaryOperatorKind.LogicalOr }:
                case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Not, Type: var notType } when notType.Equals(Core.Bool):
                    return BoolFromBranch(expression);

                case BoundBinaryOperator { OperatorKind: BinaryOperatorKind.Coalesce } coalesce:
                    return Coalesce(coalesce);

                case BoundBinaryOperator binary when binary.Method is null:
                    return Binary(binary);

                case BoundUnaryOperator unary:
                    return Unary(unary);

                case BoundConditional conditional: {
                    if (generator.Lower(conditional.Type) is not { } type) {
                        return Missing(conditional, $"a '?:' of type '{conditional.Type.ToDisplayString()}'");
                    }

                    var whenTrue = function.AddBlock();
                    var whenFalse = function.AddBlock();
                    var after = function.AddBlock();
                    var result = after.AddArgument(type);
                    Condition(conditional.Condition, whenTrue, whenFalse);

                    _current = whenTrue;
                    if (Expression(conditional.WhenTrue) is { } yes) {
                        Branch(after, yes);
                    }

                    _current = whenFalse;
                    if (Expression(conditional.WhenFalse) is { } no) {
                        Branch(after, no);
                    }

                    _current = after;
                    return result;
                }

                case BoundMatch match:
                    return Match(match);

                case BoundTry tryExpression:
                    return Try(tryExpression);

                case BoundCall call:
                    return Call(call);

                default:
                    return Missing(expression, $"the expression '{Describe(expression.Syntax)}'");
            }
        }

        SilValue? Missing(BoundNode node, string what) {
            Fail(node.Syntax, what);
            return null;
        }

        /// <summary>Where an assignment stores: a local, <c>self</c>, or a field or element inside one.</summary>
        SilValue? Address(BoundExpression target) {
            switch (target) {
                case BoundLocalAccess local:
                    return SlotFor(local.Local, local.Syntax);

                case BoundSelfAccess when _selfAddress is { } self:
                    return self;

                case BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } field }: {
                    if (Address(receiver) is not { } address) {
                        return null;
                    }

                    var structType = (address.Type as AddressType)?.Pointee as StructType;
                    var index = structType?.FieldIndex(field.Name) ?? -1;
                    return index < 0
                        ? Missing(target, $"storing to '{field.Name}'")
                        : Emit(new StructElementAddrInst(address, index));
                }

                case BoundTupleElementAccess element:
                    return Address(element.Receiver) is { } tuple
                        ? Emit(new TupleElementAddrInst(tuple, element.Index))
                        : null;

                default:
                    return Missing(target, $"storing to '{Describe(target.Syntax)}'");
            }
        }

        SilValue? Literal(BoundLiteral literal, TypeSymbol type) {
            // 'none': the optional with nothing in it.
            if (literal.Value is null && generator.Lower(type) is EnumType optional && optional.CaseIndex("None") is var none and >= 0) {
                return Emit(new EnumInst(optional, none, null));
            }

            // A plain enum's case is bound as a literal naming it.
            if (literal.Value is string caseName && type is NamedTypeSymbol { TypeKind: TypeKind.Enum }) {
                return generator.Lower(type) is EnumType enumType && enumType.CaseIndex(caseName) is var index and >= 0
                    ? Emit(new EnumInst(enumType, index, null))
                    : Missing(literal, $"the enum case '{caseName}'");
            }

            if (generator.Lower(type) is not { } silType) {
                return Missing(literal, $"a literal of type '{type.ToDisplayString()}'");
            }

            return LiteralValue(literal.Value, false, silType, literal.Syntax);
        }

        SilValue? LiteralValue(object? value, bool negative, SilType type, SyntaxNode at) {
            if (type == SilType.String && value is string text) {
                return Emit(new StringLiteralInst(text));
            }

            if (type == SilType.Bool && value is bool flag) {
                return Emit(new IntegerLiteralInst(SilType.Bool, flag ? 1 : 0));
            }

            if (type == SilType.Int && value is long or int or ulong) {
                var number = System.Convert.ToDecimal(value);
                if (negative) {
                    number = -number;
                }

                if (number is > long.MaxValue or < long.MinValue) {
                    Fail(at, $"the integer '{value}', which does not fit in Int");
                    return null;
                }

                return Emit(new IntegerLiteralInst(SilType.Int, (long)number));
            }

            if (type == SilType.F64 && value is double or long or int or ulong) {
                var number = System.Convert.ToDouble(value);
                return Emit(new FloatLiteralInst(negative ? -number : number));
            }

            Fail(at, $"the literal '{Describe(at)}' of type {type}");
            return null;
        }

        SilValue? Wrap(BoundConversion conversion) {
            if (generator.Lower(conversion.Type) is not EnumType optional) {
                return Missing(conversion, $"an optional of '{conversion.Operand.Type.ToDisplayString()}'");
            }

            // A literal wrapped straight into an optional takes the wrapped type.
            var inner = conversion.Operand is BoundLiteral literal
                ? Literal(literal, ((OptionalTypeSymbol)conversion.Type).UnderlyingType)
                : Expression(conversion.Operand);

            return inner is null ? null : Emit(new EnumInst(optional, optional.CaseIndex("Some"), inner));
        }

        SilValue? Unwrap(BoundConversion conversion) {
            if (Expression(conversion.Operand) is not { Type: EnumType optional } value) {
                return null;
            }

            var some = optional.CaseIndex("Some");
            var present = function.AddBlock();
            var payload = present.AddArgument(optional.Cases[some].Payload!);
            var absent = function.AddBlock();
            Jump(new SwitchEnumInst(value, [(some, present)], absent));

            _current = absent;
            Emit(new CondFailInst(Emit(new IntegerLiteralInst(SilType.Bool, 1)), "unwrapped a none"));
            Jump(new UnreachableInst());

            _current = present;
            return payload;
        }

        SilValue? NumericConversion(BoundConversion conversion) {
            if (Expression(conversion.Operand) is not { } value || generator.Lower(conversion.Type) is not { } target) {
                return null;
            }

            if (value.Type == target) {
                return value;
            }

            var name = $"convert_{value.Type}_{target}";
            return SilBuiltins.Find(name) is not null
                ? Emit(new BuiltinInst(name, target, value))
                : Missing(conversion, $"the conversion from {value.Type} to {target}");
        }

        SilValue? Record(BoundRecordExpression record) {
            if (generator.Lower(record.Type) is not StructType structType) {
                return Missing(record, $"a record of type '{record.Type.ToDisplayString()}'");
            }

            var fields = new SilValue?[structType.Fields.Count];
            foreach (var (field, initializer) in record.Fields) {
                if (Expression(initializer) is not { } value) {
                    return null;
                }

                fields[structType.FieldIndex(field.Name)] = value;
            }

            return fields.Any(static f => f is null)
                ? Missing(record, "a record that leaves a field to its default")
                : Emit(new StructInst(structType, [..fields!]));
        }

        SilValue? UnionCase(TypeSymbol unionType, string name, IReadOnlyList<BoundExpression> arguments, BoundNode at) {
            var enumType = generator.Lower(unionType) as EnumType;
            var index = enumType?.CaseIndex(name) ?? -1;
            if (enumType is null || index < 0) {
                return Missing(at, $"the case '{name}'");
            }

            var values = new List<SilValue>();
            foreach (var argument in arguments) {
                if (Expression(argument) is not { } value) {
                    return null;
                }

                values.Add(value);
            }

            SilValue? payload = values.Count switch {
                0 => null,
                1 when enumType.Cases[index].Payload is not TupleType => values[0],
                _ => Emit(new TupleInst((TupleType)enumType.Cases[index].Payload!, [..values])),
            };

            return Emit(new EnumInst(enumType, index, payload));
        }

        /// <summary>'a ?? b': a's value when there is one; b, evaluated only then, when there is not.</summary>
        SilValue? Coalesce(BoundBinaryOperator coalesce) {
            if (Expression(coalesce.Left) is not { Type: EnumType optional } value || generator.Lower(coalesce.Type) is not { } type) {
                return Missing(coalesce, "this '??'");
            }

            var some = optional.CaseIndex("Some");
            var present = function.AddBlock();
            var payload = present.AddArgument(optional.Cases[some].Payload!);
            var absent = function.AddBlock();
            var after = function.AddBlock();
            var result = after.AddArgument(type);
            Jump(new SwitchEnumInst(value, [(some, present)], absent));

            _current = present;
            Branch(after, payload);

            _current = absent;
            if (Expression(coalesce.Right) is { } fallback) {
                Branch(after, fallback);
            }

            _current = after;
            return result;
        }

        /// <summary>A Bool computed by branching: '&amp;&amp;', '||' and '!' as values.</summary>
        SilValue BoolFromBranch(BoundExpression condition) {
            var whenTrue = function.AddBlock();
            var whenFalse = function.AddBlock();
            var after = function.AddBlock();
            var result = after.AddArgument(SilType.Bool);
            Condition(condition, whenTrue, whenFalse);

            _current = whenTrue;
            Branch(after, Emit(new IntegerLiteralInst(SilType.Bool, 1)));
            _current = whenFalse;
            Branch(after, Emit(new IntegerLiteralInst(SilType.Bool, 0)));

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
                return Missing(binary, $"the operator in '{Describe(binary.Syntax)}' on '{binary.Left.Type.ToDisplayString()}'");
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
                return Missing(unary, $"the operator in '{Describe(unary.Syntax)}'");
            }

            return Emit(new BuiltinInst(name, signature.Result, operand));
        }

        /// <summary>
        ///     A <c>match</c>: arms tried in order, each pattern in turn, then the
        ///     guard. The binder has checked it is exhaustive, so failing every
        ///     arm cannot happen.
        /// </summary>
        SilValue? Match(BoundMatch match) {
            if (Expression(match.Subject) is not { } subject) {
                return null;
            }

            var resultType = generator.Lower(match.Type);
            var producesValue = resultType is not null && resultType != SilType.Unit && !match.Type.Equals(Core.Never);
            var after = function.AddBlock();
            var result = producesValue ? after.AddArgument(resultType!) : null;

            foreach (var arm in match.Arms) {
                var body = function.AddBlock();
                var nextArm = function.AddBlock();

                if (arm.IsDefault) {
                    Branch(body);
                } else {
                    for (var i = 0; i < arm.Patterns.Length; i++) {
                        var nextPattern = i == arm.Patterns.Length - 1 ? nextArm : function.AddBlock();
                        Pattern(arm.Patterns[i], subject, body, nextPattern);
                        _current = nextPattern;
                    }
                }

                _current = body;
                if (arm.Guard is { } guard) {
                    var guarded = function.AddBlock();
                    Condition(guard, guarded, nextArm);
                    _current = guarded;
                }

                switch (arm.Body) {
                    case BoundStatement statement:
                        Statement(statement);
                        if (_current is not null) {
                            if (producesValue) {
                                Jump(new UnreachableInst());
                            } else {
                                Branch(after);
                            }
                        }

                        break;

                    case BoundExpression value:
                        if (Expression(value) is { } armValue && _current is not null) {
                            if (!producesValue) {
                                Branch(after);
                            } else if (armValue is not UnitValue && armValue.Type == resultType) {
                                Branch(after, armValue);
                            } else {
                                Jump(new UnreachableInst());
                            }
                        }

                        break;
                }

                _current = nextArm;
            }

            Jump(new UnreachableInst());
            _current = after;
            return result ?? (SilValue)UnitValue.Instance;
        }

        /// <summary>
        ///     <c>try e</c> lets errors go to the handler in force; the others
        ///     put a handler of their own around <c>e</c>.
        /// </summary>
        SilValue? Try(BoundTry tryExpression) {
            if (tryExpression.TryKind == TryKind.Propagate) {
                return Expression(tryExpression.Expression);
            }

            var errorType = ThrownBy(tryExpression.Expression);
            if (errorType is null) {
                return Missing(tryExpression, "a 'try' whose error type has no SIL type yet");
            }

            var resultType = generator.Lower(tryExpression.Type);
            if (resultType is null) {
                return Missing(tryExpression, $"a 'try' of type '{tryExpression.Type.ToDisplayString()}'");
            }

            var handler = function.AddBlock();
            var error = handler.AddArgument(errorType);
            var after = function.AddBlock();
            var result = resultType == SilType.Unit ? null : after.AddArgument(resultType);

            _handlers.Add(new(handler, errorType, _scopes.Count));
            var value = Expression(tryExpression.Expression);
            _handlers.RemoveAt(_handlers.Count - 1);

            if (value is not null && _current is not null) {
                if (tryExpression.TryKind == TryKind.Optional) {
                    var optional = (EnumType)resultType;
                    Branch(after, Emit(new EnumInst(optional, optional.CaseIndex("Some"), value)));
                } else if (result is null) {
                    Branch(after);
                } else {
                    Branch(after, value);
                }
            }

            _current = handler;
            switch (tryExpression.TryKind) {
                case TryKind.Optional: {
                    var optional = (EnumType)resultType;
                    Branch(after, Emit(new EnumInst(optional, optional.CaseIndex("None"), null)));
                    break;
                }

                case TryKind.Force:
                    Emit(new CondFailInst(Emit(new IntegerLiteralInst(SilType.Bool, 1)), "'try!' caught an error"));
                    Jump(new UnreachableInst());
                    break;

                case TryKind.Fallback when tryExpression.Fallback is { } fallback: {
                    if (tryExpression.ErrorLocal is { } local && SlotFor(local, tryExpression.Syntax) is { } slot) {
                        Emit(new StoreInst(error, slot, StoreKind.Assign));
                    }

                    var alternative = Expression(fallback);
                    if (alternative is not null && _current is not null) {
                        if (result is null) {
                            Branch(after);
                        } else {
                            Branch(after, alternative);
                        }
                    }

                    break;
                }
            }

            _current = after;
            return result ?? (SilValue)UnitValue.Instance;
        }

        /// <summary>The error type of the first throwing call inside an expression.</summary>
        SilType? ThrownBy(BoundNode node) {
            if (node is BoundCall { Function.ThrownType: { } thrown }) {
                return generator.Lower(thrown);
            }

            foreach (var child in node.Children) {
                if (child is BoundClosure) {
                    continue;
                }

                if (ThrownBy(child) is { } found) {
                    return found;
                }
            }

            return null;
        }

        SilValue? Call(BoundCall call) {
            Locate(call.Syntax);

            // The core's print is a builtin: the runtime owns the writing.
            if (Core.Print.Contains(call.Function)) {
                return Expression(call.Arguments[0]) is { } printed
                    ? Emit(new BuiltinInst($"print_{printed.Type}", SilType.Unit, printed))
                    : null;
            }

            var callee = call.Function;

            // A synthesised memberwise initialiser is the struct, built from its fields.
            if (callee is { FunctionKind: FunctionKind.Initializer, DeclaringSyntax.IsEmpty: true }) {
                if (generator.Lower(SelfType(callee)) is not StructType structType) {
                    return Missing(call, $"initialising '{SelfType(callee).ToDisplayString()}'");
                }

                var fields = new List<SilValue>();
                foreach (var argument in call.Arguments) {
                    if (Expression(argument) is not { } value) {
                        return null;
                    }

                    fields.Add(value);
                }

                return fields.Count == structType.Fields.Count
                    ? Emit(new StructInst(structType, [..fields]))
                    : Missing(call, "a memberwise initialiser that leaves a field to its default");
            }

            if (!generator._names.TryGetValue(callee, out var name)) {
                return Missing(call, $"the call '{Describe(call.Syntax)}'");
            }

            var type = generator._module.Find(name)?.Type ?? generator.SignatureOf(callee, call.Syntax);
            if (type is null) {
                _failed = true;
                return null;
            }

            var arguments = new List<SilValue>();
            if (HasSelf(callee)) {
                SilValue? self;
                if (callee.IsMutating) {
                    self = call.Receiver is null or BoundSelfAccess ? _selfAddress : Address(call.Receiver);
                } else if (call.Receiver is null or BoundSelfAccess) {
                    self = _selfValue ?? (_selfAddress is { } address ? Emit(new LoadInst(address)) : null);
                } else {
                    self = Expression(call.Receiver);
                }

                if (self is null) {
                    return Missing(call, "the receiver of this call");
                }

                arguments.Add(self);
            }

            foreach (var argument in call.Arguments) {
                if (Expression(argument) is not { } value) {
                    return null;
                }

                arguments.Add(value);
            }

            var reference = Emit(new FunctionRefInst(name, type));

            if (type.Thrown is null) {
                var apply = Emit(new ApplyInst(reference, type.Result, [..arguments]));
                return type.Result == SilType.Unit ? UnitValue.Instance : apply;
            }

            // A throwing call: on to the normal block with the result, or the
            // error to whatever handles it here.
            var normal = function.AddBlock();
            var result = type.Result == SilType.Unit ? null : normal.AddArgument(type.Result);
            var errorBlock = function.AddBlock();
            var error = errorBlock.AddArgument(type.Thrown);
            Jump(new TryApplyInst(reference, type.Result, [..arguments], normal, errorBlock));

            _current = errorBlock;
            RouteError(error, call.Syntax);

            _current = normal;
            return result ?? (SilValue)UnitValue.Instance;
        }

        static string Describe(SyntaxNode syntax) {
            var text = syntax.ToString().Trim();
            var line = text.Split('\n')[0];
            return line.Length > 40 ? line[..40] + "…" : line;
        }
    }
}
