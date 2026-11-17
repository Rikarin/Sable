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
                if (declaration is not (FunctionDeclarationSyntax or InitDeclarationSyntax or DeinitDeclarationSyntax or OperatorDeclarationSyntax)) {
                    continue;
                }

                if (_compilation.GetDeclaredSymbol(declaration) is not FunctionSymbol symbol || !IsLowerable(symbol)) {
                    continue;
                }

                functions.Add((symbol, declaration, model));
            }

            // A computed property's getter: a method of the type, named for
            // the property, that reading the property calls.
            foreach (var accessor in tree.Root.DescendantNodes().OfType<AccessorSyntax>()) {
                if (accessor.Keyword.Kind != SyntaxKind.GetKeyword
                    || model.GetAccessorSymbol(accessor) is not { ContainingSymbol: FieldSymbol { IsStatic: false } field } getter
                    || !IsLowerable(getter)) {
                    continue;
                }

                _getters[field] = getter;
                functions.Add((getter, accessor, model));
            }
        }

        foreach (var (symbol, _, _) in functions) {
            var overloaded = functions.Count(f => f.Symbol.Name == symbol.Name
                && ReferenceEquals(f.Symbol.ContainingSymbol, symbol.ContainingSymbol)) > 1;
            _names[symbol] = Mangle(symbol, overloaded);
        }

        // Which classes something inherits from: their methods dispatch.
        foreach (var tree in _compilation.SyntaxTrees) {
            foreach (var declaration in tree.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()) {
                if (_compilation.GetDeclaredSymbol(declaration) is NamedTypeSymbol type && SuperclassOf(type) is { } superclass) {
                    for (var ancestor = (NamedTypeSymbol?)superclass; ancestor is not null; ancestor = SuperclassOf(ancestor)) {
                        _inherited.Add(ancestor.OriginalDefinition);
                    }
                }
            }
        }

        // '@main' marks where a program starts; failing that, a function
        // called main does.
        var entry = functions.FirstOrDefault(static f =>
                f.Syntax is FunctionDeclarationSyntax declaration
                && declaration.Attributes.Any(static a => a.Name.ToString().Trim() == "main")
            ).Symbol
            ?? functions.FirstOrDefault(static f => f.Symbol is { Name: "main", ContainingSymbol: ModuleSymbol }).Symbol;

        foreach (var (symbol, syntax, model) in functions) {
            _declarations[symbol] = (syntax, model);
        }

        DeclareGlobals();
        _entrySymbol = entry;

        // What is not generic is lowered as it is; what is, once for each set
        // of type arguments something calls it with.
        foreach (var (symbol, syntax, model) in functions) {
            if (IsGeneric(symbol)) {
                continue;
            }

            if (SignatureOf(symbol, syntax, TypeSubstitution.Empty) is not { } type) {
                continue;
            }

            if (ReferenceEquals(symbol, entry)) {
                _module.EntryPoint = _names[symbol];
            }

            LowerBody(symbol, syntax, model, _names[symbol], type, TypeSubstitution.Empty);
        }

        while (_pending.Count > 0) {
            var (symbol, name, type, substitution) = _pending.Dequeue();
            var (syntax, model) = _declarations[symbol];
            LowerBody(symbol, syntax, model, name, type, substitution);
        }

        LowerGlobalLifetimes();
        BuildVTables();
        RootChecker.Check(_module, _sources, _diagnostics);
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
            foreach (var declaration in tree.Root.DescendantNodes().Where(static d => d is FieldDeclarationSyntax or ConstDeclarationSyntax)) {
                if (_compilation.GetDeclaredSymbol(declaration) is not FieldSymbol field || !IsGlobal(field)
                    || field.ContainingSymbol is NamedTypeSymbol { IsGenericDefinition: true } || Lower(field.Type) is not { } type) {
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

    internal (string Name, SilType Type)? GlobalOf(FieldSymbol field) => _globalsByField.TryGetValue(field, out var global) ? global : null;

    /// <summary>
    ///     Two functions the entry point calls: one that stores each global's
    ///     initial value, in the order declared, before anything else runs;
    ///     one that destroys them, last first, as the program returns.
    /// </summary>
    void LowerGlobalLifetimes() {
        if (_globalFields.Count == 0 || _entrySymbol is not { } initSymbol) {
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

    void LowerBody(FunctionSymbol symbol, SyntaxNode syntax, SemanticModel model, string name, FunctionType type, TypeSubstitution substitution) {
        if (model.GetBoundNode(syntax) is not BoundBlock body) {
            return;
        }

        var source = model.SyntaxTree.Text;
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

    /// <summary>The type a member belongs to, whether declared in it or in an extension of it.</summary>
    internal static NamedTypeSymbol? OwnerOf(FunctionSymbol symbol) => Owner(symbol);

    static NamedTypeSymbol? Owner(FunctionSymbol symbol) =>
        symbol.ContainingSymbol switch {
            NamedTypeSymbol type => type,
            ExtensionSymbol extension => extension.ExtendedType,

            // An accessor belongs to its property, which belongs to the type.
            FieldSymbol { ContainingSymbol: NamedTypeSymbol type } => type,
            FieldSymbol { ContainingSymbol: ExtensionSymbol extension } => extension.ExtendedType,
            _ => null,
        };

    /// <summary>Every type parameter a function's body may mention: its type's, then its own.</summary>
    /// <remarks>A protocol's own method — a default implementation — has <c>Self</c> among them.</remarks>
    static IEnumerable<TypeParameterSymbol> TypeParametersOf(FunctionSymbol symbol) {
        var owner = Owner(symbol)?.OriginalDefinition;
        IEnumerable<TypeParameterSymbol> self = owner is { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } selfParameter } ? [selfParameter] : [];
        return self.Concat(owner?.TypeParameters ?? []).Concat(symbol.TypeParameters);
    }

    static bool IsGeneric(FunctionSymbol symbol) => TypeParametersOf(symbol).Any();

    /// <summary>
    ///     The instance of a generic function for these type arguments: its
    ///     name, and its signature — lowered later, once, however many calls
    ///     ask for it. Null when the arguments are not all known types.
    /// </summary>
    internal (string Name, FunctionType Type)? Instance(FunctionSymbol symbol, TypeSubstitution substitution, SyntaxNode at) {
        if (!_names.TryGetValue(symbol, out var baseName) || !_declarations.ContainsKey(symbol)) {
            return null;
        }

        var arguments = new List<SilType>();
        foreach (var parameter in TypeParametersOf(symbol)) {
            if (substitution.Lookup(parameter) is not { } argument || Lower(argument) is not { } lowered) {
                Unsupported<object>(at, $"'{symbol.Name}' with its type arguments unknown here");
                return null;
            }

            arguments.Add(lowered);
        }

        var name = $"{baseName}$of${string.Join("$", arguments.Select(static a => Sanitise(a.ToString())))}";
        if (_instances.TryGetValue(name, out var existing)) {
            return (name, existing);
        }

        if (SignatureOf(symbol, at, substitution) is not { } type) {
            return null;
        }

        _instances[name] = type;
        _pending.Enqueue((symbol, name, type, substitution));
        return (name, type);
    }

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

    /// <summary>True for a method a call must find through the vtable: one a subclass may override.</summary>
    internal bool IsDynamic(FunctionSymbol method) =>
        method.FunctionKind == FunctionKind.Ordinary && !method.IsStatic
        && method.ContainingSymbol is NamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Actor } owner
        && _inherited.Contains(owner.OriginalDefinition);

    /// <summary>
    ///     Fills each class's vtable: its superclass's slots, each override in
    ///     its base's slot, then the methods it adds.
    /// </summary>
    void BuildVTables() {
        var built = new HashSet<ClassType>();
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

            foreach (var method in symbol.OriginalDefinition.Members.OfType<FunctionSymbol>()) {
                if (method is { FunctionKind: FunctionKind.Ordinary, IsStatic: false, IsAbstract: true } && method.ContainingSymbol is NamedTypeSymbol) {
                    AbstractStub(method);
                }

                if (method is not { FunctionKind: FunctionKind.Ordinary, IsStatic: false } || method.ContainingSymbol is not NamedTypeSymbol
                    || !_names.TryGetValue(method, out var name) || _module.Find(name) is null) {
                    continue;
                }

                var slot = SlotName(method);
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
            _ => $"{owner}.{symbol.Name}",
        };
        return overloaded
            ? name + string.Concat(symbol.Parameters.Select(static p => "$" + Sanitise(p.Type.ToDisplayString())))
            : name;
    }

    static string Sanitise(string text) =>
        new([..text.Select(static c => char.IsLetterOrDigit(c) || c is '_' or '.' ? c : '$')]);

    /// <summary>True for a method that takes <c>self</c>, as opposed to a static one or an initialiser.</summary>
    // An operator is a static function of its type: both operands are arguments.
    static bool HasSelf(FunctionSymbol symbol) =>
        Owner(symbol) is not null && !symbol.IsStatic && symbol.FunctionKind is not (FunctionKind.Initializer or FunctionKind.Operator);

    FunctionType? SignatureOf(FunctionSymbol symbol, SyntaxNode at, TypeSubstitution substitution) {
        SilType? Lower(TypeSymbol type) => this.Lower(substitution.Apply(type));

        // An async function is an ordinary one: a task suspends only where
        // it waits, in the runtime, with its whole stack (docs/04 §5).
        if (symbol.IsRethrows) {
            return Unsupported<FunctionType>(at, $"'{symbol.Name}', which is async or rethrows");
        }

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
                or ParameterModifier.Ref or ParameterModifier.In)) {
                return Unsupported<FunctionType>(at, $"the '{parameter.Modifier}' parameter '{parameter.Name}'");
            }

            if (Lower(parameter.Type) is not { } type) {
                return Unsupported<FunctionType>(at, $"a parameter of type '{parameter.Type.ToDisplayString()}'");
            }

            // An inout or ref parameter is the address of the caller's storage.
            parameters.Add(parameter.Modifier is ParameterModifier.InOut or ParameterModifier.Ref ? SilType.Address(type) : type);
        }

        var resultSymbol = symbol.FunctionKind == FunctionKind.Initializer ? SelfType(symbol, substitution) : symbol.ReturnType;
        if (Lower(resultSymbol) is not { } result) {
            return Unsupported<FunctionType>(at, $"a result of type '{resultSymbol.ToDisplayString()}'");
        }

        SilType? thrown = null;
        if (symbol.ThrownType is { } thrownSymbol) {
            if (substitution.Apply(thrownSymbol).Equals(Core.AnyError) || Lower(thrownSymbol) is not { } thrownType) {
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
        _types[symbol] = type;
        _lowered.Add(symbol);
        return _module.AddType(type);
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

        if (type is NamedTypeSymbol { TypeArguments.IsEmpty: true } primitive
            && (Core.Integers.Contains(primitive) || primitive.Equals(Core.F32))
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
            // buffer; a slice is a copy, for now, rather than a view.
            // An array copies its elements on write, so a move-only element has no array yet.
            case ArrayTypeSymbol { ArrayKind: ArrayKind.Grow or ArrayKind.GrowShrink or ArrayKind.Fixed or ArrayKind.Slice } array:
                return Lower(array.ElementType) is { } elementType && elementType is not NominalType { IsNoncopyable: true }
                    ? new ArrayType(elementType)
                    : null;

            // A dictionary, until Sable.Core writes one: its entries, in an
            // array, with key equality from the builtins.
            case NamedTypeSymbol { TypeArguments: [var key, var value] } named when named.OriginalDefinition.Equals(Core.Dictionary): {
                if (Lower(key) is not { } keyType || Lower(value) is not { } valueType) {
                    return null;
                }

                return SilBuiltins.Find($"cmp_eq_{keyType}") is null
                    ? null
                    : new ArrayType(SilType.Tuple([keyType, valueType]));
            }

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
                if (functionType.ThrownType is { } thrownSymbol && (thrownSymbol.Equals(Core.AnyError) || (thrown = Lower(thrownSymbol)) is null)) {
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

                var enumType = Begin(type, new EnumType(name));
                enumType.Cases.Add(("Some", wrapped));
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
                    }

                    enumType.Cases.Add((unionCase.Name, payload));
                }

                return HoldsItself(enumType) ? null : enumType;
            }

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

    readonly Dictionary<string, int> _closureCounts = new(StringComparer.Ordinal);

    internal string ClosureName(string function) {
        var count = _closureCounts.GetValueOrDefault(function);
        _closureCounts[function] = count + 1;
        return $"{function}$closure{count}";
    }

    /// <summary>One thing a closure captured: where in its context it is, and whether that is a box.</summary>
    internal sealed record Capture(object Key, TypeSymbol Type, int Field, bool Boxed, BoundExpression? Initializer);

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
    internal static List<Capture> CapturesOf(BoundClosure closure) {
        var inner = Descendants(closure.Body).OfType<BoundClosure>().Select(static c => (Symbol)c.Symbol).ToHashSet();
        inner.Add(closure.Symbol);

        var listed = closure.Captures.Where(static c => c.Initializer is not null).ToDictionary(static c => c.Symbol, static c => c);
        var captures = new List<Capture>();
        var seen = new HashSet<object>();

        void Add(object key, TypeSymbol type, bool boxed, BoundExpression? initializer) {
            if (seen.Add(key)) {
                captures.Add(new(key, type, captures.Count, boxed, initializer));
            }
        }

        foreach (var node in Descendants(closure.Body)) {
            switch (node) {
                case BoundLocalAccess access when !inner.Contains(access.Local.ContainingSymbol!):
                    if (listed.TryGetValue(access.Local, out var listedCapture)) {
                        Add(access.Local, access.Local.Type, false, listedCapture.Initializer);
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
    internal static HashSet<LocalSymbol> BoxedLocals(Symbol function, BoundNode body) {
        var boxed = new HashSet<LocalSymbol>();
        foreach (var closure in Descendants(body).OfType<BoundClosure>()) {
            foreach (var capture in CapturesOf(closure)) {
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

    /// <summary>The functions a dictionary of these entries is read and written with: get, set and remove.</summary>
    /// <remarks>
    ///     Each looks for the key from the front, comparing with the key
    ///     type's builtin equality: linear, and a stand-in until Sable.Core
    ///     has a dictionary of its own that hashes.
    /// </remarks>
    internal (string Get, string Set, string Remove) DictionaryFunctions(ArrayType entries, EnumType optional) {
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
            var same = test.Append(new BuiltinInst(equal, SilType.Bool, test.Append(new TupleExtractInst(element, 0)), function.Entry.Arguments[1]));
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
                        if (Lower(new FunctionTypeSymbol([..method.Parameters.Select(static p => p.Type)], method.ReturnType, false, method.ThrownType)) is ThickFunctionType thick) {
                            layout.Fields.Add((SlotName(method), thick));
                            slots.Add((SlotName(method), method));
                        }

                        break;
                    }

                    case FieldSymbol { IsStatic: false } field when seen.Add(GetterSlot(field)) && !MentionsSelf(field.Type):
                        if (Lower(new FunctionTypeSymbol([], field.Type)) is ThickFunctionType getter) {
                            layout.Fields.Add((GetterSlot(field), getter));
                            slots.Add((GetterSlot(field), field));
                        }

                        break;
                }
            }

            foreach (var inherited in current.BaseTypes.OfType<NamedTypeSymbol>().Where(static b => b.TypeKind == TypeKind.Protocol)) {
                Collect(inherited);
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

    /// <summary>The member of a concrete type that implements a protocol's method: its own, or a superclass's.</summary>
    internal static FunctionSymbol? Implementation(NamedTypeSymbol concrete, FunctionSymbol requirement) {
        for (var type = (NamedTypeSymbol?)concrete.OriginalDefinition; type is not null; type = SuperclassOf(type)?.OriginalDefinition) {
            // An operator is static wherever it is written; a requirement's says nothing.
            var found = type.Members.OfType<FunctionSymbol>().FirstOrDefault(f =>
                f.Name == requirement.Name
                && (f.IsStatic == requirement.IsStatic || requirement.FunctionKind == FunctionKind.Operator)
                && f.Parameters.Length == requirement.Parameters.Length
                && Owner(f) is { TypeKind: not TypeKind.Protocol });
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

        // A property the type computes: its getter is the implementation.
        if (requirement is FieldSymbol computed
            && FindField(concrete, computed.Name) is { IsComputed: true } own && GetterOf(own) is { } getter) {
            requirement = getter;
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
        } else if ((implementation is { ContainingSymbol: ExtensionSymbol } ? implementation : implementation is null ? method : null) is { } fallback
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

        SilValue callee = implementation is not null && IsDynamic(implementation)
            ? entry.Append(new ClassMethodInst(receiver, SlotName(implementation), targetType))
            : entry.Append(new FunctionRefInst(target, targetType));

        void Release(SilBlock block) {
            if (!selfValue.Type.IsTrivial) {
                block.Append(new DestroyValueInst(selfValue));
            }
        }

        if (targetType.Thrown is null) {
            var result = entry.Append(new ApplyInst(callee, targetType.Result, [receiver, ..arguments]));
            Release(entry);
            entry.Append(new ReturnInst(targetType.Result == SilType.Unit ? null : result));
        } else {
            var normal = thunk.AddBlock();
            var result = targetType.Result == SilType.Unit ? null : normal.AddArgument(targetType.Result);
            var failed = thunk.AddBlock();
            var error = failed.AddArgument(targetType.Thrown);
            entry.Append(new TryApplyInst(callee, targetType.Result, [receiver, ..arguments], normal, failed));
            Release(normal);
            normal.Append(new ReturnInst(result));
            Release(failed);
            failed.Append(new ThrowInst(error));
        }

        _module.Add(thunk);
        return (name, code);
    }

    /// <summary>True when a type contains itself by value — through no reference — and so has no size.</summary>
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
                : substitution.Apply(type);

        SilType? LowerType(TypeSymbol type) => generator.Lower(Sub(type));

        /// <summary>Marks an expression whose value is <c>()</c>: something to have done, nothing to use.</summary>
        sealed class UnitValue : SilValue {
            public static readonly UnitValue Instance = new();
            public override SilType Type => SilType.Unit;
        }

        sealed record Loop(string? Label, SilBlock Break, SilBlock Continue, int Scopes, int Temporaries);

        /// <summary>Where an error goes: a block taking it, and how many defer scopes and temporaries were open there.</summary>
        sealed record Handler(SilBlock Block, SilType Type, int Scopes, int Temporaries);

        readonly Dictionary<ParameterSymbol, SilValue> _parameters = [];
        readonly Dictionary<LocalSymbol, AllocStackInst> _slots = [];
        readonly List<AllocStackInst> _allocations = [];
        readonly List<Loop> _loops = [];
        readonly List<Handler> _handlers = [];

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
            _boxed = BoxedLocals(symbol, body);

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
            // stores the initial value first, before its own body runs.
            if (symbol.FunctionKind == FunctionKind.Initializer
                && (symbol.ContainingSymbol is ExtensionSymbol extension ? extension.ExtendedType : symbol.ContainingSymbol) is NamedTypeSymbol owner
                && SelfType(symbol, substitution) is { } selfType) {
                foreach (var field in owner.OriginalDefinition.Members.OfType<FieldSymbol>()) {
                    if (field is { IsStatic: false, IsComputed: false, HasInitializer: true } && generator.FieldInitializer(field) is { } initial) {
                        var self = new BoundSelfAccess(initial.Syntax, selfType, true);
                        Statement(new BoundExpressionStatement(initial.Syntax, new BoundAssignment(initial.Syntax, new BoundFieldAccess(initial.Syntax, self, field), initial)));
                    }
                }
            }

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

            var value = Emit(new LoadInst(address));
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

            return value;
        }

        /// <summary>The end of a borrow: a temporary is taken off the temporaries and destroyed, a load_borrow ended.</summary>
        void End(SilValue value) {
            if (value is AccessMarker marker) {
                Emit(new BuiltinInst("access_end", SilType.Unit, marker.Address));
                return;
            }

            if (value is LoadBorrowInst borrowed) {
                Unhold(borrowed);
                Emit(new EndBorrowInst(borrowed));
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
        ///     True for a parameter passed by address — inout or ref — whose
        ///     address stands for its value; a parameter of reference type is
        ///     an address that is the value.
        /// </summary>
        static bool IsByAddress(ParameterSymbol parameter) => parameter.Modifier is ParameterModifier.InOut or ParameterModifier.Ref;

        /// <summary>The slot of a move-only local or parameter, or null.</summary>
        AllocStackInst? MoveOnlyPlace(BoundExpression expression) =>
            expression switch {
                BoundLocalAccess local when _slots.TryGetValue(local.Local, out var slot) && !_boxed.Contains(local.Local)
                    && slot.ElementType is NominalType { IsNoncopyable: true } => slot,
                BoundParameterAccess parameter when _spilled.TryGetValue(parameter.Parameter, out var spilled) => spilled,
                BoundConversion { ConversionKind: ConversionKind.Identity } conversion => MoveOnlyPlace(conversion.Operand),
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

                        case AllocStackInst slot:
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

        /// <summary>Gives a local's slot to the innermost scope, to be destroyed as it closes.</summary>
        void EndsWithScope(AllocStackInst slot) {
            if (slot.ElementType.IsTrivial || _scopes.Count == 0 || _scopes.Any(s => s.Contains(slot))) {
                return;
            }

            _scopes[^1].Add(slot);
            _scoped.Add(slot);
        }

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

            if (handler.Type != error.Type) {
                Fail(at, $"an error of type {error.Type} converted to {handler.Type}");
                return;
            }

            DestroyTemporaries(handler.Temporaries);
            RunDefers(handler.Scopes);
            Branch(handler.Block, Owned(error));
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

                case BoundContinue jump when FindLoop(jump.Label) is { } continueLoop:
                    DestroyTemporaries(continueLoop.Temporaries);
                    RunDefers(continueLoop.Scopes);
                    Branch(continueLoop.Continue);
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

                // 'unsafe { … }' and the like: a block, as far as lowering goes.
                case BoundScopedBlock { ScopeKind: not ScopeKind.Region } scoped:
                    Statement(scoped.Body);
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
        void ForRange(BoundForEach loop, BoundRange range) {
            if (range.Lower is null || range.Upper is null || loop.Element is null) {
                Fail(loop.Syntax, "a 'for' over an open range");
                return;
            }

            if (Expression(range.Lower) is not { } lower || Expression(range.Upper) is not { } upper) {
                return;
            }

            if (lower.Type != SilType.Int || SlotFor(loop.Element, loop.Syntax) is null) {
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

            _current = after;
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
                        BindLocal(binding.Local, value, binding.Syntax);
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
                    var payload = payloadType is null ? null : Payload(target, payloadType);
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

        /// <summary>Lowers an expression to an owned value — or a trivial one, or <see cref="UnitValue" />.</summary>
        SilValue? Expression(BoundExpression expression) {
            if (UsesLent(expression)) {
                return null;
            }

            if (_failed) {
                return null;
            }

            switch (expression) {
                case BoundLiteral literal:
                    return Literal(literal, literal.Type);

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
                    return OptionalChain(chained, chainedFrom, through => new BoundFieldAccess(chained.Syntax, through, chained.Field, reached.UnderlyingType));

                case BoundConversion { ConversionKind: ConversionKind.OptionalWrap, Operand: BoundCall { Receiver: { } chainedFrom } chainedCall }
                    when IsChainedThrough(chainedFrom, chainedCall.Function):
                    return OptionalChain(
                        expression,
                        chainedFrom,
                        through => new BoundCall(chainedCall.Syntax, through, chainedCall.Function, chainedCall.Arguments, chainedCall.Type) {
                            Substitution = chainedCall.Substitution,
                        }
                    );

                case BoundConversion { ConversionKind: ConversionKind.OptionalWrap } conversion:
                    return Wrap(conversion);

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

                case BoundFieldAccess { Field: { Name: "value", IsAsyncGetter: true } } taskValue:
                    return TaskValue(taskValue);

                // A global: read where it lives.
                case BoundFieldAccess { Field: var globalField } when SilGen.IsGlobal(globalField) && generator.GlobalOf(globalField) is var (globalName, globalType):
                    return Emit(new LoadInst(Emit(new GlobalAddrInst(globalName, globalType))));

                case BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: true, Name: var member } } when IsInlineArray(receiver):
                    return InlineArrayMember(receiver, member, expression);

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

                case BoundTry tryExpression:
                    return Try(tryExpression);

                case BoundCall call:
                    return Call(call);

                default:
                    return Missing(expression, $"the expression '{Describe(expression.Syntax)}' ({expression.GetType().Name}{(expression is BoundConversion c ? " " + c.ConversionKind : "")})");
            }
        }

        bool IsTrivialType(TypeSymbol type) => LowerType(type)?.IsTrivial ?? false;

        SilValue? Missing(BoundNode node, string what) {
            Fail(node.Syntax, what);
            return null;
        }

        SilValue? Assignment(BoundAssignment assignment) {
            if (IsInlineMember(assignment.Target)
                || assignment.Target is BoundIndexAccess { Receiver: var indexed } && IsInlineArray(indexed)) {
                return InlineAssign(assignment.Target, assignment.Value);
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

        bool IsDictionary(TypeSymbol type) => Sub(type) is NamedTypeSymbol named && named.OriginalDefinition.Equals(Core.Dictionary);

        (string Get, string Set, string Remove)? Dictionary(TypeSymbol type, BoundNode at) {
            var named = (NamedTypeSymbol)Sub(type);
            if (LowerType(named) is not ArrayType entries || LowerType(new OptionalTypeSymbol(named.TypeArguments[1])) is not EnumType optional) {
                Missing(at, $"a dictionary of type '{named.ToDisplayString()}'");
                return null;
            }

            return generator.DictionaryFunctions(entries, optional);
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

                case BoundTupleElementAccess element:
                    return Address(element.Receiver, borrows) is { } tuple
                        ? Emit(new TupleElementAddrInst(tuple, element.Index))
                        : null;

                default:
                    return Missing(target, $"storing to '{Describe(target.Syntax)}'");
            }
        }

        SilValue? Literal(BoundLiteral literal, TypeSymbol type) {
            // 'none': the optional with nothing in it.
            if (literal.Value is null && LowerType(type) is EnumType optional && optional.CaseIndex("None") is var none and >= 0) {
                return Emit(new EnumInst(optional, none, null));
            }

            // A plain enum's case is bound as a literal naming it.
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

            if (SilType.IntegerInfo(type) is var (bits, signed) && value is long or int or ulong) {
                var number = (System.Numerics.BigInteger)System.Convert.ToDecimal(value);
                if (negative) {
                    number = -number;
                }

                var low = signed ? -(System.Numerics.BigInteger.One << (bits - 1)) : 0;
                var high = signed ? (System.Numerics.BigInteger.One << (bits - 1)) - 1 : (System.Numerics.BigInteger.One << bits) - 1;
                if (number < low || number > high) {
                    Fail(at, $"the integer '{(negative ? "-" : "")}{value}', which does not fit in {type}");
                    return null;
                }

                return Emit(new IntegerLiteralInst(type, number > long.MaxValue ? unchecked((long)(ulong)number) : (long)number));
            }

            if (SilType.IsFloat(type) && value is double or long or int or ulong) {
                var number = System.Convert.ToDouble(value);
                return Emit(new FloatLiteralInst(negative ? -number : number, type));
            }

            Fail(at, $"the literal '{Describe(at)}' of type {type}");
            return null;
        }

        /// <summary>
        ///     <c>"x = \(x)"</c>: the text and the holes, each as a string,
        ///     joined left to right.
        /// </summary>
        SilValue? Interpolation(BoundInterpolatedString interpolated) {
            if (interpolated.Syntax is not InterpolatedStringExpressionSyntax syntax) {
                return Missing(interpolated, "this interpolated string");
            }

            SilValue? result = null;
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
                    if (hole >= interpolated.Parts.Length || Expression(interpolated.Parts[hole]) is not { } part) {
                        return null;
                    }

                    piece = Stringify(part, interpolated.Parts[hole]);
                    hole++;
                    if (piece is null) {
                        return null;
                    }
                }

                if (result is null) {
                    result = Hold(piece);
                    continue;
                }

                var joined = Emit(new BuiltinInst("string_concat", SilType.String, result, piece));
                Destroy(piece);
                End(result);
                result = Hold(joined);
            }

            return result is null ? Emit(new StringLiteralInst("")) : Unhold(result);
        }

        /// <summary>A value as a string, for interpolation: a string itself, or the scalars written out.</summary>
        SilValue? Stringify(SilValue value, BoundNode at) {
            if (value.Type == SilType.String) {
                return value;
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

            return inner is null ? null : Emit(new EnumInst(optional, optional.CaseIndex("Some"), inner));
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
            if (Expression(new BoundCall(binary.Syntax, null, callee, [first, second], resultType)) is not { } result) {
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

            if (value.Type == target) {
                return value;
            }

            var name = $"convert_{value.Type}_{target}";
            return SilBuiltins.Find(name) is not null
                ? Emit(new BuiltinInst(name, target, value))
                : Missing(conversion, $"the conversion from {value.Type} to {target}");
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

            SilValue? payload = values.Count switch {
                0 => null,
                1 when enumType.Cases[index].Payload is not TupleType => values[0],
                _ => Emit(new TupleInst((TupleType)enumType.Cases[index].Payload!, [..values])),
            };

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

        SilValue? IndexRead(BoundIndexAccess index) {
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

        /// <summary>'a ?? b': a's value when there is one; b, evaluated only then, when there is not.</summary>
        /// <summary>A member reached through '?.': the receiver is optional, and the member is not the optional's own.</summary>
        bool IsChainedThrough(BoundExpression receiver, Symbol member) =>
            Sub(receiver.Type) is OptionalTypeSymbol
            && !(member.ContainingSymbol is NamedTypeSymbol owner && ReferenceEquals(owner.OriginalDefinition, Core.Optional));

        /// <summary>
        ///     '?.': the receiver unwrapped into a temporary, the member reached
        ///     through it and its result wrapped; none when the receiver is none.
        /// </summary>
        SilValue? OptionalChain(BoundExpression chained, BoundExpression receiver, Func<BoundExpression, BoundExpression> through) {
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
                Branch(after, Emit(new EnumInst(resultType, resultType.CaseIndex("Some"), Owned(reached))));
            }

            _current = absent;
            Destroy(value);
            Branch(after, Emit(new EnumInst(resultType, resultType.CaseIndex("None"), null)));

            _current = after;
            return result;
        }

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
                var equal = OptionalEquals(optional, payloadType, left, right, binary);
                End(right);
                End(left);
                return equal is null || binary.OperatorKind == BinaryOperatorKind.Equal
                    ? equal
                    : Emit(new BuiltinInst("not_Bool", SilType.Bool, equal));
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

        static bool IsNone(BoundExpression expression) =>
            expression switch {
                BoundLiteral { Value: null } => true,
                BoundConversion conversion => IsNone(conversion.Operand),
                _ => false,
            };

        /// <summary>Two optionals of a comparable type compared: by case, and then by value.</summary>
        SilValue? OptionalEquals(EnumType optional, SilType payloadType, SilValue left, SilValue right, BoundNode at) {
            var compare = $"cmp_eq_{payloadType}";
            if (SilBuiltins.Find(compare) is null) {
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
            Branch(after, Emit(new BuiltinInst(compare, SilType.Bool, leftValue, rightValue)));
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

                    var alternative = Expression(fallback);
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

        /// <summary>The error type of the first throwing call inside an expression.</summary>
        SilType? ThrownBy(BoundNode node) {
            if (node is BoundCall { Function.ThrownType: { } thrown }) {
                return LowerType(thrown);
            }

            if (node is BoundIndirectCall { Callee.Type: FunctionTypeSymbol { ThrownType: { } indirectThrown } }) {
                return LowerType(indirectThrown);
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

        // -------------------------------------------------------------------
        // Calls
        // -------------------------------------------------------------------

        SilValue? Call(BoundCall call) {
            Locate(call.Syntax);

            // The core's print is a builtin: the runtime owns the writing.
            if (Core.Print.Contains(call.Function)) {
                if (Borrow(call.Arguments[0]) is not { } printed) {
                    return null;
                }

                Emit(new BuiltinInst($"print_{printed.Type}", SilType.Unit, printed));
                End(printed);
                return UnitValue.Instance;
            }

            var callee = call.Function;

            // Sable.Core's @intrinsic members: the compiler writes their bodies (SilGen.Reflection.cs).
            if (IsIntrinsic(callee)) {
                return Intrinsic(call);
            }

            // Tasks: made, and yielded to.
            if (ReferenceEquals(Owner(callee)?.OriginalDefinition, Core.Task) && callee.IsStatic) {
                if (callee.Name == "spawn") {
                    return Spawn(call);
                }

                Emit(new BuiltinInst("task_yield", SilType.Unit));
                return UnitValue.Instance;
            }

            // A class or actor's implicit 'init()'.
            if (callee is { FunctionKind: FunctionKind.Initializer, DeclaringSyntax.IsEmpty: true, Parameters.IsEmpty: true }
                && LowerType(call.Type) is ClassType implicitClass) {
                return ImplicitInit(call, implicitClass);
            }

            // Inline layout: the bytes of a value, a value from bytes, an inline array's members.
            if (callee is { Name: "of", IsStatic: true } && ReferenceEquals(callee.ContainingSymbol, Core.Bytes)) {
                return BytesOf(call);
            }

            if (callee is { Name: "fromBytes", IsStatic: true, ContainingSymbol: NamedTypeSymbol { IsInlineLayout: true } }) {
                return FromBytes(call);
            }

            if (call.Receiver is { } inlineArray && IsInlineArray(inlineArray)) {
                return callee.Name == "append" ? InlineAppend(call) : InlineArrayMember(inlineArray, callee.Name, call);
            }

            // A member of a core type: a builtin.
            if (CoreMember(call) is { } handled) {
                return handled.Value;
            }

            // A synthesised memberwise initialiser is the struct, built from its fields.
            if (callee is { FunctionKind: FunctionKind.Initializer, DeclaringSyntax.IsEmpty: true }) {
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

            // 'super.init(...)': the superclass's initialising half, given this object.
            var superInit = callee.FunctionKind == FunctionKind.Initializer && call.Receiver is BoundSuperAccess;
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

                // An inout argument passes where it is; the rest are borrowed.
                if (i < callee.Parameters.Length && callee.Parameters[i].Modifier == ParameterModifier.InOut) {
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
                ? Emit(new ClassMethodInst(arguments[0], SlotName(callee), type))
                : Emit(new FunctionRefInst(name, type));
            return Invoke(code, type, arguments, borrows, call.Syntax);
        }

        /// <summary>
        ///     Calls a function or a closure with borrowed arguments: <c>apply</c>,
        ///     or <c>try_apply</c> with the borrows ended on both of its edges.
        /// </summary>
        SilValue? Invoke(SilValue reference, FunctionType type, List<SilValue> arguments, List<SilValue> borrows, SyntaxNode at) {
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

            _current = errorBlock;
            for (var i = borrows.Count - 1; i >= 0; i--) {
                Destroy(borrows[i]);
            }

            RouteError(error, at);

            _current = normal;
            for (var i = borrows.Count - 1; i >= 0; i--) {
                Destroy(borrows[i]);
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
            var receiver = call.Receiver is { } r ? TypeSubstitution.For(Sub(r.Type)) : TypeSubstitution.Empty;
            var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();

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

            var captures = CapturesOf(closureExpression);
            var name = generator.ClosureName(function.Name);
            ClassType? contextType = null;
            if (captures.Count > 0) {
                contextType = generator._module.AddType(new ClassType($"{name}$context"));
                foreach (var capture in captures) {
                    if (LowerType(capture.Type) is not { } fieldType) {
                        return Missing(closureExpression, $"capturing a value of type '{capture.Type.ToDisplayString()}'");
                    }

                    contextType.Fields.Add(($"c{capture.Field}", capture.Boxed ? generator.BoxType(fieldType) : fieldType));
                }
            }

            // The code.
            var code = new SilFunction(name, thick.Implementation) {
                SourceFile = function.SourceFile,
                Line = source.GetLinePosition(closureExpression.Syntax.Span.Start).Line,
            };
            code.ParameterNames.AddRange(closureExpression.Symbol.Parameters.Select(static p => p.Name));
            var info = new ClosureInfo(contextType, captures.ToDictionary(static c => c.Key, static c => c));
            new FunctionLowering(generator, closureExpression.Symbol, code, source, substitution, info).Lower(closureExpression.Body);
            code.RemoveUnreachableBlocks();
            generator._module.Add(code);

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

                Emit(new StoreInst(Owned(value), Emit(new RefElementAddrInst(context, capture.Field)), StoreKind.Assign));
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

        /// <summary>A result, which may be nothing because lowering failed; null means "not a core member".</summary>
        sealed record Handled(SilValue? Value);

        /// <summary>
        ///     The members the core gives its own types — strings, numbers,
        ///     characters, arrays — as builtins on the receiver.
        /// </summary>
        Handled? CoreMember(BoundCall call) {
            var callee = call.Function;
            var owner = callee.ContainingSymbol;

            // Written in Sable — a protocol's requirement, or an extension's
            // member — rather than built in: called as any function is.
            if (!callee.DeclaringSyntax.IsEmpty || OwnerOf(callee) is { TypeKind: TypeKind.Protocol }) {
                return null;
            }

            // String.fromChar(c): a static member, with no receiver.
            if (callee is { Name: "fromChar", IsStatic: true } && ReferenceEquals(owner, Core.String)) {
                return new(Expression(call.Arguments[0]) is { } c ? Emit(new BuiltinInst("string_from_Char", SilType.String, c)) : null);
            }

            // Char.fromCodePoint(n): the scalar, when n is one.
            if (callee is { Name: "fromCodePoint", IsStatic: true } && ReferenceEquals(owner, Core.Char)) {
                if (Expression(call.Arguments[0]) is not { } code) {
                    return new((SilValue?)null);
                }

                var made = Emit(new BuiltinInst("char_from_Int", SilType.Tuple([SilType.Bool, SilType.Char]), code));
                return new(MakeOptional(call.Type, Emit(new TupleExtractInst(made, 0)), Emit(new TupleExtractInst(made, 1)), call));
            }

            if (callee.IsStatic && ReferenceEquals(owner, Core.Runtime)) {
                return new(RuntimeCall(call));
            }

            if (call.Receiver is not { } receiver) {
                return null;
            }

            // Mutating array members work on the array where it is.
            if (Sub(receiver.Type) is ArrayTypeSymbol && callee.Name is "append" or "removeLast" or "clear") {
                var borrows = new List<SilValue>();
                if (Address(receiver, borrows) is not { Type: AddressType { Pointee: ArrayType arrayType } } address) {
                    return new((SilValue?)null);
                }

                SilValue? result;
                switch (callee.Name) {
                    case "append":
                        if (Expression(call.Arguments[0]) is not { } element) {
                            return new((SilValue?)null);
                        }

                        Emit(new BuiltinInst("array_append", SilType.Unit, address, element));
                        result = UnitValue.Instance;
                        break;

                    case "removeLast":
                        result = Emit(new BuiltinInst("array_remove_last", arrayType.Element, address));
                        break;

                    default:
                        Emit(new BuiltinInst("array_clear", SilType.Unit, address));
                        result = UnitValue.Instance;
                        break;
                }

                EndAll(borrows);
                return new(result);
            }

            // 'arena.reset()': everything in it destroyed, its memory kept.
            if (Sub(receiver.Type).Equals(Core.Region) && callee.Name == "reset") {
                if (Borrow(receiver) is not { } emptied) {
                    return new((SilValue?)null);
                }

                Emit(new BuiltinInst("region_reset", SilType.Unit, emptied));
                End(emptied);
                return new(UnitValue.Instance);
            }

            // 'arena.alloc(value)': room in the region, the value moved there, its address back.
            if (Sub(receiver.Type).Equals(Core.Region) && callee.Name == "alloc") {
                if (Expression(call.Arguments[0]) is not { } allocated) {
                    return new((SilValue?)null);
                }

                Hold(allocated);
                if (Borrow(receiver) is not { } region) {
                    return new((SilValue?)null);
                }

                var room = Emit(new BuiltinInst("region_alloc", SilType.Address(allocated.Type), region));
                Unhold(allocated);
                Emit(new StoreInst(Owned(allocated), room, StoreKind.Init));
                End(region);
                return new(room);
            }

            var isCore = Sub(receiver.Type) is ArrayTypeSymbol
                || Sub(receiver.Type).Equals(Core.String) || Sub(receiver.Type).Equals(Core.Bool) || Sub(receiver.Type).Equals(Core.Char)
                || Core.Numerics.Any(n => n.Equals(Sub(receiver.Type)));
            if (!isCore) {
                return null;
            }

            if (Borrow(receiver) is not { } self) {
                return new((SilValue?)null);
            }

            var arguments = new List<SilValue>();
            foreach (var argument in call.Arguments) {
                if (Borrow(argument) is not { } value) {
                    return new((SilValue?)null);
                }

                arguments.Add(value);
            }

            var lowered = CoreMethod(callee.Name, self, arguments, call);
            for (var i = arguments.Count - 1; i >= 0; i--) {
                End(arguments[i]);
            }

            End(self);
            return new(lowered);
        }

        SilValue? CoreMethod(string name, SilValue self, List<SilValue> arguments, BoundCall call) {
            SilValue Builtin(string builtin, SilType result, params SilValue[] operands) =>
                Emit(new BuiltinInst(builtin, result, operands));

            switch (self.Type, name, arguments.Count) {
                case (_, "toString", 0) when SilBuiltins.Find($"string_from_{self.Type}") is not null:
                    return Builtin($"string_from_{self.Type}", SilType.String, self);

                case (_, "abs", 0) when self.Type == SilType.Int || self.Type == SilType.F64:
                    return Builtin($"abs_{self.Type}", self.Type, self);

                case (_, "min" or "max", 1) when self.Type == SilType.Int || self.Type == SilType.F64:
                    return Builtin($"{name}_{self.Type}", self.Type, self, arguments[0]);

                case (_, "at", 1) when self.Type == SilType.String:
                    return Builtin("string_at", SilType.Char, self, arguments[0]);

                case (_, "slice", 2) when self.Type == SilType.String:
                    return Builtin("string_slice", SilType.String, self, arguments[0], arguments[1]);

                case (_, "appending", 1) when self.Type == SilType.String:
                    return Builtin("string_concat", SilType.String, self, arguments[0]);

                case (_, "contains", 1) when self.Type == SilType.String:
                    return Builtin("string_contains", SilType.Bool, self, arguments[0]);

                case (_, "trimmed", 0) when self.Type == SilType.String:
                    return Builtin("string_trimmed", SilType.String, self);

                case (ArrayType, "slice", 2):
                    return Builtin("array_slice", self.Type, self, arguments[0], arguments[1]);

                case (_, "split", 1) when self.Type == SilType.String:
                    return Builtin("string_split", new ArrayType(SilType.String), self, arguments[0]);

                case (_, "indexOf", 1) when self.Type == SilType.String: {
                    var index = Builtin("string_index_of", SilType.Int, self, arguments[0]);
                    var found = Builtin("cmp_ge_Int", SilType.Bool, index, Emit(new IntegerLiteralInst(SilType.Int, 0)));
                    return MakeOptional(call.Type, found, index, call);
                }

                case (_, "toInt" or "toF64", 0) when self.Type == SilType.String: {
                    var target = name == "toInt" ? SilType.Int : SilType.F64;
                    var parsed = Builtin($"string_to_{target}", SilType.Tuple([SilType.Bool, target]), self);
                    return MakeOptional(call.Type, Emit(new TupleExtractInst(parsed, 0)), Emit(new TupleExtractInst(parsed, 1)), call);
                }

                default:
                    return Missing(call, $"the member '{name}' of '{call.Receiver?.Type.ToDisplayString()}'");
            }
        }

        /// <summary>
        ///     A call into the runtime: a builtin per call, its arguments
        ///     borrowed. Those that may fail give their value regardless, and
        ///     <c>io_succeeded</c> says whether they did; the value is then dropped.
        /// </summary>
        SilValue? RuntimeCall(BoundCall call) {
            var arguments = new List<SilValue>();
            foreach (var argument in call.Arguments) {
                if (Borrow(argument) is not { } value) {
                    return null;
                }

                arguments.Add(value);
            }

            var strings = new ArrayType(SilType.String);
            var (builtin, result, fallible) = call.Function.Name switch {
                "write" => ("io_write", SilType.Unit, false),
                "writeError" => ("io_write_error", SilType.Unit, false),
                "readLine" => ("io_read_line", SilType.String, true),
                "readFile" => ("io_read_file", SilType.String, true),
                "writeFile" => ("io_write_file", SilType.Bool, false),
                "fileExists" => ("io_file_exists", SilType.Bool, false),
                "isDirectory" => ("io_is_directory", SilType.Bool, false),
                "removeFile" => ("io_remove_file", SilType.Bool, false),
                "makeDirectory" => ("io_make_directory", SilType.Bool, false),
                "listDirectory" => ("io_list_directory", (SilType)strings, true),
                "environment" => ("io_environment", SilType.String, true),
                "argumentCount" => ("io_argument_count", SilType.Int, false),
                "argument" => ("io_argument", SilType.String, false),
                "monotonicNanoseconds" => ("time_monotonic", SilType.Int, false),
                "wallClockNanoseconds" => ("time_wall_clock", SilType.Int, false),
                "sleep" => ("time_sleep", SilType.Unit, false),
                "run" => ("process_run", SilType.String, true),
                "lastExitStatus" => ("process_status", SilType.Int, false),
                "lastErrorOutput" => ("process_error_output", SilType.String, false),
                "currentDirectory" => ("process_current_directory", SilType.String, false),
                "changeDirectory" => ("process_change_directory", SilType.Bool, false),
                "processId" => ("process_id", SilType.Int, false),
                _ => (null!, SilType.Unit, false),
            };

            if (builtin is null) {
                return Missing(call, $"the runtime call '{call.Function.Name}'");
            }

            var made = Emit(new BuiltinInst(builtin, result, [..arguments]));
            for (var i = arguments.Count - 1; i >= 0; i--) {
                End(arguments[i]);
            }

            if (result == SilType.Unit) {
                return UnitValue.Instance;
            }

            if (!fallible) {
                return made;
            }

            var present = Emit(new BuiltinInst("io_succeeded", SilType.Bool));
            return MakeOptional(call.Type, present, made, call, owned: true);
        }

        /// <summary>An optional holding the value when the flag is set, and none otherwise.</summary>
        SilValue? MakeOptional(TypeSymbol type, SilValue present, SilValue value, BoundNode at, bool owned = false) {
            if (LowerType(type) is not EnumType optional) {
                return Missing(at, $"an optional of type '{type.ToDisplayString()}'");
            }

            var some = function.AddBlock();
            var none = function.AddBlock();
            var after = function.AddBlock();
            var result = after.AddArgument(optional);
            Jump(new CondBranchInst(present, some, [], none, []));
            _current = some;
            Branch(after, Emit(new EnumInst(optional, optional.CaseIndex("Some"), value)));
            _current = none;
            if (owned) {
                // Made either way; without the flag, nobody wants it.
                Emit(new DestroyValueInst(value));
            }

            Branch(after, Emit(new EnumInst(optional, optional.CaseIndex("None"), null)));
            _current = after;
            return result;
        }

        /// <summary>The computed properties the core gives its own types, and a property read through an existential.</summary>
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

            var name = access.Field.Name;
            string? builtin = (Sub(receiver.Type), name) switch {
                (ArrayTypeSymbol, "count" or "isEmpty") => "array_count",
                (var t, "count" or "isEmpty") when t.Equals(Core.String) => "string_count",
                (_, "isDigit") when Sub(receiver.Type).Equals(Core.Char) => "char_is_digit",
                (_, "codePoint") when Sub(receiver.Type).Equals(Core.Char) => "char_to_Int",
                (_, "isLetter") when Sub(receiver.Type).Equals(Core.Char) => "char_is_letter",
                (_, "isWhitespace") when Sub(receiver.Type).Equals(Core.Char) => "char_is_whitespace",
                _ => null,
            };

            if (builtin is null) {
                return Missing(access, $"the property '{name}' of '{receiver.Type.ToDisplayString()}'");
            }

            if (Borrow(receiver) is not { } self) {
                return null;
            }

            var resultType = builtin.EndsWith("count", StringComparison.Ordinal) || builtin == "char_to_Int" ? SilType.Int : SilType.Bool;
            SilValue result = Emit(new BuiltinInst(builtin, resultType, self));
            End(self);

            if (name == "isEmpty") {
                result = Emit(new BuiltinInst("cmp_eq_Int", SilType.Bool, result, Emit(new IntegerLiteralInst(SilType.Int, 0))));
            }

            return result;
        }

        static string Describe(SyntaxNode syntax) {
            var text = syntax.ToString().Trim();
            var line = text.Split('\n')[0];
            return line.Length > 40 ? line[..40] + "…" : line;
        }
    }
}
