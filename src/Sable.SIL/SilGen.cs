using System.Collections.Immutable;
using System.Text;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     Lowers bound function bodies to SIL (docs/04 §2.2).
/// </summary>
/// <remarks>
///     <para>
///         What lowers so far: <c>Int</c>, <c>Bool</c>, <c>F64</c>,
///         <c>Char</c>; strings, with interpolation and the core's string
///         members; arrays and slices; dictionaries; structs, with their
///         methods, <c>mutating</c> methods, initialisers and computed
///         properties; classes, with inheritance — methods through a vtable,
///         <c>super</c>, <c>deinit</c> — generics, by monomorphisation;
///         closures; protocols, statically through generics and dynamically
///         through <c>any P</c>, with default implementations; tuples;
///         unions, enums and optionals, with <c>match</c>, <c>if let</c>,
///         <c>if case</c>, <c>??</c> and <c>!</c>; <c>throws</c>, <c>try</c>,
///         <c>try?</c>, <c>try!</c>, <c>try … catch</c>, <c>do</c>/<c>catch</c>
///         and <c>throw</c>; <c>defer</c>; <c>if</c>, <c>guard</c>,
///         <c>while</c>, <c>repeat</c>, <c>for</c> over a range or an array,
///         labelled jumps. Anything else is reported as not lowered yet, by
///         construct, rather than lowered wrong.
///     </para>
///     <para>
///         Every local gets a stack slot: <c>alloc_stack</c> in the entry
///         block, <c>store</c> to write it, <c>load</c> to read it, and
///         <c>dealloc_stack</c> for each, in reverse, wherever the function
///         leaves. That is SIL's raw form, as Swift's SILGen produces it;
///         promotion to SSA values is a pass of its own.
///     </para>
///     <para>
///         Ownership is naive and explicit (docs/04 §2.1, docs/02 §2.2): an
///         expression's value is owned, +1; a parameter is borrowed, and copied
///         where something must own it; a temporary is destroyed after its
///         last use. A slot of a non-trivial type starts empty and is written
///         with <c>store [assign]</c>, which destroys what it held, so a loop
///         that declares a local releases the last iteration's value; every
///         such slot is destroyed where the function leaves. Owned temporaries
///         an expression holds while it evaluates the rest are kept on a stack,
///         so that a <c>throw</c> or <c>return</c> partway through destroys
///         them on its way out.
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
public sealed partial class SilGen {
    readonly Compilation _compilation;
    readonly DiagnosticBag _diagnostics = new();
    readonly SilModule _module = new();
    readonly Dictionary<FunctionSymbol, string> _names = [];
    readonly Dictionary<TypeSymbol, SilType?> _types = [];

    SilGen(Compilation compilation) {
        _compilation = compilation;
        _module.UnspecialisedGenerics = _generics == GenericsMode.Unspecialised;

        // What the built-in types declare they conform to: their metadata's
        // conformance records, the same in every unit of a program.
        foreach (var primitive in (NamedTypeSymbol[]) [..Core.Numerics, Core.Bool, Core.Char, Core.String]) {
            if (Lower(primitive) is { } lowered) {
                _module.PrimitiveConformances[lowered.ToString()] = [..DeclaredProtocols(primitive)];
            }
        }
    }

    CoreTypes Core => _compilation.Core;

    public IReadOnlyList<Diagnostic> Diagnostics => [.._diagnostics];

    /// <summary>
    ///     The file each diagnostic is in, where lowering knows it: everything
    ///     reported while a function's body is lowered or checked is that
    ///     function's file's. Compared by reference, as two identical
    ///     complaints in two files are two diagnostics.
    /// </summary>
    readonly Dictionary<Diagnostic, SyntaxTree> _files = new(ReferenceEqualityComparer.Instance);

    IReadOnlyList<(Diagnostic Diagnostic, SyntaxTree? Tree)> PlacedDiagnostics =>
        [.._diagnostics.Select(d => (d, _files.GetValueOrDefault(d)))];

    /// <summary>Gives the diagnostics reported since <paramref name="before" /> the file they were reported in, unless they have one.</summary>
    void Place(int before, SyntaxTree tree) {
        foreach (var diagnostic in _diagnostics.Skip(before)) {
            _files.TryAdd(diagnostic, tree);
        }
    }

    SyntaxTree? TreeOf(SourceText source) => _compilation.SyntaxTrees.FirstOrDefault(t => ReferenceEquals(t.Text, source));

    /// <summary>Lowers every function the compilation declares, or reports why it cannot.</summary>
    public static (SilModule Module, IReadOnlyList<Diagnostic> Diagnostics) Lower(Compilation compilation) {
        var generator = new SilGen(compilation);
        generator.LowerAll();
        return (generator._module, generator.Diagnostics);
    }

    /// <summary>
    ///     <see cref="Lower(Compilation)" />, with the file each diagnostic is
    ///     in — null for one lowering cannot place — so that a program of many
    ///     files can say where.
    /// </summary>
    /// <param name="denyReflection">Built with <c>--deny-reflection</c>: <c>Mirror</c> and <c>TypeInfo</c> are refused (S3450).</param>
    /// <param name="onDemand">
    ///     The files whose functions are lowered only once something lowered
    ///     calls them — the standard library's, once it is known to check
    ///     cleanly with the program (<see cref="Compilation.ReachesIntoLibrary" />):
    ///     a small program then binds and lowers a few of its files, not all
    ///     of them. Everything else is lowered, reached or not, so that what
    ///     lowering says of the program's own code is said. Null for none.
    /// </param>
    public static (SilModule Module, IReadOnlyList<(Diagnostic Diagnostic, SyntaxTree? Tree)> Diagnostics) LowerByFile(Compilation compilation, bool denyReflection = false, Func<SyntaxTree, bool>? onDemand = null) {
        var generator = new SilGen(compilation) { _denyReflection = denyReflection, _onDemand = onDemand };
        generator._module.DenyReflection = denyReflection;
        generator.LowerAll();
        return (generator._module, generator.PlacedDiagnostics);
    }

    /// <summary>
    ///     The checks that run on SIL — moves (S3057–S3059), exclusivity
    ///     (S3062) and roots (S3060, S3061) — for <c>sable check</c> and the
    ///     editor, which do not otherwise lower: the program is lowered and
    ///     thrown away, and what lowering says of the program is kept, with
    ///     the file it is in.
    /// </summary>
    /// <remarks>
    ///     A construct lowering does not handle yet (S7000) is the compiler's
    ///     limit, not the program's mistake, and is left to a build to report.
    ///     Lowering is only asked of a program that type-checks: a bound tree
    ///     with errors in it is not one it can take.
    /// </remarks>
    /// <param name="onDemand">The files whose functions are lowered only where reached (<see cref="LowerByFile" />): a library known to check cleanly.</param>
    public static IReadOnlyList<(Diagnostic Diagnostic, SyntaxTree? Tree)> Check(Compilation compilation, Func<SyntaxTree, bool>? onDemand = null) {
        var generator = new SilGen(compilation) { _onDemand = onDemand };
        generator.LowerAll();
        return [..generator.PlacedDiagnostics.Where(static d => d.Diagnostic.Code != ErrorCode.ERR_NotLoweredYet)];
    }

    /// <summary>
    ///     Lowers every function, starting the program at the one given
    ///     rather than at '@main': what a test or bench build does, whose
    ///     harness is the entry point of a program that may have its own.
    /// </summary>
    /// <param name="markDecisions">Marks the source's decisions and their outcomes, for branch coverage (<see cref="SilDecision" />).</param>
    /// <param name="onDemand">The files whose functions are lowered only where reached (<see cref="LowerByFile" />).</param>
    public static (SilModule Module, IReadOnlyList<Diagnostic> Diagnostics) Lower(Compilation compilation, FunctionSymbol entryPoint, bool markDecisions = false, Func<SyntaxTree, bool>? onDemand = null) {
        var generator = new SilGen(compilation) { _entryOverride = entryPoint, _markDecisions = markDecisions, _onDemand = onDemand };
        generator.LowerAll();
        return (generator._module, generator.Diagnostics);
    }

    FunctionSymbol? _entryOverride;

    /// <summary>
    ///     Lowers a library (docs/05 §5.2): a program with no entry point,
    ///     which C calls into through its <c>@export</c> functions. Its globals
    ///     are still given their initial values before anything runs — by
    ///     the initialiser the module then has, which the code generator
    ///     makes a constructor of the library (<see cref="SilModule.IsLibrary" />).
    /// </summary>
    public static (SilModule Module, IReadOnlyList<(Diagnostic Diagnostic, SyntaxTree? Tree)> Diagnostics) LowerLibraryByFile(Compilation compilation, bool denyReflection = false) {
        var generator = new SilGen(compilation) { _library = true, _denyReflection = denyReflection };
        generator._module.IsLibrary = true;
        generator._module.DenyReflection = denyReflection;
        generator.LowerAll();
        return (generator._module, generator.PlacedDiagnostics);
    }

    bool _library;

    bool _denyReflection;

    /// <summary>
    ///     Lowers a program for a session that outlives it, as a REPL's
    ///     entries do: each global gets an initialiser of its own,
    ///     <c>$global.init.&lt;name&gt;</c>, which the interpreter runs when the
    ///     global is first reached (<see cref="SilInterpreter.Session" />), and
    ///     the entry point neither initialises nor destroys the globals. A
    ///     global an earlier program of the session set up keeps its value,
    ///     and its initialiser is not run again.
    /// </summary>
    public static (SilModule Module, IReadOnlyList<Diagnostic> Diagnostics) LowerForSession(Compilation compilation) {
        var generator = new SilGen(compilation) { _lazyGlobals = true };
        generator.LowerAll();
        return (generator._module, generator.Diagnostics);
    }

    bool _lazyGlobals;

    /// <summary>The prefix of a global's own initialiser, in a module lowered for a session.</summary>
    internal const string GlobalInitPrefix = "$global.init.";

    /// <summary>Whether a function sets up globals — all of them, or a session's one — rather than being the entry point it is lowered as.</summary>
    static bool IsGlobalInit(string function) => function == GlobalsInit || function.StartsWith(GlobalInitPrefix, StringComparison.Ordinal);

    /// <summary>
    ///     Lowers one unit of a program compiled separately: the functions of
    ///     the files it owns, and whatever instances of generic functions they
    ///     use; the program's other functions are only declared. The module's
    ///     <see cref="SilModule.Unit" /> says which is which.
    /// </summary>
    /// <param name="program">The program's declarations, gathered once for all its units; gathered here when not given.</param>
    /// <param name="library">
    ///     The unit leads a library C links (<see cref="LowerLibraryByFile" />):
    ///     it has no entry point, and in its place sets up every global of the
    ///     program and makes the library's constructor, as the unit the program
    ///     starts in otherwise does.
    /// </param>
    public static (SilModule Module, IReadOnlyList<Diagnostic> Diagnostics) LowerUnit(
        Compilation compilation,
        SilUnit unit,
        SilProgram? program = null,
        FunctionSymbol? entryPoint = null,
        bool denyReflection = false,
        bool library = false
    ) {
        var generator = new SilGen(compilation) { _entryOverride = entryPoint, _unit = unit, _program = program, _denyReflection = denyReflection, _library = library };
        generator._module.Unit = new();
        generator._module.IsLibrary = library;
        generator._module.DenyReflection = denyReflection;
        generator.LowerAll();
        return (generator._module, generator.Diagnostics);
    }

    /// <summary>
    ///     <see cref="LowerUnit" />, with the file each diagnostic is in where
    ///     lowering knows it, as <see cref="LowerByFile" /> gives them: what a
    ///     program compiled against its libraries' interfaces reports.
    /// </summary>
    public static (SilModule Module, IReadOnlyList<(Diagnostic Diagnostic, SyntaxTree? Tree)> Diagnostics) LowerUnitByFile(
        Compilation compilation,
        SilUnit unit,
        SilProgram? program = null,
        bool denyReflection = false
    ) {
        var generator = new SilGen(compilation) { _unit = unit, _program = program, _denyReflection = denyReflection };
        generator._module.Unit = new();
        generator._module.DenyReflection = denyReflection;
        generator.LowerAll();
        return (generator._module, generator.PlacedDiagnostics);
    }

    SilUnit? _unit;
    SilProgram? _program;

    /// <summary>The files whose functions are lowered only where reached (<see cref="LowerByFile" />); null for none.</summary>
    Func<SyntaxTree, bool>? _onDemand;

    /// <summary>The functions of <see cref="_onDemand" />'s files not reached yet, by name.</summary>
    readonly Dictionary<string, (FunctionSymbol Symbol, SyntaxNode Syntax, SemanticModel Model, FunctionType Type)> _deferred = new(StringComparer.Ordinal);

    /// <summary>
    ///     Whether a file's bodies are bound to lower it: a unit's own, and
    ///     not one lowered on demand, whose accessors are named from their
    ///     declarations alone (binding later gives the same symbols).
    /// </summary>
    bool Binds(SyntaxTree tree) => (_unit is null || _unit.Owns(tree)) && _onDemand?.Invoke(tree) != true;

    /// <summary>
    ///     For compile-time evaluation's generator, the files the lowering
    ///     that asked for it does not check (<see cref="BodyModel" />).
    /// </summary>
    Func<SyntaxTree, bool>? _unchecked;

    /// <summary>Whether a file's bodies are bound only as lowering needs them, one member at a time (<see cref="BodyModel" />).</summary>
    bool Unchecked(SyntaxTree tree) => _evaluation is not null ? _unchecked?.Invoke(tree) == true : !Binds(tree);

    /// <summary>
    ///     The model to bind <paramref name="syntax" /> with. A file this
    ///     lowering does not check — another unit's, or a library's lowered on
    ///     demand — has only the bodies lowering reaches bound, each by a
    ///     model of the member it is in (<see cref="SemanticModel.GetMemberModel" />),
    ///     rather than every body of the file: a program that instantiates
    ///     one generic method of <c>Dictionary</c> binds that method, not the
    ///     whole of <c>Dictionary.sb</c>. A file whose bodies are bound
    ///     already is read from as it is.
    /// </summary>
    SemanticModel BodyModel(SemanticModel model, SyntaxNode syntax) {
        if (model.Member is not null || model.BodiesBound || !Unchecked(model.SyntaxTree)) {
            return model;
        }

        SyntaxNode? member = null;
        for (var node = syntax; node is not null; node = node.Parent) {
            if (SemanticModel.IsBindableMember(node)) {
                member = node;
            }
        }

        return member is null ? model : model.GetMemberModel(member);
    }

    /// <summary>
    ///     A method of a class or an actor, or of an extension of one: its
    ///     vtable is built from the methods lowered by then, so these are
    ///     never left for later.
    /// </summary>
    static bool DispatchesFrom(FunctionSymbol function) {
        for (var symbol = function.ContainingSymbol; symbol is not null; symbol = symbol.ContainingSymbol) {
            switch (symbol) {
                case ExtensionSymbol extension:
                    return extension.ExtendedType.TypeKind is TypeKind.Class or TypeKind.Actor;
                case NamedTypeSymbol type:
                    return type.TypeKind is TypeKind.Class or TypeKind.Actor;
            }
        }

        return false;
    }

    /// <summary>
    ///     The instances of generic functions asked for, and the functions
    ///     left for later (<see cref="_onDemand" />) that what is lowered
    ///     reaches — through a reference, or as the deinit of a type it
    ///     uses — until nothing more is reached. What the code generator
    ///     and the interpreter can reach (<see cref="SilPassManager.Reachable" />)
    ///     is then all there.
    /// </summary>
    void LowerReached() {
        LowerPendingInstances();
        while (_deferred.Count > 0) {
            var reached = new List<string>();
            foreach (var function in _module.Functions) {
                foreach (var instruction in function.Blocks.SelectMany(static b => b.Instructions)) {
                    if (instruction is FunctionRefInst reference && _deferred.ContainsKey(reference.Function)) {
                        reached.Add(reference.Function);
                    }
                }
            }

            foreach (var type in _module.Types) {
                if (type switch { StructType s => s.Deinit, ClassType c => c.Deinit, _ => null } is { } deinit && _deferred.ContainsKey(deinit)) {
                    reached.Add(deinit);
                }
            }

            if (reached.Count == 0) {
                return;
            }

            foreach (var name in reached) {
                if (_deferred.Remove(name, out var function)) {
                    LowerBody(function.Symbol, function.Syntax, function.Model, name, function.Type, TypeSubstitution.Empty);
                }
            }

            LowerPendingInstances();
        }
    }

    /// <summary>
    ///     A function lowering may lower, where it is declared: its syntax, read
    ///     from a library's file when that is first asked for when the library's
    ///     declarations came from its interface.
    /// </summary>
    readonly struct FunctionSource(FunctionSymbol symbol, SyntaxNode? declared, SemanticModel model) {
        public FunctionSymbol Symbol { get; } = symbol;
        public SyntaxNode Syntax => declared ?? Symbol.DeclaringSyntax[0];

        /// <summary>The syntax where it is known without reading a library's file for it, which a place to report something of the library's is not worth.</summary>
        public SyntaxNode SyntaxOr(SyntaxNode other) => declared ?? other;

        public SemanticModel Model { get; } = model;

        public void Deconstruct(out SyntaxNode syntax, out SemanticModel model) => (syntax, model) = (Syntax, Model);
    }

    /// <summary>
    ///     Whether a file's declarations are listed from a library's interface
    ///     the compilation took them from: those of a file whose functions this
    ///     lowering only declares. One it lowers has what is declared in its
    ///     bodies too, which only its syntax says.
    /// </summary>
    bool TakesInterface(SyntaxTree tree) =>
        _compilation.IsFromInterface(tree) && (_evaluation is not null ? _unchecked?.Invoke(tree) == true : _unit is not null && !_unit.Owns(tree));

    /// <summary>
    ///     The functions of a library's file whose declarations came from its
    ///     interface: those its outline has (<see cref="SilProgram" />), in the
    ///     same order, without reading the file.
    /// </summary>
    void AddFromInterface(SyntaxTree tree, SemanticModel model, List<FunctionSource> functions) {
        var declarations = _compilation.InterfaceDeclarations(tree);
        foreach (var declared in declarations) {
            if (declared is FunctionSymbol { FunctionKind: FunctionKind.Ordinary or FunctionKind.Initializer or FunctionKind.Deinitializer or FunctionKind.Operator } symbol
                && IsLowerable(symbol)) {
                functions.Add(new(symbol, null, model));
            }
        }

        foreach (var field in declarations.OfType<FieldSymbol>()) {
            foreach (var (accessor, isRequirement) in _compilation.InterfaceAccessors(field)) {
                if (isRequirement) {
                    if (accessor.IsMutating) {
                        _setters[field] = accessor;
                    }
                } else if (IsLowerable(accessor)) {
                    (accessor.IsMutating ? _setters : _getters)[field] = accessor;
                    functions.Add(new(accessor, null, model));
                }
            }
        }

        foreach (var subscript in declarations.OfType<FunctionSymbol>()) {
            if (subscript is not { FunctionKind: FunctionKind.Subscript, IsStatic: false } || !IsLowerable(subscript)) {
                continue;
            }

            var accessors = _compilation.InterfaceAccessors(subscript).ToList();
            if (subscript.IsExpressionBodied) {
                _subscriptGetters[subscript] = subscript;
                functions.Add(new(subscript, null, model));
                continue;
            }

            foreach (var (accessor, isRequirement) in accessors) {
                if (isRequirement) {
                    if (accessor.IsMutating) {
                        _subscriptSetters[subscript] = accessor;
                    }
                } else {
                    (accessor.IsMutating ? _subscriptSetters : _subscriptGetters)[subscript] = accessor;
                    functions.Add(new(accessor, null, model));
                }
            }
        }
    }

    /// <summary>A file's declarations that lowering looks at — and perhaps other nodes, which each use filters out.</summary>
    IEnumerable<SyntaxNode> Declarations(SyntaxTree tree) =>
        _program?.Declarations(tree, _evaluation is not null ? !(Unchecked(tree) && CoreLibrary.IsStandardLibrary(tree.Header.Module)) : _unit is null || _unit.Owns(tree))
        ?? tree.Root.DescendantNodes();

    void LowerAll() {
        // Constants are computed, and comptime blocks run, before anything
        // is lowered: lowering writes each constant's value where it is read
        // (SilGen.Constants.cs). Each unit reports what is in its own files.
        if (_evaluation is null) {
            // What this lowering does not check, without this generator.
            var (unit, onDemand) = (_unit, _onDemand);
            _constants = ConstantsOf(_compilation, tree => unit is not null && !unit.Owns(tree) || onDemand?.Invoke(tree) == true, _program);
            foreach (var (diagnostic, tree) in _constants.Diagnostics) {
                if (diagnostic.Code is ErrorCode.ERR_ConstantEvaluationFailed or ErrorCode.ERR_ConstantOverflow or ErrorCode.ERR_CompileError && (_unit is null || _unit.Owns(tree))) {
                    _diagnostics.Add(diagnostic);
                }
            }
        }

        var functions = new List<FunctionSource>();

        foreach (var tree in _compilation.SyntaxTrees) {
            var model = _compilation.GetSemanticModel(tree);
            if (TakesInterface(tree)) {
                AddFromInterface(tree, model, functions);
                continue;
            }

            foreach (var declaration in Declarations(tree)) {
                if (declaration is not (FunctionDeclarationSyntax or InitDeclarationSyntax or DeinitDeclarationSyntax or OperatorDeclarationSyntax)) {
                    continue;
                }

                if (_compilation.GetDeclaredSymbol(declaration) is not FunctionSymbol symbol || !IsLowerable(symbol)) {
                    continue;
                }

                functions.Add(new(symbol, declaration, model));
            }

            // A computed property's getter and setter: methods of the type,
            // named for the property, that reading and writing it call.
            // An observed property's 'willSet' and 'didSet' are methods too,
            // which the setter the compiler writes for it calls.
            foreach (var accessor in Declarations(tree).OfType<AccessorSyntax>()) {
                // A protocol's settable property's setter: what a witness table's entry for it is (#297), with no body to lower.
                if (accessor.Keyword.Kind == SyntaxKind.SetKeyword && model.GetRequirementAccessor(accessor) is { ContainingSymbol: FieldSymbol required } requirementSetter) {
                    _setters[required] = requirementSetter;
                    continue;
                }

                if (accessor.Keyword.Kind is not (SyntaxKind.GetKeyword or SyntaxKind.SetKeyword or SyntaxKind.WillSetKeyword or SyntaxKind.DidSetKeyword)
                    || (Unchecked(tree) ? model.GetAccessorDeclaration(accessor) : model.GetAccessorSymbol(accessor))
                        is not { ContainingSymbol: FieldSymbol field } function
                    || !IsLowerable(function)) {
                    continue;
                }

                if (accessor.Keyword.Kind is SyntaxKind.GetKeyword or SyntaxKind.SetKeyword) {
                    (accessor.Keyword.Kind == SyntaxKind.GetKeyword ? _getters : _setters)[field] = function;
                } else if (!_compilation.ObservedProperties.ContainsKey(field)) {
                    continue;
                }

                functions.Add(new(function, accessor, model));
            }

            // A subscript: its getter and setter, methods of the type taking
            // the indices — and the setter the new value after them — that
            // indexing calls. One written '=> e' is its own getter.
            foreach (var declaration in Declarations(tree).OfType<SubscriptDeclarationSyntax>()) {
                if (_compilation.GetDeclaredSymbol(declaration) is not FunctionSymbol { FunctionKind: FunctionKind.Subscript, IsStatic: false } subscript
                    || !IsLowerable(subscript)) {
                    continue;
                }

                if (declaration.ExpressionBody is not null) {
                    _subscriptGetters[subscript] = subscript;
                    functions.Add(new(subscript, declaration, model));
                    continue;
                }

                foreach (var accessor in declaration.Accessors?.Accessors ?? []) {
                    // A protocol's subscript's setter, as a property's.
                    if (accessor.Keyword.Kind == SyntaxKind.SetKeyword && model.GetRequirementAccessor(accessor) is { } requiredSetter) {
                        _subscriptSetters[subscript] = requiredSetter;
                        continue;
                    }

                    if (accessor.Keyword.Kind is not (SyntaxKind.GetKeyword or SyntaxKind.SetKeyword)
                        || (Unchecked(tree) ? model.GetAccessorDeclaration(accessor) : model.GetAccessorSymbol(accessor))
                            is not { } function) {
                        continue;
                    }

                    (accessor.Keyword.Kind == SyntaxKind.GetKeyword ? _subscriptGetters : _subscriptSetters)[subscript] = function;
                    functions.Add(new(function, accessor, model));
                }
            }
        }

        AddDerived(functions);
        AddLazy(functions);
        AddInheritedInitializers(functions);
        AddGenerators(functions);
        CheckBuiltins();

        // Overloads: functions of one name in one container (counted once per
        // pair, not by a scan per function — a large program has thousands).
        // The container is compared by reference, as a symbol is itself.
        var none = new object();
        var sharing = new Dictionary<object, Dictionary<string, int>>(ReferenceEqualityComparer.Instance);
        foreach (var symbol in functions.Select(static f => f.Symbol)) {
            var container = (object?)symbol.ContainingSymbol ?? none;
            if (!sharing.TryGetValue(container, out var names)) {
                sharing[container] = names = new(StringComparer.Ordinal);
            }

            names[symbol.Name] = names.GetValueOrDefault(symbol.Name) + 1;
        }

        foreach (var symbol in functions.Select(static f => f.Symbol)) {
            var overloaded = sharing[(object?)symbol.ContainingSymbol ?? none][symbol.Name] > 1;
            _names[symbol] = Mangle(symbol, overloaded);
        }

        // Overloads with the same parameter types differ in their labels,
        // 'g(a:)' and 'g(b:)': only those get the labels in their names too.
        // A pair that still shares a name is one the binder has reported.
        var lowered = functions.Select(static f => f.Symbol).Distinct().ToList();
        foreach (var clash in lowered.GroupBy(s => _names[s], StringComparer.Ordinal).Where(static g => g.Count() > 1)) {
            foreach (var symbol in clash) {
                _names[symbol] += "$by$" + string.Join("$", symbol.Parameters.Select(static p => Sanitise(p.Label ?? p.Name)));
            }
        }

        foreach (var clash in lowered.GroupBy(s => _names[s], StringComparer.Ordinal).Where(static g => g.Count() > 1)) {
            foreach (var (symbol, i) in clash.Skip(1).Select(static (s, i) => (s, i + 1))) {
                _names[symbol] += $"${i}";
            }
        }

        // Which classes something inherits from: their methods dispatch
        // (and, compiled separately, those anything may: IsInherited).
        foreach (var tree in _compilation.SyntaxTrees) {
            var classes = TakesInterface(tree)
                ? _compilation.InterfaceDeclarations(tree).Where(static s => s is NamedTypeSymbol { TypeKind: TypeKind.Class })
                : Declarations(tree).OfType<ClassDeclarationSyntax>().Select(_compilation.GetDeclaredSymbol);
            foreach (var declared in classes) {
                if (declared is NamedTypeSymbol type && SuperclassOf(type) is { } superclass) {
                    for (var ancestor = (NamedTypeSymbol?)superclass; ancestor is not null; ancestor = SuperclassOf(ancestor)) {
                        _inherited.Add(ancestor.OriginalDefinition);
                    }
                }
            }
        }

        // '@main' marks where a program starts; failing that, a function
        // called main does. A library starts nowhere: a 'main' in it is an
        // ordinary function.
        var entry = _library ? null : _entryOverride ?? functions.FirstOrDefault(static f =>
                f.Symbol.FunctionKind == FunctionKind.Ordinary && f.Symbol.GetAttribute("main") is { FromExtension: false }
            ).Symbol
            ?? functions.FirstOrDefault(static f => f.Symbol is { Name: "main", ContainingSymbol: ModuleSymbol }).Symbol;

        foreach (var function in functions) {
            _declarations[function.Symbol] = function;
        }

        // Separately, the unit the program starts in sets up every global;
        // another makes storage only for those its code names.
        DeclareGlobals(all: _unit is null || _evaluation is not null || _library
            || entry is not null && _declarations.TryGetValue(entry, out var start) && _unit.Owns(start.Model.SyntaxTree));
        _entrySymbol = entry;

        // Compile-time evaluation lowers what it runs, as it needs it.
        if (_evaluation is not null) {
            return;
        }

        // What is not generic is lowered as it is; what is, once for each set
        // of type arguments something calls it with.
        foreach (var source in functions) {
            // A Sable.Core '@intrinsic' has the body each use is lowered to.
            var (symbol, model) = (source.Symbol, source.Model);
            if (IsGeneric(symbol) || IsIntrinsic(symbol)) {
                continue;
            }

            // '@extern': C's function, declared in every unit that may call it;
            // a variadic one for each call's types, where it is called.
            if (symbol.ForeignName is { } foreign) {
                if (symbol.Parameters is [.., { IsVariadic: true }]) {
                    continue;
                }

                if (SignatureOf(symbol, source.Syntax, TypeSubstitution.Empty) is { } signature) {
                    _module.Foreign[_names[symbol]] = (foreign, signature);
                }

                continue;
            }

            if (_unit is not null && !_unit.Owns(model.SyntaxTree)) {
                DeclareExternal(source, _names[symbol]);
                continue;
            }

            var syntax = source.Syntax;

            if (SignatureOf(symbol, syntax, TypeSubstitution.Empty) is not { } type) {
                continue;
            }

            if (ReferenceEquals(symbol, entry)) {
                _module.EntryPoint = _names[symbol];
            }

            // '@export': the name C calls it by.
            if (symbol.ExportName is { } export) {
                _module.Exports[_names[symbol]] = export;
            }

            // Lowered when something reaches it, if ever.
            if (_onDemand is not null && _onDemand(model.SyntaxTree) && !ReferenceEquals(symbol, entry) && symbol.ExportName is null && !DispatchesFrom(symbol)) {
                _deferred[_names[symbol]] = (symbol, syntax, model, type);
                continue;
            }

            var before = _module.Functions.Count;
            LowerBody(symbol, syntax, model, _names[symbol], type, TypeSubstitution.Empty);
            if (_module.Unit is { } info) {
                // The function, its closures and an initialiser's two halves
                // are this unit's alone; a thunk or a reflection function made
                // on the way may be made by another unit too.
                var own = _names[symbol];
                foreach (var made in _module.Functions.Skip(before)) {
                    if (made.Name == own || made.Name.StartsWith(own + "$", StringComparison.Ordinal) && !made.Name.EndsWith("$thunk", StringComparison.Ordinal)) {
                        info.Owned.Add(made.Name);
                    }
                }
            }
        }

        // A library kept as an archive compiles each of its generic functions
        // that can be compiled once, for the units linked with it to call
        // rather than lower again (#297 stage 7).
        if (_unit is { CompilesGenerics: true } && _generics == GenericsMode.Unspecialised) {
            foreach (var source in functions) {
                var symbol = source.Symbol;
                if (!IsGeneric(symbol) || IsIntrinsic(symbol) || symbol.ForeignName is not null || !_unit.Owns(source.Model.SyntaxTree)
                    || !_declarations.ContainsKey(symbol) || InsideBody(source.Syntax)) {
                    continue;
                }

                var syntax = source.Syntax;

                if (UnspecialisedShape(symbol) is { } shape) {
                    UnspecialisedInstance(symbol, shape, syntax);
                } else if (HierarchiesOnce) {
                    Prepare(symbol, syntax);
                }
            }
        }

        LowerReached();

        // Separately, the globals are set up by the unit the program starts
        // in. Their initial values may ask for instances nothing else did.
        if (_unit is null || _module.EntryPoint is not null || _library) {
            LowerGlobalLifetimes();
            LowerReached();
        }

        // The tables an instance of a generic type made at run time has: once the instances are all known.
        TemplateTables();
        LowerReached();

        BuildVTables();
        SharedStorage.InsertChecks(_module);
        RootChecker.Check(_module, _sources, _diagnostics, (before, source) => {
            if (TreeOf(source) is { } tree) {
                Place(before, tree);
            }
        });
        if (_module.Unit is { } unitInfo) {
            RecordGenerics(unitInfo);
            DeclareReferenced(unitInfo);
            RecordAbi(unitInfo);
        }
    }

    /// <summary>
    ///     A function a library keeps copied for each type because it works on
    ///     vectors of them, whose layout is the point: made here for every
    ///     type there is one of, each SIMD scalar, as no other type can be
    ///     given to it (<see cref="SilUnitInfo.Prepared" />).
    /// </summary>
    void Prepare(FunctionSymbol symbol, SyntaxNode at) {
        var parameters = TypeParametersOf(symbol).ToList();
        if (!symbol.HasBody || _module.Unit is not { } info || parameters.Count != 1 || parameters[0] is not { IsValue: false } parameter
            || parameter.Constraints.SelectMany(static c => CompositionTypeSymbol.PartsOf(c)).OfType<NamedTypeSymbol>().FirstOrDefault(static c => c is { Name: "SIMDScalar", ContainingSymbol: ModuleSymbol { Name: "Sable.Core" } }) is not { } scalar) {
            return;
        }

        foreach (var conformer in ConformingTypes(scalar)) {
            if (parameter.Constraints.All(c => CompositionTypeSymbol.PartsOf(c).All(p => conformer.Satisfies(p)))
                && Instance(symbol, new TypeSubstitution(new Dictionary<TypeParameterSymbol, TypeSymbol> { [parameter] = conformer }), at) is var (name, _)) {
                info.Prepared.Add(name);
                info.Owned.Add(name);
            }
        }
    }

    /// <summary>
    ///     For a library's unit, the generic functions of its files it
    ///     compiled once: each defined here for every unit linked with it,
    ///     and recorded for its interface (<see cref="SilUnitInfo.Generics" />).
    /// </summary>
    void RecordGenerics(SilUnitInfo info) {
        if (_unit is not { CompilesGenerics: true } unit) {
            return;
        }

        foreach (var (name, (symbol, _)) in _unspecialised) {
            if (_names.TryGetValue(symbol, out var baseName) && name == baseName + "$generic" && _declarations.TryGetValue(symbol, out var declared)
                && unit.Owns(declared.Model.SyntaxTree) && _module.Find(name) is { } function) {
                info.Owned.Add(name);
                info.Generics[baseName] = DescribesValues(symbol);
                if (DebugDescribesValues(symbol)) {
                    info.DebugDescribing.Add(baseName);
                }
                if (IsInlinable(symbol, function)) {
                    info.Inlinable.Add(baseName);
                }
            }
        }

        // A copy is made whole or not at all: what an inlinable function
        // calls of the generic code compiled once — directly, or through a
        // closure or a thunk of its — is inlinable too, whatever its size.
        var pending = new Stack<string>(info.Inlinable.Select(static n => n + "$generic"));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryPop(out var name)) {
            if (!seen.Add(name) || _module.Find(name) is not { } function) {
                continue;
            }

            foreach (var reference in function.Blocks.SelectMany(static b => b.Instructions).OfType<FunctionRefInst>()) {
                var callee = reference.Function;
                if (callee.EndsWith("$generic", StringComparison.Ordinal) && info.Generics.ContainsKey(callee[..^"$generic".Length])) {
                    info.Inlinable.Add(callee[..^"$generic".Length]);
                    pending.Push(callee);
                } else if (!info.Owned.Contains(callee)) {
                    pending.Push(callee);
                }
            }
        }
    }

    /// <summary>
    ///     Functions other units define, by name, with how to work out the
    ///     signature each will have there — worked out only for those this
    ///     unit refers to, since a large program has thousands.
    /// </summary>
    readonly Dictionary<string, Func<FunctionType?>> _external = new(StringComparer.Ordinal);

    readonly Dictionary<string, FunctionType?> _externalTypes = new(StringComparer.Ordinal);

    /// <summary>The signature of a function another unit defines, or null when there is none to call.</summary>
    FunctionType? External(string name) {
        if (_externalTypes.TryGetValue(name, out var known)) {
            return known;
        }

        // Not declared yet, perhaps: main may ask for a library function
        // before the library's declarations are gone through.
        if (!_external.TryGetValue(name, out var signature)) {
            return null;
        }

        var type = signature();
        _externalTypes[name] = type;
        return type;
    }

    /// <summary>
    ///     A function another unit lowers, declared here as that unit will
    ///     define it — with the side effects lowering it would have had on the
    ///     types (a deinit), so that every unit lays them out alike.
    /// </summary>
    void DeclareExternal(FunctionSource source, string name) {
        var symbol = source.Symbol;

        // LowerBody makes nothing of a declaration without a body.
        if (!symbol.HasBody && !(_compilation.IsFromInterface(source.Model.SyntaxTree)
                ? _compilation.GetDerivation(symbol) is not null || _compilation.InheritedInitializers.ContainsKey(symbol)
                : _compilation.GetDerivedBody(symbol) is not null || _compilation.GetLazyBody(symbol) is not null
                    || _compilation.GetInheritedInitializerBody(symbol) is not null)) {
            return;
        }

        // As the first loop would: no signature, nothing lowered.
        FunctionType? Signature() => SignatureOf(symbol, source.SyntaxOr(_compilation.SyntaxTrees[0].Root), TypeSubstitution.Empty);
        _external[name] = Signature;

        // A class's initialiser is two functions: the allocator, and the half that initialises.
        if (symbol.FunctionKind == FunctionKind.Initializer) {
            _external[InitializerName(name)] = () => External(name) is { Result: ClassType } allocator ? InitializerType(allocator) : null;
        }

        if (symbol.FunctionKind == FunctionKind.Deinitializer && External(name) is not null) {
            switch (Lower(SelfType(symbol, TypeSubstitution.Empty))) {
                case ClassType owner:
                    owner.Deinit = name;
                    break;
                case StructType owner:
                    owner.Deinit = name;
                    break;
            }
        }
    }

    /// <summary>
    ///     Records the signature of every function of another unit this one
    ///     refers to — by a call, a reference, a vtable or a deinit — for the
    ///     verifier and the code generator.
    /// </summary>
    void DeclareReferenced(SilUnitInfo info) {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var function in _module.Functions) {
            foreach (var block in function.Blocks) {
                foreach (var instruction in block.Instructions) {
                    if (instruction is FunctionRefInst reference) {
                        referenced.Add(reference.Function);
                    }
                }
            }
        }

        foreach (var type in _classes.Keys) {
            referenced.UnionWith(type.VTable.Select(static m => m.Function));
            if (type.Deinit is { } deinit) {
                referenced.Add(deinit);
            }
        }

        foreach (var type in _module.Types.OfType<StructType>()) {
            if (type.Deinit is { } deinit) {
                referenced.Add(deinit);
            }
        }

        // A table's witnesses, which the specialisation pass calls directly:
        // a library's generic code compiled once among them (#297 stage 7).
        foreach (var table in _module.WitnessTables) {
            referenced.UnionWith(table.Entries.Select(static e => e.Function).OfType<string>());
        }

        foreach (var name in referenced) {
            if (_module.Find(name) is null && External(name) is { } type) {
                info.External[name] = type;
            }
        }
    }

    /// <summary>What this unit took the program's classes and structs to be: every unit must agree.</summary>
    void RecordAbi(SilUnitInfo info) {
        static string Fields(IEnumerable<(string Name, SilType Type)> fields) => string.Join(",", fields.Select(static f => $"{f.Name}:{f.Type}"));
        foreach (var type in _classes.Keys) {
            info.Abi["class " + type.Name] = $"super={type.Superclass?.Name} deinit={type.Deinit} fields={Fields(type.Fields)} vtable={string.Join(",", type.VTable.Select(static m => $"{m.Method}={m.Function}"))}";
        }

        // A struct's fields come from its declaration, which every unit
        // sees; its deinit is what lowering decides.
        foreach (var type in _module.Types.OfType<StructType>().Where(static t => t.Deinit is not null)) {
            info.Abi["struct " + type.Name] = $"deinit={type.Deinit} fields={Fields(type.Fields)}";
        }
    }

    // ---- Globals --------------------------------------------------------------

    /// <summary>Module-level variables and types' static stored properties: one storage each, for the whole program.</summary>
    readonly List<(FieldSymbol Field, string Name, SemanticModel Model)> _globalFields = [];
    readonly Dictionary<FieldSymbol, (string Name, SilType Type)> _globalsByField = [];
    FunctionSymbol? _entrySymbol;

    internal const string GlobalsInit = "$globals.init", GlobalsDeinit = "$globals.deinit";

    internal static bool IsGlobal(FieldSymbol field) =>
        !field.IsComputed && (field.IsStatic || field.ContainingSymbol is ModuleSymbol);

    /// <summary>
    ///     The globals of other units' files that this unit has not yet named,
    ///     each declared when its code first does (<see cref="GlobalOf" />).
    /// </summary>
    /// <remarks>
    ///     Each unit has the storage of every global it uses, and the linker
    ///     keeps one; one that made storage for every global of the program
    ///     would depend on the units that depend on it — a global they add or
    ///     retype would leave its object with storage of the wrong size, and
    ///     their types' layouts in its records (docs/05 §5.4).
    /// </remarks>
    readonly HashSet<FieldSymbol> _otherGlobals = [];

    /// <param name="all">
    ///     Declare every global of the program, as the whole program and the
    ///     unit the program starts in, which sets them all up, do; otherwise
    ///     only the unit's own, and the rest when named.
    /// </param>
    void DeclareGlobals(bool all) {
        foreach (var tree in _compilation.SyntaxTrees) {
            var model = _compilation.GetSemanticModel(tree);
            var fields = TakesInterface(tree)
                ? _compilation.InterfaceDeclarations(tree)
                : Declarations(tree).Where(static d => d is FieldDeclarationSyntax or ConstDeclarationSyntax).Select(_compilation.GetDeclaredSymbol);
            foreach (var declared in fields) {
                if (declared is not FieldSymbol field || !IsGlobal(field)
                    || field.ContainingSymbol is NamedTypeSymbol { IsGenericDefinition: true }) {
                    continue;
                }

                if (!all && !_unit!.Owns(tree)) {
                    _otherGlobals.Add(field);
                    continue;
                }

                if (DeclareGlobal(field) is { } name) {
                    _globalFields.Add((field, name, BodyModel(model, field.DeclaringSyntax[0])));
                }
            }
        }
    }

    /// <summary>A global's storage, named for its owner; null for one that needs none, or cannot have it here.</summary>
    string? DeclareGlobal(FieldSymbol field) {
        if (Lower(field.Type) is not { } type) {
            return null;
        }

        // A constant written where it is read needs no storage, unless C reads it.
        if (_evaluation is null && field.ExportName is null && (field.IsKnownLiteral || ConstantRead(field) is not null)) {
            return null;
        }

        // Separately, the unit the program starts in sets up every
        // global, and sees no other unit's bodies: a static of a type
        // declared in one is the whole program's to compile.
        if (_unit is not null && !_compilation.IsTakenFromInterface(field) && field.DeclaringSyntax[0] is var declaration && InsideBody(declaration)) {
            Unsupported<object>(declaration, "a static stored property of a type declared in a function, compiled separately");
            return null;
        }

        var owner = field.ContainingSymbol is NamedTypeSymbol named ? QualifiedName(named) : field.ContainingSymbol?.Name;
        var name = $"{owner}.{field.Name}";
        _globalsByField[field] = (name, type);
        _module.Globals.Add((name, type));

        // '@export var': C reads and writes the storage by its name, which
        // every unit having the global (its own unit always) calls it by.
        if (field.ExportName is { } export) {
            _module.Exports[name] = export;
        }

        return name;
    }

    /// <summary>
    ///     Lowers the instances of generic functions asked for so far. A
    ///     generic class's vtable names instances of its methods, which are
    ///     lowered in their turn — and may make more classes.
    /// </summary>
    void LowerPendingInstances() {
        do {
            while (_pending.Count > 0) {
                var (symbol, name, type, substitution) = _pending.Dequeue();
                if (symbol is { IsAbstract: true, FunctionKind: FunctionKind.Ordinary }) {
                    AbstractStub(symbol, name, type);
                } else if (_localGenerics.TryGetValue(symbol, out var local)) {
                    LowerLocalFunction(symbol, local.Body, name, type, local.Source, substitution, _localGenericClosures.GetValueOrDefault(name));
                } else if (!_unspecialised.ContainsKey(name) && DeinitThunk(symbol, name, type, substitution)) {
                    // An instance's deinit is the one compiled once.
                } else {
                    var (syntax, model) = _declarations[symbol];
                    _loweringInstance = name;

                    // Generic code this unit copies for types it knows: what compiling generics once leaves.
                    if (TracesGenerics && _generics == GenericsMode.Unspecialised && !_unspecialised.ContainsKey(name)) {
                        Console.Error.WriteLine($"instance {name} of {TraceName(symbol)}");
                    }

                    using (_unspecialised.TryGetValue(name, out var generic) ? Opaque(generic.Shape) : null)
                    using (generic.Shape is not null ? AssociatedContext(symbol) : null) {
                        LowerBody(symbol, syntax, model, name, type, substitution);
                        if (generic.Shape is { } shape && _module.Find(name) is { } unspecialised) {
                            AssociatedRecipes(unspecialised, symbol, shape);

                            // A class's initialiser's other half, which initialises the object.
                            if (_module.Find(InitializerName(name)) is { } initialize) {
                                AssociatedRecipes(initialize, symbol, shape);
                            }
                        }
                    }

                    _loweringInstance = null;
                }

                if (_module.Find(name) is { } lowered) {
                    lowered.DisplayName = InstanceDisplayName(symbol, _names[symbol], substitution);
                }
            }

            if (_pending.Count == 0) {
                BuildVTables();
            }
        } while (_pending.Count > 0);
    }

    static bool InsideBody(SyntaxNode node) {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent) {
            if (parent is DeclarationStatementSyntax) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     After an initialiser has stored the globals' values, marks every
    ///     object an '@immortal' one holds immortal: its count is never
    ///     touched again, it is never released, and what it keeps alive is
    ///     no leak (docs/02 §2.2). Done where the initialiser returns.
    /// </summary>
    void MakeImmortal(SilFunction init, IEnumerable<FieldSymbol> fields) {
        var immortal = fields.Where(static f => f.IsImmortal).Select(GlobalOf).OfType<(string Name, SilType Type)>().Where(static g => !g.Type.IsTrivial).ToList();
        if (immortal.Count == 0) {
            return;
        }

        foreach (var block in init.Blocks) {
            if (block.Instructions.Count == 0 || block.Instructions[^1] is not ReturnInst returned) {
                continue;
            }

            block.Instructions.RemoveAt(block.Instructions.Count - 1);
            foreach (var (name, type) in immortal) {
                var value = block.Append(new LoadBorrowInst(block.Append(new GlobalAddrInst(name, type))));
                block.Append(new BuiltinInst("make_immortal", SilType.Unit, value));
                block.Append(new EndBorrowInst(value));
            }

            block.Append(returned);
        }
    }

    internal (string Name, SilType Type)? GlobalOf(FieldSymbol field) {
        if (!_globalsByField.TryGetValue(field, out var global) && _otherGlobals.Remove(field)) {
            DeclareGlobal(field);
            _globalsByField.TryGetValue(field, out global);
        }

        return global.Name is null ? null : global;
    }

    /// <summary>
    ///     Two functions the entry point calls: one that stores each global's
    ///     initial value, each after those it reads (SilGen.Globals.cs), before
    ///     anything else runs; one that destroys them, last first, as the
    ///     program returns.
    /// </summary>
    void LowerGlobalLifetimes() {
        // A library has no entry point: its globals' initialiser is lowered
        // as a function of its own, which nothing in the library calls.
        var initSymbol = _entrySymbol ?? (_library && _globalFields.Count > 0
            ? FunctionSymbol.Synthesized(GlobalsInit, _globalFields[0].Field.ContainingSymbol!)
            : null);
        if (_globalFields.Count == 0 || initSymbol is null) {
            return;
        }

        var ordered = OrderGlobals();

        // Lowered in the entry point's context: a function of no parameters.
        var unit = new FunctionType([], SilType.Unit);
        var statements = new List<BoundStatement>();
        foreach (var (field, _, model) in ordered) {
            if (model.GetFieldInitializer(field) is { } initial) {
                var target = new BoundFieldAccess(initial.Syntax, null, field);
                statements.Add(new BoundExpressionStatement(initial.Syntax, new BoundAssignment(initial.Syntax, target, initial)));
            }
        }

        // A session's globals are set up one at a time, as each is reached,
        // and outlive the program: nothing destroys them at its end.
        if (_lazyGlobals) {
            foreach (var (field, name, model) in _globalFields) {
                if (model.GetFieldInitializer(field) is not { } initial) {
                    continue;
                }

                var target = new BoundFieldAccess(initial.Syntax, null, field);
                var assignment = new BoundExpressionStatement(initial.Syntax, new BoundAssignment(initial.Syntax, target, initial));
                var own = new SilFunction(GlobalInitPrefix + name, unit);
                new FunctionLowering(this, initSymbol, own, model.SyntaxTree.Text, TypeSubstitution.Empty).Lower(new BoundBlock(initial.Syntax, [], [assignment]));
                own.RemoveUnreachableBlocks();
                MakeImmortal(own, [field]);
                _module.Add(own);
            }

            return;
        }

        var source = _globalFields[0].Model.SyntaxTree.Text;
        var syntax = _globalFields[0].Field.DeclaringSyntax[0];
        var init = new SilFunction(GlobalsInit, unit);
        new FunctionLowering(this, initSymbol, init, source, TypeSubstitution.Empty).Lower(new BoundBlock(syntax, [], [..statements]));
        init.RemoveUnreachableBlocks();
        MakeImmortal(init, ordered.Select(static g => g.Field));
        _module.Add(init);

        var deinit = new SilFunction(GlobalsDeinit, unit);
        var entry = deinit.AddBlock();
        for (var i = ordered.Count - 1; i >= 0; i--) {
            var (name, type) = _globalsByField[ordered[i].Field];
            // An immortal global's objects are never released.
            if (!type.IsTrivial && !ordered[i].Field.IsImmortal) {
                entry.Append(new DestroyAddrInst(entry.Append(new GlobalAddrInst(name, type))));
            }
        }

        entry.Append(new ReturnInst(null));
        _module.Add(deinit);
    }

    /// <summary>Each lowered function's source, for the diagnostics of checks run on the whole module.</summary>
    readonly Dictionary<SilFunction, SourceText> _sources = [];

    readonly Dictionary<FunctionSymbol, FunctionSource> _declarations = [];
    readonly Dictionary<string, FunctionType> _instances = new(StringComparer.Ordinal);
    readonly Queue<(FunctionSymbol Symbol, string Name, FunctionType Type, TypeSubstitution Substitution)> _pending = new();

    /// <summary>
    ///     What asked for each instance: the generic function, its type
    ///     arguments, and the instance whose body asked, null when that body
    ///     was not generic — the chain that brought each instance about.
    /// </summary>
    readonly Dictionary<string, (FunctionSymbol Symbol, TypeSubstitution Substitution, string? Requester)> _instanceOrigins =
        new(StringComparer.Ordinal);

    /// <summary>The instance whose body is being lowered, or null for a function that is not generic.</summary>
    string? _loweringInstance;

    /// <summary>
    ///     How long a chain of instances, each asked for by the one before,
    ///     may grow. Ordinary generic code comes nowhere near; it is a
    ///     backstop for growth <see cref="Grows" /> does not see.
    /// </summary>
    const int MaxInstanceDepth = 200;

    void LowerBody(FunctionSymbol symbol, SyntaxNode syntax, SemanticModel model, string name, FunctionType type, TypeSubstitution substitution) {
        // A generator's call, its 'makeIterator()' and 'next()': code no source spells.
        if (_compilation.GetGenerator(symbol) is not null) {
            var before = _diagnostics.Count;
            try {
                LowerGeneratorPart(symbol, syntax, model.SyntaxTree.Text, name, type, substitution);
            } finally {
                Place(before, model.SyntaxTree);
            }

            return;
        }

        if ((_compilation.GetDerivedBody(symbol) ?? _compilation.GetLazyBody(symbol) ?? _compilation.GetInheritedInitializerBody(symbol)
                ?? BodyModel(model, syntax).GetBoundNode(syntax)) is not BoundBlock body) {
            return;
        }

        var reported = _diagnostics.Count;
        try {
            LowerBody(symbol, syntax, body, model.SyntaxTree.Text, name, type, substitution);
        } finally {
            Place(reported, model.SyntaxTree);
        }
    }

    /// <summary>What the declaration's <c>@inline(always)</c> or <c>@inline(never)</c> asks, if it has one.</summary>
    static SilInline InlineOf(FunctionSymbol symbol) {
        foreach (var attribute in symbol.Attributes) {
            if (attribute is { Name: "inline", FromExtension: false } && attribute.Arguments is [var how]) {
                return how switch {
                    "always" => SilInline.Always,
                    "never" => SilInline.Never,
                    _ => SilInline.Default,
                };
            }
        }

        return SilInline.Default;
    }

    void LowerBody(FunctionSymbol symbol, SyntaxNode syntax, BoundBlock body, SourceText source, string name, FunctionType type, TypeSubstitution substitution) {
        var function = new SilFunction(name, type) {
            SourceFile = source.FileName,
            Line = source.GetLinePosition(syntax.Span.Start).Line,
            Inline = InlineOf(symbol),
            Vectorised = symbol.GetAttribute("vectorised") is not null,
        };

        if (HasSelf(symbol)) {
            function.ParameterNames.Add("self");
        }

        function.ParameterNames.AddRange(symbol.Parameters.Select(static p => p.Name));

        // A class's initialiser is two functions: one that allocates the
        // object and calls the other, which initialises an object it is given
        // — what a subclass's 'super.init' calls.
        if (symbol.FunctionKind == FunctionKind.Initializer && type.Result is ClassType classType) {
            var initialize = new SilFunction(InitializerName(name), InitializerType(type)) {
                SourceFile = function.SourceFile,
                Line = function.Line,
            };
            initialize.ParameterNames.Add("self");
            initialize.ParameterNames.AddRange(function.ParameterNames);

            // Compiled once (#297): both take the class's arguments' metadata, after the initialiser's own parameters.
            if (_unspecialised.TryGetValue(name, out var generic)) {
                _unspecialised[initialize.Name] = generic;
            }

            new FunctionLowering(this, symbol, initialize, source, substitution).Lower(body);
            initialize.RemoveUnreachableBlocks();
            _module.Add(initialize);
            var allocator = _module.Add(Allocator(function, classType, initialize));
            if (initialize.IsUnspecialised) {
                allocator.ParameterNames.Clear();
                allocator.ParameterNames.AddRange(initialize.ParameterNames.Skip(1));
            }

            // Its body is checked as any other is: 'self.h = h' moves a
            // @noncopy parameter into the object, and is not a copy of it.
            MoveChecker.Check(initialize, source, _diagnostics);
            _sources[initialize] = source;
            return;
        }

        new FunctionLowering(this, symbol, function, source, substitution).Lower(body);
        function.RemoveUnreachableBlocks();
        _module.Add(function);

        if (symbol.FunctionKind == FunctionKind.Deinitializer && !_unspecialised.ContainsKey(name)) {
            switch (Lower(SelfType(symbol, substitution))) {
                case ClassType owner:
                    owner.Deinit = function.Name;
                    break;
                case StructType owner:
                    owner.Deinit = function.Name;
                    break;
            }
        }

        MoveChecker.Check(function, source, _diagnostics);
        _sources[function] = source;
    }

    internal static string InitializerName(string allocator) => allocator + "$initialize";

    /// <summary>
    ///     What initialising takes: the object, then the initialiser's own
    ///     parameters; it gives nothing, and may suspend or throw as the
    ///     initialiser may.
    /// </summary>
    internal static FunctionType InitializerType(FunctionType allocator) =>
        new([allocator.Result, ..allocator.Parameters], SilType.Unit, allocator.Thrown,
            allocator.Owned.IsEmpty ? default : [false, ..allocator.Owned], allocator.IsAsync);

    /// <summary>
    ///     The allocating initialiser: a new object, initialised; or, if
    ///     initialising throws, unmade — the fields set so far destroyed, no
    ///     deinit run — and let go of.
    /// </summary>
    static SilFunction Allocator(SilFunction allocator, ClassType type, SilFunction initialize) {
        var entry = allocator.AddBlock();
        var arguments = allocator.Type.Parameters.Select(p => (SilValue)entry.AddArgument(p)).ToList();
        var instance = entry.Append(new AllocRefInst(type));
        var reference = entry.Append(new FunctionRefInst(initialize.Name, initialize.Type));
        if (initialize.Type.Thrown is null) {
            entry.Append(new ApplyInst(reference, SilType.Unit, [instance, ..arguments]));
            entry.Append(new ReturnInst(instance));
            return allocator;
        }

        var normal = allocator.AddBlock();
        var failed = allocator.AddBlock();
        var error = failed.AddArgument(initialize.Type.Thrown);
        entry.Append(new TryApplyInst(reference, SilType.Unit, [instance, ..arguments], normal, failed));
        normal.Append(new ReturnInst(instance));
        failed.Append(new BuiltinInst("object_unmade", SilType.Unit, instance));
        failed.Append(new DestroyValueInst(instance));
        failed.Append(new ThrowInst(error));
        return allocator;
    }

    /// <summary>Each computed property's getter, lowered as a method.</summary>
    readonly Dictionary<FieldSymbol, FunctionSymbol> _getters = [];

    internal FunctionSymbol? GetterOf(FieldSymbol field) => _getters.GetValueOrDefault(field);

    readonly Dictionary<FieldSymbol, FunctionSymbol> _setters = [];

    /// <summary>A computed property's setter, which a store to the property calls.</summary>
    internal FunctionSymbol? SetterOf(FieldSymbol field) => _setters.GetValueOrDefault(field);

    /// <summary>Each subscript's getter and setter, lowered as methods.</summary>
    readonly Dictionary<FunctionSymbol, FunctionSymbol> _subscriptGetters = [];

    readonly Dictionary<FunctionSymbol, FunctionSymbol> _subscriptSetters = [];

    internal FunctionSymbol? SubscriptGetter(FunctionSymbol subscript) => _subscriptGetters.GetValueOrDefault(subscript);

    internal FunctionSymbol? SubscriptSetter(FunctionSymbol subscript) => _subscriptSetters.GetValueOrDefault(subscript);

    /// <summary>The type a member belongs to, whether declared in it or in an extension of it.</summary>
    internal static NamedTypeSymbol? OwnerOf(FunctionSymbol symbol) => Owner(symbol);

    static NamedTypeSymbol? Owner(FunctionSymbol symbol) =>
        symbol.ContainingSymbol switch {
            NamedTypeSymbol type => type,
            ExtensionSymbol extension => extension.ExtendedType,

            // An accessor belongs to its property, which belongs to the type.
            FieldSymbol { ContainingSymbol: NamedTypeSymbol type } => type,
            FieldSymbol { ContainingSymbol: ExtensionSymbol extension } => extension.ExtendedType,

            // ... or to its subscript, which does too.
            FunctionSymbol { FunctionKind: FunctionKind.Subscript } subscript => Owner(subscript),
            _ => null,
        };

    /// <summary>Every type parameter a function's body may mention: its type's, then its own.</summary>
    /// <remarks>A protocol's own method — a default implementation — has <c>Self</c> among them.</remarks>
    static IEnumerable<TypeParameterSymbol> TypeParametersOf(FunctionSymbol symbol) {
        var owner = Owner(symbol)?.OriginalDefinition;
        IEnumerable<TypeParameterSymbol> self = owner is { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } selfParameter } ? [selfParameter] : [];
        return self.Concat(owner?.TypeParameters ?? []).Concat(symbol.TypeParameters).Concat(OpaqueParametersOf(symbol));
    }

    /// <summary>
    ///     The type parameter standing for each distinct <c>some P</c> a
    ///     function's parameters have (<see cref="FunctionSymbol.OpaqueTypeParameters" />).
    /// </summary>
    static ImmutableArray<TypeParameterSymbol> OpaqueParametersOf(FunctionSymbol symbol) => symbol.OpaqueTypeParameters;

    /// <summary>A type with these type arguments in force, a <c>some P</c> parameter's type among them.</summary>
    /// <remarks>A <c>some P</c> result or local is what it hides: code is generated for the concrete type.</remarks>
    internal static TypeSymbol Substitute(TypeSubstitution substitution, TypeSymbol type) {
        TypeSymbol? StandsFor(OpaqueTypeSymbol opaque) {
            foreach (var parameter in substitution.Parameters) {
                if (parameter.OpaqueParameter is { } standsFor && standsFor.Equals(opaque)) {
                    return substitution.Lookup(parameter)!;
                }
            }

            return null;
        }

        if (type is OpaqueTypeSymbol { Origin: null } opaque && StandsFor(opaque) is { } argument) {
            return argument;
        }

        return TypeSubstitution.Reveal(substitution.Apply(type), StandsFor);
    }

    static bool IsGeneric(FunctionSymbol symbol) => TypeParametersOf(symbol).Any();

    /// <summary>
    ///     An instance's name as Sable writes it, for a debugger: the type
    ///     arguments of a generic owner after the owner's name
    ///     (<c>M.Stack&lt;Int&gt;.push</c>), the function's own after its
    ///     name (<c>M.describe&lt;String&gt;</c>), and for a protocol's
    ///     default implementation, the type it is for in the protocol's place
    ///     (<c>Circle.describe</c>).
    /// </summary>
    static string InstanceDisplayName(FunctionSymbol symbol, string baseName, TypeSubstitution substitution) {
        string Argument(TypeParameterSymbol parameter) => substitution.Lookup(parameter)?.ToDisplayString() ?? parameter.Name;

        var name = baseName;
        if (Owner(symbol)?.OriginalDefinition is { } owner && baseName.StartsWith(QualifiedName(owner) + ".", StringComparison.Ordinal)) {
            var member = baseName[(QualifiedName(owner).Length + 1)..];
            if (owner is { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } self }) {
                name = $"{Argument(self)}.{member}";
            } else if (owner.TypeParameters.Length > 0) {
                name = $"{QualifiedName(owner)}<{string.Join(", ", owner.TypeParameters.Select(Argument))}>.{member}";
            }
        }

        var own = symbol.TypeParameters.Concat(OpaqueParametersOf(symbol)).ToList();
        return own.Count == 0 ? name : $"{name}<{string.Join(", ", own.Select(Argument))}>";
    }

    /// <summary>
    ///     The instance of a generic function for these type arguments: its
    ///     name, and its signature — lowered later, once, however many calls
    ///     ask for it. Null when the arguments are not all known types.
    /// </summary>
    /// <remarks>
    ///     A call, <paramref name="polymorphic" />, may be given the one
    ///     instance that serves every type instead, when the one it asks for
    ///     would be one of an endless series (<see cref="PolymorphicInstance" />);
    ///     it then passes its arguments as that instance takes them.
    /// </remarks>
    internal (string Name, FunctionType Type)? Instance(FunctionSymbol symbol, TypeSubstitution substitution, SyntaxNode at, bool polymorphic = false) {
        if (!_names.TryGetValue(symbol, out var baseName) || !(_declarations.ContainsKey(symbol) || _localGenerics.ContainsKey(symbol))) {
            return null;
        }

        substitution = WithAssociatedTypes(symbol, substitution);

        // A static member of a generic type named without the type's
        // arguments — 'Task.sleep(…)', which never mentions Success — is one
        // function whatever they would be: they are Never.
        if (symbol.IsStatic
            && Owner(symbol)?.OriginalDefinition is { TypeKind: TypeKind.Class or TypeKind.Struct or TypeKind.Union, TypeParameters: { Length: > 0 } ownParameters }
            && ownParameters.Any(p => substitution.Lookup(p) is null)) {
            var map = substitution.Parameters.ToDictionary(static p => p, p => substitution.Lookup(p)!);
            foreach (var parameter in ownParameters.Where(p => substitution.Lookup(p) is null)) {
                map[parameter] = Core.Never;
            }

            substitution = new(map);
        }

        // '@polymorphic': the one instance, whatever the types, for a call
        // that passes its arguments as that instance takes them.
        if (polymorphic && symbol.IsPolymorphic && PolymorphicInstance(symbol, at) is { } single) {
            return single;
        }

        var arguments = new List<string>();
        foreach (var parameter in TypeParametersOf(symbol)) {
            // Never has no values to lower, and is still an argument: the
            // error type of 'apply { 7 }', whose closure cannot throw.
            if (substitution.Lookup(parameter) is { } never && never.Equals(Core.Never)) {
                arguments.Add("Never");
                continue;
            }

            // A value generic parameter's number names the instance as a type does.
            if (substitution.Lookup(parameter) is ValueTypeArgumentSymbol value) {
                arguments.Add(value.ToDisplayString());
                continue;
            }

            if (substitution.Lookup(parameter) is not { } argument || Lower(argument) is not { } lowered) {
                Unsupported<object>(at, $"'{symbol.Name}' with its type arguments unknown here");
                return null;
            }

            arguments.Add(Sanitise(lowered.ToString()));
        }

        var name = $"{baseName}$of${string.Join("$", arguments)}";
        if (_instances.TryGetValue(name, out var existing)) {
            return (name, existing);
        }

        if (EndlessInstances(symbol, substitution) is var (endless, grows)) {
            if (polymorphic && grows && PolymorphicInstance(symbol, at) is { } shared) {
                return shared;
            }

            _diagnostics.Error(ErrorCode.ERR_EndlessInstantiation, at.Span, endless);
            return null;
        }

        if (SignatureOf(symbol, at, substitution) is not { } type) {
            return null;
        }

        // A generic local function that captures takes its context last.
        if (_localGenerics.TryGetValue(symbol, out var local) && local.Closure is { Context: { } context } closure) {
            type = new FunctionType([..type.Parameters, context], type.Result, type.Thrown, type.Owned.IsEmpty ? default : [..type.Owned, false]);
            _localGenericClosures[name] = closure;
        }

        _instances[name] = type;
        _instanceOrigins[name] = (symbol, substitution, _loweringInstance);

        // One the library has in its archive: called there.
        if (_generics == GenericsMode.Unspecialised && CompiledBy(symbol)?.Prepared?.Contains(name) == true) {
            _external[name] = () => type;
            return (name, type);
        }

        _pending.Enqueue((symbol, name, type, substitution));
        return (name, type);
    }

    /// <summary>
    ///     A protocol's default implementation, for the type Self stands for:
    ///     its associated types too, which its body names bare — 'Item' in
    ///     an extension of Container is Self.Item, Int for a Pair whose
    ///     'first' returns one. Self decides them, so they are not part of
    ///     the instance's name.
    /// </summary>
    static TypeSubstitution WithAssociatedTypes(FunctionSymbol symbol, TypeSubstitution substitution) {
        if (Owner(symbol)?.OriginalDefinition is not { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } self } protocol
            || substitution.Lookup(self) is null) {
            return substitution;
        }

        var map = substitution.Parameters.ToDictionary(p => p, p => substitution.Lookup(p)!);
        var seen = new HashSet<NamedTypeSymbol>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<NamedTypeSymbol>([protocol]);
        while (pending.TryPop(out var current)) {
            if (!seen.Add(current.OriginalDefinition)) {
                continue;
            }

            foreach (var associated in current.OriginalDefinition.AssociatedTypes) {
                // Self.Item, read through what Self is.
                map.TryAdd(associated, substitution.Apply(new DependentMemberTypeSymbol(self, associated)));
            }

            foreach (var refined in current.OriginalDefinition.BaseTypes.OfType<NamedTypeSymbol>().Where(static b => b.TypeKind == TypeKind.Protocol)) {
                pending.Push(refined);
            }
        }

        return new(map);
    }

    /// <summary>
    ///     Why a new instance would be one of an endless series, or null: a
    ///     generic function that calls itself, directly or through others,
    ///     with a type argument built from its own, as <c>deep((x, x), n - 1)</c>
    ///     in <c>deep&lt;T&gt;</c> does (<c>Grows</c>), or a chain of instances
    ///     too long to be anything else. Each copy would ask for a larger one,
    ///     and monomorphisation would never end.
    /// </summary>
    (string Message, bool Grows)? EndlessInstances(FunctionSymbol symbol, TypeSubstitution substitution) {
        var depth = 0;
        for (var requester = _loweringInstance;
             requester is not null && _instanceOrigins.TryGetValue(requester, out var origin);
             requester = origin.Requester) {
            if (++depth >= MaxInstanceDepth) {
                return ($"'{symbol.Name}' is reached through more than {MaxInstanceDepth} generic calls, each needing a copy "
                        + "of a generic function for new types: a generic function is compiled once for each type it is used with, "
                        + "so this would not finish compiling", false);
            }

            if (!ReferenceEquals(origin.Symbol, symbol)) {
                continue;
            }

            foreach (var parameter in TypeParametersOf(symbol)) {
                if (origin.Substitution.Lookup(parameter) is { } before
                    && substitution.Lookup(parameter) is { } after
                    && Grows(before, after)) {
                    return ($"'{symbol.Name}' with {parameter.Name} = '{before.ToDisplayString()}' leads to a call of it with "
                            + $"{parameter.Name} = '{after.ToDisplayString()}', which leads to a larger type again, without end: "
                            + "a generic function is compiled once for each type it is used with, so this would not finish compiling; "
                            + "it can be compiled once for every type only when each of its parameters and its result is a type "
                            + "parameter or mentions none, and each type parameter is constrained to protocols alone", true);
                }
            }
        }

        return null;
    }

    /// <summary>The instances that serve every type: for each, the function, and the existentials its type parameters stand for.</summary>
    readonly Dictionary<string, (FunctionSymbol Symbol, TypeSubstitution Substitution)> _polymorphic = new(StringComparer.Ordinal);

    /// <summary>The function and the existentials of an instance that serves every type, or null for any other.</summary>
    internal (FunctionSymbol Symbol, TypeSubstitution Substitution)? PolymorphicOf(string instance) =>
        _polymorphic.TryGetValue(instance, out var polymorphic) ? polymorphic : null;

    /// <summary>
    ///     The one instance of a generic function that serves every type,
    ///     for polymorphic recursion, which no set of copies can: each type
    ///     parameter stands for <c>any C</c> of its constraints, so a value
    ///     comes boxed with the witnesses of its type's conformances — the
    ///     dictionary passed at run time — and the instance's own calls of
    ///     the function, with whatever types they build, are calls of it
    ///     again. Null when the function's signature cannot be passed that
    ///     way: a parameter or result that mentions a type parameter without
    ///     being one (a 'T[]' would need each element boxed), or a type
    ///     parameter constrained by more than protocols, or one of a type
    ///     around the function.
    /// </summary>
    (string Name, FunctionType Type)? PolymorphicInstance(FunctionSymbol symbol, SyntaxNode at) {
        var name = $"{_names[symbol]}$polymorphic";
        if (_instances.TryGetValue(name, out var existing)) {
            return (name, existing);
        }

        if (!TypeParametersOf(symbol).SequenceEqual(symbol.TypeParameters) || _localGenerics.ContainsKey(symbol)) {
            return null;
        }

        var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
        foreach (var parameter in symbol.TypeParameters) {
            var constraint = parameter.Constraints.Length == 1 ? parameter.Constraints[0] : new CompositionTypeSymbol(parameter.Constraints);
            if (ExistentialProtocols(constraint) is null) {
                return null;
            }

            map[parameter] = new ExistentialTypeSymbol(constraint);
        }

        bool Passable(TypeSymbol type) =>
            type is TypeParameterSymbol parameter && map.ContainsKey(parameter)
            || !symbol.TypeParameters.Any(p => Occurs(p, type));

        if (!symbol.Parameters.All(p => Passable(p.Type)) || !Passable(symbol.ReturnType)
            || symbol.ThrownType is { } thrown && symbol.TypeParameters.Any(p => Occurs(p, thrown))) {
            return null;
        }

        var substitution = new TypeSubstitution(map);
        if (SignatureOf(symbol, at, substitution) is not { } type) {
            return null;
        }

        _instances[name] = type;
        _instanceOrigins[name] = (symbol, substitution, _loweringInstance);
        _polymorphic[name] = (symbol, substitution);
        _pending.Enqueue((symbol, name, type, substitution));
        return (name, type);
    }

    /// <summary>True when <paramref name="after" /> is built from <paramref name="before" />, and is larger.</summary>
    static bool Grows(TypeSymbol before, TypeSymbol after) =>
        !after.Equals(before) && Occurs(before, after);

    /// <summary>True when <paramref name="part" /> is <paramref name="type" />, or one of the types it is made of.</summary>
    static bool Occurs(TypeSymbol part, TypeSymbol type) =>
        type.Equals(part)
        || type switch {
            NamedTypeSymbol named => named.TypeArguments.Any(a => Occurs(part, a)),
            ArrayTypeSymbol array => Occurs(part, array.ElementType),
            OptionalTypeSymbol optional => Occurs(part, optional.UnderlyingType),
            MetatypeTypeSymbol metatype => Occurs(part, metatype.InstanceType),
            ReferenceTypeSymbol reference => Occurs(part, reference.ReferencedType),
            TupleTypeSymbol tuple => tuple.ElementTypes.Any(e => Occurs(part, e)),
            FunctionTypeSymbol function =>
                function.ParameterTypes.Any(p => Occurs(part, p)) || Occurs(part, function.ReturnType),
            StructuralUnionTypeSymbol union => union.Cases.Any(c => Occurs(part, c)),
            _ => false,
        };

    /// <summary>
    ///     A function of the module, or a member of a struct, union, enum or
    ///     class, or of an extension of one. Local functions and classes that
    ///     take part in inheritance wait for closures and dynamic dispatch.
    /// </summary>
    static bool IsLowerable(FunctionSymbol symbol) =>
        symbol.ContainingSymbol is ModuleSymbol
        || Owner(symbol) switch {
            { TypeKind: TypeKind.Struct or TypeKind.Union or TypeKind.Enum } => true,

            // An extension of a primitive: 'extension Int: Comparable { … }'.
            { TypeKind: TypeKind.Builtin } => true,
            { TypeKind: TypeKind.Class or TypeKind.Actor } => true,

            // A protocol's default implementations: generic over Self, so
            // lowered only for the types that use them.
            { TypeKind: TypeKind.Protocol } => true,
            _ => false,
        };

    /// <summary>A class's superclass, if it has one.</summary>
    /// <remarks>
    ///     A class that inherits from itself, through any number of others,
    ///     has none: the type checker reports the cycle (S3026), and every
    ///     walk up the chain here — vtables, witnesses, metadata — would
    ///     otherwise never end. A language server lowers programs with
    ///     errors, so it reaches such a chain whenever one is written.
    /// </remarks>
    static NamedTypeSymbol? SuperclassOf(NamedTypeSymbol type) {
        var superclass = DirectSuperclassOf(type);
        if (superclass is null) {
            return null;
        }

        var seen = new HashSet<NamedTypeSymbol>(ReferenceEqualityComparer.Instance) { type.OriginalDefinition };
        for (var ancestor = superclass; ancestor is not null; ancestor = DirectSuperclassOf(ancestor)) {
            if (!seen.Add(ancestor.OriginalDefinition)) {
                return null;
            }
        }

        return superclass;
    }

    static NamedTypeSymbol? DirectSuperclassOf(NamedTypeSymbol type) =>
        type.BaseTypes.OfType<NamedTypeSymbol>().FirstOrDefault(static b => b.TypeKind == TypeKind.Class);

    /// <summary>
    ///     The classes some class this lowering can see inherits from: every
    ///     class declared outside a body, and those in the bodies it lowers.
    /// </summary>
    readonly HashSet<NamedTypeSymbol> _inherited = [];

    /// <summary>
    ///     Whether a class's methods are called through its vtable: whether
    ///     anything may inherit from it.
    /// </summary>
    /// <remarks>
    ///     Compiled whole, that is exactly whether something does. A unit
    ///     must not depend on what the units that depend on it declare —
    ///     each unit's object is kept until the units it depends on change
    ///     (docs/05 §5.4) — so separately, an <c>open</c> class is taken to
    ///     be inherited from: any module may subclass it, and another unit's
    ///     subclass, added or removed, then leaves this unit's code as it
    ///     was. A class that is not open can be subclassed only in its own
    ///     module, which is the unit's own or one it shares with the units it
    ///     is keyed by, so what is seen of it is all there is.
    /// </remarks>
    bool IsInherited(NamedTypeSymbol type) =>
        _inherited.Contains(type.OriginalDefinition)
        || _unit is not null && type.OriginalDefinition is { IsOpen: true, IsFinal: false, IsSealed: false };

    /// <summary>Whether a type is written with an array, whose kinds are one type here.</summary>
    static bool MentionsArray(TypeSymbol type) =>
        type switch {
            ArrayTypeSymbol => true,
            OptionalTypeSymbol optional => MentionsArray(optional.UnderlyingType),
            NamedTypeSymbol named => named.TypeArguments.Any(MentionsArray),
            TupleTypeSymbol tuple => tuple.ElementTypes.Any(MentionsArray),
            FunctionTypeSymbol function => function.ParameterTypes.Any(MentionsArray) || MentionsArray(function.ReturnType),
            _ => false,
        };

    /// <summary>The language class each SIL class was lowered from, for building its vtable.</summary>
    readonly Dictionary<ClassType, NamedTypeSymbol> _classes = [];

    /// <summary>The slot a method takes in a vtable: its name, and its parameter types, so that overloads differ.</summary>
    internal static string SlotName(FunctionSymbol method) =>
        method.Parameters.IsEmpty ? method.Name : method.Name + string.Concat(method.Parameters.Select(static p => "$" + Sanitise(p.Type.ToDisplayString())));

    /// <summary>
    ///     The slot a method or a property's getter or setter takes in a
    ///     vtable: an override takes the slot of what it overrides, whatever
    ///     its own parameter types say — 'take(_ x: Int)' overriding
    ///     'take(_ x: T)' is 'take$T' — and an accessor is named for its
    ///     property, which an override shares.
    /// </summary>
    internal static string VTableSlot(FunctionSymbol method) =>
        method is { FunctionKind: FunctionKind.Accessor, ContainingSymbol: FieldSymbol property }
            ? $"{method.Name}${property.Name}"
            : SlotName(method.RootOverridden ?? method);

    /// <summary>
    ///     True for a method a call must find through the vtable: one a
    ///     subclass may override — a property's getter and setter among them.
    /// </summary>
    internal bool IsDynamic(FunctionSymbol method) =>
        IsVirtual(method) && IsInherited(Owner(method)!);

    /// <summary>True for a member of a class that takes a slot in its vtable.</summary>
    static bool IsVirtual(FunctionSymbol method) =>
        !method.IsStatic
        && (method.FunctionKind == FunctionKind.Ordinary && method.ContainingSymbol is NamedTypeSymbol
            || method is { FunctionKind: FunctionKind.Accessor, Name: "get" or "set", ContainingSymbol: FieldSymbol { ContainingSymbol: NamedTypeSymbol } })
        && Owner(method) is { TypeKind: TypeKind.Class or TypeKind.Actor };

    /// <summary>
    ///     Fills each class's vtable: its superclass's slots, each override in
    ///     its base's slot, then the methods it adds.
    /// </summary>
    void BuildVTables() {
        var built = _builtVTables;
        void Build(ClassType type) {
            if (!built.Add(type) || !_classes.TryGetValue(type, out var symbol)) {
                return;
            }

            // One made at run time, of a hierarchy compiled once: as its definition's.
            using var opaqueInstance = HierarchiesOnce && SilType.MentionsOpaque(type) ? Opaque(new GenericShape(symbol.OriginalDefinition.TypeParameters, [])) : null;
            // A class of a type parameter's own, 'Rule<V>', was lowered to ask
            // what a closure may capture: no object is one, and its methods
            // have no instances.
            if (opaqueInstance is null && symbol.TypeArguments.Any(a => a is not ValueTypeArgumentSymbol && !a.Equals(Core.Never) && Lower(a) is null)) {
                return;
            }

            if (type.Superclass is { } superclass) {
                Build(superclass);
                type.VTable.AddRange(superclass.VTable);
            }

            if (!IsInherited(symbol) && type.Superclass is null) {
                return;
            }

            if (opaqueInstance is not null && HasSlotsOnce(symbol.OriginalDefinition)) {
                SuperclassAccessor(symbol.OriginalDefinition);
            }

            // A generic class's methods, for this class's type arguments:
            // instances, lowered once the vtables are built.
            var arguments = TypeSubstitution.For(symbol);
            var accessors = symbol.OriginalDefinition.Members.OfType<FieldSymbol>()
                .Where(static f => f is { IsStatic: false, IsComputed: true })
                .SelectMany(f => new[] { GetterOf(f), SetterOf(f) })
                .OfType<FunctionSymbol>();
            foreach (var method in symbol.OriginalDefinition.Members.OfType<FunctionSymbol>().Concat(accessors)) {
                if (method is { FunctionKind: FunctionKind.Ordinary, IsStatic: false, IsAbstract: true } && method.ContainingSymbol is NamedTypeSymbol && !IsGeneric(method)) {
                    AbstractStub(method);
                }

                if (!IsVirtual(method) || !_names.TryGetValue(method, out var name)) {
                    continue;
                }

                if (SlotEntry(type, symbol, method) is { } entry) {
                    name = entry;
                } else if (opaqueInstance is not null) {
                    // No object of it is made: what it has is each slot in its place.
                    name = MissingSlot(type);
                    if (type.MethodIndex(VTableSlot(method)) >= 0 || !method.TypeParameters.IsEmpty) {
                        continue;
                    }
                } else if (IsGeneric(method)) {
                    if (!method.TypeParameters.IsEmpty || method.DeclaringSyntax.FirstOrDefault() is not { } at
                        || Instance(method, arguments, at) is not { } instance) {
                        continue;
                    }

                    name = instance.Name;
                } else if (_module.Find(name) is null && (_unit is null || External(name) is null)) {
                    continue;
                }

                var slot = VTableSlot(method);
                var index = type.MethodIndex(slot);
                if (index >= 0) {
                    type.VTable[index] = (slot, name);
                } else {
                    type.VTable.Add((slot, name));
                }
            }
        }

        foreach (var type in _classes.Keys.ToList()) {
            Build(type);
        }
    }

    /// <summary>The classes whose vtables are built: building one may lower more code, which may lower more classes.</summary>
    readonly HashSet<ClassType> _builtVTables = [];

    /// <summary>
    ///     An abstract method's slot: code that traps, never reached — the
    ///     binder makes every concrete subclass override it, and an abstract
    ///     class cannot be made.
    /// </summary>
    void AbstractStub(FunctionSymbol method) {
        var name = _names.TryGetValue(method, out var known) ? known : Mangle(method, false);
        if (_module.Find(name) is not null || method.DeclaringSyntax.FirstOrDefault() is not { } syntax
            || SignatureOf(method, syntax, TypeSubstitution.Empty) is not { } type) {
            return;
        }

        AbstractStub(method, name, type);
        _names[method] = name;
    }

    /// <summary>The stub of that name: a generic class's abstract method has one for each of its instances.</summary>
    void AbstractStub(FunctionSymbol method, string name, FunctionType type) {
        var stub = new SilFunction(name, type);
        var entry = stub.AddBlock();
        foreach (var parameter in type.Parameters) {
            entry.AddArgument(parameter);
        }

        entry.Append(new CondFailInst(entry.Append(new IntegerLiteralInst(SilType.Bool, 1)), $"abstract method '{method.Name}' called"));
        entry.Append(new UnreachableInst());
        _module.Add(stub);
    }

    /// <summary>
    ///     A declaration's name after the module's and every type's it is
    ///     nested in: <c>App.Outer.Inner</c>, <c>Sable.Core.String.Index</c>.
    ///     Two modules may each nest a type of one name in a type of one
    ///     name, which the container alone would give one name. A type
    ///     nested in an extension is of the module the extension is written
    ///     in, which may not be the extended type's.
    /// </summary>
    internal static string QualifiedName(Symbol symbol) {
        if (symbol is NamedTypeSymbol type) {
            symbol = type.OriginalDefinition;
        }

        return symbol.ContainingSymbol switch {
            NamedTypeSymbol container => $"{QualifiedName(container)}.{symbol.Name}",
            ExtensionSymbol { DeclaringModule: { } module } extension => $"{module.Name}.{NestedName(extension.ExtendedType)}.{symbol.Name}",
            ExtensionSymbol extension => $"{QualifiedName(extension.ExtendedType)}.{symbol.Name}",
            var container => $"{container?.Name}.{symbol.Name}",
        };

        // The type's name after those of the types it is nested in, with no module.
        static string NestedName(NamedTypeSymbol type) {
            type = type.OriginalDefinition;
            return type.ContainingSymbol switch {
                NamedTypeSymbol container => $"{NestedName(container)}.{type.Name}",
                ExtensionSymbol extension => $"{NestedName(extension.ExtendedType)}.{type.Name}",
                _ => type.Name,
            };
        }
    }

    /// <summary>
    ///     <c>Module.name</c>, <c>Module.Type.name</c> for a member, and the
    ///     parameter types after it when the name is overloaded:
    ///     <c>M.area$Int$Int</c>.
    /// </summary>
    static string Mangle(FunctionSymbol symbol, bool overloaded) {
        var owner = Owner(symbol) is { } type
            ? QualifiedName(type)
            : symbol.ContainingSymbol?.Name;

        var name = symbol switch {
            { FunctionKind: FunctionKind.Deinitializer } => $"{owner}.deinit",
            { FunctionKind: FunctionKind.Accessor, ContainingSymbol: FieldSymbol field } => $"{owner}.{field.Name}.{symbol.Name}",

            // A type may have several subscripts, told apart by their indices'
            // types: 'M.Grid.subscript$Int$Int.get'.
            { FunctionKind: FunctionKind.Accessor, ContainingSymbol: FunctionSymbol { FunctionKind: FunctionKind.Subscript } subscript } =>
                $"{owner}.{SubscriptName(subscript)}.{symbol.Name}",
            { FunctionKind: FunctionKind.Subscript } => $"{owner}.{SubscriptName(symbol)}.get",

            // 'operator []' and 'operator ()' are named in words: brackets
            // would end the name where SIL text is read back.
            { FunctionKind: FunctionKind.Operator, Name: "[]" } => $"{owner}.operator$index",
            { FunctionKind: FunctionKind.Operator, Name: "()" } => $"{owner}.operator$call",
            // 'fn Drawable.render()' beside 'fn Printable.render()': 'M.Shape.Drawable.render'.
            { ExplicitProtocol: NamedTypeSymbol protocol } => $"{owner}.{protocol.Name}.{symbol.Name}",
            _ => $"{owner}.{symbol.Name}",
        };
        if (symbol.FunctionKind == FunctionKind.Subscript || symbol.ContainingSymbol is FunctionSymbol { FunctionKind: FunctionKind.Subscript }) {
            return name;
        }

        return overloaded
            ? name + string.Concat(symbol.Parameters.Select(static p => "$" + Sanitise(p.Type.ToDisplayString())))
            : name;
    }

    static string SubscriptName(FunctionSymbol subscript) =>
        "subscript" + string.Concat(subscript.Parameters.Select(static p => "$" + Sanitise(p.Type.ToDisplayString())));

    internal static string Sanitise(string text) =>
        new([..text.Select(static c => char.IsLetterOrDigit(c) || c is '_' or '.' ? c : '$')]);

    /// <summary>True for a method that takes <c>self</c>, as opposed to a static one or an initialiser.</summary>
    // An operator is a static function of its type: both operands are arguments.
    // A closure takes no 'self', whatever it is written in — a stored
    // property's initial value among them, whose closure the property
    // contains, as an accessor is contained: what it uses of 'self' it captures.
    static bool HasSelf(FunctionSymbol symbol) =>
        Owner(symbol) is not null && !symbol.IsStatic && symbol.FunctionKind is not (FunctionKind.Initializer or FunctionKind.Operator)
        && symbol is not { Name.Length: 0, ContainingSymbol: FieldSymbol, DeclaringSyntax: [ClosureExpressionSyntax or BraceClosureExpressionSyntax or KeyPathExpressionSyntax] };

    FunctionType? SignatureOf(FunctionSymbol symbol, SyntaxNode at, TypeSubstitution substitution) {
        SilType? Lower(TypeSymbol type) => this.Lower(Substitute(substitution, type));

        // An async function is lowered as an ordinary one, its type marked
        // async: AsyncLowering makes it a coroutine once every pass has run.
        // A 'rethrows' one throws what its function parameters throw,
        // together: the binder has made that its thrown type.
        var parameters = ImmutableArray.CreateBuilder<SilType>();

        // 'self' comes first: by value, or by address when the method may change it.
        if (HasSelf(symbol)) {
            if (this.Lower(SelfType(symbol, substitution)) is not { } self) {
                return Unsupported<FunctionType>(at, $"a member of '{SelfType(symbol, substitution).ToDisplayString()}'");
            }

            parameters.Add(symbol.IsMutating && self is not ClassType ? SilType.Address(self) : self);
        }

        foreach (var parameter in symbol.Parameters) {
            // 'in r: region' borrows the region, as any parameter is borrowed.
            if (parameter.Modifier is not (ParameterModifier.None or ParameterModifier.InOut or ParameterModifier.Consuming
                or ParameterModifier.In or ParameterModifier.Out)) {
                return Unsupported<FunctionType>(at, $"the '{parameter.Modifier}' parameter '{parameter.Name}'");
            }

            if (Lower(parameter.Type) is not { } type) {
                return Unsupported<FunctionType>(at, $"a parameter of type '{parameter.Type.ToDisplayString()}'");
            }

            // An inout or out parameter is the address of the caller's storage:
            // an out one is written there before the callee returns. A 'T&'
            // one is an address by its type.
            parameters.Add(parameter.Modifier is ParameterModifier.InOut or ParameterModifier.Out ? SilType.Address(type) : type);
        }

        var resultSymbol = symbol.FunctionKind == FunctionKind.Initializer ? SelfType(symbol, substitution) : symbol.ReturnType;
        if (Lower(resultSymbol) is not { } result) {
            return Unsupported<FunctionType>(at, $"a result of type '{resultSymbol.ToDisplayString()}'");
        }

        // 'throws(E)' with E as Never cannot throw: it is an ordinary function.
        // Nor does a generator's call, which only captures: its 'next()' throws.
        SilType? thrown = null;
        var isGeneratorCall = _compilation.GetGenerator(symbol) is { } yielding && ReferenceEquals(yielding.Function, symbol);
        if (symbol.ThrownType is { } thrownSymbol && !isGeneratorCall && !Substitute(substitution, thrownSymbol).Equals(Core.Never)) {
            if (Lower(thrownSymbol) is not { } thrownType) {
                return Unsupported<FunctionType>(at, $"'{symbol.Name}', which throws '{thrownSymbol.ToDisplayString()}'");
            }

            thrown = thrownType;
        }

        // An initialiser owns its parameters: it usually stores them, and a
        // caller's temporary then passes straight in rather than being copied.
        // A 'consuming' parameter is owned whatever the function.
        var selfCount = HasSelf(symbol) ? 1 : 0;
        var owned = parameters.Select((p, i) => p is not AddressType && !p.IsTrivial
            && (symbol.FunctionKind == FunctionKind.Initializer
                || i >= selfCount && symbol.Parameters[i - selfCount].Modifier == ParameterModifier.Consuming)).ToImmutableArray();

        // A generator's call only captures: only its 'next()' may wait.
        return new FunctionType(parameters.ToImmutable(), result, thrown, owned, symbol.IsAsync && !isGeneratorCall);
    }

    /// <summary>The type a member's <c>self</c> is: its owner, constructed with the type arguments in force.</summary>
    static TypeSymbol SelfType(FunctionSymbol symbol, TypeSubstitution substitution) {
        var owner = Owner(symbol)!.OriginalDefinition;

        // A protocol's default implementation: Self is the type it is used for.
        if (owner is { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } self }) {
            return substitution.Apply(self);
        }

        // An array extension's member: self is the array, of this element.
        if (owner.ExtendedArray is { } array) {
            return substitution.Apply(array);
        }

        // An optional's member: self is the optional, of what it wraps.
        if (owner.ExtendedOptional is { } optional) {
            return substitution.Apply(optional);
        }

        return owner.TypeParameters.IsEmpty
            ? owner
            : owner.Construct([..owner.TypeParameters.Select(p => substitution.Apply(p))]);
    }

    // =======================================================================
    // Types
    // =======================================================================

    /// <summary>The language types lowered so far, in order, so that a failure can take back what it started.</summary>
    readonly List<TypeSymbol> _lowered = [];

    /// <summary>The SIL type for a language type, or null when there is none yet.</summary>
    /// <remarks>
    ///     A named type is entered before its members are lowered, so that a
    ///     member reaching it again through a reference — a class's own type,
    ///     an array of it — finds it. One that holds itself by value has no
    ///     size and no SIL type; nor has one with a member that has none, and
    ///     every type begun while lowering it is taken back with it.
    /// </remarks>
    internal SilType? Lower(TypeSymbol type) {
        // In unspecialised code, a value of a type parameter's type is held
        // indirectly (#297), and an array of them is a buffer of such values:
        // neither kept, as the type parameter is one only while its code is lowered.
        // An associated type read through one is too, by the name its class
        // of same-type requirements reads it as — or the type they make it.
        if (_opaque.Count > 0 && IsAssociatedOf(type, _opaque) && Canonical(type) is var canonical && !canonical.Equals(type)) {
            return Lower(canonical);
        }

        // A union of them, with what the requirements in force make one type collapsed.
        if (_opaque.Count > 0 && type is StructuralUnionTypeSymbol && Canonical(type) is var union && !union.Equals(type)) {
            return Lower(union);
        }

        if (IsOpaque(type)) {
            return new OpaqueType(OpaqueName(type));
        }

        if (_opaque.Count > 0 && IsArrayOf(type, _opaque)) {
            return Lower(((ArrayTypeSymbol)type).ElementType) is { } opaqueElement
                ? ((ArrayTypeSymbol)type).ArrayKind == ArrayKind.Slice ? new SliceType(opaqueElement) : new ArrayType(opaqueElement)
                : null;
        }

        // An optional or a tuple of them: laid out at run time from its parts' metadata.
        if (_opaque.Count > 0 && type is OptionalTypeSymbol or TupleTypeSymbol && Mentions(type, _opaque)) {
            return LowerOpaqueAggregate(type);
        }

        // A generic type of them is laid out at run time, and found again by its name:
        // not kept for the type, whose type parameters are opaque only while their code is lowered.
        var mentionsOpaque = _opaque.Count > 0 && Mentions(type, _opaque);
        if (mentionsOpaque && type is NamedTypeSymbol { IsConstructed: true } heldType && _module.FindType(TypeName(heldType)) is { } named) {
            return named;
        }

        if (!mentionsOpaque && _types.TryGetValue(type, out var cached)) {
            return cached;
        }

        if (type is NamedTypeSymbol { IsConstructed: true } constructed && EndlessType(constructed) is { } endless) {
            var definition = constructed.OriginalDefinition;
            if (_endlessTypes.Add(definition)) {
                _diagnostics.Add(Diagnostic.Error(ErrorCode.ERR_EndlessInstantiation, definition.Location ?? default, endless)
                    .AnchoredAt(definition.DeclaringSyntax.FirstOrDefault()));
            }

            _types[type] = null;
            return null;
        }

        var lowered = _lowered.Count;
        var types = _module.Types.Count;
        _loweringTypes.Add(type);
        SilType? result;
        try {
            result = LowerUncached(type);
        } finally {
            _loweringTypes.RemoveAt(_loweringTypes.Count - 1);
        }

        if (result is null) {
            // Take back every type begun inside this one.
            foreach (var begun in _lowered.Skip(lowered)) {
                _types.Remove(begun);
            }

            _lowered.RemoveRange(lowered, _lowered.Count - lowered);
            foreach (var added in _module.Types.Skip(types).ToList()) {
                _module.RemoveType(added);
            }
        }

        _types[type] = result;
        if (mentionsOpaque) {
            _types.Remove(type);
        }

        _lowered.Add(type);
        return result;
    }

    /// <summary>
    ///     An optional or a tuple with a type parameter of unspecialised code
    ///     among its parts (#297), as it is held there: an
    ///     <c>Optional$$opaque$T$</c> whose payload is opaque, laid out at run
    ///     time from its metadata; not kept for the type, which mentions a type
    ///     parameter that is opaque only while its code is lowered.
    /// </summary>
    SilType? LowerOpaqueAggregate(TypeSymbol type) {
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
                if (Lower(optional.UnderlyingType) is not { } wrapped || wrapped == SilType.Unit) {
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

            default:
                return null;
        }
    }

    /// <summary>The types being lowered, each needed by the one before: a member's type, a type argument's.</summary>
    readonly List<TypeSymbol> _loweringTypes = [];

    /// <summary>The generic types reported as needing endless copies, each reported once.</summary>
    readonly HashSet<NamedTypeSymbol> _endlessTypes = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    ///     Why a copy of a generic type would be one of an endless series, or
    ///     null: lowering it is needed by lowering a copy of the same type
    ///     whose type arguments it grows from — 'Tree&lt;T&gt;' with a case that
    ///     holds a 'Tree&lt;T?&gt;', which holds a 'Tree&lt;T??&gt;', and so on
    ///     (<see cref="Grows" />) — or by a chain of types too long to be
    ///     anything else. A generic type is laid out once for each set of
    ///     type arguments, so its layout would never be finished.
    /// </summary>
    string? EndlessType(NamedTypeSymbol type) {
        if (_loweringTypes.Count >= MaxInstanceDepth) {
            return $"'{type.ToDisplayString()}' is reached through more than {MaxInstanceDepth} types, each needing the next to be laid out: "
                + "a generic type is laid out once for each set of type arguments it is used with, so this would not finish compiling";
        }

        foreach (var outer in _loweringTypes) {
            if (outer is not NamedTypeSymbol named || !ReferenceEquals(named.OriginalDefinition, type.OriginalDefinition)) {
                continue;
            }

            for (var i = 0; i < type.TypeArguments.Length && i < named.TypeArguments.Length; i++) {
                if (Grows(named.TypeArguments[i], type.TypeArguments[i])) {
                    var parameter = type.OriginalDefinition.TypeParameters[i].Name;
                    return $"'{named.ToDisplayString()}' holds a '{type.ToDisplayString()}', which holds a larger type again, without end "
                        + $"({parameter} = '{named.TypeArguments[i].ToDisplayString()}' leads to {parameter} = '{type.TypeArguments[i].ToDisplayString()}'): "
                        + "a generic type is laid out once for each set of type arguments it is used with, so this would not finish compiling; "
                        + "hold the same type arguments, or keep what grows in a value rather than in a type";
                }
            }
        }

        return null;
    }

    /// <summary>Enters a named type before its members, so that they may refer to it.</summary>
    /// <summary>
    ///     A generic type's deinit for an instance of the type: lowered for
    ///     its type arguments, as nothing calls it by name, and named now so
    ///     that what destroys a value of the type runs it.
    /// </summary>
    // A type whose arguments are not all known — 'Box<S.Element>', lowered
    // to ask of a generic function's local whether it is move-only, before
    // any instance of the function says what S is — has no deinit instance
    // to name: none is made, and none is asked for.
    string? GenericDeinit(NamedTypeSymbol named) =>
        named.OriginalDefinition.Members.OfType<FunctionSymbol>().FirstOrDefault(static f => f.FunctionKind == FunctionKind.Deinitializer) is { } deinit
        && IsGeneric(deinit) && deinit.DeclaringSyntax.FirstOrDefault() is { } at
        && TypeSubstitution.For(named) is var substitution
        && substitution.Parameters.All(p => substitution.Lookup(p) is { } argument
            && (argument.Equals(Core.Never) || argument is ValueTypeArgumentSymbol || Lower(argument) is not null))
            ? Instance(deinit, substitution, at)?.Name
            : null;

    /// <summary>
    ///     A move-only struct's deinit by the name it has, known from the
    ///     type itself: its body may be lowered only once something is found
    ///     to use the type, and how the type is laid out — with a flag for
    ///     whether it still holds its value — must be known before.
    /// </summary>
    string? PlainDeinit(NamedTypeSymbol named) =>
        named.OriginalDefinition.IsNoncopyable && DeinitOf(named.OriginalDefinition) is { } deinit && !IsGeneric(deinit)
            ? _names.GetValueOrDefault(deinit)
            : null;

    T Begin<T>(TypeSymbol symbol, T type) where T : NominalType {
        if (symbol is NamedTypeSymbol { IsConstructed: true } constructed) {
            type.DisplayName = $"{QualifiedName(constructed)}<{string.Join(", ", constructed.TypeArguments.Select(static a => a.ToDisplayString()))}>";
        }

        _types[symbol] = type;
        _lowered.Add(symbol);

        // Its type arguments, for its metadata: TypeName has lowered each already.
        if (symbol is NamedTypeSymbol { IsConstructed: true } generic
            && generic.TypeArguments.Select(Lower).ToList() is var arguments && arguments.TrueForAll(static a => a is not null)) {
            type.TypeArguments = [..arguments!];

            // Its name has its arguments' as their metadata has them, qualified,
            // as one made at run time from theirs does (sable_aggregate_type).
            if (!arguments.Exists(static a => SilType.MentionsOpaque(a!))) {
                type.DisplayName = $"{QualifiedName(generic)}<{string.Join(", ", arguments.Select(static a => TypeMetadata.DisplayName(a!)))}>";
            }
        }

        // What it declares it conforms to: its metadata's conformance records;
        // and where its arguments are known, those of its conditional
        // conformances they meet.
        if (symbol is NamedTypeSymbol named) {
            type.Conformances.AddRange(DeclaredProtocols(named.OriginalDefinition));
            type.ConditionalConformances.AddRange(ConditionalProtocols(named.OriginalDefinition).Where(c => !type.Conformances.Contains(c.Protocol)));
            _module.MeetConditionalConformances(type);

            // Laid out at run time, for arguments known only there: its conformances get template tables (SilGen.Templates.cs).
            if (named.IsConstructed && type is StructType or ClassType or EnumType && !type.TypeArguments.IsDefaultOrEmpty && type.TypeArguments.Any(SilType.MentionsOpaque)) {
                _runtimeInstances.Add((named, type));
            }
        }

        return _module.AddType(type);
    }

    SilType? LowerUncached(TypeSymbol type) {
        // A 'some P' result or local is laid out as what it hides; inside an
        // optional or an array, too.
        if (type is OpaqueTypeSymbol { Origin: not null } opaque) {
            return TypeSubstitution.Reveal(opaque) is var hidden && hidden is not OpaqueTypeSymbol ? Lower(hidden) : null;
        }

        if (type.Equals(Core.Int)) {
            return SilType.Int;
        }

        if (type.Equals(Core.Bool)) {
            return SilType.Bool;
        }

        if (type.Equals(Core.F64)) {
            return SilType.F64;
        }

        if (type is NamedTypeSymbol { TypeArguments.IsEmpty: true } primitive
            && (Core.Integers.Contains(primitive) || Core.Floats.Contains(primitive))
            && SilType.Numbers.FirstOrDefault(n => n.ToString() == primitive.Name) is { } number) {
            return number;
        }

        if (type.Equals(Core.Char)) {
            return SilType.Char;
        }

        // 'SIMD<F32, 4>' is four F32 lanes, and 'SIMDMask<4>' four Bools: a
        // vector (Simd.cs), however Sable.Core declares the struct.
        if (Core.SimdLanes(type) is var (lane, count)) {
            return count is >= 1 and <= 1024 && Lower(lane) is { } element && VectorType.IsLane(element) ? new VectorType(element, (int)count) : null;
        }

        // Never has no values: what gives it never returns, so it is laid
        // out as nothing at all, and a call to it is followed by 'unreachable'.
        if (type.Equals(Core.Void) || type.Equals(Core.Never)) {
            return SilType.Unit;
        }

        if (type.Equals(Core.String)) {
            return SilType.String;
        }

        if (type.Equals(Core.Region)) {
            return SilType.Region;
        }

        if (type.Equals(Core.Type) || type is ExistentialTypeSymbol { MetatypeOf: not null }) {
            return SilType.Metadata;
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

            // The growable kinds and the fixed one share a counted buffer.
            // So does a bounded array outside an @inline_layout struct —
            // 'T[..k]' as a local, a parameter or a field of a plain struct —
            // with its bound checked where it grows; inside one it is bytes in
            // the block (InlineField). A slice is a view of a run of such a
            // buffer's elements, sharing it ('[T:]').
            // An array copies its elements on write, so a move-only element has no array yet.
            case ArrayTypeSymbol { ArrayKind: ArrayKind.Grow or ArrayKind.GrowShrink or ArrayKind.Fixed or ArrayKind.Slice or ArrayKind.Inline } array:
                return Lower(array.ElementType) is { } elementType && elementType is not NominalType { IsNoncopyable: true }
                    ? array.ArrayKind == ArrayKind.Slice ? new SliceType(elementType) : new ArrayType(elementType)
                    : null;

            // 'any Error': an object, which each error type boxes itself in.
            case ExistentialTypeSymbol { Constraint: var constraint } when constraint.Equals(Core.Error):
                return AnyErrorType();

            // 'A | any Error' is any error at all.
            case StructuralUnionTypeSymbol union when union.Cases.Any(c => c.Equals(Core.AnyError)):
                return AnyErrorType();

            // 'A | B': an enum with a case for each.
            case StructuralUnionTypeSymbol union:
                return LowerUnion(union);

            // 'alias Json = … | Json[]': the same, named for the alias, so
            // the cases that mention it find it while it is being made.
            case RecursiveUnionTypeSymbol { Union: { } union } recursive:
                return LowerRecursiveUnion(recursive, union);

            // 'any P', 'any A & B': the box holding the value, and a slot per requirement.
            case ExistentialTypeSymbol { Constraint: var constraint } when ExistentialProtocols(constraint) is { } protocols:
                return ExistentialLayout(protocols);

            // 'T.Type': its one value is the type, as its metadata — what a
            // 'Type' holds too, so a metatype converts to one as it is. Each
            // instance of generic code is generated for its types, so there
            // the value is a constant, 'metadata $T'.
            case MetatypeTypeSymbol:
                return SilType.Metadata;

            // A reference to an inline value, absolute or relative: a view of its bytes.
            case ReferenceTypeSymbol reference when Lower(reference.ReferencedType) is StructType { IsInline: true } inline:
                return new InlinePointerType(inline);

            // A reference: the address of what it refers to. Root inference
            // checks it does not outlive that.
            case ReferenceTypeSymbol { RelativeWidth: null } reference:
                return Lower(reference.ReferencedType) is { } referenced ? SilType.Address(referenced) : null;

            // A function as a value: code and a context, called with both.
            case FunctionTypeSymbol { IsRethrows: false } functionType: {
                var parameters = ImmutableArray.CreateBuilder<SilType>();
                for (var i = 0; i < functionType.ParameterTypes.Length; i++) {
                    if (Lower(functionType.ParameterTypes[i]) is not { } lowered) {
                        return null;
                    }

                    // An inout parameter is the caller's storage, as a declared function's is.
                    parameters.Add(functionType.IsInOut(i) ? SilType.Address(lowered) : lowered);
                }

                if (Lower(functionType.ReturnType) is not { } result) {
                    return null;
                }

                SilType? thrown = null;
                if (functionType.ThrownType is { } thrownSymbol && !thrownSymbol.Equals(Core.Never) && (thrown = Lower(thrownSymbol)) is null) {
                    return null;
                }

                // In unspecialised code, a function of a type parameter's
                // values takes the generic convention (#297): a result of one
                // is written where the caller says, after the parameters.
                if (SilType.IsIndirect(result)) {
                    parameters.Add(SilType.Address(result));
                    result = SilType.Unit;
                }

                // So is an error of one, thrown as that address.
                if (thrown is not null && SilType.IsIndirect(thrown)) {
                    thrown = SilType.Address(thrown);
                    parameters.Add(thrown);
                }

                return new ThickFunctionType(new FunctionType(parameters.ToImmutable(), result, thrown, isAsync: functionType.IsAsync));
            }

            case OptionalTypeSymbol optional: {
                if (Lower(optional.UnderlyingType) is not { } wrapped) {
                    return null;
                }

                var name = $"Optional${Sanitise(wrapped.ToString())}";
                if (_module.FindType(name) is EnumType existing) {
                    return existing;
                }

                // 'Void?' — what 'try?' makes of a call that returns nothing —
                // has nothing to carry: whether there was a value is all of it.
                var enumType = Begin(type, new EnumType(name));
                enumType.Cases.Add(("Some", wrapped == SilType.Unit ? null : wrapped));
                enumType.Cases.Add(("None", null));
                return HoldsItself(enumType) ? null : enumType;
            }

            // A generator's sequence or iterator: what it captured, and the body's state.
            case NamedTypeSymbol { IsGenericDefinition: false, TypeKind: TypeKind.Struct } named when _compilation.GetGenerator(named) is { } generator:
                return LowerGeneratorType(named, generator);

            case NamedTypeSymbol { IsGenericDefinition: false, TypeKind: TypeKind.Struct } named when named.OriginalDefinition.IsInlineLayout:
                return LowerInline(named);

            case NamedTypeSymbol { IsGenericDefinition: false, TypeKind: TypeKind.Struct } named: {
                var structType = Begin(named, new StructType(TypeName(named)) {
                    IsNoncopyable = named.OriginalDefinition.IsNoncopyable,
                    IsPacked = named.OriginalDefinition.IsPacked,
                    Alignment = named.OriginalDefinition.Alignment,
                });
                var members = TypeSubstitution.For(named);
                foreach (var field in StoredFields(named.OriginalDefinition)) {
                    if ((StoredFunctionType(field) ?? Lower(members.Apply(field.Type))) is not { } fieldType) {
                        return null;
                    }

                    structType.Fields.Add((field.Name, Stored(field.ReferenceKind, fieldType)));
                }

                // A struct that holds a move-only value is move-only itself:
                // copying it would copy the value, and run its deinit twice.
                // This is how 'Box<T: ~Copyable>' holding a '@noncopy' type
                // is moved, never copied.
                structType.IsNoncopyable |= structType.Fields.Exists(static f => IsMoveOnly(f.Type));
                if (HoldsItself(structType)) {
                    return null;
                }

                structType.Deinit ??= GenericDeinit(named) ?? PlainDeinit(named);
                return structType;
            }

            case NamedTypeSymbol { TypeArguments: [var success] } task when task.OriginalDefinition.Equals(Core.Task):
                return TaskClass(success);

            // An actor is a class whose calls are serialised by its executor:
            // a call from outside it hops onto it first (SilGen.Actors.cs).
            case NamedTypeSymbol { IsGenericDefinition: false, TypeKind: TypeKind.Class or TypeKind.Actor } named when !named.OriginalDefinition.Equals(Core.Task): {
                // 'Box<Int[]>' and 'Box<Int[>..]>' are one class here, as their
                // arguments are one type: its methods have one set of instances.
                var className = TypeName(named);
                if (named.TypeArguments.Any(static a => MentionsArray(a)) && named.TypeArguments.All(a => Lower(a) is not null)
                    && _classes.FirstOrDefault(c => c.Key.Name == className && ReferenceEquals(c.Value.OriginalDefinition, named.OriginalDefinition)).Key is { } same
                    && _types.ContainsValue(same)) {
                    _types[named] = same;
                    return same;
                }

                var classType = Begin(named, new ClassType(className));
                _classes[classType] = named;

                // A superclass's fields come first, where they are in it.
                if (SuperclassOf(named.OriginalDefinition) is { } superclass) {
                    if (Lower(TypeSubstitution.For(named).Apply(superclass)) is not ClassType superType) {
                        return null;
                    }

                    classType.Superclass = superType;
                    classType.Fields.AddRange(superType.Fields);
                    classType.LetFields.UnionWith(superType.LetFields);
                }

                var members = TypeSubstitution.For(named);
                foreach (var field in StoredFields(named.OriginalDefinition)) {
                    if ((StoredFunctionType(field) ?? Lower(members.Apply(field.Type))) is not { } fieldType) {
                        return null;
                    }

                    if (field is { IsMutable: false, IsLazy: false }) {
                        classType.LetFields.Add(classType.Fields.Count);
                    }

                    classType.Fields.Add((field.Name, Stored(field.ReferenceKind, fieldType)));
                }

                // An actor's serial executor, the runtime's: after every
                // field, where reflection, which pairs the declared fields
                // with the first ones, does not see it (SilGen.Actors.cs).
                if (named.TypeKind == TypeKind.Actor) {
                    classType.Fields.Add((ExecutorField, SilType.Int));
                    classType.Fields.Add((ExecutorQueueField, SilType.Int));
                    classType.Fields.Add((ExecutorHolderField, SilType.Int));
                }

                // Unspecialised code never makes one of a type only its metadata knows (#297): nothing destroys one as that.
                if (!SilType.MentionsOpaque(classType)) {
                    classType.Deinit ??= GenericDeinit(named);
                } else {
                    classType.HasGenericDeinit = DeinitOf(named.OriginalDefinition) is not null;
                }

                return classType;
            }

            case NamedTypeSymbol { IsGenericDefinition: false, TypeKind: TypeKind.Union } named: {
                var enumType = Begin(named, new EnumType(TypeName(named)) { IsNoncopyable = named.OriginalDefinition.IsNoncopyable, IsUnion = true });
                var members = TypeSubstitution.For(named);
                foreach (var unionCase in named.OriginalDefinition.Members.OfType<UnionCaseSymbol>()) {
                    SilType? payload = null;
                    if (unionCase.Payload.Length > 0) {
                        var parts = ImmutableArray.CreateBuilder<SilType>();
                        foreach (var field in unionCase.Payload) {
                            if ((StoredPayloadType(named.OriginalDefinition, field.Type) ?? Lower(members.Apply(field.Type))) is not { } part) {
                                return null;
                            }

                            parts.Add(part);
                        }

                        payload = parts.Count == 1 ? parts[0] : SilType.Tuple(parts.ToImmutable());
                        if (unionCase.Payload.All(static field => field.Label is not null)) {
                            enumType.PayloadLabels[unionCase.Name] = [..unionCase.Payload.Select(static field => field.Label!)];
                        }
                    }

                    enumType.Cases.Add((unionCase.Name, payload));
                }

                // As for a struct: a union with a move-only payload is move-only.
                enumType.IsNoncopyable |= enumType.Cases.Exists(static c => c.Payload is { } payload && IsMoveOnly(payload));

                // A case that holds the union by value — 'Node(left: Tree<T>, …)'
                // — would make it infinitely large: its payload goes in a box,
                // a counted allocation shared on copy (docs/03 §"Indirect
                // cases"); '@indirect' asks for one anyway.
                for (var i = 0; i < enumType.Cases.Count; i++) {
                    if (enumType.Cases[i].Payload is not { } payload) {
                        continue;
                    }

                    if (Holds(payload, enumType) || IsMarkedIndirect(named.OriginalDefinition, enumType.Cases[i].Name)) {
                        enumType.Cases[i] = (enumType.Cases[i].Name, BoxType(payload));
                        (_indirect.TryGetValue(enumType, out var boxed) ? boxed : _indirect[enumType] = []).Add(i);
                    }
                }

                return HoldsItself(enumType) ? null : enumType;
            }

            // A '@flags' enum is any set of its cases: the integer it is.
            case NamedTypeSymbol { TypeKind: TypeKind.Enum, IsFlags: true } flags:
                return Lower(_compilation.EnumRawType(flags));

            case NamedTypeSymbol { TypeKind: TypeKind.Enum } named: {
                var enumType = Begin(named, new EnumType(TypeName(named)));
                foreach (var enumCase in named.Members.OfType<EnumCaseSymbol>()) {
                    enumType.Cases.Add((enumCase.Name, null));
                }

                return enumType;
            }

            default:
                return null;
        }
    }

    /// <summary>
    ///     An <c>@inline_layout</c> struct: its fields as they are stored —
    ///     scalars, inline arrays of scalars or inline structs, inline structs,
    ///     relative references to inline structs. Nothing with a pointer in it.
    /// </summary>
    StructType? LowerInline(NamedTypeSymbol named) {
        var structType = Begin(named, new StructType(TypeName(named)) { IsInline = true, Alignment = named.OriginalDefinition.Alignment });
        var members = TypeSubstitution.For(named);
        foreach (var field in StoredFields(named.OriginalDefinition)) {
            if (InlineField(members.Apply(field.Type)) is not { } fieldType) {
                return null;
            }

            structType.Fields.Add((field.Name, fieldType));
        }

        return HoldsItself(structType) ? null : structType;
    }

    SilType? InlineField(TypeSymbol type) {
        static int? Width(string? width) => width switch { "u8" or "U8" => 1, "u16" or "U16" => 2, "u32" or "U32" => 4, _ => null };

        switch (type) {
            case OptionalTypeSymbol { UnderlyingType: ReferenceTypeSymbol { RelativeWidth: var width, Pool: null } reference }
                when Width(width) is { } bytes && Lower(reference.ReferencedType) is StructType { IsInline: true } target:
                return new RelativeReferenceType(target, bytes, true);

            case ReferenceTypeSymbol { RelativeWidth: var width, Pool: null } reference
                when Width(width) is { } bytes && Lower(reference.ReferencedType) is StructType { IsInline: true } target:
                return new RelativeReferenceType(target, bytes, false);

            case ArrayTypeSymbol { ArrayKind: ArrayKind.Inline, Capacity: { } capacity } array:
                return InlineElement(array.ElementType) is { } bounded ? new InlineArrayType(bounded, capacity) : null;

            case ArrayTypeSymbol { ArrayKind: ArrayKind.Grow } array:
                return InlineElement(array.ElementType) is { } grown ? new InlineArrayType(grown, null) : null;

            default:
                return InlineElement(type);
        }
    }

    /// <summary>What an inline array, or a field, may hold by value: a scalar or an inline struct.</summary>
    SilType? InlineElement(TypeSymbol type) =>
        Lower(type) switch {
            StructType { IsInline: true } inline => inline,
            { } scalar when InlineLayout.Scalar(scalar) is not null => scalar,
            _ => null,
        };

    /// <summary>
    ///     The box a local lives in when a closure captures it by reference:
    ///     an object of one field, shared by the frame and every closure.
    /// </summary>
    internal ClassType BoxType(SilType type) {
        var name = $"Box$of${Sanitise(type.ToString())}";
        if (_module.FindType(name) is ClassType existing) {
            return existing;
        }

        var box = _module.AddType(new ClassType(name));
        box.Fields.Add(("value", type));

        // Of a type only metadata knows: an instance of a generic class, laid out at run time (#297).
        if (SilType.MentionsOpaque(type)) {
            box.TypeArguments = [type];
            box.DisplayName = $"$box<{TypeMetadata.DisplayName(type)}>";
        }

        return box;
    }

    // -----------------------------------------------------------------------
    // Errors
    // -----------------------------------------------------------------------

    /// <summary>
    ///     <c>any Error</c>, as bare <c>throws</c> throws it: an object, of a
    ///     class with no fields that each error type has a box subclass of —
    ///     so the error stays what it was, and <c>catch let e as IoError</c>
    ///     asks the object's class, as a downcast does. A typed error is
    ///     thrown as itself; this is what one becomes where <c>any Error</c>
    ///     is wanted.
    /// </summary>
    /// <remarks>
    ///     Its one vtable slot writes the error out, as <c>Reflect.describe</c>
    ///     writes the error's own type: that is how <c>"\(error)"</c> in a
    ///     <c>catch</c> says which error it caught.
    /// </remarks>
    internal ClassType AnyErrorType() {
        if (_module.FindType("any$Error") is ClassType existing) {
            return existing;
        }

        var anyError = _module.AddType(new ClassType("any$Error"));
        anyError.VTable.Add((ErrorDescribeSlot, ErrorDescriber(anyError, null, null)));
        foreach (var (slot, _, type) in ErrorSlots(anyError)) {
            anyError.VTable.Add((slot, ErrorSlotDefault(anyError, slot, type)));
        }

        return anyError;
    }

    /// <summary>The subclass of <c>any Error</c>'s class that holds an error of one type.</summary>
    /// <remarks>
    ///     Its slots for Error's requirements are the error type's own, or
    ///     its defaults, where the type conforms; one thrown that does not
    ///     keeps the base class's, which describe it by what it is.
    /// </remarks>
    internal ClassType ErrorBox(SilType error) {
        var name = $"any$Error$of${Sanitise(error.ToString())}";
        if (_module.FindType(name) is ClassType existing) {
            return existing;
        }

        var anyError = AnyErrorType();
        // The error's type is the box's type argument, as an existential's
        // box's is: what 'type(of: error)' reads (sable_box_value_metadata).
        var box = _module.AddType(new ClassType(name) { Superclass = anyError, TypeArguments = [error] });
        box.Fields.Add(("value", error));
        box.VTable.Add((ErrorDescribeSlot, ErrorDescriber(anyError, box, error)));

        var concrete = _types.FirstOrDefault(pair => error.Equals(pair.Value)).Key switch {
            NamedTypeSymbol named => named,
            OptionalTypeSymbol optional => Core.Optional.Construct([optional.UnderlyingType]),
            _ => null,
        };
        var conforms = concrete is not null && concrete.Satisfies(Core.Error);
        var inherited = anyError.VTable.Skip(1).ToList();
        var slots = ErrorSlots(anyError);
        for (var i = 0; i < slots.Count; i++) {
            var own = conforms ? ErrorSlotWitness(anyError, box, concrete!, error, slots[i]) : null;
            box.VTable.Add((slots[i].Slot, own ?? inherited[i].Function));
        }

        return box;
    }

    List<(string Slot, Symbol Member, FunctionType Type)>? _errorSlots;

    /// <summary>
    ///     The requirements of Error, and of the protocols it refines, that
    ///     <c>any Error</c> answers: each a slot of its class's vtable, called
    ///     with the object first, as a method is.
    /// </summary>
    internal List<(string Slot, Symbol Member, FunctionType Type)> ErrorSlots(ClassType anyError) {
        if (_errorSlots is not null) {
            return _errorSlots;
        }

        _errorSlots = [];
        var seen = new HashSet<string>();
        var visited = new HashSet<NamedTypeSymbol>();
        var slots = new List<(string, Symbol, FunctionType)>();

        void Collect(NamedTypeSymbol protocol) {
            if (!visited.Add(protocol.OriginalDefinition)) {
                return;
            }

            var self = protocol.SelfTypeParameter;
            foreach (var member in protocol.OriginalDefinition.Members) {
                if (member.ContainingSymbol is not NamedTypeSymbol) {
                    continue;
                }

                switch (member) {
                    case FunctionSymbol { IsStatic: false, FunctionKind: FunctionKind.Ordinary, TypeParameters.IsEmpty: true, IsMutating: false, ThrownType: null } method
                        when method.Parameters.All(p => p.Modifier == ParameterModifier.None && !p.Type.Equals(self)) && !method.ReturnType.Equals(self)
                             && seen.Add(SlotName(method)): {
                        var parameters = method.Parameters.Select(p => Lower(p.Type)).ToList();
                        if (parameters.All(static p => p is not null) && Lower(method.ReturnType) is { } result) {
                            slots.Add((SlotName(method), method, new FunctionType([anyError, ..parameters!], result)));
                        }

                        break;
                    }

                    case FieldSymbol { IsStatic: false } field when !field.Type.Equals(self) && seen.Add(GetterSlot(field)): {
                        if (Lower(field.Type) is { } type) {
                            slots.Add((GetterSlot(field), field, new FunctionType([anyError], type)));
                        }

                        break;
                    }
                }
            }

            foreach (var inherited in protocol.OriginalDefinition.BaseTypes.OfType<NamedTypeSymbol>().Where(static b => b.TypeKind == TypeKind.Protocol)) {
                Collect(inherited);
            }
        }

        Collect(Core.Error);
        _errorSlots = slots;
        return slots;
    }

    /// <summary>
    ///     What a slot of <c>any Error</c> does for the base class, and for an
    ///     error whose type does not conform: a description is the error
    ///     written out, as the describe slot writes it; a source is none.
    /// </summary>
    string ErrorSlotDefault(ClassType anyError, string slot, FunctionType type) {
        var name = $"$error.{slot}";
        if (_module.Find(name) is not null) {
            return name;
        }

        var function = new SilFunction(name, type);
        _module.Add(function);
        var s = new Synthesis(function);
        var self = s.Block.AddArgument(anyError);
        for (var i = 1; i < type.Parameters.Length; i++) {
            s.Block.AddArgument(type.Parameters[i]);
        }

        if (type.Result == SilType.String) {
            var describer = s.Emit(new ClassMethodInst(self, ErrorDescribeSlot, ErrorDescriberType(anyError)));
            s.Emit(new ReturnInst(s.Emit(new ApplyInst(describer, SilType.String, self, s.Int(0)))));
        } else if (type.Result is EnumType optional && optional.CaseIndex("None") is var none and >= 0) {
            s.Emit(new ReturnInst(s.Emit(new EnumInst(optional, none, null))));
        } else {
            s.Emit(new CondFailInst(s.Emit(new IntegerLiteralInst(SilType.Bool, 1)), $"'{slot}' of an error that is not an Error"));
            s.Emit(new UnreachableInst());
        }

        return name;
    }

    /// <summary>
    ///     A slot of <c>any Error</c> for an error of a type that conforms:
    ///     the entry of the type's witness table for the requirement (#299),
    ///     given the error where its object holds it, and the type's
    ///     metadata. Null when there is none.
    /// </summary>
    string? ErrorSlotWitness(ClassType anyError, ClassType box, NamedTypeSymbol concrete, SilType value, (string Slot, Symbol Member, FunctionType Type) slot) {
        var name = $"$error.{slot.Slot}${Sanitise(value.ToString())}";
        if (_module.Find(name) is not null) {
            return name;
        }

        if (Core.Error.DeclaringSyntax.FirstOrDefault() is not { } at || slot.Member.ContainingSymbol is not NamedTypeSymbol protocol
            || WitnessTable(concrete, value, protocol, at) is not { } table
            || table.Entries.FirstOrDefault(e => e.Requirement == slot.Slot) is not { Function: { } witness, Type: { } type }
            || type.Parameters.Length != slot.Type.Parameters.Length + 1 || type.Thrown is not null || type.Result != slot.Type.Result) {
            return null;
        }

        var function = new SilFunction(name, slot.Type);
        _module.Add(function);
        var s = new Synthesis(function);
        var self = s.Block.AddArgument(anyError);
        var arguments = new List<SilValue>();
        for (var i = 1; i < slot.Type.Parameters.Length; i++) {
            arguments.Add(s.Block.AddArgument(slot.Type.Parameters[i]));
        }

        // The error where its object holds it, seen as the witness's Self.
        var held = s.Emit(new RefElementAddrInst(s.Emit(new UncheckedRefCastInst(self, box)), 0));
        var opened = s.Emit(new LoadBorrowInst(s.Emit(new BuiltinInst("opaque_cast", SilType.Address(type.Parameters[0]), held))));
        var result = s.Emit(new ApplyInst(s.Emit(new FunctionRefInst(witness, type)), type.Result, [opened, ..arguments, s.Emit(new MetadataInst(value))]));
        s.Emit(new EndBorrowInst(opened));
        s.Emit(new ReturnInst(slot.Type.Result == SilType.Unit ? null : result));
        return name;
    }

    /// <summary>
    ///     The witness table of <c>any Error</c>'s conformance to a protocol
    ///     Error refines (<c>any Debug</c>), for an <c>any Error</c> put in an
    ///     existential of it: each entry takes the error object where the box
    ///     holds it, and calls the slot of its vtable by the same name. Null
    ///     when a requirement the existential can call has no such slot.
    /// </summary>
    internal SilWitnessTable? ErrorTable(ClassType anyError, NamedTypeSymbol protocol) {
        var name = QualifiedName(protocol);
        if (_module.FindWitnessTable(anyError, name) is { } existing) {
            return existing;
        }

        var table = new SilWitnessTable(anyError, name);
        foreach (var refined in Bases(protocol.OriginalDefinition)) {
            table.Bases.Add(QualifiedName(refined));
            if (ErrorTable(anyError, refined) is null) {
                return null;
            }
        }

        foreach (var (key, member) in GenericRequirements(protocol)) {
            var type = GenericEntryType(protocol, member);
            if (type is null || ErrorSlots(anyError).FirstOrDefault(s => s.Slot == key) is not { Type: { } slotType }
                || type.Thrown is not null || type.Result != slotType.Result || type.Parameters.Length != slotType.Parameters.Length + 1) {
                if (type is not null && ReachableThroughExistential(member, type)) {
                    return null;
                }

                table.Entries.Add((key, null, null));
                continue;
            }

            var witness = $"$witness$any$Error${key}";
            if (_module.Find(witness) is null) {
                var function = new SilFunction(witness, type);
                _module.Add(function);
                var s = new Synthesis(function);
                var self = s.Block.AddArgument(type.Parameters[0]);
                var arguments = type.Parameters.Skip(1).SkipLast(1).Select(p => (SilValue)s.Block.AddArgument(p)).ToList();
                s.Block.AddArgument(SilType.Metadata);
                var error = s.Emit(new LoadInst(s.Emit(new BuiltinInst("opaque_address", SilType.Address(anyError), self))));
                var method = s.Emit(new ClassMethodInst(error, key, slotType));
                var result = s.Emit(new ApplyInst(method, slotType.Result, [error, ..arguments]));
                s.Emit(new DestroyValueInst(error));
                s.Emit(new ReturnInst(slotType.Result == SilType.Unit ? null : result));
            }

            table.Entries.Add((key, witness, type));
        }

        _module.AddWitnessTable(table);
        return table;
    }
    /// <summary>The structural unions lowered so far: <c>A | B</c> is an enum with a case for each.</summary>
    readonly HashSet<EnumType> _unions = [];

    internal bool IsUnion(SilType type) => type is EnumType enumType && _unions.Contains(enumType);

    /// <summary><c>A | B</c>: an enum whose cases are named for, and carry, the types it joins.</summary>
    EnumType? LowerUnion(StructuralUnionTypeSymbol union) {
        var cases = new List<SilType>();
        foreach (var member in union.Cases) {
            if (Lower(member) is not { } lowered || cases.Contains(lowered)) {
                return null;
            }

            cases.Add(lowered);
        }

        // In the order of their types' names, as a union of types known only at
        // run time orders them (sable_structural_union_type, #297).
        cases.Sort(static (a, b) => string.CompareOrdinal(TypeMetadata.DisplayName(a), TypeMetadata.DisplayName(b)));
        var name = "Union$" + string.Join("$or$", cases.Select(static c => Sanitise(c.ToString())));
        if (_module.FindType(name) is EnumType existing) {
            return existing;
        }

        var enumType = Begin(union, new EnumType(name) { IsUnion = true, DisplayName = string.Join(" | ", cases.Select(TypeMetadata.DisplayName)) });
        foreach (var lowered in cases) {
            enumType.Cases.Add((UnionCaseName(lowered), lowered));
        }

        _unions.Add(enumType);
        return HoldsItself(enumType) ? null : enumType;
    }

    /// <summary>
    ///     A union's case is named for the type it carries — but a name must
    ///     start with a letter to be read back, and an array's or a tuple's
    ///     starts with a bracket.
    /// </summary>
    static string UnionCaseName(SilType payload) {
        var name = Sanitise(payload.ToString());
        return char.IsLetter(name[0]) ? name : "_" + name;
    }

    EnumType? LowerRecursiveUnion(RecursiveUnionTypeSymbol recursive, StructuralUnionTypeSymbol union) {
        var name = "Union$" + Sanitise(QualifiedName(recursive.Alias));
        if (_module.FindType(name) is EnumType existing) {
            return existing;
        }

        var enumType = Begin(recursive, new EnumType(name) { IsUnion = true });
        _unions.Add(enumType);
        foreach (var member in union.Cases) {
            if (Lower(member) is not { } lowered || enumType.Cases.Exists(c => Equals(c.Payload, lowered))) {
                return null;
            }

            enumType.Cases.Add((UnionCaseName(lowered), lowered));
        }

        return HoldsItself(enumType) ? null : enumType;
    }

    /// <summary>
    ///     An owned error, as the error type wanted where it goes: put in a
    ///     union that has a case for it, boxed as <c>any Error</c>, or — where
    ///     the binder knows only that type can arrive, as a 'rethrows' call
    ///     that throws less than its signature does — taken back out, with a
    ///     trap should anything else turn up. Each case of a union is converted
    ///     on its own. The instructions go into <paramref name="block" />,
    ///     which ends as the block the converted error is in; null when there
    ///     is no conversion between the two.
    /// </summary>
    internal SilValue? ConvertError(SilFunction function, ref SilBlock block, SilValue error, SilType to) {
        var from = error.Type;
        if (from == to) {
            return error;
        }

        // A union, case by case, each converted and the results joined.
        if (IsUnion(from) && from is EnumType union) {
            var joined = function.AddBlock();
            var result = joined.AddArgument(to);
            var targets = new List<(int, SilBlock)>();
            for (var i = 0; i < union.Cases.Count; i++) {
                var target = function.AddBlock();
                var payload = target.AddArgument(union.Cases[i].Payload!, !union.Cases[i].Payload!.IsTrivial);
                targets.Add((i, target));

                SilValue owned = payload.Type.IsTrivial ? payload : target.Append(new CopyValueInst(payload));
                if (!from.IsTrivial) {
                    target.Append(new DestroyValueInst(error));
                }

                var current = target;
                if (ConvertError(function, ref current, owned, to) is not { } converted) {
                    return null;
                }

                current.Append(new BranchInst(joined, [converted]));
            }

            block.Append(new SwitchEnumInst(error, targets, null));
            block = joined;
            return result;
        }

        // Into a union with a case for it.
        if (IsUnion(to) && to is EnumType into && into.Cases.FindIndex(c => c.Payload == from) is var index and >= 0) {
            return block.Append(new EnumInst(into, index, error));
        }

        var anyError = AnyErrorType();

        // One unspecialised code knows only by its metadata: boxed by its
        // type's eraser, made where the type is known (#297).
        if (to == anyError && from is OpaqueType) {
            // One that is an 'any Error' already is itself.
            ErrorEraser(anyError);
            return block.Append(new BuiltinInst("opaque_erase_error", anyError, error));
        }

        if (to == anyError) {
            var box = ErrorBox(from);
            var boxed = block.Append(new AllocRefInst(box));
            block.Append(new StoreInst(error, block.Append(new RefElementAddrInst(boxed, 0)), StoreKind.Init));
            var upcast = block.Append(new CopyValueInst(block.Append(new UpcastInst(boxed, anyError))));
            block.Append(new DestroyValueInst(boxed));
            return upcast;
        }

        // Out of 'any Error': into a union, trying each case's box in turn;
        // or as the one type it must be.
        if (from == anyError) {
            var cases = IsUnion(to) ? ((EnumType)to).Cases.Select(static c => c.Payload!).ToList() : [to];
            var joined = function.AddBlock();
            var result = joined.AddArgument(to);
            foreach (var wanted in cases) {
                var box = ErrorBox(wanted);
                var isA = block.Append(new BuiltinInst($"class_is:{box.Name}", SilType.Bool, error));
                var yes = function.AddBlock();
                var no = function.AddBlock();
                block.Append(new CondBranchInst(isA, yes, [], no, []));

                var unboxed = yes.Append(new LoadInst(yes.Append(new RefElementAddrInst(yes.Append(new UncheckedRefCastInst(error, box)), 0))));
                yes.Append(new DestroyValueInst(error));
                var current = yes;
                if (ConvertError(function, ref current, unboxed, to) is not { } converted) {
                    return null;
                }

                current.Append(new BranchInst(joined, [converted]));
                block = no;
            }

            block.Append(new CondFailInst(block.Append(new IntegerLiteralInst(SilType.Bool, 1)), $"an error that is not {to}"));
            block.Append(new UnreachableInst());
            block = joined;
            return result;
        }

        return null;
    }

    /// <summary>
    ///     A function value as a function type that throws where it does not,
    ///     or throws another error, or is async where it is not: code that
    ///     calls it, converting what it throws — async, a frame that never
    ///     suspends. Its context is a box holding the function converted.
    /// </summary>
    internal (string Name, FunctionType Type)? ConversionThunk(ThickFunctionType from, ThickFunctionType to) {
        var name = $"$convert${Sanitise(from.ToString())}$to${Sanitise(to.ToString())}";
        var code = to.Implementation;
        if (_module.Find(name) is not null) {
            return (name, code);
        }

        var source = from.Signature;
        if (!source.Parameters.SequenceEqual(to.Signature.Parameters) || source.Result != to.Signature.Result
            || source.Thrown is not null && to.Signature.Thrown is null || source.IsAsync && !to.Signature.IsAsync) {
            return null;
        }

        var thunk = new SilFunction(name, code);
        var entry = thunk.AddBlock();
        var arguments = to.Signature.Parameters.Select((p, i) => (SilValue)entry.AddArgument(p)).ToList();
        var context = entry.AddArgument(SilType.Object);
        var box = BoxType(from);
        // Borrowed from the box, which outlives the call: no retain and
        // release of the function's own context on every call through it.
        var callee = entry.Append(new LoadBorrowInst(entry.Append(new RefElementAddrInst(entry.Append(new UncheckedRefCastInst(context, box)), 0))));
        if (source.Thrown is null) {
            var result = entry.Append(new ApplyInst(callee, source.Result, [..arguments]));
            entry.Append(new EndBorrowInst(callee));
            entry.Append(new ReturnInst(source.Result == SilType.Unit ? null : result));
        } else {
            var normal = thunk.AddBlock();
            var result = source.Result == SilType.Unit ? null : normal.AddArgument(source.Result);
            var failed = thunk.AddBlock();
            var error = failed.AddArgument(source.Thrown);
            entry.Append(new TryApplyInst(callee, source.Result, [..arguments], normal, failed));
            normal.Append(new EndBorrowInst(callee));
            normal.Append(new ReturnInst(result));
            failed.Append(new EndBorrowInst(callee));
            var current = failed;
            if (ConvertError(thunk, ref current, error, to.Signature.Thrown!) is not { } converted) {
                return null;
            }

            current.Append(new ThrowInst(converted));
        }

        thunk.RemoveUnreachableBlocks();
        _module.Add(thunk);
        return (name, code);
    }

    /// <summary>
    ///     Where a requirement called on an existential is met (#299): which
    ///     of its tables holds it (<paramref name="Part" />), the bases from
    ///     that protocol to the one that declares it, that protocol as the
    ///     existential sees it — an instance of a generic one with the
    ///     existential's arguments — its entry there, and the entry's type.
    ///     The type is null for a requirement the generic calling convention
    ///     cannot pass yet.
    /// </summary>
    internal sealed record ExistentialRequirement(int Part, ImmutableArray<int> Path, NamedTypeSymbol Protocol, int Entry, Symbol Member, FunctionType? Type);

    /// <summary>The requirement of an existential's protocols by its key (<c>area</c>, <c>get$name</c>), or null when none of them, nor what they refine, has it.</summary>
    internal ExistentialRequirement? RequirementOf(StructType layout, string key) {
        var parts = LayoutProtocols(layout);
        ExistentialRequirement? Search(int part, NamedTypeSymbol protocol, ImmutableArray<int> path, int depth) {
            var requirements = GenericRequirements(protocol);
            if (requirements.FindIndex(r => r.Key == key) is var entry and >= 0) {
                return new(part, path, protocol, entry, requirements[entry].Member, GenericEntryType(protocol, requirements[entry].Member));
            }

            if (depth > 32) {
                return null;
            }

            var through = TypeSubstitution.For(protocol);
            var index = 0;
            foreach (var refined in Bases(protocol.OriginalDefinition)) {
                if (through.Apply(refined) is NamedTypeSymbol instance && Search(part, instance, path.Add(index), depth + 1) is { } found) {
                    return found;
                }

                index++;
            }

            return null;
        }

        for (var i = 0; i < parts.Count; i++) {
            if (Search(i, parts[i], [], 0) is { } found) {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    ///     Where an existential of some protocols holds the table of another
    ///     (<c>any Sub</c> as <c>any Base</c>): which of its tables, then the
    ///     bases from there. Null when none of its protocols is or refines it.
    /// </summary>
    internal (int Part, ImmutableArray<int> Path)? TablePath(List<NamedTypeSymbol> parts, NamedTypeSymbol wanted) {
        ImmutableArray<int>? Search(NamedTypeSymbol protocol, ImmutableArray<int> path, int depth) {
            if (ReferenceEquals(protocol.OriginalDefinition, wanted.OriginalDefinition)) {
                return path;
            }

            if (depth > 32) {
                return null;
            }

            var through = TypeSubstitution.For(protocol);
            var index = 0;
            foreach (var refined in Bases(protocol.OriginalDefinition)) {
                if (through.Apply(refined) is NamedTypeSymbol instance && Search(instance, path.Add(index), depth + 1) is { } found) {
                    return found;
                }

                index++;
            }

            return null;
        }

        for (var i = 0; i < parts.Count; i++) {
            if (Search(parts[i], [], 0) is { } path) {
                return (i, path);
            }
        }

        return null;
    }

    /// <summary>The table that holds a requirement, from the existential's table of its protocol: through that one's bases.</summary>
    internal static SilValue RequirementTable(Func<SilInstruction, SilValue> emit, SilValue table, ExistentialRequirement requirement) {
        foreach (var step in requirement.Path) {
            table = emit(new BuiltinInst($"witness_base:{step}", SilType.WitnessTable, table));
        }

        return table;
    }

    /// <summary>
    ///     What <c>d.shout</c> calls with <c>d: any Shout</c>: a closure whose
    ///     context is a box holding the existential, which calls the
    ///     requirement's entry of the existential's table with the value
    ///     where the box holds it, its arguments, and the metadata.
    /// </summary>
    internal string ExistentialRequirementThunk(StructType layout, string key, ExistentialRequirement requirement, ThickFunctionType closure) {
        var thunkName = $"{layout.Name}${Sanitise(key)}$bound";
        if (_module.Find(thunkName) is not null) {
            return thunkName;
        }

        var type = requirement.Type!;
        var thunk = new SilFunction(thunkName, closure.Implementation);
        var entry = thunk.AddBlock();
        var arguments = closure.Signature.Parameters.Select(p => (SilValue)entry.AddArgument(p)).ToList();
        var context = entry.AddArgument(SilType.Object);
        var holder = entry.Append(new UncheckedRefCastInst(context, BoxType(layout)));
        var existential = entry.Append(new LoadBorrowInst(entry.Append(new RefElementAddrInst(holder, 0))));
        var box = entry.Append(new StructExtractInst(existential, 0));
        var metadata = entry.Append(new StructExtractInst(existential, 1));
        var table = RequirementTable(i => entry.Append(i), entry.Append(new StructExtractInst(existential, ExistentialTables + requirement.Part)), requirement);
        var callee = entry.Append(new BuiltinInst($"witness_generic:{requirement.Entry}", type, table));
        var self = entry.Append(new BuiltinInst("box_project", type.Parameters[0], box, metadata));
        SilValue[] all = [self, ..arguments, metadata];

        if (type.Thrown is null) {
            var result = entry.Append(new ApplyInst(callee, type.Result, all));
            entry.Append(new EndBorrowInst(existential));
            entry.Append(new ReturnInst(type.Result == SilType.Unit ? null : result));
        } else {
            var normal = thunk.AddBlock();
            var result = type.Result == SilType.Unit ? null : normal.AddArgument(type.Result);
            var failed = thunk.AddBlock();
            var error = failed.AddArgument(type.Thrown);
            entry.Append(new TryApplyInst(callee, type.Result, all, normal, failed));
            normal.Append(new EndBorrowInst(existential));
            normal.Append(new ReturnInst(result));
            failed.Append(new EndBorrowInst(existential));
            failed.Append(new ThrowInst(error));
        }

        _module.Add(thunk);
        return thunkName;
    }
    /// <summary>
    ///     What <c>s.get</c> calls: a closure whose context is a box holding
    ///     the receiver, which it passes as <c>self</c> to the method, through
    ///     the vtable when a subclass may override it.
    /// </summary>
    internal (string Name, FunctionType Type) BoundMethodThunk(FunctionSymbol method, string name, FunctionType type, TypeSubstitution? owner = null) {
        var dynamic = IsDynamic(method);
        // Through a slot in another convention: a function of this one that calls it.
        if (dynamic && owner is not null && HierarchiesOnce && (DynamicRoot(method) is not null || IsSlotOnce(SlotRoot(method)))) {
            name = DispatchShim(method, name, type, owner);
            dynamic = false;
        }

        var thunkName = $"{name}$bound{(dynamic ? "$dynamic" : "")}";
        var closure = new ThickFunctionType(new([..type.Parameters.Skip(1)], type.Result, type.Thrown));
        var code = closure.Implementation;
        if (_module.Find(thunkName) is not null) {
            return (thunkName, code);
        }

        var thunk = new SilFunction(thunkName, code);
        var entry = thunk.AddBlock();
        var arguments = closure.Signature.Parameters.Select(p => (SilValue)entry.AddArgument(p)).ToList();
        var context = entry.AddArgument(SilType.Object);
        var box = entry.Append(new UncheckedRefCastInst(context, BoxType(type.Parameters[0])));
        var held = entry.Append(new RefElementAddrInst(box, 0));
        // The caller keeps the context, which nothing writes, until this
        // returns: the method borrows the receiver from it. An async one is
        // awaited in a frame of its own.
        var borrows = !type.Parameters[0].IsTrivial && !method.IsAsync;
        SilInstruction self = borrows ? new LoadBorrowInst(held) : new LoadInst(held);
        entry.Append(self);
        SilValue callee = dynamic
            ? entry.Append(new ClassMethodInst(self, VTableSlot(method), type))
            : entry.Append(new FunctionRefInst(name, type));
        SilValue[] all = [self, ..arguments];

        void Release(SilBlock block) {
            if (borrows) {
                block.Append(new EndBorrowInst(self));
            } else if (!self.Type.IsTrivial) {
                block.Append(new DestroyValueInst(self));
            }
        }

        if (type.Thrown is null) {
            var result = entry.Append(new ApplyInst(callee, type.Result, all));
            Release(entry);
            entry.Append(new ReturnInst(type.Result == SilType.Unit ? null : result));
        } else {
            var normal = thunk.AddBlock();
            var result = type.Result == SilType.Unit ? null : normal.AddArgument(type.Result);
            var failed = thunk.AddBlock();
            var error = failed.AddArgument(type.Thrown);
            entry.Append(new TryApplyInst(callee, type.Result, all, normal, failed));
            Release(normal);
            normal.Append(new ReturnInst(result));
            Release(failed);
            failed.Append(new ThrowInst(error));
        }

        _module.Add(thunk);
        return (thunkName, code);
    }

    readonly Dictionary<string, int> _closureCounts = new(StringComparer.Ordinal);

    /// <summary>A function declared in another's body.</summary>
    internal static bool IsLocalFunction(FunctionSymbol function) =>
        function is { FunctionKind: FunctionKind.Ordinary, Name.Length: > 0, ContainingSymbol: FunctionSymbol };

    /// <summary>A function as a value of function type: what a local function that captures is.</summary>
    internal static FunctionTypeSymbol TypeOf(FunctionSymbol function) =>
        new([..function.Parameters.Select(static p => p.Type)], function.ReturnType, function.IsAsync, function.ThrownType);

    /// <summary>Each local function lowering has seen, by its symbol.</summary>
    readonly Dictionary<FunctionSymbol, BoundLocalFunction> _localFunctionNodes = new(ReferenceEqualityComparer.Instance);

    readonly Dictionary<FunctionSymbol, bool> _localCaptures = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    ///     Whether a local function captures anything — a local, a parameter
    ///     or <c>self</c> of a function around it, or another local function
    ///     that does. One that captures nothing is a plain function.
    /// </summary>
    internal bool Captures(FunctionSymbol local) {
        if (_localCaptures.TryGetValue(local, out var known)) {
            return known;
        }

        if (!_localFunctionNodes.TryGetValue(local, out var node) || node.Body is null) {
            return false;
        }

        _localCaptures[local] = false;
        var captures = CapturesOf(local, node.Body, []).Count > 0;
        _localCaptures[local] = captures;
        return captures;
    }

    /// <summary><c>M.outer$helper</c>, and a count after it for a second 'helper' in the same function.</summary>
    internal string LocalFunctionName(string function, string local) {
        var name = $"{function}${local}";
        for (var i = 1; _module.Find(name) is not null; i++) {
            name = $"{function}${local}{i}";
        }

        return name;
    }

    /// <summary>
    ///     A function declared in a body. Named, and in the module, before its
    ///     body is lowered — a call to it, its own among them, finds it there.
    /// </summary>
    /// <summary>The bodies of the generic local functions declared so far, which each call instantiates.</summary>
    readonly Dictionary<FunctionSymbol, (BoundBlock Body, SourceText Source, ClosureInfo? Closure)> _localGenerics = [];

    /// <summary>
    ///     The context each instance of a generic local function that
    ///     captures is given, by the instance's name: what the instance of
    ///     the function around it that asked for it captured for it.
    /// </summary>
    readonly Dictionary<string, ClosureInfo> _localGenericClosures = new(StringComparer.Ordinal);

    /// <summary>
    ///     A generic local function, met in the body of an instance of the
    ///     function around it: its name is for that instance, so the
    ///     instances its calls ask for are that instance's own. One that
    ///     captures has its captures built into a context there, once, which
    ///     every instance takes as a last argument, as a closure's code does.
    /// </summary>
    internal void DeclareLocalGeneric(FunctionSymbol symbol, BoundBlock body, string name, SourceText source, ClosureInfo? closure = null) {
        _names[symbol] = name;
        _localGenerics[symbol] = (body, source, closure);
    }

    /// <summary>The context class of a generic local function that captures, as the instance of the function around it being lowered made it.</summary>
    internal ClassType? LocalGenericContext(FunctionSymbol symbol) =>
        _localGenerics.TryGetValue(symbol, out var local) ? local.Closure?.Context : null;

    internal void LowerLocalFunction(FunctionSymbol symbol, BoundBlock body, string name, FunctionType type, SourceText source, TypeSubstitution substitution, ClosureInfo? closure = null) {
        var syntax = symbol.DeclaringSyntax.FirstOrDefault();
        var function = new SilFunction(name, type) {
            SourceFile = source.FileName,
            Line = syntax is null ? 0 : source.GetLinePosition(syntax.Span.Start).Line,
            Inline = InlineOf(symbol),
        };
        function.ParameterNames.AddRange(symbol.Parameters.Select(static p => p.Name));

        // A generic one's instance does not rename it: a call in the
        // instance still asks for an instance of the declaration's, where a
        // recursive call would otherwise name an instance of this instance,
        // and so on without end.
        if (!_localGenerics.ContainsKey(symbol)) {
            _names[symbol] = name;
        }

        _module.Add(function);

        var reported = _diagnostics.Count;
        new FunctionLowering(this, symbol, function, source, substitution, closure).Lower(body);
        function.RemoveUnreachableBlocks();
        MoveChecker.Check(function, source, _diagnostics);
        _sources[function] = source;
        if (TreeOf(source) is { } tree) {
            Place(reported, tree);
        }
    }

    internal string ClosureName(string function) {
        var count = _closureCounts.GetValueOrDefault(function);
        _closureCounts[function] = count + 1;
        return $"{function}$closure{count}";
    }

    /// <summary>
    ///     One thing a closure captured: where in its context it is, whether
    ///     that is a box, and whether it holds an object without keeping it —
    ///     '[weak x]', '[unowned self]'.
    /// </summary>
    internal sealed record Capture(object Key, TypeSymbol Type, int Field, bool Boxed, BoundExpression? Initializer, CaptureKind Kind = CaptureKind.Default);

    /// <summary>
    ///     True for an initialiser that hands its work to another of its
    ///     type's own, <c>self.init(…)</c>: that one initialises the fields.
    /// </summary>
    static bool Delegates(BoundBlock body) =>
        Descendants(body).Any(static n => n is BoundCall { Function.FunctionKind: FunctionKind.Initializer, Receiver: BoundSelfAccess });

    /// <summary>The key a captured <c>self</c> is filed under.</summary>
    internal const string SelfKey = "self";

    static IEnumerable<BoundNode> Descendants(BoundNode node) {
        var pending = new Stack<BoundNode>();
        pending.Push(node);
        while (pending.Count > 0) {
            var next = pending.Pop();
            yield return next;
            foreach (var child in next.Children.Reverse()) {
                pending.Push(child);
            }
        }
    }

    /// <summary>
    ///     What a closure captures, in order of first use: the locals of the
    ///     functions around it by reference, through boxes; their parameters,
    ///     <c>self</c>, and what its capture list names, by value.
    /// </summary>
    internal List<Capture> CapturesOf(BoundClosure closure) => CapturesOf(closure.Symbol, closure.Body, closure.Captures, closure: true);

    /// <summary>
    ///     What a closure, or a local function, captures. A local function
    ///     that captures is itself captured, as its closure, by what calls it.
    ///     A closure captures a local that cannot change — a 'let', a loop's
    ///     element — by value, as '[x]' would: there is nothing a box could
    ///     share, and a context that holds no box is one allocation, not two.
    ///     So does a local function, of a 'let' that has its value where the
    ///     function is declared, and so made; one declared before a 'let' it
    ///     reads has its value reads it through a box.
    /// </summary>
    internal List<Capture> CapturesOf(FunctionSymbol symbol, BoundBlock body, ImmutableArray<BoundCapture> listedCaptures, bool closure = false) {
        var inner = Descendants(body)
            .Select(static n => n switch {
                    BoundClosure c => c.Symbol,
                    BoundLocalFunction l => l.Symbol,
                    _ => null,
                }
            )
            .OfType<Symbol>()
            .ToHashSet();
        inner.Add(symbol);

        var listed = listedCaptures.Where(static c => c.Initializer is not null).ToDictionary(static c => c.Symbol, static c => c);
        var captures = new List<Capture>();
        var seen = new HashSet<object>();

        void Add(object key, TypeSymbol type, bool boxed, BoundExpression? initializer, CaptureKind kind = CaptureKind.Default) {
            if (seen.Add(key)) {
                captures.Add(new(key, type, captures.Count, boxed, initializer, kind));
            }
        }

        foreach (var node in Descendants(body)) {
            switch (node) {
                case BoundCall { Function: var called } when IsLocalFunction(called) && !inner.Contains(called) && Captures(called):
                    Add(called, TypeOf(called), false, null);
                    break;

                case BoundFunctionReference { Function: var referenced } when IsLocalFunction(referenced) && !inner.Contains(referenced) && Captures(referenced):
                    Add(referenced, TypeOf(referenced), false, null);
                    break;

                case BoundLocalAccess access when !inner.Contains(access.Local.ContainingSymbol!):
                    if (listed.TryGetValue(access.Local, out var listedCapture)) {
                        Add(access.Local, access.Local.Type, false, listedCapture.Initializer, listedCapture.CaptureKind);
                    } else if ((closure || HasValueBefore(access.Local, symbol)) && access.Local is { IsMutable: false, IsAsyncLet: false }
                               && Lower(access.Local.Type) is { } held && !IsMoveOnly(held)) {
                        Add(access.Local, access.Local.Type, false, new BoundLocalAccess(access.Syntax, access.Local, access.Local.Type));
                    } else {
                        Add(access.Local, access.Local.Type, true, null);
                    }

                    break;

                // An inout parameter is captured as a local is, by its box:
                // the function it belongs to keeps the parameter's value in
                // one for the length of the call, and writes it back to the
                // caller's storage on the way out.
                case BoundParameterAccess access when !inner.Contains(access.Parameter.ContainingSymbol!):
                    Add(access.Parameter, access.Parameter.Type, access.Parameter.Modifier == ParameterModifier.InOut, null);
                    break;

                // A mutating member's self, which some closure changes, is
                // shared in a box with those that cannot outlive the call;
                // any other closure, which only reads it, holds a copy.
                case BoundSelfAccess self:
                    Add(SelfKey, self.Type, symbol.IsContainedClosure && self is { IsMutable: true, Type.IsReferenceType: false } && OutermostFunction(symbol) is { SelfChangedInClosure: true }, null);
                    break;
            }
        }

        return captures;
    }

    /// <summary>The function a closure or local function is written in, through any around it.</summary>
    static FunctionSymbol OutermostFunction(FunctionSymbol function) {
        while (function.ContainingSymbol is FunctionSymbol outer) {
            function = outer;
        }

        return function;
    }

    /// <summary>
    ///     True when a local has its value by the time a local function is
    ///     declared: its declaration, with its initialiser — or a binding,
    ///     which always has one — comes first in the source, around it.
    /// </summary>
    static bool HasValueBefore(LocalSymbol local, Symbol function) {
        if (local.DeclaringSyntax is not [var declaration] || function.DeclaringSyntax is not [var declared] || declaration.Span.End > declared.Span.Start) {
            return false;
        }

        return declaration is not LocalDeclarationStatementSyntax statement || statement.Declarator.Initializer is not null;
    }

    /// <summary>
    ///     The inout parameters of a function that some closure inside it
    ///     captures: each is copied into a box when the call begins, and
    ///     the box's value back into the caller's storage at every exit.
    /// </summary>
    internal List<ParameterSymbol> BoxedInoutParameters(FunctionSymbol function, BoundNode body) {
        var boxed = new List<ParameterSymbol>();
        foreach (var node in Descendants(body)) {
            var captures = node switch {
                BoundClosure closure => CapturesOf(closure),
                BoundLocalFunction local when local.Body is not null && Captures(local.Symbol) => CapturesOf(local.Symbol, local.Body, []),
                _ => [],
            };
            foreach (var capture in captures) {
                if (capture is { Boxed: true, Key: ParameterSymbol parameter } && ReferenceEquals(parameter.ContainingSymbol, function) && !boxed.Contains(parameter)) {
                    boxed.Add(parameter);
                }
            }
        }

        return boxed;
    }

    /// <summary>The locals of a function that some closure inside it captures by reference: they live in boxes.</summary>
    internal HashSet<LocalSymbol> BoxedLocals(Symbol function, BoundNode body) {
        var nested = Descendants(body).Where(static n => n is BoundClosure or BoundLocalFunction { Body: not null }).ToList();
        foreach (var local in nested.OfType<BoundLocalFunction>()) {
            _localFunctionNodes[local.Symbol] = local;
        }

        var boxed = new HashSet<LocalSymbol>();
        foreach (var node in nested) {
            var captures = node switch {
                BoundClosure closure => CapturesOf(closure),
                BoundLocalFunction local when Captures(local.Symbol) => CapturesOf(local.Symbol, local.Body!, []),
                _ => [],
            };
            foreach (var capture in captures) {
                if (capture is { Boxed: true, Key: LocalSymbol local } && ReferenceEquals(local.ContainingSymbol, function)) {
                    boxed.Add(local);
                }
            }
        }

        return boxed;
    }

    /// <summary>
    ///     A named function as a closure's code: a thunk that takes the
    ///     context it does not need, and calls the function.
    /// </summary>
    internal string Thunk(string name, FunctionType type) {
        var thunkName = name + "$thunk";
        if (_module.Find(thunkName) is not null) {
            return thunkName;
        }

        var thunk = new SilFunction(thunkName, new ThickFunctionType(type).Implementation);
        var entry = thunk.AddBlock();
        var arguments = type.Parameters.Select(p => (SilValue)entry.AddArgument(p)).ToArray();
        entry.AddArgument(SilType.Object);
        var reference = entry.Append(new FunctionRefInst(name, type));
        if (type.Thrown is null) {
            var result = entry.Append(new ApplyInst(reference, type.Result, arguments));
            entry.Append(new ReturnInst(type.Result == SilType.Unit ? null : result));
        } else {
            var normal = thunk.AddBlock();
            var result = type.Result == SilType.Unit ? null : normal.AddArgument(type.Result);
            var failed = thunk.AddBlock();
            var error = failed.AddArgument(type.Thrown);
            entry.Append(new TryApplyInst(reference, type.Result, arguments, normal, failed));
            normal.Append(new ReturnInst(result));
            failed.Append(new ThrowInst(error));
        }

        _module.Add(thunk);
        return thunkName;
    }

    /// <summary>A property's getter's requirement key, in a witness table.</summary>
    internal static string GetterSlot(FieldSymbol field) => "get$" + field.Name;

    /// <summary>A property's setter's requirement key, in a witness table.</summary>
    internal static string SetterSlot(FieldSymbol field) => "set$" + field.Name;

    /// <summary>
    ///     The class every existential's box is a subclass of: what the
    ///     existential holds, whatever the value's type, and what its
    ///     witnesses are given.
    /// </summary>
    internal ClassType ExistentialBoxRoot() =>
        _module.FindType("any$box") as ClassType ?? _module.AddType(new ClassType("any$box"));

    /// <summary>
    ///     The box an existential keeps a value of one type in: a subclass of
    ///     <see cref="ExistentialBoxRoot" />, so that which type it holds is
    ///     the box's class — what <c>is</c> and <c>as?</c> ask at run time.
    /// </summary>
    internal ClassType ExistentialBox(SilType value) {
        var name = $"any$box$of${Sanitise(value.ToString())}";
        if (_module.FindType(name) is ClassType existing) {
            return existing;
        }

        var box = _module.AddType(new ClassType(name) { Superclass = ExistentialBoxRoot(), TypeArguments = [value] });
        box.Fields.Add(("value", value));
        return box;
    }

    /// <summary>
    ///     The protocols an existential's constraint names: one, or each part
    ///     of a composition, <c>any A &amp; B</c>. Null when a part is not a
    ///     protocol.
    /// </summary>
    internal static List<NamedTypeSymbol>? ExistentialProtocols(TypeSymbol constraint) {
        var protocols = new List<NamedTypeSymbol>();
        foreach (var part in CompositionTypeSymbol.PartsOf(constraint)) {
            if (part is not NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol) {
                return null;
            }

            protocols.Add(protocol);
        }

        return protocols;
    }

    /// <summary>
    ///     The struct <c>any P</c> lowers to (docs/06 §6.2, #299): the box
    ///     holding the value, the value's metadata, then for each protocol —
    ///     one, or each part of <c>any A &amp; B</c> — a pointer to the
    ///     conformance's witness table. Copying an existential shares the box;
    ///     a call through it gives the witness the value where the box holds
    ///     it, and the metadata; one that changes the value first makes the
    ///     box the existential's own (copy on write). Its size does not grow
    ///     with the protocol's requirements.
    /// </summary>
    internal StructType ExistentialLayout(List<NamedTypeSymbol> protocols) {
        var name = protocols.Count == 0 ? "any$Any" : "any$" + string.Join("$and$", protocols.Select(TypeName));
        if (_module.FindType(name) is StructType existing) {
            _layoutProtocols.TryAdd(existing, protocols);
            return existing;
        }

        var layout = new StructType(name);
        layout.Fields.Add(("box", ExistentialBoxRoot()));
        layout.Fields.Add(("type", SilType.Metadata));
        foreach (var protocol in protocols) {
            layout.Fields.Add((TypeName(protocol), SilType.WitnessTable));
        }

        // Of an instance of a generic protocol, its type arguments are the
        // existential's, as its metadata lists them.
        if (protocols.Exists(static p => p.IsConstructed)
            && protocols.SelectMany(static p => p.TypeArguments).Select(Lower).ToList() is var arguments && arguments.TrueForAll(static a => a is not null && !SilType.MentionsOpaque(a))) {
            layout.TypeArguments = [..arguments!];
            layout.DisplayName = "any " + string.Join(" & ", protocols.Select(p => !p.IsConstructed ? QualifiedName(p)
                : $"{QualifiedName(p)}<{string.Join(", ", p.TypeArguments.Select(a => TypeMetadata.DisplayName(Lower(a)!)))}>"));
        }

        _module.AddType(layout);
        _layoutProtocols[layout] = protocols;
        return layout;
    }

    /// <summary>The field of an existential that holds its first protocol's witness table: after the box and the metadata.</summary>
    internal const int ExistentialTables = 2;

    /// <summary>The protocols an existential's layout holds a table for, in its order.</summary>
    internal List<NamedTypeSymbol> LayoutProtocols(StructType layout) => _layoutProtocols[layout];
    /// <summary>True for what <c>any P</c> lowers to.</summary>
    internal bool IsExistentialLayout(SilType type) => type is StructType layout && _layoutProtocols.ContainsKey(layout);

    /// <summary>A type's field by name: its own, or a superclass's.</summary>
    static FieldSymbol? FindField(NamedTypeSymbol concrete, string name) {
        for (var type = (NamedTypeSymbol?)concrete.OriginalDefinition; type is not null; type = SuperclassOf(type)?.OriginalDefinition) {
            if (type.Members.OfType<FieldSymbol>().FirstOrDefault(f => f.Name == name && !f.IsStatic) is { } found) {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    ///     A method or property a protocol extension adds that meets none of
    ///     the protocol's requirements, nor those of a protocol it refines: it
    ///     has no witness, only its own body.
    /// </summary>
    static bool IsExtensionOnly(Symbol member) {
        // A property's getter is its property's: 'hashValue''s 'get' is no
        // requirement, and not a conformer's 'get()' either.
        if (member is FunctionSymbol { FunctionKind: FunctionKind.Accessor, ContainingSymbol: FieldSymbol property }) {
            return IsExtensionOnly(property);
        }

        // ... and a subscript's its subscript's: 'xs[^1]''s 'get' is not a
        // conformer's 'get(_ key:)'.
        if (member is FunctionSymbol { FunctionKind: FunctionKind.Accessor, ContainingSymbol: FunctionSymbol { FunctionKind: FunctionKind.Subscript } subscript }) {
            return IsExtensionOnly(subscript);
        }

        if (member.ContainingSymbol is not ExtensionSymbol { ExtendedType: NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol }) {
            return false;
        }

        bool Meets(Symbol requirement) =>
            requirement.ContainingSymbol is NamedTypeSymbol
            && requirement.Name == member.Name
            && (requirement, member) switch {
                (FunctionSymbol r, FunctionSymbol m) =>
                    (r.IsStatic == m.IsStatic || r.FunctionKind == FunctionKind.Operator) && r.Parameters.Length == m.Parameters.Length
                    // 'append(contentsOf:)' is not 'append(element:)'.
                    && (r.FunctionKind == FunctionKind.Operator || SameLabels(r, m)),
                (FieldSymbol r, FieldSymbol m) => r.IsStatic == m.IsStatic,
                _ => false,
            };

        var visited = new HashSet<NamedTypeSymbol>();
        bool Requires(NamedTypeSymbol current) =>
            visited.Add(current.OriginalDefinition)
            && (current.OriginalDefinition.Members.Any(Meets)
                || current.OriginalDefinition.BaseTypes.OfType<NamedTypeSymbol>().Any(b => b.TypeKind == TypeKind.Protocol && Requires(b)));

        return !Requires(protocol);
    }

    /// <summary>The member of a concrete type that implements a protocol's method: its own, or a superclass's.</summary>
    /// <summary>True when two functions' parameters have the same argument labels.</summary>
    /// <summary>
    ///     Whether the candidate takes the type itself wherever the requirement
    ///     takes <c>Self</c>: of overloads with one name and the same labels —
    ///     BigInt's <c>&gt;&gt;</c> by a BigInt, which BinaryInteger requires,
    ///     and by an Int — the one that meets the requirement.
    /// </summary>
    static bool SelfParametersMatch(FunctionSymbol candidate, FunctionSymbol requirement, NamedTypeSymbol conformer) {
        if (Owner(requirement) is not { TypeKind: TypeKind.Protocol } protocol) {
            return true;
        }

        for (var i = 0; i < requirement.Parameters.Length; i++) {
            var wanted = requirement.Parameters[i].Type;
            if (!wanted.Equals(protocol) && !ReferenceEquals(wanted, protocol.OriginalDefinition.SelfTypeParameter)) {
                continue;
            }

            if (candidate.Parameters[i].Type is not NamedTypeSymbol given || !ReferenceEquals(given.OriginalDefinition, conformer.OriginalDefinition)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Whether the candidate's parameters could be the requirement's, as
    ///     far as the types both name go: of overloads with one name and the
    ///     same labels — <c>random(in: Range&lt;Self&gt;)</c> and
    ///     <c>random(in: ClosedRange&lt;Self&gt;)</c> — the one that meets the
    ///     requirement. A type parameter, or Self, may be any type.
    /// </summary>
    static bool ParametersFit(FunctionSymbol candidate, FunctionSymbol requirement) {
        // An array is not a type with a name: 'init(_ bytes: U8[:])' is not
        // the 'init(_ key: PublicKey)' it is declared beside.
        static bool Fits(TypeSymbol given, TypeSymbol wanted) =>
            (given, wanted) is not ((ArrayTypeSymbol, NamedTypeSymbol { TypeKind: not TypeKind.Protocol }) or (NamedTypeSymbol { TypeKind: not TypeKind.Protocol }, ArrayTypeSymbol))
            && FitsNamed(given, wanted);

        static bool FitsNamed(TypeSymbol given, TypeSymbol wanted) =>
            given is not NamedTypeSymbol { TypeKind: not TypeKind.Protocol } g
            || wanted is not NamedTypeSymbol { TypeKind: not TypeKind.Protocol } w
            || ReferenceEquals(g.OriginalDefinition, w.OriginalDefinition)
            && g.TypeArguments.Length == w.TypeArguments.Length
            && g.TypeArguments.Zip(w.TypeArguments).All(static pair => Fits(pair.First, pair.Second));

        return candidate.Parameters.Length == requirement.Parameters.Length
            && candidate.Parameters.Zip(requirement.Parameters).All(static pair => Fits(pair.First.Type, pair.Second.Type));
    }

    static bool SameLabels(FunctionSymbol a, FunctionSymbol b) =>
        a.Parameters.Length == b.Parameters.Length
        && a.Parameters.Zip(b.Parameters).All(static p => (p.First.Label ?? p.First.Name) == (p.Second.Label ?? p.Second.Name));

    /// <summary>
    ///     The member of an array that implements a protocol's method: one an
    ///     array extension declares (those of the array's own kind first, then
    ///     the slice's), or else the protocol's default.
    /// </summary>
    internal FunctionSymbol? ArrayImplementation(ArrayTypeSymbol array, FunctionSymbol requirement) {
        if (IsExtensionOnly(requirement)) {
            return requirement;
        }

        // An operator is static wherever it is written; a requirement's says nothing.
        var own = _compilation.ArrayMembersNamed(array, requirement.Name).OfType<FunctionSymbol>()
            .Where(f => (f.IsStatic == requirement.IsStatic || requirement.FunctionKind == FunctionKind.Operator) && SameLabels(f, requirement))
            .OrderBy(f => !ParametersFit(f, requirement))
            .FirstOrDefault();
        if (own is not null) {
            return own;
        }

        if (Owner(requirement) is { TypeKind: TypeKind.Protocol } protocol) {
            return protocol.OriginalDefinition.Members.OfType<FunctionSymbol>()
                .Where(f =>
                    f.ContainingSymbol is ExtensionSymbol
                    && f.Name == requirement.Name
                    && f.IsStatic == requirement.IsStatic
                    && f.Parameters.Length == requirement.Parameters.Length)
                .OrderBy(f => !ParametersFit(f, requirement))
                .FirstOrDefault();
        }

        return null;
    }

    /// <summary>The instance property an array extension gives arrays of this kind by this name.</summary>
    internal FieldSymbol? ArrayProperty(ArrayTypeSymbol array, string name) =>
        _compilation.ArrayMembersNamed(array, name).OfType<FieldSymbol>().FirstOrDefault(static f => !f.IsStatic);

    /// <summary>
    ///     Whether the method is another requirement's, told apart from this
    ///     one by its labels: a type's 'sleep(until:)' does not implement
    ///     'sleep(for:)', for which it takes the protocol's default.
    /// </summary>
    static bool ImplementsOverload(FunctionSymbol method, FunctionSymbol requirement, NamedTypeSymbol? protocol) =>
        !SameLabels(method, requirement)
        && protocol is { TypeKind: TypeKind.Protocol }
        && protocol.Members.OfType<FunctionSymbol>().Any(other =>
            !ReferenceEquals(other, requirement)
            && other.ContainingSymbol is NamedTypeSymbol
            && other.Name == requirement.Name
            && other.IsStatic == requirement.IsStatic
            && SameLabels(other, method));

    /// <summary>Whether an extension of the protocol has a default for the requirement with its labels.</summary>
    static bool HasLabelledDefault(NamedTypeSymbol? protocol, FunctionSymbol requirement) =>
        protocol is { TypeKind: TypeKind.Protocol }
        && protocol.Members.OfType<FunctionSymbol>().Any(f =>
            f.ContainingSymbol is ExtensionSymbol
            && f.Name == requirement.Name
            && f.IsStatic == requirement.IsStatic
            && SameLabels(f, requirement));

    internal static FunctionSymbol? Implementation(NamedTypeSymbol concrete, FunctionSymbol requirement) {
        // docs/03 §3.2: a protocol extension's member that is not a
        // requirement is chosen by the static type, not looked up on the
        // concrete one — a 'g' the type declares itself does not replace it.
        if (IsExtensionOnly(requirement)) {
            return requirement;
        }

        var required = Owner(requirement)?.OriginalDefinition;
        for (var type = (NamedTypeSymbol?)concrete.OriginalDefinition; type is not null; type = SuperclassOf(type)?.OriginalDefinition) {
            // An operator is static wherever it is written; a requirement's
            // says nothing. 'fn Printable.render()' implements Printable's
            // requirement and no other's, and is the one for it even beside
            // an ordinary 'render'. Overloads told apart by their labels are
            // told apart here the same way.
            var found = type.Members.OfType<FunctionSymbol>()
                .Where(f =>
                    f.Name == requirement.Name
                    && (f.IsStatic == requirement.IsStatic || requirement.FunctionKind == FunctionKind.Operator)
                    && f.Parameters.Length == requirement.Parameters.Length
                    && Owner(f) is { TypeKind: not TypeKind.Protocol }
                    && (f.ExplicitProtocol is null
                        || ReferenceEquals((f.ExplicitProtocol as NamedTypeSymbol)?.OriginalDefinition, required))
                    && !ImplementsOverload(f, requirement, required))
                .OrderBy(f => f.ExplicitProtocol is null)
                .ThenBy(f => !SameLabels(f, requirement))
                .ThenBy(f => !SelfParametersMatch(f, requirement, type))
                .ThenBy(f => !ParametersFit(f, requirement))
                .FirstOrDefault();
            // One of other labels is another method where the protocol has a
            // default of the requirement's own: F64's 'scaled(places:)' is not
            // FloatingPoint's 'scaled(byPowerOfTwo:)'. (An operator's operands have no labels.)
            if (found is not null && (found.FunctionKind == FunctionKind.Operator || SameLabels(found, requirement) || !HasLabelledDefault(required, requirement))) {
                return found;
            }
        }

        // None of its own: the default an extension of the protocol gives —
        // the one with the requirement's labels and type parameters, where
        // the extension overloads the name ('clamping(wideValue:)' beside
        // the generic 'clamping(_:)').
        if (Owner(requirement) is { TypeKind: TypeKind.Protocol } protocol) {
            return protocol.OriginalDefinition.Members.OfType<FunctionSymbol>()
                    .Where(f =>
                        f.ContainingSymbol is ExtensionSymbol
                        && f.Name == requirement.Name
                        && f.IsStatic == requirement.IsStatic
                        && f.Parameters.Length == requirement.Parameters.Length)
                    .OrderBy(f => !SameLabels(f, requirement))
                    .ThenBy(f => f.TypeParameters.Length != requirement.TypeParameters.Length)
                    .ThenBy(f => !ParametersFit(f, requirement))
                    .FirstOrDefault()
                ?? RefinedDefault(concrete, protocol.OriginalDefinition, requirement);
        }

        return null;
    }

    /// <summary>
    ///     A property an extension of the requirement's protocol gives by
    ///     default — <c>extension Error { var source: (any Error)? =&gt; none }</c>
    ///     — or of a protocol refining it that the type conforms to, or failing
    ///     that of any protocol it conforms to, as the conformance checker
    ///     finds it.
    /// </summary>
    static FieldSymbol? DefaultProperty(NamedTypeSymbol concrete, FieldSymbol requirement) {
        FieldSymbol? unrelated = null;
        static FieldSymbol? In(NamedTypeSymbol protocol, FieldSymbol requirement) =>
            protocol.OriginalDefinition.Members.OfType<FieldSymbol>().FirstOrDefault(f =>
                f.ContainingSymbol is ExtensionSymbol && f.Name == requirement.Name && f.IsStatic == requirement.IsStatic && f.IsComputed);

        if (requirement.ContainingSymbol is not NamedTypeSymbol required) {
            return null;
        }

        if (In(required, requirement) is { } own) {
            return own;
        }

        var seen = new HashSet<NamedTypeSymbol>();
        var pending = new Queue<NamedTypeSymbol>();
        for (var type = (NamedTypeSymbol?)concrete.OriginalDefinition; type is not null; type = SuperclassOf(type)?.OriginalDefinition) {
            foreach (var conformed in type.BaseTypes.OfType<NamedTypeSymbol>()) {
                pending.Enqueue(conformed.OriginalDefinition);
            }
        }

        while (pending.Count > 0) {
            var protocol = pending.Dequeue();
            if (protocol.TypeKind != TypeKind.Protocol || !seen.Add(protocol)) {
                continue;
            }

            if (In(protocol, requirement) is { } found) {
                if (protocol.Satisfies(required.OriginalDefinition)) {
                    return found;
                }

                unrelated ??= found;
            }

            foreach (var inherited in protocol.BaseTypes.OfType<NamedTypeSymbol>()) {
                pending.Enqueue(inherited.OriginalDefinition);
            }
        }

        return unrelated;
    }

    /// <summary>
    ///     The default a protocol that refines the requirement's gives, among
    ///     those the type conforms to: <c>extension Error { fn
    ///     debugDescription() }</c> meets Debug's requirement for every Error.
    ///     Failing that, one any protocol the type conforms to gives, as the
    ///     conformance checker finds it: <c>extension Int: Summable {}</c> has
    ///     its <c>+</c> from AdditiveArithmetic's extension, the built-in one.
    /// </summary>
    static FunctionSymbol? RefinedDefault(NamedTypeSymbol concrete, NamedTypeSymbol required, FunctionSymbol requirement) {
        FunctionSymbol? unrelated = null;
        var seen = new HashSet<NamedTypeSymbol>();
        var pending = new Queue<NamedTypeSymbol>();
        for (var type = (NamedTypeSymbol?)concrete.OriginalDefinition; type is not null; type = SuperclassOf(type)?.OriginalDefinition) {
            foreach (var conformed in type.BaseTypes.OfType<NamedTypeSymbol>()) {
                pending.Enqueue(conformed.OriginalDefinition);
            }
        }

        while (pending.Count > 0) {
            var protocol = pending.Dequeue();
            if (protocol.TypeKind != TypeKind.Protocol || !seen.Add(protocol)) {
                continue;
            }

            if (!ReferenceEquals(protocol, required)
                && protocol.Members.OfType<FunctionSymbol>()
                    .Where(f =>
                        f.ContainingSymbol is ExtensionSymbol
                        && f.Name == requirement.Name
                        && (f.IsStatic == requirement.IsStatic || requirement.FunctionKind == FunctionKind.Operator)
                        && f.Parameters.Length == requirement.Parameters.Length
                        && SameLabels(f, requirement))
                    .OrderBy(f => !ParametersFit(f, requirement))
                    .FirstOrDefault() is { } found) {
                if (protocol.Satisfies(required)) {
                    return found;
                }

                unrelated ??= found;
            }

            foreach (var inherited in protocol.BaseTypes.OfType<NamedTypeSymbol>()) {
                pending.Enqueue(inherited.OriginalDefinition);
            }
        }

        return unrelated;
    }

    /// <summary>
    ///     The concrete types of the program that meet a constraint, in the
    ///     order the declarations are in, but each class before the classes it
    ///     derives from: the most specific witnesses are found first. Generic
    ///     types are not among them; only their instances are concrete.
    /// </summary>
    List<NamedTypeSymbol> ConformingTypes(TypeSymbol constraint) {
        var found = new List<NamedTypeSymbol>();

        void Visit(IEnumerable<Symbol> members) {
            foreach (var type in members.OfType<NamedTypeSymbol>()) {
                if (!type.TypeParameters.IsEmpty) {
                    continue;
                }

                if (type.TypeKind is TypeKind.Struct or TypeKind.Class or TypeKind.Actor or TypeKind.Enum or TypeKind.Union or TypeKind.Builtin
                    && type.Satisfies(constraint)) {
                    found.Add(type);
                }

                Visit(type.Members);
            }
        }

        foreach (var module in _compilation.Modules) {
            Visit(module.Members);
        }

        static int Depth(NamedTypeSymbol type) {
            var depth = 0;
            for (var ancestor = SuperclassOf(type); ancestor is not null; ancestor = SuperclassOf(ancestor)) {
                depth++;
            }

            return depth;
        }

        return [..found.Select(static (t, i) => (t, i)).OrderByDescending(static p => Depth(p.t)).ThenBy(static p => p.i).Select(static p => p.t)];
    }

    /// <summary>
    ///     The instances of the program's generic types that meet a constraint
    ///     one of whose protocols is an instance of a generic one, where that
    ///     says what each of their type arguments is: <c>Sql&lt;Order&gt;</c>,
    ///     for an <c>any Repo&lt;Order&gt;</c> and <c>Sql&lt;T&gt;: Repo&lt;T&gt;</c>.
    ///     The declarations alone say which, so every unit finds the same.
    /// </summary>
    List<NamedTypeSymbol> ConformingInstances(TypeSymbol constraint) {
        var found = new List<NamedTypeSymbol>();
        var instances = CompositionTypeSymbol.PartsOf(constraint).OfType<NamedTypeSymbol>().Where(static p => p.TypeKind == TypeKind.Protocol && p.IsConstructed).ToList();

        void Visit(IEnumerable<Symbol> members) {
            foreach (var type in members.OfType<NamedTypeSymbol>()) {
                if (type.TypeParameters.IsEmpty) {
                    Visit(type.Members);
                } else if (type.TypeKind is TypeKind.Struct or TypeKind.Class or TypeKind.Actor or TypeKind.Union && InstanceFor(type) is { } instance) {
                    found.Add(instance);
                }
            }
        }

        NamedTypeSymbol? InstanceFor(NamedTypeSymbol type) {
            var self = type.Construct([..type.TypeParameters]);
            var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
            foreach (var protocol in instances) {
                if (self.SupertypeOf(protocol) is not { } declared || !TypeSubstitution.Unify(declared, protocol, type.TypeParameters, map)) {
                    return null;
                }
            }

            if (!type.TypeParameters.All(map.ContainsKey)) {
                return null;
            }

            var substitution = new TypeSubstitution(map);
            if (!type.TypeParameters.All(p => p.Constraints.All(c => map[p].Satisfies(substitution.Apply(c))))) {
                return null;
            }

            var instance = type.Construct([..type.TypeParameters.Select(p => map[p])]);
            return instance.Satisfies(constraint) ? instance : null;
        }

        // A protocol declared in a function is conformed to by what is declared beside it.
        foreach (var protocol in instances) {
            if (protocol.OriginalDefinition.ContainingSymbol is FunctionSymbol { LocalTypes: var locals }) {
                Visit(locals);
            }
        }

        foreach (var module in _compilation.Modules) {
            Visit(module.Members);
        }

        return found;
    }

    /// <summary>
    ///     What <c>e as? T</c> does with the box of an existential: the value
    ///     as <typeparamref name="T" />, or none. To a concrete type, the box
    ///     is asked whether its class is that type's box — or, for an object,
    ///     the box of a class it derives from, and the object then whether it
    ///     is one. To another existential, <c>any Q</c>, the conformance
    ///     registry is asked for the witness table of what the box holds
    ///     (<see cref="RegistryCast" />); an instance of a generic protocol,
    ///     whose slots may be variant, has each type of the program that
    ///     conforms tried in turn instead, the first the box holds given Q's
    ///     witnesses for it. Made once per type, the same in every unit of a
    ///     program, since every unit sees every declaration.
    /// </summary>
    internal (string Name, FunctionType Type)? ExistentialCast(TypeSymbol target, SyntaxNode at) {
        if (Lower(target) is not { } wanted || Lower(new OptionalTypeSymbol(target)) is not EnumType optional) {
            return null;
        }

        var root = ExistentialBoxRoot();
        var name = $"$cast$to${Sanitise(wanted.ToString())}";
        var type = new FunctionType([root], optional);
        if (_module.Find(name) is not null) {
            return (name, type);
        }

        var cast = new SilFunction(name, type);
        var entry = cast.AddBlock();
        var box = entry.AddArgument(root);
        var joined = cast.AddBlock();
        joined.Append(new ReturnInst(joined.AddArgument(optional)));
        var some = optional.CaseIndex("Some");
        var none = optional.CaseIndex("None");

        var layout = target is ExistentialTypeSymbol ? wanted as StructType : null;
        if (layout is not null && TabledProtocols(layout) is { } tabled) {
            RegistryCast(cast, entry, box, joined, optional, layout, tabled, at);
            _module.Add(cast);
            return (name, type);
        }

        // A class that others derive from: whatever object the box holds, of
        // whichever class, is asked whether it is one.
        if (layout is null && wanted is ClassType && target is NamedTypeSymbol { TypeKind: TypeKind.Class } open && IsInherited(open.OriginalDefinition)) {
            var held = entry.Append(new BuiltinInst("box_metadata", SilType.Metadata, box));
            var derives = entry.Append(new BuiltinInst("metadata_is_subtype", SilType.Bool, held, entry.Append(new MetadataInst(wanted))));
            var one = cast.AddBlock();
            var other = cast.AddBlock();
            entry.Append(new CondBranchInst(derives, one, [], other, []));
            var reference = one.Append(new LoadInst(one.Append(new RefElementAddrInst(one.Append(new UncheckedRefCastInst(box, ExistentialBox(wanted))), 0))));
            one.Append(new BranchInst(joined, [one.Append(new EnumInst(optional, some, reference))]));
            other.Append(new BranchInst(joined, [other.Append(new EnumInst(optional, none, null))]));
            _module.Add(cast);
            return (name, type);
        }

        List<TypeSymbol> candidates = target switch {
            ExistentialTypeSymbol existential when layout is not null => [..ConformingTypes(existential.Constraint), ..ConformingInstances(existential.Constraint)],
            ExistentialTypeSymbol => [],
            var concrete => [concrete],
        };

        var block = entry;
        foreach (var candidate in candidates) {
            if (Lower(candidate) is not { } value) {
                continue;
            }

            // Q's witness tables for the type, made first: a type whose
            // witnesses cannot all be made yet is passed over, and says
            // nothing about it.
            var candidateBox = ExistentialBox(value);
            if (layout is not null) {
                var complete = true;
                foreach (var protocol in LayoutProtocols(layout)) {
                    complete &= WitnessTable(candidate, value, protocol, at) is { } table
                                && MissingWitness(table, candidate.SupertypeOf(protocol) ?? protocol) is null;
                }

                if (!complete) {
                    continue;
                }
            }

            // The existential of Q a box of the type is: the box, the type's
            // metadata, and its tables — constants where the type is known.
            SilValue Witnessed(SilBlock at, SilValue own) {
                var metadata = at.Append(new MetadataInst(value));
                var tables = LayoutProtocols(layout!).Select(p => at.Append(new BuiltinInst($"witness_table_known:{QualifiedName(p)}", SilType.WitnessTable, metadata)));
                return at.Append(new StructInst(layout!, [own, metadata, ..tables]));
            }

            // Where a box may hold it: its own; and an object, the box of
            // any class it derives from, holding it as that class.
            var holders = new List<TypeSymbol> { candidate };
            if (value is ClassType && candidate is NamedTypeSymbol named) {
                for (var ancestor = SuperclassOf(named); ancestor is not null; ancestor = SuperclassOf(ancestor)) {
                    holders.Add(ancestor);
                }
            }

            foreach (var holderType in holders) {
                if (Lower(holderType) is not { } heldAs) {
                    continue;
                }

                var holder = ExistentialBox(heldAs);
                var isA = block.Append(new BuiltinInst($"class_is:{holder.Name}", SilType.Bool, box));
                var yes = cast.AddBlock();
                var no = cast.AddBlock();
                block.Append(new CondBranchInst(isA, yes, [], no, []));
                block = no;

                var current = yes;
                SilValue produced;
                if (heldAs == value && layout is not null) {
                    // Its own box, shared, with Q's tables.
                    produced = Witnessed(current, current.Append(new CopyValueInst(box)));
                } else {
                    var held = current.Append(new LoadInst(current.Append(new RefElementAddrInst(current.Append(new UncheckedRefCastInst(box, holder)), 0))));
                    SilValue item = held;
                    if (heldAs != value) {
                        // An object held as a class it derives from: one of the candidate's class?
                        var isOne = current.Append(new BuiltinInst($"class_is:{((ClassType)value).Name}", SilType.Bool, held));
                        var match = cast.AddBlock();
                        var other = cast.AddBlock();
                        current.Append(new CondBranchInst(isOne, match, [], other, []));
                        other.Append(new DestroyValueInst(held));
                        other.Append(new BranchInst(no, []));
                        current = match;
                        item = current.Append(new CopyValueInst(current.Append(new UncheckedRefCastInst(held, (ClassType)value))));
                        current.Append(new DestroyValueInst(held));
                    }

                    if (layout is null) {
                        produced = item;
                    } else {
                        // Put in a box of its own type, which Q's witnesses for it are given.
                        var fresh = current.Append(new AllocRefInst(candidateBox));
                        current.Append(new StoreInst(item, current.Append(new RefElementAddrInst(fresh, 0)), StoreKind.Init));
                        var own = current.Append(new CopyValueInst(current.Append(new UpcastInst(fresh, root))));
                        current.Append(new DestroyValueInst(fresh));
                        produced = Witnessed(current, own);
                    }
                }

                current.Append(new BranchInst(joined, [current.Append(new EnumInst(optional, some, produced))]));
            }
        }

        block.Append(new BranchInst(joined, [block.Append(new EnumInst(optional, none, null))]));
        _module.Add(cast);
        return (name, type);
    }

    /// <summary>
    ///     The type, among a concrete type and its superclasses, that declares
    ///     a method — with the type arguments the concrete type gives it.
    /// </summary>
    static NamedTypeSymbol? ConstructedOwner(NamedTypeSymbol concrete, FunctionSymbol method) {
        var declaring = Owner(method)?.OriginalDefinition;
        for (var type = (NamedTypeSymbol?)concrete; type is not null; type = SuperclassOf(type)) {
            if (ReferenceEquals(type.OriginalDefinition, declaring)) {
                return type;
            }
        }

        return null;
    }

    /// <summary>The cases of each union whose payload is boxed, by index.</summary>
    readonly Dictionary<EnumType, HashSet<int>> _indirect = new(ReferenceEqualityComparer.Instance);

    /// <summary>True when a case's payload is in a box: the box's one field holds it.</summary>
    internal bool IsIndirect(EnumType type, int index) => _indirect.TryGetValue(type, out var boxed) && boxed.Contains(index);

    static bool IsMarkedIndirect(NamedTypeSymbol union, string caseName) =>
        union.Members.OfType<UnionCaseSymbol>().FirstOrDefault(c => c.Name == caseName)?.HasAttribute("indirect") == true;

    /// <summary>True when a value of one type holds a value of another by value — through no reference.</summary>
    static bool Holds(SilType part, NominalType type) {
        var seen = new HashSet<SilType>();
        bool Reaches(SilType at) {
            if (ReferenceEquals(at, type)) {
                return true;
            }

            if (!seen.Add(at)) {
                return false;
            }

            return at switch {
                StructType structType => structType.Fields.Any(f => Reaches(f.Type)),
                TupleType tuple => tuple.Elements.Any(Reaches),
                EnumType enumType => enumType.Cases.Any(c => c.Payload is { } payload && Reaches(payload)),
                _ => false,
            };
        }

        return Reaches(part);
    }

    /// <summary>True when a type contains itself by value — through no reference — and so has no size.</summary>
    /// <summary>A value of this type cannot be copied: it is, or holds inline, a move-only one.</summary>
    static bool IsMoveOnly(SilType type) =>
        type switch {
            NominalType { IsNoncopyable: true } => true,
            TupleType tuple => tuple.Elements.Any(IsMoveOnly),
            _ => false,
        };

    /// <summary>What a field of a struct or a class holds: a weak or unowned one its object without keeping it.</summary>
    static SilType Stored(ReferenceKind kind, SilType type) =>
        (kind, type) switch {
            (ReferenceKind.Weak, EnumType optional) => new WeakStorageType(optional),
            (ReferenceKind.Unowned, ClassType referent) => new UnownedStorageType(referent),
            _ => type,
        };

    /// <summary>
    ///     A struct's field as a value — a weak or unowned one made from the
    ///     object, which the field holds without keeping alive. Consumes the
    ///     value, and gives one the struct takes.
    /// </summary>
    internal static SilValue IntoField(SilType field, SilValue value, Func<SilInstruction, SilValue> emit) {
        if (field is not (WeakStorageType or UnownedStorageType) || value.Type == field) {
            return value;
        }

        var made = emit(field is WeakStorageType ? new WeakCopyValueInst(value) : new UnownedCopyValueInst(value));
        emit(new DestroyValueInst(value));
        return made;
    }

    /// <summary>
    ///     What a field read out of a struct value reads as: the object a weak or
    ///     unowned field holds, retained (owned); any other field as extracted
    ///     (borrowed from the struct).
    /// </summary>
    internal static SilValue OutOfField(SilValue extracted, Func<SilInstruction, SilValue> emit) =>
        extracted.Type switch {
            WeakStorageType => emit(new StrongCopyWeakValueInst(extracted)),
            UnownedStorageType => emit(new StrongCopyUnownedValueInst(extracted)),
            _ => extracted,
        };

    static bool HoldsItself(NominalType type) {
        var seen = new HashSet<SilType>();
        bool Reaches(SilType part) {
            if (ReferenceEquals(part, type)) {
                return true;
            }

            if (!seen.Add(part)) {
                return false;
            }

            return part switch {
                StructType structType => structType.Fields.Any(f => Reaches(f.Type)),
                TupleType tuple => tuple.Elements.Any(Reaches),
                EnumType enumType => enumType.Cases.Any(c => c.Payload is { } payload && Reaches(payload)),
                _ => false, // a scalar, or something reached through a reference
            };
        }

        return type switch {
            StructType structType => structType.Fields.Any(f => Reaches(f.Type)),
            EnumType enumType => enumType.Cases.Any(c => c.Payload is { } payload && Reaches(payload)),
            _ => false,
        };
    }

    /// <summary><c>M.Point</c>; a constructed type adds its arguments: <c>M.Box$of$Int</c>.</summary>
    string TypeName(NamedTypeSymbol type) {
        var name = QualifiedName(type);
        if (!type.IsConstructed) {
            return name;
        }

        var arguments = type.TypeArguments.Select(a => Lower(a)?.ToString() ?? a.ToDisplayString());
        return $"{name}$of${string.Join("$", arguments.Select(Sanitise))}";
    }

    /// <summary>
    ///     The protocols a type declares it conforms to — where it is declared
    ///     and in its extensions that hold for every instance, or with
    ///     <paramref name="conditional" /> in its constrained ones too — with
    ///     those they refine, by qualified name: <c>M.Named</c>.
    /// </summary>
    static IEnumerable<string> DeclaredProtocols(NamedTypeSymbol type, bool conditional = false) {
        // Its base types have each extension's conformances too: a constrained one's are left out.
        var constrained = conditional ? [] : type.Extensions.Where(static e => e.IsConstrained).SelectMany(static e => e.Conformances).ToHashSet();
        var pending = new Queue<TypeSymbol>(type.BaseTypes.Where(b => !constrained.Contains(b)));
        foreach (var extension in type.Extensions.Where(static e => !e.IsConstrained)) {
            foreach (var conformance in extension.Conformances) {
                pending.Enqueue(conformance);
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0) {
            if (pending.Dequeue() is not NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol) {
                continue;
            }

            var name = QualifiedName(protocol);
            if (!seen.Add(name)) {
                continue;
            }

            yield return name;
            foreach (var refined in protocol.BaseTypes) {
                pending.Enqueue(refined);
            }
        }
    }

    /// <summary>
    ///     The protocols a generic type's constrained extensions conform it to
    ///     (<c>extension Box: P where T: P</c>), with those they refine, each
    ///     with what its type arguments must conform to, by index. One whose
    ///     requirements are not all a type parameter's conformance to a
    ///     protocol (<c>where T == Int</c>) is left out: no record says so.
    /// </summary>
    static IEnumerable<(string Protocol, ImmutableArray<(int Argument, string Protocol)> Requirements)> ConditionalProtocols(NamedTypeSymbol type) {
        var parameters = type.TypeParameters;
        foreach (var extension in type.Extensions.Where(static e => e.IsConstrained && e.SameTypeRequirements.IsEmpty)) {
            ImmutableArray<(int, string)>.Builder? requirements = ImmutableArray.CreateBuilder<(int, string)>();
            foreach (var requirement in extension.ConformanceRequirements) {
                var index = requirement.Subject is TypeParameterSymbol parameter ? parameters.IndexOf(parameter) : -1;
                if (index < 0 || requirement.Constraint is not NamedTypeSymbol { TypeKind: TypeKind.Protocol, TypeParameters.IsEmpty: true } protocol) {
                    requirements = null;
                    break;
                }

                requirements.Add((index, QualifiedName(protocol)));
            }

            if (requirements is null) {
                continue;
            }

            var pending = new Queue<TypeSymbol>(extension.Conformances);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (pending.Count > 0) {
                if (pending.Dequeue() is not NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol || !seen.Add(QualifiedName(protocol))) {
                    continue;
                }

                yield return (QualifiedName(protocol), requirements!.ToImmutable());
                foreach (var refined in protocol.BaseTypes) {
                    pending.Enqueue(refined);
                }
            }
        }
    }

    internal static IEnumerable<FieldSymbol> StoredFields(NamedTypeSymbol type) =>
        type.Members.OfType<FieldSymbol>().Where(static f => f is { IsStatic: false, IsComputed: false });

    internal void Report(ErrorCode code, Sable.Syntax.TextSpan span, string message) => _diagnostics.Error(code, span, message);

    internal void Report(Diagnostic diagnostic) => _diagnostics.Add(diagnostic);

    /// <summary>A stored property's initial value, bound: from the model of the file that declares it.</summary>
    internal BoundExpression? FieldInitializer(FieldSymbol field) {
        if (field.DeclaringSyntax.FirstOrDefault() is not { } syntax) {
            return null;
        }

        return _compilation.TreeOf(syntax) is { } tree ? BodyModel(_compilation.GetSemanticModel(tree), syntax).GetFieldInitializer(field) : null;
    }

    T? Unsupported<T>(SyntaxNode at, string what) where T : class {
        _diagnostics.Error(ErrorCode.ERR_NotLoweredYet, at.Span, $"SIL lowering does not handle {what} yet");        return null;
    }

    // =======================================================================

    /// <summary>What a closure's code knows of its context: its class, and where each capture is in it.</summary>
    internal sealed record ClosureInfo(ClassType? Context, Dictionary<object, Capture> Captures) {
        /// <summary>
        ///     In a closure compiled with unspecialised code (#297), what it
        ///     captured of a type parameter's type: each in an array of one in
        ///     its context, sized at run time, and copied where its code starts
        ///     into a slot of its own, which reads of it find.
        /// </summary>
        public List<(Capture Capture, SilType Type)> Indirect { get; init; } = [];
    }

    sealed partial class FunctionLowering(
        SilGen generator,
        FunctionSymbol symbol,
        SilFunction function,
        SourceText source,
        TypeSubstitution substitution,
        ClosureInfo? closure = null
    ) {
        /// <summary>In a closure's code, its context, as the class it is.</summary>
        SilValue? _context;

        /// <summary>This function's locals that closures capture by reference, each kept in a box.</summary>
        HashSet<LocalSymbol> _boxed = [];

        /// <summary>A type as this instance sees it: its type parameters replaced by what they stand for.</summary>
        /// <remarks>
        ///     Inside a generic type, <c>self</c> has the type's own definition
        ///     as its type, which names no arguments; this instance supplies them.
        /// </remarks>
        TypeSymbol Sub(TypeSymbol type) =>
            // In a protocol's default implementation, the protocol is Self.
            type is NamedTypeSymbol { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } self } && substitution.Lookup(self) is { } concrete
                ? concrete
                : type is NamedTypeSymbol { IsGenericDefinition: true } definition
            && definition.TypeParameters.All(p => substitution.Lookup(p) is not null)
                ? definition.Construct([..definition.TypeParameters.Select(p => substitution.Lookup(p)!)])
                : Substitute(substitution, type);

        SilType? LowerType(TypeSymbol type) => generator.Lower(Sub(type));

        /// <summary>Marks an expression whose value is <c>()</c>: something to have done, nothing to use.</summary>
        sealed class UnitValue : SilValue {
            public static readonly UnitValue Instance = new();
            public override SilType Type => SilType.Unit;
        }

        /// <summary>A loop, or a <c>switch</c>: a <c>break</c> leaves either, but a <c>continue</c> passes a switch, whose Continue is null.</summary>
        sealed record Loop(string? Label, SilBlock Break, SilBlock? Continue, int Scopes, int Temporaries);

        /// <summary>The case a <c>fallthrough</c> goes on to, and the defer scopes and temporaries open where it began.</summary>
        sealed record Fallthrough(SilBlock Target, int Scopes, int Temporaries);

        /// <summary>Where an error goes: a block taking it, and how many defer scopes and temporaries were open there.</summary>
        sealed record Handler(SilBlock Block, SilType Type, int Scopes, int Temporaries);

        readonly Dictionary<ParameterSymbol, SilValue> _parameters = [];
        readonly Dictionary<LocalSymbol, AllocStackInst> _slots = [];
        readonly List<AllocStackInst> _allocations = [];
        readonly List<Loop> _loops = [];
        readonly List<Handler> _handlers = [];

        /// <summary>For each <c>switch</c> case being lowered, innermost last: where its <c>fallthrough</c> goes.</summary>
        readonly List<Fallthrough?> _fallthroughs = [];

        /// <summary>The deferred bodies of each open scope, innermost last.</summary>
        /// <remarks>
        ///     A scope's cleanups are its deferred bodies and its locals'
        ///     slots, in the order they appeared; leaving the scope runs them
        ///     last first — so a local dies at the end of the block that
        ///     declared it, after any <c>defer</c> written below it.
        /// </remarks>
        readonly List<List<object>> _scopes = [];

        /// <summary>
        ///     Owned values an expression holds while it evaluates the rest of
        ///     itself: what a <c>throw</c> or <c>return</c> from inside it must
        ///     destroy on the way out.
        /// </summary>
        readonly List<SilValue> _temporaries = [];

        SilValue? _selfValue;
        SilValue? _selfAddress;

        /// <summary>True in the half of a class's initialiser that is given the object to initialise.</summary>
        bool _initializing;

        SilBlock? _current;

        /// <summary>The source position instructions emitted now belong to.</summary>
        SilLocation? _location;

        /// <summary>The lexical scope being lowered: 0 for the function's own, else one of its <see cref="SilFunction.Scopes" />.</summary>
        int _scope;

        /// <summary>The function's body, whose scope is the function's own.</summary>
        BoundBlock? _body;

        void Locate(SyntaxNode syntax) {
            var position = source.GetLinePosition(syntax.Span.Start);
            _location = new SilLocation(position.Line, position.Column, _scope);
        }

        SilBlock _entry = null!;
        bool _failed;

        CoreTypes Core => generator.Core;

        public void Lower(BoundBlock body) {
            _entry = function.AddBlock();
            var parameterTypes = function.Type.Parameters;
            var next = 0;

            // A generator's body is given the iterator it keeps its state in, first.
            if (Generator is not null) {
                _iterator = _entry.AddArgument(parameterTypes[next++]);
                if (Generator.Boxed) {
                    _leave = _entry.AddArgument(parameterTypes[next++]);
                }
            }

            // Initialising an object: it comes first, already allocated.
            _initializing = symbol.FunctionKind == FunctionKind.Initializer && function.Type.Result == SilType.Unit
                && parameterTypes.Length > 0 && parameterTypes[0] is ClassType && OwnerOf(symbol) is { TypeKind: TypeKind.Class or TypeKind.Actor };

            if (HasSelf(symbol) || _initializing) {
                var self = _entry.AddArgument(parameterTypes[next++]);
                if (Bridged is { OpaqueSelfValue: true }) {
                    _bridgeSelf = self;
                } else if (self.Type is AddressType) {
                    _selfAddress = self;
                } else {
                    _selfValue = self;
                }
            }

            foreach (var parameter in Bridged is { TakesNoParameters: true } ? [] : symbol.Parameters) {
                _parameters[parameter] = _entry.AddArgument(parameterTypes[next++]);
                if (Bridged is { } bridged && bridged.OpaqueParameters.Contains(parameter)) {
                    _bridgeParameters.Add((parameter, (SilArgument)_parameters[parameter]));
                }
            }

            // Unspecialised code's metadata and witness tables, after its own parameters.
            GenericParameters(parameterTypes, ref next);

            _current = _entry;
            BridgeArguments();

            // A move-only parameter the function owns lives in a slot, like a
            // local, so that moving it out is checked as a local's move is; so
            // does a 'consuming' one the body moves, so that 'move' hands on
            // the caller's value rather than a copy of it.
            for (var i = 0; i < symbol.Parameters.Length; i++) {
                if (!_parameters.TryGetValue(symbol.Parameters[i], out var parameterValue)) {
                    continue;
                }

                var argument = (SilArgument)parameterValue;
                if (function.Type.IsOwned(_entry.Arguments.IndexOf(argument))
                    && (argument.Type is NominalType { IsNoncopyable: true } || !argument.Type.IsTrivial && MovesParameter(body, symbol.Parameters[i]))) {
                    var slot = NewSlot(argument.Type, symbol.Parameters[i].Name);
                    Emit(new StoreInst(argument, slot, StoreKind.Assign));
                    _spilled[symbol.Parameters[i]] = slot;
                    _spilledArguments.Add(argument);
                    _parameters.Remove(symbol.Parameters[i]);
                }
            }

            _current = _entry;
            _boxed = generator.BoxedLocals(symbol, body);

            // An inout parameter a closure captures is copied in to a box the
            // closures share, and is that box's value for the rest of the
            // call; the exits copy it back out to the caller's storage.
            foreach (var parameter in generator.BoxedInoutParameters(symbol, body)) {
                if (!_parameters.TryGetValue(parameter, out var original) || LowerType(parameter.Type) is not { } boxedType) {
                    continue;
                }

                var box = Emit(new AllocRefInst(generator.BoxType(boxedType)));
                Emit(new StoreInst(Emit(new LoadInst(original)), Emit(new RefElementAddrInst(box, 0)), StoreKind.Assign));
                _parameterBoxes[parameter] = (box, original);
                _parameters[parameter] = Emit(new RefElementAddrInst(box, 0));
            }

            // A closure's code takes its context last: as any object, or, for
            // an instance of a generic local function, which is called
            // directly, as the class it is.
            if (closure is not null) {
                var context = _entry.AddArgument(next < parameterTypes.Length ? parameterTypes[next] : SilType.Object);
                if (closure.Context is { } contextType) {
                    _context = context.Type == contextType ? context : Emit(new UncheckedRefCastInst(context, contextType));
                }

                UnpackIndirectCaptures();

                // A mutating member's self, shared in a box: the closure
                // reads and changes it there, holding the box for its call.
                if (_context is not null && closure.Captures.TryGetValue(SelfKey, out var capturedSelf) && capturedSelf.Boxed) {
                    _selfBox = Emit(new LoadInst(Emit(new RefElementAddrInst(_context, capturedSelf.Field))));
                    _selfAddress = Emit(new RefElementAddrInst(_selfBox, 0));
                }
            } else if (symbol.SelfChangedInClosure && _selfAddress is { Type: AddressType { Pointee: var boxedSelf } } selfAddress) {
                // A closure inside changes self: for the call, self is a box
                // the closures share, moved in now and back out to the
                // caller's storage at every exit: moved, not copied, so that
                // what it holds has no second reference a change would have
                // to copy for, and a move-only self may be.
                var box = Emit(new AllocRefInst(generator.BoxType(boxedSelf)));
                Emit(new StoreInst(Emit(new LoadInst(selfAddress, boxedSelf.IsTrivial ? null : LoadKind.Take)), Emit(new RefElementAddrInst(box, 0)), StoreKind.Assign));
                _selfBox = box;
                _selfOriginal = selfAddress;
                _selfAddress = Emit(new RefElementAddrInst(box, 0));
            }

            // A struct's initialiser builds 'self' in a slot of its own, field
            // by field.
            if (symbol.FunctionKind == FunctionKind.Initializer && !_initializing && Bridged is null) {
                _selfAddress = NewSlot(_indirectResult is { Type: AddressType { Pointee: var made } } ? made : function.Type.Result, "self");
            }

            // The entry point first gives every global its initial value.
            if (ReferenceEquals(symbol, generator._entrySymbol) && generator._globalFields.Count > 0 && !IsGlobalInit(function.Name) && !generator._lazyGlobals) {
                var unit = new FunctionType([], SilType.Unit);
                Emit(new ApplyInst(Emit(new FunctionRefInst(GlobalsInit, unit)), SilType.Unit));
            }

            // 'var name = "x"': every initialiser of the type that declares it
            // stores the initial value first, before its own body runs — but
            // one that hands the work to another, 'self.init(…)', leaves it
            // to that one.
            if (symbol.FunctionKind == FunctionKind.Initializer && Bridged is null && !Delegates(body)
                && (symbol.ContainingSymbol is ExtensionSymbol extension ? extension.ExtendedType : symbol.ContainingSymbol) is NamedTypeSymbol owner
                && SelfType(symbol, substitution) is { } selfType) {
                foreach (var field in owner.OriginalDefinition.Members.OfType<FieldSymbol>()) {
                    if (field is { IsStatic: false, IsComputed: false, HasInitializer: true } && generator.FieldInitializer(field) is { } initial) {
                        var self = new BoundSelfAccess(initial.Syntax, selfType, true);
                        Statement(new BoundExpressionStatement(initial.Syntax, new BoundAssignment(initial.Syntax, new BoundFieldAccess(initial.Syntax, self, field), initial)));
                    }
                }

                // A subclass whose superclass is made with the implicit
                // 'init()' need not call it: one that does not has it called
                // for it, which gives the superclass's fields their values.
                if (owner.BaseTypes.OfType<NamedTypeSymbol>().FirstOrDefault(static b => b.TypeKind == TypeKind.Class) is { } superclass
                    && superclass.OriginalDefinition.GetMembers("init").OfType<FunctionSymbol>()
                        .FirstOrDefault(i => i is { IsSynthesized: true, Parameters.IsEmpty: true } && !generator._compilation.InheritedInitializers.ContainsKey(i)) is { } implicitInit
                    && !Descendants(body).Any(static n => n is BoundCall { Function.FunctionKind: FunctionKind.Initializer, Receiver: BoundSuperAccess })) {
                    var call = new BoundCall(body.Syntax, new BoundSuperAccess(body.Syntax, superclass), implicitInit, [], Core.Void) { Substitution = TypeSubstitution.For(superclass) };
                    Statement(new BoundExpressionStatement(body.Syntax, call));
                }
            }

            _body = body;
            if (!BridgeBody()) {
                Statement(body);
            }

            // Falling off the end: fine for a function returning nothing, and
            // for an initialiser, which returns what it built. For one with a
            // result the binder has said every path returns, so the end
            // cannot be reached.
            if (_current is not null) {
                if (_initializing) {
                    Return(null);
                } else if (symbol.FunctionKind == FunctionKind.Initializer) {
                    Return(TakeSelf());
                } else if (function.Type.Result == SilType.Unit || Generator is not null) {
                    Return(null);
                } else {
                    Jump(new UnreachableInst());
                }
            }

            // The exits, built last, so that they free every slot the body made.
            if (_returnBlock is { } returnBlock) {
                _current = returnBlock;
                _location = null;
                WriteBackInoutParameters();
                DestroyOwnedParameters();
                Deallocate();
                // The program ends once every task has, and then its globals go.
                if (ReferenceEquals(symbol, generator._entrySymbol) && !IsGlobalInit(function.Name)) {
                    Emit(new BuiltinInst("task_drain", SilType.Unit));
                }

                if (ReferenceEquals(symbol, generator._entrySymbol) && generator._globalFields.Count > 0 && !IsGlobalInit(function.Name) && !generator._lazyGlobals) {
                    var unit = new FunctionType([], SilType.Unit);
                    Emit(new ApplyInst(Emit(new FunctionRefInst(GlobalsDeinit, unit)), SilType.Unit));
                }

                // A generator that has finished stays finished.
                if (Generator is not null) {
                    StoreState(GeneratorDone);
                }

                Jump(new ReturnInst(returnBlock.Arguments.Count == 0 ? null : returnBlock.Arguments[0]));
            }

            if (_throwBlock is { } throwBlock) {
                _current = throwBlock;
                WriteBackInoutParameters();
                DestroyOwnedParameters();
                Deallocate();
                if (Generator is not null) {
                    StoreState(GeneratorDone);
                }

                Jump(new ThrowInst(throwBlock.Arguments[0]));
            }

            if (Generator is not null && !_failed) {
                Split();
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

        /// <summary>What an initialiser returns: the value or object it built, taken out of its slot.</summary>
        SilValue TakeSelf() {
            var slot = _selfAddress!;
            var type = ((AddressType)slot.Type).Pointee;

            // A struct with a deinit is a value once every field is set: built
            // again from its fields, as a literal is, it is made live, so its
            // deinit runs when it goes.
            if (SelfFieldByField() is { } made) {
                var fields = new SilValue[made.Fields.Count];
                for (var i = 0; i < fields.Length; i++) {
                    var field = Emit(new StructElementAddrInst(slot, i));
                    fields[i] = Emit(new LoadInst(field, made.Fields[i].Type.IsTrivial ? LoadKind.Trivial : LoadKind.Take));
                }

                return Emit(new StructInst(made, fields));
            }

            return Emit(new LoadInst(slot, type.IsTrivial ? LoadKind.Trivial : LoadKind.Take));
        }

        /// <summary>
        ///     The struct an initialiser builds when it has a deinit: its slot
        ///     holds fields, not yet a value, so it is taken and destroyed
        ///     field by field — a throw part way lets go of the fields set so
        ///     far without running the deinit. (The type's deinit may not be
        ///     lowered yet, so it is asked of the declaration.)
        /// </summary>
        StructType? SelfFieldByField() =>
            _selfAddress is { Type: AddressType { Pointee: StructType made } }
            && (symbol.ContainingSymbol is ExtensionSymbol extension ? extension.ExtendedType : symbol.ContainingSymbol) is NamedTypeSymbol owner
            && owner.OriginalDefinition.Members.OfType<FunctionSymbol>().Any(static f => f.FunctionKind == FunctionKind.Deinitializer)
                ? made
                : null;

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

            // A UnitValue is only a marker, defined nowhere: where one is used
            // as a value — `let x = f()` for an f returning nothing, `(f(), 1)`,
            // `[f()]` — it is the empty tuple, made here.
            for (var i = 0; i < instruction.Operands.Count; i++) {
                if (instruction.Operands[i] is UnitValue) {
                    instruction.SetOperand(i, _current.Append(new TupleInst(new TupleType([])) { Location = instruction.Location }));
                }
            }

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

            if (LowerType(local.Type) is not { } type) {
                Fail(at, $"a local of type '{local.Type.ToDisplayString()}'");
                return null;
            }

            return _slots[local] = NewSlot(_boxed.Contains(local) ? generator.BoxType(type) : type, local.Name);
        }

        /// <summary>
        ///     Gives a local a value where it is bound: a declaration, a
        ///     pattern, a loop's element. A local closures share gets a fresh
        ///     box each time, so that each closure sees the binding it was made in.
        /// </summary>
        void BindLocal(LocalSymbol local, SilValue? value, SyntaxNode at, bool declaration = false) {
            if (SlotFor(local, at) is not { } slot) {
                return;
            }

            EndsWithScope(slot);

            if (_boxed.Contains(local)) {
                var box = Emit(new AllocRefInst((ClassType)slot.ElementType));
                if (value is not null) {
                    Emit(new StoreInst(Owned(value), Emit(new RefElementAddrInst(box, 0)), StoreKind.Assign));
                }

                Emit(new StoreInst(box, slot, StoreKind.Assign));
                return;
            }

            if (value is not null) {
                Store(value, slot, declaration);
            }
        }

        /// <summary>
        ///     Where a local's value is: its slot; its box, reached through the
        ///     slot or, in a closure, through the context; or, for a value a
        ///     closure captured, its place in the context. A box is borrowed
        ///     until the caller ends <paramref name="borrows" />.
        /// </summary>
        SilValue? LocalAddress(LocalSymbol local, SyntaxNode at, List<SilValue> borrows) {
            if (closure is not null && closure.Captures.TryGetValue(local, out var capture)) {
                var field = Emit(new RefElementAddrInst(_context!, capture.Field));
                if (!capture.Boxed) {
                    return field;
                }

                var capturedBox = Hold(Emit(new LoadInst(field)));
                borrows.Add(capturedBox);
                return Emit(new RefElementAddrInst(capturedBox, 0));
            }

            if (SlotFor(local, at) is not { } slot) {
                return null;
            }

            if (!_boxed.Contains(local)) {
                return slot;
            }

            var box = Hold(Emit(new LoadInst(slot)));
            borrows.Add(box);
            return Emit(new RefElementAddrInst(box, 0));
        }

        /// <summary>
        ///     Where a parameter a closure captured is: its place in the
        ///     context, or, for an inout one, its box's field. A box is
        ///     borrowed until the caller ends <paramref name="borrows" />.
        /// </summary>
        SilValue CapturedParameterAddress(Capture captured, List<SilValue> borrows) {
            var field = Emit(new RefElementAddrInst(_context!, captured.Field));
            if (!captured.Boxed) {
                return field;
            }

            var box = Hold(Emit(new LoadInst(field)));
            borrows.Add(box);
            return Emit(new RefElementAddrInst(box, 0));
        }

        SilValue? LoadLocal(BoundLocalAccess access) {
            var borrows = new List<SilValue>();
            if (LocalAddress(access.Local, access.Syntax, borrows) is not { } address) {
                return null;
            }

            // A weak or unowned capture reads as its storage says.
            var value = ReadField(address);
            EndAll(borrows);
            return value;
        }

        /// <summary>The parameters the function owns, destroyed wherever it leaves.</summary>
        /// <summary>Move-only parameters the function owns, kept in slots.</summary>
        readonly Dictionary<ParameterSymbol, AllocStackInst> _spilled = [];

        /// <summary>The arguments that went into those slots: consumed there, not destroyed on the way out.</summary>
        readonly HashSet<SilArgument> _spilledArguments = [];

        /// <summary>The inout parameters closures capture: the box each lives in for the call, and the caller's storage.</summary>
        readonly Dictionary<ParameterSymbol, (SilValue Box, SilValue Original)> _parameterBoxes = [];

        /// <summary>Copies each boxed inout parameter's value back out to the caller's storage, and lets go of the box.</summary>
        void WriteBackInoutParameters() {
            foreach (var (box, original) in _parameterBoxes.Values) {
                Emit(new StoreInst(Emit(new LoadInst(Emit(new RefElementAddrInst(box, 0)))), original, StoreKind.Assign));
                Emit(new DestroyValueInst(box));
            }

            if (_selfBox is { } selfBox) {
                if (_selfOriginal is { } original) {
                    var trivial = ((AddressType)original.Type).Pointee.IsTrivial;
                    Emit(new StoreInst(Emit(new LoadInst(Emit(new RefElementAddrInst(selfBox, 0)), trivial ? null : LoadKind.Take)), original, trivial ? StoreKind.Assign : StoreKind.Init));
                }

                Emit(new DestroyValueInst(selfBox));
            }
        }

        /// <summary>
        ///     The box a mutating member's self lives in for the call, when a
        ///     closure inside changes it — the member's own, or, in such a
        ///     closure, the one its context holds — and the caller's value it
        ///     is written back to, in the member.
        /// </summary>
        SilValue? _selfBox;

        SilValue? _selfOriginal;

        void DestroyOwnedParameters() {
            for (var i = 0; i < _entry.Arguments.Count; i++) {
                if (function.Type.IsOwned(i) && !_spilledArguments.Contains(_entry.Arguments[i])) {
                    Emit(new DestroyValueInst(_entry.Arguments[i]));
                }
            }
        }

        /// <summary>Destroys what every non-trivial slot holds, then frees the slots, in reverse.</summary>
        void Deallocate() {
            for (var i = _allocations.Count - 1; i >= 0; i--) {
                if (_allocations[i].ElementType.IsTrivial || _allocations[i].Unmanaged || _scoped.Contains(_allocations[i])) {
                    continue;
                }

                if (ReferenceEquals(_allocations[i], _selfAddress) && SelfFieldByField() is { } made) {
                    for (var field = 0; field < made.Fields.Count; field++) {
                        if (!made.Fields[field].Type.IsTrivial) {
                            Emit(new DestroyAddrInst(Emit(new StructElementAddrInst(_allocations[i], field))));
                        }
                    }

                    continue;
                }

                Emit(new DestroyAddrInst(_allocations[i]));
            }

            for (var i = _allocations.Count - 1; i >= 0; i--) {
                Emit(new DeallocStackInst(_allocations[i]));
            }
        }

        // -------------------------------------------------------------------
        // Ownership
        // -------------------------------------------------------------------

        /// <summary>
        ///     True for an owned value this code may consume. An owned
        ///     parameter is not one: it is the function's to destroy as it
        ///     leaves, so a use of it is a copy.
        /// </summary>
        bool IsOwned(SilValue value) =>
            value is not UnitValue && !IsParameter(value) && SilVerifier.OwnershipOf(value) == Ownership.Owned;

        bool IsParameter(SilValue value) => value is SilArgument argument && argument.Block == _entry;

        /// <summary>A value something can take ownership of: itself if owned or trivial, else a copy.</summary>
        SilValue Owned(SilValue value) =>
            value is UnitValue || value.Type.IsTrivial || IsOwned(value)
                ? value
                : Emit(new CopyValueInst(value));

        /// <summary>The end of an owned value's life; nothing for a borrowed or trivial one.</summary>
        void Destroy(SilValue value) {
            if (IsOwned(value)) {
                Emit(new DestroyValueInst(value));
            }
        }

        /// <summary>Keeps an owned value on the temporaries while more of the expression is evaluated.</summary>
        SilValue Hold(SilValue value) {
            if (IsOwned(value) || value is LoadBorrowInst) {
                _temporaries.Add(value);
            }

            return value;
        }

        /// <summary>Takes a held value back off the temporaries, for whatever consumes it next.</summary>
        SilValue Unhold(SilValue value) {
            if (IsOwned(value) || value is LoadBorrowInst) {
                _temporaries.RemoveAt(_temporaries.LastIndexOf(value));
            }

            // What a borrow is borrowed out of comes off with it.
            if (value is LoadBorrowInst inner && _borrowedOutOf.TryGetValue(inner, out var outer)) {
                Unhold(outer);
            }

            return value;
        }

        /// <summary>
        ///     A borrow of a field of an object, and the object, itself borrowed
        ///     or a temporary, that must outlive it: ended right after it.
        /// </summary>
        readonly Dictionary<LoadBorrowInst, SilValue> _borrowedOutOf = new(ReferenceEqualityComparer.Instance);

        /// <summary>Ends what a borrow, already ended and off the temporaries, was borrowed out of.</summary>
        void ReleaseOuter(LoadBorrowInst inner) {
            if (!_borrowedOutOf.TryGetValue(inner, out var outer)) {
                return;
            }

            if (outer is LoadBorrowInst outerBorrow) {
                Emit(new EndBorrowInst(outerBorrow));
                ReleaseOuter(outerBorrow);
            } else {
                Destroy(outer);
            }
        }

        /// <summary>The end of a borrow: a temporary is taken off the temporaries and destroyed, a load_borrow ended.</summary>
        void End(SilValue value) {
            if (value is WriteBack writeBack) {
                writeBack.Run();
                return;
            }

            if (value is DeferredStep or OnError) {
                return;
            }

            if (value is AccessMarker marker) {
                if (marker.Begun) {
                    Emit(new BuiltinInst("access_end", SilType.Unit, marker.Address));
                }

                return;
            }

            if (value is LoadBorrowInst borrowed) {
                Unhold(borrowed);
                Emit(new EndBorrowInst(borrowed));
                ReleaseOuter(borrowed);
                return;
            }

            Destroy(Unhold(value));
        }

        /// <summary>Destroys, on the path being emitted, the temporaries above a depth, without forgetting them.</summary>
        void DestroyTemporaries(int depth) {
            for (var i = _temporaries.Count - 1; i >= depth; i--) {
                Emit<SilInstruction>(_temporaries[i] is LoadBorrowInst borrowed ? new EndBorrowInst(borrowed) : new DestroyValueInst(_temporaries[i]));
            }
        }

        /// <summary>
        ///     An expression's value to use without taking it: a parameter or
        ///     <c>self</c>, or a field of one, as it is; anything else evaluated
        ///     and held as a temporary, to be <see cref="End" />ed after the use.
        /// </summary>
        SilValue? Borrow(BoundExpression expression) {
            if (_evaluated.Count > 0 && _evaluated.TryGetValue(expression, out var once)) {
                return once.Type.IsTrivial ? once : Hold(Emit(new CopyValueInst(once)));
            }

            if (UsesLent(expression)) {
                return null;
            }

            // A move-only variable is borrowed where it is: a copy, destroyed
            // after, would run its deinit.
            if (MoveOnlyPlace(expression) is { } place) {
                return Hold(Emit(new LoadBorrowInst(place)));
            }

            // So is a move-only field of something in memory: a
            // method that borrows 'self' borrows it in place, where a copy
            // would be a move out of what holds it.
            if (expression is BoundFieldAccess && LowerType(expression.Type) is NominalType { IsNoncopyable: true } && Place(expression) is { } inPlace) {
                return Hold(Emit(new LoadBorrowInst(inPlace)));
            }

            // And one of an object: borrowed in the object, which stays
            // borrowed, or alive, until the field's borrow ends.
            if (expression is BoundFieldAccess && LowerType(expression.Type) is NominalType { IsNoncopyable: true }
                && ObjectOf(expression) is not null) {
                if (FieldInObject(expression) is not var (address, reference)) {
                    return null;
                }

                var inObject = Emit(new LoadBorrowInst(address));
                _borrowedOutOf[inObject] = reference;
                return Hold(inObject);
            }

            // So is one a closure captures, in its box. The box is held by the
            // variable's slot, or by the closure's context, for longer than this.
            if (CapturedMoveOnly(expression) is { } captured) {
                var boxes = new List<SilValue>();
                if (LocalAddress(captured.Local, captured.Syntax, boxes) is not { } boxed) {
                    return null;
                }

                var borrowedInBox = Emit(new LoadBorrowInst(boxed));
                EndAll(boxes);
                return Hold(borrowedInBox);
            }

            // A property of a type parameter of unspecialised code: its getter's result.
            if (expression is BoundFieldAccess opaqueProperty && GenericPropertyOf(opaqueProperty) is not null) {
                return Expression(expression) is { } read ? Hold(read) : null;
            }

            // A narrowed read is the payload, taken out: a value of its own.
            if (NarrowedUnion(expression) is not null) {
                return Expression(expression) is { } payload ? Hold(payload) : null;
            }

            switch (expression) {
                case BoundParameterAccess access when _parameters.TryGetValue(access.Parameter, out var parameter) && parameter.Type is not AddressType:
                    return parameter;

                // A value a closure captured by value, borrowed where its
                // context holds it: the context outlives the closure's code
                // and nothing changes it there, so a use needs no copy. A
                // task's captured object, shared with every other thread,
                // is then not counted up and down on each use.
                case BoundLocalAccess access when closure is not null && closure.Captures.TryGetValue(access.Local, out var byValue) && !byValue.Boxed
                    && byValue.Kind is CaptureKind.Default or CaptureKind.ByValue && LowerType(access.Type) is { IsTrivial: false } and not AddressType:
                    return Hold(Emit(new LoadBorrowInst(Emit(new RefElementAddrInst(_context!, byValue.Field)))));

                case BoundSelfAccess when _selfValue is not null:
                    return _selfValue;

                // 'super': this object, seen as its superclass.
                case BoundSuperAccess super when _selfValue is not null && LowerType(super.Type) is ClassType superclass:
                    return Emit(new UpcastInst(_selfValue, superclass));

                case BoundConversion { ConversionKind: ConversionKind.Identity } conversion when !IsArrayView(conversion):
                    return Borrow(conversion.Operand);

                case BoundConversion { ConversionKind: ConversionKind.Open, Type: MetatypeTypeSymbol { InstanceType: TypeParameterSymbol openedType } } when _genericMetadata.TryGetValue(openedType, out var openedMetadata):
                    return openedMetadata;

                case BoundConversion { ConversionKind: ConversionKind.Existential, Type: OpaqueTypeSymbol } opened:
                    return Borrow(opened.Operand);

                // A stored field of something borrowed is itself borrowed.
                case BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } field }
                    when Sub(receiver.Type).TypeKind == TypeKind.Struct && IsBorrowable(receiver) && !IsInlineType(receiver.Type): {
                    if (Borrow(receiver) is not { Type: StructType structType } value) {
                        return null;
                    }

                    var index = structType.FieldIndex(field.Name);
                    if (index < 0) {
                        return Missing(expression, $"the member '{field.Name}'");
                    }

                    // A weak or unowned field is read as the object, held as any owned value borrowed is.
                    var extracted = Emit(new StructExtractInst(value, index));
                    var read = OutOfField(extracted, Emit);
                    if (AsRead(read, (BoundFieldAccess)expression) is { } seen && !ReferenceEquals(seen, read)) {
                        return Hold(seen);
                    }

                    return ReferenceEquals(read, extracted) ? read : Hold(read);
                }

                default:
                    return Expression(expression) is { } owned ? Hold(owned) : null;
            }
        }

        /// <summary>
        ///     True for a parameter passed by address — inout or out — whose
        ///     address stands for its value; a parameter of reference type is
        ///     an address that is the value.
        /// </summary>
        static bool IsByAddress(ParameterSymbol parameter) => IsByAddressModifier(parameter);

        /// <summary>
        ///     The object a stored field is in — 'c.box.value' is in 'c' —
        ///     reached through value fields; null when it is in none.
        /// </summary>
        BoundExpression? ObjectOf(BoundExpression expression) =>
            expression is BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } field } && !SilGen.IsGlobal(field)
                ? Sub(receiver.Type).TypeKind is TypeKind.Class or TypeKind.Actor ? receiver
                : Sub(receiver.Type).TypeKind == TypeKind.Struct && !IsInlineType(receiver.Type) ? ObjectOf(receiver)
                : null
                : null;

        /// <summary>
        ///     The address of a stored field inside an object, and the object,
        ///     borrowed, which must outlive any use of the address.
        /// </summary>
        (SilValue Address, SilValue Reference)? FieldInObject(BoundExpression expression) {
            var access = (BoundFieldAccess)expression;
            var receiver = access.Receiver!;
            if (Sub(receiver.Type).TypeKind is TypeKind.Class or TypeKind.Actor) {
                if (Borrow(receiver) is not { Type: ClassType classType } reference) {
                    return null;
                }

                var index = classType.FieldIndex(access.Field.Name);
                if (index < 0) {
                    End(reference);
                    Missing(expression, $"the member '{access.Field.Name}'");
                    return null;
                }

                return (Emit(new RefElementAddrInst(reference, index)), reference);
            }

            if (FieldInObject(receiver) is not var (outer, owner)) {
                return null;
            }

            var fieldIndex = ((outer.Type as AddressType)?.Pointee as StructType)?.FieldIndex(access.Field.Name) ?? -1;
            if (fieldIndex < 0) {
                End(owner);
                Missing(expression, $"the member '{access.Field.Name}'");
                return null;
            }

            return (Emit(new StructElementAddrInst(outer, fieldIndex)), owner);
        }

        static bool IsByAddressModifier(ParameterSymbol parameter) => parameter.Modifier is ParameterModifier.InOut or ParameterModifier.Out;

        /// <summary>The slot of a move-only local or parameter, or null.</summary>
        AllocStackInst? MoveOnlyPlace(BoundExpression expression) =>
            expression switch {
                BoundLocalAccess local when _slots.TryGetValue(local.Local, out var slot) && !_boxed.Contains(local.Local)
                    && slot.ElementType is NominalType { IsNoncopyable: true } => slot,
                BoundParameterAccess parameter when _spilled.TryGetValue(parameter.Parameter, out var spilled)
                    && spilled.ElementType is NominalType { IsNoncopyable: true } => spilled,
                BoundConversion { ConversionKind: ConversionKind.Identity } conversion => MoveOnlyPlace(conversion.Operand),
                _ => null,
            };

        /// <summary>A move-only local kept in a box, because a closure captures it — here or in the closure — or null.</summary>
        BoundLocalAccess? CapturedMoveOnly(BoundExpression expression) =>
            expression switch {
                BoundLocalAccess local when LowerType(local.Type) is NominalType { IsNoncopyable: true }
                    && (_boxed.Contains(local.Local) || closure?.Captures.TryGetValue(local.Local, out var capture) == true && capture.Boxed) => local,
                BoundConversion { ConversionKind: ConversionKind.Identity } conversion => CapturedMoveOnly(conversion.Operand),
                _ => null,
            };

        /// <summary>True for an expression <see cref="Borrow" /> reaches without evaluating anything owned.</summary>
        bool IsBorrowable(BoundExpression expression) =>
            expression switch {
                BoundParameterAccess access => _parameters.TryGetValue(access.Parameter, out var parameter) && parameter.Type is not AddressType,
                BoundSelfAccess => _selfValue is not null,
                BoundConversion { ConversionKind: ConversionKind.Identity } conversion => !IsArrayView(conversion) && IsBorrowable(conversion.Operand),
                BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } }
                    => Sub(receiver.Type).TypeKind == TypeKind.Struct && IsBorrowable(receiver),
                _ => false,
            };

        /// <summary>Evaluates expressions in order, holding each while the next is evaluated.</summary>
        List<SilValue>? Evaluate(IEnumerable<BoundExpression> expressions) {
            var values = new List<SilValue>();
            foreach (var expression in expressions) {
                if (Expression(expression) is not { } value) {
                    values.ForEach(v => Unhold(v));
                    return null;
                }

                values.Add(Hold(value));
            }

            // Whatever consumes them next owns them.
            for (var i = values.Count - 1; i >= 0; i--) {
                Unhold(values[i]);
            }

            return values;
        }

        /// <summary>
        ///     What a <c>Some</c> carries: nothing for a value of no bytes —
        ///     Void, or Never, which a generic <c>T</c> may be — since the
        ///     optional of one has a <c>Some</c> with no payload.
        /// </summary>
        static SilValue? Carried(SilValue value) => value is UnitValue || value.Type == SilType.Unit ? null : value;

        /// <summary>
        ///     A payload argument for a <c>switch_enum</c> case block: borrowed
        ///     from the enum. A case with nothing to carry — the <c>Some</c> of
        ///     <c>Never?</c> or <c>Void?</c>, which a generic <c>T?</c> may be
        ///     — has no argument, and its payload is the unit value.
        /// </summary>
        static SilValue Payload(SilBlock block, SilType? type) =>
            type is null ? UnitValue.Instance : block.AddArgument(type, !type.IsTrivial);

        /// <summary>A block that destroys a value and goes on to another; or the other itself, when there is nothing to destroy.</summary>
        SilBlock Destroying(SilValue value, SilBlock target) {
            if (!IsOwned(value)) {
                return target;
            }

            var saved = _current;
            var block = function.AddBlock();
            _current = block;
            Destroy(value);
            Branch(target);
            _current = saved;
            return block;
        }

        /// <summary>
        ///     Runs the cleanups of every scope deeper than <paramref name="depth" />,
        ///     innermost first: deferred bodies, and the destruction of the
        ///     locals each declared. A local never bound on this path is
        ///     empty, and destroying it does nothing.
        /// </summary>
        void RunDefers(int depth) {
            var saved = _scopes.Select(static s => s.ToList()).ToList();
            for (var i = saved.Count - 1; i >= depth; i--) {
                // While a scope's defers run, it and those inside it are closed.
                _scopes.RemoveRange(i, _scopes.Count - i);
                foreach (var cleanup in Enumerable.Reverse(saved[i])) {
                    switch (cleanup) {
                        case BoundStatement deferred:
                            Statement(deferred);
                            break;

                        case AllocStackInst slot when !slot.ElementType.IsTrivial || _referred.Contains(slot):
                            Emit(new DestroyAddrInst(slot));
                            break;

                        case AsyncLetEnd asyncLet:
                            EndAsyncLet(asyncLet);
                            break;

                        case RegionPop pop: {
                            var frameRegion = Emit(new LoadBorrowInst(pop.Slot));
                            Emit(new BuiltinInst("region_pop", SilType.Unit, frameRegion));
                            Emit(new EndBorrowInst(frameRegion));
                            break;
                        }
                    }
                }
            }

            _scopes.Clear();
            _scopes.AddRange(saved);
        }

        /// <summary>A region block's frame, given back to the region it was lent from as the block ends.</summary>
        sealed record RegionPop(AllocStackInst Slot);

        /// <summary>Regions lent to a 'region frame = pool' block, by the frame's name: not to be used directly inside it.</summary>
        readonly Dictionary<Symbol, string> _lent = [];

        /// <summary>A use of a region lent to a block's frame: an error, since what it allocates would go with the frame.</summary>
        bool UsesLent(BoundExpression expression) {
            Symbol? symbol = expression switch {
                BoundLocalAccess local => local.Local,
                BoundParameterAccess parameter => parameter.Parameter,
                _ => null,
            };

            if (symbol is null || !_lent.TryGetValue(symbol, out var frame)) {
                return false;
            }

            generator.Report(
                ErrorCode.ERR_ReferenceOutlivesRoot,
                expression.Syntax.Span,
                $"'{symbol.Name}' is lent to '{frame}' for this block: allocate through '{frame}', whose allocations end with the block"
            );
            return true;
        }

        /// <summary>
        ///     Gives a local's slot to the innermost scope, to be destroyed as it
        ///     closes. A trivial one has nothing to destroy, but a reference
        ///     taken to it must still not outlive the scope: its end is marked
        ///     for root checking (docs/02 §2.4) when a reference was taken.
        /// </summary>
        void EndsWithScope(AllocStackInst slot) {
            if (_scopes.Count == 0 || _scopes.Any(s => s.Contains(slot))) {
                return;
            }

            _scopes[^1].Add(slot);
            if (!slot.ElementType.IsTrivial) {
                _scoped.Add(slot);
            }
        }

        /// <summary>
        ///     Trivial slots a reference has been taken to, so far: those whose
        ///     scope's end is written down as a <c>destroy_addr</c>. The rest
        ///     need none, and stay promotable to SSA values.
        /// </summary>
        readonly HashSet<AllocStackInst> _referred = [];

        static AllocStackInst? SlotUnder(SilValue address) =>
            address switch {
                AllocStackInst slot => slot,
                StructElementAddrInst element => SlotUnder(element.Address),
                TupleElementAddrInst element => SlotUnder(element.Address),
                _ => null,
            };

        /// <summary>
        ///     Slots a scope destroys: on every way out of it they are destroyed
        ///     or, on a way out lowered before they were bound, still empty — so
        ///     the function's exits need not destroy them again.
        /// </summary>
        readonly HashSet<AllocStackInst> _scoped = [];

        /// <summary>
        ///     The one block every return goes through. Its deallocations are
        ///     written last, when every slot is known: a slot made after some
        ///     return was lowered must still be freed on that return's path.
        /// </summary>
        SilBlock? _returnBlock;

        void Return(SilValue? value) {
            value = IndirectResult(value);

            // A return from inside an expression leaves its temporaries behind.
            DestroyTemporaries(0);
            RunDefers(0);

            // A generator's ends: no more elements.
            if (Generator is not null && _current is not null) {
                value ??= NoMoreElements();
            }

            if (_returnBlock is null) {
                _returnBlock = function.AddBlock();
                if (function.Type.Result != SilType.Unit) {
                    _returnBlock.AddArgument(function.Type.Result);
                }
            }

            if (function.Type.Result == SilType.Unit) {
                // 'set => field = newValue': what a function giving nothing
                // is handed to return is dropped.
                if (value is not null) {
                    Destroy(value);
                }

                Branch(_returnBlock);
            } else if (value is not null and not UnitValue) {
                // A witness's array of Self's values, as its entry gives it.
                var owned = Owned(value);
                Branch(_returnBlock, Bridged is not null && owned.Type is ArrayType && function.Type.Result is ArrayType result && result != owned.Type && SilType.MentionsOpaque(result)
                    ? Emit(new BuiltinInst("opaque_take_as", result, owned))
                    : owned);
            }
        }

        /// <summary>Sends an error to the innermost handler, running the defers on the way.</summary>
        void RouteError(SilValue error, SyntaxNode at) {
            if (_handlers.Count == 0 && StopsUncaught(error, at)) {
                return;
            }

            if (ErrorOut(ref error) && FunctionHandler(error.Type, at) is { } written) {
                DestroyTemporaries(written.Temporaries);
                RunDefers(written.Scopes);
                Branch(written.Block, error);
                return;
            }

            var handler = _handlers.Count > 0 ? _handlers[^1] : FunctionHandler(error.Type, at);
            if (handler is null) {
                return;
            }

            if (Convert(Owned(error), handler.Type, at) is not { } converted) {
                return;
            }

            DestroyTemporaries(handler.Temporaries);
            RunDefers(handler.Scopes);
            Branch(handler.Block, converted);
        }

        /// <summary>An owned error as the type a handler takes: into a union, into or out of 'any Error'.</summary>
        SilValue? Convert(SilValue error, SilType to, SyntaxNode at) {
            if (error.Type == to) {
                return error;
            }

            _current ??= function.AddBlock();
            var block = _current;
            if (generator.ConvertError(function, ref block, error, to) is { } converted) {
                _current = block;
                return converted;
            }

            // Not a union, nor 'any Error': the handler type's own From.
            if (FromConversion(error, to, at) is { } from) {
                return from;
            }

            Fail(at, $"an error of type {error.Type} converted to {to}");
            return null;
        }

        /// <summary>
        ///     An error on its way to a handler of another type: converted by
        ///     the handler type's <c>From</c> conformance (docs/03 §3.6), its
        ///     static <c>from</c> called with the error, which is then done with.
        /// </summary>
        SilValue? FromConversion(SilValue error, SilType wanted, SyntaxNode at) {
            TypeSymbol? SymbolOf(SilType type) =>
                generator._types.FirstOrDefault(entry => entry.Value is { } lowered && lowered == type).Key;

            if (SymbolOf(wanted) is not NamedTypeSymbol target || SymbolOf(error.Type) is not { } source) {
                return null;
            }

            var members = TypeSubstitution.For(target);
            var from = target.OriginalDefinition.GetMembers("from")
                .OfType<FunctionSymbol>()
                .FirstOrDefault(f => f is { IsStatic: true, Parameters.Length: 1, TypeParameters.IsEmpty: true }
                    && members.Apply(f.Parameters[0].Type).Equals(source)
                    && members.Apply(f.ReturnType).Equals(target));
            if (from is null) {
                return null;
            }

            string name;
            FunctionType? type;
            if (IsGeneric(from)) {
                if (generator.Instance(from, members, at) is not { } instance) {
                    return null;
                }

                (name, type) = instance;
            } else {
                if (!generator._names.TryGetValue(from, out var plainName)) {
                    return null;
                }

                name = plainName;
                type = generator._module.Find(name)?.Type ?? generator.SignatureOf(from, at, TypeSubstitution.Empty);
            }

            if (type is null || type.Result != wanted) {
                return null;
            }

            var converted = Emit(new ApplyInst(Emit(new FunctionRefInst(name, type)), wanted, error));
            Destroy(error);
            return converted;
        }

        /// <summary>
        ///     An error leaving the program's <c>main</c>, sync or async, which
        ///     nothing above it would catch: stopped at where it leaves, by
        ///     Sable.Core's <c>uncaughtError</c> — 'fatal error: uncaught
        ///     error: ' and its description, the place of the <c>throw</c>
        ///     or <c>try</c>, through the panic hook, and status 1 — rather
        ///     than thrown to a caller that has nothing to do with it. Boxed
        ///     as <c>any Error</c> on the way, as any type may be. False
        ///     where this is not main, or there is no <c>uncaughtError</c>
        ///     to call (a Sable.Core without it), and the error is thrown.
        /// </summary>
        bool StopsUncaught(SilValue error, SyntaxNode at) {
            if (!ReferenceEquals(symbol, generator._entrySymbol) || IsGlobalInit(function.Name) || Generator is not null
                || Core.Module.GetMembers("uncaughtError").OfType<FunctionSymbol>().FirstOrDefault(static f => f.Parameters.Length == 4) is not { } report
                || !generator._names.TryGetValue(report, out var name)
                || (generator._module.Find(name)?.Type ?? generator.External(name) ?? generator.SignatureOf(report, at, TypeSubstitution.Empty)) is not { } type) {
                return false;
            }

            var boxed = Convert(Owned(error), generator.AnyErrorType(), at);
            if (boxed is null || _current is null) {
                return true;
            }

            var place = source.GetLinePosition(at.Span.Start);
            var arguments = new SilValue[] {
                boxed,
                Emit(new StringLiteralInst(function.SourceFile ?? "")),
                Emit(new IntegerLiteralInst(SilType.Int, place.Line)),
                Emit(new IntegerLiteralInst(SilType.Int, place.Column)),
            };
            Emit(new ApplyInst(Emit(new FunctionRefInst(name, type)), type.Result, arguments));
            Jump(new UnreachableInst());
            return true;
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
            return new(_throwBlock, thrown, 0, 0);
        }

        // -------------------------------------------------------------------
        // Statements
        // -------------------------------------------------------------------

        /// <summary>
        ///     Lowers a statement, in a lexical scope of its own when it is a
        ///     nested block or binds names for one — a loop's variable, an
        ///     'if let''s, a case's — so that a debugger shows a local only
        ///     where it can be named, and a shadowing one hides what it shadows.
        /// </summary>
        void Statement(BoundStatement statement) {
            var outer = _scope;
            if (statement is BoundBlock or BoundForEach or BoundIf or BoundWhile or BoundSwitch or BoundDo && !ReferenceEquals(statement, _body)) {
                var start = source.GetLinePosition(statement.Syntax.Span.Start);
                function.Scopes.Add(new SilScope(outer, start.Line, start.Column));
                _scope = function.Scopes.Count;
            }

            try {
                LowerStatement(statement);
            } finally {
                _scope = outer;
            }
        }

        void LowerStatement(BoundStatement statement) {
            if (_failed) {
                return;
            }

            if (statement is not BoundBlock) {
                Locate(statement.Syntax);
            }

            switch (statement) {
                case BoundBlock block: {
                    _scopes.Add([]);
                    foreach (var child in block.Statements) {
                        Statement(child);
                    }

                    if (_current is not null) {
                        RunDefers(_scopes.Count - 1);
                    }

                    _scopes.RemoveAt(_scopes.Count - 1);
                    return;
                }

                case BoundDefer defer:
                    _scopes[^1].Add(defer.Body);
                    return;

                case BoundLocalDeclaration { Local.IsAsyncLet: true } asyncLet:
                    AsyncLet(asyncLet);
                    return;

                case BoundLocalDeclaration declaration:
                    if (declaration.Initializer is { } initializer) {
                        if (Expression(initializer) is { } initial) {
                            BindLocal(declaration.Local, initial, declaration.Syntax, declaration: true);
                        }
                    } else if (_boxed.Contains(declaration.Local)) {
                        BindLocal(declaration.Local, null, declaration.Syntax);
                    } else if (SlotFor(declaration.Local, declaration.Syntax) is { } declared) {
                        EndsWithScope(declared);
                    }

                    return;

                case BoundPatternDeclaration declaration: {
                    if (Expression(declaration.Initializer) is not { } value) {
                        return;
                    }

                    // Irrefutable, so the failing branch cannot be taken.
                    var matched = function.AddBlock();
                    var failed = function.AddBlock();
                    Hold(value);
                    Pattern(declaration.Pattern, value, matched, failed);
                    Unhold(value);
                    _current = failed;
                    Jump(new UnreachableInst());
                    _current = matched;
                    Destroy(value);
                    return;
                }

                // 'writer.write("total: \(n)")' into a TextWriter: each piece
                // written as it is made, with no string of them all.
                case BoundExpressionStatement { Expression: BoundCall { Arguments: [BoundInterpolatedString interpolated] } call }
                    when WritesInPieces(call, interpolated):
                    WriteInPieces(call, interpolated);
                    return;

                case BoundExpressionStatement expression:
                    // An assignment on its own: its value, which nothing reads, is not made.
                    if ((expression.Expression is BoundAssignment assignment ? OnActorFor(assignment, () => Assignment(assignment)) : Expression(expression.Expression)) is { } discarded) {
                        Destroy(discarded);
                    }

                    return;

                case BoundReturn returnStatement: {
                    if (returnStatement.Value is { } returned) {
                        // An assignment a function giving nothing returns is only a store.
                        var stores = returned is BoundAssignment && function.Type.Result == SilType.Unit;
                        if ((stores ? Assignment((BoundAssignment)returned) : Expression(returned)) is { } value) {
                            Return(value);
                        }
                    } else if (symbol.FunctionKind == FunctionKind.Initializer && !_initializing) {
                        Return(TakeSelf());
                    } else {
                        Return(null);
                    }

                    return;
                }

                case BoundYield yieldStatement:
                    Yield(yieldStatement);
                    return;

                case BoundThrow throwStatement:
                    if (Expression(throwStatement.Value) is { } error) {
                        // An error of type Never — a generic one's, made Never
                        // in this instance — has no value: the throw is never reached.
                        if (Sub(throwStatement.Value.Type).Equals(Core.Never)) {
                            Jump(new UnreachableInst());
                            return;
                        }

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

                    // The loop's 'break' goes to after too. 'while true' decides nothing: only a jump leaves it.
                    var decision = loop is { Syntax: WhileStatementSyntax written, Condition: not BoundLiteral } ? Decide(written.WhileKeyword.SpanStart, "while", "true", "false") : null;
                    _current = header;
                    Condition(loop.Condition, Mark(body, decision, 0), Land(after, decision, 1));

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

                    // The body is entered first, and the loop's 'break' goes to after.
                    var decision = loop is { Syntax: RepeatStatementSyntax written, Condition: not BoundLiteral } ? Decide(written.WhileKeyword.SpanStart, "while", "true", "false") : null;
                    _current = test;
                    Condition(loop.Condition, Land(body, decision, 0), Land(after, decision, 1));

                    _current = after;
                    return;
                }

                case BoundForEach { Sequence: BoundRange range } loop:
                    ForRange(loop, range);
                    return;

                case BoundForEach loop when Core.IsCountableRange(Sub(loop.Sequence.Type)):
                    ForRangeValue(loop);
                    return;

                case BoundForEach loop when Core.IsStride(Sub(loop.Sequence.Type)):
                    ForStride(loop);
                    return;

                case BoundForEach { Sequence.Type: ArrayTypeSymbol } loop when IsInlineArray(loop.Sequence):
                    ForInline(loop);
                    return;

                case BoundForEach { Sequence.Type: ArrayTypeSymbol } loop:
                    ForArray(loop);
                    return;

                case BoundBreak jump when FindLoop(jump.Label) is { } breakLoop:
                    DestroyTemporaries(breakLoop.Temporaries);
                    RunDefers(breakLoop.Scopes);
                    Branch(breakLoop.Break);
                    return;

                case BoundContinue jump when FindLoop(jump.Label, continues: true) is { Continue: { } target } continueLoop:
                    DestroyTemporaries(continueLoop.Temporaries);
                    RunDefers(continueLoop.Scopes);
                    Branch(target);
                    return;

                case BoundSwitch switchStatement:
                    Switch(switchStatement);
                    return;

                case BoundFallthrough when _fallthroughs is [.., { } next]:
                    DestroyTemporaries(next.Temporaries);
                    RunDefers(next.Scopes);
                    Branch(next.Target);
                    return;

                case BoundDo doStatement:
                    Do(doStatement);
                    return;

                // 'region arena { … }': a region for the block, ended with it.
                case BoundScopedBlock { ScopeKind: ScopeKind.Region, Source: null, Name: { } arena } region: {
                    _scopes.Add([]);
                    BindLocal(arena, Emit(new BuiltinInst("region_create", SilType.Region)), region.Syntax);
                    Statement(region.Body);
                    if (_current is not null) {
                        RunDefers(_scopes.Count - 1);
                    }

                    _scopes.RemoveAt(_scopes.Count - 1);
                    return;
                }

                // 'region frame = pool { … }': the block allocates in 'pool' through
                // 'frame', and gives back what it took as it ends. 'pool' is lent
                // to 'frame' meanwhile: what the block allocates is rooted in
                // 'frame', and dies with it.
                case BoundScopedBlock { ScopeKind: ScopeKind.Region, Source: { } source, Name: { } frame } region: {
                    if (Expression(source) is not { Type: var regionType } lent || regionType != SilType.Region) {
                        Fail(region.Syntax, "a region block whose source is not a region");
                        return;
                    }

                    Emit(new BuiltinInst("region_push", SilType.Unit, lent));
                    _scopes.Add([]);
                    BindLocal(frame, lent, region.Syntax);
                    _scopes[^1].Add(new RegionPop(_slots[frame]));
                    var lentSymbol = source switch {
                        BoundLocalAccess local => (Symbol)local.Local,
                        BoundParameterAccess parameter => parameter.Parameter,
                        _ => null,
                    };

                    if (lentSymbol is not null) {
                        _lent[lentSymbol] = frame.Name;
                    }

                    Statement(region.Body);
                    if (lentSymbol is not null) {
                        _lent.Remove(lentSymbol);
                    }

                    if (_current is not null) {
                        RunDefers(_scopes.Count - 1);
                    }

                    _scopes.RemoveAt(_scopes.Count - 1);
                    return;
                }

                // 'comptime { … }' ran as the program was compiled (SilGen.Constants.cs).
                case BoundScopedBlock { ScopeKind: ScopeKind.Comptime }:
                    return;

                // 'unsafe { … }' and the like: a block, as far as lowering goes.
                case BoundScopedBlock { ScopeKind: not ScopeKind.Region } scoped:
                    Statement(scoped.Body);
                    return;

                case BoundLocalFunction local:
                    LocalFunction(local);
                    return;

                default:
                    Fail(statement.Syntax, $"the statement '{Describe(statement.Syntax)}'");
                    return;
            }
        }

        /// <summary>
        ///     Stores an owned value: <c>[init]</c> for a trivial declaration, as
        ///     its slot is fresh; <c>[assign]</c> otherwise, which destroys any
        ///     value the storage held — the last iteration's, in a loop.
        /// </summary>
        void Store(SilValue value, SilValue address, bool declaration = false) =>
            Emit(new StoreInst(Owned(value), address, declaration && value.Type.IsTrivial ? StoreKind.Init : StoreKind.Assign));

        void If(BoundIf ifStatement) {
            var after = function.AddBlock();
            var decision = ifStatement.Syntax switch {
                IfStatementSyntax written => Decide(written.IfKeyword.SpanStart, "if", "true", "false"),
                GuardStatementSyntax written => Decide(written.GuardKeyword.SpanStart, "guard", "true", "false"),
                _ => null,
            };

            // 'guard c else { body }': the body runs when c is false.
            if (ifStatement.Syntax is GuardStatementSyntax) {
                var elseBody = function.AddBlock();
                Condition(ifStatement.Condition, Land(after, decision, 0), Mark(elseBody, decision, 1));
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
            Condition(ifStatement.Condition, Mark(consequence, decision, 0),
                ifStatement.Alternative is null ? Land(after, decision, 1) : Mark(alternative, decision, 1));

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
        /// <remarks>
        ///     <c>for i in a...</c> has no upper bound: it counts until the loop
        ///     is left, and stepping past <c>Int.max</c> traps as any overflow does.
        /// </remarks>
        void ForRange(BoundForEach loop, BoundRange range) {
            if (range.Lower is null || loop.Element is null) {
                Fail(loop.Syntax, "a 'for' over a range with no lower bound");
                return;
            }

            if (Expression(range.Lower) is not { } lower) {
                return;
            }

            SilValue? upper = null;
            if (range.Upper is not null && (upper = Expression(range.Upper)) is null) {
                return;
            }

            CountingLoop(loop, lower, upper, range.IsInclusive);
        }

        /// <summary>
        ///     <c>for i in r</c>, a range held in a variable or given by a call:
        ///     the same counting loop, over the bounds taken out of it.
        /// </summary>
        void ForRangeValue(BoundForEach loop) {
            if (loop.Element is null) {
                Fail(loop.Syntax, "a 'for' over a range");
                return;
            }

            if (Expression(loop.Sequence) is not { Type: StructType structType } range) {
                return;
            }

            // 'a...' held in a variable has no upper bound to stop at.
            var lower = Emit(new StructExtractInst(range, structType.FieldIndex("lowerBound")));
            var upper = structType.FieldIndex("upperBound") is var upperIndex and >= 0
                ? Emit(new StructExtractInst(range, upperIndex))
                : null;
            Destroy(range);
            var isInclusive = Sub(loop.Sequence.Type) is NamedTypeSymbol named
                && named.OriginalDefinition.Equals(Core.ClosedRange);
            CountingLoop(loop, lower, upper, isInclusive);
        }

        /// <remarks>
        ///     The counter is of the bounds' own integer type, and steps with
        ///     that type's arithmetic, so stepping past its largest value traps.
        ///     A range of characters counts their code points, as Ints, and
        ///     steps over the surrogates, which are no characters.
        /// </remarks>
        void CountingLoop(BoundForEach loop, SilValue lower, SilValue? upper, bool isInclusive) {
            var isCharacter = lower.Type == SilType.Char;
            if (!(SilType.IsInteger(lower.Type) || isCharacter) || SlotFor(loop.Element!, loop.Syntax) is null) {
                Fail(loop.Syntax, $"a 'for' over a range of {lower.Type}");
                return;
            }

            if (isCharacter) {
                lower = Emit(new BuiltinInst("char_to_Int", SilType.Int, lower));
                if (upper is not null) {
                    upper = Emit(new BuiltinInst("char_to_Int", SilType.Int, upper));
                }
            }

            var type = lower.Type;
            var counter = NewSlot(type);
            Emit(new StoreInst(lower, counter, StoreKind.Init));

            // In a generator the body may pause, and only what is in a slot
            // is there when it resumes: the bound too.
            AllocStackInst? bound = null;
            if (Generator is not null && upper is not null) {
                bound = NewSlot(type);
                Emit(new StoreInst(upper, bound, StoreKind.Init));
            }

            SilValue Upper() => bound is null ? upper! : Emit(new LoadInst(bound));

            var header = function.AddBlock();
            var body = function.AddBlock();
            var step = function.AddBlock();
            var after = function.AddBlock();
            Branch(header);

            _current = header;
            if (upper is null) {
                Branch(body);
            } else {
                var current = Emit(new LoadInst(counter));
                var compare = isInclusive ? $"cmp_le_{type}" : $"cmp_lt_{type}";
                var inside = Emit(new BuiltinInst(compare, SilType.Bool, current, Upper()));
                Jump(new CondBranchInst(inside, body, [], after, []));
            }

            _current = body;
            SilValue value = Emit(new LoadInst(counter));
            if (isCharacter) {
                value = Emit(new TupleExtractInst(Emit(new BuiltinInst("char_from_Int", SilType.Tuple([SilType.Bool, SilType.Char]), value)), 1));
            }

            BindLocal(loop.Element!, value, loop.Syntax);
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
            if (isInclusive && upper is not null) {
                var atEnd = Emit(new BuiltinInst($"cmp_eq_{type}", SilType.Bool, last, Upper()));
                Jump(new CondBranchInst(atEnd, after, [], more, []));
            } else {
                Branch(more);
            }

            _current = more;
            var one = Emit(new IntegerLiteralInst(type, 1));
            var stepped = Emit(new BuiltinInst($"add_{type}", type, last, one));

            Emit(new StoreInst(stepped, counter, StoreKind.Assign));

            // After U+D7FF comes U+E000: the surrogates between are no characters.
            if (isCharacter) {
                var isSurrogate = Emit(new BuiltinInst("cmp_eq_Int", SilType.Bool, stepped, Emit(new IntegerLiteralInst(SilType.Int, 0xD800))));
                var skip = function.AddBlock();
                Jump(new CondBranchInst(isSurrogate, skip, [], header, []));
                _current = skip;
                Emit(new StoreInst(Emit(new IntegerLiteralInst(SilType.Int, 0xE000)), counter, StoreKind.Assign));
            }

            Branch(header);

            _current = after;
        }

        /// <summary>
        ///     <c>for i in stride(from: a, to: b, by: s)</c>, and through: a
        ///     counting loop that steps by <c>s</c>, as <c>StrideIterator</c>
        ///     does, with no iterator made. Written in the loop, the call to
        ///     <c>stride</c> is not made either: its arguments are the bounds.
        /// </summary>
        /// <remarks>
        ///     The step's sign is known only as the program runs, so each test
        ///     asks it; a step of zero gives nothing. A step that would pass
        ///     Int's bounds ends the loop instead, so a stride up to
        ///     <c>Int.max</c> does not overflow.
        /// </remarks>
        void ForStride(BoundForEach loop) {
            if (loop.Element is null || SlotFor(loop.Element, loop.Syntax) is null) {
                Fail(loop.Syntax, "a 'for' over a stride");
                return;
            }

            var through = Sub(loop.Sequence.Type).Equals(Core.StrideThrough);
            SilValue? start, end, stride;
            if (loop.Sequence is BoundCall { Receiver: null, Arguments.Length: 3 } call
                && call.Function.Name == "stride" && ReferenceEquals(call.Function.ContainingSymbol, Core.Module)) {
                if ((start = Expression(call.Arguments[0])) is null || (end = Expression(call.Arguments[1])) is null
                    || (stride = Expression(call.Arguments[2])) is null) {
                    return;
                }
            } else {
                if (Expression(loop.Sequence) is not { Type: StructType structType } value) {
                    return;
                }

                start = Emit(new StructExtractInst(value, structType.FieldIndex("start")));
                end = Emit(new StructExtractInst(value, structType.FieldIndex("end")));
                stride = Emit(new StructExtractInst(value, structType.FieldIndex("step")));
                Destroy(value);
            }

            var type = SilType.Int;
            var counter = NewSlot(type);
            Emit(new StoreInst(start, counter, StoreKind.Init));

            // In a generator the body may pause, and only what is in a slot
            // is there when it resumes: the end and the step too.
            AllocStackInst? endSlot = null, stepSlot = null;
            if (Generator is not null) {
                endSlot = NewSlot(type);
                Emit(new StoreInst(end, endSlot, StoreKind.Init));
                stepSlot = NewSlot(type);
                Emit(new StoreInst(stride, stepSlot, StoreKind.Init));
            }

            SilValue End() => endSlot is null ? end : Emit(new LoadInst(endSlot));
            SilValue Step() => stepSlot is null ? stride : Emit(new LoadInst(stepSlot));
            SilValue Literal(long value) => Emit(new IntegerLiteralInst(type, value));
            SilValue Compare(string how, SilValue left, SilValue right) => Emit(new BuiltinInst($"cmp_{how}_Int", SilType.Bool, left, right));

            var header = function.AddBlock();
            var up = function.AddBlock();
            var down = function.AddBlock();
            var body = function.AddBlock();
            var step = function.AddBlock();
            var stepUp = function.AddBlock();
            var stepDown = function.AddBlock();
            var more = function.AddBlock();
            var after = function.AddBlock();

            // A step of zero never gets anywhere.
            Jump(new CondBranchInst(Compare("eq", Step(), Literal(0)), after, [], header, []));

            _current = header;
            Jump(new CondBranchInst(Compare("gt", Step(), Literal(0)), up, [], down, []));

            _current = up;
            Jump(new CondBranchInst(Compare(through ? "le" : "lt", Emit(new LoadInst(counter)), End()), body, [], after, []));

            _current = down;
            Jump(new CondBranchInst(Compare(through ? "ge" : "gt", Emit(new LoadInst(counter)), End()), body, [], after, []));

            _current = body;
            BindLocal(loop.Element, Emit(new LoadInst(counter)), loop.Syntax);
            if (loop.Filter is { } filter) {
                var filtered = function.AddBlock();
                Condition(filter, filtered, step);
                _current = filtered;
            }

            InLoop(loop, after, step, () => Statement(loop.Body));
            if (_current is not null) {
                Branch(step);
            }

            // The next value is past the end when it is past Int's bounds:
            // asked without the addition that would overflow.
            _current = step;
            Jump(new CondBranchInst(Compare("gt", Step(), Literal(0)), stepUp, [], stepDown, []));

            _current = stepUp;
            var highest = Emit(new BuiltinInst("sub_Int", type, Literal(long.MaxValue), Step()));
            Jump(new CondBranchInst(Compare("le", Emit(new LoadInst(counter)), highest), more, [], after, []));

            _current = stepDown;
            var lowest = Emit(new BuiltinInst("sub_Int", type, Literal(long.MinValue), Step()));
            Jump(new CondBranchInst(Compare("ge", Emit(new LoadInst(counter)), lowest), more, [], after, []));

            _current = more;
            var stepped = Emit(new BuiltinInst("add_Int", type, Emit(new LoadInst(counter)), Step()));
            Emit(new StoreInst(stepped, counter, StoreKind.Assign));
            Branch(header);

            _current = after;
        }

        /// <summary>
        ///     <c>for x in array</c>: a counting loop over the array, or the
        ///     slice, which is kept in a slot of its own so that every way out
        ///     of the loop releases it with the rest.
        /// </summary>
        void ForArray(BoundForEach loop) {
            if (loop.Element is null || Expression(loop.Sequence) is not { } sequence || ArrayType.ElementOf(sequence.Type) is not { } elementType
                || SlotFor(loop.Element, loop.Syntax) is null) {
                Fail(loop.Syntax, "this 'for' over an array");
                return;
            }

            var sequenceType = sequence.Type;
            var array = NewSlot(sequenceType);
            Emit(new StoreInst(sequence, array, StoreKind.Assign));
            var counter = NewSlot(SilType.Int);
            Emit(new StoreInst(Emit(new IntegerLiteralInst(SilType.Int, 0)), counter, StoreKind.Init));

            var header = function.AddBlock();
            var body = function.AddBlock();
            var step = function.AddBlock();
            var after = function.AddBlock();
            Branch(header);

            _current = header;
            var current = Emit(new LoadInst(counter));
            var held = Emit(new LoadInst(array));
            var count = Emit(new BuiltinInst("array_count", SilType.Int, held));
            Destroy(held);
            Jump(new CondBranchInst(Emit(new BuiltinInst("cmp_lt_Int", SilType.Bool, current, count)), body, [], after, []));

            _current = body;

            // The loop walks a copy of the array that nothing else can change,
            // so each element stays alive and as it is while it is the loop's:
            // the variable holds it unretained, read in place.
            if (!elementType.IsTrivial && !_boxed.Contains(loop.Element) && closure is null && Generator is null && !Moves(loop.Body, loop.Element)
                && elementType is not NominalType { IsNoncopyable: true } && SlotFor(loop.Element, loop.Syntax) is { } elementSlot) {
                elementSlot.Unmanaged = true;
                var lent = Emit(new LoadBorrowInst(array));
                var borrowed = Emit(new BuiltinInst("array_get_borrowed", elementType, lent, Emit(new LoadInst(counter))));
                Emit(new StoreInst(borrowed, elementSlot, StoreKind.Assign));
                Emit(new EndBorrowInst(lent));
            } else {
                var again = Emit(new LoadInst(array));
                var value = Emit(new BuiltinInst("array_get", elementType, again, Emit(new LoadInst(counter))));
                Destroy(again);
                BindLocal(loop.Element, value, loop.Syntax);
            }
            if (loop.Filter is { } filter) {
                var filtered = function.AddBlock();
                Condition(filter, filtered, step);
                _current = filtered;
            }

            InLoop(loop, after, step, () => Statement(loop.Body));
            if (_current is not null) {
                Branch(step);
            }

            _current = step;
            var one = Emit(new IntegerLiteralInst(SilType.Int, 1));
            Emit(new StoreInst(Emit(new BuiltinInst("add_Int", SilType.Int, Emit(new LoadInst(counter)), one)), counter, StoreKind.Assign));
            Branch(header);

            // The loop's copy goes as the loop ends: kept until the slot was
            // next written, a loop run again later held the last run's array
            // — all of its buffer — alive through everything in between.
            _current = after;
            Emit(new StoreInst(Emit(new BuiltinInst("array_empty", sequenceType)), array, StoreKind.Assign));
        }

        /// <summary>True when the code moves the parameter's value out: 'move p'.</summary>
        static bool MovesParameter(BoundNode node, ParameterSymbol parameter) =>
            node is BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Move, Operand: BoundParameterAccess moved } && moved.Parameter == parameter
            || node.Children.Any(child => MovesParameter(child, parameter));

        /// <summary>True when the code moves the local's value out: 'move x'.</summary>
        static bool Moves(BoundNode node, LocalSymbol local) =>
            node is BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Move, Operand: BoundLocalAccess moved } && moved.Local == local
            || node.Children.Any(child => Moves(child, local));

        void InLoop(BoundStatement loop, SilBlock breakTarget, SilBlock continueTarget, Action body) {
            var label = loop.Syntax.Parent is LabeledStatementSyntax { Label.Text: { Length: > 0 } text } ? text : null;
            _loops.Add(new(label, breakTarget, continueTarget, _scopes.Count, _temporaries.Count));
            body();
            _loops.RemoveAt(_loops.Count - 1);
        }

        Loop? FindLoop(string? label, bool continues = false) {
            for (var i = _loops.Count - 1; i >= 0; i--) {
                if (continues && _loops[i].Continue is null) {
                    continue;
                }

                if (label is null || _loops[i].Label == label) {
                    return _loops[i];
                }
            }

            return null;
        }

        /// <summary>
        ///     <c>switch</c>: the cases' values compared with the subject in
        ///     order, then <c>default</c> if there is one. A case runs to its
        ///     end, unless a <c>fallthrough</c> goes on into the next or a
        ///     <c>break</c> leaves early. The subject lives until a case is
        ///     chosen.
        /// </summary>
        void Switch(BoundSwitch switchStatement) {
            if (Expression(switchStatement.Subject) is not { } subject) {
                return;
            }

            var after = function.AddBlock();
            var bodies = switchStatement.Cases.Select(_ => function.AddBlock()).ToList();
            SilBlock? defaultBody = null;

            // An outcome per case, and one for none when there is no 'default'.
            var defaultIndex = switchStatement.Cases.IndexOf(switchStatement.Cases.FirstOrDefault(static c => c.Values.IsEmpty)!);
            var decision = switchStatement.Syntax is SwitchStatementSyntax written
                ? Decide(written.SwitchKeyword.SpanStart, "switch", [
                    ..switchStatement.Cases.Select(c => c.Syntax is SwitchCaseSyntax caseSyntax && !c.Values.IsEmpty
                        ? "case " + string.Join(", ", caseSyntax.Values.Select(TextOf))
                        : "default"),
                    ..(defaultIndex >= 0 ? Array.Empty<string>() : ["no case"]),
                ])
                : null;

            Hold(subject);
            for (var i = 0; i < switchStatement.Cases.Length; i++) {
                var switchCase = switchStatement.Cases[i];
                if (switchCase.Values.IsEmpty) {
                    defaultBody = bodies[i];
                    continue;
                }

                foreach (var value in switchCase.Values) {
                    if (CaseEquals(subject, value) is not { } equal) {
                        return;
                    }

                    var next = function.AddBlock();
                    Jump(new CondBranchInst(equal, Destroying(subject, Land(bodies[i], decision, i)), [], next, []));
                    _current = next;
                }
            }

            Unhold(subject);
            Destroy(subject);
            Branch(defaultBody is null ? Land(after, decision, switchStatement.Cases.Length) : Land(defaultBody, decision, defaultIndex));

            _loops.Add(new(null, after, null, _scopes.Count, _temporaries.Count));
            for (var i = 0; i < switchStatement.Cases.Length; i++) {
                _current = bodies[i];
                _fallthroughs.Add(i + 1 < bodies.Count ? new(bodies[i + 1], _scopes.Count, _temporaries.Count) : null);
                _scopes.Add([]);
                foreach (var statement in switchStatement.Cases[i].Statements) {
                    Statement(statement);
                }

                if (_current is not null) {
                    RunDefers(_scopes.Count - 1);
                    Branch(after);
                }

                _scopes.RemoveAt(_scopes.Count - 1);
                _fallthroughs.RemoveAt(_fallthroughs.Count - 1);
            }

            _loops.RemoveAt(_loops.Count - 1);
            _current = after;
        }

        /// <summary>
        ///     Whether a switch's subject is one of its case's values: by case
        ///     for an enum, and by value for a number, character or string.
        /// </summary>
        SilValue? CaseEquals(SilValue subject, BoundExpression value) {
            if (Expression(value) is not { } candidate) {
                return null;
            }

            SilValue? equal;
            if (IsPayloadFree(subject.Type) && candidate.Type == subject.Type) {
                equal = SameCase(subject, candidate, equal: true);
            } else if (candidate.Type == subject.Type && SilBuiltins.Find($"cmp_eq_{subject.Type}") is not null) {
                equal = Emit(new BuiltinInst($"cmp_eq_{subject.Type}", SilType.Bool, subject, candidate));
            } else {
                equal = Missing(value, $"a switch on '{value.Type.ToDisplayString()}'");
            }

            Destroy(candidate);
            return equal;
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
                Fail(doStatement.Syntax, "a 'do' whose catches take an error type that has no SIL type");
                return;
            }

            var dispatch = function.AddBlock();
            var error = dispatch.AddArgument(errorType);

            _handlers.Add(new(dispatch, errorType, _scopes.Count, _temporaries.Count));
            Statement(doStatement.Body);
            _handlers.RemoveAt(_handlers.Count - 1);
            if (_current is not null) {
                Branch(after);
            }

            // Each catch in turn; one that does not match, or whose guard
            // fails, passes the error to the next, and the last to whatever
            // handles errors outside. The error lives until a catch is chosen.
            _current = dispatch;
            foreach (var clause in doStatement.Catches) {
                var handler = function.AddBlock();
                var next = function.AddBlock();

                Hold(error);
                if (clause.Pattern is { } pattern) {
                    Pattern(pattern, error, handler, next);
                } else {
                    Branch(handler);
                }

                _current = handler;
                if (clause.ErrorLocal is { } local) {
                    BindLocal(local, error.Type.IsTrivial ? error : Emit(new CopyValueInst(error)), clause.Syntax);
                }

                if (clause.Guard is { } guard) {
                    var guarded = function.AddBlock();
                    Condition(guard, guarded, next);
                    _current = guarded;
                }

                Unhold(error);
                Destroy(error);
                Statement(clause.Body);
                if (_current is not null) {
                    Branch(after);
                }

                _current = next;
            }

            // Nothing caught it: on to whatever handles errors outside — unless
            // the last catch takes everything, when nothing gets this far.
            // Where nothing outside could take it, the binder has checked
            // that the catches cover every error.
            var last = doStatement.Catches[^1];
            if (last.Pattern is null && last.Guard is null || _handlers.Count == 0 && function.Type.Thrown is null) {
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
                    return LowerType(local.Type);
                }

                if (clause.Pattern is { } pattern) {
                    return LowerType(pattern.InputType);
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
                // Its outcomes: the right side run, or skipped.
                case BoundBinaryOperator { OperatorKind: BinaryOperatorKind.LogicalAnd } conjunction: {
                    var decision = conjunction.Syntax is BinaryExpressionSyntax written ? Decide(written.OperatorToken.SpanStart, "&&", "right side run", "right side skipped") : null;
                    var right = Mark(function.AddBlock(), decision, 0);
                    Condition(conjunction.Left, right, Land(whenFalse, decision, 1));
                    _current = right;
                    Condition(conjunction.Right, whenTrue, whenFalse);
                    return;
                }

                case BoundBinaryOperator { OperatorKind: BinaryOperatorKind.LogicalOr } disjunction: {
                    var decision = disjunction.Syntax is BinaryExpressionSyntax written ? Decide(written.OperatorToken.SpanStart, "||", "right side run", "right side skipped") : null;
                    var right = Mark(function.AddBlock(), decision, 0);
                    Condition(disjunction.Left, Land(whenTrue, decision, 1), right);
                    _current = right;
                    Condition(disjunction.Right, whenTrue, whenFalse);
                    return;
                }

                case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Not } negation:
                    Condition(negation.Operand, whenFalse, whenTrue);
                    return;

                // 'if let x = e': matched when e holds a value, which x then is.
                case BoundConditionBinding { Local: { } local } binding: {
                    // The slot ends with its scope before the source runs: in
                    // 'while let x = try it.next()', an error from the second
                    // 'next()' leaves with the first x still in the slot.
                    if (SlotFor(local, binding.Syntax) is not { } bindingSlot) {
                        return;
                    }

                    if (!_boxed.Contains(local)) {
                        EndsWithScope(bindingSlot);
                    }

                    if (Expression(binding.Source) is not { } optional) {
                        return;
                    }

                    var optionalType = optional.Type as EnumType;
                    var some = optionalType?.CaseIndex("Some") ?? -1;
                    if (optionalType is null || some < 0) {
                        Fail(binding.Syntax, "an 'if let' on something that is not optional");
                        return;
                    }

                    var decision = Decide(binding.Syntax.Span.Start, "let", "some", "none");
                    var bound = Mark(function.AddBlock(), decision, 0);
                    var payload = Payload(bound, optionalType.Cases[some].Payload);
                    Jump(new SwitchEnumInst(optional, [(some, bound)], Destroying(optional, Land(whenFalse, decision, 1))));
                    _current = bound;
                    BindLocal(local, payload, binding.Syntax);
                    Destroy(optional);
                    Branch(whenTrue);
                    return;
                }

                // 'if case .Circle(let r) = shape'.
                case BoundConditionBinding { Pattern: { } pattern } binding: {
                    if (Expression(binding.Source) is { } value) {
                        var decision = Decide(binding.Syntax.Span.Start, "case", "matched", "not matched");
                        Hold(value);
                        Pattern(pattern, value, Destroying(value, Land(whenTrue, decision, 0)), Destroying(value, Land(whenFalse, decision, 1)));
                        Unhold(value);
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
        ///     The value is borrowed; a binding stores a copy.
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
                    if (SlotFor(binding.Local, binding.Syntax) is not null) {
                        // A function a generic union keeps in its own convention, as the one it has here.
                        if (value.Type is ThickFunctionType && LowerType(binding.Local.Type) is ThickFunctionType here && here != value.Type) {
                            BindLocal(binding.Local, AsFunction(value, here), binding.Syntax);
                            Branch(matched);
                            return;
                        }

                        // A value held while the pattern runs — a match's
                        // subject, which the match destroys once the arm is
                        // chosen — is still its holder's, so the binding keeps
                        // a copy; storing it would end its life twice. A value
                        // the pattern made for the binding (an element, the
                        // rest of an array) is the binding's to keep.
                        var held = !value.Type.IsTrivial && value is not UnitValue && _temporaries.Contains(value);
                        BindLocal(binding.Local, held ? Emit(new CopyValueInst(value)) : value, binding.Syntax);
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
                    Destroy(constant);
                    Jump(new CondBranchInst(equal, matched, [], failed, []));
                    return;
                }

                // '1...9', '0..<10', '...0', '5...': a bound left out is no
                // test at all, and '..<' leaves its upper bound out.
                case BoundRangePattern { Lower: null or BoundLiteralPattern, Upper: null or BoundLiteralPattern } range: {
                    if (range.Lower is BoundLiteralPattern lower) {
                        if (LiteralValue(lower.Value, lower.IsNegative, value.Type, range.Syntax) is not { } low) {
                            return;
                        }

                        var aboveLow = function.AddBlock();
                        var atLeast = Emit(new BuiltinInst($"cmp_ge_{value.Type}", SilType.Bool, value, low));
                        Jump(new CondBranchInst(atLeast, aboveLow, [], failed, []));
                        _current = aboveLow;
                    }

                    if (range.Upper is BoundLiteralPattern upper) {
                        if (LiteralValue(upper.Value, upper.IsNegative, value.Type, range.Syntax) is not { } high) {
                            return;
                        }

                        var below = Emit(new BuiltinInst($"{(range.IsHalfOpen ? "cmp_lt" : "cmp_le")}_{value.Type}", SilType.Bool, value, high));
                        Jump(new CondBranchInst(below, matched, [], failed, []));
                        return;
                    }

                    Branch(matched);
                    return;
                }

                case BoundTuplePattern tuple when value.Type is TupleType:
                    Sequence(
                        tuple.Elements.Select((element, i) => (element, (Func<SilValue>)(() => Emit(new TupleExtractInst(value, i))))),
                        matched,
                        failed
                    );
                    return;

                case BoundArrayPattern array when ArrayType.ElementOf(value.Type) is { } elementType:
                    ArrayPattern(array, value, elementType, matched, failed);
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
                    var payload = payloadType is null ? null : Payload(target, payloadType);
                    Jump(new SwitchEnumInst(value, [(index, target)], failed));
                    _current = target;

                    // An indirect case: the payload is borrowed out of its box
                    // for as long as the sub-patterns look at it.
                    if (payload is not null && !unionCase.Payload.IsEmpty && generator.IsIndirect(enumType, index)) {
                        var boxed = Emit(new RefElementAddrInst(payload, 0));
                        if (((ClassType)payload.Type).Fields[0].Type.IsTrivial) {
                            PayloadPattern(unionCase, Emit(new LoadInst(boxed)), matched, failed);
                            return;
                        }

                        var inner = Emit(new LoadBorrowInst(boxed));
                        var innerMatched = function.AddBlock();
                        var innerFailed = function.AddBlock();
                        PayloadPattern(unionCase, inner, innerMatched, innerFailed);

                        _current = innerMatched;
                        Emit(new EndBorrowInst(inner));
                        Branch(matched);

                        _current = innerFailed;
                        Emit(new EndBorrowInst(inner));
                        Branch(failed);
                        return;
                    }

                    if (payload is null || unionCase.Payload.IsEmpty) {
                        Branch(matched);
                    } else {
                        PayloadPattern(unionCase, payload, matched, failed);
                    }

                    return;
                }

                // '.Some(p)' and 'p?': the payload, if there is one, against p.
                case BoundSomePattern some when value.Type is EnumType optional && optional.CaseIndex("Some") is var index and >= 0: {
                    var target = function.AddBlock();
                    var payload = Payload(target, optional.Cases[index].Payload);
                    Jump(new SwitchEnumInst(value, [(index, target)], failed));
                    _current = target;
                    Pattern(some.Payload, payload, matched, failed);
                    return;
                }

                case BoundEnumCasePattern enumCase when value.Type is EnumType enumType:
                    Jump(new SwitchEnumInst(value, [(enumType.CaseIndex(enumCase.EnumCase.Name), matched)], failed));
                    return;

                // A '@flags' enum's case, matched: the value is exactly it.
                case BoundEnumCasePattern flag when SilBuiltins.Find($"cmp_eq_{value.Type}") is not null: {
                    var same = Emit(new BuiltinInst($"cmp_eq_{value.Type}", SilType.Bool, value, Emit(new IntegerLiteralInst(value.Type, flag.EnumCase.RawValue))));
                    Jump(new CondBranchInst(same, matched, [], failed, []));
                    return;
                }

                case BoundOrPattern or: {
                    var right = function.AddBlock();
                    Pattern(or.Left, value, matched, right);
                    _current = right;
                    Pattern(or.Right, value, matched, failed);
                    return;
                }

                // 'let e as IoError' on a union: the case that carries it.
                case BoundTypeTestPattern test when generator.IsUnion(value.Type) && LowerType(test.TestedType) is { } tested
                                                    && ((EnumType)value.Type).Cases.FindIndex(c => c.Payload == tested) is var index and >= 0: {
                    var target = function.AddBlock();
                    var payload = Payload(target, tested);
                    Jump(new SwitchEnumInst(value, [(index, target)], failed));
                    _current = target;
                    if (test.Local is { } local) {
                        BindLocal(local, payload, test.Syntax);
                    }

                    Branch(matched);
                    return;
                }

                // ... and one only its metadata knows: asked of the box, and copied out of it.
                case BoundTypeTestPattern test when _genericMetadata.Count > 0 && Mentions(test.TestedType, [.._genericMetadata.Keys])
                                                    && (value.Type == generator.AnyErrorType() || generator.IsExistentialLayout(value.Type))
                                                    && LowerType(test.TestedType) is { } held && MetadataOf(Sub(test.TestedType)) is { } heldMetadata: {
                    var slot = NewSlot(held);
                    var isOne = Emit(new BuiltinInst("existential_cast", SilType.Bool, value.Type is ClassType ? value : Emit(new StructExtractInst(value, 0)), heldMetadata, slot));
                    var target = function.AddBlock();
                    Jump(new CondBranchInst(isOne, target, [], failed, []));
                    _current = target;
                    if (test.Local is { } local) {
                        BindLocal(local, Emit(new LoadInst(slot, LoadKind.Take)), test.Syntax);
                    } else {
                        Emit(new DestroyAddrInst(slot));
                    }

                    Branch(matched);
                    return;
                }

                // ... and on 'any Error': the object, if it boxes one.
                case BoundTypeTestPattern test when value.Type == generator.AnyErrorType() && LowerType(test.TestedType) is { } tested: {
                    var box = generator.ErrorBox(tested);
                    var isA = Emit(new BuiltinInst($"class_is:{box.Name}", SilType.Bool, value));
                    var target = function.AddBlock();
                    Jump(new CondBranchInst(isA, target, [], failed, []));
                    _current = target;
                    if (test.Local is { } local) {
                        BindLocal(local, Emit(new LoadInst(Emit(new RefElementAddrInst(Emit(new UncheckedRefCastInst(value, box)), 0)))), test.Syntax);
                    }

                    Branch(matched);
                    return;
                }

                // ... and on 'any P': the value, if the existential's box holds one.
                case BoundTypeTestPattern test when generator.IsExistentialLayout(value.Type): {
                    var target = CastTarget(test.TestedType);
                    if (generator.ExistentialCast(target, test.Syntax) is not var (name, type) || type.Result is not EnumType result) {
                        Fail(pattern.Syntax, $"the pattern '{Describe(pattern.Syntax)}'");
                        return;
                    }

                    var cast = Emit(new ApplyInst(Emit(new FunctionRefInst(name, type)), result, [Emit(new StructExtractInst(value, 0))]));
                    var some = result.CaseIndex("Some");
                    var bound = function.AddBlock();
                    var payload = Payload(bound, result.Cases[some].Payload);
                    Jump(new SwitchEnumInst(cast, [(some, bound)], Destroying(cast, failed)));
                    _current = bound;
                    if (test.Local is { } local) {
                        BindLocal(local, Emit(new CopyValueInst(payload)), test.Syntax);
                    }

                    Destroy(cast);
                    Branch(matched);
                    return;
                }

                default:
                    Fail(pattern.Syntax, $"the pattern '{Describe(pattern.Syntax)}'");
                    return;
            }
        }

        /// <summary>
        ///     <c>[a, b]</c> matches an array of exactly two elements;
        ///     <c>[first, ...rest, last]</c> one of at least two, whose middle
        ///     <c>rest</c> is a slice of, sharing its buffer. The array may be a
        ///     slice itself. The elements are borrowed where they
        ///     are, as the array is held for as long as the patterns look; the
        ///     middle is taken last, once every element has matched, so a
        ///     failed match has nothing to give back.
        /// </summary>
        void ArrayPattern(BoundArrayPattern array, SilValue value, SilType elementType, SilBlock matched, SilBlock failed) {
            var restIndex = array.RestIndex;
            var prefix = restIndex < 0 ? array.Elements.Length : restIndex;
            var suffix = restIndex < 0 ? 0 : array.Elements.Length - restIndex - 1;

            var count = Emit(new BuiltinInst("array_count", SilType.Int, value));
            var length = Emit(new IntegerLiteralInst(SilType.Int, prefix + suffix));
            var fits = Emit(new BuiltinInst(restIndex < 0 ? "cmp_eq_Int" : "cmp_ge_Int", SilType.Bool, count, length));
            var sized = function.AddBlock();
            Jump(new CondBranchInst(fits, sized, [], failed, []));
            _current = sized;

            SilValue Element(Func<SilValue> index) =>
                Emit(new BuiltinInst("array_get_borrowed", elementType, value, index()));

            SilValue FromStart(int i) => Emit(new IntegerLiteralInst(SilType.Int, i));

            SilValue FromEnd(int back) =>
                Emit(new BuiltinInst("sub_Int", SilType.Int, count, Emit(new IntegerLiteralInst(SilType.Int, back))));

            var parts = new List<(BoundPattern, Func<SilValue>)>();
            for (var i = 0; i < prefix; i++) {
                var at = i;
                parts.Add((array.Elements[at], () => Element(() => FromStart(at))));
            }

            for (var j = 0; j < suffix; j++) {
                var back = suffix - j;
                parts.Add((array.Elements[restIndex + 1 + j], () => Element(() => FromEnd(back))));
            }

            if (restIndex >= 0 && array.Elements[restIndex] is not BoundWildcardPattern) {
                parts.Add((array.Elements[restIndex],
                    () => Emit(new BuiltinInst("array_slice", new SliceType(elementType), value, FromStart(prefix), FromEnd(suffix)))));
            }

            Sequence(parts, matched, failed);
        }

        /// <summary>
        ///     A union case's sub-patterns, against its payload: the one field
        ///     itself, a tuple or not, or a tuple of the fields.
        /// </summary>
        void PayloadPattern(BoundUnionCasePattern unionCase, SilValue payload, SilBlock matched, SilBlock failed) {
            if (unionCase.Payload.Length == 1 && unionCase.UnionCase.Payload.Length == 1) {
                Pattern(unionCase.Payload[0], payload, matched, failed);
            } else {
                Sequence(
                    unionCase.Payload.Select((sub, i) => (sub, (Func<SilValue>)(() => Emit(new TupleExtractInst(payload, i))))),
                    matched,
                    failed
                );
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

        /// <summary>Lowers an expression to an owned value — or a trivial one, or <see cref="UnitValue" />.</summary>
        SilValue? Expression(BoundExpression expression) {
            // An expression that can trap — an index, arithmetic, an unwrap, a cast —
            // is where its instructions are, so that the trap names its
            // column; what follows is back where the statement was.
            if (expression is BoundIndexAccess or BoundBinaryOperator or BoundUnaryOperator or BoundConversion { ConversionKind: ConversionKind.Explicit }
                && _location is not null) {
                var around = _location;
                Locate(expression.Syntax);
                try {
                    return ExpressionHere(expression);
                } finally {
                    _location = around;
                }
            }

            return ExpressionHere(expression);
        }

        SilValue? ExpressionHere(BoundExpression expression) {
            if (_evaluated.Count > 0 && _evaluated.TryGetValue(expression, out var once)) {
                return once.Type.IsTrivial ? once : Emit(new CopyValueInst(once));
            }

            if (UsesLent(expression)) {
                return null;
            }

            if (_failed) {
                return null;
            }

            // A binding a test narrowed out of a union: the case's payload (SilGen.Unions.cs).
            if (NarrowedUnion(expression) is var (whole, unionCases)) {
                return NarrowedRead(expression, whole, unionCases);
            }

            switch (expression) {
                case BoundLiteral literal:
                    return Literal(literal, literal.Type);

                case BoundConversion { ConversionKind: ConversionKind.UnionInjection } injection:
                    return Inject(injection);

                case BoundInterpolatedString interpolated:
                    return Interpolation(interpolated);

                case BoundHoleText text:
                    return text.Debug ? DebugText(text.Value) : Hole(text.Value, null);

                case BoundInterpolationHole hole:
                    return Hole(hole.Value, hole.Format);

                case BoundFormatSpec spec:
                    return Expression(spec.Value);

                // An array where a slice of it is wanted: a view of it (SilGen.Slices.cs).
                case BoundConversion { ConversionKind: ConversionKind.Identity } view when IsArrayView(view):
                    return ArrayView(view);

                case BoundConversion { ConversionKind: ConversionKind.Identity } conversion:
                    return Expression(conversion.Operand);

                // 'Never' standing for a value — 'case .A => unreachable()':
                // what makes it does not return, so nothing after it runs and
                // no value of the wanted type is ever made.
                case BoundConversion { ConversionKind: ConversionKind.Bottom } bottom:
                    if (Expression(bottom.Operand) is null) {
                        return null;
                    }

                    if (_current is not null) {
                        Jump(new UnreachableInst());
                    }

                    return UnitValue.Instance;

                // A literal taking the type the context asked for: 'let x: F64 = 1'.
                case BoundConversion { ConversionKind: ConversionKind.LiteralTyping, Operand: BoundLiteral literal } conversion:
                    return Literal(literal, conversion.Type);

                // An array literal taking the kind of array the context asked for.
                case BoundConversion { ConversionKind: ConversionKind.LiteralTyping, Operand: BoundArray array } conversion:
                    return ArrayLiteral(array, conversion.Type);

                // 'x?.member' and 'x?.call()': reached through the optional.
                case BoundFieldAccess { Receiver: { } chainedFrom, Type: OptionalTypeSymbol reached } chained
                    when IsChainedThrough(chainedFrom, chained.Field):
                    return OptionalChain(
                        chained,
                        chainedFrom,
                        through => new BoundFieldAccess(chained.Syntax, through, chained.Field, chained.FlattensChain ? reached : reached.UnderlyingType),
                        flatten: chained.FlattensChain
                    );

                // 'pair?.0': the element, reached through the optional.
                case BoundTupleElementAccess { Receiver: { } chainedFrom } chainedElement
                    when Sub(chainedFrom.Type) is OptionalTypeSymbol { UnderlyingType: TupleTypeSymbol chainedTuple }: {
                    var elementType = chainedTuple.ElementTypes[chainedElement.Index];
                    return OptionalChain(
                        chainedElement,
                        chainedFrom,
                        through => new BoundTupleElementAccess(chainedElement.Syntax, elementType, through, chainedElement.Index),
                        flatten: Sub(elementType) is OptionalTypeSymbol
                    );
                }

                case BoundConversion { ConversionKind: ConversionKind.OptionalWrap, Operand: BoundCall { Receiver: { } chainedFrom } chainedCall }
                    when IsChainedThrough(chainedFrom, chainedCall.Function):
                    return OptionalChain(
                        expression,
                        chainedFrom,
                        through => new BoundCall(chainedCall.Syntax, through, chainedCall.Function, chainedCall.Arguments, chainedCall.Type) {
                            Substitution = chainedCall.Substitution,
                        }
                    );

                // 'x?[i]': the binder unwraps x to index it, and wraps what it
                // gives; indexed only when there is an x, and none when not.
                case BoundConversion {
                        ConversionKind: ConversionKind.OptionalWrap,
                        Operand: BoundIndexAccess { Receiver: BoundConversion { ConversionKind: ConversionKind.Explicit, Operand: { Type: OptionalTypeSymbol } chainedFrom } } chainedIndex,
                    } when chainedIndex.Syntax is ElementAccessExpressionSyntax { QuestionToken.IsNone: false }:
                    return OptionalChain(
                        expression,
                        chainedFrom,
                        through => new BoundIndexAccess(chainedIndex.Syntax, chainedIndex.Type, through, chainedIndex.Indices) {
                            Subscript = chainedIndex.Subscript,
                            Substitution = chainedIndex.Substitution,
                        }
                    );

                // 'd?[k]' of a dictionary, whose indexing gives an optional
                // already: that one, when there is a d.
                case BoundIndexAccess { Receiver: BoundConversion { ConversionKind: ConversionKind.Explicit, Operand: { Type: OptionalTypeSymbol } chainedFrom }, Type: OptionalTypeSymbol } flatIndex
                    when flatIndex.Syntax is ElementAccessExpressionSyntax { QuestionToken.IsNone: false }:
                    return OptionalChain(
                        expression,
                        chainedFrom,
                        through => new BoundIndexAccess(flatIndex.Syntax, flatIndex.Type, through, flatIndex.Indices) {
                            Subscript = flatIndex.Subscript,
                            Substitution = flatIndex.Substitution,
                        },
                        flatten: true
                    );

                // 'x?.next()' of a method that gives an optional: that optional,
                // when there is an x.
                case BoundCall { Receiver: { } chainedFrom, Type: OptionalTypeSymbol } flatCall
                    when IsChainedThrough(chainedFrom, flatCall.Function)
                    && flatCall.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { OperatorToken.Kind: SyntaxKind.QuestionDotToken } }:
                    return OptionalChain(
                        expression,
                        chainedFrom,
                        through => new BoundCall(flatCall.Syntax, through, flatCall.Function, flatCall.Arguments, flatCall.Type) {
                            Substitution = flatCall.Substitution,
                        },
                        flatten: true
                    );

                // 'f?(x)': the function called when there is one, its result
                // wrapped — or, when it gives an optional, that one.
                case BoundConversion { ConversionKind: ConversionKind.OptionalWrap, Operand: BoundIndirectCall { Callee: { Type: OptionalTypeSymbol } optionalCallee } wrappedCall }:
                    return OptionalChain(
                        expression,
                        optionalCallee,
                        through => new BoundIndirectCall(wrappedCall.Syntax, wrappedCall.Type, through, wrappedCall.Arguments)
                    );

                case BoundIndirectCall { Callee: { Type: OptionalTypeSymbol } optionalCallee, Type: OptionalTypeSymbol } flatIndirect:
                    return OptionalChain(
                        expression,
                        optionalCallee,
                        through => new BoundIndirectCall(flatIndirect.Syntax, flatIndirect.Type, through, flatIndirect.Arguments),
                        flatten: true
                    );

                case BoundConversion { ConversionKind: ConversionKind.OptionalWrap } conversion:
                    return Wrap(conversion);

                // A call's result of the type it opened a Type as, erased (SilGen.Opening.cs).
                case BoundConversion { ConversionKind: ConversionKind.OpenedErasure, Operand: BoundCall erased } erasure:
                    return OpenedCall(erased, erasure);

                // The Type a call opens: its metadata, evaluated as the call began.
                case BoundConversion { ConversionKind: ConversionKind.Open, Type: MetatypeTypeSymbol { InstanceType: TypeParameterSymbol openedType } } open:
                    return _genericMetadata.TryGetValue(openedType, out var openedMetadata) ? openedMetadata : Missing(open, "a Type opened outside its call");

                // A value passed as 'some P' is passed as it is: the callee is
                // an instance for its type.
                case BoundConversion { ConversionKind: ConversionKind.Existential, Type: OpaqueTypeSymbol } opened:
                    return Expression(opened.Operand);

                // An error as 'any Error': boxed, keeping what it was.
                case BoundConversion { ConversionKind: ConversionKind.Existential } toError when Sub(toError.Type).Equals(Core.AnyError): {
                    if (Expression(toError.Operand) is not { } error) {
                        return null;
                    }

                    return Convert(Owned(error), generator.AnyErrorType(), toError.Syntax);
                }

                case BoundConversion { ConversionKind: ConversionKind.Existential } conversion:
                    return MakeExistential(conversion);

                // A reference read: what it refers to, copied.
                case BoundConversion { ConversionKind: ConversionKind.Dereference } dereference:
                    return Expression(dereference.Operand) is { Type: AddressType } address ? Emit(new LoadInst(address)) : null;

                // A place where a reference is wanted, or '&x': its address.
                case BoundConversion { ConversionKind: ConversionKind.Reference } reference:
                    return ReferenceTo(reference.Operand);

                case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.AddressOf, Operand: var target }:
                    return ReferenceTo(target);

                // An object as one of its superclasses: the same object, owned as that.
                // A function as a function type that throws more: a thunk.
                case BoundConversion { ConversionKind: ConversionKind.Upcast } functionConversion when Sub(functionConversion.Type) is FunctionTypeSymbol:
                    return FunctionConversion(functionConversion);

                case BoundConversion { ConversionKind: ConversionKind.Upcast } conversion: {
                    if (LowerType(conversion.Type) is not ClassType target || Expression(conversion.Operand) is not { Type: ClassType } value) {
                        return Missing(conversion, $"the conversion to '{conversion.Type.ToDisplayString()}'");
                    }

                    var upcast = Emit(new CopyValueInst(Emit(new UpcastInst(value, target))));
                    Destroy(value);
                    return upcast;
                }

                // 'x!': the value, or a trap.
                case BoundConversion { ConversionKind: ConversionKind.Explicit, Operand.Type: OptionalTypeSymbol } conversion:
                    return Unwrap(conversion);

                // 'error as? IoError', of any error: the one it boxes.
                case BoundConversion { ConversionKind: ConversionKind.Explicit, Syntax: CastExpressionSyntax { QuestionOrBangToken.IsNone: false } } ofError
                    when IsAnyError(ofError.Operand.Type) && CastTarget(ofError.Type is OptionalTypeSymbol named ? named.UnderlyingType : ofError.Type) is not ExistentialTypeSymbol:
                    return ErrorCast(ofError);

                // 'x as? Dog' and 'x as! Dog': a downcast, checked as the program runs.
                case BoundConversion { ConversionKind: ConversionKind.Explicit } downcast when IsDowncast(downcast):
                    return Downcast(downcast);

                // 't as? any P.Type': the type itself, if it conforms.
                case BoundConversion { ConversionKind: ConversionKind.Explicit, Syntax: CastExpressionSyntax { QuestionOrBangToken.IsNone: false } } metatype
                    when CastTo(metatype) is ExistentialTypeSymbol { MetatypeOf: { } conformed } && LowerType(metatype.Operand.Type) == SilType.Metadata:
                    return MetatypeDowncast(metatype, conformed);

                // 'error as? IoError': what an 'any Error' boxes.
                case BoundConversion { ConversionKind: ConversionKind.Explicit, Syntax: CastExpressionSyntax { QuestionOrBangToken.IsNone: false } } thrown when IsErrorCast(thrown):
                    return ErrorDowncast(thrown);

                // 'provider as? T', T here a type no object is a value of.
                case BoundConversion { ConversionKind: ConversionKind.Explicit, Syntax: CastExpressionSyntax { QuestionOrBangToken.IsNone: false } } never when IsNeverCast(never):
                    return Expression(never.Operand) is { } unlike ? SettledCast(never, unlike, null, CastTo(never)) : null;

                // 'value as? any P', of a value only its metadata knows.
                case BoundConversion { ConversionKind: ConversionKind.Explicit, Syntax: CastExpressionSyntax { QuestionOrBangToken.IsNone: false } } erased
                    when CastTo(erased) is ExistentialTypeSymbol { MetatypeOf: null } or NamedTypeSymbol { TypeKind: not TypeKind.Protocol } && IsOpaqueHere(erased.Operand.Type):
                    return OpaqueAsExistential(erased);

                // 'value as? T', T here a type only its metadata knows.
                case BoundConversion { ConversionKind: ConversionKind.Explicit, Syntax: CastExpressionSyntax { QuestionOrBangToken.IsNone: false } } held
                    when _genericMetadata.Count > 0 && (IsExistential(held.Operand.Type) || IsAnyError(held.Operand.Type)) && Mentions(CastTo(held), [.._genericMetadata.Keys]):
                    return OpaqueDowncast(held);

                // 'shape as? Square', 'shape as? any Named': asked of the existential's box;
                // 'animal as? any Named', of a box made for the object.
                case BoundConversion { ConversionKind: ConversionKind.Explicit } opened when IsExistential(opened.Operand.Type) || IsObjectToProtocol(opened):
                    return ExistentialDowncast(opened);

                case BoundTypeTest test:
                    return TypeTest(test);

                // 'N' of 'Buf<T, N: Int>': the number this instance gives it.
                case BoundValueParameter valueParameter:
                    return Sub(valueParameter.Parameter) is ValueTypeArgumentSymbol number && LowerType(valueParameter.Type) is { } numberType
                        ? Emit(new IntegerLiteralInst(numberType, number.Value))
                        : Missing(valueParameter, $"'{valueParameter.Parameter.Name}' with its number unknown here");

                case BoundConversion { ConversionKind: ConversionKind.Explicit, Syntax: CastExpressionSyntax { QuestionOrBangToken.IsNone: false } } generic
                    when IsKnownHere(generic.Operand.Type):
                    return KnownCast(generic);

                // 'x as! C.Index' in unspecialised code, between two types only their metadata knows.
                case BoundConversion { ConversionKind: ConversionKind.Explicit, Syntax: CastExpressionSyntax { QuestionOrBangToken.IsNone: false } } opaqueCast
                    when Sub(opaqueCast.Type) is not OptionalTypeSymbol && IsOpaqueHere(opaqueCast.Operand.Type) && IsOpaqueHere(opaqueCast.Type):
                    return OpaqueForcedCast(opaqueCast);

                // 'elements as? Element[>..]' there, of a value only its metadata knows.
                case BoundConversion { ConversionKind: ConversionKind.Explicit, Syntax: CastExpressionSyntax { QuestionOrBangToken.IsNone: false } } heldCast
                    when IsOpaqueHere(heldCast.Operand.Type) && CastTo(heldCast) is ArrayTypeSymbol or OptionalTypeSymbol or TupleTypeSymbol or NamedTypeSymbol { IsConstructed: true, TypeKind: TypeKind.Struct or TypeKind.Union }
                         && Mentions(CastTo(heldCast), [.._genericMetadata.Keys]):
                    return OpaqueHeldCast(heldCast);

                case BoundConversion { ConversionKind: ConversionKind.Explicit } conversion:
                    return NumericConversion(conversion);

                case BoundParameterAccess access when _spilled.TryGetValue(access.Parameter, out var spilledSlot):
                    return Emit(new LoadInst(spilledSlot));

                // 'move x': the variable's value, taken; the variable is then empty.
                case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Move, Operand: var moved }:
                    return Move(moved);

                case BoundParameterAccess access when _parameters.TryGetValue(access.Parameter, out var parameter):
                    return IsByAddress(access.Parameter) ? Emit(new LoadInst(parameter)) : Owned(parameter);

                // A loop's element of an inline array, wanted as a value: a block of its own.
                case BoundLocalAccess access when _views.Contains(access.Local):
                    return Emit(new BuiltinInst("inline_copy", ((InlinePointerType)_slots[access.Local].ElementType).Pointee, Emit(new LoadInst(_slots[access.Local]))));

                case BoundLocalAccess access when _asyncLets.TryGetValue(access.Local, out var asyncLet):
                    return AsyncLetValue(access, asyncLet);

                case BoundLocalAccess access:
                    return LoadLocal(access);

                case BoundParameterAccess access when closure?.Captures.TryGetValue(access.Parameter, out var captured) == true: {
                    var borrows = new List<SilValue>();
                    var value = Emit(new LoadInst(CapturedParameterAddress(captured, borrows)));
                    EndAll(borrows);
                    return value;
                }

                case BoundClosure closureExpression:
                    return Closure(closureExpression);

                case BoundFunctionReference reference:
                    return FunctionValue(reference);

                // 'Int.self': the type is known here, so its metadata is a constant.
                case BoundMetatype metatype:
                    return MetatypeValue(metatype);

                case BoundGenericType generic:
                    return GenericTypeValue(generic);

                case BoundIndirectCall indirect:
                    return IndirectCall(indirect);

                case BoundSelfAccess:
                    if (_selfValue is { } selfValue) {
                        return Owned(selfValue);
                    }

                    if (_selfAddress is { } selfSlot) {
                        return Emit(new LoadInst(selfSlot));
                    }

                    return CapturedSelf() ?? Missing(expression, "'self' here");

                case BoundAssignment assignment:
                    return AssignmentValue(assignment);

                // '_ = f()': nothing is left of it once the value is made.
                case BoundDiscard discard: {
                    if (Expression(discard.Value) is not { } dropped) {
                        return null;
                    }

                    Destroy(dropped);
                    return Emit(new TupleInst(new TupleType([])));
                }

                // 'task.value', Sable.Core's @intrinsic async getter.
                case BoundFieldAccess { Field: { Name: "value", IsAsyncGetter: true, ContainingSymbol: var task } } taskValue
                    when task is not null && ReferenceEquals(task, Core.Task):
                    return TaskValue(taskValue);

                case BoundFieldAccess { Field: { Name: "handle", ContainingSymbol: var task } } taskHandle
                    when task is not null && ReferenceEquals(task, Core.Task):
                    return HandleOf(taskHandle);

                // A constant: its value, computed as the program was compiled.
                case BoundFieldAccess { Field: var constantField } when generator.ConstantRead(constantField) is { } constant
                    && LowerType(constantField.Type) is { } constantType:
                    return Constant(constant, constantType, expression.Syntax);

                // A global: read where it lives.
                case BoundFieldAccess { Field: var globalField } when SilGen.IsGlobal(globalField) && generator.GlobalOf(globalField) is var (globalName, globalType):
                    return Emit(new LoadInst(Emit(new GlobalAddrInst(globalName, globalType))));

                case BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: true, Name: var member } } when IsInlineArray(receiver):
                    return InlineArrayMember(receiver, member, expression);

                // A property requirement on a type parameter of unspecialised code: through its table.
                case BoundFieldAccess opaqueProperty when GenericPropertyOf(opaqueProperty) is { } opaqueOwner:
                    return GenericPropertyRead(opaqueProperty, opaqueOwner);

                case BoundFieldAccess { Field.Builtin: not null } builtin:
                    return BuiltinProperty(builtin);

                // 'T.zero': a protocol's static property, on the type T stands for here.
                case BoundFieldAccess { Receiver: BoundTypeExpression on, Field.IsStatic: true } access
                    when access.Field.ContainingSymbol is NamedTypeSymbol { TypeKind: TypeKind.Protocol }
                        or ExtensionSymbol { ExtendedType: NamedTypeSymbol { TypeKind: TypeKind.Protocol } }:
                    return StaticPropertyWitness(access, on);

                // 'F64.pi': a static property's getter, called with nothing.
                // On a generic type named with its arguments, 'MemoryLayout<Int>.size',
                // the getter is the instance for them.
                case BoundFieldAccess { Field: { IsComputed: true, IsStatic: true } } access when generator.GetterOf(access.Field) is { } staticGetter:
                    return Expression(new BoundCall(access.Syntax, null, staticGetter, [], access.Type) {
                        Substitution = access.Substitution,
                    });

                case BoundFieldAccess { Receiver: { } receiver, Field.IsComputed: true } access:
                    return ComputedProperty(access, receiver);

                case BoundFieldAccess access when IsInlineMember(access):
                    return InlineRead(access);

                case BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } field } access:
                    return AsRead(FieldAccess(access, receiver, field), access);

                case BoundTupleElementAccess element: {
                    if (Sub(element.Receiver.Type) is TupleTypeSymbol && !IsTrivialType(element.Receiver.Type) && Place(element) is { } place) {
                        return Emit(new LoadInst(place));
                    }

                    if (Borrow(element.Receiver) is not { Type: TupleType } elementOf) {
                        return Missing(expression, "this tuple element");
                    }

                    var extracted = Owned(Emit(new TupleExtractInst(elementOf, element.Index)));
                    End(elementOf);
                    return extracted;
                }

                case BoundIndexAccess index:
                    return IndexRead(index);

                case BoundBuiltin builtin:
                    return CompilerBuiltin(builtin);

                case BoundTuple tuple: {
                    if (Evaluate(tuple.Elements) is not { } elements) {
                        return null;
                    }

                    return Emit(new TupleInst(new TupleType([..elements.Select(static e => e.Type)]), [..elements]));
                }

                case BoundArray array:
                    return ArrayLiteral(array, array.Type);

                case BoundDictionary dictionary:
                    return DictionaryLiteral(dictionary);

                case BoundRecordExpression record:
                    return Record(record);

                case BoundRange range:
                    return RangeValue(range);

                case BoundUnionCaseConstruction construction:
                    return UnionCase(construction.Type, construction.UnionCase.Name, construction.Arguments, expression);

                case BoundUnionCaseGroup group:
                    return UnionCase(group.Type, group.UnionCase.Name, [], expression);

                case BoundBinaryOperator { OperatorKind: BinaryOperatorKind.LogicalAnd or BinaryOperatorKind.LogicalOr }:
                case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Not, Type: var notType } when notType.Equals(Core.Bool):
                    return BoolFromBranch(expression);

                case BoundBinaryOperator { OperatorKind: BinaryOperatorKind.Coalesce } coalesce:
                    return Coalesce(coalesce);

                // Two arrays or optionals of a type parameter's values in unspecialised code, neither the literal none.
                case BoundBinaryOperator { Method: null, OperatorKind: BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual } held
                    when _genericMetadata.Count > 0 && held.Left is not BoundLiteral { Value: null } && held.Right is not BoundLiteral { Value: null }
                         && Sub(held.Left.Type) is ArrayTypeSymbol or OptionalTypeSymbol && Mentions(Sub(held.Left.Type), [.._genericMetadata.Keys]) && !IsNoneLiteral(held.Left) && !IsNoneLiteral(held.Right):
                    return HeldEquality(held);

                case BoundBinaryOperator binary when binary.Method is null:
                    return Binary(binary);

                // 'await x': x. Where it waits, the runtime suspends the task.
                case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Await, Operand: var awaited }:
                    return Expression(awaited);

                // An operator of the program's own: a call to it.
                case BoundBinaryOperator { Method: { } binaryMethod } userBinary:
                    return UserBinary(userBinary, binaryMethod);

                case BoundUnaryOperator { Method: { } unaryMethod } userUnary:
                    return UserUnary(userUnary, unaryMethod);

                case BoundUnaryOperator unary:
                    return Unary(unary);

                case BoundConditional conditional: {
                    if (LowerType(conditional.Type) is not { } type) {
                        return Missing(conditional, $"a '?:' of type '{conditional.Type.ToDisplayString()}'");
                    }

                    var whenTrue = function.AddBlock();
                    var whenFalse = function.AddBlock();
                    var after = function.AddBlock();
                    var result = after.AddArgument(type);
                    var decision = conditional.Syntax is ConditionalExpressionSyntax written ? Decide(written.QuestionToken.SpanStart, "?:", "true", "false") : null;
                    Condition(conditional.Condition, Mark(whenTrue, decision, 0), Mark(whenFalse, decision, 1));

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

                case BoundIfExpression ifExpression:
                    return IfValue(ifExpression);

                // 'throw e' where a value is wanted leaves; nothing after it runs.
                case BoundThrowExpression throwExpression:
                    Statement(throwExpression.Body);
                    if (_current is not null) {
                        Jump(new UnreachableInst());
                    }

                    return UnitValue.Instance;

                case BoundTry tryExpression:
                    return Try(tryExpression);

                case BoundCall call: {
                    var result = Call(call);

                    // A call to a function that gives Never does not come back:
                    // nothing after it runs.
                    if (result is not null && _current is not null && Sub(call.Type).Equals(Core.Never)) {
                        Jump(new UnreachableInst());
                        return UnitValue.Instance;
                    }

                    return result;
                }

                default:
                    return Missing(expression, $"the expression '{Describe(expression.Syntax)}' ({expression.GetType().Name}{(expression is BoundConversion c ? " " + c.ConversionKind : "")})");
            }
        }

        bool IsTrivialType(TypeSymbol type) => LowerType(type)?.IsTrivial ?? false;

        /// <summary>
        ///     'fn helper(…) { … }' in a body: a function of its own, named
        ///     for this one, which captures nothing (spec/SYNTAX.md §5) — so it
        ///     is lowered as any function is, with this instance's type
        ///     arguments for the ones around it.
        /// </summary>
        void LocalFunction(BoundLocalFunction local) {
            var callee = local.Symbol;
            if (local.Body is null) {
                return;
            }

            if (!callee.TypeParameters.IsEmpty) {
                // It is instantiated for each call's type arguments, as a
                // generic function of the module is — with this instance's
                // for the type parameters around it.
                var genericName = generator.LocalFunctionName(function.Name, callee.Name);
                if (!generator.Captures(callee)) {
                    generator.DeclareLocalGeneric(callee, local.Body, genericName, source);
                    return;
                }

                // A closure is one value, and a generic function is code for
                // each set of type arguments; so one that captures is not a
                // closure but a context alone, built here, once, and kept for
                // the rest of the scope. Each instance's code takes it last,
                // as a closure's does, and each call passes it.
                Locate(local.Syntax);
                var captures = generator.CapturesOf(callee, local.Body, []);
                if (ContextType(local, captures, genericName) is not { } contextType || Context(local, contextType, captures) is not { } context) {
                    return;
                }

                var contextHolder = new LocalSymbol("$" + callee.Name, Core.Module, null, false);
                _slots[contextHolder] = NewSlot(contextType, callee.Name);
                BindLocal(contextHolder, Unhold(context), local.Syntax, declaration: true);
                _localFunctions[callee] = contextHolder;
                generator.DeclareLocalGeneric(callee, local.Body, genericName, source, new ClosureInfo(contextType, captures.ToDictionary(static c => c.Key, static c => c)));
                return;
            }

            // One that captures is a closure, kept in a slot of its own for
            // the rest of the scope; a call to it is a call through that.
            if (generator.Captures(callee)) {
                if (callee.Parameters.Any(static p => p.Modifier is not (ParameterModifier.None or ParameterModifier.In or ParameterModifier.InOut))) {
                    Fail(local.Syntax, $"the local function '{callee.Name}', which captures, with an '{callee.Parameters.First(static p => p.Modifier is not (ParameterModifier.None or ParameterModifier.In or ParameterModifier.InOut)).Modifier}' parameter");
                    return;
                }

                if (LocalFunctionType(callee) is not { } thick) {
                    Fail(local.Syntax, $"the local function '{callee.Name}'");
                    return;
                }

                Locate(local.Syntax);
                var name = generator.LocalFunctionName(function.Name, callee.Name);
                if (ClosureOf(local, callee, local.Body, thick, generator.CapturesOf(callee, local.Body, []), name) is not { } value) {
                    return;
                }

                var holder = new LocalSymbol("$" + callee.Name, Core.Module, null, false);
                _slots[holder] = NewSlot(thick, callee.Name);
                BindLocal(holder, value, local.Syntax, declaration: true);
                _localFunctions[callee] = holder;
                return;
            }

            if (generator.SignatureOf(callee, local.Syntax, substitution) is not { } type) {
                _failed = true;
                return;
            }

            generator.LowerLocalFunction(callee, local.Body, generator.LocalFunctionName(function.Name, callee.Name), type, source, substitution);
        }

        /// <summary>
        ///     The closure a local function that captures is. A function type
        ///     has no inout parameters, so one with them is typed from its own
        ///     signature, where such a parameter is an address.
        /// </summary>
        ThickFunctionType? LocalFunctionType(FunctionSymbol local) =>
            local.Parameters.Any(static p => p.Modifier == ParameterModifier.InOut)
                ? local.DeclaringSyntax.FirstOrDefault() is { } declaration && generator.SignatureOf(local, declaration, substitution) is { } signature
                    ? new ThickFunctionType(signature)
                    : null
                : LowerType(TypeOf(local)) as ThickFunctionType;

        /// <summary>The slots holding the closures of this function's local functions that capture.</summary>
        readonly Dictionary<FunctionSymbol, LocalSymbol> _localFunctions = [];

        /// <summary>
        ///     A local function that captures, as the closure it is: from its
        ///     slot here, from the context of a closure that captured it, or —
        ///     in its own code, calling itself — remade from its own context.
        /// </summary>
        SilValue? LocalFunctionValue(FunctionSymbol local, BoundNode at) {
            if (closure is not null && closure.Captures.TryGetValue(local, out var captured)) {
                return Emit(new LoadInst(Emit(new RefElementAddrInst(_context!, captured.Field))));
            }

            // A generic one calling itself: its own context.
            if (ReferenceEquals(local, symbol) && _context is { } own && !local.TypeParameters.IsEmpty) {
                return Emit(new CopyValueInst(own));
            }

            if (ReferenceEquals(local, symbol) && _context is { } context && LocalFunctionType(local) is { } thick) {
                var reference = Emit(new FunctionRefInst(function.Name, thick.Implementation));
                return Emit(new PartialApplyInst(reference, Emit(new CopyValueInst(context)), thick));
            }

            if (_localFunctions.TryGetValue(local, out var holder)) {
                return Expression(new BoundLocalAccess(at.Syntax, holder, TypeOf(local)));
            }

            return Missing(at, $"the local function '{local.Name}' here");
        }

        /// <summary>A call to a local function that captures: through its closure.</summary>
        SilValue? LocalFunctionCall(BoundCall call) {
            Locate(call.Syntax);
            if (LocalFunctionValue(call.Function, call) is not { Type: ThickFunctionType thick } callee) {
                return null;
            }

            Hold(callee);
            var arguments = new List<SilValue>();
            var borrows = new List<SilValue> { callee };
            for (var i = 0; i < call.Arguments.Length; i++) {
                // An inout argument passes where it is, as to any function.
                if (i < call.Function.Parameters.Length && call.Function.Parameters[i].Modifier == ParameterModifier.InOut) {
                    if (InoutArgument(call.Arguments[i], borrows) is not { } address) {
                        return null;
                    }

                    arguments.Add(address);
                    continue;
                }

                if (Borrow(call.Arguments[i]) is not { } value) {
                    return null;
                }

                arguments.Add(value);
                borrows.Add(value);
            }

            return Invoke(callee, thick.Signature, arguments, borrows, call.Syntax);
        }

        SilValue? Missing(BoundNode node, string what) {
            Fail(node.Syntax, what);
            return null;
        }

        /// <summary>
        ///     An assignment used as a value — 'a = b = s', 'let c = a[0] = s'
        ///     — is the value it stored. The value is made first and kept in a
        ///     slot of its own; the store takes a copy of it, read through the
        ///     slot as a local is, and the original is then taken out as the
        ///     result. Most stores — a setter's, a subscript's, 'array_set', a
        ///     non-trivial value's — give nothing back of their own.
        /// </summary>
        SilValue? AssignmentValue(BoundAssignment assignment) => OnActorFor(assignment, () => AssignmentValueHere(assignment));

        SilValue? AssignmentValueHere(BoundAssignment assignment) {
            var evaluated = new List<BoundExpression>();
            return EndEvaluated(evaluated, EvaluateTargetOnce(assignment, evaluated) ? AssignmentValueOf(assignment) : null);
        }

        SilValue? AssignmentValueOf(BoundAssignment assignment) {
            // 'a.next?.v = 9' stores only when there is a next, evaluating the
            // value only then: its value is optional.
            if (assignment.Target is BoundFieldAccess { Receiver: { } chainedFrom } chainedTarget
                && IsChainedThrough(chainedFrom, chainedTarget.Field)) {
                return ChainedAssignment(assignment, chainedTarget, chainedFrom, asValue: true);
            }

            if (LowerType(assignment.Value.Type) is not { } type) {
                return Missing(assignment, $"an assignment of '{assignment.Value.Type.ToDisplayString()}' used as a value");
            }

            if (Expression(assignment.Value) is not { } value) {
                return null;
            }

            var slot = NewSlot(type, "$assigned");
            Emit(new StoreInst(Owned(value), slot, StoreKind.Init));
            var temporary = new LocalSymbol("$assigned", Core.Module, null, false);
            _slots[temporary] = slot;

            var stored = new BoundAssignment(assignment.Syntax, assignment.Target, new BoundLocalAccess(assignment.Value.Syntax, temporary, assignment.Value.Type));
            if (Assignment(stored) is not { } done) {
                return null;
            }

            Destroy(done);
            return Emit(new LoadInst(slot, type.IsTrivial ? LoadKind.Trivial : LoadKind.Take));
        }

        /// <summary>
        ///     What a compound assignment's target is made of, evaluated once
        ///     for the read and the store: by the node, which the two share, its
        ///     value, held.
        /// </summary>
        readonly Dictionary<BoundExpression, SilValue> _evaluated = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        ///     <c>xs[next()] += 1</c> reads the target, then stores to it, and
        ///     evaluates it once: an index that does more than read a variable,
        ///     or whose variable the right side may change, and an object a
        ///     call gives (<c>make().count += 1</c>), are evaluated first, and
        ///     held, and both the read and the store take them from there.
        ///     False when one could not be.
        /// </summary>
        bool EvaluateTargetOnce(BoundAssignment assignment, List<BoundExpression> evaluated) {
            if (assignment.Syntax is not AssignmentExpressionSyntax { OperatorToken.Kind: not SyntaxKind.EqualsToken }) {
                return true;
            }

            bool? changes = null;
            return Visit(assignment.Target);

            bool Visit(BoundExpression? place) {
                switch (place) {
                    case BoundIndexAccess index:
                        return Visit(index.Receiver) && index.Indices.All(i => IsRead(i) && !(i is not BoundLiteral && RightSideChanges()) || Once(i));

                    case BoundFieldAccess field:
                        return Visit(field.Receiver);

                    case BoundConversion { ConversionKind: ConversionKind.Identity } conversion:
                        return Visit(conversion.Operand);

                    case BoundCall or BoundIndirectCall or BoundUnaryOperator { Operand: BoundCall or BoundIndirectCall } when Sub(place.Type).TypeKind is TypeKind.Class or TypeKind.Actor:
                        return Once(place);

                    default:
                        return true;
                }
            }

            bool Once(BoundExpression node) {
                if (_evaluated.ContainsKey(node)) {
                    return true;
                }

                if (LowerType(Sub(node.Type)) is null || Expression(node) is not { } value) {
                    return false;
                }

                _evaluated[node] = Hold(Owned(value));
                evaluated.Add(node);
                return true;
            }

            bool RightSideChanges() => changes ??= !IsRead(assignment.Value);

            // Reading it again gives the same and does nothing.
            bool IsRead(BoundNode node) =>
                ReferenceEquals(node, assignment.Target)
                || node is BoundLiteral or BoundLocalAccess or BoundParameterAccess or BoundSelfAccess or BoundValueParameter or BoundTypeExpression or BoundMetatype
                    or BoundFieldAccess { Field.IsComputed: false } or BoundUnaryOperator or BoundBinaryOperator or BoundConversion or BoundRange or BoundTuple
                    or BoundTupleElementAccess or BoundIndexAccess { Subscript: null }
                && node.Children.All(IsRead);
        }

        SilValue? EndEvaluated(List<BoundExpression> evaluated, SilValue? result) {
            foreach (var node in Enumerable.Reverse(evaluated)) {
                _evaluated.Remove(node, out var value);
                if (result is null) {
                    Unhold(value!);
                } else {
                    End(value!);
                }
            }

            return result;
        }

        SilValue? Assignment(BoundAssignment assignment) {
            var evaluated = new List<BoundExpression>();
            return EndEvaluated(evaluated, EvaluateTargetOnce(assignment, evaluated) ? AssignmentHere(assignment) : null);
        }

        SilValue? AssignmentHere(BoundAssignment assignment) {
            if (assignment.Target is BoundFieldAccess { Receiver: { } chainedFrom } chainedTarget
                && IsChainedThrough(chainedFrom, chainedTarget.Field)) {
                return ChainedAssignment(assignment, chainedTarget, chainedFrom);
            }

            if (IsInlineMember(assignment.Target)
                || assignment.Target is BoundIndexAccess { Receiver: var indexed } && IsInlineArray(indexed)) {
                return InlineAssign(assignment.Target, assignment.Value);
            }

            // 'x.name = v' for x: T, T: Named: the property of the type T
            // stands for, now that it is known — stored or with a setter.
            if (assignment.Target is BoundFieldAccess { Receiver: { } holder } requirement
                && PropertyWitness(requirement.Field, holder) is { } own) {
                return Assignment(new BoundAssignment(
                    assignment.Syntax,
                    new BoundFieldAccess(requirement.Syntax, holder, own, requirement.Type),
                    assignment.Value
                ));
            }

            // 'c[i] = v' for C: MutableCollection, C an array: the array's own element.
            if (assignment.Target is BoundIndexAccess indexTarget && AsArrayIndexing(indexTarget) is { } arrayTarget) {
                return Assignment(new BoundAssignment(assignment.Syntax, arrayTarget, assignment.Value));
            }

            // 'grid[x, y] = v': a call to the subscript's setter, which takes
            // the receiver where it is, as any mutating method does.
            if (assignment.Target is BoundIndexAccess { Subscript: { } subscript } subscripted) {
                subscript = SubscriptWitness(subscript, subscripted.Receiver);
                if (generator.SubscriptSetter(subscript) is not { } setter) {
                    return Missing(assignment, $"storing through the subscript of '{subscripted.Receiver.Type.ToDisplayString()}'");
                }

                var stored = Expression(new BoundCall(assignment.Syntax, subscripted.Receiver, setter, [..subscripted.Indices, assignment.Value]) {
                    Substitution = subscripted.Substitution
                });
                return stored is null ? null : UnitValue.Instance;
            }

            // 'shape.size = v' through 'any Sized': the setter's slot, given
            // the box where the existential keeps it.
            if (assignment.Target is BoundFieldAccess { Receiver: { } existentialHolder } throughExistential
                && Sub(existentialHolder.Type) is ExistentialTypeSymbol) {
                return ExistentialCall(existentialHolder, SetterSlot(throughExistential.Field), [assignment.Value], assignment) is null
                    ? null
                    : UnitValue.Instance;
            }

            // 't.fahrenheit = v': a call to the property's setter, which takes
            // the receiver where it is, as a subscript's does.
            if (assignment.Target is BoundFieldAccess { Field.IsComputed: true } property
                && (property.Receiver is null || Sub(property.Receiver.Type) is not ExistentialTypeSymbol)) {
                if (generator.SetterOf(property.Field) is not { } setter) {
                    return Missing(assignment, $"storing to the property '{property.Field.Name}'");
                }

                var stored = Expression(SetterCall(property, setter, assignment.Value, assignment.Syntax));
                return stored is null ? null : UnitValue.Instance;
            }

            if (Expression(assignment.Value) is not { } value) {
                return null;
            }

            Hold(value);

            // 'a[1..<3] = v': the elements stored over the range's.
            if (assignment.Target is BoundIndexAccess rangeTarget && SlicingRange(rangeTarget) is { } rangeIndex) {
                return RangeStore(rangeTarget, rangeIndex, value);
            }

            // 'a[i] = v': the array, made unique, takes the value.
            if (assignment.Target is BoundIndexAccess { Indices: [var indexExpression] } indexAccess
                && Sub(indexAccess.Receiver.Type) is ArrayTypeSymbol) {
                var ends = new List<SilValue>();
                if (Address(indexAccess.Receiver, ends) is not { } arrayAddress || Expression(indexExpression) is not { } index) {
                    return null;
                }

                Unhold(value);
                Emit(new BuiltinInst("array_set", SilType.Unit, arrayAddress, index, value));
                EndAll(ends);
                return UnitValue.Instance;
            }

            var borrows = new List<SilValue>();
            if (Address(assignment.Target, borrows) is not { } address) {
                return null;
            }

            Unhold(value);
            switch (((AddressType)address.Type).Pointee) {
                // A weak or unowned field is given the object without taking the value.
                case WeakStorageType:
                    Emit(new StoreWeakInst(value, address));
                    Destroy(value);
                    break;

                case UnownedStorageType:
                    Emit(new StoreUnownedInst(value, address));
                    Destroy(value);
                    break;

                case var pointee:
                    Emit(new StoreInst(AsFunction(value, pointee), address, StoreKind.Assign));
                    break;
            }

            EndAll(borrows);
            return value.Type.IsTrivial ? value : UnitValue.Instance;
        }

        /// <summary>
        ///     <c>["a": 1]</c>: <c>Dictionary(dictionaryLiteral: [("a", 1)])</c>,
        ///     the entries in an array that the initialiser adds in order, a
        ///     later one with a key already there replacing it; <c>[:]</c>
        ///     the same with none.
        /// </summary>
        SilValue? DictionaryLiteral(BoundDictionary dictionary) {
            if (Sub(dictionary.Type) is not NamedTypeSymbol { TypeArguments: [var key, var value] } type
                || type.OriginalDefinition.GetMembers("init").OfType<FunctionSymbol>()
                    .FirstOrDefault(static f => f.Parameters is [{ Label: "dictionaryLiteral" }]) is not { } initializer) {
                return Missing(dictionary, $"a dictionary of type '{Sub(dictionary.Type).ToDisplayString()}'");
            }

            var pair = new TupleTypeSymbol([key, value], [null, null]);
            var entries = new BoundArray(
                dictionary.Syntax,
                new ArrayTypeSymbol(pair, ArrayKind.Grow),
                [..dictionary.Entries.Select(e => (BoundExpression)new BoundTuple(e.Key.Syntax, pair, [e.Key, e.Value]))]
            );
            return Expression(new BoundCall(dictionary.Syntax, null, initializer, [entries], type) {
                Substitution = TypeSubstitution.For(type)
            });
        }

        /// <summary>
        ///     The address a reference to an expression holds: a place's own; a
        ///     parameter passed by value, or a value with no place, in a slot of
        ///     its own — which root inference then knows dies with the function.
        /// </summary>
        SilValue? ReferenceTo(BoundExpression expression) {
            // A reference to a part of inline bytes: a view of it. A whole
            // block is referred to where it is held, as any value is.
            if (IsInlineType(expression.Type) && LowerType(expression.Type) is StructType { IsInline: true }
                && (IsInlineMember(expression)
                    || Unwrapped(expression) is BoundIndexAccess { Receiver: var indexed } && IsInlineArray(indexed)
                    || Unwrapped(expression) is BoundLocalAccess viewed && _views.Contains(viewed.Local))) {
                var ends = new List<SilValue>();
                var view = View(expression, ends);
                EndAll(ends);
                return view;
            }

            if (LowerType(expression.Type) is AddressType) {
                return Expression(expression); // already a reference
            }

            if (Place(expression) is { } place) {
                if (SlotUnder(place) is { ElementType.IsTrivial: true } referred) {
                    _referred.Add(referred);
                }

                return place;
            }

            if (Expression(expression) is not { } value) {
                return null;
            }

            var name = expression switch {
                BoundParameterAccess parameter => parameter.Parameter.Name,
                BoundSelfAccess => "self",
                _ => null,
            };
            var slot = NewSlot(value.Type, name);
            Store(value, slot);
            return slot;
        }

        /// <summary>'move x': a local's value taken out of its slot; anything else is already a value of its own.</summary>
        SilValue? Move(BoundExpression operand) {
            // One closures share, in a box: an array's buffer is taken where no
            // closure holds the box any more — 'assert(xs.isEmpty)' has come
            // and gone — so that 'move' gives the only reference to it, and
            // copied where one does, which may read it yet.
            if (operand is BoundLocalAccess { Local: var shared } && _boxed.Contains(shared)
                && closure?.Captures.ContainsKey(shared) != true
                && _slots.TryGetValue(shared, out var boxSlot) && LowerType(shared.Type) is ArrayType array) {
                var taken = function.AddBlock();
                var copied = function.AddBlock();
                var moved = function.AddBlock();
                Jump(new CondBranchInst(Emit(new BuiltinInst("ref_is_unique", SilType.Bool, boxSlot)), taken, [], copied, []));

                _current = taken;
                var borrows = new List<SilValue>();
                var address = LocalAddress(shared, operand.Syntax, borrows)!;
                var value = Emit(new LoadInst(address, LoadKind.Take));
                Emit(new StoreInst(Emit(new BuiltinInst("array_empty", array)), address, StoreKind.Init));
                EndAll(borrows);
                Branch(moved, value);

                _current = copied;
                Branch(moved, Owned(Expression(operand)!));

                _current = moved;
                return moved.AddArgument(array);
            }

            var slot = operand switch {
                BoundLocalAccess local when _slots.TryGetValue(local.Local, out var found) && !_boxed.Contains(local.Local)
                    && closure?.Captures.ContainsKey(local.Local) != true => found,
                BoundParameterAccess parameter when _spilled.TryGetValue(parameter.Parameter, out var spilled) => spilled,
                _ => null,
            };

            if (slot is null || slot.ElementType.IsTrivial) {
                return Expression(operand);
            }

            return Emit(new LoadInst(slot, LoadKind.Take));
        }

        /// <summary>A field's value, read as its storage says: a copy, or the object a weak or unowned reference holds.</summary>
        SilValue ReadField(SilValue address) =>
            ((AddressType)address.Type).Pointee switch {
                WeakStorageType => Emit(new LoadWeakInst(address)),
                UnownedStorageType => Emit(new LoadUnownedInst(address)),
                _ => Emit(new LoadInst(address)),
            };

        void EndAll(List<SilValue> borrows) {
            for (var i = borrows.Count - 1; i >= 0; i--) {
                End(borrows[i]);
            }
        }

        SilValue? FieldAccess(BoundFieldAccess access, BoundExpression receiver, FieldSymbol field) {
            if (generator.ConstantRead(field) is { } constant && LowerType(field.Type) is { } constantType) {
                return Constant(constant, constantType, access.Syntax);
            }

            if (SilGen.IsGlobal(field) && generator.GlobalOf(field) is var (globalName, globalType)) {
                return Emit(new LoadInst(Emit(new GlobalAddrInst(globalName, globalType))));
            }

            if (IsInlineType(receiver.Type)) {
                return InlineRead(access);
            }

            // A field of an object: through its address, while the object is borrowed.
            if (Sub(receiver.Type).TypeKind is TypeKind.Class or TypeKind.Actor) {
                if (Borrow(receiver) is not { Type: ClassType classType } reference) {
                    return null;
                }

                var index = classType.FieldIndex(field.Name);
                if (index < 0) {
                    return Missing(access, $"the member '{field.Name}'");
                }

                // An actor's state read from outside it: on the actor (SilGen.Actors.cs).
                var back = IsolatedRead(field) ? HopInto(reference, []) : null;
                var loaded = ReadField(Emit(new RefElementAddrInst(reference, index)));
                if (back is not null) {
                    HopBack(back, loaded);
                }

                End(reference);
                return loaded;
            }

            // A field of a self shared in a box: that field alone, as a call
            // with another field of it inout may be in progress.
            if (_selfBox is not null && SelfPart(access) && Place(access) is { } shared) {
                return ReadField(shared);
            }

            // A field of something in memory, read without copying the whole of it.
            if (!IsTrivialType(receiver.Type) && !IsBorrowable(access) && Place(access) is { } place) {
                return ReadField(place);
            }

            if (!IsTrivialType(receiver.Type)) {
                if (Borrow(receiver) is not { Type: StructType borrowedType } borrowed) {
                    return null;
                }

                var borrowedIndex = borrowedType.FieldIndex(field.Name);
                if (borrowedIndex < 0) {
                    return Missing(access, $"the member '{field.Name}'");
                }

                var extracted = Emit(new StructExtractInst(borrowed, borrowedIndex));
                var read = OutOfField(extracted, Emit);
                var copy = ReferenceEquals(read, extracted) ? Owned(extracted) : read;
                End(borrowed);
                return copy;
            }

            if (Expression(receiver) is not { } value) {
                return null;
            }

            var fieldIndex = (value.Type as StructType)?.FieldIndex(field.Name) ?? -1;
            return fieldIndex < 0
                ? Missing(access, $"the member '{field.Name}'")
                : Emit(new StructExtractInst(value, fieldIndex));
        }

        /// <summary>A stored field of self's value, or of one inside it.</summary>
        static bool SelfPart(BoundFieldAccess access) =>
            access.Receiver is BoundSelfAccess { Type.IsReferenceType: false }
            || access.Receiver is BoundFieldAccess { Type.IsReferenceType: false, Field: { IsComputed: false, IsStatic: false } } outer && SelfPart(outer);

        /// <summary>
        ///     The address of a local, of <c>self</c> in memory, or of a field or
        ///     element inside one — reached without evaluating anything — or null.
        /// </summary>
        SilValue? Place(BoundExpression expression) {
            switch (expression) {
                case BoundLocalAccess local when _slots.TryGetValue(local.Local, out var slot) && !_boxed.Contains(local.Local):
                    return slot;

                case BoundParameterAccess access when _parameters.TryGetValue(access.Parameter, out var inout) && IsByAddress(access.Parameter):
                    return inout;

                case BoundParameterAccess access when _spilled.TryGetValue(access.Parameter, out var spilled):
                    return spilled;

                case BoundSelfAccess when _selfAddress is { } self:
                    return self;

                case BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } field }
                    when Sub(receiver.Type).TypeKind == TypeKind.Struct && !IsInlineType(receiver.Type): {
                    if (Place(receiver) is not { Type: AddressType { Pointee: StructType structType } } address) {
                        return null;
                    }

                    var index = structType.FieldIndex(field.Name);
                    return index < 0 ? null : Emit(new StructElementAddrInst(address, index));
                }

                case BoundTupleElementAccess element when Place(element.Receiver) is { Type: AddressType { Pointee: TupleType } } tuple:
                    return Emit(new TupleElementAddrInst(tuple, element.Index));

                // What a reference refers to is where it points.
                case BoundConversion { ConversionKind: ConversionKind.Dereference } dereference:
                    return Expression(dereference.Operand) is { Type: AddressType } referred ? referred : null;

                default:
                    return null;
            }
        }

        /// <summary>
        ///     A property with accessors has no address to change part of it
        ///     at. Its value is read, by the getter, into a slot of its own,
        ///     which is changed instead; once the change is done — when the
        ///     caller ends <paramref name="borrows" /> — the slot's value is
        ///     handed to the setter, so 'willSet' and 'didSet' run once, for
        ///     the whole change.
        /// </summary>
        SilValue? ReadForWriteBack(BoundFieldAccess property, FunctionSymbol setter, List<SilValue> borrows) {
            if (Expression(property) is not { } current) {
                return null;
            }

            var slot = NewSlot(current.Type, $"${property.Field.Name}");
            Emit(new StoreInst(Owned(current), slot, StoreKind.Init));
            var temporary = new LocalSymbol($"${property.Field.Name}", Core.Module, null, true);
            _slots[temporary] = slot;

            var taken = new BoundUnaryOperator(property.Syntax, property.Type, UnaryOperatorKind.Move, new BoundLocalAccess(property.Syntax, temporary, property.Type));
            var setterCall = SetterCall(property, setter, taken, property.Syntax);
            borrows.Add(new WriteBack(() => Expression(setterCall)));
            return slot;
        }

        /// <summary>A call to a property's setter, on its receiver — <c>self</c> when it is written bare.</summary>
        BoundCall SetterCall(BoundFieldAccess property, FunctionSymbol setter, BoundExpression value, SyntaxNode syntax) {
            var receiver = property.Receiver
                ?? (setter.IsStatic ? null : new BoundSelfAccess(syntax, Owner(setter) is { } owner ? SelfOf(owner) : property.Field.Type));
            return new BoundCall(syntax, receiver, setter, [value]);
        }

        /// <summary>
        ///     Among an address's borrows: the setter call that writes a
        ///     property's changed value back, made when the borrows end.
        /// </summary>
        sealed class WriteBack(Action run) : SilValue {
            public void Run() => run();
            public override SilType Type => SilType.Unit;
        }

        /// <summary>
        ///     Where an assignment stores: a local, <c>self</c>, or a field or
        ///     element inside one, or a field of an object. An object reached
        ///     on the way is borrowed until the caller ends <paramref name="borrows" />.
        /// </summary>
        SilValue? Address(BoundExpression target, List<SilValue> borrows) {
            if (target is BoundFieldAccess { Field: { } globalField } && SilGen.IsGlobal(globalField) && generator.GlobalOf(globalField) is var (globalName, globalType)) {
                return Emit(new GlobalAddrInst(globalName, globalType));
            }

            switch (target) {
                case BoundLocalAccess local:
                    return LocalAddress(local.Local, local.Syntax, borrows);

                case BoundParameterAccess access when _parameters.TryGetValue(access.Parameter, out var inout) && IsByAddress(access.Parameter):
                    return inout;

                case BoundParameterAccess access when closure?.Captures.TryGetValue(access.Parameter, out var captured) == true && captured.Boxed:
                    return CapturedParameterAddress(captured, borrows);

                case BoundParameterAccess access when _spilled.TryGetValue(access.Parameter, out var spilled):
                    return spilled;

                case BoundSelfAccess when _selfAddress is { } self:
                    return self;

                case BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } field }
                    when Sub(receiver.Type).TypeKind is TypeKind.Class or TypeKind.Actor: {
                    if (Borrow(receiver) is not { Type: ClassType classType } reference) {
                        return null;
                    }

                    borrows.Add(reference);
                    var index = classType.FieldIndex(field.Name);
                    return index < 0
                        ? Missing(target, $"storing to '{field.Name}'")
                        : Emit(new RefElementAddrInst(reference, index));
                }

                case BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } field } when !IsInlineType(receiver.Type): {
                    if (Address(receiver, borrows) is not { } address) {
                        return null;
                    }

                    var structType = (address.Type as AddressType)?.Pointee as StructType;
                    var index = structType?.FieldIndex(field.Name) ?? -1;
                    return index < 0
                        ? Missing(target, $"storing to '{field.Name}'")
                        : Emit(new StructElementAddrInst(address, index));
                }

                // Part of a computed or observed property: 'h.c.n = 7',
                // 'h.c.bump()', '&h.c'.
                case BoundFieldAccess { Field: { IsComputed: true, IsStatic: false } property } computed
                    when (computed.Receiver is null || Sub(computed.Receiver.Type) is not ExistentialTypeSymbol)
                         && generator.SetterOf(property) is { } setter:
                    return ReadForWriteBack(computed, setter, borrows);

                case BoundTupleElementAccess element:
                    return Address(element.Receiver, borrows) is { } tuple
                        ? Emit(new TupleElementAddrInst(tuple, element.Index))
                        : null;

                // A range of an array's elements, read out and written back:
                // 'sortAll(&values[1..<^1])', 'values[2...].sort()'.
                case BoundIndexAccess sliced when SlicingRange(sliced) is { } rangeIndex && !IsInlineArray(sliced.Receiver):
                    return SliceForWriteBack(sliced, rangeIndex, borrows);

                // The whole of an array, where an inout slice is wanted: 'fill(&values)'.
                case BoundConversion { ConversionKind: ConversionKind.Identity } view when IsArrayView(view) && !IsInlineArray(view.Operand):
                    return ArrayForWriteBack(view, borrows);

                // An element of an array, where it is: 'bump(&values[0])'.
                case BoundIndexAccess { Subscript: null, Indices: [var indexExpression] } element
                    when Sub(element.Receiver.Type) is ArrayTypeSymbol && !IsInlineArray(element.Receiver): {
                    if (Address(element.Receiver, borrows) is not { Type: AddressType { Pointee: var arrayType } } array
                        || ArrayType.ElementOf(arrayType) is not { } elementType
                        || Expression(indexExpression) is not { } index) {
                        return null;
                    }

                    return Emit(new BuiltinInst("array_element_addr", SilType.Address(elementType), array, index));
                }

                // Part of what a subscript gives: 'groups[key, default: []].append(v)'.
                case BoundIndexAccess { Subscript: { } subscript } subscripted when AsArrayIndexing(subscripted) is null
                    && generator.SubscriptSetter(SubscriptWitness(subscript, subscripted.Receiver)) is { } subscriptSetter:
                    return SubscriptForWriteBack(subscripted, subscriptSetter, borrows);

                case BoundIndexAccess subscripted when AsArrayIndexing(subscripted) is { } arrayIndexing:
                    return Address(arrayIndexing, borrows);

                default:
                    return Missing(target, $"storing to '{Describe(target.Syntax)}'");
            }
        }

        /// <summary>
        ///     What a subscript gives has no address to change part of it at,
        ///     as a computed property's has not: it is read, by the getter,
        ///     into a slot of its own, which is changed instead, and handed to
        ///     the setter once the caller ends <paramref name="borrows" />. The
        ///     indices are worked out once, for both.
        /// </summary>
        SilValue? SubscriptForWriteBack(BoundIndexAccess subscripted, FunctionSymbol setter, List<SilValue> borrows) {
            var indices = ImmutableArray.CreateBuilder<BoundExpression>(subscripted.Indices.Length);
            foreach (var index in subscripted.Indices) {
                if (Expression(index) is not { } value) {
                    return null;
                }

                var indexSlot = NewSlot(value.Type, "$index");
                Emit(new StoreInst(Owned(value), indexSlot, StoreKind.Init));
                var indexLocal = new LocalSymbol("$index", Core.Module, null, true);
                _slots[indexLocal] = indexSlot;
                indices.Add(new BoundLocalAccess(index.Syntax, indexLocal, index.Type));
            }

            var read = new BoundIndexAccess(subscripted.Syntax, subscripted.Type, subscripted.Receiver, indices.ToImmutable()) {
                Subscript = subscripted.Subscript,
                Substitution = subscripted.Substitution,
            };
            if (Expression(read) is not { } current) {
                return null;
            }

            var slot = NewSlot(current.Type, "$subscript");
            Emit(new StoreInst(Owned(current), slot, StoreKind.Init));
            var temporary = new LocalSymbol("$subscript", Core.Module, null, true);
            _slots[temporary] = slot;

            var taken = new BoundUnaryOperator(subscripted.Syntax, subscripted.Type, UnaryOperatorKind.Move, new BoundLocalAccess(subscripted.Syntax, temporary, subscripted.Type));
            var setterCall = new BoundCall(subscripted.Syntax, subscripted.Receiver, setter, [..indices, taken]) {
                Substitution = subscripted.Substitution
            };
            borrows.Add(new WriteBack(() => Expression(setterCall)));
            return slot;
        }

        SilValue? Literal(BoundLiteral literal, TypeSymbol type) {
            // 'none': the optional with nothing in it.
            if (literal.Value is null && LowerType(type) is EnumType optional && optional.CaseIndex("None") is var none and >= 0) {
                return Emit(new EnumInst(optional, none, null));
            }

            // A plain enum's case is bound as a literal naming it; a
            // '@flags' enum's is the integer it is.
            if (literal.Value is string flagName && Sub(type) is NamedTypeSymbol { TypeKind: TypeKind.Enum, IsFlags: true } flags) {
                return LowerType(flags) is { } flagsType && flags.GetMembers(flagName).OfType<EnumCaseSymbol>().FirstOrDefault() is { } flag
                    ? Emit(new IntegerLiteralInst(flagsType, flag.RawValue))
                    : Missing(literal, $"the flag '{flagName}'");
            }

            if (literal.Value is string caseName && type is NamedTypeSymbol { TypeKind: TypeKind.Enum }) {
                return LowerType(type) is EnumType enumType && enumType.CaseIndex(caseName) is var index and >= 0
                    ? Emit(new EnumInst(enumType, index, null))
                    : Missing(literal, $"the enum case '{caseName}'");
            }

            if (LowerType(type) is not { } silType) {
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

            if (type == SilType.Char && value is Rune scalar) {
                return Emit(new IntegerLiteralInst(SilType.Char, scalar.Value));
            }

            if (type == SilType.Int && IntegerOf(value) is { } whole) {
                var number = negative ? -whole : whole;
                if (number > long.MaxValue || number < long.MinValue) {
                    Fail(at, $"the integer '{value}', which does not fit in Int");
                    return null;
                }

                return Emit(new IntegerLiteralInst(SilType.Int, (long)number));
            }

            if (SilType.IntegerInfo(type) is var (bits, signed) && IntegerOf(value) is { } integer) {
                var number = negative ? -integer : integer;
                var low = signed ? -(System.Numerics.BigInteger.One << (bits - 1)) : 0;
                var high = signed ? (System.Numerics.BigInteger.One << (bits - 1)) - 1 : (System.Numerics.BigInteger.One << bits) - 1;
                if (number < low || number > high) {
                    Fail(at, $"the integer '{(negative ? "-" : "")}{value}', which does not fit in {type}");
                    return null;
                }

                // The bits, read as signed: an unsigned value past the signed
                // range is kept as the negative number with the same bits.
                var bitsValue = number > (System.Numerics.BigInteger)Int128.MaxValue ? number - (System.Numerics.BigInteger.One << 128) : number;
                return Emit(new IntegerLiteralInst(type, (Int128)bitsValue));
            }

            if (SilType.IsFloat(type) && (value is double ? System.Convert.ToDouble(value) : IntegerOf(value) is { } exact ? (double)exact : (double?)null) is { } real) {
                return Emit(new FloatLiteralInst(negative ? -real : real, type));
            }

            Fail(at, $"the literal '{Describe(at)}' of type {type}");
            return null;
        }

        /// <summary>An integer literal's value, of whichever width the lexer and binder gave it; null for anything else.</summary>
        static System.Numerics.BigInteger? IntegerOf(object? value) =>
            value switch {
                int i => i,
                long l => l,
                ulong u => u,
                Int128 i => (System.Numerics.BigInteger)i,
                UInt128 u => (System.Numerics.BigInteger)u,
                _ => null,
            };

        /// <summary>
        ///     <c>"x = \(x)"</c>: the text and the holes, each as a string,
        ///     joined left to right.
        /// </summary>
        /// <summary>
        ///     True for a TextWriter's <c>write(_:)</c> given an interpolation,
        ///     on a writer named by a variable, a parameter or self — read
        ///     again for each piece with nothing else done on the way. A
        ///     TextWriter's contract is that writing text in pieces writes it
        ///     as writing it whole would.
        /// </summary>
        bool WritesInPieces(BoundCall call, BoundInterpolatedString interpolated) =>
            interpolated.Syntax is InterpolatedStringExpressionSyntax
            && call is { Function: { Name: "write", Parameters: [{ Label: null or "_", Type: var parameterType }] }, Receiver: BoundLocalAccess or BoundParameterAccess or BoundSelfAccess }
            && parameterType.Equals(Core.String)
            && Core.Module.GetType("TextWriter") is { TypeKind: TypeKind.Protocol } writer
            && Sub(call.Receiver!.Type).Satisfies(writer);

        void WriteInPieces(BoundCall call, BoundInterpolatedString interpolated) {
            var syntax = (InterpolatedStringExpressionSyntax)interpolated.Syntax;
            var hole = 0;
            foreach (var content in syntax.Contents) {
                BoundExpression piece;
                if (content is InterpolatedTextSyntax text) {
                    var value = text.TextToken.Value as string ?? "";
                    if (value.Length == 0) {
                        continue;
                    }

                    piece = new BoundLiteral(content, Core.String, value);
                } else {
                    if (hole >= interpolated.Parts.Length) {
                        return;
                    }

                    piece = new BoundInterpolationHole(content, Core.String, interpolated.Parts[hole], interpolated.Formats[hole]);
                    hole++;
                }

                Statement(new BoundExpressionStatement(call.Syntax, new BoundCall(call.Syntax, call.Receiver, call.Function, [piece], call.Type) {
                    Substitution = call.Substitution,
                }));
                if (_current is null) {
                    return;
                }
            }
        }

        SilValue? Interpolation(BoundInterpolatedString interpolated) {
            if (interpolated.Syntax is not InterpolatedStringExpressionSyntax syntax) {
                return Missing(interpolated, "this interpolated string");
            }

            // Every piece first, then one string of them all: an
            // interpolation of five pieces is one allocation, not four.
            var pieces = new List<SilValue>();
            var hole = 0;
            foreach (var content in syntax.Contents) {
                SilValue? piece;
                if (content is InterpolatedTextSyntax text) {
                    var value = text.TextToken.Value as string ?? "";
                    if (value.Length == 0) {
                        continue;
                    }

                    piece = Emit(new StringLiteralInst(value));
                } else {
                    if (hole >= interpolated.Parts.Length || Hole(interpolated.Parts[hole], interpolated.Formats[hole]) is not { } written) {
                        return null;
                    }

                    piece = written;
                    hole++;
                }

                pieces.Add(Hold(piece));
            }

            return Joined(pieces);
        }

        /// <summary>
        ///     Held strings, joined: the one itself, or one string of them
        ///     all, each piece ended after — in the order they were held.
        /// </summary>
        SilValue Joined(List<SilValue> pieces) {
            switch (pieces.Count) {
                case 0:
                    return Emit(new StringLiteralInst(""));
                case 1:
                    return Unhold(pieces[0]);
            }

            var joined = Emit(new BuiltinInst("string_concat_many", SilType.String, [..pieces]));
            for (var i = pieces.Count - 1; i >= 0; i--) {
                End(pieces[i]);
            }

            return joined;
        }

        /// <summary>
        ///     What a hole writes: a string itself; a number, a Bool or a Char
        ///     as text; a <c>Formattable</c> value as its <c>format</c> writes
        ///     it; a <c>Debug</c> value as its <c>debugDescription()</c> says,
        ///     so a type that describes itself is written that way; and
        ///     anything else as <c>Reflect.describe</c> writes it. Decided for
        ///     the type a generic instance has, so <c>"\(t)"</c> writes a
        ///     <c>T</c> that is a String as a string. A hole with a format spec
        ///     is <see cref="FormattedHole" />'s.
        /// </summary>
        SilValue? Hole(BoundExpression part, BoundFormatSpec? format) {
            part = AsTypeValue(part);
            // '"\(x:?)"' of one too, by its debug describer (#297).
            if ((format is null || format.Specifier == DebugFormat) && IsOpaqueHere(Sub(part.Type)) && MetadataOf(Sub(part.Type)) is { } described) {
                if (Borrow(part) is not { } value) {
                    return null;
                }

                var text = Emit(new BuiltinInst(format is null ? "generic_describe" : "generic_debug_describe", SilType.String, value, described));
                End(value);
                return text;
            }

            if (format is not null) {
                return FormattedHole(part, format);
            }

            if (LowerType(part.Type) is not { } lowered) {
                return Missing(part, $"interpolating a value of type '{part.Type.ToDisplayString()}'");
            }

            // A string and a scalar: the runtime writes them, as their own
            // 'format' would with a spec that says nothing, without the call.
            if (lowered == SilType.String || (lowered is ScalarType && SilBuiltins.Find($"string_from_{lowered}") is not null)) {
                return Expression(part) is { } value ? Stringify(value, part) : null;
            }

            // A type that writes itself, with the spec that says nothing.
            if (FormatRequirement() is var (formattable, write) && Sub(part.Type).Satisfies(formattable)
                && FormatSpecifier.Default.Bind(Core, part.Syntax) is { } plain) {
                return Expression(new BoundCall(part.Syntax, part, write, [plain], Core.String));
            }

            // An enum, which writes its case's name.
            if (Sub(part.Type) is NamedTypeSymbol { TypeKind: TypeKind.Enum }) {
                return Expression(part) is { } value ? Stringify(value, part) : null;
            }

            return DebugText(part);
        }

        /// <summary>
        ///     A metatype written out — <c>"\(T.self)"</c>, <c>print(Int.self)</c>
        ///     — is the <c>Type</c> it converts to, which writes its name.
        /// </summary>
        BoundExpression AsTypeValue(BoundExpression part) =>
            Sub(part.Type) is MetatypeTypeSymbol or ExistentialTypeSymbol { MetatypeOf: not null } ? new BoundConversion(part.Syntax, Core.Type, part, ConversionKind.Identity) : part;

        /// <summary><c>Formattable</c> and its <c>format</c>, when Sable.Core has them.</summary>
        (NamedTypeSymbol, FunctionSymbol)? FormatRequirement() =>
            Core.Formattable is { } formattable && formattable.GetMembers("format").OfType<FunctionSymbol>().FirstOrDefault() is { } format
                ? (formattable, format)
                : null;

        /// <summary>
        ///     A hole with a format spec, <c>\(x:&gt;8)</c>: a <c>Formattable</c>
        ///     value's <c>format</c> given the spec; any other's text as a
        ///     hole without one writes it (or as <c>Debug</c> describes it, for
        ///     <c>?</c>) padded to the spec's width, which is all the binder
        ///     lets such a spec say.
        /// </summary>
        /// <summary>The bound value of <c>?</c>'s spec, for a debug describer to write a value with.</summary>
        BoundExpression? DebugSpec(SyntaxNode at) => DebugFormat.Bind(Core, at);

        SilValue? FormattedHole(BoundExpression part, BoundFormatSpec format) {
            if (FormatRequirement() is var (formattable, write) && Sub(part.Type).Satisfies(formattable)) {
                return Expression(new BoundCall(part.Syntax, part, write, [format.Value], Core.String));
            }

            if (Core.FormatSpec?.GetMembers("padded").OfType<FunctionSymbol>().FirstOrDefault() is not { } padded) {
                return Missing(part, "a format spec without 'FormatSpec.padded'");
            }

            BoundExpression text = new BoundHoleText(part.Syntax, Core.String, part, format.Specifier.Kind == "Debug");

            // '#?': the description laid out over lines, as FormatSpec.prettyDebug lays it.
            if (format.Specifier.IsPrettyDebug && Core.FormatSpec?.GetMembers("prettyDebug").OfType<FunctionSymbol>().FirstOrDefault() is { } pretty) {
                text = new BoundCall(part.Syntax, null, pretty, [text], Core.String);
            }

            return Expression(new BoundCall(part.Syntax, format.Value, padded, [text], Core.String));
        }

        /// <summary>
        ///     A value as <c>Debug</c> describes it, or, for a type that says
        ///     nothing of itself, as <c>Reflect.describe</c> does: what a hole
        ///     writes for a type that does not write itself, and what <c>?</c>
        ///     asks for.
        /// </summary>
        SilValue? DebugText(BoundExpression part) {
            part = AsTypeValue(part);
            if (LowerType(part.Type) is not { } lowered) {
                return Missing(part, $"interpolating a value of type '{part.Type.ToDisplayString()}'");
            }

            if (Core.Module.GetType("Debug") is { TypeKind: TypeKind.Protocol } debug
                && debug.GetMembers("debugDescription").OfType<FunctionSymbol>().FirstOrDefault() is { } describe
                && Sub(part.Type).Satisfies(debug)
                // Not from inside the very 'debugDescription' it would call:
                // Error's default writes '"\(self)"', the value written out.
                && !(Nominal(Sub(part.Type)) is { } describedType && ReferenceEquals(Implementation(describedType, describe), symbol))) {
                return Expression(new BoundCall(part.Syntax, part, describe, [], Core.String));
            }

            // '()' is no value to pass, and is always written the same.
            if (lowered == SilType.Unit) {
                Discard(part);
                return Emit(new StringLiteralInst("()"));
            }

            if (Borrow(part) is not { } borrowed) {
                return null;
            }

            if (generator.Reflector("describe", Sub(part.Type)) is not { } describer) {
                End(borrowed);
                return Missing(part, $"interpolating a value of type '{part.Type.ToDisplayString()}'");
            }

            var signature = ReflectorType("describe", borrowed.Type);
            var described = Emit(new ApplyInst(Emit(new FunctionRefInst(describer, signature)), SilType.String, borrowed, Emit(new IntegerLiteralInst(SilType.Int, 0))));
            End(borrowed);
            return described;
        }

        /// <summary>A value as a string, for interpolation: a string itself, or the scalars written out.</summary>
        SilValue? Stringify(SilValue value, BoundNode at) {
            if (value.Type == SilType.String) {
                return value;
            }

            // An enum's value is its case's name; a '@flags' enum's, the
            // name of the case it is exactly, or else its integer.
            if (at is BoundExpression { Type: var enumSymbol } && Sub(enumSymbol) is NamedTypeSymbol { TypeKind: TypeKind.Enum } named) {
                return value.Type is EnumType enumType ? CaseName(value, enumType) : FlagsName(value, named, at);
            }

            var name = $"string_from_{value.Type}";
            return SilBuiltins.Find(name) is not null
                ? Emit(new BuiltinInst(name, SilType.String, value))
                : Missing(at, $"interpolating a value of type {value.Type}");
        }

        SilValue? Wrap(BoundConversion conversion) {
            if (LowerType(conversion.Type) is not EnumType optional) {
                return Missing(conversion, $"an optional of '{conversion.Operand.Type.ToDisplayString()}'");
            }

            // A literal wrapped straight into an optional takes the wrapped type.
            var inner = conversion.Operand is BoundLiteral literal
                ? Literal(literal, ((OptionalTypeSymbol)conversion.Type).UnderlyingType)
                : Expression(conversion.Operand);

            return inner is null ? null : Emit(new EnumInst(optional, optional.CaseIndex("Some"), Carried(inner)));
        }

        SilValue? Unwrap(BoundConversion conversion) {
            if (Expression(conversion.Operand) is not { Type: EnumType optional } value) {
                return null;
            }

            var some = optional.CaseIndex("Some");
            var present = function.AddBlock();
            var payload = Payload(present, optional.Cases[some].Payload);
            var absent = function.AddBlock();
            Jump(new SwitchEnumInst(value, [(some, present)], absent));

            _current = absent;
            Emit(new CondFailInst(Emit(new IntegerLiteralInst(SilType.Bool, 1)), "unwrapped a none"));
            Jump(new UnreachableInst());

            _current = present;
            var result = Owned(payload);
            Destroy(value);
            return result;
        }

        /// <summary>
        ///     The operator that meets a protocol's requirement for the type
        ///     the operand turned out to be; the operator itself otherwise.
        /// </summary>
        FunctionSymbol? OperatorWitness(FunctionSymbol method, TypeSymbol operand) =>
            OwnerOf(method) is not { TypeKind: TypeKind.Protocol } ? method
            : Sub(operand) is ArrayTypeSymbol array ? generator.ArrayImplementation(array, method)
            : Nominal(Sub(operand)) is { } concrete ? Implementation(concrete, method)
            : method;

        /// <summary>
        ///     The named type whose members a type has: itself, or for
        ///     <c>T?</c> the Optional of Sable.Core's extensions, which give an
        ///     optional its conformances.
        /// </summary>
        NamedTypeSymbol? Nominal(TypeSymbol type) =>
            type switch {
                NamedTypeSymbol named => named,
                OptionalTypeSymbol optional => Core.Optional.Construct([optional.UnderlyingType]),
                _ => null,
            };

        /// <summary>
        ///     '-z' through an operator the program declares: a call to it,
        ///     which, on a generic type, takes its type arguments from the
        ///     operand's — from what the operand refers to, when it is passed
        ///     by reference.
        /// </summary>
        SilValue? UserUnary(BoundUnaryOperator unary, FunctionSymbol method) {
            var callee = OperatorWitness(method, unary.Operand.Type) ?? method;
            var owner = Sub(unary.Operand.Type) is ReferenceTypeSymbol reference ? reference.ReferencedType : Sub(unary.Operand.Type);
            return Expression(new BoundCall(unary.Syntax, null, callee, [unary.Operand], unary.Type) {
                Substitution = OwnerOf(callee) is { IsGenericDefinition: true } ? TypeSubstitution.For(owner) : TypeSubstitution.Empty,
            });
        }

        /// <summary>
        ///     'a == b' through an operator the program declares: a call to it.
        ///     '!=' is the negation of '=='; '>' is '<' with the operands
        ///     swapped, '<=' its negation, '>=' the negation of '<'.
        /// </summary>
        SilValue? UserBinary(BoundBinaryOperator binary, FunctionSymbol method) {
            if (OperatorWitness(method, binary.Left.Type) is not { } callee) {
                // An enum that says it is Equatable and writes no '==' of
                // its own is compared by case, as one that says nothing is.
                if (method.Name == "==" && binary.OperatorKind is BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual or BinaryOperatorKind.UserDefined
                    && Sub(binary.Left.Type) is NamedTypeSymbol { TypeKind: TypeKind.Enum } && LowerType(binary.Left.Type) is { } enumType && IsPayloadFree(enumType)) {
                    if (Borrow(binary.Left) is not { } left) {
                        return null;
                    }

                    if (Borrow(binary.Right) is not { } right) {
                        End(left);
                        return null;
                    }

                    var compared = SameCase(left, right, binary.OperatorKind != BinaryOperatorKind.NotEqual);
                    End(right);
                    End(left);
                    return compared;
                }

                return Missing(binary, $"'{method.Name}' for '{Sub(binary.Left.Type).ToDisplayString()}'");
            }

            var (first, second, negate) = binary.OperatorKind switch {
                BinaryOperatorKind.NotEqual => (binary.Left, binary.Right, true),
                BinaryOperatorKind.GreaterThan => (binary.Right, binary.Left, false),
                BinaryOperatorKind.LessThanOrEqual => (binary.Right, binary.Left, true),
                BinaryOperatorKind.GreaterThanOrEqual => (binary.Left, binary.Right, true),
                _ => (binary.Left, binary.Right, false),
            };

            var resultType = binary.OperatorKind == BinaryOperatorKind.UserDefined ? binary.Type : Core.Bool;
            // An operator of a generic type takes its type arguments from the
            // operands' — from what an operand refers to, when it is passed
            // by reference.
            var owner = Sub(first.Type) is ReferenceTypeSymbol reference ? reference.ReferencedType : Sub(first.Type);
            owner = owner is OptionalTypeSymbol && OwnerOf(callee) is { ExtendedOptional: not null } ? Nominal(owner)! : owner;

            // '3 * v': the type is the second operand's.
            if (OwnerOf(callee) is { IsGenericDefinition: true } definition && !(owner is NamedTypeSymbol mine && mine.OriginalDefinition.Equals(definition))
                && Sub(second.Type) is NamedTypeSymbol theirs && theirs.OriginalDefinition.Equals(definition)) {
                owner = theirs;
            }

            // An array extension's operator: its element is the operands',
            // which it takes as slices of them, whatever kind they are.
            if (OwnerOf(callee) is { ExtendedArray: not null } carrier) {
                var view = Compilation.MatchArray(carrier, owner) ?? TypeSubstitution.Empty;
                BoundExpression Viewed(BoundExpression operand, int i) =>
                    view.Apply(callee.Parameters[i].Type) is ArrayTypeSymbol { ArrayKind: ArrayKind.Slice } slice && Sub(operand.Type) is ArrayTypeSymbol { ArrayKind: not ArrayKind.Slice }
                        ? new BoundConversion(operand.Syntax, slice, operand, ConversionKind.Identity)
                        : operand;
                var arrayCall = new BoundCall(binary.Syntax, null, callee, [Viewed(first, 0), Viewed(second, 1)], resultType) { Substitution = view };
                return Expression(arrayCall) is not { } compared ? null
                    : negate ? Emit(new BuiltinInst("not_Bool", SilType.Bool, compared)) : compared;
            }

            var call = new BoundCall(binary.Syntax, null, callee, [first, second], resultType) {
                Substitution = OwnerOf(callee) is { IsGenericDefinition: true } ? TypeSubstitution.For(owner) : TypeSubstitution.Empty,
            };
            if (Expression(call) is not { } result) {
                return null;
            }

            return negate ? Emit(new BuiltinInst("not_Bool", SilType.Bool, result)) : result;
        }

        /// <summary>An existential of protocols, which keeps its value in a box: not <c>any Error</c>, which is an object of its own.</summary>
        bool IsExistential(TypeSymbol type) =>
            Sub(type) is ExistentialTypeSymbol { Constraint: var constraint } && !constraint.Equals(Core.Error) && ExistentialProtocols(constraint) is not null;

        /// <summary>What a cast or a test asks for: a protocol, named alone in 'is Named', as its existential.</summary>
        TypeSymbol CastTarget(TypeSymbol tested) =>
            Sub(tested) switch {
                NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol => new ExistentialTypeSymbol(protocol),
                CompositionTypeSymbol composition => new ExistentialTypeSymbol(composition),
                var other => other,
            };

        /// <summary>
        ///     The cast of an existential's box to a type, applied: the value
        ///     as that type, or none (see <see cref="SilGen.ExistentialCast" />).
        /// </summary>
        SilValue? CastBox(BoundExpression operand, TypeSymbol target, BoundNode at) {
            if (generator.ExistentialCast(target, at.Syntax) is not var (name, type)) {
                return Missing(at, $"the cast from '{operand.Type.ToDisplayString()}' to '{target.ToDisplayString()}'");
            }

            // An object, not yet an existential: put in the box of its
            // static class, which the cast asks as it asks any box holding
            // an object — whether what it holds is of a class that conforms.
            if (Sub(operand.Type).TypeKind is TypeKind.Class or TypeKind.Actor) {
                if (Expression(operand) is not { Type: ClassType classType } value) {
                    return null;
                }

                var box = Emit(new AllocRefInst(generator.ExistentialBox(classType)));
                Emit(new StoreInst(Owned(value), Emit(new RefElementAddrInst(box, 0)), StoreKind.Init));
                var asked = Emit(new ApplyInst(Emit(new FunctionRefInst(name, type)), type.Result, [Emit(new UpcastInst(box, generator.ExistentialBoxRoot()))]));
                Emit(new DestroyValueInst(box));
                return asked;
            }

            if (Borrow(operand) is not { } existential) {
                return null;
            }

            var result = Emit(new ApplyInst(Emit(new FunctionRefInst(name, type)), type.Result, [Emit(new StructExtractInst(existential, 0))]));
            End(existential);
            return result;
        }

        /// <summary>'shape as? Square', and 'as!', which traps where 'as?' gives none.</summary>
        SilValue? ExistentialDowncast(BoundConversion conversion) {
            var optional = Sub(conversion.Type) is OptionalTypeSymbol;
            var target = CastTarget(optional ? ((OptionalTypeSymbol)Sub(conversion.Type)).UnderlyingType : Sub(conversion.Type));
            if (CastBox(conversion.Operand, target, conversion) is not { Type: EnumType result } cast) {
                return null;
            }

            if (optional) {
                return cast;
            }

            var some = result.CaseIndex("Some");
            var present = function.AddBlock();
            var payload = Payload(present, result.Cases[some].Payload);
            var absent = function.AddBlock();
            Jump(new SwitchEnumInst(cast, [(some, present)], absent));

            _current = absent;
            Emit(new CondFailInst(Emit(new IntegerLiteralInst(SilType.Bool, 1)), $"cast to '{target.ToDisplayString()}' failed"));
            Jump(new UnreachableInst());

            _current = present;
            var value = Owned(payload);
            Destroy(cast);
            return value;
        }

        /// <summary>
        ///     A call of the instance of a generic function that serves every
        ///     type (<see cref="SilGen.PolymorphicInstance" />): each argument
        ///     for a type parameter boxed as the existential it stands for
        ///     there, unless it is one already, and a result of a type
        ///     parameter's taken back out of its box as the type this call
        ///     gives it. Not handled when nothing needs either.
        /// </summary>
        (bool Handled, SilValue? Result) PolymorphicCall(BoundCall call, FunctionSymbol callee, TypeSubstitution polymorphic) {
            var arguments = call.Arguments.ToArray();
            var boxed = false;
            for (var i = 0; i < arguments.Length && i < callee.Parameters.Length; i++) {
                if (callee.Parameters[i].Type is not TypeParameterSymbol parameter || polymorphic.Lookup(parameter) is not { } wanted
                    || Sub(arguments[i].Type).Equals(wanted)) {
                    continue;
                }

                if (callee.Parameters[i].Modifier is not (ParameterModifier.None or ParameterModifier.In)) {
                    return (true, Missing(arguments[i], $"passing '{arguments[i].Type.ToDisplayString()}' to '{callee.Name}', which serves every type, as '{callee.Parameters[i].Modifier}'"));
                }

                arguments[i] = new BoundConversion(arguments[i].Syntax, wanted, arguments[i], ConversionKind.Existential);
                boxed = true;
            }

            var resultType = callee.ReturnType is TypeParameterSymbol returned ? polymorphic.Lookup(returned) : null;
            var unbox = resultType is not null && !Sub(call.Type).Equals(resultType);
            if (!boxed && !unbox) {
                return (false, null);
            }

            var passed = new BoundCall(call.Syntax, call.Receiver, callee, [..arguments], unbox ? resultType : call.Type) { Substitution = call.Substitution };
            if (Call(passed) is not { } result) {
                return (true, null);
            }

            return (true, unbox ? Unbox(result, Sub(call.Type), call) : result);
        }

        /// <summary>The value an owned existential holds, as the type it must be; a trap should it be another.</summary>
        SilValue? Unbox(SilValue existential, TypeSymbol target, BoundNode at) {
            if (generator.ExistentialCast(target, at.Syntax) is not var (name, type) || type.Result is not EnumType result) {
                Destroy(existential);
                return Missing(at, $"the result of '{Describe(at.Syntax)}' as '{target.ToDisplayString()}'");
            }

            var cast = Emit(new ApplyInst(Emit(new FunctionRefInst(name, type)), result, [Emit(new StructExtractInst(existential, 0))]));
            Destroy(existential);
            var some = result.CaseIndex("Some");
            var present = function.AddBlock();
            var payload = Payload(present, result.Cases[some].Payload);
            var absent = function.AddBlock();
            Jump(new SwitchEnumInst(cast, [(some, present)], absent));

            _current = absent;
            Emit(new CondFailInst(Emit(new IntegerLiteralInst(SilType.Bool, 1)), $"a result that is not '{target.ToDisplayString()}'"));
            Jump(new UnreachableInst());

            _current = present;
            var value = Owned(payload);
            Destroy(cast);
            return value;
        }

        /// <summary>'shape is Square', 'shape is Named': whether the existential's box holds one.</summary>
        SilValue? ExistentialTest(BoundTypeTest test) {
            if (CastBox(test.Operand, CastTarget(test.TestedType), test) is not { Type: EnumType result } cast) {
                return null;
            }

            var some = result.CaseIndex("Some");
            var present = function.AddBlock();
            Payload(present, result.Cases[some].Payload);
            var absent = function.AddBlock();
            var after = function.AddBlock();
            var answer = after.AddArgument(SilType.Bool);
            Jump(new SwitchEnumInst(cast, [(some, present)], absent));

            _current = present;
            Destroy(cast);
            Branch(after, Emit(new IntegerLiteralInst(SilType.Bool, 1)));

            _current = absent;
            Destroy(cast);
            Branch(after, Emit(new IntegerLiteralInst(SilType.Bool, 0)));

            _current = after;
            return answer;
        }

        /// <summary>The type a cast asks for: the wrapped one, of 'as?'.</summary>
        TypeSymbol CastTo(BoundConversion conversion) => CastTarget(Sub(conversion.Type) is OptionalTypeSymbol optional ? optional.UnderlyingType : Sub(conversion.Type));

        /// <summary>'error as? IoError': a cast of an 'any Error' to a type of error.</summary>
        bool IsErrorCast(BoundConversion conversion) =>
            Sub(conversion.Operand.Type) is ExistentialTypeSymbol && LowerType(conversion.Operand.Type) == generator.AnyErrorType() && CastTo(conversion) is not ExistentialTypeSymbol
            && !(_genericMetadata.Count > 0 && Mentions(CastTo(conversion), [.._genericMetadata.Keys]));

        /// <summary>The error an 'any Error' boxes, if it is of the type: '.Some' of it for 'as?', a trap otherwise for 'as!'.</summary>
        SilValue? ErrorDowncast(BoundConversion conversion) {
            var target = CastTo(conversion);
            if (LowerType(target) is not { } wanted || LowerType(new OptionalTypeSymbol(target)) is not EnumType result || Borrow(conversion.Operand) is not { } error) {
                return Missing(conversion, $"the cast to '{target.ToDisplayString()}'");
            }

            var box = generator.ErrorBox(wanted);
            var isOne = Emit(new BuiltinInst($"class_is:{box.Name}", SilType.Bool, error));
            SilValue Boxed() => Emit(new LoadInst(Emit(new RefElementAddrInst(Emit(new UncheckedRefCastInst(error, box)), 0))));
            if (Sub(conversion.Type) is not OptionalTypeSymbol) {
                Emit(new CondFailInst(Emit(new BuiltinInst("not_Bool", SilType.Bool, isOne)), $"cast to '{target.ToDisplayString()}' failed"));
                var forced = Boxed();
                End(error);
                return forced;
            }

            var yes = function.AddBlock();
            var no = function.AddBlock();
            var after = function.AddBlock();
            var joined = after.AddArgument(result);
            Jump(new CondBranchInst(isOne, yes, [], no, []));

            _current = yes;
            Branch(after, Emit(new EnumInst(result, result.CaseIndex("Some"), Boxed())));

            _current = no;
            Branch(after, Emit(new EnumInst(result, result.CaseIndex("None"), null)));

            _current = after;
            End(error);
            return joined;
        }

        /// <summary>'t as? any P.Type': whether the type conforms, asked of the registry; the type is the value.</summary>
        SilValue? MetatypeDowncast(BoundConversion conversion, TypeSymbol protocol) {
            if (LowerType(new ExistentialTypeSymbol(protocol)) is not { } existential || LowerType(conversion.Type) is not { } result || Expression(conversion.Operand) is not { } type) {
                return Missing(conversion, $"the cast to '{conversion.Type.ToDisplayString()}'");
            }

            var conforms = Emit(new BuiltinInst("metadata_is_subtype", SilType.Bool, type, Emit(new MetadataInst(existential))));
            if (Sub(conversion.Type) is not OptionalTypeSymbol) {
                Emit(new CondFailInst(Emit(new BuiltinInst("not_Bool", SilType.Bool, conforms)), $"cast to 'any {protocol.ToDisplayString()}.Type' failed"));
                return type;
            }

            var optional = (EnumType)result;
            var yes = function.AddBlock();
            var no = function.AddBlock();
            var after = function.AddBlock();
            var joined = after.AddArgument(optional);
            Jump(new CondBranchInst(conforms, yes, [], no, []));
            _current = yes;
            Branch(after, Emit(new EnumInst(optional, optional.CaseIndex("Some"), type)));
            _current = no;
            Branch(after, Emit(new EnumInst(optional, optional.CaseIndex("None"), null)));
            _current = after;
            return joined;
        }

        /// <summary>'provider as? T' of an object, in an instance of generic code that makes T a type whose values are no objects.</summary>
        bool IsNeverCast(BoundConversion conversion) =>
            Sub(conversion.Operand.Type).TypeKind is TypeKind.Class or TypeKind.Actor
            && CastTo(conversion) is var target and not (ExistentialTypeSymbol or OptionalTypeSymbol or TypeParameterSymbol or DependentMemberTypeSymbol)
            && target.TypeKind is not (TypeKind.Class or TypeKind.Actor or TypeKind.Protocol) && StructuralUnionTypeSymbol.Of(target) is null
            && conversion.Type is OptionalTypeSymbol { UnderlyingType: TypeParameterSymbol } or TypeParameterSymbol;

        /// <summary>'animal as? any Named': an object cast to an existential of protocols.</summary>
        bool IsObjectToProtocol(BoundConversion conversion) {
            var target = Sub(conversion.Type) is OptionalTypeSymbol optional ? optional.UnderlyingType : Sub(conversion.Type);
            return Sub(conversion.Operand.Type).TypeKind is TypeKind.Class or TypeKind.Actor
                && CastTarget(target) is ExistentialTypeSymbol { Constraint: var constraint } && !constraint.Equals(Core.Error)
                && ExistentialProtocols(constraint) is not null;
        }

        bool IsDowncast(BoundConversion conversion) {
            var target = Sub(conversion.Type) is OptionalTypeSymbol optional ? optional.UnderlyingType : Sub(conversion.Type);
            return Sub(conversion.Operand.Type).TypeKind == TypeKind.Class && target.TypeKind == TypeKind.Class;
        }

        /// <summary>
        ///     A downcast: the object, if it is of the class or a subclass of it —
        ///     '.Some' of it for 'as?', a trap otherwise for 'as!'.
        /// </summary>
        SilValue? Downcast(BoundConversion conversion) {
            var optional = Sub(conversion.Type) is OptionalTypeSymbol;
            var targetSymbol = optional ? ((OptionalTypeSymbol)Sub(conversion.Type)).UnderlyingType : Sub(conversion.Type);
            if (LowerType(targetSymbol) is not ClassType target || Expression(conversion.Operand) is not { Type: ClassType } value) {
                return Missing(conversion, $"the cast to '{targetSymbol.ToDisplayString()}'");
            }

            // In an instance of a generic function, where what was a type
            // parameter's value is of a known class, the cast may go up, which
            // always succeeds, or to a class on another line, which never does.
            if (value.Type is ClassType known && !target.IsSubclassOf(known)) {
                return SettledCast(conversion, value, known.IsSubclassOf(target) ? target : null, targetSymbol);
            }

            var isA = Emit(new BuiltinInst($"class_is:{target.Name}", SilType.Bool, value));
            if (!optional) {
                Emit(new CondFailInst(Emit(new BuiltinInst("not_Bool", SilType.Bool, isA)), $"cast to '{targetSymbol.ToDisplayString()}' failed"));
                var forced = Emit(new CopyValueInst(Emit(new UncheckedRefCastInst(value, target))));
                Destroy(value);
                return forced;
            }

            if (LowerType(conversion.Type) is not EnumType result) {
                Destroy(value);
                return Missing(conversion, $"the cast to '{conversion.Type.ToDisplayString()}'");
            }

            var yes = function.AddBlock();
            var no = function.AddBlock();
            var after = function.AddBlock();
            var joined = after.AddArgument(result);
            Jump(new CondBranchInst(isA, yes, [], no, []));

            _current = yes;
            var cast = Emit(new CopyValueInst(Emit(new UncheckedRefCastInst(value, target))));
            Destroy(value);
            Branch(after, Emit(new EnumInst(result, result.CaseIndex("Some"), cast)));

            _current = no;
            Destroy(value);
            Branch(after, Emit(new EnumInst(result, result.CaseIndex("None"), null)));

            _current = after;
            return joined;
        }

        /// <summary>A downcast that always gives <paramref name="value" /> as <paramref name="up" />, a class it inherits from, or, with none, never succeeds.</summary>
        SilValue? SettledCast(BoundConversion conversion, SilValue value, ClassType? up, TypeSymbol targetSymbol) {
            if (LowerType(conversion.Type) is not { } result) {
                Destroy(value);
                return Missing(conversion, $"the cast to '{targetSymbol.ToDisplayString()}'");
            }

            var optional = Sub(conversion.Type) is OptionalTypeSymbol ? result as EnumType : null;
            if (up is not null) {
                var cast = Emit(new CopyValueInst(Emit(new UpcastInst(value, up))));
                Destroy(value);
                return optional is null ? cast : Emit(new EnumInst(optional, optional.CaseIndex("Some"), cast));
            }

            Destroy(value);
            if (optional is not null) {
                return Emit(new EnumInst(optional, optional.CaseIndex("None"), null));
            }

            Emit(new CondFailInst(Emit(new IntegerLiteralInst(SilType.Bool, 1)), $"cast to '{targetSymbol.ToDisplayString()}' failed"));
            Jump(new UnreachableInst());

            // Never reached: a block nothing branches to, dropped with what uses it.
            _current = function.AddBlock();
            return _current.AddArgument(result);
        }

        /// <summary>
        ///     True for a type parameter that this instance makes a type whose
        ///     values carry nothing of what they are — not an object, an
        ///     existential or a union — so a cast of one is decided here.
        /// </summary>
        bool IsKnownHere(TypeSymbol type) =>
            type is TypeParameterSymbol
            && Sub(type) is var known and not (TypeParameterSymbol or ExistentialTypeSymbol or OptionalTypeSymbol)
            && known.TypeKind is not (TypeKind.Class or TypeKind.Actor)
            && StructuralUnionTypeSymbol.Of(known) is null;

        /// <summary>
        ///     Whether two types are one in this instance. An instance is made
        ///     once for each lowered type, and every kind of array lowers to
        ///     one, so the answer is the same for all of them: one instance
        ///     serves an S that is 'Int[]' and one that is 'Int[>..]'.
        /// </summary>
        bool IsSameHere(TypeSymbol type, TypeSymbol other) =>
            Sub(type).Equals(Sub(other)) || LowerType(type) is { } lowered && lowered.Equals(LowerType(other));

        /// <summary>
        ///     'elements as? Element[>..]' for an S this instance makes a known
        ///     type: the value itself when S is that type, none — or for 'as!',
        ///     a trap — when it is not.
        /// </summary>
        SilValue? KnownCast(BoundConversion conversion) {
            var optional = Sub(conversion.Type) is OptionalTypeSymbol;
            var targetSymbol = optional ? ((OptionalTypeSymbol)Sub(conversion.Type)).UnderlyingType : Sub(conversion.Type);
            if (LowerType(conversion.Type) is not { } resultType) {
                return null;
            }

            // 'value as? any Named': whether the type conforms is known now too.
            if (CastTarget(targetSymbol) is ExistentialTypeSymbol wanted && Sub(conversion.Operand.Type).Satisfies(wanted.Constraint)) {
                return Expression(new BoundConversion(conversion.Syntax, wanted, conversion.Operand, ConversionKind.Existential)) is not { } held ? null
                    : optional ? Emit(new EnumInst((EnumType)resultType, ((EnumType)resultType).CaseIndex("Some"), Carried(held))) : held;
            }

            if (Expression(conversion.Operand) is not { } value) {
                return null;
            }

            var same = IsSameHere(conversion.Operand.Type, targetSymbol);
            if (optional) {
                var result = (EnumType)resultType;
                if (same) {
                    return Emit(new EnumInst(result, result.CaseIndex("Some"), Carried(Owned(value))));
                }

                Destroy(value);
                return Emit(new EnumInst(result, result.CaseIndex("None"), null));
            }

            if (same) {
                return value;
            }

            Destroy(value);
            Emit(new CondFailInst(Emit(new IntegerLiteralInst(SilType.Bool, 1)), $"cast to '{targetSymbol.ToDisplayString()}' failed"));
            Jump(new UnreachableInst());

            // Never reached: a block nothing branches to, dropped with what uses it.
            _current = function.AddBlock();
            return _current.AddArgument(resultType);
        }

        /// <summary>
        ///     'error as? IoError' and 'error as! IoError' of an 'any Error': what
        ///     'match error { case let e as IoError => e; default => none }' gives,
        ///     and, for 'as!', unwrapped.
        /// </summary>
        SilValue? ErrorCast(BoundConversion conversion) {
            var optional = Sub(conversion.Type) as OptionalTypeSymbol ?? new OptionalTypeSymbol(Sub(conversion.Type));
            var at = conversion.Syntax;
            var local = LocalSymbol.Synthesised("$error", Core.Module, optional.UnderlyingType);
            var matched = new BoundMatch(at, optional, conversion.Operand, [
                new BoundMatchArm(at, [new BoundTypeTestPattern(at, conversion.Operand.Type, optional.UnderlyingType, local)], null,
                    new BoundConversion(at, optional, new BoundLocalAccess(at, local), ConversionKind.OptionalWrap)),
                new BoundMatchArm(at, [], null, new BoundLiteral(at, optional, null)),
            ]);
            return conversion.Type is OptionalTypeSymbol
                ? Expression(matched)
                : Expression(new BoundConversion(at, optional.UnderlyingType, matched, ConversionKind.Explicit));
        }

        /// <summary>'x is Dog': whether the object is of the class, or a subclass of it.</summary>
        SilValue? TypeTest(BoundTypeTest test) {
            // 'x is Named' of a value only its metadata knows: its dynamic type asked.
            if (IsOpaqueHere(test.Operand.Type) && MetadataOf(Sub(test.Operand.Type)) is { } operandMetadata && LowerType(CastTarget(test.TestedType)) is { } wantedType) {
                if (Borrow(test.Operand) is not { } opaqueValue) {
                    return null;
                }

                var dynamicType = Emit(new BuiltinInst("opaque_type", SilType.Metadata, opaqueValue, operandMetadata));
                var isSubtype = Emit(new BuiltinInst("metadata_is_subtype", SilType.Bool, dynamicType, Emit(new MetadataInst(wantedType))));
                End(opaqueValue);
                return isSubtype;
            }

            // 't is any P.Type': whether the type conforms.
            if (Sub(test.TestedType) is ExistentialTypeSymbol { MetatypeOf: { } conformed } && LowerType(test.Operand.Type) == SilType.Metadata) {
                return LowerType(new ExistentialTypeSymbol(conformed)) is { } asked && Expression(test.Operand) is { } type
                    ? Emit(new BuiltinInst("metadata_is_subtype", SilType.Bool, type, Emit(new MetadataInst(asked))))
                    : null;
            }

            // A value already known to be of the type: a union's binding that
            // an earlier test, failing, narrowed down to this one case.
            if (Sub(test.Operand.Type).Equals(Sub(test.TestedType))) {
                return Emit(new IntegerLiteralInst(SilType.Bool, 1));
            }

            // 'elements is Int[>..]' for an S this instance makes a known
            // type: whether it is that one, known now. (The operand is still
            // evaluated, for what it does.)
            if (IsKnownHere(test.Operand.Type) && CastTarget(test.TestedType) is not ExistentialTypeSymbol) {
                if (Expression(test.Operand) is not { } evaluated) {
                    return null;
                }

                Destroy(evaluated);
                return Emit(new IntegerLiteralInst(SilType.Bool, IsSameHere(test.Operand.Type, test.TestedType) ? 1 : 0));
            }

            // 'shape is Square', 'shape is Named': whether the cast would succeed.
            if (IsExistential(test.Operand.Type)) {
                return ExistentialTest(test);
            }

            // 'circle is Named' of a value whose type is known: whether the
            // type conforms, which is known now. (The operand is still
            // evaluated, for what it does.)
            if (CastTarget(test.TestedType) is ExistentialTypeSymbol wanted && Sub(test.Operand.Type) is not ExistentialTypeSymbol and not TypeParameterSymbol
                && Sub(test.Operand.Type).TypeKind is not (TypeKind.Class or TypeKind.Actor)) {
                if (Expression(test.Operand) is not { } evaluated) {
                    return null;
                }

                Destroy(evaluated);
                return Emit(new IntegerLiteralInst(SilType.Bool, Sub(test.Operand.Type).Satisfies(wanted.Constraint) ? 1 : 0));
            }

            if (StructuralUnionTypeSymbol.Of(Sub(test.Operand.Type)) is { } union) {
                return UnionTypeTest(test, union);
            }

            // 'animal is Named': asked of the conformance registry, which the
            // object's class's metadata, and its superclasses', carry.
            if (CastTarget(test.TestedType) is ExistentialTypeSymbol { Constraint: var constraint }
                && ExistentialProtocols(constraint) is { Count: > 0 } protocols
                && Sub(test.Operand.Type).TypeKind is TypeKind.Class or TypeKind.Actor) {
                if (Borrow(test.Operand) is not { Type: ClassType } reference) {
                    return null;
                }

                var conforms = Emit(new BuiltinInst($"class_conforms:{string.Join("&", protocols.Select(QualifiedName))}", SilType.Bool, reference));
                End(reference);
                return conforms;
            }

            // 'error is CancellationError': whether an 'any Error' boxes one.
            if (LowerType(test.Operand.Type) == generator.AnyErrorType() && CastTarget(test.TestedType) is not ExistentialTypeSymbol
                && LowerType(test.TestedType) is { } boxed) {
                if (Borrow(test.Operand) is not { } error) {
                    return null;
                }

                var boxes = Emit(new BuiltinInst($"class_is:{generator.ErrorBox(boxed).Name}", SilType.Bool, error));
                End(error);
                return boxes;
            }

            if (LowerType(test.TestedType) is not ClassType target || Borrow(test.Operand) is not { Type: ClassType } value) {
                return Missing(test, $"'is {test.TestedType.ToDisplayString()}' on '{test.Operand.Type.ToDisplayString()}'");
            }

            var isA = Emit(new BuiltinInst($"class_is:{target.Name}", SilType.Bool, value));
            End(value);
            return isA;
        }

        SilValue? NumericConversion(BoundConversion conversion) {
            if (Expression(conversion.Operand) is not { } value || LowerType(conversion.Type) is not { } target) {
                return null;
            }

            // 'Status.Closed as U8': the integer the case is; '9 as Status':
            // the case that integer is, or a trap.
            if (value.Type is EnumType && Sub(conversion.Operand.Type) is NamedTypeSymbol { TypeKind: TypeKind.Enum } from) {
                if (LowerType(generator._compilation.EnumRawType(from)) is not { } fromRaw) {
                    return null;
                }

                value = RawValue(value, from, fromRaw);
                if (value.Type == target) {
                    return value;
                }
            } else if (target is EnumType && Sub(conversion.Type) is NamedTypeSymbol { TypeKind: TypeKind.Enum } to) {
                if (LowerType(generator._compilation.EnumRawType(to)) is not { } toRaw) {
                    return null;
                }

                if (value.Type != toRaw) {
                    var widen = $"convert_{value.Type}_{toRaw}";
                    if (SilBuiltins.Find(widen) is null) {
                        return Missing(conversion, $"the conversion from {value.Type} to {toRaw}");
                    }

                    value = Emit(new BuiltinInst(widen, toRaw, value));
                }

                return CaseOfValue(value, to, (EnumType)target);
            }

            if (value.Type == target) {
                return value;
            }

            // A character converts through its code point, an Int: to an
            // integer as an Int converts, and from one only when the number
            // is a Unicode scalar, which 'as' checks and traps on.
            if (value.Type == SilType.Char && SilType.IsInteger(target)) {
                var code = Emit(new BuiltinInst("char_to_Int", SilType.Int, value));
                return target == SilType.Int ? code : Emit(new BuiltinInst($"{(conversion.IsUnchecked ? "wrap" : "convert")}_Int_{target}", target, code));
            }

            if (target == SilType.Char && SilType.IsInteger(value.Type)) {
                var number = value.Type == SilType.Int ? value : Emit(new BuiltinInst($"convert_{value.Type}_Int", SilType.Int, value));
                var scalar = Emit(new BuiltinInst("char_from_Int", SilType.Tuple([SilType.Bool, SilType.Char]), number));
                var valid = Emit(new TupleExtractInst(scalar, 0));
                Emit(new CondFailInst(Emit(new BuiltinInst("not_Bool", SilType.Bool, valid)), $"{value.Type} is not a Unicode scalar, so it is no Char"));
                return Emit(new TupleExtractInst(scalar, 1));
            }

            // 'unchecked as' wraps where 'as' would trap.
            var name = $"{(conversion.IsUnchecked ? "wrap" : "convert")}_{value.Type}_{target}";
            return SilBuiltins.Find(name) is not null
                ? Emit(new BuiltinInst(name, target, value))
                : Missing(conversion, $"the conversion from {value.Type} to {target}");
        }

        /// <summary>
        ///     <c>a..&lt;b</c> or <c>a...b</c> as a value: the Sable.Core struct,
        ///     built from its two bounds.
        /// </summary>
        SilValue? RangeValue(BoundRange range) {
            if (LowerType(range.Type) is not StructType structType) {
                return Missing(range, $"a range of type '{Sub(range.Type).ToDisplayString()}'");
            }

            // A one-sided range has a field for the bound it has, and only that.
            var given = new List<(string Field, BoundExpression Value)>();
            if (range.Lower is { } lowerBound) {
                given.Add(("lowerBound", lowerBound));
            }

            if (range.Upper is { } upperBound) {
                given.Add(("upperBound", upperBound));
            }

            if (given.Count != structType.Fields.Count || given.Any(g => structType.FieldIndex(g.Field) < 0)) {
                return Missing(range, $"a range of type '{Sub(range.Type).ToDisplayString()}'");
            }

            if (Evaluate(given.Select(static g => g.Value)) is not { } bounds) {
                return null;
            }

            var fields = new SilValue[structType.Fields.Count];
            for (var i = 0; i < given.Count; i++) {
                fields[structType.FieldIndex(given[i].Field)] = bounds[i];
            }

            return Emit(new StructInst(structType, fields));
        }

        SilValue? Record(BoundRecordExpression record) {
            if (LowerType(record.Type) is StructType { IsInline: true } inline) {
                return InlineRecord(record, inline);
            }

            if (LowerType(record.Type) is not StructType structType) {
                return Missing(record, $"a record of type '{record.Type.ToDisplayString()}'");
            }

            if (Evaluate(record.Fields.Select(static f => f.Value)) is not { } values) {
                return null;
            }

            var fields = new SilValue?[structType.Fields.Count];
            for (var i = 0; i < values.Count; i++) {
                fields[structType.FieldIndex(record.Fields[i].Field.Name)] = values[i];
            }

            // A field the record leaves out takes its initial value, evaluated
            // after the ones written, as the type's initialisers store it.
            if (fields.Any(static f => f is null) && Sub(record.Type) is NamedTypeSymbol { OriginalDefinition: var definition }) {
                foreach (var field in definition.Members.OfType<FieldSymbol>()) {
                    if (field is not { IsStatic: false, IsComputed: false, HasInitializer: true }
                        || structType.FieldIndex(field.Name) is var index && (index < 0 || fields[index] is not null)) {
                        continue;
                    }

                    if (generator.FieldInitializer(field) is not { } initial || Expression(initial) is not { } value) {
                        foreach (var made in fields.OfType<SilValue>()) {
                            Destroy(made);
                        }

                        return null;
                    }

                    fields[index] = Owned(value);
                }
            }

            if (fields.Any(static f => f is null)) {
                values.ForEach(Destroy);
                return Missing(record, "a record that leaves a field to its default");
            }

            return Emit(new StructInst(structType, [..fields!.Select((f, i) => IntoField(structType.Fields[i].Type, AsFunction(f!, structType.Fields[i].Type), Emit))]));
        }

        SilValue? UnionCase(TypeSymbol unionType, string name, IReadOnlyList<BoundExpression> arguments, BoundNode at) {
            var enumType = LowerType(unionType) as EnumType;
            var index = enumType?.CaseIndex(name) ?? -1;
            if (enumType is null || index < 0) {
                return Missing(at, $"the case '{name}'");
            }

            if (Evaluate(arguments) is not { } values) {
                return null;
            }

            // An indirect case's payload is built as any other, then boxed.
            var indirect = generator.IsIndirect(enumType, index);
            var payloadType = indirect ? ((ClassType)enumType.Cases[index].Payload!).Fields[0].Type : enumType.Cases[index].Payload;
            // A function of the union's type parameters' values is kept in its own convention.
            for (var i = 0; i < values.Count; i++) {
                values[i] = AsFunction(values[i], values.Count == 1 ? payloadType! : ((TupleType)payloadType!).Elements[i]);
            }

            SilValue? payload = values.Count switch {
                0 => null,
                // One field is the payload itself, even when it is a tuple.
                1 => values[0],
                _ => Emit(new TupleInst((TupleType)payloadType!, [..values])),
            };

            if (indirect && payload is not null) {
                var box = Emit(new AllocRefInst((ClassType)enumType.Cases[index].Payload!));
                Emit(new StoreInst(Owned(payload), Emit(new RefElementAddrInst(box, 0)), StoreKind.Assign));
                payload = box;
            }

            return Emit(new EnumInst(enumType, index, payload));
        }

        /// <summary>Expressions gathered into a new array: what a variadic parameter receives.</summary>
        SilValue? Gather(SilType type, IEnumerable<BoundExpression> elements) {
            // A variadic parameter is a slice: the arguments, gathered into an
            // array, are the whole of it.
            if (type is SliceType slice) {
                return Gather(new ArrayType(slice.Element), elements) is { } gathered ? WholeSlice(gathered) : null;
            }

            if (type is not ArrayType arrayType) {
                return null;
            }

            var empty = Emit(new BuiltinInst("array_empty", arrayType));
            var list = elements.ToList();
            if (list.Count == 0) {
                return empty;
            }

            var slot = NewSlot(arrayType);
            Emit(new StoreInst(empty, slot, StoreKind.Assign));
            foreach (var element in list) {
                if (Expression(element) is not { } value) {
                    return null;
                }

                Emit(new BuiltinInst("array_append", SilType.Unit, slot, value));
            }

            return Emit(new LoadInst(slot, LoadKind.Take));
        }

        /// <summary><c>[a, b]</c>: an array made of its elements.</summary>
        SilValue? ArrayLiteral(BoundArray array, TypeSymbol type) {
            // '[1, 2, 3]' where a slice is wanted: the array, and the whole of it viewed.
            if (LowerType(type) is SliceType) {
                return ArrayLiteral(array, new ArrayTypeSymbol(((ArrayTypeSymbol)Sub(type)).ElementType, ArrayKind.Grow)) is { } literal
                    ? WholeSlice(literal)
                    : null;
            }

            if (LowerType(type) is not ArrayType arrayType) {
                return Missing(array, $"an array of type '{type.ToDisplayString()}'");
            }

            if (array.Elements.IsEmpty) {
                return Emit(new BuiltinInst("array_empty", arrayType));
            }

            // The elements, then the array of them: of numbers, Bools and
            // Chars every one a literal, a constant, which native code keeps
            // in read-only memory, made once and never counted (#265); of
            // anything else, made and filled in one go, in the frame when it
            // does not escape it (PromoteToStack).
            if (Evaluate(array.Elements) is not { } values) {
                return null;
            }

            return Emit(new BuiltinInst("array_literal", arrayType, [..values.Select(Owned)]));
        }

        /// <summary>
        ///     A protocol's subscript, used on a T that stands for a known type
        ///     now: that type's own. Any other subscript is itself.
        /// </summary>
        /// <remarks>
        ///     Only a requirement has a witness: a subscript a protocol's
        ///     extension gives, by a FromEnd or a range, is its own getter.
        /// </remarks>
        FunctionSymbol SubscriptWitness(FunctionSymbol subscript, BoundExpression receiver) =>
            OwnerOf(subscript) is { TypeKind: TypeKind.Protocol } && subscript.ContainingSymbol is not ExtensionSymbol
            && Sub(receiver.Type) is NamedTypeSymbol concrete
                && Implementation(concrete, subscript) is { FunctionKind: FunctionKind.Subscript } own
                ? own
                : subscript;

        /// <summary>
        ///     A protocol's property, used on a T that stands for a known type
        ///     now: that type's own field. Null for any other property.
        /// </summary>
        FieldSymbol? PropertyWitness(FieldSymbol property, BoundExpression receiver) =>
            property is { IsStatic: false, ContainingSymbol: NamedTypeSymbol { TypeKind: TypeKind.Protocol } }
            && Sub(receiver.Type) is NamedTypeSymbol { TypeKind: not TypeKind.Protocol } concrete
                ? FindField(concrete, property.Name)
                : null;

        /// <summary>
        ///     A protocol's subscript used where Self is now an array: the
        ///     array's own indexing, which needs no subscript.
        /// </summary>
        /// <remarks>
        ///     A subscript a protocol's extension gives, by a FromEnd or a
        ///     range, is a getter like any other, called with Self the array.
        /// </remarks>
        BoundIndexAccess? AsArrayIndexing(BoundIndexAccess index) =>
            index.Subscript is { } subscript && OwnerOf(subscript) is { TypeKind: TypeKind.Protocol }
            && subscript.ContainingSymbol is not ExtensionSymbol && Sub(index.Receiver.Type) is ArrayTypeSymbol
                ? new BoundIndexAccess(index.Syntax, index.Type, index.Receiver, index.Indices)
                : null;

        /// <summary>
        ///     A protocol's static property read through a type: the type's own
        ///     property of that name when it is a requirement the type meets;
        ///     otherwise the protocol extension's, its getter called with Self
        ///     standing for the type.
        /// </summary>
        SilValue? StaticPropertyWitness(BoundFieldAccess access, BoundTypeExpression on) {
            var property = access.Field;
            if (!IsExtensionOnly(property)
                && Sub(on.ReferencedType) is NamedTypeSymbol { TypeKind: not TypeKind.Protocol } concrete
                && StaticField(concrete, property.Name) is { } own) {
                // A generic type's, 'Complex<F64>.zero', is the instance for its arguments.
                return Expression(new BoundFieldAccess(access.Syntax, null, own, access.Type) {
                    Substitution = TypeSubstitution.For(concrete),
                });
            }

            var chosen = IsExtensionOnly(property) ? property : DefaultStaticField(property);
            return chosen is not null && generator.GetterOf(chosen) is { } getter
                ? Expression(new BoundCall(access.Syntax, on, getter, [], access.Type))
                : Missing(access, $"'{property.Name}' on '{Sub(on.ReferencedType).ToDisplayString()}'");
        }

        /// <summary>A type's own static property (not its protocols' defaults), by name.</summary>
        static FieldSymbol? StaticField(NamedTypeSymbol concrete, string name) {
            for (var type = (NamedTypeSymbol?)concrete.OriginalDefinition; type is not null; type = SuperclassOf(type)?.OriginalDefinition) {
                if (type.Members.OfType<FieldSymbol>().FirstOrDefault(f => f.Name == name && f.IsStatic) is { } found) {
                    return found;
                }
            }

            return null;
        }

        /// <summary>The default an extension of the requirement's protocol gives a static property.</summary>
        static FieldSymbol? DefaultStaticField(FieldSymbol requirement) {
            if (requirement.ContainingSymbol is not NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol) {
                return null;
            }

            return protocol.OriginalDefinition.Members.OfType<FieldSymbol>()
                .FirstOrDefault(f => f.ContainingSymbol is ExtensionSymbol && f.Name == requirement.Name && f.IsStatic);
        }

        SilValue? IndexRead(BoundIndexAccess index) {
            if (AsArrayIndexing(index) is { } arrayIndexing) {
                return IndexRead(arrayIndexing);
            }

            // A type's own subscript: a call to its getter.
            if (index.Subscript is { } subscript) {
                // A protocol's requirement on a type parameter of unspecialised code: through the table it is given.
                if (subscript.ContainingSymbol is NamedTypeSymbol { TypeKind: TypeKind.Protocol }
                    && new BoundCall(index.Syntax, index.Receiver, subscript, index.Indices, index.Type) { Substitution = index.Substitution } is var required
                    && OpaqueSelf(required) is { } opaqueSelf) {
                    return GenericRequirementCall(required, opaqueSelf);
                }

                subscript = SubscriptWitness(subscript, index.Receiver);
                return generator.SubscriptGetter(subscript) is { } getter
                    ? Expression(new BoundCall(index.Syntax, index.Receiver, getter, index.Indices, index.Type) { Substitution = index.Substitution })
                    : Missing(index, $"indexing '{index.Receiver.Type.ToDisplayString()}'");
            }

            if (SlicingRange(index) is { } rangeIndex) {
                return RangeRead(index, rangeIndex);
            }

            if (IsInlineArray(index.Receiver)) {
                return InlineRead(index);
            }

            if (Sub(index.Receiver.Type) is not ArrayTypeSymbol || index.Indices is not [var indexExpression]) {
                return Missing(index, $"indexing '{index.Receiver.Type.ToDisplayString()}'");
            }

            // An array's element, or a slice's: the index is checked against
            // the slice's own count, not its buffer's.
            if (Borrow(index.Receiver) is not { } array || ArrayType.ElementOf(array.Type) is not { } elementType) {
                return null;
            }

            if (Expression(indexExpression) is not { } position) {
                return null;
            }

            var element = Emit(new BuiltinInst("array_get", elementType, array, position));
            End(array);
            return element;
        }

        /// <summary>
        ///     'xs[1..<3]': the elements in the range, as a slice — a view that
        ///     shares the array's buffer, as 'slice(from:to:)' is, trapping
        ///     where the range is not within the array. A range written in place
        ///     may leave out a bound: the start, or the array's count. A slice
        ///     of a slice is a view of the same buffer, its range within the
        ///     slice's. An array field of an inline struct is viewed whole
        ///     first, its elements copied out (InlineArrayAsSlice).
        /// </summary>
        SilValue? RangeRead(BoundIndexAccess index, BoundExpression rangeIndex) {
            var inline = IsInlineArray(index.Receiver);
            if ((inline ? InlineArrayAsSlice(index.Receiver) : Borrow(index.Receiver)) is not { } array) {
                return null;
            }

            void Done() {
                if (inline) {
                    Destroy(array);
                } else {
                    End(array);
                }
            }

            if (ArrayType.ElementOf(array.Type) is not { } elementType
                || RangeBounds(rangeIndex, () => Emit(new BuiltinInst("array_count", SilType.Int, array))) is not var (from, to)) {
                Done();
                return null;
            }

            var slice = Emit(new BuiltinInst("array_slice", new SliceType(elementType), array, from, to));
            Done();
            return slice;
        }

        /// <summary>A member reached through '?.': the receiver is optional, and the member is not the optional's own.</summary>
        bool IsChainedThrough(BoundExpression receiver, Symbol member) =>
            Sub(receiver.Type) is OptionalTypeSymbol
            && !(member.ContainingSymbol is NamedTypeSymbol owner && ReferenceEquals(owner.OriginalDefinition, Core.Optional));

        /// <summary>
        ///     '?.': the receiver unwrapped into a temporary, the member reached
        ///     through it and its result wrapped; none when the receiver is none.
        /// </summary>
        /// <remarks>With <paramref name="flatten" />, what is reached is optional already, and is the result as it is.</remarks>
        SilValue? OptionalChain(BoundExpression chained, BoundExpression receiver, Func<BoundExpression, BoundExpression> through, bool flatten = false) {
            if (Expression(receiver) is not { Type: EnumType optional } value || LowerType(chained.Type) is not EnumType resultType) {
                return Missing(chained, "this '?.'");
            }

            var some = optional.CaseIndex("Some");
            var present = function.AddBlock();
            var payload = Payload(present, optional.Cases[some].Payload);
            var absent = function.AddBlock();
            var after = function.AddBlock();
            var result = after.AddArgument(resultType);
            Jump(new SwitchEnumInst(value, [(some, present)], absent));

            _current = present;
            var unwrappedType = ((OptionalTypeSymbol)Sub(receiver.Type)).UnderlyingType;
            var temporary = new LocalSymbol("$chained", Core.Module, null, false);
            _slots[temporary] = NewSlot(payload.Type, "$chained");
            BindLocal(temporary, payload, chained.Syntax, declaration: true);
            Destroy(value);
            if (Expression(through(new BoundLocalAccess(receiver.Syntax, temporary, unwrappedType))) is { } reached) {
                // A member that gives Void makes a Void?, whose 'Some' carries nothing.
                var resultSome = resultType.CaseIndex("Some");
                Branch(after, flatten ? Owned(reached)
                    : Emit(new EnumInst(resultType, resultSome, resultType.Cases[resultSome].Payload is null ? null : Owned(reached))));
            }

            _current = absent;
            Destroy(value);
            Branch(after, Emit(new EnumInst(resultType, resultType.CaseIndex("None"), null)));

            _current = after;
            return result;
        }

        /// <summary>
        ///     'a.next?.v = 9': when there is a next, its v is stored, and the
        ///     value evaluated only then; when there is none, nothing is. An
        ///     object is written where it is. A value is a copy once out of its
        ///     optional, so the copy is stored, then wrapped and written back.
        ///     Used as a value, it is what was stored, wrapped, or none when
        ///     nothing was — or, where the chain flattens, what was stored.
        /// </summary>
        SilValue? ChainedAssignment(BoundAssignment assignment, BoundFieldAccess target, BoundExpression chainedFrom, bool asValue = false) {
            EnumType? resultType = null;
            if (asValue) {
                if (LowerType(assignment.Type) is not EnumType lowered) {
                    return Missing(assignment, "the value of this store through '?.'");
                }

                resultType = lowered;
            }

            var flattens = target.FlattensChain;
            var unwrappedType = ((OptionalTypeSymbol)Sub(chainedFrom.Type)).UnderlyingType;
            var writesBack = !unwrappedType.IsReferenceType;

            var borrows = new List<SilValue>();
            SilValue? place = null;
            SilValue? value;
            if (writesBack) {
                place = Address(chainedFrom, borrows);
                value = place is null ? null : Emit(new LoadInst(place));
            } else {
                value = Expression(chainedFrom);
            }

            if (value is not { Type: EnumType optional }) {
                return value is null ? null : Missing(assignment, "this store through '?.'");
            }

            var some = optional.CaseIndex("Some");
            var present = function.AddBlock();
            var payload = Payload(present, optional.Cases[some].Payload);
            var absent = function.AddBlock();
            var after = function.AddBlock();
            var result = resultType is null ? null : after.AddArgument(resultType);
            Jump(new SwitchEnumInst(value, [(some, present)], absent));

            _current = present;
            var temporary = new LocalSymbol("$chained", Core.Module, null, true);
            var slot = NewSlot(payload.Type, "$chained");
            _slots[temporary] = slot;
            BindLocal(temporary, payload, assignment.Syntax, declaration: true);
            Destroy(value);

            var through = new BoundLocalAccess(chainedFrom.Syntax, temporary, unwrappedType);
            var store = new BoundAssignment(assignment.Syntax, new BoundFieldAccess(target.Syntax, through, target.Field, assignment.Value.Type), assignment.Value);
            if ((resultType is null ? Assignment(store) : AssignmentValue(store)) is not { } stored) {
                return null;
            }

            if (writesBack) {
                var updated = Emit(new LoadInst(slot));
                Emit(new StoreInst(Emit(new EnumInst(optional, some, updated)), place!, StoreKind.Assign));
            }

            if (resultType is null) {
                Destroy(stored);
                Branch(after);
            } else {
                Branch(after, flattens ? Owned(stored) : Emit(new EnumInst(resultType, resultType.CaseIndex("Some"), Owned(stored))));
            }

            _current = absent;
            Destroy(value);
            if (resultType is null) {
                Branch(after);
            } else {
                Branch(after, Emit(new EnumInst(resultType, resultType.CaseIndex("None"), null)));
            }

            _current = after;
            EndAll(borrows);
            return (SilValue?)result ?? UnitValue.Instance;
        }

        /// <summary>'a ?? b': a's value when there is one; b, evaluated only then, when there is not.</summary>
        SilValue? Coalesce(BoundBinaryOperator coalesce) {
            if (Expression(coalesce.Left) is not { Type: EnumType optional } value || LowerType(coalesce.Type) is not { } type) {
                return Missing(coalesce, "this '??'");
            }

            var some = optional.CaseIndex("Some");
            var decision = coalesce.Syntax is BinaryExpressionSyntax written ? Decide(written.OperatorToken.SpanStart, "??", "value", "fallback") : null;
            var present = Mark(function.AddBlock(), decision, 0);
            var payload = Payload(present, optional.Cases[some].Payload);
            var absent = Mark(function.AddBlock(), decision, 1);
            var after = function.AddBlock();
            var result = after.AddArgument(type);
            Jump(new SwitchEnumInst(value, [(some, present)], absent));

            _current = present;
            var unwrapped = Owned(payload);
            Destroy(value);
            Branch(after, unwrapped);

            _current = absent;
            Destroy(value);
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

        /// <summary>The built-in '+' of two strings.</summary>
        bool IsStringConcat(BoundExpression expression) =>
            expression is BoundBinaryOperator { OperatorKind: BinaryOperatorKind.Add, Method: null } concat
            && Sub(concat.Left.Type).Equals(Core.String) && Sub(concat.Right.Type).Equals(Core.String);

        /// <summary>The operands of a chain of string '+'s, left to right.</summary>
        void Flatten(BoundExpression expression, List<BoundExpression> operands) {
            if (IsStringConcat(expression)) {
                var concat = (BoundBinaryOperator)expression;
                Flatten(concat.Left, operands);
                operands.Add(concat.Right);
            } else {
                operands.Add(expression);
            }
        }

        SilValue? Binary(BoundBinaryOperator binary) {
            // 'T.self == Int.self': a metatype's one value is its type, so in
            // this instance of the code the two are equal or not already. An
            // operand other than 'X.self' is still evaluated, for what it does.
            if (binary.OperatorKind is BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual
                && Sub(binary.Left.Type) is MetatypeTypeSymbol leftMetatype
                && Sub(binary.Right.Type) is MetatypeTypeSymbol rightMetatype) {
                // In unspecialised code a type parameter's is known only at run time.
                if (_genericMetadata.Count > 0 && (Mentions(leftMetatype.InstanceType, [.._genericMetadata.Keys]) || Mentions(rightMetatype.InstanceType, [.._genericMetadata.Keys]))) {
                    if (Expression(binary.Left) is not { } leftType || Expression(binary.Right) is not { } rightType) {
                        return null;
                    }

                    var sameType = Emit(new BuiltinInst("metadata_equal", SilType.Bool, leftType, rightType));
                    return binary.OperatorKind == BinaryOperatorKind.Equal ? sameType : Emit(new BuiltinInst("not_Bool", SilType.Bool, sameType));
                }

                // 'T.self' does nothing, and its metadata is not wanted.
                if (binary.Left is not BoundMetatype && Expression(binary.Left) is null || binary.Right is not BoundMetatype && Expression(binary.Right) is null) {
                    return null;
                }

                var same = leftMetatype.InstanceType.Equals(rightMetatype.InstanceType);
                var equal = binary.OperatorKind == BinaryOperatorKind.Equal;
                return Emit(new IntegerLiteralInst(SilType.Bool, same == equal ? 1 : 0));
            }

            // 'x == none': only the case matters, so any optional compares.
            if (binary.OperatorKind is BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual
                && (IsNone(binary.Right) ? binary.Left : IsNone(binary.Left) ? binary.Right : null) is { } tested) {
                if (Borrow(tested) is not { Type: EnumType testedType } testedValue) {
                    return Missing(binary, "this comparison with 'none'");
                }

                var noneCase = testedType.CaseIndex("None");
                var isNone = function.AddBlock();
                var isSome = function.AddBlock();
                var joined = function.AddBlock();
                var answer = joined.AddArgument(SilType.Bool);
                var equal = binary.OperatorKind == BinaryOperatorKind.Equal;
                Jump(new SwitchEnumInst(testedValue, [(noneCase, isNone)], isSome));
                _current = isNone;
                Branch(joined, Emit(new IntegerLiteralInst(SilType.Bool, equal ? 1 : 0)));
                _current = isSome;
                Branch(joined, Emit(new IntegerLiteralInst(SilType.Bool, equal ? 0 : 1)));
                _current = joined;
                End(testedValue);
                return answer;
            }

            // '(a, b) == (c, d)': the tuples given to the comparison bound
            // over them, which compares element by element.
            if (binary.Structural is { } byElement && Sub(binary.Left.Type) is TupleTypeSymbol) {
                if (Expression(binary.Left) is not { } leftTuple) {
                    return null;
                }

                BindLocal(byElement.Left, leftTuple, binary.Syntax, declaration: true);
                if (Expression(binary.Right) is not { } rightTuple) {
                    return null;
                }

                BindLocal(byElement.Right, rightTuple, binary.Syntax, declaration: true);
                var same = Expression(byElement.Equal);
                return same is null || binary.OperatorKind == BinaryOperatorKind.Equal
                    ? same
                    : Emit(new BuiltinInst("not_Bool", SilType.Bool, same));
            }

            // '[1, 2] == [1, 2]': as many elements, each equal to the other's.
            if (binary.Structural is { } byIndex && Sub(binary.Left.Type) is ArrayTypeSymbol) {
                if (Borrow(binary.Left) is not { } leftArray) {
                    return null;
                }

                if (Borrow(binary.Right) is not { } rightArray) {
                    End(leftArray);
                    return null;
                }

                var same = ArrayEquals(leftArray, rightArray, byIndex, binary);
                End(rightArray);
                End(leftArray);
                return same is null || binary.OperatorKind == BinaryOperatorKind.Equal
                    ? same
                    : Emit(new BuiltinInst("not_Bool", SilType.Bool, same));
            }

            // 'a + b + c' on strings: one string of the three, not one for each '+'.
            if (IsStringConcat(binary) && IsStringConcat(binary.Left)) {
                var operands = new List<BoundExpression>();
                Flatten(binary, operands);
                var pieces = new List<SilValue>();
                foreach (var operand in operands) {
                    if (Borrow(operand) is not { } piece) {
                        for (var i = pieces.Count - 1; i >= 0; i--) {
                            End(pieces[i]);
                        }

                        return null;
                    }

                    pieces.Add(piece);
                }

                return Joined(pieces);
            }

            if (Borrow(binary.Left) is not { } left) {
                return null;
            }

            if (Borrow(binary.Right) is not { } right) {
                End(left);
                return null;
            }

            // An optional compared with a plain value: the value, wrapped.
            if (binary.OperatorKind is BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual) {
                if (left.Type is EnumType { Cases: [("Some", { } leftPayload), ("None", null)] } leftOptional && right.Type == leftPayload) {
                    right = Hold(Emit(new EnumInst(leftOptional, 0, Owned(Unhold(right)))));
                } else if (right.Type is EnumType { Cases: [("Some", { } rightPayload), ("None", null)] } rightOptional && left.Type == rightPayload) {
                    // Left was borrowed first; keep the temporaries in order.
                    Unhold(right);
                    left = Emit(new EnumInst(rightOptional, 0, Owned(Unhold(left))));
                    Hold(left);
                    Hold(right);
                }
            }

            // Optionals compare equal when both are none, or both hold equal values.
            if (binary.OperatorKind is BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual
                && left.Type is EnumType { Cases: [("Some", { } payloadType), ("None", null)] } optional
                && right.Type == left.Type) {
                var equal = OptionalEquals(optional, payloadType, left, right, binary, binary.Structural);
                End(right);
                End(left);
                return equal is null || binary.OperatorKind == BinaryOperatorKind.Equal
                    ? equal
                    : Emit(new BuiltinInst("not_Bool", SilType.Bool, equal));
            }

            // An enum with no payloads and no '==' of its own is equal to
            // another when the two are the same case.
            if (binary.OperatorKind is BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual
                && IsPayloadFree(left.Type) && right.Type == left.Type) {
                var compared = SameCase(left, right, binary.OperatorKind == BinaryOperatorKind.Equal);
                End(right);
                End(left);
                return compared;
            }

            // 'a === b': the one object, whatever class each is seen as.
            if (binary.OperatorKind is BinaryOperatorKind.ReferenceEqual or BinaryOperatorKind.ReferenceNotEqual
                && left.Type is ClassType leftClass && right.Type is ClassType rightClass) {
                // Seen as the same class: the subclass's side as its base.
                // Two classes neither derives from never share an object.
                SilValue same = leftClass == rightClass ? Emit(new BuiltinInst("ref_identical", SilType.Bool, left, right))
                    : rightClass.IsSubclassOf(leftClass) ? Emit(new BuiltinInst("ref_identical", SilType.Bool, left, Emit(new UpcastInst(right, leftClass))))
                    : leftClass.IsSubclassOf(rightClass) ? Emit(new BuiltinInst("ref_identical", SilType.Bool, Emit(new UpcastInst(left, rightClass)), right))
                    : Emit(new IntegerLiteralInst(SilType.Bool, 0));
                End(right);
                End(left);
                return binary.OperatorKind == BinaryOperatorKind.ReferenceEqual
                    ? same
                    : Emit(new BuiltinInst("not_Bool", SilType.Bool, same));
            }

            var op = binary.OperatorKind switch {
                BinaryOperatorKind.Add when left.Type == SilType.String => "string_concat",
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

            var name = op is null or "string_concat" ? op : $"{op}_{left.Type}";
            if (name is null || SilBuiltins.Find(name) is not { } signature || left.Type != right.Type) {
                End(right);
                End(left);
                return Missing(binary, $"the operator in '{Describe(binary.Syntax)}' on '{binary.Left.Type.ToDisplayString()}'");
            }

            var result = Emit(new BuiltinInst(name, signature.Result, left, right));
            End(right);
            End(left);
            return result;
        }

        static bool IsPayloadFree(SilType type) =>
            type is EnumType { Cases.Count: > 0 } enumType && enumType.Cases.All(static c => c.Payload is null);

        /// <summary>Whether two payload-free enum values are the same case (or, with <paramref name="equal" /> false, different ones).</summary>
        SilValue SameCase(SilValue left, SilValue right, bool equal) {
            var enumType = (EnumType)left.Type;
            var first = CaseNumber(left, enumType);
            var second = CaseNumber(right, enumType);
            return Emit(new BuiltinInst(equal ? "cmp_eq_Int" : "cmp_ne_Int", SilType.Bool, first, second));
        }

        /// <summary>A plain enum's value as the integer its case is: one block per case, joined.</summary>
        SilValue RawValue(SilValue value, NamedTypeSymbol enumSymbol, SilType raw) {
            var cases = enumSymbol.OriginalDefinition.Members.OfType<EnumCaseSymbol>().ToList();
            return ByCase(value, (EnumType)value.Type, raw, i => Emit(new IntegerLiteralInst(raw, cases[i].RawValue)));
        }

        /// <summary>A plain enum's value as its case's name.</summary>
        SilValue CaseName(SilValue value, EnumType type) =>
            ByCase(value, type, SilType.String, i => Emit(new StringLiteralInst(type.Cases[i].Name)));

        /// <summary>One value for each case of a payload-free enum: a block per case, joined.</summary>
        SilValue ByCase(SilValue value, EnumType type, SilType result, Func<int, SilValue> make) {
            var joined = function.AddBlock();
            var made = joined.AddArgument(result);
            var targets = new List<(int, SilBlock)>();
            for (var i = 0; i < type.Cases.Count; i++) {
                targets.Add((i, function.AddBlock()));
            }

            Jump(new SwitchEnumInst(value, targets, null));
            foreach (var (index, block) in targets) {
                _current = block;
                Branch(joined, make(index));
            }

            _current = joined;
            return made;
        }

        /// <summary>
        ///     The case an integer is, compared with each case's value in
        ///     turn; one that is no case's traps, as an unwrapped none does.
        /// </summary>
        SilValue? CaseOfValue(SilValue raw, NamedTypeSymbol enumSymbol, EnumType type) {
            var compare = $"cmp_eq_{raw.Type}";
            if (SilBuiltins.Find(compare) is null) {
                return null;
            }

            var cases = enumSymbol.OriginalDefinition.Members.OfType<EnumCaseSymbol>().ToList();
            var joined = function.AddBlock();
            var made = joined.AddArgument(type);
            for (var i = 0; i < cases.Count; i++) {
                var found = function.AddBlock();
                var next = function.AddBlock();
                var same = Emit(new BuiltinInst(compare, SilType.Bool, raw, Emit(new IntegerLiteralInst(raw.Type, cases[i].RawValue))));
                Jump(new CondBranchInst(same, found, [], next, []));
                _current = found;
                Branch(joined, Emit(new EnumInst(type, type.CaseIndex(cases[i].Name), null)));
                _current = next;
            }

            Emit(new CondFailInst(Emit(new IntegerLiteralInst(SilType.Bool, 1)), $"no case of '{enumSymbol.Name}' has this value"));
            Jump(new UnreachableInst());
            _current = joined;
            return made;
        }

        /// <summary>A '@flags' enum's value as the name of the case it is exactly, or else its integer.</summary>
        SilValue? FlagsName(SilValue raw, NamedTypeSymbol flags, BoundNode at) {
            var compare = $"cmp_eq_{raw.Type}";
            var digits = $"string_from_{raw.Type}";
            if (SilBuiltins.Find(compare) is null || SilBuiltins.Find(digits) is null) {
                return Missing(at, $"interpolating a value of '{flags.Name}'");
            }

            var joined = function.AddBlock();
            var made = joined.AddArgument(SilType.String);
            var seen = new HashSet<long>();
            foreach (var flag in flags.OriginalDefinition.Members.OfType<EnumCaseSymbol>()) {
                if (!seen.Add(flag.RawValue)) {
                    continue;
                }

                var found = function.AddBlock();
                var next = function.AddBlock();
                var same = Emit(new BuiltinInst(compare, SilType.Bool, raw, Emit(new IntegerLiteralInst(raw.Type, flag.RawValue))));
                Jump(new CondBranchInst(same, found, [], next, []));
                _current = found;
                Branch(joined, Emit(new StringLiteralInst(flag.Name)));
                _current = next;
            }

            Branch(joined, Emit(new BuiltinInst(digits, SilType.String, raw)));
            _current = joined;
            return made;
        }

        /// <summary>Which case a payload-free enum value is, as an Int: one block per case, joined.</summary>
        SilValue CaseNumber(SilValue value, EnumType type) {
            var joined = function.AddBlock();
            var number = joined.AddArgument(SilType.Int);
            var targets = new List<(int, SilBlock)>();
            for (var i = 0; i < type.Cases.Count; i++) {
                targets.Add((i, function.AddBlock()));
            }

            Jump(new SwitchEnumInst(value, targets, null));
            foreach (var (index, block) in targets) {
                _current = block;
                Branch(joined, Emit(new IntegerLiteralInst(SilType.Int, index)));
            }

            _current = joined;
            return number;
        }

        static bool IsNone(BoundExpression expression) =>
            expression switch {
                BoundLiteral { Value: null } => true,
                BoundConversion conversion => IsNone(conversion.Operand),
                _ => false,
            };

        /// <summary>Two optionals of a comparable type compared: by case, and then by value.</summary>
        /// <remarks>
        ///     Two values held compare by their built-in comparison or, where
        ///     there is none, by the one the binder bound over stand-ins for
        ///     them (<paramref name="byPayload" />): a type's own '==', or a
        ///     tuple's element by element.
        /// </remarks>
        /// <summary>How many elements it takes for the runtime's comparison of them all to be quicker than comparing each: Array+Comparison.sb's <c>comparedByBits</c>.</summary>
        const int ComparedByBits = 8;

        /// <summary>
        ///     Two arrays compared: unequal at once if their counts differ, and
        ///     otherwise element by element, each pair bound to the comparison's
        ///     locals, until one pair differs.
        /// </summary>
        SilValue? ArrayEquals(SilValue left, SilValue right, StructuralComparison byIndex, BoundNode at) {
            if (ArrayType.ElementOf(left.Type) is not { } element) {
                return Missing(at, "comparing arrays");
            }

            var count = Emit(new BuiltinInst("array_count", SilType.Int, left));
            var sameCount = Emit(new BuiltinInst("cmp_eq_Int", SilType.Bool, count, Emit(new BuiltinInst("array_count", SilType.Int, right))));
            var header = function.AddBlock();
            var index = header.AddArgument(SilType.Int);
            var body = function.AddBlock();
            var after = function.AddBlock();
            var result = after.AddArgument(SilType.Bool);
            var differ = Emit(new IntegerLiteralInst(SilType.Bool, 0));
            if (SilBuiltins.ComparesByBits(element)) {
                // More than a few elements that are equal when their bits
                // are, as Array+Comparison.sb's '==' compares them: at once.
                var sized = function.AddBlock();
                var atOnce = function.AddBlock();
                Jump(new CondBranchInst(sameCount, sized, [], after, [differ]));

                _current = sized;
                var many = Emit(new BuiltinInst("cmp_ge_Int", SilType.Bool, count, Emit(new IntegerLiteralInst(SilType.Int, ComparedByBits))));
                Jump(new CondBranchInst(many, atOnce, [], header, [Emit(new IntegerLiteralInst(SilType.Int, 0))]));

                _current = atOnce;
                var mismatch = Emit(new BuiltinInst("array_mismatch", SilType.Int, left, right));
                Branch(after, Emit(new BuiltinInst("cmp_eq_Int", SilType.Bool, mismatch, count)));
            } else {
                Jump(new CondBranchInst(sameCount, header, [Emit(new IntegerLiteralInst(SilType.Int, 0))], after, [differ]));
            }

            // Every index looked at, and every element equal: the arrays are.
            _current = header;
            var more = Emit(new BuiltinInst("cmp_lt_Int", SilType.Bool, index, count));
            Jump(new CondBranchInst(more, body, [], after, [Emit(new IntegerLiteralInst(SilType.Bool, 1))]));

            _current = body;
            BindLocal(byIndex.Left, Emit(new BuiltinInst("array_get", element, left, index)), at.Syntax, declaration: true);
            BindLocal(byIndex.Right, Emit(new BuiltinInst("array_get", element, right, index)), at.Syntax, declaration: true);
            if (Expression(byIndex.Equal) is not { } same) {
                return null;
            }

            var next = Emit(new BuiltinInst("add_Int", SilType.Int, index, Emit(new IntegerLiteralInst(SilType.Int, 1))));
            Jump(new CondBranchInst(same, header, [next], after, [Emit(new IntegerLiteralInst(SilType.Bool, 0))]));

            _current = after;
            return result;
        }

        SilValue? OptionalEquals(EnumType optional, SilType payloadType, SilValue left, SilValue right, BoundNode at, StructuralComparison? byPayload = null) {
            var compare = $"cmp_eq_{payloadType}";
            if (SilBuiltins.Find(compare) is null && byPayload is null) {
                return Missing(at, $"comparing optionals of {payloadType}");
            }

            var some = optional.CaseIndex("Some");
            var leftSome = function.AddBlock();
            var leftValue = Payload(leftSome, payloadType);
            var leftNone = function.AddBlock();
            var after = function.AddBlock();
            var result = after.AddArgument(SilType.Bool);
            Jump(new SwitchEnumInst(left, [(some, leftSome)], leftNone));

            // Left holds a value: equal only if right holds an equal one.
            _current = leftSome;
            var bothSome = function.AddBlock();
            var rightValue = Payload(bothSome, payloadType);
            var differ = function.AddBlock();
            Jump(new SwitchEnumInst(right, [(some, bothSome)], differ));
            _current = bothSome;
            if (SilBuiltins.Find(compare) is not null) {
                Branch(after, Emit(new BuiltinInst(compare, SilType.Bool, leftValue, rightValue)));
            } else {
                BindLocal(byPayload!.Left, Owned(leftValue), at.Syntax, declaration: true);
                BindLocal(byPayload.Right, Owned(rightValue), at.Syntax, declaration: true);
                if (Expression(byPayload.Equal) is not { } same) {
                    return null;
                }

                Branch(after, same);
            }
            _current = differ;
            Branch(after, Emit(new IntegerLiteralInst(SilType.Bool, 0)));

            // Left is none: equal only if right is too.
            _current = leftNone;
            var rightSome = function.AddBlock();
            Payload(rightSome, payloadType);
            var bothNone = function.AddBlock();
            Jump(new SwitchEnumInst(right, [(some, rightSome)], bothNone));
            _current = rightSome;
            Branch(after, Emit(new IntegerLiteralInst(SilType.Bool, 0)));
            _current = bothNone;
            Branch(after, Emit(new IntegerLiteralInst(SilType.Bool, 1)));

            _current = after;
            return result;
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
        ///     arm cannot happen. The subject lives until an arm is chosen.
        /// </summary>
        SilValue? Match(BoundMatch match) {
            if (Expression(match.Subject) is not { } subject) {
                return null;
            }

            var resultType = LowerType(match.Type);
            var producesValue = resultType is not null && resultType != SilType.Unit && !match.Type.Equals(Core.Never);
            var after = function.AddBlock();
            var result = producesValue ? after.AddArgument(resultType!) : null;

            // An outcome per arm: chosen, its guard if any having held.
            var decision = match.Syntax is MatchExpressionSyntax written
                ? Decide(written.MatchKeyword.SpanStart, "match", [
                    ..match.Arms.Select(a => a.Syntax is MatchArmSyntax armSyntax && !a.IsDefault
                        ? string.Join(", ", armSyntax.Patterns.Select(TextOf)) + (armSyntax.WhereClause is { } where ? " " + TextOf(where) : "")
                        : "default"),
                ])
                : null;

            for (var index = 0; index < match.Arms.Length; index++) {
                var arm = match.Arms[index];
                var body = function.AddBlock();
                var nextArm = function.AddBlock();
                if (arm.Guard is null) {
                    Mark(body, decision, index);
                }

                Hold(subject);
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
                    var guarded = Mark(function.AddBlock(), decision, index);
                    Condition(guard, guarded, nextArm);
                    _current = guarded;
                }

                Unhold(subject);
                Destroy(subject);

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
                                Destroy(armValue);
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

            // Every arm left: nothing comes here.
            if (match.Type.Equals(Core.Never)) {
                Jump(new UnreachableInst());
            }

            return result ?? (SilValue)UnitValue.Instance;
        }

        /// <summary>
        ///     <c>if c { a } else { b }</c> as a value: the condition branches, as
        ///     for the statement, and each branch's last line goes to the join
        ///     as its value, after the branch's defers have run.
        /// </summary>
        SilValue? IfValue(BoundIfExpression ifExpression) {
            if (LowerType(ifExpression.Type) is not { } resultType) {
                return Missing(ifExpression, $"an 'if' of type '{ifExpression.Type.ToDisplayString()}'");
            }

            var producesValue = resultType != SilType.Unit && !ifExpression.Type.Equals(Core.Never);
            var after = function.AddBlock();
            var result = producesValue ? after.AddArgument(resultType) : null;

            Branches(ifExpression.Body);
            _current = after;
            return result ?? (SilValue)UnitValue.Instance;

            void Branches(BoundIf branch) {
                var consequence = function.AddBlock();
                var alternative = function.AddBlock();
                var decision = branch.Syntax is IfStatementSyntax written ? Decide(written.IfKeyword.SpanStart, "if", "true", "false") : null;
                Condition(branch.Condition, Mark(consequence, decision, 0), Mark(alternative, decision, 1));

                _current = consequence;
                Arm(branch.Consequence);

                _current = alternative;
                if (branch.Alternative is BoundIf elseIf) {
                    Branches(elseIf);
                } else if (branch.Alternative is { } otherwise) {
                    Arm(otherwise);
                }
            }

            void Arm(BoundStatement body) {
                if (_current is null || body is not BoundBlock block) {
                    return;
                }

                _scopes.Add([]);
                foreach (var statement in block.Statements) {
                    if (_current is null) {
                        break;
                    }

                    if (statement != block.Statements[^1] || statement is not BoundExpressionStatement last) {
                        Statement(statement);
                        continue;
                    }

                    var value = Expression(last.Expression);
                    if (_current is null || value is null) {
                        break;
                    }

                    RunDefers(_scopes.Count - 1);
                    if (producesValue && value is not UnitValue && value.Type == resultType) {
                        Branch(after, value);
                    } else {
                        Destroy(value);
                        if (producesValue) {
                            Jump(new UnreachableInst());
                        } else {
                            Branch(after);
                        }
                    }
                }

                // A branch that ends some other way leaves: by 'return' or
                // 'throw', where it has run its defers already.
                if (_current is not null) {
                    RunDefers(_scopes.Count - 1);
                    Jump(new UnreachableInst());
                }

                _scopes.RemoveAt(_scopes.Count - 1);
            }
        }

        /// <summary>
        ///     <c>try e</c> lets errors go to the handler in force; the others
        ///     put a handler of their own around <c>e</c>.
        /// </summary>
        SilValue? Try(BoundTry tryExpression) {
            if (tryExpression.TryKind == TryKind.Propagate) {
                return Expression(tryExpression.Expression);
            }

            // Nothing in 'e' throws (the binder warns, S3036), so there is no
            // error to handle: 'try? e' is 'e' made optional, and 'try! e' and
            // 'try e ?? f' are 'e'.
            if (tryExpression.ErrorLocal is null && !Throws(tryExpression.Expression)) {
                var plain = Expression(tryExpression.Expression);
                if (plain is null || _current is null || tryExpression.TryKind != TryKind.Optional) {
                    return plain;
                }

                if (LowerType(tryExpression.Type) is not EnumType wrapped) {
                    return Missing(tryExpression, $"a 'try' of type '{tryExpression.Type.ToDisplayString()}'");
                }

                return Emit(new EnumInst(wrapped, wrapped.CaseIndex("Some"), Carried(plain)));
            }

            // What the catch's 'error' is, when it names it; otherwise
            // whatever the expression can throw.
            var errorType = tryExpression.ErrorLocal is { } named ? LowerType(named.Type) : ThrownBy(tryExpression.Expression);
            if (errorType is null) {
                return Missing(tryExpression, "a 'try' whose error type has no SIL type");
            }

            var resultType = LowerType(tryExpression.Type);
            if (resultType is null) {
                return Missing(tryExpression, $"a 'try' of type '{tryExpression.Type.ToDisplayString()}'");
            }

            // Its outcomes: a value, or an error caught, which only the
            // handler's own edges reach.
            var decision = tryExpression.Syntax is TryExpressionSyntax written
                ? Decide(written.TryKeyword.SpanStart, tryExpression.TryKind switch {
                    TryKind.Optional => "try?",
                    TryKind.Force => "try!",
                    _ => "catch",
                }, "value", "error")
                : null;
            var handler = Mark(function.AddBlock(), decision, 1);
            var error = handler.AddArgument(errorType);
            var after = function.AddBlock();
            var result = resultType == SilType.Unit ? null : after.AddArgument(resultType);

            _handlers.Add(new(handler, errorType, _scopes.Count, _temporaries.Count));
            var value = Expression(tryExpression.Expression);
            _handlers.RemoveAt(_handlers.Count - 1);

            if (value is not null && _current is not null) {
                Enter(decision, 0);
                if (tryExpression.TryKind == TryKind.Optional) {
                    var optional = (EnumType)resultType;
                    Branch(after, Emit(new EnumInst(optional, optional.CaseIndex("Some"), Carried(value))));
                } else if (result is null) {
                    Branch(after);
                } else {
                    Branch(after, value);
                }
            }

            _current = handler;
            switch (tryExpression.TryKind) {
                case TryKind.Optional: {
                    Destroy(error);
                    var optional = (EnumType)resultType;
                    Branch(after, Emit(new EnumInst(optional, optional.CaseIndex("None"), null)));
                    break;
                }

                case TryKind.Force:
                    Emit(new CondFailInst(Emit(new IntegerLiteralInst(SilType.Bool, 1)), "'try!' caught an error"));
                    Jump(new UnreachableInst());
                    break;

                case TryKind.Fallback when tryExpression.Fallback is { } fallback: {
                    if (tryExpression.ErrorLocal is { } local) {
                        BindLocal(local, error, tryExpression.Syntax);
                    } else {
                        Destroy(error);
                    }

                    // A 'catch { ... }' block's statements run before its
                    // value, in a scope of their own that the value can see.
                    if (tryExpression.FallbackBody is { } body) {
                        _scopes.Add([]);
                        foreach (var statement in body.Statements) {
                            Statement(statement);
                        }
                    }

                    var alternative = _current is null ? null : Expression(fallback);
                    if (tryExpression.FallbackBody is not null) {
                        if (_current is not null) {
                            RunDefers(_scopes.Count - 1);
                        }

                        _scopes.RemoveAt(_scopes.Count - 1);
                    }

                    if (alternative is not null && _current is not null) {
                        if (result is null) {
                            Destroy(alternative);
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

        /// <summary>The error type of the throwing calls inside an expression: one's, or the union of theirs.</summary>
        SilType? ThrownBy(BoundNode node) {
            var thrown = new List<TypeSymbol>();
            CollectThrown(node, thrown);
            return thrown.Count == 0 ? null
                : thrown.Any(t => t.Equals(Core.AnyError)) ? LowerType(Core.AnyError)
                : LowerType(StructuralUnionTypeSymbol.Create(thrown));
        }

        bool Throws(BoundNode node) {
            var thrown = new List<TypeSymbol>();
            CollectThrown(node, thrown);
            return thrown.Count > 0;
        }

        /// <summary>True for an error type that, here, has no values: Never, or a union of nothing else.</summary>
        bool ThrowsNothing(TypeSymbol thrown) =>
            CanonicalHere(thrown).Equals(Core.Never) || thrown is StructuralUnionTypeSymbol union && union.Cases.All(c => CanonicalHere(c).Equals(Core.Never));

        void CollectThrown(BoundNode node, List<TypeSymbol> into) {
            switch (node) {
                // A 'rethrows' callee throws what its arguments do, not all its signature allows.
                // So does a 'rethrows(E)' one, and E as well.
                case BoundCall { Function.IsRethrows: true } rethrowing:
                    if (RethrownBy(rethrowing) is { } rethrown) {
                        into.Add(Sub(rethrowing.Substitution.Apply(rethrown)));
                    }

                    if (rethrowing.Function.RethrownAlso is { } also) {
                        into.Add(Sub(rethrowing.Substitution.Apply(also)));
                    }

                    break;

                // A generator's call captures; its 'next()' is what throws.
                case BoundCall call when generator._compilation.GetGenerator(call.Function) is { } yielding && ReferenceEquals(yielding.Function, call.Function):
                    break;

                case BoundCall { Function.ThrownType: { } thrown } call:
                    into.Add(Sub(call.Substitution.Apply(thrown)));
                    break;

                case BoundIndirectCall { Callee.Type: FunctionTypeSymbol { ThrownType: { } indirectThrown } }:
                    into.Add(Sub(indirectThrown));
                    break;
            }

            // What throws Never throws nothing.
            into.RemoveAll(t => t.Equals(Core.Never));

            foreach (var child in node.Children) {
                if (child is not BoundClosure) {
                    CollectThrown(child, into);
                }
            }
        }

        /// <summary>
        ///     What a call to a 'rethrows' function can throw: what the
        ///     arguments it was given throw, before they were converted to its
        ///     parameters' types — as the binder decided whether it needs 'try'.
        /// </summary>
        static TypeSymbol? RethrownBy(BoundCall call) {
            TypeSymbol? thrown = null;
            foreach (var argument in call.Arguments) {
                var passed = argument is BoundConversion { ConversionKind: ConversionKind.Upcast or ConversionKind.Identity } conversion
                    ? conversion.Operand.Type
                    : argument.Type;
                if ((passed is OptionalTypeSymbol optional ? optional.UnderlyingType : passed) is FunctionTypeSymbol { ThrownType: { } one }) {
                    thrown = thrown is null ? one : StructuralUnionTypeSymbol.Create([thrown, one]);
                }
            }

            return thrown;
        }

        // -------------------------------------------------------------------
        // Calls
        // -------------------------------------------------------------------

        SilValue? Call(BoundCall call) {
            Locate(call.Syntax);

            // A call that opens a Type: its metadata and tables found first (SilGen.Opening.cs).
            if (!ReferenceEquals(_openingCall, call) && call.Arguments.Any(static a => a is BoundConversion { ConversionKind: ConversionKind.Open })) {
                return OpenedCall(call, null);
            }

            // Generic code given an existential for a type parameter: the existential opened (SilGen.Opening.cs).
            if (!ReferenceEquals(_openingCall, call) && OpensExistential(call) is { } opening) {
                return OpenedExistentialCall(call, opening);
            }

            var callee = call.Function;

            // An inline array's members are the inline layout's, whatever declares them.
            if (call.Receiver is { } inlineArray && IsInlineArray(inlineArray)) {
                return callee.Name == "append" ? InlineAppend(call) : InlineArrayMember(inlineArray, callee.Name, call);
            }

            // The standard library's @builtin members: the builtin, here (SilGen.Builtins.cs).
            if (callee.Builtin is not null) {
                return BuiltinCall(call, callee);
            }

            // A generic one is called as a generic function is, given its context.
            if (IsLocalFunction(callee) && generator.Captures(callee) && callee.TypeParameters.IsEmpty) {
                return LocalFunctionCall(call);
            }

            // Sable.Core's @intrinsic members: the compiler writes their bodies.
            if (IsIntrinsic(callee) && callee is { Name: "blackBox", ContainingSymbol: ModuleSymbol }) {
                return BlackBox(call);
            }

            if (IsIntrinsic(callee) && callee is { Name: "compileError", ContainingSymbol: ModuleSymbol }) {
                return CompileError(call);
            }

            if (IsIntrinsic(callee)) {
                var owner = Owner(callee)?.OriginalDefinition;

                // 'Runtime.waitForSocket(socket, writable:)': a place the task
                // suspends until the I/O reactor says the socket is ready.
                if (owner is { Name: "Runtime", ContainingSymbol: ModuleSymbol { Name: "Sable.Core" } } && callee.Name == "waitForSocket") {
                    if (Expression(call.Arguments[0]) is not { } socket || Expression(call.Arguments[1]) is not { } writable) {
                        return null;
                    }

                    Emit(new BuiltinInst("task_io_wait", SilType.Unit, socket, writable));
                    return UnitValue.Instance;
                }

                // Tasks: made, slept, and yielded to (SilGen.Tasks.cs).
                if (owner is not null && ReferenceEquals(owner, Core.Task)) {
                    if (callee.Name is "spawn" or "detached") {
                        return Spawn(call, callee.Name == "detached");
                    }

                    if (callee.Name == "installCancellationHandler") {
                        return InstallCancellationHandler(call);
                    }

                    if (callee.Name == "waitForGroup") {
                        if (Expression(call.Arguments[0]) is not { } group) {
                            return null;
                        }

                        Emit(new BuiltinInst("task_group_wait", SilType.Unit, group));
                        return UnitValue.Instance;
                    }

                    if (callee.Name == "park") {
                        if (Expression(call.Arguments[0]) is not { } list) {
                            return null;
                        }

                        Emit(new BuiltinInst("task_park", SilType.Unit, list));
                        return UnitValue.Instance;
                    }

                    if (callee.Name == "parkCancellably") {
                        if (Expression(call.Arguments[0]) is not { } list || Expression(call.Arguments[1]) is not { } ticket) {
                            return null;
                        }

                        Emit(new BuiltinInst("task_park_cancellable", SilType.Unit, list, ticket));
                        return UnitValue.Instance;
                    }

                    if (callee.Name == "runBlocking") {
                        return RunBlocking(call);
                    }

                    if (callee.Name == "sleepUncancelled") {
                        if (Expression(call.Arguments[0]) is not { } nanoseconds) {
                            return null;
                        }

                        Emit(new BuiltinInst("task_sleep", SilType.Unit, nanoseconds));
                        return UnitValue.Instance;
                    }

                    if (callee.Name == "cancel") {
                        return CancelTask(call);
                    }

                    Emit(new BuiltinInst("task_yield", SilType.Unit));
                    return UnitValue.Instance;
                }

                // 'Runtime.share(value)': its objects counted atomically from now on.
                if (owner is { Name: "Runtime", ContainingSymbol: ModuleSymbol { Name: "Sable.Core" } } && callee.Name == "share") {
                    return RawMemoryIntrinsic(call, "share");
                }

                // A value at a raw address (RawMemory.sb): the type is the
                // call's, as specialised here — the result, or the value given.
                if (owner is { Name: "RawMemory", ContainingSymbol: ModuleSymbol { Name: "Sable.Core" } }) {
                    return RawMemoryIntrinsic(call, callee.Name);
                }

                // C function pointers (SilGen.Foreign.cs).
                if (owner is { Name: "CFunction", ContainingSymbol: ModuleSymbol { Name: "Sable.Interop" } }
                    || callee is { Name: "cFunction", ContainingSymbol: ModuleSymbol { Name: "Sable.Interop" } }) {
                    return ForeignIntrinsic(call);
                }

                // An inline-layout value's bytes (SilGen.Inline.cs).
                if (owner is not null && ReferenceEquals(owner, Core.Bytes)) {
                    return BytesOf(call);
                }

                // 'arena.alloc(value)'.
                if (owner is not null && owner.Equals(Core.Region)) {
                    return RegionAlloc(call);
                }

                // A type's metadata, and its value witnesses (SilGen.Metadata.cs).
                if (owner is not null && owner.Equals(Core.Type)) {
                    return MetadataIntrinsic(call);
                }

                // 'type(of: x)': the dynamic type of the value (SilGen.Metadata.cs).
                if (callee is { Name: "type", ContainingSymbol: ModuleSymbol { Name: "Sable.Core" } }) {
                    return DynamicType(call);
                }

                // Reflection (SilGen.Reflection.cs).
                return Intrinsic(call);
            }

            // 'flags.contains(.Write)', on a '@flags' enum: every flag of the
            // argument set in the receiver.
            if (callee is { Name: "contains", IsSynthesized: true, ContainingSymbol: NamedTypeSymbol { IsFlags: true } flagsType }) {
                return FlagsContains(call, flagsType);
            }

            // 'self.init(…)' in a value type's initialiser: what the other
            // initialiser builds is self, stored where self is being built.
            if (callee.FunctionKind == FunctionKind.Initializer && call.Receiver is BoundSelfAccess
                && LowerType(call.Type) is not ClassType) {
                if (_selfAddress is not { } selfSlot) {
                    return Missing(call, "'self.init' here");
                }

                var built = Expression(new BoundCall(call.Syntax, null, callee, call.Arguments, call.Type) { Substitution = call.Substitution });
                if (built is null) {
                    return null;
                }

                Emit(new StoreInst(Owned(built), selfSlot, StoreKind.Init));
                return UnitValue.Instance;
            }

            // 'super.init()' where the superclass has only the implicit
            // initialiser: its fields' initial values, stored into this object.
            var inheritedInit = generator._compilation.InheritedInitializers.ContainsKey(callee);
            if (callee is { FunctionKind: FunctionKind.Initializer, IsSynthesized: true, Parameters.IsEmpty: true }
                && !inheritedInit && call.Receiver is BoundSuperAccess super) {
                return SuperImplicitInit(call, super);
            }

            // A class or actor's implicit 'init()'.
            if (!inheritedInit && callee is { FunctionKind: FunctionKind.Initializer, IsSynthesized: true, Parameters.IsEmpty: true }
                && LowerType(call.Type) is ClassType implicitClass) {
                return ImplicitInit(call, implicitClass);
            }

            // Inline layout: a value from bytes, its 'fromBytes' synthesised
            // for InlineLayout (Compilation.InlineLayout.cs); an inline array's members.
            if (callee is { Name: "fromBytes", IsStatic: true, ContainingSymbol: NamedTypeSymbol { IsInlineLayout: true } }) {
                return FromBytes(call);
            }

            // A synthesised memberwise initialiser is the struct, built from its fields.
            if (!inheritedInit && callee is { FunctionKind: FunctionKind.Initializer, IsSynthesized: true }) {
                if (LowerType(call.Type) is not StructType structType) {
                    return Missing(call, $"initialising '{Sub(call.Type).ToDisplayString()}'");
                }

                if (Evaluate(call.Arguments) is not { } fields) {
                    return null;
                }

                if (fields.Count != structType.Fields.Count) {
                    fields.ForEach(Destroy);
                    return Missing(call, "a memberwise initialiser that leaves a field to its default");
                }

                return Emit(new StructInst(structType, [..fields.Select((f, i) => IntoField(structType.Fields[i].Type, AsFunction(f, structType.Fields[i].Type), Emit))]));
            }

            // A requirement of Error's, called on 'any Error': through the
            // object's vtable, where its box put the error type's own.
            if (OwnerOf(callee) is { TypeKind: TypeKind.Protocol } && call.Receiver is { } errorReceiver && IsAnyError(errorReceiver.Type)) {
                return ErrorCall(errorReceiver, SlotName(callee), call.Arguments, call);
            }

            // A protocol's requirement, called on an existential: through its
            // table; a member of an extension of the protocol, on the value
            // opened as a type known by its metadata.
            if (OwnerOf(callee) is { TypeKind: TypeKind.Protocol } && call.Receiver is { } existentialReceiver
                && Sub(existentialReceiver.Type) is ExistentialTypeSymbol) {
                return IsRequirementOf(existentialReceiver, SlotName(callee)) && !OpensForResult(callee, callee.ReturnType, callee.ThrownType) && generator.OwnShape(callee) is null
                    ? ExistentialCall(existentialReceiver, SlotName(callee), call.Arguments, call)
                    : OpenedExistential(existentialReceiver, callee, callee.IsMutating, call,
                        (self, opened) => {
                            var opening = Opening(call.Substitution, opened);
                            return new BoundCall(call.Syntax, self, callee, call.Arguments, opening.Apply(callee.ReturnType)) { Substitution = opening };
                        }, call.Type);
            }

            // 'C()' for C: RangeReplaceableCollection, C an array: '[]'.
            if (OwnerOf(callee) is { TypeKind: TypeKind.Protocol } && callee is { FunctionKind: FunctionKind.Initializer, Parameters.IsEmpty: true }
                && call.Receiver is BoundTypeExpression made && Sub(made.ReferencedType) is ArrayTypeSymbol emptyArray) {
                return ArrayLiteral(new BoundArray(call.Syntax, emptyArray, []), emptyArray);
            }

            // A requirement called on a type parameter of unspecialised code:
            // through the witness table it is given.
            if (OwnerOf(callee) is { TypeKind: TypeKind.Protocol } && OpaqueSelf(call) is { } opaqueSelf) {
                return GenericRequirementCall(call, opaqueSelf);
            }

            // A protocol's requirement, called on a type parameter: the
            // concrete type's own member, now that the type is known.
            if (OwnerOf(callee) is { TypeKind: TypeKind.Protocol } && Witness(call) is { } witness) {
                if (witness.Builtin is not null) {
                    return BuiltinCall(call, witness);
                }

                // 'T.fromBytes(…)' where T is an @inline_layout struct: its synthesised body.
                if (witness is { Name: "fromBytes", IsStatic: true, ContainingSymbol: NamedTypeSymbol { IsInlineLayout: true } }) {
                    return FromBytes(call);
                }

                callee = witness;
            }

            // C's '...': declared for the types this call passes.
            if (callee is { ForeignName: { } cName, Parameters: [.., { IsVariadic: true }] }) {
                return VariadicForeignCall(call, callee, cName);
            }

            // A method that overrides one whose slot is in another convention: called as that one.
            if (HasSelf(callee) && call.Receiver is not BoundSuperAccess && generator.IsDynamic(callee) && generator.DynamicRoot(callee) is { } root) {
                var receiver = call.Receiver ?? new BoundSelfAccess(call.Syntax, SelfType(symbol, substitution), false);
                callee = root;
                call = new BoundCall(call.Syntax, receiver, root, call.Arguments, call.Type) { Substitution = call.Substitution };
            }

            string name;
            FunctionType? type;
            GenericShape? genericShape = null;
            var bound = IsGeneric(callee) ? OwnSelfBound(callee, CallSubstitution(call, callee)) : TypeSubstitution.Empty;
            if (IsGeneric(callee) && CallsUnspecialised(callee, bound, call.Syntax, out genericShape)) {
                // One instance for every type (#297): boxes, metadata and witness tables.
                if (generator.UnspecialisedInstance(callee, genericShape!, call.Syntax) is not { } generic) {
                    _failed = true;
                    return null;
                }

                (name, type) = generic;
            } else if (IsGeneric(callee) && _genericMetadata.Count > 0 && bound.Parameters.Any(p => bound.Lookup(p) is { } given && Mentions(given, [.._genericMetadata.Keys]))) {
                return Missing(call, $"'{callee.Name}' with a type parameter of unspecialised code");
            } else if (IsGeneric(callee)) {
                if (generator.Instance(callee, CallSubstitution(call, callee), call.Syntax, polymorphic: true) is not { } instance) {
                    _failed = true;
                    return null;
                }

                // The instance that serves every type: its arguments boxed
                // as the existentials it takes, and its result unboxed.
                if (generator.PolymorphicOf(instance.Name) is { } polymorphic && PolymorphicCall(call, callee, polymorphic.Substitution) is (true, var result)) {
                    return result;
                }

                (name, type) = instance;
            } else {
                if (!generator._names.TryGetValue(callee, out var plainName)) {
                    return Missing(call, $"the call '{Describe(call.Syntax)}'");
                }

                name = plainName;
                type = generator._module.Find(name)?.Type ?? generator.SignatureOf(callee, call.Syntax, TypeSubstitution.Empty);
                if (type is null) {
                    _failed = true;
                    return null;
                }
            }

            // 'super.init(...)': the superclass's initialising half, given this
            // object; 'self.init(...)' in a class's initialiser: its own
            // class's other one, likewise.
            var superInit = callee.FunctionKind == FunctionKind.Initializer && call.Receiver is BoundSuperAccess or BoundSelfAccess;
            if (superInit) {
                name = InitializerName(name);
                type = InitializerType(type);
            }

            // Everything passed is borrowed for the call: a parameter as it
            // is, anything evaluated as a temporary destroyed after.
            var arguments = new List<SilValue>();
            var borrows = new List<SilValue>();
            var owned = new List<SilValue>();
            var inPlace = genericShape is not null && LendsInPlace(callee, type, call);
            if (HasSelf(callee) || superInit) {
                SilValue? self;
                if (type.Parameters[0] is AddressType) {
                    self = call.Receiver is null or BoundSelfAccess ? _selfAddress : Address(call.Receiver, borrows);
                    if (self is not null) {
                        self = AsMutableSliceReceiver(call, self, type.Parameters[0], borrows);
                    }
                } else if (call.Receiver is null) {
                    self = _selfValue ?? (_selfAddress is { } address ? Hold(Emit(new LoadInst(address))) : null)
                        ?? (CapturedSelf() is { } captured ? Hold(captured) : null);
                    if (self is not null && IsOwned(self)) {
                        borrows.Add(self);
                    }

                    // A protocol's default, its Self an array, calling what the
                    // slice's extension declares: self, viewed as a slice.
                    if (self is not null) {
                        self = AsSliceReceiver(self, type.Parameters[0], borrows);
                    }
                } else if (inPlace && SilType.IsIndirect(type.Parameters[0]) && LowerType(Sub(call.Receiver.Type)) is { } receiverType && !SilType.IsIndirect(receiverType)
                           && GenericInPlace(call.Receiver, receiverType, type.Parameters[0], borrows) is { } lent) {
                    // A local the callee knows only as Self: lent where it is.
                    self = lent;
                } else {
                    self = Borrow(call.Receiver);
                    if (self is not null) {
                        borrows.Add(self);
                        self = AsSliceReceiver(self, type.Parameters[0], borrows);
                    }
                }

                if (self is null) {
                    return Missing(call, "the receiver of this call");
                }

                // A method a superclass declares takes the object as that class.
                if (self.Type is ClassType selfClass && type.Parameters[0] is ClassType parameterClass && selfClass != parameterClass) {
                    self = AsClass(self, selfClass, parameterClass);
                }

                // One unspecialised code knows only by its metadata (a protocol
                // extension's member) takes it where it is, seen as Self.
                if (genericShape is not null && SilType.MentionsOpaque(type.Parameters[0]) && !SilType.SameErasingOpaque(self.Type, type.Parameters[0])) {
                    self = GenericReceiver(self, type.Parameters[0], borrows);
                }

                arguments.Add(self);
            }

            // A variadic parameter takes the arguments from its position on,
            // gathered into an array.
            var variadicAt = callee.Parameters.Length > 0 && callee.Parameters[^1].IsVariadic ? callee.Parameters.Length - 1 : -1;
            var passed = call.Arguments.Length;
            if (variadicAt >= 0) {
                passed = variadicAt;
            }

            CheckExclusivity(call, callee, type.Parameters.Length > 0 && type.Parameters[0] is AddressType && HasSelf(callee));
            if (arguments.Count > 0 && arguments[0].Type is AddressType && HasSelf(callee)) {
                BeginAccess(arguments[0], modify: true, borrows);
            }

            for (var i = 0; i < passed; i++) {
                // A value of a type parameter's type, to unspecialised code: borrowed where it is.
                // So is an optional or a tuple of them, laid out at run time.
                if (genericShape is not null && i < callee.Parameters.Length && SilType.IsIndirect(type.Parameters[arguments.Count])) {
                    var given = Substitute(WithAssociatedTypes(callee, bound), callee.Parameters[i].Type);
                    if (GenericArgument(call.Arguments[i], given, type.Parameters[arguments.Count], borrows, inPlace) is not { } genericArgument) {
                        return null;
                    }

                    arguments.Add(genericArgument);
                    continue;
                }

                // A function of them, in the generic convention: the closure
                // as it is, or a thunk over it that converts (#297).
                if (genericShape is not null && i < callee.Parameters.Length && callee.Parameters[i] is { IsByReference: false, Modifier: ParameterModifier.None or ParameterModifier.In }
                    && !type.IsOwned(arguments.Count) && type.Parameters[arguments.Count] is ThickFunctionType genericFunction && SilType.MentionsOpaque(genericFunction)) {
                    if (GenericFunctionArgument(call.Arguments[i], genericFunction, borrows) is not { } function) {
                        EndAll(borrows);
                        return null;
                    }

                    arguments.Add(function);
                    continue;
                }

                // An optional function of them, likewise.
                if (genericShape is not null && i < callee.Parameters.Length && type.Parameters[arguments.Count] is EnumType { Cases: [("Some", ThickFunctionType), ("None", null)] } optionalFunction
                    && SilType.MentionsOpaque(optionalFunction) && !type.IsOwned(arguments.Count)) {
                    if (Expression(call.Arguments[i]) is not { } given) {
                        EndAll(borrows);
                        return null;
                    }

                    var converted = Hold(AsFunction(Owned(given), optionalFunction));
                    arguments.Add(converted);
                    borrows.Add(converted);
                    continue;
                }

                // An array of them, or a generic struct holding them by reference: the same bits, seen as the callee's.
                if (genericShape is not null && i < callee.Parameters.Length && callee.Parameters[i] is { IsByReference: false, Modifier: ParameterModifier.None or ParameterModifier.In }
                    && !type.IsOwned(arguments.Count) && type.Parameters[arguments.Count] is var arrayParameter && SilType.MentionsOpaque(arrayParameter) && arrayParameter is not AddressType) {
                    if (Borrow(call.Arguments[i]) is not { } array) {
                        EndAll(borrows);
                        return null;
                    }

                    borrows.Add(array);
                    arguments.Add(array.Type == arrayParameter ? array : Emit(new BuiltinInst("opaque_borrow_as", arrayParameter, array)));
                    continue;
                }

                // A 'T&' argument of inline bytes is a view of them.
                if (i < callee.Parameters.Length && callee.Parameters[i].IsByReference
                    && arguments.Count < type.Parameters.Length && type.Parameters[arguments.Count] is InlinePointerType) {
                    var viewed = call.Arguments[i] is BoundConversion { ConversionKind: ConversionKind.Reference } shared
                        ? shared.Operand
                        : call.Arguments[i];
                    if (View(viewed, borrows) is not { } view) {
                        return null;
                    }

                    arguments.Add(view);
                    continue;
                }

                // A 'T&' argument passes where it is, or a temporary when it
                // is nowhere: the binder made it a reference.
                if (i < callee.Parameters.Length && callee.Parameters[i].IsByReference
                    && arguments.Count < type.Parameters.Length && type.Parameters[arguments.Count] is AddressType) {
                    if (Expression(call.Arguments[i]) is not { } referred) {
                        return null;
                    }

                    arguments.Add(referred);
                    if (referred.Type is AddressType) {
                        BeginAccess(referred, modify: false, borrows);
                    }

                    continue;
                }

                // An inout or out argument passes where it is; the rest are borrowed.
                if (i < callee.Parameters.Length && callee.Parameters[i].Modifier is ParameterModifier.InOut or ParameterModifier.Out) {
                    if (InoutArgument(call.Arguments[i], borrows) is not { } address) {
                        return null;
                    }

                    arguments.Add(address);

                    // To unspecialised code, a type parameter's: the same storage, seen as the callee's.
                    if (genericShape is not null && type.Parameters[arguments.Count - 1] is var changed && changed != address.Type && SilType.MentionsOpaque(changed)) {
                        arguments[^1] = Emit(new BuiltinInst("opaque_cast", changed, address));
                    }

                    continue;
                }

                // An owned parameter is handed a value of its own, held until the call.
                if (type.IsOwned(arguments.Count)) {
                    if (Expression(call.Arguments[i]) is not { } handed) {
                        return null;
                    }

                    arguments.Add(Hold(Owned(handed)));
                    owned.Add(arguments[^1]);
                    continue;
                }

                if (Borrow(call.Arguments[i]) is not { } value) {
                    return null;
                }

                arguments.Add(value);
                borrows.Add(value);
            }

            foreach (var handed in owned) {
                Unhold(handed);
            }

            if (variadicAt >= 0) {
                // To unspecialised code, gathered as the type they are here, and seen as the callee's.
                var taken = type.Parameters[arguments.Count];
                var gatheredAs = genericShape is not null && SilType.MentionsOpaque(taken) ? LowerType(Substitute(WithAssociatedTypes(callee, bound), callee.Parameters[^1].Type)) : taken;
                if (gatheredAs is null || Gather(gatheredAs, call.Arguments.Skip(variadicAt)) is not { } gathered) {
                    return Missing(call, "the variadic arguments of this call");
                }

                if (gatheredAs != taken) {
                    Hold(gathered);
                    borrows.Add(gathered);
                    arguments.Add(Emit(new BuiltinInst("opaque_borrow_as", taken, gathered)));
                } else if (type.IsOwned(arguments.Count)) {
                    arguments.Add(gathered);
                } else {
                    arguments.Add(Hold(gathered));
                    borrows.Add(gathered);
                }
            }

            // Unspecialised code's metadata and witness tables, after the rest,
            // and before them where a result of a type parameter's type goes.
            (SilValue Passed, AllocStackInst Slot)? genericOut = null;
            (SilValue Passed, AllocStackInst? Slot, bool CannotThrow)? genericError = null;
            SilValue? openedBuffer = null;
            if (genericShape is not null) {
                if (type.Result == SilType.Unit && arguments.Count < type.Parameters.Length && ResultOf(callee) is { } calleeResult && !calleeResult.Equals(Core.Void)) {
                    var returned = Substitute(WithAssociatedTypes(callee, bound), calleeResult);
                    if (type.Parameters[arguments.Count] is AddressType && OpenedBufferMetadata(returned) is { } resultMetadata) {
                        // Of a type a Type was opened as: a buffer its metadata sizes, let go if the call throws.
                        var buffer = Emit(new BuiltinInst("opaque_alloc", type.Parameters[arguments.Count], resultMetadata));
                        openedBuffer = buffer;
                        arguments.Add(buffer);
                        borrows.Add(new OnError(() => Emit(new BuiltinInst("opaque_free", SilType.Unit, buffer))));
                    } else if ((genericOut = GenericOut(type.Parameters[arguments.Count], returned)) is { } into) {
                        arguments.Add(into.Passed);
                    }
                }

                // An error of a type parameter's type or an associated type's: where it goes.
                if (type.Thrown is AddressType errorAddress && arguments.Count < type.Parameters.Length && type.Parameters[arguments.Count] == errorAddress) {
                    if (callee.ThrownType is not { } calleeThrown || GenericErrorOut(errorAddress, Substitute(WithAssociatedTypes(callee, bound), calleeThrown)) is not { } place) {
                        EndAll(borrows);
                        return Missing(call, "where this call's error goes");
                    }

                    genericError = place;
                    arguments.Add(place.Passed);
                }

                if (GenericHiddenArguments(genericShape, bound, call.Syntax, callee) is not { } hidden) {
                    EndAll(borrows);
                    return null;
                }

                arguments.AddRange(hidden);
            }

            // A generic local function that captures: its context, last.
            if (IsLocalFunction(callee) && !callee.TypeParameters.IsEmpty && generator.Captures(callee)) {
                if (LocalFunctionValue(callee, call) is not { } context) {
                    return null;
                }

                Hold(context);
                arguments.Add(context);
                borrows.Add(context);
            }

            // A method a subclass may override is found through the vtable;
            // 'super.m()' calls the superclass's own.
            SilValue code = HasSelf(callee) && call.Receiver is not BoundSuperAccess && generator.IsDynamic(callee)
                ? Emit(new ClassMethodInst(arguments[0], VTableSlot(callee), type))
                : Emit(new FunctionRefInst(name, type));

            // Into an actor, or onto the main actor, from outside it: a hop
            // there and back (SilGen.Actors.cs).
            var (hops, main) = Crossing(callee);
            var back = hops && (main || arguments is [{ Type: ClassType }, ..]) ? HopInto(main ? null : arguments[0], arguments) : null;

            // A 'rethrows' callee given functions that throw nothing throws
            // nothing: the binder asked for no 'try', and nothing handles it.
            // Nor does a 'rethrows(E)' one whose E is Never here.
            var invoked = Invoke(code, type, arguments, borrows, call.Syntax,
                cannotThrow: genericError is { CannotThrow: true } || callee.IsRethrows && RethrownBy(call) is null
                             && (callee.RethrownAlso is not { } also || ThrowsNothing(Sub(call.Substitution.Apply(also)))),
                hopBack: back, errorSlot: genericError?.Slot);
            if (genericOut is { } written) {
                return ShareWritten(TakeOut(invoked, written.Slot), back);
            }

            if (openedBuffer is not null) {
                return invoked is null || _current is null ? invoked : openedBuffer;
            }

            // A function of them it gives back, as a function of the types it has here.
            if (genericShape is not null && invoked is { Type: ThickFunctionType or EnumType { Cases: [("Some", ThickFunctionType), ("None", null)] } } function1 && LowerType(call.Type) is { } here && here != function1.Type && _current is not null) {
                return AsFunction(function1, here);
            }

            // A result of an array of them, or of a generic struct holding them by reference, seen as the type it is here.
            if (genericShape is not null && invoked is { Type: var resultType } arrayResult && !(call.Type is ArrayTypeSymbol { ElementType: var listed } && IsOpenedHere(listed) && resultType is ArrayType) && SilType.MentionsOpaque(resultType) && !SilType.IsIndirect(resultType)
                && LowerType(call.Type) is { } wanted && wanted != arrayResult.Type) {
                return Emit(new BuiltinInst("opaque_take_as", wanted, arrayResult));
            }

            return invoked;
        }

        /// <summary>
        ///     Calls a function or a closure with borrowed arguments: <c>apply</c>,
        ///     or <c>try_apply</c> with the borrows ended on both of its edges.
        /// </summary>
        /// <param name="hopBack">After a call into an actor: where the task ran before, to hop back to on either way out.</param>
        SilValue? Invoke(SilValue reference, FunctionType type, List<SilValue> arguments, List<SilValue> borrows, SyntaxNode at, bool cannotThrow = false,
            SilValue? hopBack = null, AllocStackInst? errorSlot = null) {
            BeginAccesses(borrows);
            if (type.Thrown is null) {
                var apply = Emit(new ApplyInst(reference, type.Result, [..arguments]));
                if (hopBack is not null) {
                    HopBack(hopBack, type.Result == SilType.Unit ? null : apply);
                }

                EndAll(borrows);
                return type.Result == SilType.Unit ? UnitValue.Instance : apply;
            }

            // A throwing call: on to the normal block with the result, or the
            // error to whatever handles it here. Its borrows end on both.
            var normal = function.AddBlock();
            var result = type.Result == SilType.Unit ? null : normal.AddArgument(type.Result);
            var errorBlock = function.AddBlock();
            var error = errorBlock.AddArgument(type.Thrown);
            for (var i = borrows.Count - 1; i >= 0; i--) {
                Unhold(borrows[i]);
            }

            Jump(new TryApplyInst(reference, type.Result, [..arguments], normal, errorBlock));

            // Already off the temporaries: a borrow of a place is ended, an
            // access closed, a temporary destroyed.
            void Release(SilValue borrow, bool failed = false) {
                switch (borrow) {
                    case AccessMarker marker:
                        Emit(new BuiltinInst("access_end", SilType.Unit, marker.Address));
                        break;
                    // A property changed in place is set again whichever way
                    // the call leaves.
                    case WriteBack writeBack:
                        writeBack.Run();
                        break;
                    case DeferredStep:
                        break;
                    // Only on the error's path.
                    case OnError step:
                        if (failed) {
                            step.Run();
                        }

                        break;
                    case LoadBorrowInst borrowed:
                        Emit(new EndBorrowInst(borrowed));
                        ReleaseOuter(borrowed);
                        break;
                    default:
                        Destroy(borrow);
                        break;
                }
            }

            _current = errorBlock;
            if (hopBack is not null) {
                HopBack(hopBack, error);
            }

            for (var i = borrows.Count - 1; i >= 0; i--) {
                Release(borrows[i], failed: true);
            }

            if (cannotThrow) {
                // An error written where it was told — none, here — is no value to destroy.
                if (error.Type is not AddressType) {
                    Destroy(error);
                }

                Jump(new UnreachableInst());
            } else if (errorSlot is not null) {
                // An error written where it was told, of a type known here: taken from there.
                RouteError(ShareWritten(Emit(new LoadInst(errorSlot, errorSlot.ElementType.IsTrivial ? LoadKind.Trivial : LoadKind.Take)), hopBack)!, at);
            } else {
                RouteError(error, at);
            }

            _current = normal;
            if (hopBack is not null) {
                HopBack(hopBack, result);
            }

            for (var i = borrows.Count - 1; i >= 0; i--) {
                Release(borrows[i]);
            }

            return result ?? (SilValue)UnitValue.Instance;
        }

        /// <summary>
        ///     What the callee's type parameters stand for at a call, in this
        ///     instance: what the binder inferred, seen through this instance's
        ///     own substitution; for a member, what the receiver's type says;
        ///     failing both, what they stand for here — a call from inside the
        ///     same generic type.
        /// </summary>
        static bool IsNoneLiteral(BoundExpression expression) =>
            expression switch {
                BoundLiteral { Value: null } => true,
                BoundConversion conversion => IsNoneLiteral(conversion.Operand),
                _ => false,
            };

        /// <summary>An object as a class it is of, or inherits from — seen as the one code compiled once for its arguments takes.</summary>
        SilValue AsClass(SilValue value, ClassType from, ClassType to) {
            if (SilType.SameErasingOpaque(from, to, opaqueOnly: true)) {
                return Emit(new BuiltinInst("opaque_borrow_as", to, value));
            }

            for (var at = from.Superclass; at is not null; at = at.Superclass) {
                if (at != to && SilType.SameErasingOpaque(at, to, opaqueOnly: true)) {
                    return Emit(new BuiltinInst("opaque_borrow_as", to, Emit(new UpcastInst(value, at))));
                }
            }

            return Emit(new UpcastInst(value, to));
        }

        TypeSubstitution CallSubstitution(BoundCall call, FunctionSymbol callee) {
            // An array extension's member: what its element is, from the array's.
            // In a protocol's default implementation, 'first()' is called on
            // self: the type Self stands for, whose own type arguments
            // a generic conformer's member needs.
            // 'C.from(…)', static, is called on the type the call says Self
            // is. A protocol's member called from an extension of a protocol
            // refining it is called on this function's own Self.
            var receiverOf = call.Receiver is { } r ? Sub(r.Type)
                : OwnerOf(call.Function)?.OriginalDefinition is { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } implicitSelf }
                    ? (call.Substitution.Lookup(implicitSelf) is { } statedSelf ? Sub(statedSelf) : null)
                      ?? substitution.Lookup(implicitSelf)
                      ?? (OwnerOf(symbol)?.OriginalDefinition is { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } ownSelf } ? substitution.Lookup(ownSelf) : null)
                    : null;
            var receiver = receiverOf is null ? TypeSubstitution.Empty
                : OwnerOf(callee) is { ExtendedArray: not null } carrier ? Compilation.MatchArray(carrier, receiverOf) ?? TypeSubstitution.Empty
                : OwnerOf(callee) is { ExtendedOptional: not null } && receiverOf is OptionalTypeSymbol optional ? TypeSubstitution.For(Core.Optional.Construct([optional.UnderlyingType]))
                : TypeSubstitution.For(receiverOf is NamedTypeSymbol named && ConstructedOwner(named, callee) is { } owner ? owner : receiverOf);
            // A local function sees the type parameters around it as this
            // instance has them.
            var map = IsLocalFunction(callee) ? substitution.Parameters.ToDictionary(p => p, p => substitution.Lookup(p)!) : new Dictionary<TypeParameterSymbol, TypeSymbol>();

            // A protocol's default implementation: Self is the receiver's type.
            if (OwnerOf(callee)?.OriginalDefinition is { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } self }) {
                var receiverType = call.Receiver is { } selfReceiver ? Sub(selfReceiver.Type) : substitution.Lookup(self) ?? receiverOf;
                if (receiverType is not null) {
                    map[self] = receiverType;
                }
            }

            // What this instance's own parameters stand for is the fallback
            // only for one the call says nothing of: a protocol's default
            // called on an F64 from an instance where Self is an F32 is the
            // F64's, which the receiver said above.
            foreach (var parameter in TypeParametersOf(callee)) {
                var bound = call.Substitution.Lookup(parameter) is { } inferred
                    ? Sub(inferred)
                    : receiver.Lookup(parameter) ?? (map.ContainsKey(parameter) ? null : substitution.Lookup(parameter));
                if (bound is not null) {
                    map[parameter] = bound;
                }
            }

            // A generic requirement's witness has its own parameters, standing
            // for what the call gave the requirement's, in order.
            if (!ReferenceEquals(callee, call.Function) && callee.TypeParameters.Length == call.Function.TypeParameters.Length) {
                for (var i = 0; i < callee.TypeParameters.Length; i++) {
                    if (!map.ContainsKey(callee.TypeParameters[i]) && call.Substitution.Lookup(call.Function.TypeParameters[i]) is { } given) {
                        map[callee.TypeParameters[i]] = Sub(given);
                    }
                }
            }

            // A 'some P' parameter stands for the type of what is passed to it.
            foreach (var opaque in OpaqueParametersOf(callee)) {
                var standsFor = opaque.OpaqueParameter;
                foreach (var (parameter, argument) in callee.Parameters.Zip(call.Arguments)) {
                    if (!parameter.Type.Equals(standsFor)) {
                        continue;
                    }

                    var passed = Sub(argument is BoundConversion { ConversionKind: ConversionKind.Existential, Type: OpaqueTypeSymbol } opened
                        ? opened.Operand.Type
                        : argument.Type);

                    // Two arguments of different types for one 'some P' have no one instance.
                    map[opaque] = map.TryGetValue(opaque, out var earlier) && !earlier.Equals(passed) ? ErrorTypeSymbol.Instance : passed;
                }
            }

            return new(map);
        }

        /// <summary>The member of the receiver's concrete type that implements a protocol requirement.</summary>
        FunctionSymbol? Witness(BoundCall call) {
            // 'T.decode(…)': a static requirement, on the type 'T' stands for.
            var self = call.Receiver switch {
                BoundTypeExpression typeExpression => Sub(typeExpression.ReferencedType),
                { } receiver => Sub(receiver.Type),
                null => OwnerOf(call.Function)?.OriginalDefinition is { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } selfParameter }
                    ? CallSubstitution(call, call.Function).Lookup(selfParameter)
                    : null,
            };
            return self switch {
                NamedTypeSymbol concrete => ImplementationByArguments(concrete, call) ?? Implementation(concrete, call.Function),
                ArrayTypeSymbol array => generator.ArrayImplementation(array, call.Function),
                OptionalTypeSymbol optional => Implementation(Nominal(optional)!, call.Function),
                _ => null,
            };
        }

        /// <summary>
        ///     Of a type that conforms to several instances of one generic
        ///     protocol — <c>Provides&lt;Int&gt;</c> and <c>Provides&lt;String&gt;</c>
        ///     — the member that takes what the call gives; null for any other.
        /// </summary>
        FunctionSymbol? ImplementationByArguments(NamedTypeSymbol concrete, BoundCall call) {
            if (call.Arguments.IsEmpty || OwnerOf(call.Function)?.OriginalDefinition is not { TypeKind: TypeKind.Protocol, TypeParameters.IsEmpty: false } protocol
                || IsExtensionOnly(call.Function)
                || concrete.SupertypesOf(protocol).Take(2).Count() < 2) {
                return null;
            }

            var own = TypeSubstitution.For(concrete);
            var given = call.Arguments.Select(a => Sub(a.Type)).ToList();
            return concrete.OriginalDefinition.Members.OfType<FunctionSymbol>().FirstOrDefault(f =>
                f.Name == call.Function.Name
                && f.IsStatic == call.Function.IsStatic
                && f.TypeParameters.IsEmpty
                && SameLabels(f, call.Function)
                && f.Parameters.Zip(given).All(pair => own.Apply(pair.First.Type).Equals(pair.Second)));
        }

        /// <summary>
        ///     A function value as a function type that throws where it did
        ///     not, or throws another error: the same function, when the two
        ///     lower alike; otherwise a thunk over it, which converts errors.
        /// </summary>
        SilValue? FunctionConversion(BoundConversion conversion) {
            if (LowerType(conversion.Type) is not ThickFunctionType target || LowerType(conversion.Operand.Type) is not ThickFunctionType source) {
                return Missing(conversion, $"the conversion to '{conversion.Type.ToDisplayString()}'");
            }

            if (source == target) {
                return Expression(conversion.Operand);
            }

            if (generator.ConversionThunk(source, target) is not { } thunk) {
                return Missing(conversion, $"the conversion to '{conversion.Type.ToDisplayString()}'");
            }

            if (Expression(conversion.Operand) is not { } value) {
                return null;
            }

            var box = generator.BoxType(source);
            var boxed = Emit(new AllocRefInst(box));
            Emit(new StoreInst(Owned(value), Emit(new RefElementAddrInst(boxed, 0)), StoreKind.Init));
            return Emit(new PartialApplyInst(Emit(new FunctionRefInst(thunk.Name, thunk.Type)), boxed, target));
        }

        /// <summary>
        ///     A value as <c>any P</c> (#299): boxed, with its type's metadata and
        ///     its witness table for each of the existential's protocols — or,
        ///     from another existential, that one's box and metadata, and the
        ///     tables of the protocols this one needs.
        /// </summary>
        SilValue? MakeExistential(BoundConversion conversion) {
            if (LowerType(conversion.Type) is not StructType layout) {
                return Missing(conversion, $"the conversion to '{conversion.Type.ToDisplayString()}'");
            }

            if (IsAnyError(conversion.Operand.Type)) {
                return ErrorAsExistential(conversion, layout);
            }

            if (IsOpaqueHere(conversion.Operand.Type)) {
                return OpaqueAsExistential(conversion);
            }

            if (Sub(conversion.Operand.Type) is ExistentialTypeSymbol) {
                return Reexistential(conversion, layout);
            }

            // A value of any type goes into an existential with no
            // requirements; one with some needs a type with witness tables.
            // An optional's witnesses are met as code that knows it meets them: by its Optional extensions' members.
            var concrete = Sub(conversion.Operand.Type);
            if (concrete is not (NamedTypeSymbol or ArrayTypeSymbol or OptionalTypeSymbol) && generator.LayoutProtocols(layout).Count > 0) {
                return Missing(conversion, $"the conversion to '{conversion.Type.ToDisplayString()}'");
            }

            if (Expression(conversion.Operand) is not { } value) {
                return null;
            }

            var box = generator.ExistentialBox(value.Type);
            var boxed = Emit(new AllocRefInst(box));
            Emit(new StoreInst(Owned(value), Emit(new RefElementAddrInst(boxed, 0)), StoreKind.Assign));
            var held = Emit(new CopyValueInst(Emit(new UpcastInst(boxed, generator.ExistentialBoxRoot()))));
            Emit(new DestroyValueInst(boxed));
            return Existential(layout, concrete, value.Type, held, conversion);
        }

        /// <summary>
        ///     <c>any P</c> for a box holding a value of a type that conforms:
        ///     the box, the type's metadata and its witness tables, constants
        ///     here, where the type is known. Takes <paramref name="held" />.
        /// </summary>
        SilValue? Existential(StructType layout, TypeSymbol concrete, SilType value, SilValue held, BoundNode at) {
            var metadata = Emit(new MetadataInst(value));
            var tables = new List<SilValue>();
            foreach (var protocol in generator.LayoutProtocols(layout)) {
                var table = generator.WitnessTable(concrete, value, protocol, at.Syntax);
                var missing = table is null ? protocol.Name : generator.MissingWitness(table, concrete.SupertypeOf(protocol) ?? protocol);
                if (missing is not null) {
                    Emit(new DestroyValueInst(held));
                    return Missing(at, $"'{missing}' of '{concrete.ToDisplayString()}' through an existential");
                }

                tables.Add(Emit(new BuiltinInst($"witness_table_known:{QualifiedName(protocol)}", SilType.WitnessTable, metadata)));
            }

            // The same tables are what a cast finds in the registry.
            generator.Boxed(concrete, value, layout, at.Syntax);
            return Emit(new StructInst(layout, [held, metadata, ..tables]));
        }

        /// <summary>
        ///     An existential as one of fewer protocols — <c>any A &amp; B</c>
        ///     as <c>any A</c>, <c>any Sub</c> as <c>any Base</c>: the same box
        ///     and metadata, and for each protocol the table the other holds
        ///     for it, or for a protocol that refines it, through that one's bases.
        /// </summary>
        SilValue? Reexistential(BoundConversion conversion, StructType layout) {
            if (LowerType(conversion.Operand.Type) is not StructType from || !generator.IsExistentialLayout(from)) {
                return Missing(conversion, $"the conversion to '{conversion.Type.ToDisplayString()}'");
            }

            var paths = new List<(int Part, ImmutableArray<int> Path)>();
            foreach (var wanted in generator.LayoutProtocols(layout)) {
                if (generator.TablePath(generator.LayoutProtocols(from), wanted) is not { } path) {
                    return Missing(conversion, $"the conversion from '{conversion.Operand.Type.ToDisplayString()}' to '{conversion.Type.ToDisplayString()}'");
                }

                paths.Add(path);
            }

            if (Expression(conversion.Operand) is not { } source) {
                return null;
            }

            Hold(source);
            var fields = new List<SilValue> {
                Emit(new CopyValueInst(Emit(new StructExtractInst(source, 0)))),
                Emit(new StructExtractInst(source, 1)),
            };
            foreach (var (part, path) in paths) {
                SilValue table = Emit(new StructExtractInst(source, SilGen.ExistentialTables + part));
                foreach (var step in path) {
                    table = Emit(new BuiltinInst($"witness_base:{step}", SilType.WitnessTable, table));
                }

                fields.Add(table);
            }

            Destroy(Unhold(source));
            return Emit(new StructInst(layout, [..fields]));
        }

        /// <summary>
        ///     <c>any Error</c> as <c>any Debug</c>, or <c>any</c>: the object
        ///     boxed as a value is, with the tables of <c>any Error</c>'s own
        ///     conformances, whose entries call its vtable's slots, where its
        ///     box put the error type's own.
        /// </summary>
        SilValue? ErrorAsExistential(BoundConversion conversion, StructType layout) {
            var anyError = generator.AnyErrorType();
            foreach (var protocol in generator.LayoutProtocols(layout)) {
                if (generator.ErrorTable(anyError, protocol) is null) {
                    return Missing(conversion, $"'any Error' through '{conversion.Type.ToDisplayString()}'");
                }
            }

            if (Expression(conversion.Operand) is not { } error) {
                return null;
            }

            var box = generator.ExistentialBox(anyError);
            var boxed = Emit(new AllocRefInst(box));
            Emit(new StoreInst(Owned(error), Emit(new RefElementAddrInst(boxed, 0)), StoreKind.Init));
            var held = Emit(new CopyValueInst(Emit(new UpcastInst(boxed, generator.ExistentialBoxRoot()))));
            Emit(new DestroyValueInst(boxed));
            var metadata = Emit(new MetadataInst(anyError));
            var tables = generator.LayoutProtocols(layout)
                .Select(p => (SilValue)Emit(new BuiltinInst($"witness_table_known:{QualifiedName(p)}", SilType.WitnessTable, metadata)))
                .ToList();
            return Emit(new StructInst(layout, [held, metadata, ..tables]));
        }

        /// <summary><c>any Error</c>, which is an object of its own, not an existential's box and slots.</summary>
        bool IsAnyError(TypeSymbol type) =>
            Sub(type) is ExistentialTypeSymbol { Constraint: var constraint } && constraint.Equals(Core.Error);

        /// <summary>
        ///     A requirement of Error's called on <c>any Error</c>: the slot of
        ///     the object's vtable its box filled, given the object and the
        ///     arguments.
        /// </summary>
        SilValue? ErrorCall(BoundExpression receiver, string slot, IReadOnlyList<BoundExpression> argumentExpressions, BoundNode at) {
            var anyError = generator.AnyErrorType();
            if (generator.ErrorSlots(anyError).FirstOrDefault(s => s.Slot == slot) is not { Type: { } type }) {
                return Missing(at, $"calling '{slot}' through 'any Error'");
            }

            if (Borrow(receiver) is not { } error) {
                return null;
            }

            var arguments = new List<SilValue> { error };
            var borrows = new List<SilValue> { error };
            foreach (var argument in argumentExpressions) {
                if (Borrow(argument) is not { } value) {
                    return null;
                }

                arguments.Add(value);
                borrows.Add(value);
            }

            var method = Emit(new ClassMethodInst(error, slot, type));
            return Invoke(method, type, arguments, borrows, at.Syntax);
        }

        /// <summary>
        ///     A requirement called on an existential (#299): its entry of the
        ///     existential's table, given the value where the box holds it, the
        ///     arguments, and the metadata. One that changes the value — a
        ///     mutating method, a property's setter — is given the value's
        ///     address, the box first made the existential's own.
        /// </summary>
        SilValue? ExistentialCall(BoundExpression receiver, string slot, IReadOnlyList<BoundExpression> argumentExpressions, BoundNode at) {
            var layout = LowerType(receiver.Type) as StructType;
            if (layout is null || generator.RequirementOf(layout, slot) is not { Type: { } type } requirement
                || type.Parameters.Length != argumentExpressions.Count + 2 || type.Thrown is AddressType
                || type.Parameters[0] is not (OpaqueType or AddressType { Pointee: OpaqueType })) {
                return Missing(at, $"calling '{slot}' through '{Sub(receiver.Type).ToDisplayString()}'");
            }

            var changes = type.Parameters[0] is AddressType;
            var arguments = new List<SilValue>();
            var borrows = new List<SilValue>();
            SilValue? existential = null;
            if (!changes) {
                if ((existential = Borrow(receiver)) is null) {
                    return null;
                }

                borrows.Add(existential);
            }

            for (var i = 0; i < argumentExpressions.Count; i++) {
                SilValue? argument;
                if (type.Parameters[i + 1] is AddressType) {
                    argument = Address(argumentExpressions[i], borrows);
                } else if ((argument = Borrow(argumentExpressions[i])) is not null) {
                    borrows.Add(argument);
                }

                if (argument is null) {
                    EndAll(borrows);
                    return null;
                }

                arguments.Add(argument);
            }

            SilValue self, metadata, table;
            if (existential is not null) {
                metadata = Emit(new StructExtractInst(existential, 1));
                table = Emit(new StructExtractInst(existential, SilGen.ExistentialTables + requirement.Part));
                self = Emit(new BuiltinInst("box_project", type.Parameters[0], Emit(new StructExtractInst(existential, 0)), metadata));
            } else {
                if (Address(receiver, borrows) is not { } address) {
                    EndAll(borrows);
                    return null;
                }

                metadata = Emit(new LoadInst(Emit(new StructElementAddrInst(address, 1))));
                table = Emit(new LoadInst(Emit(new StructElementAddrInst(address, SilGen.ExistentialTables + requirement.Part))));
                BeginAccess(address, modify: true, borrows);
                self = Emit(new BuiltinInst("box_project_unique", type.Parameters[0], Emit(new StructElementAddrInst(address, 0)), metadata));
            }

            var code = Emit(new BuiltinInst($"witness_generic:{requirement.Entry}", type, SilGen.RequirementTable(Emit, table, requirement)));
            return Invoke(code, type, [self, ..arguments, metadata], borrows, at.Syntax);
        }

        /// <summary><c>self</c>, as a closure captured it: a copy out of the context.</summary>
        SilValue? CapturedSelf() =>
            closure?.Captures.TryGetValue(SelfKey, out var capture) == true
                ? Emit(new LoadInst(Emit(new RefElementAddrInst(_context!, capture.Field))))
                : null;

        /// <summary>
        ///     A closure: its code lowered as a function of its own, then its
        ///     context built — a box for each local captured by reference, a
        ///     copy of each value captured by value — and the two put together.
        /// </summary>
        SilValue? Closure(BoundClosure closureExpression) {
            if (generator._compilation.GetGenerator(closureExpression.Symbol) is { } yielding && ReferenceEquals(yielding.Function, closureExpression.Symbol)) {
                return GeneratorClosure(closureExpression, yielding);
            }

            if (LowerType(closureExpression.Type) is not ThickFunctionType thick) {
                return Missing(closureExpression, $"a closure of type '{closureExpression.Type.ToDisplayString()}'");
            }

            // In unspecialised code, one over a type parameter's values is compiled with it (#297).
            if (_genericMetadata.Count > 0 && generator._unspecialised.ContainsKey(function.Name) && MentionsOpaqueHere(closureExpression)) {
                return GenericClosure(closureExpression, thick, generator.CapturesOf(closureExpression), generator.ClosureName(function.Name));
            }

            return ClosureOf(closureExpression, closureExpression.Symbol, closureExpression.Body, thick,
                generator.CapturesOf(closureExpression), generator.ClosureName(function.Name));
        }

        /// <summary>
        ///     Code and a context: a closure's, or a local function's that
        ///     captures — the two differ only in how they are reached.
        /// </summary>
        SilValue? ClosureOf(BoundNode closureExpression, FunctionSymbol codeSymbol, BoundBlock body, ThickFunctionType thick, List<Capture> captures, string name) {
            ClassType? contextType = null;
            if (captures.Count > 0) {
                contextType = ContextType(closureExpression, captures, name);
                if (contextType is null) {
                    return null;
                }
            }

            // The code.
            var code = new SilFunction(name, thick.Implementation) {
                SourceFile = function.SourceFile,
                Line = source.GetLinePosition(closureExpression.Syntax.Span.Start).Line,
                // An '@autoclosure' argument is an expression, inlined
                // where the callee, inlined, asks for it (InlineClosures).
                Inline = closureExpression is BoundClosure { IsAutoclosure: true } ? SilInline.Always : SilInline.Default,
            };
            code.ParameterNames.AddRange(codeSymbol.Parameters.Select(static p => p.Name));
            var info = new ClosureInfo(contextType, captures.ToDictionary(static c => c.Key, static c => c));
            new FunctionLowering(generator, codeSymbol, code, source, substitution, info).Lower(body);
            code.RemoveUnreachableBlocks();
            generator._module.Add(code);

            // A closure's body is checked as a function's is: what it does with
            // a move-only value it captures, or makes, is held to the same rules.
            MoveChecker.Check(code, source, generator._diagnostics);

            // The context. An '@autoclosure''s is made by the call, and
            // stays at the call's place: the call that follows is there,
            // as a backtrace through it names it.
            if (closureExpression is not BoundClosure { IsAutoclosure: true }) {
                Locate(closureExpression.Syntax);
            }
            var reference = Emit(new FunctionRefInst(name, thick.Implementation));
            if (contextType is null) {
                return Emit(new PartialApplyInst(reference, null, thick));
            }

            return Context(closureExpression, contextType, captures) is { } context
                ? Emit(new PartialApplyInst(reference, Unhold(context), thick))
                : null;
        }

        /// <summary>The class of a closure's context: a field for each thing it captures.</summary>
        ClassType? ContextType(BoundNode closureExpression, List<Capture> captures, string name) {
            var contextType = generator._module.AddType(new ClassType($"{name}$context"));
            foreach (var capture in captures) {
                // A local function captured is its closure, typed as its own
                // calls see it; a generic one is its context alone.
                var fieldType = capture.Key is FunctionSymbol capturedFunction && IsLocalFunction(capturedFunction)
                    ? capturedFunction.TypeParameters.IsEmpty ? LocalFunctionType(capturedFunction) : generator.LocalGenericContext(capturedFunction)
                    : LowerType(capture.Type);
                if (fieldType is null) {
                    Missing(closureExpression, $"capturing a value of type '{capture.Type.ToDisplayString()}'");
                    return null;
                }

                // Each field is named for what it captured, so that a
                // debugger shows it by that name inside the closure.
                var fieldName = capture.Key switch {
                    Symbol { Name: { Length: > 0 } captured } => captured,
                    string key => key,
                    _ => $"c{capture.Field}",
                };
                if (contextType.FieldIndex(fieldName) >= 0) {
                    fieldName = $"{fieldName}${capture.Field}";
                }

                // A weak or unowned capture is held as a weak or unowned field is.
                SilType stored = (capture.Kind, fieldType) switch {
                    _ when capture.Boxed => generator.BoxType(fieldType),
                    (CaptureKind.Weak, EnumType optional) => new WeakStorageType(optional),
                    (CaptureKind.Unowned, ClassType referent) => new UnownedStorageType(referent),
                    _ => fieldType,
                };
                contextType.Fields.Add((fieldName, stored));
            }

            return contextType;
        }

        /// <summary>A closure's context, made and filled with what it captures here; held, for the caller to take.</summary>
        SilValue? Context(BoundNode closureExpression, ClassType contextType, List<Capture> captures) {
            var context = Hold(Emit(new AllocRefInst(contextType)));
            foreach (var capture in captures) {
                if (CapturedValue(capture, closureExpression) is not { } value) {
                    return null;
                }

                var field = Emit(new RefElementAddrInst(context, capture.Field));
                switch (((AddressType)field.Type).Pointee) {
                    // Given the object without keeping it: the value is let go.
                    case WeakStorageType:
                        Emit(new StoreWeakInst(value, field));
                        Destroy(value);
                        break;

                    case UnownedStorageType:
                        Emit(new StoreUnownedInst(value, field));
                        Destroy(value);
                        break;

                    default:
                        Emit(new StoreInst(Owned(value), field, StoreKind.Assign));
                        break;
                }
            }

            return context;
        }

        /// <summary>What goes into a closure's context for one capture: a box, or a value.</summary>
        SilValue? CapturedValue(Capture capture, BoundNode at) {
            switch (capture.Key) {
                case LocalSymbol local when capture.Boxed:
                    // The box itself: from this function's slot, or from its own context.
                    if (closure is not null && closure.Captures.TryGetValue(local, out var outer) && outer.Boxed) {
                        return Emit(new LoadInst(Emit(new RefElementAddrInst(_context!, outer.Field))));
                    }

                    if (!_boxed.Contains(local) || SlotFor(local, at.Syntax) is not { } slot) {
                        return Missing(at, $"capturing '{local.Name}'");
                    }

                    return Emit(new LoadInst(slot));

                case LocalSymbol when capture.Initializer is { } initializer:
                    return Expression(initializer);

                // An inout parameter's box, shared with the frame.
                case ParameterSymbol parameter when _parameterBoxes.TryGetValue(parameter, out var boxed):
                    return Emit(new CopyValueInst(boxed.Box));

                case ParameterSymbol parameter when _parameters.TryGetValue(parameter, out var value):
                    return Owned(value);

                case ParameterSymbol parameter when closure?.Captures.TryGetValue(parameter, out var outerParameter) == true:
                    return Emit(new LoadInst(Emit(new RefElementAddrInst(_context!, outerParameter.Field))));

                // One this closure unpacked from its own context into a slot (UnpackIndirectCaptures).
                case ParameterSymbol parameter when _spilled.TryGetValue(parameter, out var unpacked):
                    return Emit(new LoadInst(unpacked));

                case FunctionSymbol local:
                    return LocalFunctionValue(local, at);

                case SelfKey when capture.Boxed:
                    return _selfBox is { } selfBox ? Emit(new CopyValueInst(selfBox)) : Missing(at, "capturing 'self' to change");

                case SelfKey:
                    return _selfValue is { } self ? Owned(self)
                        : _selfAddress is { } selfSlot ? Emit(new LoadInst(selfSlot))
                        : CapturedSelf() ?? Missing(at, "capturing 'self'");

                default:
                    return Missing(at, "this capture");
            }
        }

        /// <summary>
        ///     A call to a variadic C function: the fixed arguments as any C
        ///     call passes them, then each of the rest as the value it is —
        ///     not the CVarArg the binder saw it as — through a declaration of
        ///     the function for exactly these types, which the emitter calls
        ///     with C's '...' (and its default promotions).
        /// </summary>
        SilValue? VariadicForeignCall(BoundCall call, FunctionSymbol callee, string cName) {
            var fixedCount = callee.Parameters.Length - 1;
            var arguments = new List<SilValue>();
            for (var i = 0; i < call.Arguments.Length; i++) {
                var argument = call.Arguments[i];
                while (i >= fixedCount && argument is BoundConversion { Type: NamedTypeSymbol { TypeKind: TypeKind.Protocol } } toProtocol) {
                    argument = toProtocol.Operand;
                }

                if (Borrow(argument) is not { } value) {
                    arguments.ForEach(End);
                    return null;
                }

                arguments.Add(value);
            }

            var result = ReferenceEquals(callee.ReturnType, Core.Void) ? SilType.Unit : LowerType(callee.ReturnType);
            if (result is null) {
                arguments.ForEach(End);
                return Missing(call, $"the result of '{callee.Name}'");
            }

            var type = new FunctionType([..arguments.Select(static a => a.Type)], result);
            var name = $"{generator._names[callee]}$va${string.Join("$", type.Parameters.Skip(fixedCount).Select(static t => Sanitise(t.ToString())))}";
            generator._module.Foreign[name] = (cName, type);
            generator._module.ForeignVariadic[name] = fixedCount;
            Locate(call.Syntax);
            var made = Emit(new ApplyInst(Emit(new FunctionRefInst(name, type)), result, [..arguments]));
            arguments.ForEach(End);
            return result == SilType.Unit ? UnitValue.Instance : made;
        }

        /// <summary>A named function as a value: a closure over a thunk, with no context.</summary>
        SilValue? FunctionValue(BoundFunctionReference reference) {
            var callee = reference.Function;

            // A local function that captures already is a closure; a generic
            // one is not, and is not a value until its type arguments are.
            if (IsLocalFunction(callee) && generator.Captures(callee) && callee.TypeParameters.IsEmpty) {
                return LocalFunctionValue(callee, reference);
            }

            if (reference.Receiver is { } receiver && HasSelf(callee)) {
                return BoundMethodValue(reference, receiver);
            }

            if (reference.Receiver is not null || LowerType(reference.Type) is not ThickFunctionType thick) {
                return Missing(reference, $"'{callee.Name}' as a value");
            }

            if (ReferencedFunction(reference, callee) is not var (name, type)) {
                return null;
            }

            if (type != thick.Signature || !type.Owned.IsEmpty) {
                return Missing(reference, $"'{callee.Name}' as a value");
            }

            var thunk = generator.Thunk(name, type);
            return Emit(new PartialApplyInst(Emit(new FunctionRefInst(thunk, thick.Implementation)), null, thick));
        }

        /// <summary>
        ///     The function a reference names, and its signature: for a generic
        ///     one, the instance for the type arguments the reference settled
        ///     and its receiver's type gives. Null, with the error reported,
        ///     when there is none.
        /// </summary>
        (string Name, FunctionType Type)? ReferencedFunction(BoundFunctionReference reference, FunctionSymbol callee) {
            if (IsGeneric(callee)) {
                var receiver = reference.Receiver is { } r ? TypeSubstitution.For(Sub(r.Type)) : TypeSubstitution.Empty;
                var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
                foreach (var parameter in TypeParametersOf(callee)) {
                    var bound = reference.Substitution.Lookup(parameter) is { } settled
                        ? Sub(settled)
                        : receiver.Lookup(parameter) ?? substitution.Lookup(parameter);
                    if (bound is not null) {
                        map[parameter] = bound;
                    }
                }

                if (generator.Instance(callee, new TypeSubstitution(map), reference.Syntax) is not { } instance) {
                    _failed = true;
                    return null;
                }

                return instance;
            }

            if (!generator._names.TryGetValue(callee, out var name)) {
                Missing(reference, $"'{callee.Name}' as a value");
                return null;
            }

            if ((generator._module.Find(name)?.Type ?? generator.SignatureOf(callee, reference.Syntax, TypeSubstitution.Empty)) is not { } type) {
                _failed = true;
                return null;
            }

            return (name, type);
        }

        /// <summary>
        ///     <c>s.get</c> as a value: the method with its receiver, evaluated
        ///     here, in a box the closure carries — a copy of a value, or the
        ///     reference to an object, as <c>s.get()</c> would have used.
        /// </summary>
        SilValue? BoundMethodValue(BoundFunctionReference reference, BoundExpression receiver) {
            var callee = reference.Function;
            if (LowerType(reference.Type) is not ThickFunctionType thick) {
                return Missing(reference, $"'{callee.Name}' as a value");
            }

            // A protocol's requirement, named on an existential: its witness
            // is only known at run time, from the existential's witness
            // table. The closure's context holds a copy of the existential —
            // its box, metadata and tables — and calls the requirement's
            // entry with the value, as a call through the existential would.
            if (OwnerOf(callee) is { TypeKind: TypeKind.Protocol } && Sub(receiver.Type) is ExistentialTypeSymbol) {
                var layout = LowerType(receiver.Type) as StructType;
                var key = SlotName(callee);
                if (layout is null || generator.RequirementOf(layout, key) is not { Type: { } entryType } requirement
                    || entryType.Parameters[0] is not OpaqueType
                    || new FunctionType([..entryType.Parameters.Skip(1).SkipLast(1)], entryType.Result, entryType.Thrown) != thick.Signature) {
                    return Missing(reference, $"the requirement '{callee.Name}' of '{Sub(receiver.Type).ToDisplayString()}' as a value");
                }

                if (Expression(receiver) is not { } existential) {
                    return null;
                }

                var bound = generator.ExistentialRequirementThunk(layout, key, requirement, thick);
                var holder = Emit(new AllocRefInst(generator.BoxType(layout)));
                Emit(new StoreInst(Owned(existential), Emit(new RefElementAddrInst(holder, 0)), StoreKind.Init));
                return Emit(new PartialApplyInst(Emit(new FunctionRefInst(bound, thick.Implementation)), holder, thick));
            }

            // A protocol's requirement, named on a type parameter: the
            // concrete type's own member, now that the type is known.
            if (OwnerOf(callee) is { TypeKind: TypeKind.Protocol }) {
                if (Sub(receiver.Type) is not NamedTypeSymbol { TypeKind: not TypeKind.Protocol } concrete
                    || Implementation(concrete, callee) is not { Builtin: null } witness) {
                    return Missing(reference, $"the requirement '{callee.Name}' of '{Sub(receiver.Type).ToDisplayString()}' as a value");
                }

                callee = witness;
            }

            if (ReferencedFunction(reference, callee) is not var (name, type)) {
                return null;
            }

            // A mutating method changes the value it is called on, which the
            // closure's copy is not; a consuming one takes what it is given.
            if (type.Parameters[0] is AddressType || !type.Owned.IsEmpty) {
                return Missing(reference, $"the {(type.Parameters[0] is AddressType ? "mutating" : "consuming")} method '{callee.Name}' as a value");
            }

            if (new FunctionType([..type.Parameters.Skip(1)], type.Result, type.Thrown) != thick.Signature) {
                return Missing(reference, $"'{callee.Name}' as a value of type '{reference.Type.ToDisplayString()}'");
            }

            var thunk = generator.BoundMethodThunk(callee, name, type, Sub(receiver.Type) is NamedTypeSymbol boundOwner && ConstructedOwner(boundOwner, callee) is { } declaredIn ? TypeSubstitution.For(declaredIn) : TypeSubstitution.Empty);
            if (Expression(receiver) is not { } self) {
                return null;
            }

            // A method a superclass declares takes the object as that class.
            if (self.Type is ClassType selfClass && type.Parameters[0] is ClassType parameterClass && selfClass != parameterClass) {
                self = SilType.SameErasingOpaque(selfClass, parameterClass, opaqueOnly: true) ? Emit(new BuiltinInst("opaque_borrow_as", parameterClass, self)) : Emit(new UpcastInst(self, parameterClass));
            }

            var boxed = Emit(new AllocRefInst(generator.BoxType(type.Parameters[0])));
            Emit(new StoreInst(Owned(self), Emit(new RefElementAddrInst(boxed, 0)), StoreKind.Init));
            return Emit(new PartialApplyInst(Emit(new FunctionRefInst(thunk.Name, thunk.Type)), boxed, thick));
        }

        /// <summary>A call through a closure: the closure and the arguments, all borrowed for the call.</summary>
        SilValue? IndirectCall(BoundIndirectCall call) {
            Locate(call.Syntax);
            if (Borrow(call.Callee) is not { Type: ThickFunctionType thick } callee) {
                return Missing(call, "this call through a value");
            }

            var arguments = new List<SilValue>();
            var borrows = new List<SilValue> { callee };
            var functionType = Sub(call.Callee.Type) as FunctionTypeSymbol;
            for (var i = 0; i < call.Arguments.Length; i++) {
                var argument = call.Arguments[i];

                // An inout argument passes where it is, as to a declared function.
                if (functionType?.IsInOut(i) == true) {
                    if (InoutArgument(argument, borrows) is not { } address) {
                        return null;
                    }

                    arguments.Add(address);
                    continue;
                }

                if (Borrow(argument) is not { } value) {
                    return null;
                }

                arguments.Add(value);
                borrows.Add(value);
            }

            // A function of a type parameter's values, in the generic
            // convention: its result written where this call says (#297).
            var writesError = thick.Signature is { Thrown: AddressType errorAddress, Parameters: [.., var last] } && last == errorAddress;
            (SilValue Passed, AllocStackInst Slot)? into = null;
            if (arguments.Count == thick.Signature.Parameters.Length - (writesError ? 2 : 1) && thick.Signature.Result == SilType.Unit
                && (into = GenericOut(thick.Signature.Parameters[arguments.Count], Sub(call.Type))) is { } result) {
                arguments.Add(result.Passed);
            }

            // And an error of one, where it goes.
            (SilValue Passed, AllocStackInst? Slot, bool CannotThrow)? error = null;
            if (writesError) {
                if (functionType?.ThrownType is not { } thrown || GenericErrorOut((AddressType)thick.Signature.Thrown!, thrown) is not { } place) {
                    EndAll(borrows);
                    return Missing(call, "where this call's error goes");
                }

                error = place;
                arguments.Add(place.Passed);
            }

            var invoked = Invoke(callee, thick.Signature, arguments, borrows, call.Syntax, cannotThrow: error is { CannotThrow: true }, errorSlot: error?.Slot);
            return into is { } written ? TakeOut(invoked, written.Slot) : invoked;
        }

        /// <summary>
        ///     <c>arena.alloc(value)</c>, Sable.Core's <c>@intrinsic</c>: room in
        ///     the region, the value moved there, its address back.
        /// </summary>
        SilValue? RegionAlloc(BoundCall call) {
            if (call is not { Receiver: { } receiver, Arguments: [var argument] }) {
                return Missing(call, "'alloc' without a region and a value");
            }

            if (Expression(argument) is not { } allocated) {
                return null;
            }

            Hold(allocated);
            if (Borrow(receiver) is not { } region) {
                return null;
            }

            var room = Emit(new BuiltinInst("region_alloc", SilType.Address(allocated.Type), region));
            Unhold(allocated);
            Emit(new StoreInst(Owned(allocated), room, StoreKind.Init));
            End(region);
            return room;
        }

        /// <summary>
        ///     <c>RawMemory.load(address)</c> and the rest, Sable.Core's
        ///     <c>@intrinsic</c>s under <c>RawPtr</c>: a value of the call's
        ///     type copied out of the memory at an address, moved out of it,
        ///     or moved into it — the builtins <c>raw_load</c>, <c>raw_take</c>
        ///     and <c>raw_init</c>, of that type.
        /// </summary>
        SilValue? RawMemoryIntrinsic(BoundCall call, string name) {
            // A value many tasks will reach, every object it holds made to be
            // counted atomically from now on, as one crossing to a task is.
            if (name == "share" && call.Arguments is [var shared]) {
                if (Borrow(shared) is not { } sharedValue) {
                    return null;
                }

                Emit(new BuiltinInst("share", SilType.Unit, sharedValue));
                End(sharedValue);
                return UnitValue.Instance;
            }

            // Where a value, an array's elements or a slice's are, and the end
            // of what is done there: the storage is the caller's, 'inout', or
            // kept alive, in between.
            if (name is "addressOf" or "addressEnd" or "arrayAddress" && call.Arguments is [var place]) {
                var borrows = new List<SilValue>();
                if (Address(Unconverted(place), borrows) is not { } at) {
                    EndAll(borrows);
                    return null;
                }

                var made = Emit(name switch {
                    "addressOf" => new BuiltinInst("address_begin", SilType.Int, at),
                    "arrayAddress" => new BuiltinInst("array_address", SilType.Int, at),
                    _ => new BuiltinInst("address_end", SilType.Unit, at),
                });
                EndAll(borrows);
                return name == "addressEnd" ? UnitValue.Instance : made;
            }

            if (name is "sliceAddress" or "sliceEnd" && call.Arguments is [var viewed]) {
                if (Borrow(viewed) is not { } slice) {
                    return null;
                }

                var at = Emit(new BuiltinInst(name == "sliceEnd" ? "slice_end" : "slice_address", name == "sliceEnd" ? SilType.Unit : SilType.Int, slice));
                End(slice);
                return name == "sliceEnd" ? UnitValue.Instance : at;
            }

            var builtin = name switch {
                "load" => "raw_load",
                "loadUnaligned" => "raw_load_unaligned",
                "take" => "raw_take",
                "initialize" => "raw_init",
                "initializeUnaligned" => "raw_init_unaligned",
                _ => null,
            };
            if (builtin is null || call.Arguments.Length != (builtin.StartsWith("raw_init", StringComparison.Ordinal) ? 2 : 1)) {
                return Missing(call, $"the intrinsic 'RawMemory.{name}'");
            }

            if (Expression(call.Arguments[0]) is not { } address) {
                return null;
            }

            if (call.Arguments.Length == 1) {
                return LowerType(call.Type) is { } type ? Emit(new BuiltinInst(builtin, type, address)) : Missing(call, $"the type '{call.Type.ToDisplayString()}' in raw memory");
            }

            if (Expression(call.Arguments[1]) is not { } value) {
                return null;
            }

            Emit(new BuiltinInst(builtin, SilType.Unit, address, Owned(value)));
            return UnitValue.Instance;
        }

        /// <summary>
        ///     <c>blackBox(value)</c>: the value, put in a slot the builtin
        ///     <c>black_box</c> is given the address of, and taken out again.
        ///     Natively that is an empty assembly statement that may read and
        ///     write any memory, so the optimiser must compute the value and
        ///     cannot know what comes back: a benchmark's work is not deleted
        ///     or folded away.
        /// </summary>
        SilValue? BlackBox(BoundCall call) {
            if (call.Arguments is not [var argument]) {
                return Missing(call, "'blackBox' without a value");
            }

            if (Expression(argument) is not { } value) {
                return null;
            }

            var owned = Owned(value);
            var slot = NewSlot(owned.Type, "$blackBox");
            Emit(new StoreInst(owned, slot, StoreKind.Init));
            Emit(new BuiltinInst(SilBuiltins.BlackBox, SilType.Unit, slot));
            return Emit(new LoadInst(slot, owned.Type.IsTrivial ? LoadKind.Trivial : LoadKind.Take));
        }

        /// <summary>A property read through an existential or a getter.</summary>
        SilValue? ComputedProperty(BoundFieldAccess access, BoundExpression receiver) {
            if (IsAnyError(receiver.Type)) {
                return ErrorCall(receiver, GetterSlot(access.Field), [], access);
            }

            if (Sub(receiver.Type) is ExistentialTypeSymbol) {
                return IsRequirementOf(receiver, GetterSlot(access.Field)) && !OpensForResult(access.Field, access.Field.Type, null)
                    ? ExistentialCall(receiver, GetterSlot(access.Field), [], access)
                    : OpenedExistential(receiver, access.Field, false, access,
                        (self, opened) => new BoundFieldAccess(access.Syntax, self, access.Field, OpenedMember(access.Field, access.Field.Type, opened, access.Type)), access.Type);
            }

            // A computed property of the program's own: a call to its getter.
            if (generator.GetterOf(access.Field) is { } getter) {
                return Expression(new BoundCall(access.Syntax, receiver, getter, [], access.Type));
            }

            // A protocol's property, read where Self is known: the type's own field.
            if (access.Field.ContainingSymbol is NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol && Sub(receiver.Type) is NamedTypeSymbol concrete) {
                for (var type = (NamedTypeSymbol?)concrete.OriginalDefinition; type is not null; type = SuperclassOf(type)?.OriginalDefinition) {
                    if (type.Members.OfType<FieldSymbol>().FirstOrDefault(f => f.Name == access.Field.Name && !f.IsStatic) is { } own) {
                        return own.Builtin is not null ? BuiltinProperty(access, own)
                            : own.IsComputed && generator.GetterOf(own) is { } ownGetter
                            ? Expression(new BoundCall(access.Syntax, receiver, ownGetter, [], access.Type))
                            : FieldAccess(access, receiver, own);
                    }
                }

                // None of its own: the default an extension of the protocol gives.
                if (protocol.OriginalDefinition.Members.OfType<FieldSymbol>().FirstOrDefault(f =>
                        f.ContainingSymbol is ExtensionSymbol && f.Name == access.Field.Name && !f.IsStatic) is { } fallback) {
                    return fallback.Builtin is not null ? BuiltinProperty(new BoundFieldAccess(access.Syntax, receiver, fallback, access.Type), fallback)
                        : generator.GetterOf(fallback) is { } fallbackGetter
                        ? Expression(new BoundCall(access.Syntax, receiver, fallbackGetter, [], access.Type))
                        : Missing(access, $"the property '{access.Field.Name}' of '{receiver.Type.ToDisplayString()}'");
                }
            }

            // A protocol's property, read where Self is an array: the one an
            // array extension declares, as 'count' is the slice's.
            if (access.Field.ContainingSymbol is NamedTypeSymbol { TypeKind: TypeKind.Protocol } && Sub(receiver.Type) is ArrayTypeSymbol array
                && generator.ArrayProperty(array, access.Field.Name) is { } arrayOwn) {
                return Expression(new BoundFieldAccess(access.Syntax, receiver, arrayOwn, access.Type));
            }

            return Missing(access, $"the property '{access.Field.Name}' of '{receiver.Type.ToDisplayString()}'");
        }

        /// <summary>
        ///     <c>#file</c>, <c>#line</c>, <c>#column</c>, <c>#function</c> and
        ///     <c>#module</c> (spec/lexical.md §9): where they are written,
        ///     known now, so each is a literal.
        /// </summary>
        SilValue? CompilerBuiltin(BoundBuiltin builtin) {
            var position = source.GetLinePosition(builtin.Syntax.Span.Start);
            switch (builtin.Name) {
                case "file":
                    return Emit(new StringLiteralInst(source.FileName));

                case "line":
                    return Emit(new IntegerLiteralInst(SilType.Int, position.Line));

                case "column":
                    return Emit(new IntegerLiteralInst(SilType.Int, position.Column));

                case "function":
                    return Emit(new StringLiteralInst(FunctionName(symbol)));

                case "module": {
                    Symbol? container = symbol;
                    while (container is not null and not ModuleSymbol) {
                        container = container.ContainingSymbol;
                    }

                    return Emit(new StringLiteralInst(container?.Name ?? ""));
                }

                default:
                    return Missing(builtin, $"the builtin '#{builtin.Name}'");
            }

            // A closure is in the function that wrote it; an accessor is named
            // for its property or subscript; the rest by their own names.
            static string FunctionName(Symbol? function) {
                while (function is FunctionSymbol { Name: "" }) {
                    function = function.ContainingSymbol;
                }

                return function switch {
                    FunctionSymbol { FunctionKind: FunctionKind.Accessor, ContainingSymbol: { } member } => member.Name,
                    null => "",
                    _ => function.Name,
                };
            }
        }

        static string Describe(SyntaxNode syntax) {
            var text = syntax.ToString().Trim();
            var line = text.Split('\n')[0];
            return line.Length > 40 ? line[..40] + "…" : line;
        }
    }
}
