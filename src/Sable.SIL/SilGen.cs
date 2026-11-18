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
    public static (SilModule Module, IReadOnlyList<(Diagnostic Diagnostic, SyntaxTree? Tree)> Diagnostics) LowerByFile(Compilation compilation, bool denyReflection = false) {
        var generator = new SilGen(compilation) { _denyReflection = denyReflection };
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
    public static IReadOnlyList<(Diagnostic Diagnostic, SyntaxTree? Tree)> Check(Compilation compilation) {
        var generator = new SilGen(compilation);
        generator.LowerAll();
        return [..generator.PlacedDiagnostics.Where(static d => d.Diagnostic.Code != ErrorCode.ERR_NotLoweredYet)];
    }

    /// <summary>
    ///     Lowers every function, starting the program at the one given
    ///     rather than at '@main': what a test or bench build does, whose
    ///     harness is the entry point of a program that may have its own.
    /// </summary>
    public static (SilModule Module, IReadOnlyList<Diagnostic> Diagnostics) Lower(Compilation compilation, FunctionSymbol entryPoint) {
        var generator = new SilGen(compilation) { _entryOverride = entryPoint };
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
    ///     Lowers one unit of a program compiled separately: the functions of
    ///     the files it owns, and whatever instances of generic functions they
    ///     use; the program's other functions are only declared. The module's
    ///     <see cref="SilModule.Unit" /> says which is which.
    /// </summary>
    /// <param name="program">The program's declarations, gathered once for all its units; gathered here when not given.</param>
    public static (SilModule Module, IReadOnlyList<Diagnostic> Diagnostics) LowerUnit(
        Compilation compilation,
        SilUnit unit,
        SilProgram? program = null,
        FunctionSymbol? entryPoint = null,
        bool denyReflection = false
    ) {
        var generator = new SilGen(compilation) { _entryOverride = entryPoint, _unit = unit, _program = program, _denyReflection = denyReflection };
        generator._module.Unit = new();
        generator._module.DenyReflection = denyReflection;
        generator.LowerAll();
        return (generator._module, generator.Diagnostics);
    }

    SilUnit? _unit;
    SilProgram? _program;

    /// <summary>A file's declarations that lowering looks at — and perhaps other nodes, which each use filters out.</summary>
    IEnumerable<SyntaxNode> Declarations(SyntaxTree tree) => _program?.Declarations(tree, _unit is null || _unit.Owns(tree)) ?? tree.Root.DescendantNodes();

    void LowerAll() {
        // Constants are computed, and comptime blocks run, before anything
        // is lowered: lowering writes each constant's value where it is read
        // (SilGen.Constants.cs). Each unit reports what is in its own files.
        if (_evaluation is null) {
            _constants = ConstantsOf(_compilation);
            foreach (var (diagnostic, tree) in _constants.Diagnostics) {
                if (diagnostic.Code is ErrorCode.ERR_ConstantEvaluationFailed or ErrorCode.ERR_ConstantOverflow or ErrorCode.ERR_CompileError && (_unit is null || _unit.Owns(tree))) {
                    _diagnostics.Add(diagnostic);
                }
            }
        }

        var functions = new List<(FunctionSymbol Symbol, SyntaxNode Syntax, SemanticModel Model)>();

        foreach (var tree in _compilation.SyntaxTrees) {
            var model = _compilation.GetSemanticModel(tree);
            foreach (var declaration in Declarations(tree)) {
                if (declaration is not (FunctionDeclarationSyntax or InitDeclarationSyntax or DeinitDeclarationSyntax or OperatorDeclarationSyntax)) {
                    continue;
                }

                if (_compilation.GetDeclaredSymbol(declaration) is not FunctionSymbol symbol || !IsLowerable(symbol)) {
                    continue;
                }

                functions.Add((symbol, declaration, model));
            }

            // A computed property's getter and setter: methods of the type,
            // named for the property, that reading and writing it call.
            // An observed property's 'willSet' and 'didSet' are methods too,
            // which the setter the compiler writes for it calls.
            foreach (var accessor in Declarations(tree).OfType<AccessorSyntax>()) {
                if (accessor.Keyword.Kind is not (SyntaxKind.GetKeyword or SyntaxKind.SetKeyword or SyntaxKind.WillSetKeyword or SyntaxKind.DidSetKeyword)
                    || (_unit is null || _unit.Owns(tree) ? model.GetAccessorSymbol(accessor) : model.GetAccessorDeclaration(accessor))
                        is not { ContainingSymbol: FieldSymbol field } function
                    || !IsLowerable(function)) {
                    continue;
                }

                if (accessor.Keyword.Kind is SyntaxKind.GetKeyword or SyntaxKind.SetKeyword) {
                    (accessor.Keyword.Kind == SyntaxKind.GetKeyword ? _getters : _setters)[field] = function;
                } else if (!_compilation.ObservedProperties.ContainsKey(field)) {
                    continue;
                }

                functions.Add((function, accessor, model));
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
                    functions.Add((subscript, declaration, model));
                    continue;
                }

                foreach (var accessor in declaration.Accessors?.Accessors ?? []) {
                    if (accessor.Keyword.Kind is not (SyntaxKind.GetKeyword or SyntaxKind.SetKeyword)
                        || (_unit is null || _unit.Owns(tree) ? model.GetAccessorSymbol(accessor) : model.GetAccessorDeclaration(accessor))
                            is not { } function) {
                        continue;
                    }

                    (accessor.Keyword.Kind == SyntaxKind.GetKeyword ? _subscriptGetters : _subscriptSetters)[subscript] = function;
                    functions.Add((function, accessor, model));
                }
            }
        }

        AddDerived(functions);
        AddLazy(functions);
        AddInheritedInitializers(functions);
        CheckBuiltins();

        // Overloads: functions of one name in one container (counted once per
        // pair, not by a scan per function — a large program has thousands).
        // The container is compared by reference, as a symbol is itself.
        var none = new object();
        var sharing = new Dictionary<object, Dictionary<string, int>>(ReferenceEqualityComparer.Instance);
        foreach (var (symbol, _, _) in functions) {
            var container = (object?)symbol.ContainingSymbol ?? none;
            if (!sharing.TryGetValue(container, out var names)) {
                sharing[container] = names = new(StringComparer.Ordinal);
            }

            names[symbol.Name] = names.GetValueOrDefault(symbol.Name) + 1;
        }

        foreach (var (symbol, _, _) in functions) {
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

        // Which classes something inherits from: their methods dispatch.
        foreach (var tree in _compilation.SyntaxTrees) {
            foreach (var declaration in Declarations(tree).OfType<ClassDeclarationSyntax>()) {
                if (_compilation.GetDeclaredSymbol(declaration) is NamedTypeSymbol type && SuperclassOf(type) is { } superclass) {
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
                f.Syntax is FunctionDeclarationSyntax declaration
                && declaration.Attributes.Any(static a => a.Name.ToString().Trim() == "main")
            ).Symbol
            ?? functions.FirstOrDefault(static f => f.Symbol is { Name: "main", ContainingSymbol: ModuleSymbol }).Symbol;

        foreach (var (symbol, syntax, model) in functions) {
            _declarations[symbol] = (syntax, model);
        }

        DeclareGlobals();
        _entrySymbol = entry;

        // Compile-time evaluation lowers what it runs, as it needs it.
        if (_evaluation is not null) {
            return;
        }

        // What is not generic is lowered as it is; what is, once for each set
        // of type arguments something calls it with.
        foreach (var (symbol, syntax, model) in functions) {
            // A Sable.Core '@intrinsic' has the body each use is lowered to.
            if (IsGeneric(symbol) || IsIntrinsic(symbol)) {
                continue;
            }

            // '@extern': C's function, declared in every unit that may call it.
            if (symbol.ForeignName is { } foreign) {
                if (SignatureOf(symbol, syntax, TypeSubstitution.Empty) is { } signature) {
                    _module.Foreign[_names[symbol]] = (foreign, signature);
                }

                continue;
            }

            if (_unit is not null && !_unit.Owns(model.SyntaxTree)) {
                DeclareExternal(symbol, syntax, _names[symbol]);
                continue;
            }

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

        // A generic class's vtable names instances of its methods, which
        // are lowered in their turn — and may make more classes.
        do {
            while (_pending.Count > 0) {
                var (symbol, name, type, substitution) = _pending.Dequeue();
                if (_localGenerics.TryGetValue(symbol, out var local)) {
                    LowerLocalFunction(symbol, local.Body, name, type, local.Source, substitution);
                    continue;
                }

                var (syntax, model) = _declarations[symbol];
                _loweringInstance = name;
                LowerBody(symbol, syntax, model, name, type, substitution);
                _loweringInstance = null;
            }

            BuildVTables();
        } while (_pending.Count > 0);

        // Separately, the globals are set up by the unit the program starts in.
        if (_unit is null || _module.EntryPoint is not null) {
            LowerGlobalLifetimes();
        }

        BuildVTables();
        SharedStorage.InsertChecks(_module);
        RootChecker.Check(_module, _sources, _diagnostics, (before, source) => {
            if (TreeOf(source) is { } tree) {
                Place(before, tree);
            }
        });
        if (_module.Unit is { } unitInfo) {
            DeclareReferenced(unitInfo);
            RecordAbi(unitInfo);
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

        var type = _external.TryGetValue(name, out var signature) ? signature() : null;
        _externalTypes[name] = type;
        return type;
    }

    /// <summary>
    ///     A function another unit lowers, declared here as that unit will
    ///     define it — with the side effects lowering it would have had on the
    ///     types (a deinit), so that every unit lays them out alike.
    /// </summary>
    void DeclareExternal(FunctionSymbol symbol, SyntaxNode syntax, string name) {
        // LowerBody makes nothing of a declaration without a body.
        var hasBody = syntax switch {
            FunctionDeclarationSyntax f => f.Body is not null || f.ExpressionBody is not null,
            InitDeclarationSyntax i => i.Body is not null,
            DeinitDeclarationSyntax => true,
            OperatorDeclarationSyntax o => o.Body is not null || o.ExpressionBody is not null,
            AccessorSyntax a => a.Body is not null || a.ExpressionBody is not null,
            _ => false,
        };
        if (!hasBody && _compilation.GetDerivedBody(symbol) is null && _compilation.GetLazyBody(symbol) is null
            && _compilation.GetInheritedInitializerBody(symbol) is null) {
            return;
        }

        // As the first loop would: no signature, nothing lowered.
        FunctionType? Signature() => SignatureOf(symbol, syntax, TypeSubstitution.Empty);
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

    void DeclareGlobals() {
        foreach (var tree in _compilation.SyntaxTrees) {
            var model = _compilation.GetSemanticModel(tree);
            foreach (var declaration in Declarations(tree).Where(static d => d is FieldDeclarationSyntax or ConstDeclarationSyntax)) {
                if (_compilation.GetDeclaredSymbol(declaration) is not FieldSymbol field || !IsGlobal(field)
                    || field.ContainingSymbol is NamedTypeSymbol { IsGenericDefinition: true } || Lower(field.Type) is not { } type) {
                    continue;
                }

                // A constant written where it is read needs no storage.
                if (_evaluation is null && ConstantRead(field) is not null) {
                    continue;
                }

                // Separately, the unit the program starts in sets up every
                // global, and sees no other unit's bodies: a static of a type
                // declared in one is the whole program's to compile.
                if (_unit is not null && InsideBody(declaration)) {
                    Unsupported<object>(declaration, "a static stored property of a type declared in a function, compiled separately");
                    continue;
                }

                var owner = field.ContainingSymbol is NamedTypeSymbol named ? $"{named.ContainingSymbol?.Name}.{named.Name}" : field.ContainingSymbol?.Name;
                var name = $"{owner}.{field.Name}";
                _globalFields.Add((field, name, model));
                _globalsByField[field] = (name, type);
                _module.Globals.Add((name, type));
            }
        }
    }

    static bool InsideBody(SyntaxNode node) {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent) {
            if (parent is DeclarationStatementSyntax) {
                return true;
            }
        }

        return false;
    }

    internal (string Name, SilType Type)? GlobalOf(FieldSymbol field) => _globalsByField.TryGetValue(field, out var global) ? global : null;

    /// <summary>
    ///     Two functions the entry point calls: one that stores each global's
    ///     initial value, in the order declared, before anything else runs;
    ///     one that destroys them, last first, as the program returns.
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

        // Lowered in the entry point's context: a function of no parameters.
        var unit = new FunctionType([], SilType.Unit);
        var statements = new List<BoundStatement>();
        foreach (var (field, _, model) in _globalFields) {
            if (model.GetFieldInitializer(field) is { } initial) {
                var target = new BoundFieldAccess(initial.Syntax, null, field);
                statements.Add(new BoundExpressionStatement(initial.Syntax, new BoundAssignment(initial.Syntax, target, initial)));
            }
        }

        var source = _globalFields[0].Model.SyntaxTree.Text;
        var syntax = _globalFields[0].Field.DeclaringSyntax[0];
        var init = new SilFunction(GlobalsInit, unit);
        new FunctionLowering(this, initSymbol, init, source, TypeSubstitution.Empty).Lower(new BoundBlock(syntax, [], [..statements]));
        init.RemoveUnreachableBlocks();
        _module.Add(init);

        var deinit = new SilFunction(GlobalsDeinit, unit);
        var entry = deinit.AddBlock();
        for (var i = _globalFields.Count - 1; i >= 0; i--) {
            var (name, type) = _globalsByField[_globalFields[i].Field];
            if (!type.IsTrivial) {
                entry.Append(new DestroyAddrInst(entry.Append(new GlobalAddrInst(name, type))));
            }
        }

        entry.Append(new ReturnInst(null));
        _module.Add(deinit);
    }

    /// <summary>Each lowered function's source, for the diagnostics of checks run on the whole module.</summary>
    readonly Dictionary<SilFunction, SourceText> _sources = [];

    readonly Dictionary<FunctionSymbol, (SyntaxNode Syntax, SemanticModel Model)> _declarations = [];
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
        if ((_compilation.GetDerivedBody(symbol) ?? _compilation.GetLazyBody(symbol) ?? _compilation.GetInheritedInitializerBody(symbol)
                ?? model.GetBoundNode(syntax)) is not BoundBlock body) {
            return;
        }

        var reported = _diagnostics.Count;
        try {
            LowerBody(symbol, syntax, body, model.SyntaxTree.Text, name, type, substitution);
        } finally {
            Place(reported, model.SyntaxTree);
        }
    }

    void LowerBody(FunctionSymbol symbol, SyntaxNode syntax, BoundBlock body, SourceText source, string name, FunctionType type, TypeSubstitution substitution) {
        var function = new SilFunction(name, type) {
            SourceFile = source.FileName,
            Line = source.GetLinePosition(syntax.Span.Start).Line,
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
            new FunctionLowering(this, symbol, initialize, source, substitution).Lower(body);
            initialize.RemoveUnreachableBlocks();
            _module.Add(initialize);
            _module.Add(Allocator(function, classType, initialize));

            // Its body is checked as any other is: 'self.h = h' moves a
            // @noncopy parameter into the object, and is not a copy of it.
            MoveChecker.Check(initialize, source, _diagnostics);
            _sources[initialize] = source;
            return;
        }

        new FunctionLowering(this, symbol, function, source, substitution).Lower(body);
        function.RemoveUnreachableBlocks();
        _module.Add(function);

        if (symbol.FunctionKind == FunctionKind.Deinitializer) {
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

    /// <summary>What initialising takes: the object, then the initialiser's own parameters; it gives nothing.</summary>
    internal static FunctionType InitializerType(FunctionType allocator) =>
        new([allocator.Result, ..allocator.Parameters], SilType.Unit, allocator.Thrown,
            allocator.Owned.IsEmpty ? default : [false, ..allocator.Owned]);

    /// <summary>The allocating initialiser: a new object, initialised; or, if initialising throws, destroyed.</summary>
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

    /// <summary>The type parameter standing for each distinct <c>some P</c> a function's parameters have.</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FunctionSymbol, TypeParameterSymbol[]> s_opaqueParameters = new();

    /// <summary>The <c>some P</c> each of those type parameters stands in for.</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TypeParameterSymbol, OpaqueTypeSymbol> s_opaqueOf = new();

    /// <summary>
    ///     A parameter of type <c>some P</c> is an anonymous generic parameter
    ///     (docs/03 §3.1): each distinct one has a type parameter here, which a
    ///     call binds to its argument's type, so the function is monomorphised
    ///     as a generic one is. The binder gives two <c>some P</c> of the same
    ///     P one type, so they share one parameter.
    /// </summary>
    static TypeParameterSymbol[] OpaqueParametersOf(FunctionSymbol symbol) =>
        s_opaqueParameters.GetValue(symbol, static function => {
            var opaques = new List<OpaqueTypeSymbol>();
            foreach (var parameter in function.Parameters) {
                if (parameter.Type is OpaqueTypeSymbol opaque && !opaques.Contains(opaque)) {
                    opaques.Add(opaque);
                }
            }

            return [..opaques.Select((opaque, i) => {
                var parameter = new TypeParameterSymbol(opaque.ToDisplayString(), function, null, -1 - i);
                s_opaqueOf.Add(parameter, opaque);
                return parameter;
            })];
        });

    /// <summary>A type with these type arguments in force, a <c>some P</c> parameter's type among them.</summary>
    /// <remarks>A <c>some P</c> result or local is what it hides: code is generated for the concrete type.</remarks>
    internal static TypeSymbol Substitute(TypeSubstitution substitution, TypeSymbol type) {
        if (type is OpaqueTypeSymbol { Origin: null } opaque) {
            foreach (var parameter in substitution.Parameters) {
                if (s_opaqueOf.TryGetValue(parameter, out var standsFor) && standsFor.Equals(opaque)) {
                    return substitution.Lookup(parameter)!;
                }
            }
        }

        return Reveal(substitution.Apply(type));
    }

    /// <summary>
    ///     The concrete type behind a <c>some P</c> result or local — itself
    ///     perhaps another's result — or the type unchanged.
    /// </summary>
    /// <remarks>
    ///     Functions that return only each other's results hide nothing
    ///     concrete; the chain is followed until it repeats, and then the
    ///     opaque type is left as it is, which nothing lowers.
    /// </remarks>
    static TypeSymbol Reveal(TypeSymbol type) {
        var seen = new HashSet<Symbol>(ReferenceEqualityComparer.Instance);
        while (type is OpaqueTypeSymbol { Origin: { } origin, HiddenType: { } hidden } && seen.Add(origin)) {
            type = hidden;
        }

        return type;
    }

    static bool IsGeneric(FunctionSymbol symbol) => TypeParametersOf(symbol).Any();

    /// <summary>
    ///     The instance of a generic function for these type arguments: its
    ///     name, and its signature — lowered later, once, however many calls
    ///     ask for it. Null when the arguments are not all known types.
    /// </summary>
    internal (string Name, FunctionType Type)? Instance(FunctionSymbol symbol, TypeSubstitution substitution, SyntaxNode at) {
        if (!_names.TryGetValue(symbol, out var baseName) || !(_declarations.ContainsKey(symbol) || _localGenerics.ContainsKey(symbol))) {
            return null;
        }

        substitution = WithAssociatedTypes(symbol, substitution);

        var arguments = new List<string>();
        foreach (var parameter in TypeParametersOf(symbol)) {
            // Never has no values to lower, and is still an argument: the
            // error type of 'apply { 7 }', whose closure cannot throw.
            if (substitution.Lookup(parameter) is { } never && never.Equals(Core.Never)) {
                arguments.Add("Never");
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

        if (EndlessInstances(symbol, substitution, at)) {
            return null;
        }

        if (SignatureOf(symbol, at, substitution) is not { } type) {
            return null;
        }

        _instances[name] = type;
        _instanceOrigins[name] = (symbol, substitution, _loweringInstance);
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
    ///     True, with an error reported, when a new instance would be one of
    ///     an endless series: a generic function that calls itself, directly
    ///     or through others, with a type argument built from its own, as
    ///     <c>deep((x, x), n - 1)</c> in <c>deep&lt;T&gt;</c> does. Each copy
    ///     would ask for a larger one, and monomorphisation would never end.
    /// </summary>
    bool EndlessInstances(FunctionSymbol symbol, TypeSubstitution substitution, SyntaxNode at) {
        var depth = 0;
        for (var requester = _loweringInstance;
             requester is not null && _instanceOrigins.TryGetValue(requester, out var origin);
             requester = origin.Requester) {
            if (++depth >= MaxInstanceDepth) {
                _diagnostics.Error(
                    ErrorCode.ERR_EndlessInstantiation,
                    at.Span,
                    $"'{symbol.Name}' is reached through more than {MaxInstanceDepth} generic calls, each needing a copy "
                    + "of a generic function for new types: a generic function is compiled once for each type it is used with, "
                    + "so this would not finish compiling"
                );
                return true;
            }

            if (!ReferenceEquals(origin.Symbol, symbol)) {
                continue;
            }

            foreach (var parameter in TypeParametersOf(symbol)) {
                if (origin.Substitution.Lookup(parameter) is { } before
                    && substitution.Lookup(parameter) is { } after
                    && Grows(before, after)) {
                    _diagnostics.Error(
                        ErrorCode.ERR_EndlessInstantiation,
                        at.Span,
                        $"'{symbol.Name}' with {parameter.Name} = '{before.ToDisplayString()}' leads to a call of it with "
                        + $"{parameter.Name} = '{after.ToDisplayString()}', which leads to a larger type again, without end: "
                        + "a generic function is compiled once for each type it is used with, so this would not finish compiling"
                    );
                    return true;
                }
            }
        }

        return false;
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
    static NamedTypeSymbol? SuperclassOf(NamedTypeSymbol type) =>
        type.BaseTypes.OfType<NamedTypeSymbol>().FirstOrDefault(static b => b.TypeKind == TypeKind.Class);

    /// <summary>The classes some other class inherits from: their methods are called through the vtable.</summary>
    readonly HashSet<NamedTypeSymbol> _inherited = [];

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
        IsVirtual(method) && _inherited.Contains(Owner(method)!.OriginalDefinition);

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

            if (type.Superclass is { } superclass) {
                Build(superclass);
                type.VTable.AddRange(superclass.VTable);
            }

            if (!_inherited.Contains(symbol.OriginalDefinition) && type.Superclass is null) {
                return;
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

                if (IsGeneric(method)) {
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

        var stub = new SilFunction(name, type);
        var entry = stub.AddBlock();
        foreach (var parameter in type.Parameters) {
            entry.AddArgument(parameter);
        }

        entry.Append(new CondFailInst(entry.Append(new IntegerLiteralInst(SilType.Bool, 1)), $"abstract method '{method.Name}' called"));
        entry.Append(new UnreachableInst());
        _module.Add(stub);
        _names[method] = name;
    }

    /// <summary>
    ///     <c>Module.name</c>, <c>Module.Type.name</c> for a member, and the
    ///     parameter types after it when the name is overloaded:
    ///     <c>M.area$Int$Int</c>.
    /// </summary>
    static string Mangle(FunctionSymbol symbol, bool overloaded) {
        var owner = Owner(symbol) is { } type
            ? $"{type.ContainingSymbol?.Name}.{type.Name}"
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

    static string Sanitise(string text) =>
        new([..text.Select(static c => char.IsLetterOrDigit(c) || c is '_' or '.' ? c : '$')]);

    /// <summary>True for a method that takes <c>self</c>, as opposed to a static one or an initialiser.</summary>
    // An operator is a static function of its type: both operands are arguments.
    static bool HasSelf(FunctionSymbol symbol) =>
        Owner(symbol) is not null && !symbol.IsStatic && symbol.FunctionKind is not (FunctionKind.Initializer or FunctionKind.Operator);

    FunctionType? SignatureOf(FunctionSymbol symbol, SyntaxNode at, TypeSubstitution substitution) {
        SilType? Lower(TypeSymbol type) => this.Lower(Substitute(substitution, type));

        // An async function is an ordinary one: a task suspends only where
        // it waits, in the runtime, with its whole stack (docs/04 §5). A
        // 'rethrows' one throws what its function parameters throw, together:
        // the binder has made that its thrown type.
        var parameters = ImmutableArray.CreateBuilder<SilType>();

        // 'self' comes first: by value, or by address when the method may change it.
        if (HasSelf(symbol)) {
            if (this.Lower(SelfType(symbol, substitution)) is not { } self) {
                return Unsupported<FunctionType>(at, $"a member of '{SelfType(symbol, substitution).ToDisplayString()}'");
            }

            parameters.Add(symbol.IsMutating && self is not ClassType ? SilType.Address(self) : self);
        }

        foreach (var parameter in symbol.Parameters) {
            // 'in r: region' borrows the region, as any parameter is borrowed; so
            // does a 'borrowing' one, which only says so (and may not let it escape).
            if (parameter.Modifier is not (ParameterModifier.None or ParameterModifier.InOut or ParameterModifier.Consuming
                or ParameterModifier.Ref or ParameterModifier.In or ParameterModifier.Out or ParameterModifier.Borrowing)) {
                return Unsupported<FunctionType>(at, $"the '{parameter.Modifier}' parameter '{parameter.Name}'");
            }

            if (Lower(parameter.Type) is not { } type) {
                return Unsupported<FunctionType>(at, $"a parameter of type '{parameter.Type.ToDisplayString()}'");
            }

            // An inout, out or ref parameter is the address of the caller's storage:
            // an out one is written there before the callee returns.
            parameters.Add(parameter.Modifier is ParameterModifier.InOut or ParameterModifier.Out or ParameterModifier.Ref ? SilType.Address(type) : type);
        }

        var resultSymbol = symbol.FunctionKind == FunctionKind.Initializer ? SelfType(symbol, substitution) : symbol.ReturnType;
        if (Lower(resultSymbol) is not { } result) {
            return Unsupported<FunctionType>(at, $"a result of type '{resultSymbol.ToDisplayString()}'");
        }

        // 'throws(E)' with E as Never cannot throw: it is an ordinary function.
        SilType? thrown = null;
        if (symbol.ThrownType is { } thrownSymbol && !Substitute(substitution, thrownSymbol).Equals(Core.Never)) {
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

        return new FunctionType(parameters.ToImmutable(), result, thrown, owned);
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
        if (_types.TryGetValue(type, out var cached)) {
            return cached;
        }

        var lowered = _lowered.Count;
        var types = _module.Types.Count;
        var result = LowerUncached(type);
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
        _lowered.Add(type);
        return result;
    }

    /// <summary>Enters a named type before its members, so that they may refer to it.</summary>
    T Begin<T>(TypeSymbol symbol, T type) where T : NominalType {
        if (symbol is NamedTypeSymbol { IsConstructed: true } constructed) {
            type.DisplayName = $"{constructed.OriginalDefinition.ContainingSymbol?.Name}.{constructed.Name}<{string.Join(", ", constructed.TypeArguments.Select(static a => a.ToDisplayString()))}>";
        }

        _types[symbol] = type;
        _lowered.Add(symbol);
        return _module.AddType(type);
    }

    SilType? LowerUncached(TypeSymbol type) {
        // A 'some P' result or local is laid out as what it hides; inside an
        // optional or an array, too.
        if (type is OpaqueTypeSymbol { Origin: not null } opaque) {
            return Reveal(opaque) is var hidden && hidden is not OpaqueTypeSymbol ? Lower(hidden) : null;
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

        if (type.Equals(Core.Void)) {
            return SilType.Unit;
        }

        if (type.Equals(Core.String)) {
            return SilType.String;
        }

        if (type.Equals(Core.Region)) {
            return SilType.Region;
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

            // The growable kinds, the fixed one and slices share a counted
            // buffer; a slice is a copy, for now, rather than a view. So
            // does a bounded array outside an @inline_layout struct — 'T[..k]'
            // as a local, a parameter or a field of a plain struct — with its
            // bound checked where it grows; inside one it is bytes in the
            // block (InlineField).
            // An array copies its elements on write, so a move-only element has no array yet.
            case ArrayTypeSymbol { ArrayKind: ArrayKind.Grow or ArrayKind.GrowShrink or ArrayKind.Fixed or ArrayKind.Slice or ArrayKind.Inline } array:
                return Lower(array.ElementType) is { } elementType && elementType is not NominalType { IsNoncopyable: true }
                    ? new ArrayType(elementType)
                    : null;

            // A dictionary, until Sable.Core writes one: its entries, in an
            // array, with the key type's equality — a builtin, or its '=='.
            case NamedTypeSymbol { TypeArguments: [var key, var value] } named when named.OriginalDefinition.Equals(Core.Dictionary): {
                if (Lower(key) is not { } keyType || Lower(value) is not { } valueType) {
                    return null;
                }

                return SilBuiltins.Find($"cmp_eq_{keyType}") is null && KeyEquality(key) is null
                    ? null
                    : new ArrayType(SilType.Tuple([keyType, valueType]));
            }

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

            // 'any P': a closure per requirement, sharing one box with the value.
            case ExistentialTypeSymbol { Constraint: NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol }:
                return ExistentialLayout(protocol);

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
                foreach (var parameter in functionType.ParameterTypes) {
                    if (Lower(parameter) is not { } lowered) {
                        return null;
                    }

                    parameters.Add(lowered);
                }

                if (Lower(functionType.ReturnType) is not { } result) {
                    return null;
                }

                SilType? thrown = null;
                if (functionType.ThrownType is { } thrownSymbol && !thrownSymbol.Equals(Core.Never) && (thrown = Lower(thrownSymbol)) is null) {
                    return null;
                }

                return new ThickFunctionType(new FunctionType(parameters.ToImmutable(), result, thrown));
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

            case NamedTypeSymbol { IsGenericDefinition: false, TypeKind: TypeKind.Struct } named when named.OriginalDefinition.IsInlineLayout:
                return LowerInline(named);

            case NamedTypeSymbol { IsGenericDefinition: false, TypeKind: TypeKind.Struct } named: {
                var structType = Begin(named, new StructType(TypeName(named)) { IsNoncopyable = named.OriginalDefinition.IsNoncopyable });
                var members = TypeSubstitution.For(named);
                if (StoredFields(named.OriginalDefinition).Any(static f => f.ReferenceKind != ReferenceKind.Strong)) {
                    return null; // weak and unowned fields of structs wait for value witnesses of their own
                }

                foreach (var field in StoredFields(named.OriginalDefinition)) {
                    if (Lower(members.Apply(field.Type)) is not { } fieldType) {
                        return null;
                    }

                    structType.Fields.Add((field.Name, fieldType));
                }

                // A struct that holds a move-only value is move-only itself:
                // copying it would copy the value, and run its deinit twice.
                // This is how 'Box<T: ~Copyable>' holding a '@noncopy' type
                // is moved, never copied.
                structType.IsNoncopyable |= structType.Fields.Exists(static f => IsMoveOnly(f.Type));
                return HoldsItself(structType) ? null : structType;
            }

            case NamedTypeSymbol { TypeArguments: [var success] } task when task.OriginalDefinition.Equals(Core.Task):
                return TaskClass(success);

            // An actor is a class whose calls are serialised: the cooperative
            // scheduler runs one task at a time, so its state needs no more.
            case NamedTypeSymbol { IsGenericDefinition: false, TypeKind: TypeKind.Class or TypeKind.Actor } named when !named.OriginalDefinition.Equals(Core.Task): {
                var classType = Begin(named, new ClassType(TypeName(named)));
                _classes[classType] = named;

                // A superclass's fields come first, where they are in it.
                if (SuperclassOf(named.OriginalDefinition) is { } superclass) {
                    if (Lower(TypeSubstitution.For(named).Apply(superclass)) is not ClassType superType) {
                        return null;
                    }

                    classType.Superclass = superType;
                    classType.Fields.AddRange(superType.Fields);
                }

                classType.Conformances.AddRange(DeclaredProtocols(named.OriginalDefinition));

                var members = TypeSubstitution.For(named);
                foreach (var field in StoredFields(named.OriginalDefinition)) {
                    if (Lower(members.Apply(field.Type)) is not { } fieldType) {
                        return null;
                    }

                    // A weak or unowned field holds its object without keeping it.
                    SilType stored = (field.ReferenceKind, fieldType) switch {
                        (ReferenceKind.Weak, EnumType optional) => new WeakStorageType(optional),
                        (ReferenceKind.Unowned, ClassType referent) => new UnownedStorageType(referent),
                        _ => fieldType,
                    };

                    classType.Fields.Add((field.Name, stored));
                }

                return classType;
            }

            case NamedTypeSymbol { IsGenericDefinition: false, TypeKind: TypeKind.Union } named: {
                var enumType = Begin(named, new EnumType(TypeName(named)) { IsNoncopyable = named.OriginalDefinition.IsNoncopyable });
                var members = TypeSubstitution.For(named);
                foreach (var unionCase in named.OriginalDefinition.Members.OfType<UnionCaseSymbol>()) {
                    SilType? payload = null;
                    if (unionCase.Payload.Length > 0) {
                        var parts = ImmutableArray.CreateBuilder<SilType>();
                        foreach (var field in unionCase.Payload) {
                            if (Lower(members.Apply(field.Type)) is not { } part) {
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
        var structType = Begin(named, new StructType(TypeName(named)) { IsInline = true });
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
        return anyError;
    }

    /// <summary>The subclass of <c>any Error</c>'s class that holds an error of one type.</summary>
    internal ClassType ErrorBox(SilType error) {
        var name = $"any$Error$of${Sanitise(error.ToString())}";
        if (_module.FindType(name) is ClassType existing) {
            return existing;
        }

        var anyError = AnyErrorType();
        var box = _module.AddType(new ClassType(name) { Superclass = anyError });
        box.Fields.Add(("value", error));
        box.VTable.Add((ErrorDescribeSlot, ErrorDescriber(anyError, box, error)));
        return box;
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

        var name = "Union$" + string.Join("$or$", cases.Select(static c => Sanitise(c.ToString())));
        if (_module.FindType(name) is EnumType existing) {
            return existing;
        }

        var enumType = Begin(union, new EnumType(name));
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
        var name = "Union$" + Sanitise($"{recursive.Alias.ContainingSymbol?.Name}.{recursive.Alias.Name}");
        if (_module.FindType(name) is EnumType existing) {
            return existing;
        }

        var enumType = Begin(recursive, new EnumType(name));
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
    ///     or throws another error: code that calls it, converting what it
    ///     throws. Its context is a box holding the function converted.
    /// </summary>
    internal (string Name, FunctionType Type)? ConversionThunk(ThickFunctionType from, ThickFunctionType to) {
        var name = $"$convert${Sanitise(from.ToString())}$to${Sanitise(to.ToString())}";
        var code = to.Implementation;
        if (_module.Find(name) is not null) {
            return (name, code);
        }

        var source = from.Signature;
        if (!source.Parameters.SequenceEqual(to.Signature.Parameters) || source.Result != to.Signature.Result
            || source.Thrown is not null && to.Signature.Thrown is null) {
            return null;
        }

        var thunk = new SilFunction(name, code);
        var entry = thunk.AddBlock();
        var arguments = to.Signature.Parameters.Select((p, i) => (SilValue)entry.AddArgument(p)).ToList();
        var context = entry.AddArgument(SilType.Object);
        var box = BoxType(from);
        var callee = entry.Append(new LoadInst(entry.Append(new RefElementAddrInst(entry.Append(new UncheckedRefCastInst(context, box)), 0))));
        if (source.Thrown is null) {
            var result = entry.Append(new ApplyInst(callee, source.Result, [..arguments]));
            entry.Append(new DestroyValueInst(callee));
            entry.Append(new ReturnInst(source.Result == SilType.Unit ? null : result));
        } else {
            var normal = thunk.AddBlock();
            var result = source.Result == SilType.Unit ? null : normal.AddArgument(source.Result);
            var failed = thunk.AddBlock();
            var error = failed.AddArgument(source.Thrown);
            entry.Append(new TryApplyInst(callee, source.Result, [..arguments], normal, failed));
            normal.Append(new DestroyValueInst(callee));
            normal.Append(new ReturnInst(result));
            failed.Append(new DestroyValueInst(callee));
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
    ///     What <c>s.get</c> calls: a closure whose context is a box holding
    ///     the receiver, which it passes as <c>self</c> to the method, through
    ///     the vtable when a subclass may override it.
    /// </summary>
    internal (string Name, FunctionType Type) BoundMethodThunk(FunctionSymbol method, string name, FunctionType type) {
        var dynamic = IsDynamic(method);
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
        var self = entry.Append(new LoadInst(entry.Append(new RefElementAddrInst(box, 0))));
        SilValue callee = dynamic
            ? entry.Append(new ClassMethodInst(self, SlotName(method), type))
            : entry.Append(new FunctionRefInst(name, type));
        SilValue[] all = [self, ..arguments];

        void Release(SilBlock block) {
            if (!self.Type.IsTrivial) {
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
    readonly Dictionary<FunctionSymbol, (BoundBlock Body, SourceText Source)> _localGenerics = [];

    /// <summary>
    ///     A generic local function, met in the body of an instance of the
    ///     function around it: its name is for that instance, so the
    ///     instances its calls ask for are that instance's own.
    /// </summary>
    internal void DeclareLocalGeneric(FunctionSymbol symbol, BoundBlock body, string name, SourceText source) {
        _names[symbol] = name;
        _localGenerics[symbol] = (body, source);
    }

    internal void LowerLocalFunction(FunctionSymbol symbol, BoundBlock body, string name, FunctionType type, SourceText source, TypeSubstitution substitution) {
        var syntax = symbol.DeclaringSyntax.FirstOrDefault();
        var function = new SilFunction(name, type) {
            SourceFile = source.FileName,
            Line = syntax is null ? 0 : source.GetLinePosition(syntax.Span.Start).Line,
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
        new FunctionLowering(this, symbol, function, source, substitution).Lower(body);
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
    internal List<Capture> CapturesOf(BoundClosure closure) => CapturesOf(closure.Symbol, closure.Body, closure.Captures);

    /// <summary>
    ///     What a closure, or a local function, captures. A local function
    ///     that captures is itself captured, as its closure, by what calls it.
    /// </summary>
    internal List<Capture> CapturesOf(FunctionSymbol symbol, BoundBlock body, ImmutableArray<BoundCapture> listedCaptures) {
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
                    } else {
                        Add(access.Local, access.Local.Type, true, null);
                    }

                    break;

                case BoundParameterAccess access when !inner.Contains(access.Parameter.ContainingSymbol!):
                    Add(access.Parameter, access.Parameter.Type, false, null);
                    break;

                case BoundSelfAccess self:
                    Add(SelfKey, self.Type, false, null);
                    break;
            }
        }

        return captures;
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

    /// <summary>
    ///     The '==' a dictionary compares keys of a type with, when its
    ///     equality is not a builtin: the operator the type declares or
    ///     '@derive' gave it, as the binder would find for 'a == b'.
    /// </summary>
    static FunctionSymbol? KeyEquality(TypeSymbol key) =>
        key is NamedTypeSymbol named
            ? named.OriginalDefinition.Members.OfType<FunctionSymbol>().FirstOrDefault(static f =>
                f is { FunctionKind: FunctionKind.Operator, Fixity: OperatorFixity.Infix, Name: "==", Parameters.Length: 2 })
            : null;

    /// <summary>The SIL function a key type's '==' is, for its construction; null when it cannot be had.</summary>
    internal (string Name, FunctionType Type)? KeyEqualityFunction(TypeSymbol key, SyntaxNode at) {
        if (KeyEquality(key) is not { } equality) {
            return null;
        }

        if (IsGeneric(equality)) {
            return Instance(equality, TypeSubstitution.For(key), at);
        }

        if (!_names.TryGetValue(equality, out var name)) {
            return null;
        }

        return (_module.Find(name)?.Type ?? SignatureOf(equality, at, TypeSubstitution.Empty)) is { } type ? (name, type) : null;
    }

    /// <summary>The functions a dictionary of these entries is read and written with: get, set and remove.</summary>
    /// <remarks>
    ///     Each looks for the key from the front, comparing with the key
    ///     type's builtin equality, or else with its <paramref name="equality" />:
    ///     linear, and a stand-in until Sable.Core has a dictionary of its own
    ///     that hashes.
    /// </remarks>
    internal (string Get, string Set, string Remove) DictionaryFunctions(ArrayType entries, EnumType optional, (string Name, FunctionType Type)? equality = null) {
        var tuple = (TupleType)entries.Element;
        var (key, value) = (tuple.Elements[0], tuple.Elements[1]);
        var stem = Sanitise(entries.ToString());
        var names = ($"$dictionary.get${stem}", $"$dictionary.set${stem}", $"$dictionary.remove${stem}");
        if (_module.Find(names.Item1) is not null) {
            return names;
        }

        var equal = $"cmp_eq_{key}";
        SilValue Copy(SilBlock block, SilValue v) => v.Type.IsTrivial ? v : block.Append(new CopyValueInst(v));
        void Destroy(SilBlock block, SilValue v) {
            if (!v.Type.IsTrivial) {
                block.Append(new DestroyValueInst(v));
            }
        }

        SilValue Count(SilBlock block, SilValue array) => block.Append(new BuiltinInst("array_count", SilType.Int, array));
        SilValue One(SilBlock block) => block.Append(new IntegerLiteralInst(SilType.Int, 1));

        // A loop over the indices, calling 'found' where the key is, and
        // 'missing' after the last; 'read' gives the array to look in.
        void Search(SilFunction function, SilBlock entry, SilValue searched, bool byAddress, Action<SilBlock, SilValue, SilValue?> found, Action<SilBlock> missing) {
            var header = function.AddBlock();
            var index = header.AddArgument(SilType.Int);
            var test = function.AddBlock();
            var hit = function.AddBlock();
            var next = function.AddBlock();
            var end = function.AddBlock();
            entry.Append(new BranchInst(header, entry.Append(new IntegerLiteralInst(SilType.Int, 0))));

            // The entries are borrowed, never copied: a load_borrow of the
            // address for each look, an element read in place.
            var counted = byAddress ? header.Append(new LoadBorrowInst(searched)) : searched;
            var count = Count(header, counted);
            if (byAddress) {
                header.Append(new EndBorrowInst(counted));
            }

            header.Append(new CondBranchInst(header.Append(new BuiltinInst("cmp_lt_Int", SilType.Bool, index, count)), test, [], end, []));

            var array = byAddress ? test.Append(new LoadBorrowInst(searched)) : searched;
            var element = test.Append(new BuiltinInst("array_get_borrowed", tuple, array, index));
            var stored = test.Append(new TupleExtractInst(element, 0));
            SilValue same = equality is { } byOperator
                ? test.Append(new ApplyInst(test.Append(new FunctionRefInst(byOperator.Name, byOperator.Type)), SilType.Bool, stored, function.Entry.Arguments[1]))
                : test.Append(new BuiltinInst(equal, SilType.Bool, stored, function.Entry.Arguments[1]));
            if (byAddress) {
                test.Append(new EndBorrowInst(array));
            }

            test.Append(new CondBranchInst(same, hit, [], next, []));
            found(hit, index, byAddress ? null : element);

            next.Append(new BranchInst(header, next.Append(new BuiltinInst("add_Int", SilType.Int, index, One(next)))));
            missing(end);
        }

        // get(entries, key) -> Value?
        var get = new SilFunction(names.Item1, new FunctionType([entries, key], optional));
        var getEntry = get.AddBlock();
        var getEntries = getEntry.AddArgument(entries);
        getEntry.AddArgument(key);
        Search(get, getEntry, getEntries, false,
            (hit, _, element) => {
                var found = Copy(hit, hit.Append(new TupleExtractInst(element!, 1)));
                hit.Append(new ReturnInst(hit.Append(new EnumInst(optional, optional.CaseIndex("Some"), found))));
            },
            end => end.Append(new ReturnInst(end.Append(new EnumInst(optional, optional.CaseIndex("None"), null)))));
        _module.Add(get);

        // set(&entries, key, value): replaces the entry with the key, or adds one.
        var set = new SilFunction(names.Item2, new FunctionType([SilType.Address(entries), key, value], SilType.Unit));
        var setEntry = set.AddBlock();
        var setAddress = setEntry.AddArgument(SilType.Address(entries));
        var setKey = setEntry.AddArgument(key);
        var setValue = setEntry.AddArgument(value);
        Search(set, setEntry, setAddress, true,
            (hit, index, _) => {
                var pair = hit.Append(new TupleInst(tuple, Copy(hit, setKey), Copy(hit, setValue)));
                hit.Append(new BuiltinInst("array_set", SilType.Unit, setAddress, index, pair));
                hit.Append(new ReturnInst(null));
            },
            end => {
                var pair = end.Append(new TupleInst(tuple, Copy(end, setKey), Copy(end, setValue)));
                end.Append(new BuiltinInst("array_append", SilType.Unit, setAddress, pair));
                end.Append(new ReturnInst(null));
            });
        _module.Add(set);

        // remove(&entries, key): the last entry takes the removed one's place.
        var remove = new SilFunction(names.Item3, new FunctionType([SilType.Address(entries), key], SilType.Unit));
        var removeEntry = remove.AddBlock();
        var removeAddress = removeEntry.AddArgument(SilType.Address(entries));
        removeEntry.AddArgument(key);
        Search(remove, removeEntry, removeAddress, true,
            (hit, index, _) => {
                var last = hit.Append(new BuiltinInst("array_remove_last", tuple, removeAddress));
                var after = hit.Append(new LoadInst(removeAddress));
                var remaining = Count(hit, after);
                Destroy(hit, after);
                var wasLast = remove.AddBlock();
                var move = remove.AddBlock();
                hit.Append(new CondBranchInst(hit.Append(new BuiltinInst("cmp_eq_Int", SilType.Bool, index, remaining)), wasLast, [], move, []));
                Destroy(wasLast, last);
                wasLast.Append(new ReturnInst(null));
                move.Append(new BuiltinInst("array_set", SilType.Unit, removeAddress, index, last));
                move.Append(new ReturnInst(null));
            },
            end => end.Append(new ReturnInst(null)));
        _module.Add(remove);

        return names;
    }

    /// <summary>What an existential of each protocol holds, in order: each requirement's slot and member.</summary>
    readonly Dictionary<StructType, List<(string Slot, Symbol Member)>> _existentials = [];

    /// <summary>The slot a property's getter takes in an existential.</summary>
    internal static string GetterSlot(FieldSymbol field) => "get$" + field.Name;

    /// <summary>
    ///     The struct <c>any P</c> lowers to: for each requirement P has, or
    ///     inherits — a method that does not mutate, or a property to read —
    ///     a closure that calls it on the value the existential holds.
    ///     Requirements that mention <c>Self</c> cannot be called on an
    ///     existential and have no slot.
    /// </summary>
    StructType? ExistentialLayout(NamedTypeSymbol protocol) {
        var name = $"any${TypeName(protocol)}";
        if (_module.FindType(name) is StructType existing) {
            return existing;
        }

        var layout = new StructType(name);
        var slots = new List<(string, Symbol)>();
        var seen = new HashSet<string>();

        // 'any Producer<Animal>': the definition's requirements, with T read
        // as Animal — each instance of a generic protocol is a layout of its own.
        var arguments = TypeSubstitution.For(protocol);
        protocol = protocol.OriginalDefinition;
        var self = protocol.SelfTypeParameter;

        bool MentionsSelf(TypeSymbol type) =>
            self is not null && (type.Equals(self) || type is NamedTypeSymbol { TypeArguments: var arguments } && arguments.Any(MentionsSelf)
                || type is ArrayTypeSymbol array && MentionsSelf(array.ElementType)
                || type is OptionalTypeSymbol optional && MentionsSelf(optional.UnderlyingType));

        void Collect(NamedTypeSymbol current) {
            foreach (var member in current.Members) {
                switch (member) {
                    case FunctionSymbol { IsStatic: false, IsMutating: false, FunctionKind: FunctionKind.Ordinary, TypeParameters.IsEmpty: true } method
                        when seen.Add(SlotName(method)) && !method.Parameters.Any(p => MentionsSelf(p.Type) || p.Modifier != ParameterModifier.None)
                             && !MentionsSelf(method.ReturnType): {
                        if (Lower(arguments.Apply(new FunctionTypeSymbol([..method.Parameters.Select(static p => p.Type)], method.ReturnType, false, method.ThrownType))) is ThickFunctionType thick) {
                            layout.Fields.Add((SlotName(method), thick));
                            slots.Add((SlotName(method), method));
                        }

                        break;
                    }

                    case FieldSymbol { IsStatic: false } field when seen.Add(GetterSlot(field)) && !MentionsSelf(field.Type):
                        if (Lower(new FunctionTypeSymbol([], arguments.Apply(field.Type))) is ThickFunctionType getter) {
                            layout.Fields.Add((GetterSlot(field), getter));
                            slots.Add((GetterSlot(field), field));
                        }

                        break;
                }
            }

            foreach (var inherited in current.BaseTypes.OfType<NamedTypeSymbol>().Where(static b => b.TypeKind == TypeKind.Protocol)) {
                // 'protocol Sub<U>: Source<U>' reaches Source's requirements
                // with U's argument in place of Source's T.
                if (inherited.IsConstructed) {
                    var through = TypeSubstitution.For(inherited);
                    var outer = arguments;
                    arguments = new TypeSubstitution(inherited.OriginalDefinition.TypeParameters.ToDictionary(static p => p, p => outer.Apply(through.Apply(p))));
                    Collect(inherited.OriginalDefinition);
                    arguments = outer;
                } else {
                    Collect(inherited);
                }
            }
        }

        Collect(protocol);
        _module.AddType(layout);
        _existentials[layout] = slots;
        return layout;
    }

    internal List<(string Slot, Symbol Member)> ExistentialSlots(StructType layout) => _existentials[layout];

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
        if (member.ContainingSymbol is not ExtensionSymbol { ExtendedType: NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol }) {
            return false;
        }

        bool Meets(Symbol requirement) =>
            requirement.ContainingSymbol is NamedTypeSymbol
            && requirement.Name == member.Name
            && (requirement, member) switch {
                (FunctionSymbol r, FunctionSymbol m) =>
                    (r.IsStatic == m.IsStatic || r.FunctionKind == FunctionKind.Operator) && r.Parameters.Length == m.Parameters.Length,
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
    static bool SameLabels(FunctionSymbol a, FunctionSymbol b) =>
        a.Parameters.Length == b.Parameters.Length
        && a.Parameters.Zip(b.Parameters).All(static p => (p.First.Label ?? p.First.Name) == (p.Second.Label ?? p.Second.Name));

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
                        || ReferenceEquals((f.ExplicitProtocol as NamedTypeSymbol)?.OriginalDefinition, required)))
                .OrderBy(f => f.ExplicitProtocol is null)
                .ThenBy(f => !SameLabels(f, requirement))
                .FirstOrDefault();
            if (found is not null) {
                return found;
            }
        }

        // None of its own: the default an extension of the protocol gives.
        if (Owner(requirement) is { TypeKind: TypeKind.Protocol } protocol) {
            return protocol.OriginalDefinition.Members.OfType<FunctionSymbol>().FirstOrDefault(f =>
                f.ContainingSymbol is ExtensionSymbol
                && f.Name == requirement.Name
                && f.IsStatic == requirement.IsStatic
                && f.Parameters.Length == requirement.Parameters.Length);
        }

        return null;
    }

    /// <summary>
    ///     The code an existential's slot holds for one concrete type: it opens
    ///     the box, and calls the type's member — through the vtable if a
    ///     subclass may override it — or the protocol's default, or reads the
    ///     field. Null when the requirement cannot be met that way yet.
    /// </summary>
    internal (string Name, FunctionType Type)? WitnessThunk(NamedTypeSymbol concrete, SilType value, ClassType box, string slot, Symbol requirement, ThickFunctionType signature, SyntaxNode at) {
        var name = $"$witness${Sanitise(value.ToString())}${slot}";
        var code = signature.Implementation;
        if (_module.Find(name) is not null) {
            return (name, code);
        }

        var thunk = new SilFunction(name, code);
        var entry = thunk.AddBlock();
        var arguments = signature.Signature.Parameters.Select(p => (SilValue)entry.AddArgument(p)).ToList();
        var context = entry.AddArgument(SilType.Object);
        var opened = entry.Append(new RefElementAddrInst(entry.Append(new UncheckedRefCastInst(context, box)), 0));

        // A property an extension of the protocol adds, and no requirement:
        // its own getter, whatever the type declares (docs/03 §3.2).
        if (requirement is FieldSymbol { IsComputed: true } extensionOnly && IsExtensionOnly(extensionOnly) && GetterOf(extensionOnly) is { } extensionGetter) {
            requirement = extensionGetter;
        } else if (requirement is FieldSymbol computed
            && FindField(concrete, computed.Name) is { IsComputed: true } own && GetterOf(own) is { } getter) {
            requirement = getter;
        } else if (requirement is FieldSymbol { IsComputed: true } defaulted
                   && FindField(concrete, defaulted.Name) is null && GetterOf(defaulted) is { } defaultGetter) {
            // None of its own: the getter the protocol gives by default.
            requirement = defaultGetter;
        }

        if (requirement is FieldSymbol field) {
            SilValue read;
            switch (value) {
                case StructType structType when structType.FieldIndex(field.Name) is var index and >= 0:
                    read = entry.Append(new LoadInst(entry.Append(new StructElementAddrInst(opened, index))));
                    break;

                case ClassType classType when classType.FieldIndex(field.Name) is var index and >= 0: {
                    var instance = entry.Append(new LoadInst(opened));
                    var fieldAddress = entry.Append(new RefElementAddrInst(instance, index));
                    read = ((AddressType)fieldAddress.Type).Pointee switch {
                        WeakStorageType => entry.Append(new LoadWeakInst(fieldAddress)),
                        UnownedStorageType => entry.Append(new LoadUnownedInst(fieldAddress)),
                        _ => entry.Append(new LoadInst(fieldAddress)),
                    };
                    entry.Append(new DestroyValueInst(instance));
                    break;
                }

                default:
                    Unsupported<object>(at, $"'{field.Name}' of '{concrete.ToDisplayString()}' through an existential");
                    return null;
            }

            entry.Append(new ReturnInst(read));
            _module.Add(thunk);
            return (name, code);
        }

        var method = (FunctionSymbol)requirement;
        string target;
        FunctionType? targetType;
        var implementation = method.FunctionKind == FunctionKind.Accessor ? method : Implementation(concrete, method);
        if (implementation is not null && !IsGeneric(implementation) && _names.TryGetValue(implementation, out var implementationName)) {
            target = implementationName;
            targetType = _module.Find(target)?.Type ?? SignatureOf(implementation, at, TypeSubstitution.Empty);
        } else if (implementation is { ContainingSymbol: NamedTypeSymbol, TypeParameters.IsEmpty: true }
                   && ConstructedOwner(concrete, implementation) is { } owner
                   && Instance(implementation, TypeSubstitution.For(owner), at) is { } generic) {
            // A method of a generic type, 'G<Int>' boxed as 'any P': its
            // instance for the type arguments the boxed value has.
            (target, targetType) = generic;
        } else if ((implementation is { ContainingSymbol: ExtensionSymbol }
                    || (implementation is { FunctionKind: FunctionKind.Accessor } && Owner(implementation) is { TypeKind: TypeKind.Protocol })
                        ? implementation
                        : implementation is null ? method : null) is { } fallback
                   && _declarations.ContainsKey(fallback) && Owner(fallback)!.SelfTypeParameter is { } self
                   && Instance(fallback, new(new() { [self] = concrete }), at) is { } instance) {
            // The protocol's default, for this type: an instance with Self bound.
            (target, targetType) = instance;
            implementation = null;
        } else {
            Unsupported<object>(at, $"'{method.Name}' of '{concrete.ToDisplayString()}' through an existential");
            return null;
        }

        if (targetType is null || targetType.Parameters.Length == 0 || targetType.Parameters[0] is AddressType) {
            Unsupported<object>(at, $"'{method.Name}' of '{concrete.ToDisplayString()}' through an existential");
            return null;
        }

        var selfValue = entry.Append(new LoadInst(opened));
        SilValue receiver = selfValue;
        if (selfValue.Type is ClassType selfClass && targetType.Parameters[0] is ClassType parameterClass && selfClass != parameterClass) {
            receiver = entry.Append(new UpcastInst(selfValue, parameterClass));
        }

        // An 'in' parameter of a variant existential: the slot takes a Dog
        // where the witness takes any Animal, which a Dog is.
        for (var i = 0; i < arguments.Count && i + 1 < targetType.Parameters.Length; i++) {
            if (arguments[i].Type is ClassType given && targetType.Parameters[i + 1] is ClassType taken && given != taken) {
                arguments[i] = entry.Append(new UpcastInst(arguments[i], taken));
            }
        }

        thunk.TrampolineTarget = target;
        SilValue callee = implementation is not null && IsDynamic(implementation)
            ? entry.Append(new ClassMethodInst(receiver, SlotName(implementation), targetType))
            : entry.Append(new FunctionRefInst(target, targetType));

        void Release(SilBlock block) {
            if (!selfValue.Type.IsTrivial) {
                block.Append(new DestroyValueInst(selfValue));
            }
        }

        // An 'out' result of a variant existential: the witness gives a Dog
        // where the slot promises an Animal.
        var promised = signature.Signature.Result;
        if (targetType.Thrown is null) {
            var result = entry.Append(new ApplyInst(callee, targetType.Result, [receiver, ..arguments]));
            Release(entry);
            var block = entry;
            SilValue? returned = null;
            if (targetType.Result != SilType.Unit && (returned = Upcast(thunk, ref block, result, promised)) is null) {
                Unsupported<object>(at, $"'{method.Name}' of '{concrete.ToDisplayString()}' through an existential, as a '{promised}'");
                return null;
            }

            block.Append(new ReturnInst(returned));
        } else {
            var normal = thunk.AddBlock();
            var result = targetType.Result == SilType.Unit ? null : normal.AddArgument(targetType.Result);
            var failed = thunk.AddBlock();
            var error = failed.AddArgument(targetType.Thrown);
            entry.Append(new TryApplyInst(callee, targetType.Result, [receiver, ..arguments], normal, failed));
            Release(normal);
            var block = normal;
            SilValue? returned = null;
            if (result is not null && (returned = Upcast(thunk, ref block, result, promised)) is null) {
                Unsupported<object>(at, $"'{method.Name}' of '{concrete.ToDisplayString()}' through an existential, as a '{promised}'");
                return null;
            }

            block.Append(new ReturnInst(returned));

            Release(failed);
            failed.Append(new ThrowInst(error));
        }

        _module.Add(thunk);
        return (name, code);
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

    /// <summary>
    ///     An owned value of a witness's type as the type a variant
    ///     existential's slot promises (docs/03 §3.4): an object as its
    ///     superclass, or an optional of one as an optional of the other. The
    ///     instructions go into <paramref name="block" />, which ends as the
    ///     block the converted value is in; null when there is no such
    ///     conversion.
    /// </summary>
    static SilValue? Upcast(SilFunction function, ref SilBlock block, SilValue value, SilType to) {
        if (value.Type == to) {
            return value;
        }

        if (value.Type is ClassType && to is ClassType target) {
            var upcast = block.Append(new CopyValueInst(block.Append(new UpcastInst(value, target))));
            block.Append(new DestroyValueInst(value));
            return upcast;
        }

        if (value.Type is not EnumType { Cases: [("Some", { } wrapped), ("None", null)] } from
            || to is not EnumType { Cases: [("Some", { } wanted), ("None", null)] } into) {
            return null;
        }

        var joined = function.AddBlock();
        var result = joined.AddArgument(to);

        var some = function.AddBlock();
        var payload = some.AddArgument(wrapped, !wrapped.IsTrivial);
        SilValue owned = wrapped.IsTrivial ? payload : some.Append(new CopyValueInst(payload));
        if (!from.IsTrivial) {
            some.Append(new DestroyValueInst(value));
        }

        var current = some;
        if (Upcast(function, ref current, owned, wanted) is not { } converted) {
            return null;
        }

        current.Append(new BranchInst(joined, current.Append(new EnumInst(into, 0, converted))));

        var none = function.AddBlock();
        if (!from.IsTrivial) {
            none.Append(new DestroyValueInst(value));
        }

        none.Append(new BranchInst(joined, none.Append(new EnumInst(into, 1, null))));

        block.Append(new SwitchEnumInst(value, [(0, some), (1, none)], null));
        block = joined;
        return result;
    }

    /// <summary>The cases of each union whose payload is boxed, by index.</summary>
    readonly Dictionary<EnumType, HashSet<int>> _indirect = new(ReferenceEqualityComparer.Instance);

    /// <summary>True when a case's payload is in a box: the box's one field holds it.</summary>
    internal bool IsIndirect(EnumType type, int index) => _indirect.TryGetValue(type, out var boxed) && boxed.Contains(index);

    static bool IsMarkedIndirect(NamedTypeSymbol union, string caseName) =>
        union.Members.OfType<UnionCaseSymbol>().FirstOrDefault(c => c.Name == caseName)?.DeclaringSyntax.FirstOrDefault()
            is UnionCaseDeclarationSyntax declaration
        && declaration.Attributes.Any(static a => a.Name.ToString().Trim() == "indirect");

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
        var name = $"{type.OriginalDefinition.ContainingSymbol?.Name}.{type.Name}";
        if (!type.IsConstructed) {
            return name;
        }

        var arguments = type.TypeArguments.Select(a => Lower(a)?.ToString() ?? a.ToDisplayString());
        return $"{name}$of${string.Join("$", arguments.Select(Sanitise))}";
    }

    /// <summary>
    ///     The protocols a type declares it conforms to — where it is declared
    ///     and in its extensions that hold for every instance — with those
    ///     they refine, by qualified name: <c>M.Named</c>.
    /// </summary>
    static IEnumerable<string> DeclaredProtocols(NamedTypeSymbol type) {
        var pending = new Queue<TypeSymbol>(type.BaseTypes);
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

            var name = $"{protocol.OriginalDefinition.ContainingSymbol?.Name}.{protocol.Name}";
            if (!seen.Add(name)) {
                continue;
            }

            yield return name;
            foreach (var refined in protocol.BaseTypes) {
                pending.Enqueue(refined);
            }
        }
    }

    internal static IEnumerable<FieldSymbol> StoredFields(NamedTypeSymbol type) =>
        type.Members.OfType<FieldSymbol>().Where(static f => f is { IsStatic: false, IsComputed: false });

    internal void Report(ErrorCode code, Sable.Syntax.TextSpan span, string message) => _diagnostics.Error(code, span, message);

    /// <summary>A stored property's initial value, bound: from the model of the file that declares it.</summary>
    internal BoundExpression? FieldInitializer(FieldSymbol field) {
        if (field.DeclaringSyntax.FirstOrDefault() is not { } syntax) {
            return null;
        }

        var root = syntax;
        while (root.Parent is { } parent) {
            root = parent;
        }

        var tree = _compilation.SyntaxTrees.FirstOrDefault(t => ReferenceEquals(t.Root, root));
        return tree is null ? null : _compilation.GetSemanticModel(tree).GetFieldInitializer(field);
    }

    T? Unsupported<T>(SyntaxNode at, string what) where T : class {
        _diagnostics.Error(ErrorCode.ERR_NotLoweredYet, at.Span, $"SIL lowering does not handle {what} yet");
        return null;
    }

    // =======================================================================

    /// <summary>What a closure's code knows of its context: its class, and where each capture is in it.</summary>
    sealed record ClosureInfo(ClassType? Context, Dictionary<object, Capture> Captures);

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

            // Initialising an object: it comes first, already allocated.
            _initializing = symbol.FunctionKind == FunctionKind.Initializer && function.Type.Result == SilType.Unit
                && parameterTypes.Length > 0 && parameterTypes[0] is ClassType;

            if (HasSelf(symbol) || _initializing) {
                var self = _entry.AddArgument(parameterTypes[next++]);
                if (self.Type is AddressType) {
                    _selfAddress = self;
                } else {
                    _selfValue = self;
                }
            }

            foreach (var parameter in symbol.Parameters) {
                _parameters[parameter] = _entry.AddArgument(parameterTypes[next++]);
            }

            _current = _entry;

            // A move-only parameter the function owns lives in a slot, like a
            // local, so that moving it out is checked as a local's move is.
            for (var i = 0; i < symbol.Parameters.Length; i++) {
                var argument = (SilArgument)_parameters[symbol.Parameters[i]];
                if (function.Type.IsOwned(_entry.Arguments.IndexOf(argument)) && argument.Type is NominalType { IsNoncopyable: true }) {
                    var slot = NewSlot(argument.Type, symbol.Parameters[i].Name);
                    Emit(new StoreInst(argument, slot, StoreKind.Assign));
                    _spilled[symbol.Parameters[i]] = slot;
                    _spilledArguments.Add(argument);
                    _parameters.Remove(symbol.Parameters[i]);
                }
            }

            _current = _entry;
            _boxed = generator.BoxedLocals(symbol, body);

            // A closure's code takes its context last.
            if (closure is not null) {
                var context = _entry.AddArgument(SilType.Object);
                if (closure.Context is { } contextType) {
                    _context = Emit(new UncheckedRefCastInst(context, contextType));
                }
            }

            // A struct's initialiser builds 'self' in a slot of its own, field
            // by field.
            if (symbol.FunctionKind == FunctionKind.Initializer && !_initializing) {
                _selfAddress = NewSlot(function.Type.Result, "self");
            }

            // The entry point first gives every global its initial value.
            if (ReferenceEquals(symbol, generator._entrySymbol) && generator._globalFields.Count > 0 && function.Name != GlobalsInit) {
                var unit = new FunctionType([], SilType.Unit);
                Emit(new ApplyInst(Emit(new FunctionRefInst(GlobalsInit, unit)), SilType.Unit));
            }

            // 'var name = "x"': every initialiser of the type that declares it
            // stores the initial value first, before its own body runs — but
            // one that hands the work to another, 'self.init(…)', leaves it
            // to that one.
            if (symbol.FunctionKind == FunctionKind.Initializer && !Delegates(body)
                && (symbol.ContainingSymbol is ExtensionSymbol extension ? extension.ExtendedType : symbol.ContainingSymbol) is NamedTypeSymbol owner
                && SelfType(symbol, substitution) is { } selfType) {
                foreach (var field in owner.OriginalDefinition.Members.OfType<FieldSymbol>()) {
                    if (field is { IsStatic: false, IsComputed: false, HasInitializer: true } && generator.FieldInitializer(field) is { } initial) {
                        var self = new BoundSelfAccess(initial.Syntax, selfType, true);
                        Statement(new BoundExpressionStatement(initial.Syntax, new BoundAssignment(initial.Syntax, new BoundFieldAccess(initial.Syntax, self, field), initial)));
                    }
                }
            }

            _body = body;
            Statement(body);

            // Falling off the end: fine for a function returning nothing, and
            // for an initialiser, which returns what it built. For one with a
            // result the binder has said every path returns, so the end
            // cannot be reached.
            if (_current is not null) {
                if (_initializing) {
                    Return(null);
                } else if (symbol.FunctionKind == FunctionKind.Initializer) {
                    Return(TakeSelf());
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
                DestroyOwnedParameters();
                Deallocate();
                // The program ends once every task has, and then its globals go.
                if (ReferenceEquals(symbol, generator._entrySymbol) && function.Name != GlobalsInit) {
                    Emit(new BuiltinInst("task_drain", SilType.Unit));
                }

                if (ReferenceEquals(symbol, generator._entrySymbol) && generator._globalFields.Count > 0 && function.Name != GlobalsInit) {
                    var unit = new FunctionType([], SilType.Unit);
                    Emit(new ApplyInst(Emit(new FunctionRefInst(GlobalsDeinit, unit)), SilType.Unit));
                }

                Jump(new ReturnInst(returnBlock.Arguments.Count == 0 ? null : returnBlock.Arguments[0]));
            }

            if (_throwBlock is { } throwBlock) {
                _current = throwBlock;
                DestroyOwnedParameters();
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

        /// <summary>What an initialiser returns: the value or object it built, taken out of its slot.</summary>
        SilValue TakeSelf() {
            var slot = _selfAddress!;
            var type = ((AddressType)slot.Type).Pointee;
            return Emit(new LoadInst(slot, type.IsTrivial ? LoadKind.Trivial : LoadKind.Take));
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
                if (!_allocations[i].ElementType.IsTrivial && !_scoped.Contains(_allocations[i])) {
                    Emit(new DestroyAddrInst(_allocations[i]));
                }
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

            // A narrowed read is the payload, taken out: a value of its own.
            if (NarrowedUnion(expression) is not null) {
                return Expression(expression) is { } payload ? Hold(payload) : null;
            }

            switch (expression) {
                case BoundParameterAccess access when _parameters.TryGetValue(access.Parameter, out var parameter) && parameter.Type is not AddressType:
                    return parameter;

                case BoundSelfAccess when _selfValue is not null:
                    return _selfValue;

                // 'super': this object, seen as its superclass.
                case BoundSuperAccess super when _selfValue is not null && LowerType(super.Type) is ClassType superclass:
                    return Emit(new UpcastInst(_selfValue, superclass));

                case BoundConversion { ConversionKind: ConversionKind.Identity } conversion:
                    return Borrow(conversion.Operand);

                case BoundConversion { ConversionKind: ConversionKind.Existential, Type: OpaqueTypeSymbol } opened:
                    return Borrow(opened.Operand);

                // A stored field of something borrowed is itself borrowed.
                case BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } field }
                    when Sub(receiver.Type).TypeKind == TypeKind.Struct && IsBorrowable(receiver) && !IsInlineType(receiver.Type): {
                    if (Borrow(receiver) is not { Type: StructType structType } value) {
                        return null;
                    }

                    var index = structType.FieldIndex(field.Name);
                    return index < 0 ? Missing(expression, $"the member '{field.Name}'") : Emit(new StructExtractInst(value, index));
                }

                default:
                    return Expression(expression) is { } owned ? Hold(owned) : null;
            }
        }

        /// <summary>
        ///     True for a parameter passed by address — inout, out or ref — whose
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

        static bool IsByAddressModifier(ParameterSymbol parameter) => parameter.Modifier is ParameterModifier.InOut or ParameterModifier.Out or ParameterModifier.Ref;

        /// <summary>The slot of a move-only local or parameter, or null.</summary>
        AllocStackInst? MoveOnlyPlace(BoundExpression expression) =>
            expression switch {
                BoundLocalAccess local when _slots.TryGetValue(local.Local, out var slot) && !_boxed.Contains(local.Local)
                    && slot.ElementType is NominalType { IsNoncopyable: true } => slot,
                BoundParameterAccess parameter when _spilled.TryGetValue(parameter.Parameter, out var spilled) => spilled,
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
                BoundConversion { ConversionKind: ConversionKind.Identity } conversion => IsBorrowable(conversion.Operand),
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

        /// <summary>A payload argument for a <c>switch_enum</c> case block: borrowed from the enum.</summary>
        static SilArgument Payload(SilBlock block, SilType type) => block.AddArgument(type, !type.IsTrivial);

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
            // A return from inside an expression leaves its temporaries behind.
            DestroyTemporaries(0);
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
                Branch(_returnBlock, Owned(value));
            }
        }

        /// <summary>Sends an error to the innermost handler, running the defers on the way.</summary>
        void RouteError(SilValue error, SyntaxNode at) {
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

                case BoundExpressionStatement expression:
                    if (Expression(expression.Expression) is { } discarded) {
                        Destroy(discarded);
                    }

                    return;

                case BoundReturn returnStatement: {
                    if (returnStatement.Value is { } returned) {
                        if (Expression(returned) is { } value) {
                            Return(value);
                        }
                    } else if (symbol.FunctionKind == FunctionKind.Initializer && !_initializing) {
                        Return(TakeSelf());
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

                case BoundForEach loop when Core.IsCountableRange(Sub(loop.Sequence.Type)):
                    ForRangeValue(loop);
                    return;

                case BoundForEach { Sequence.Type: ArrayTypeSymbol } loop when IsInlineArray(loop.Sequence):
                    ForInline(loop);
                    return;

                case BoundForEach { Sequence.Type: ArrayTypeSymbol } loop:
                    ForArray(loop);
                    return;

                // A dictionary's entries are an array of key and value pairs.
                case BoundForEach loop when IsDictionary(loop.Sequence.Type):
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
                var inside = Emit(new BuiltinInst(compare, SilType.Bool, current, upper));
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
                var atEnd = Emit(new BuiltinInst($"cmp_eq_{type}", SilType.Bool, last, upper));
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
        ///     <c>for x in array</c>: a counting loop over the array, which is
        ///     kept in a slot of its own so that every way out of the loop
        ///     releases it with the rest.
        /// </summary>
        void ForArray(BoundForEach loop) {
            if (loop.Element is null || Expression(loop.Sequence) is not { Type: ArrayType arrayType } sequence
                || SlotFor(loop.Element, loop.Syntax) is null) {
                Fail(loop.Syntax, "this 'for' over an array");
                return;
            }

            var array = NewSlot(arrayType);
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
            if (!arrayType.Element.IsTrivial && !_boxed.Contains(loop.Element) && closure is null && !Moves(loop.Body, loop.Element)
                && arrayType.Element is not NominalType { IsNoncopyable: true } && SlotFor(loop.Element, loop.Syntax) is { } elementSlot) {
                elementSlot.Unmanaged = true;
                var lent = Emit(new LoadBorrowInst(array));
                var borrowed = Emit(new BuiltinInst("array_get_borrowed", arrayType.Element, lent, Emit(new LoadInst(counter))));
                Emit(new StoreInst(borrowed, elementSlot, StoreKind.Assign));
                Emit(new EndBorrowInst(lent));
            } else {
                var again = Emit(new LoadInst(array));
                var value = Emit(new BuiltinInst("array_get", arrayType.Element, again, Emit(new LoadInst(counter))));
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
            Emit(new StoreInst(Emit(new BuiltinInst("array_empty", arrayType)), array, StoreKind.Assign));
        }

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
                    Jump(new CondBranchInst(equal, Destroying(subject, bodies[i]), [], next, []));
                    _current = next;
                }
            }

            Unhold(subject);
            Destroy(subject);
            Branch(defaultBody ?? after);

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
                    if (Expression(binding.Source) is not { } optional || SlotFor(local, binding.Syntax) is null) {
                        return;
                    }

                    var optionalType = optional.Type as EnumType;
                    var some = optionalType?.CaseIndex("Some") ?? -1;
                    if (optionalType is null || some < 0) {
                        Fail(binding.Syntax, "an 'if let' on something that is not optional");
                        return;
                    }

                    var bound = function.AddBlock();
                    var payload = Payload(bound, optionalType.Cases[some].Payload!);
                    Jump(new SwitchEnumInst(optional, [(some, bound)], Destroying(optional, whenFalse)));
                    _current = bound;
                    BindLocal(local, payload, binding.Syntax);
                    Destroy(optional);
                    Branch(whenTrue);
                    return;
                }

                // 'if case .Circle(let r) = shape'.
                case BoundConditionBinding { Pattern: { } pattern } binding: {
                    if (Expression(binding.Source) is { } value) {
                        Hold(value);
                        Pattern(pattern, value, Destroying(value, whenTrue), Destroying(value, whenFalse));
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

                case BoundArrayPattern array when value.Type is ArrayType arrayType:
                    ArrayPattern(array, value, arrayType, matched, failed);
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
                    var payload = Payload(target, optional.Cases[index].Payload!);
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

                default:
                    Fail(pattern.Syntax, $"the pattern '{Describe(pattern.Syntax)}'");
                    return;
            }
        }

        /// <summary>
        ///     <c>[a, b]</c> matches an array of exactly two elements;
        ///     <c>[first, ...rest, last]</c> one of at least two, whose middle
        ///     <c>rest</c> is a copy of. The elements are borrowed where they
        ///     are, as the array is held for as long as the patterns look; the
        ///     middle is taken last, once every element has matched, so a
        ///     failed match has nothing to give back.
        /// </summary>
        void ArrayPattern(BoundArrayPattern array, SilValue value, ArrayType arrayType, SilBlock matched, SilBlock failed) {
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
                Emit(new BuiltinInst("array_get_borrowed", arrayType.Element, value, index()));

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
                    () => Emit(new BuiltinInst("array_slice", arrayType, value, FromStart(prefix), FromEnd(suffix)))));
            }

            Sequence(parts, matched, failed);
        }

        /// <summary>A union case's sub-patterns, against its payload: one value, or a tuple of them.</summary>
        void PayloadPattern(BoundUnionCasePattern unionCase, SilValue payload, SilBlock matched, SilBlock failed) {
            if (unionCase.Payload.Length == 1 && payload.Type is not TupleType) {
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

                case BoundConversion { ConversionKind: ConversionKind.Identity } conversion:
                    return Expression(conversion.Operand);

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

                // 'x as? Dog' and 'x as! Dog': a downcast, checked as the program runs.
                case BoundConversion { ConversionKind: ConversionKind.Explicit } downcast when IsDowncast(downcast):
                    return Downcast(downcast);

                case BoundTypeTest test:
                    return TypeTest(test);

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

                case BoundLocalAccess access:
                    return LoadLocal(access);

                case BoundParameterAccess access when closure?.Captures.TryGetValue(access.Parameter, out var captured) == true:
                    return Emit(new LoadInst(Emit(new RefElementAddrInst(_context!, captured.Field))));

                case BoundClosure closureExpression:
                    return Closure(closureExpression);

                case BoundFunctionReference reference:
                    return FunctionValue(reference);

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
                    return Assignment(assignment);

                // 'task.value', Sable.Core's @intrinsic async getter.
                case BoundFieldAccess { Field: { Name: "value", IsAsyncGetter: true, ContainingSymbol: var task } } taskValue
                    when task is not null && ReferenceEquals(task, Core.Task):
                    return TaskValue(taskValue);

                // A constant: its value, computed as the program was compiled.
                case BoundFieldAccess { Field: var constantField } when generator.ConstantRead(constantField) is { } constant
                    && LowerType(constantField.Type) is { } constantType:
                    return Constant(constant, constantType, expression.Syntax);

                // A global: read where it lives.
                case BoundFieldAccess { Field: var globalField } when SilGen.IsGlobal(globalField) && generator.GlobalOf(globalField) is var (globalName, globalType):
                    return Emit(new LoadInst(Emit(new GlobalAddrInst(globalName, globalType))));

                case BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: true, Name: var member } } when IsInlineArray(receiver):
                    return InlineArrayMember(receiver, member, expression);

                case BoundFieldAccess { Field.Builtin: not null } builtin:
                    return BuiltinProperty(builtin);

                // 'F64.pi': a static property's getter, called with nothing.
                case BoundFieldAccess { Field: { IsComputed: true, IsStatic: true } } access when generator.GetterOf(access.Field) is { } staticGetter:
                    return Expression(new BoundCall(access.Syntax, null, staticGetter, [], access.Type));

                case BoundFieldAccess { Receiver: { } receiver, Field.IsComputed: true } access:
                    return ComputedProperty(access, receiver);

                case BoundFieldAccess access when IsInlineMember(access):
                    return InlineRead(access);

                case BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } field } access:
                    return FieldAccess(access, receiver, field);

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

                case BoundBinaryOperator binary when binary.Method is null:
                    return Binary(binary);

                // 'await x': x. Where it waits, the runtime suspends the task.
                case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Await, Operand: var awaited }:
                    return Expression(awaited);

                // An operator of the program's own: a call to it.
                case BoundBinaryOperator { Method: { } binaryMethod } userBinary:
                    return UserBinary(userBinary, binaryMethod);

                case BoundUnaryOperator { Method: { } unaryMethod } userUnary:
                    return Expression(new BoundCall(userUnary.Syntax, null, OperatorWitness(unaryMethod, userUnary.Operand.Type) ?? unaryMethod, [userUnary.Operand], userUnary.Type));

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

                case BoundIfExpression ifExpression:
                    return IfValue(ifExpression);

                case BoundTry tryExpression:
                    return Try(tryExpression);

                case BoundCall call:
                    return Call(call);

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
                // A closure is one value, and a generic function is code for
                // each set of type arguments: one that captures has no one
                // value to be.
                if (generator.Captures(callee)) {
                    Fail(local.Syntax, $"the generic local function '{callee.Name}', which captures");
                    return;
                }

                // One that does not is instantiated for each call's type
                // arguments, as a generic function of the module is — with
                // this instance's for the type parameters around it.
                generator.DeclareLocalGeneric(callee, local.Body, generator.LocalFunctionName(function.Name, callee.Name), source);
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
                    if (Address(call.Arguments[i], borrows) is not { } address) {
                        return null;
                    }

                    arguments.Add(address);
                    BeginAccess(address, modify: true, borrows);
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

        SilValue? Assignment(BoundAssignment assignment) {
            if (assignment.Target is BoundFieldAccess { Receiver: { } chainedFrom } chainedTarget
                && IsChainedThrough(chainedFrom, chainedTarget.Field)) {
                return ChainedAssignment(assignment, chainedTarget, chainedFrom);
            }

            if (IsInlineMember(assignment.Target)
                || assignment.Target is BoundIndexAccess { Receiver: var indexed } && IsInlineArray(indexed)) {
                return InlineAssign(assignment.Target, assignment.Value);
            }

            if (ArrayElementWithin(assignment.Target) is { } element) {
                return ElementPartAssignment(assignment, element);
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

            // 'd[k] = v' sets; 'd[k] = none' removes.
            if (assignment.Target is BoundIndexAccess { Indices: [var keyExpression] } dictionaryAccess
                && IsDictionary(dictionaryAccess.Receiver.Type)) {
                Unhold(value);
                return DictionaryStore(dictionaryAccess, keyExpression, value);
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

                default:
                    Emit(new StoreInst(value, address, StoreKind.Assign));
                    break;
            }

            EndAll(borrows);
            return value.Type.IsTrivial ? value : UnitValue.Instance;
        }

        /// <summary>
        ///     The array element a store reaches into, through value fields
        ///     and tuple elements — 'a[i]' in 'a[i].p.x = v' — or null when the
        ///     target is not inside one. An element has no address of its own,
        ///     and an object's field is reached through the reference instead.
        /// </summary>
        BoundIndexAccess? ArrayElementWithin(BoundExpression target) {
            var current = target switch {
                BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } } => receiver,
                BoundTupleElementAccess element => element.Receiver,
                _ => null,
            };

            while (current is not null) {
                switch (current) {
                    case BoundIndexAccess { Indices: [_] } index when Sub(index.Receiver.Type) is ArrayTypeSymbol && !IsInlineArray(index.Receiver):
                        return index;

                    case BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } }
                        when Sub(receiver.Type).TypeKind is not (TypeKind.Class or TypeKind.Actor) && !IsInlineType(receiver.Type):
                        current = receiver;
                        break;

                    case BoundTupleElementAccess element:
                        current = element.Receiver;
                        break;

                    default:
                        return null;
                }
            }

            return null;
        }

        /// <summary>
        ///     'a[i].x = v': the element read out into a slot of its own, the
        ///     store made there, and the element written back — one 'array_set',
        ///     which makes the array unique first, as 'a[i] = e' does.
        /// </summary>
        SilValue? ElementPartAssignment(BoundAssignment assignment, BoundIndexAccess element) {
            var ends = new List<SilValue>();
            if (Address(element.Receiver, ends) is not { Type: AddressType { Pointee: ArrayType arrayType } } arrayAddress
                || Expression(element.Indices[0]) is not { } position) {
                return null;
            }

            var array = Emit(new LoadBorrowInst(arrayAddress));
            var current = Emit(new BuiltinInst("array_get", arrayType.Element, array, position));
            Emit(new EndBorrowInst(array));

            var slot = NewSlot(arrayType.Element, "$element");
            Emit(new StoreInst(current, slot, StoreKind.Init));
            var temporary = new LocalSymbol("$element", Core.Module, null, true);
            _slots[temporary] = slot;

            var within = Replace(assignment.Target, element, new BoundLocalAccess(element.Syntax, temporary, element.Type));
            if (Assignment(new BoundAssignment(assignment.Syntax, within, assignment.Value)) is not { } result) {
                return null;
            }

            Emit(new BuiltinInst("array_set", SilType.Unit, arrayAddress, position, Emit(new LoadInst(slot, LoadKind.Take))));
            EndAll(ends);
            return result;

            static BoundExpression Replace(BoundExpression node, BoundExpression old, BoundExpression replacement) =>
                ReferenceEquals(node, old)
                    ? replacement
                    : node switch {
                        BoundFieldAccess field => new BoundFieldAccess(field.Syntax, Replace(field.Receiver!, old, replacement), field.Field, field.Type),
                        BoundTupleElementAccess part => new BoundTupleElementAccess(part.Syntax, part.Type, Replace(part.Receiver, old, replacement), part.Index),
                        _ => node,
                    };
        }

        bool IsDictionary(TypeSymbol type) => Sub(type) is NamedTypeSymbol named && named.OriginalDefinition.Equals(Core.Dictionary);

        (string Get, string Set, string Remove)? Dictionary(TypeSymbol type, BoundNode at) {
            var named = (NamedTypeSymbol)Sub(type);
            if (LowerType(named) is not ArrayType entries || LowerType(new OptionalTypeSymbol(named.TypeArguments[1])) is not EnumType optional) {
                Missing(at, $"a dictionary of type '{named.ToDisplayString()}'");
                return null;
            }

            var key = named.TypeArguments[0];
            if (SilBuiltins.Find($"cmp_eq_{((TupleType)entries.Element).Elements[0]}") is not null) {
                return generator.DictionaryFunctions(entries, optional);
            }

            if (generator.KeyEqualityFunction(Sub(key), at.Syntax) is not { } equality) {
                Missing(at, $"comparing keys of type '{key.ToDisplayString()}'");
                return null;
            }

            return generator.DictionaryFunctions(entries, optional, equality);
        }

        /// <summary>Stores an optional value under a key: set when it holds one, remove when it is none.</summary>
        SilValue? DictionaryStore(BoundIndexAccess target, BoundExpression keyExpression, SilValue value) {
            if (Dictionary(target.Receiver.Type, target) is not { } functions) {
                return null;
            }

            Hold(value);
            var ends = new List<SilValue>();
            if (Address(target.Receiver, ends) is not { } address || Borrow(keyExpression) is not { } key) {
                return null;
            }

            var optional = (EnumType)value.Type;
            var setType = generator._module.Find(functions.Set)!.Type;
            var removeType = generator._module.Find(functions.Remove)!.Type;
            var present = function.AddBlock();
            var payload = Payload(present, optional.Cases[optional.CaseIndex("Some")].Payload!);
            var absent = function.AddBlock();
            var after = function.AddBlock();
            Jump(new SwitchEnumInst(value, [(optional.CaseIndex("Some"), present)], absent));

            _current = present;
            Emit(new ApplyInst(Emit(new FunctionRefInst(functions.Set, setType)), SilType.Unit, address, key, payload));
            Branch(after);

            _current = absent;
            Emit(new ApplyInst(Emit(new FunctionRefInst(functions.Remove, removeType)), SilType.Unit, address, key));
            Branch(after);

            _current = after;
            End(key);
            End(value);
            EndAll(ends);
            return UnitValue.Instance;
        }

        /// <summary><c>["a": 1]</c>: an empty dictionary in a slot of its own, each entry set, then taken out.</summary>
        SilValue? DictionaryLiteral(BoundDictionary dictionary) {
            if (Dictionary(dictionary.Type, dictionary) is not { } functions || LowerType(dictionary.Type) is not ArrayType entries) {
                return null;
            }

            var empty = Emit(new BuiltinInst("array_empty", entries));
            if (dictionary.Entries.IsEmpty) {
                return empty;
            }

            var slot = NewSlot(entries);
            Emit(new StoreInst(empty, slot, StoreKind.Assign));
            var setType = generator._module.Find(functions.Set)!.Type;
            foreach (var (keyExpression, valueExpression) in dictionary.Entries) {
                if (Evaluate([keyExpression, valueExpression]) is not [var key, var value]) {
                    return null;
                }

                Emit(new ApplyInst(Emit(new FunctionRefInst(functions.Set, setType)), SilType.Unit, slot, key, value));
                Destroy(value);
                Destroy(key);
            }

            return Emit(new LoadInst(slot, LoadKind.Take));
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

                var loaded = ReadField(Emit(new RefElementAddrInst(reference, index)));
                End(reference);
                return loaded;
            }

            // A field of something in memory, read without copying the whole of it.
            if (!IsTrivialType(receiver.Type) && !IsBorrowable(access) && Place(access) is { } place) {
                return Emit(new LoadInst(place));
            }

            if (!IsTrivialType(receiver.Type)) {
                if (Borrow(receiver) is not { Type: StructType borrowedType } borrowed) {
                    return null;
                }

                var borrowedIndex = borrowedType.FieldIndex(field.Name);
                if (borrowedIndex < 0) {
                    return Missing(access, $"the member '{field.Name}'");
                }

                var copy = Owned(Emit(new StructExtractInst(borrowed, borrowedIndex)));
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

                // An element of an array, where it is: 'bump(&values[0])'.
                case BoundIndexAccess { Subscript: null, Indices: [var indexExpression] } element
                    when Sub(element.Receiver.Type) is ArrayTypeSymbol && !IsInlineArray(element.Receiver): {
                    if (Address(element.Receiver, borrows) is not { Type: AddressType { Pointee: ArrayType } } array
                        || Expression(indexExpression) is not { } index) {
                        return null;
                    }

                    return Emit(new BuiltinInst("array_element_addr", SilType.Address(((ArrayType)((AddressType)array.Type).Pointee).Element), array, index));
                }

                default:
                    return Missing(target, $"storing to '{Describe(target.Syntax)}'");
            }
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
                    if (hole >= interpolated.Parts.Length || Hole(interpolated.Parts[hole]) is not { } written) {
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
        ///     as text; a <c>Debug</c> value as its <c>debugDescription()</c>
        ///     says, so a type that describes itself is written that way; and
        ///     anything else as <c>Reflect.describe</c> writes it. Decided for
        ///     the type a generic instance has, so <c>"\(t)"</c> writes a
        ///     <c>T</c> that is a String as a string.
        /// </summary>
        SilValue? Hole(BoundExpression part) {
            if (LowerType(part.Type) is not { } lowered) {
                return Missing(part, $"interpolating a value of type '{part.Type.ToDisplayString()}'");
            }

            // A string, a scalar, and an enum, which writes its case's name.
            if (lowered == SilType.String
                || (lowered is ScalarType && SilBuiltins.Find($"string_from_{lowered}") is not null)
                || Sub(part.Type) is NamedTypeSymbol { TypeKind: TypeKind.Enum }) {
                return Expression(part) is { } value ? Stringify(value, part) : null;
            }

            if (Core.Module.GetType("Debug") is { TypeKind: TypeKind.Protocol } debug
                && debug.GetMembers("debugDescription").OfType<FunctionSymbol>().FirstOrDefault() is { } describe
                && Sub(part.Type).Satisfies(debug)) {
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

            return inner is null ? null : Emit(new EnumInst(optional, optional.CaseIndex("Some"), inner is UnitValue ? null : inner));
        }

        SilValue? Unwrap(BoundConversion conversion) {
            if (Expression(conversion.Operand) is not { Type: EnumType optional } value) {
                return null;
            }

            var some = optional.CaseIndex("Some");
            var present = function.AddBlock();
            var payload = Payload(present, optional.Cases[some].Payload!);
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
            OwnerOf(method) is { TypeKind: TypeKind.Protocol } && Sub(operand) is NamedTypeSymbol concrete
                ? Implementation(concrete, method)
                : method;

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
            // An operator of a generic type takes its type arguments from the operands'.
            var call = new BoundCall(binary.Syntax, null, callee, [first, second], resultType) {
                Substitution = OwnerOf(callee) is { IsGenericDefinition: true } ? TypeSubstitution.For(Sub(first.Type)) : TypeSubstitution.Empty,
            };
            if (Expression(call) is not { } result) {
                return null;
            }

            return negate ? Emit(new BuiltinInst("not_Bool", SilType.Bool, result)) : result;
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

        /// <summary>'x is Dog': whether the object is of the class, or a subclass of it.</summary>
        SilValue? TypeTest(BoundTypeTest test) {
            // A value already known to be of the type: a union's binding that
            // an earlier test, failing, narrowed down to this one case.
            if (Sub(test.Operand.Type).Equals(Sub(test.TestedType))) {
                return Emit(new IntegerLiteralInst(SilType.Bool, 1));
            }

            if (StructuralUnionTypeSymbol.Of(Sub(test.Operand.Type)) is { } union) {
                return UnionTypeTest(test, union);
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

            return Emit(new StructInst(structType, [..fields!]));
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
            SilValue? payload = values.Count switch {
                0 => null,
                1 when payloadType is not TupleType => values[0],
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
        SilValue? Gather(ArrayType arrayType, IEnumerable<BoundExpression> elements) {
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

        /// <summary><c>[a, b]</c>: an empty array in a slot of its own, each element appended, then taken out.</summary>
        SilValue? ArrayLiteral(BoundArray array, TypeSymbol type) {
            if (LowerType(type) is not ArrayType arrayType) {
                return Missing(array, $"an array of type '{type.ToDisplayString()}'");
            }

            var empty = Emit(new BuiltinInst("array_empty", arrayType));
            if (array.Elements.IsEmpty) {
                return empty;
            }

            var slot = NewSlot(arrayType);
            Emit(new StoreInst(empty, slot, StoreKind.Assign));
            foreach (var element in array.Elements) {
                if (Expression(element) is not { } value) {
                    return null;
                }

                Emit(new BuiltinInst("array_append", SilType.Unit, slot, value));
            }

            return Emit(new LoadInst(slot, LoadKind.Take));
        }

        /// <summary>
        ///     A protocol's subscript, used on a T that stands for a known type
        ///     now: that type's own. Any other subscript is itself.
        /// </summary>
        FunctionSymbol SubscriptWitness(FunctionSymbol subscript, BoundExpression receiver) =>
            OwnerOf(subscript) is { TypeKind: TypeKind.Protocol } && Sub(receiver.Type) is NamedTypeSymbol concrete
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

        SilValue? IndexRead(BoundIndexAccess index) {
            // A type's own subscript: a call to its getter.
            if (index.Subscript is { } subscript) {
                subscript = SubscriptWitness(subscript, index.Receiver);
                return generator.SubscriptGetter(subscript) is { } getter
                    ? Expression(new BoundCall(index.Syntax, index.Receiver, getter, index.Indices, index.Type) { Substitution = index.Substitution })
                    : Missing(index, $"indexing '{index.Receiver.Type.ToDisplayString()}'");
            }

            if (index.Indices is [var rangeIndex] && Core.IsAnyRange(Sub(rangeIndex.Type)) && !IsInlineArray(index.Receiver)) {
                return RangeRead(index, rangeIndex);
            }

            if (IsInlineArray(index.Receiver)) {
                return InlineRead(index);
            }

            if (IsDictionary(index.Receiver.Type) && index.Indices is [var keyExpression]) {
                if (Dictionary(index.Receiver.Type, index) is not { } functions
                    || Borrow(index.Receiver) is not { Type: ArrayType } entries) {
                    return null;
                }

                if (Borrow(keyExpression) is not { } key) {
                    End(entries);
                    return null;
                }

                var type = generator._module.Find(functions.Get)!.Type;
                var found = Emit(new ApplyInst(Emit(new FunctionRefInst(functions.Get, type)), type.Result, entries, key));
                End(key);
                End(entries);
                return found;
            }

            if (Sub(index.Receiver.Type) is not ArrayTypeSymbol || index.Indices is not [var indexExpression]) {
                return Missing(index, $"indexing '{index.Receiver.Type.ToDisplayString()}'");
            }

            if (Borrow(index.Receiver) is not { Type: ArrayType arrayType } array) {
                return null;
            }

            if (Expression(indexExpression) is not { } position) {
                return null;
            }

            var element = Emit(new BuiltinInst("array_get", arrayType.Element, array, position));
            End(array);
            return element;
        }

        /// <summary>
        ///     'xs[1..<3]': the elements in the range, as a slice — copied,
        ///     for now, as 'slice(from:to:)' does, trapping where the range
        ///     is not within the array. A range written in place may leave out
        ///     a bound: the start, or the array's count.
        /// </summary>
        SilValue? RangeRead(BoundIndexAccess index, BoundExpression rangeIndex) {
            if (Borrow(index.Receiver) is not { Type: ArrayType arrayType } array) {
                return null;
            }

            SilValue? from, to;
            bool inclusive;
            if (rangeIndex is BoundRange written) {
                from = written.Lower is { } lower ? Expression(lower) : Emit(new IntegerLiteralInst(SilType.Int, 0));
                to = written.Upper is { } upper ? Expression(upper) : Emit(new BuiltinInst("array_count", SilType.Int, array));
                inclusive = written.IsInclusive && written.Upper is not null;
            } else {
                if (Expression(rangeIndex) is not { Type: StructType rangeType } range) {
                    End(array);
                    return null;
                }

                // A one-sided range held in a variable has only one of the
                // fields: the start, or the array's count, stands in for the other.
                from = rangeType.FieldIndex("lowerBound") is var lowerIndex and >= 0
                    ? Emit(new StructExtractInst(range, lowerIndex))
                    : Emit(new IntegerLiteralInst(SilType.Int, 0));
                var upperIndex = rangeType.FieldIndex("upperBound");
                to = upperIndex >= 0
                    ? Emit(new StructExtractInst(range, upperIndex))
                    : Emit(new BuiltinInst("array_count", SilType.Int, array));
                var definition = ((NamedTypeSymbol)Sub(rangeIndex.Type)).OriginalDefinition;
                inclusive = upperIndex >= 0
                    && (ReferenceEquals(definition, Core.ClosedRange) || ReferenceEquals(definition, Core.PartialRangeThrough));
            }

            if (from is null || to is null) {
                End(array);
                return null;
            }

            if (inclusive) {
                to = Emit(new BuiltinInst("add_Int", SilType.Int, to, Emit(new IntegerLiteralInst(SilType.Int, 1))));
            }

            var slice = Emit(new BuiltinInst("array_slice", arrayType, array, from, to));
            End(array);
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
            var payload = Payload(present, optional.Cases[some].Payload!);
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
                Branch(after, flatten ? Owned(reached) : Emit(new EnumInst(resultType, resultType.CaseIndex("Some"), Owned(reached))));
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
        /// </summary>
        SilValue? ChainedAssignment(BoundAssignment assignment, BoundFieldAccess target, BoundExpression chainedFrom) {
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
            var payload = Payload(present, optional.Cases[some].Payload!);
            var absent = function.AddBlock();
            var after = function.AddBlock();
            Jump(new SwitchEnumInst(value, [(some, present)], absent));

            _current = present;
            var temporary = new LocalSymbol("$chained", Core.Module, null, true);
            var slot = NewSlot(payload.Type, "$chained");
            _slots[temporary] = slot;
            BindLocal(temporary, payload, assignment.Syntax, declaration: true);
            Destroy(value);

            var through = new BoundLocalAccess(chainedFrom.Syntax, temporary, unwrappedType);
            var store = new BoundAssignment(assignment.Syntax, new BoundFieldAccess(target.Syntax, through, target.Field, assignment.Value.Type), assignment.Value);
            if (Expression(store) is null) {
                return null;
            }

            if (writesBack) {
                var updated = Emit(new LoadInst(slot));
                Emit(new StoreInst(Emit(new EnumInst(optional, some, updated)), place!, StoreKind.Assign));
            }

            Branch(after);

            _current = absent;
            Destroy(value);
            Branch(after);

            _current = after;
            EndAll(borrows);
            return UnitValue.Instance;
        }

        /// <summary>'a ?? b': a's value when there is one; b, evaluated only then, when there is not.</summary>
        SilValue? Coalesce(BoundBinaryOperator coalesce) {
            if (Expression(coalesce.Left) is not { Type: EnumType optional } value || LowerType(coalesce.Type) is not { } type) {
                return Missing(coalesce, "this '??'");
            }

            var some = optional.CaseIndex("Some");
            var present = function.AddBlock();
            var payload = Payload(present, optional.Cases[some].Payload!);
            var absent = function.AddBlock();
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
        /// <summary>
        ///     Two arrays compared: unequal at once if their counts differ, and
        ///     otherwise element by element, each pair bound to the comparison's
        ///     locals, until one pair differs.
        /// </summary>
        SilValue? ArrayEquals(SilValue left, SilValue right, StructuralComparison byIndex, BoundNode at) {
            if (left.Type is not ArrayType { Element: var element }) {
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
            Jump(new CondBranchInst(sameCount, header, [Emit(new IntegerLiteralInst(SilType.Int, 0))], after, [differ]));

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

            foreach (var arm in match.Arms) {
                var body = function.AddBlock();
                var nextArm = function.AddBlock();

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
                    var guarded = function.AddBlock();
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
                Condition(branch.Condition, consequence, alternative);

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

                return Emit(new EnumInst(wrapped, wrapped.CaseIndex("Some"), plain is UnitValue ? null : plain));
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

            var handler = function.AddBlock();
            var error = handler.AddArgument(errorType);
            var after = function.AddBlock();
            var result = resultType == SilType.Unit ? null : after.AddArgument(resultType);

            _handlers.Add(new(handler, errorType, _scopes.Count, _temporaries.Count));
            var value = Expression(tryExpression.Expression);
            _handlers.RemoveAt(_handlers.Count - 1);

            if (value is not null && _current is not null) {
                if (tryExpression.TryKind == TryKind.Optional) {
                    var optional = (EnumType)resultType;
                    Branch(after, Emit(new EnumInst(optional, optional.CaseIndex("Some"), value is UnitValue ? null : value)));
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

        void CollectThrown(BoundNode node, List<TypeSymbol> into) {
            switch (node) {
                // A 'rethrows' callee throws what its arguments do, not all its signature allows.
                case BoundCall { Function.IsRethrows: true } rethrowing:
                    if (RethrownBy(rethrowing) is { } rethrown) {
                        into.Add(Sub(rethrowing.Substitution.Apply(rethrown)));
                    }

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

            var callee = call.Function;

            // An inline array's members are the inline layout's, whatever declares them.
            if (call.Receiver is { } inlineArray && IsInlineArray(inlineArray)) {
                return callee.Name == "append" ? InlineAppend(call) : InlineArrayMember(inlineArray, callee.Name, call);
            }

            // Sable.Core's @builtin members: the builtin, here (SilGen.Builtins.cs).
            if (callee.Builtin is not null) {
                return BuiltinCall(call, callee);
            }

            if (IsLocalFunction(callee) && generator.Captures(callee)) {
                return LocalFunctionCall(call);
            }

            // Sable.Core's @intrinsic members: the compiler writes their bodies.
            if (IsIntrinsic(callee) && callee is { Name: "compileError", ContainingSymbol: ModuleSymbol }) {
                return CompileError(call);
            }

            if (IsIntrinsic(callee)) {
                var owner = Owner(callee)?.OriginalDefinition;

                // Tasks: made, slept, and yielded to (SilGen.Tasks.cs).
                if (owner is not null && ReferenceEquals(owner, Core.Task)) {
                    if (callee.Name == "spawn") {
                        return Spawn(call);
                    }

                    if (callee.Name == "sleep") {
                        if (Expression(call.Arguments[0]) is not { } nanoseconds) {
                            return null;
                        }

                        Emit(new BuiltinInst("task_sleep", SilType.Unit, nanoseconds));
                        return UnitValue.Instance;
                    }

                    Emit(new BuiltinInst("task_yield", SilType.Unit));
                    return UnitValue.Instance;
                }

                // An inline-layout value's bytes (SilGen.Inline.cs).
                if (owner is not null && ReferenceEquals(owner, Core.Bytes)) {
                    return BytesOf(call);
                }

                // 'arena.alloc(value)'.
                if (owner is not null && owner.Equals(Core.Region)) {
                    return RegionAlloc(call);
                }

                // Reflection (SilGen.Reflection.cs).
                return Intrinsic(call);
            }

            // 'flags.contains(.Write)', on a '@flags' enum: every flag of the
            // argument set in the receiver.
            if (callee is { Name: "contains", DeclaringSyntax.IsEmpty: true, ContainingSymbol: NamedTypeSymbol { IsFlags: true } flagsType }) {
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
            if (callee is { FunctionKind: FunctionKind.Initializer, DeclaringSyntax.IsEmpty: true, Parameters.IsEmpty: true }
                && !inheritedInit && call.Receiver is BoundSuperAccess super) {
                return SuperImplicitInit(call, super);
            }

            // A class or actor's implicit 'init()'.
            if (!inheritedInit && callee is { FunctionKind: FunctionKind.Initializer, DeclaringSyntax.IsEmpty: true, Parameters.IsEmpty: true }
                && LowerType(call.Type) is ClassType implicitClass) {
                return ImplicitInit(call, implicitClass);
            }

            // Inline layout: a value from bytes, its 'fromBytes' synthesised
            // for InlineLayout (Compilation.InlineLayout.cs); an inline array's members.
            if (callee is { Name: "fromBytes", IsStatic: true, ContainingSymbol: NamedTypeSymbol { IsInlineLayout: true } }) {
                return FromBytes(call);
            }

            // A synthesised memberwise initialiser is the struct, built from its fields.
            if (!inheritedInit && callee is { FunctionKind: FunctionKind.Initializer, DeclaringSyntax.IsEmpty: true }) {
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

                return Emit(new StructInst(structType, [..fields]));
            }

            // A protocol's requirement, called on an existential: through its slot.
            if (OwnerOf(callee) is { TypeKind: TypeKind.Protocol } && call.Receiver is { } existentialReceiver
                && Sub(existentialReceiver.Type) is ExistentialTypeSymbol) {
                return ExistentialCall(existentialReceiver, SlotName(callee), call.Arguments, call);
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

            string name;
            FunctionType? type;
            if (IsGeneric(callee)) {
                if (generator.Instance(callee, CallSubstitution(call, callee), call.Syntax) is not { } instance) {
                    _failed = true;
                    return null;
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
            if (HasSelf(callee) || superInit) {
                SilValue? self;
                if (type.Parameters[0] is AddressType) {
                    self = call.Receiver is null or BoundSelfAccess ? _selfAddress : Address(call.Receiver, borrows);
                } else if (call.Receiver is null) {
                    self = _selfValue ?? (_selfAddress is { } address ? Hold(Emit(new LoadInst(address))) : null)
                        ?? (CapturedSelf() is { } captured ? Hold(captured) : null);
                    if (self is not null && IsOwned(self)) {
                        borrows.Add(self);
                    }
                } else {
                    self = Borrow(call.Receiver);
                    if (self is not null) {
                        borrows.Add(self);
                    }
                }

                if (self is null) {
                    return Missing(call, "the receiver of this call");
                }

                // A method a superclass declares takes the object as that class.
                if (self.Type is ClassType selfClass && type.Parameters[0] is ClassType parameterClass && selfClass != parameterClass) {
                    self = Emit(new UpcastInst(self, parameterClass));
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
                // A ref argument passes where it is, or a temporary when it is nowhere.
                if (i < callee.Parameters.Length && callee.Parameters[i].Modifier == ParameterModifier.Ref) {
                    if (ReferenceTo(call.Arguments[i]) is not { } referred) {
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
                    if (Address(call.Arguments[i], borrows) is not { } address) {
                        return null;
                    }

                    arguments.Add(address);
                    BeginAccess(address, modify: true, borrows);
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
                if (type.Parameters[^1] is not ArrayType variadicType || Gather(variadicType, call.Arguments.Skip(variadicAt)) is not { } gathered) {
                    return Missing(call, "the variadic arguments of this call");
                }

                if (type.IsOwned(arguments.Count)) {
                    arguments.Add(gathered);
                } else {
                    arguments.Add(Hold(gathered));
                    borrows.Add(gathered);
                }
            }

            // A method a subclass may override is found through the vtable;
            // 'super.m()' calls the superclass's own.
            SilValue code = HasSelf(callee) && call.Receiver is not BoundSuperAccess && generator.IsDynamic(callee)
                ? Emit(new ClassMethodInst(arguments[0], VTableSlot(callee), type))
                : Emit(new FunctionRefInst(name, type));
            // A 'rethrows' callee given functions that throw nothing throws
            // nothing: the binder asked for no 'try', and nothing handles it.
            return Invoke(code, type, arguments, borrows, call.Syntax, cannotThrow: callee.IsRethrows && RethrownBy(call) is null);
        }

        /// <summary>
        ///     Calls a function or a closure with borrowed arguments: <c>apply</c>,
        ///     or <c>try_apply</c> with the borrows ended on both of its edges.
        /// </summary>
        SilValue? Invoke(SilValue reference, FunctionType type, List<SilValue> arguments, List<SilValue> borrows, SyntaxNode at, bool cannotThrow = false) {
            BeginAccesses(borrows);
            if (type.Thrown is null) {
                var apply = Emit(new ApplyInst(reference, type.Result, [..arguments]));
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
            void Release(SilValue borrow) {
                switch (borrow) {
                    case AccessMarker marker:
                        Emit(new BuiltinInst("access_end", SilType.Unit, marker.Address));
                        break;
                    // A property changed in place is set again whichever way
                    // the call leaves.
                    case WriteBack writeBack:
                        writeBack.Run();
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
            for (var i = borrows.Count - 1; i >= 0; i--) {
                Release(borrows[i]);
            }

            if (cannotThrow) {
                Destroy(error);
                Jump(new UnreachableInst());
            } else {
                RouteError(error, at);
            }

            _current = normal;
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
        TypeSubstitution CallSubstitution(BoundCall call, FunctionSymbol callee) {
            // An array extension's member: what its element is, from the array's.
            // In a protocol's default implementation, 'first()' is called on
            // self: the type Self stands for, whose own type arguments
            // a generic conformer's member needs.
            var receiverOf = call.Receiver is { } r ? Sub(r.Type)
                : OwnerOf(call.Function)?.OriginalDefinition is { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } implicitSelf }
                    ? substitution.Lookup(implicitSelf)
                    : null;
            var receiver = receiverOf is null ? TypeSubstitution.Empty
                : OwnerOf(callee) is { ExtendedArray: not null } carrier ? Compilation.MatchArray(carrier, receiverOf) ?? TypeSubstitution.Empty
                : TypeSubstitution.For(receiverOf is NamedTypeSymbol named && ConstructedOwner(named, callee) is { } owner ? owner : receiverOf);
            // A local function sees the type parameters around it as this
            // instance has them.
            var map = IsLocalFunction(callee) ? substitution.Parameters.ToDictionary(p => p, p => substitution.Lookup(p)!) : new Dictionary<TypeParameterSymbol, TypeSymbol>();

            // A protocol's default implementation: Self is the receiver's type.
            if (OwnerOf(callee)?.OriginalDefinition is { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } self }) {
                var receiverType = call.Receiver is { } selfReceiver ? Sub(selfReceiver.Type) : substitution.Lookup(self);
                if (receiverType is not null) {
                    map[self] = receiverType;
                }
            }

            foreach (var parameter in TypeParametersOf(callee)) {
                var bound = call.Substitution.Lookup(parameter) is { } inferred
                    ? Sub(inferred)
                    : receiver.Lookup(parameter) ?? substitution.Lookup(parameter);
                if (bound is not null) {
                    map[parameter] = bound;
                }
            }

            // A 'some P' parameter stands for the type of what is passed to it.
            foreach (var opaque in OpaqueParametersOf(callee)) {
                var standsFor = s_opaqueOf.TryGetValue(opaque, out var found) ? found : null;
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
            return self is NamedTypeSymbol concrete ? Implementation(concrete, call.Function) : null;
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

        /// <summary>A value as <c>any P</c>: boxed, with a closure per requirement over the box.</summary>
        SilValue? MakeExistential(BoundConversion conversion) {
            if (LowerType(conversion.Type) is not StructType layout || Sub(conversion.Operand.Type) is not NamedTypeSymbol concrete) {
                return Missing(conversion, $"the conversion to '{conversion.Type.ToDisplayString()}'");
            }

            if (Expression(conversion.Operand) is not { } value) {
                return null;
            }

            var box = generator.BoxType(value.Type);
            var boxed = Emit(new AllocRefInst(box));
            Emit(new StoreInst(Owned(value), Emit(new RefElementAddrInst(boxed, 0)), StoreKind.Assign));

            var slots = generator.ExistentialSlots(layout);
            var closures = new List<SilValue>();
            for (var i = 0; i < slots.Count; i++) {
                var thick = (ThickFunctionType)layout.Fields[i].Type;
                if (generator.WitnessThunk(concrete, value.Type, box, slots[i].Slot, slots[i].Member, thick, conversion.Syntax) is not { } thunk) {
                    _failed = true;
                    return null;
                }

                SilValue context = i == slots.Count - 1 ? boxed : Emit(new CopyValueInst(boxed));
                closures.Add(Emit(new PartialApplyInst(Emit(new FunctionRefInst(thunk.Name, thunk.Type)), context, thick)));
            }

            if (slots.Count == 0) {
                Emit(new DestroyValueInst(boxed));
            }

            return Emit(new StructInst(layout, [..closures]));
        }

        /// <summary>A requirement called on an existential: its slot's closure, with the arguments.</summary>
        SilValue? ExistentialCall(BoundExpression receiver, string slot, IReadOnlyList<BoundExpression> argumentExpressions, BoundNode at) {
            var layout = LowerType(receiver.Type) as StructType;
            var index = layout?.FieldIndex(slot) ?? -1;
            if (layout is null || index < 0) {
                return Missing(at, $"calling '{slot}' through '{Sub(receiver.Type).ToDisplayString()}'");
            }

            if (Borrow(receiver) is not { } existential) {
                return null;
            }

            var closure = Emit(new StructExtractInst(existential, index));
            var arguments = new List<SilValue>();
            var borrows = new List<SilValue> { existential };
            foreach (var argument in argumentExpressions) {
                if (Borrow(argument) is not { } value) {
                    return null;
                }

                arguments.Add(value);
                borrows.Add(value);
            }

            return Invoke(closure, ((ThickFunctionType)closure.Type).Signature, arguments, borrows, at.Syntax);
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
            if (LowerType(closureExpression.Type) is not ThickFunctionType thick) {
                return Missing(closureExpression, $"a closure of type '{closureExpression.Type.ToDisplayString()}'");
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
                contextType = generator._module.AddType(new ClassType($"{name}$context"));
                foreach (var capture in captures) {
                    // A local function captured is its closure, typed as its own calls see it.
                    if ((capture.Key is FunctionSymbol capturedFunction && IsLocalFunction(capturedFunction) ? LocalFunctionType(capturedFunction) : LowerType(capture.Type)) is not { } fieldType) {
                        return Missing(closureExpression, $"capturing a value of type '{capture.Type.ToDisplayString()}'");
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
            }

            // The code.
            var code = new SilFunction(name, thick.Implementation) {
                SourceFile = function.SourceFile,
                Line = source.GetLinePosition(closureExpression.Syntax.Span.Start).Line,
            };
            code.ParameterNames.AddRange(codeSymbol.Parameters.Select(static p => p.Name));
            var info = new ClosureInfo(contextType, captures.ToDictionary(static c => c.Key, static c => c));
            new FunctionLowering(generator, codeSymbol, code, source, substitution, info).Lower(body);
            code.RemoveUnreachableBlocks();
            generator._module.Add(code);

            // A closure's body is checked as a function's is: what it does with
            // a move-only value it captures, or makes, is held to the same rules.
            MoveChecker.Check(code, source, generator._diagnostics);

            // The context.
            Locate(closureExpression.Syntax);
            var reference = Emit(new FunctionRefInst(name, thick.Implementation));
            if (contextType is null) {
                return Emit(new PartialApplyInst(reference, null, thick));
            }

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

            return Emit(new PartialApplyInst(reference, Unhold(context), thick));
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

                case ParameterSymbol parameter when _parameters.TryGetValue(parameter, out var value):
                    return Owned(value);

                case ParameterSymbol parameter when closure?.Captures.TryGetValue(parameter, out var outerParameter) == true:
                    return Emit(new LoadInst(Emit(new RefElementAddrInst(_context!, outerParameter.Field))));

                case FunctionSymbol local:
                    return LocalFunctionValue(local, at);

                case SelfKey:
                    return _selfValue is { } self ? Owned(self)
                        : _selfAddress is { } selfSlot ? Emit(new LoadInst(selfSlot))
                        : CapturedSelf() ?? Missing(at, "capturing 'self'");

                default:
                    return Missing(at, "this capture");
            }
        }

        /// <summary>A named function as a value: a closure over a thunk, with no context.</summary>
        SilValue? FunctionValue(BoundFunctionReference reference) {
            var callee = reference.Function;

            // A local function that captures already is a closure.
            if (IsLocalFunction(callee) && generator.Captures(callee)) {
                return LocalFunctionValue(callee, reference);
            }

            if (reference.Receiver is { } receiver && HasSelf(callee)) {
                return BoundMethodValue(reference, receiver);
            }

            if (reference.Receiver is not null || IsGeneric(callee) || !generator._names.TryGetValue(callee, out var name)
                || LowerType(reference.Type) is not ThickFunctionType thick) {
                return Missing(reference, $"'{callee.Name}' as a value");
            }

            var type = generator._module.Find(name)?.Type ?? generator.SignatureOf(callee, reference.Syntax, TypeSubstitution.Empty);
            if (type is null || type != thick.Signature || !type.Owned.IsEmpty) {
                return Missing(reference, $"'{callee.Name}' as a value");
            }

            var thunk = generator.Thunk(name, type);
            return Emit(new PartialApplyInst(Emit(new FunctionRefInst(thunk, thick.Implementation)), null, thick));
        }

        /// <summary>
        ///     <c>s.get</c> as a value: the method with its receiver, evaluated
        ///     here, in a box the closure carries — a copy of a value, or the
        ///     reference to an object, as <c>s.get()</c> would have used.
        /// </summary>
        SilValue? BoundMethodValue(BoundFunctionReference reference, BoundExpression receiver) {
            var callee = reference.Function;
            if (IsGeneric(callee) || !generator._names.TryGetValue(callee, out var name)
                || generator.SignatureOf(callee, reference.Syntax, TypeSubstitution.Empty) is not { } type
                || LowerType(reference.Type) is not ThickFunctionType thick) {
                return Missing(reference, $"'{callee.Name}' as a value");
            }

            // A mutating method changes the value it is called on, which the
            // closure's copy is not; a consuming one takes what it is given.
            if (type.Parameters[0] is AddressType || !type.Owned.IsEmpty) {
                return Missing(reference, $"the {(type.Parameters[0] is AddressType ? "mutating" : "consuming")} method '{callee.Name}' as a value");
            }

            if (new FunctionType([..type.Parameters.Skip(1)], type.Result, type.Thrown) != thick.Signature) {
                return Missing(reference, $"'{callee.Name}' as a value of type '{reference.Type.ToDisplayString()}'");
            }

            var thunk = generator.BoundMethodThunk(callee, name, type);
            if (Expression(receiver) is not { } self) {
                return null;
            }

            // A method a superclass declares takes the object as that class.
            if (self.Type is ClassType selfClass && type.Parameters[0] is ClassType parameterClass && selfClass != parameterClass) {
                self = Emit(new UpcastInst(self, parameterClass));
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
            foreach (var argument in call.Arguments) {
                if (Borrow(argument) is not { } value) {
                    return null;
                }

                arguments.Add(value);
                borrows.Add(value);
            }

            return Invoke(callee, thick.Signature, arguments, borrows, call.Syntax);
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

        /// <summary>A property read through an existential or a getter.</summary>
        SilValue? ComputedProperty(BoundFieldAccess access, BoundExpression receiver) {
            if (Sub(receiver.Type) is ExistentialTypeSymbol) {
                return ExistentialCall(receiver, GetterSlot(access.Field), [], access);
            }

            // A computed property of the program's own: a call to its getter.
            if (generator.GetterOf(access.Field) is { } getter) {
                return Expression(new BoundCall(access.Syntax, receiver, getter, [], access.Type));
            }

            // A protocol's property, read where Self is known: the type's own field.
            if (access.Field.ContainingSymbol is NamedTypeSymbol { TypeKind: TypeKind.Protocol } && Sub(receiver.Type) is NamedTypeSymbol concrete) {
                for (var type = (NamedTypeSymbol?)concrete.OriginalDefinition; type is not null; type = SuperclassOf(type)?.OriginalDefinition) {
                    if (type.Members.OfType<FieldSymbol>().FirstOrDefault(f => f.Name == access.Field.Name && !f.IsStatic) is { } own) {
                        return own.IsComputed && generator.GetterOf(own) is { } ownGetter
                            ? Expression(new BoundCall(access.Syntax, receiver, ownGetter, [], access.Type))
                            : FieldAccess(access, receiver, own);
                    }
                }
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
