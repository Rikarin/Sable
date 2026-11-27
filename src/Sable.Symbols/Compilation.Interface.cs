using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     A compilation that takes the standard library's declarations from its
///     interface (<see cref="LibrarySymbols" />, docs/05 §5.4) rather than from
///     its files.
/// </summary>
/// <remarks>
///     The symbols answer as those declared from source do. What only a file
///     says is found when it is asked for: <see cref="Symbol.DeclaringSyntax" />
///     parses the file, <see cref="GetDeclaredSymbol" /> gives its declarations
///     their symbols, and <see cref="GetFileScope" /> resolves its imports — so a
///     body, a default argument or a constant's value of the library's is bound
///     from its file as it is in a compilation that declared it.
/// </remarks>
public sealed partial class Compilation {
    readonly InterfaceLibrary? _interface;

    /// <summary>The program extends a type of the library's: its symbols are then declared from source, in the order that gives.</summary>
    bool _extendsInterface;

    /// <summary>Whether the library's declarations were taken from its interface.</summary>
    public bool IsAgainstInterface => _interface is not null;

    /// <summary>Whether a file is one of the library's whose declarations were taken from its interface.</summary>
    public bool IsFromInterface(SyntaxTree tree) => _interface is not null && _interface.Contains(tree);

    /// <summary>Whether a declaration was taken from the library's interface.</summary>
    public bool IsTakenFromInterface(Symbol symbol) => symbol.Source is { } source && IsFromInterface(source.Tree);

    /// <summary>
    ///     The declarations of a file whose declarations were taken from the
    ///     interface — functions, types, fields and constants, as
    ///     <see cref="GetDeclaredSymbol" /> gives them — in document order.
    /// </summary>
    public IReadOnlyList<Symbol> InterfaceDeclarations(SyntaxTree tree) => _interface?.DeclarationsOf(tree) ?? [];

    /// <summary>The accessors of a property or a subscript taken from the interface, in document order, and whether each is a protocol's requirement.</summary>
    public IEnumerable<(FunctionSymbol Accessor, bool IsRequirement)> InterfaceAccessors(Symbol owner) =>
        (owner switch {
            FieldSymbol field => field.InterfaceAccessors,
            FunctionSymbol subscript => subscript.InterfaceAccessors,
            _ => [],
        }).Select(a => (a, _interface!.Requirements.Contains(a)));

    /// <summary>A compilation of these files that declares the library from its source, whatever <see cref="LibrarySymbols.Mode" /> says.</summary>
    internal static Compilation FromSource(ImmutableArray<SyntaxTree> trees, ParseOptions options) => new(trees, options);

    /// <summary>The declarations that are in no member list: the types array extensions declare their members on, and every extension.</summary>
    internal IEnumerable<Symbol> ExtensionRoots() =>
        [..ArrayExtensions, ..Extensions.Select(e => GetDeclaredSymbol(e.Syntax)).OfType<ExtensionSymbol>()];

    internal bool HasUserAttributes => _userAttributes.Count > 0;

    internal bool HasInlineLayouts => _inlineLayouts.Count > 0;

    /// <summary>The names functions with an <c>@autoclosure</c> parameter are called by, as the files declare them.</summary>
    internal IEnumerable<string> DeclaredAutoclosureCallees => AutoclosureCallees;

    void AddDerivation(Derivation derivation) {
        _derived.Add(derivation);
        _derivedByFunction[derivation.Function] = derivation;
    }

    /// <summary>What a compilation keeps of the library it takes from an interface.</summary>
    internal void AttachInterface(InterfaceLibrary library) {
        foreach (var derivation in library.Derivations) {
            AddDerivation(derivation);
        }

        foreach (var (init, inherited) in library.InheritedInitializers) {
            _inheritedInitializers[init] = inherited;
        }

        foreach (var carrier in library.Carriers) {
            ArrayExtensions.Add(carrier);
            if (!carrier.BaseTypes.IsEmpty) {
                FindArrayMembers(carrier);
            }
        }
    }

    internal FileScope DeclareInterfaceScope(SyntaxTree tree, ModuleSymbol module) {
        var scope = new FileScope(tree, module);
        _fileScopes[tree] = scope;
        if (tree.Root is CompilationUnitSyntax unit) {
            // Its tiers were checked as the library was compiled (S3239).
            ResolveImports(unit, scope, importer: null);
        }

        return scope;
    }
}

/// <summary>The library a compilation takes from an interface: its symbols, by the files they are declared in.</summary>
internal sealed class InterfaceLibrary {
    readonly Compilation _compilation;
    readonly LibrarySymbols _library;
    readonly IReadOnlyList<Symbol> _table;
    readonly Dictionary<SyntaxTree, List<Symbol>> _declared = [];
    readonly HashSet<SyntaxTree> _recorded = [];
    readonly HashSet<ModuleSymbol> _modules;

    public InterfaceLibrary(Compilation compilation, LibrarySymbols library) {
        _compilation = compilation;
        _library = library;
        Modules = library.ReadModules(out _table);
        _modules = new(Modules, ReferenceEqualityComparer.Instance);
        foreach (var tree in library.Trees) {
            _declared[tree] = [];
        }
    }

    public IReadOnlyList<ModuleSymbol> Modules { get; }

    /// <summary>The types the library's array extensions declare their members on, in order.</summary>
    public List<NamedTypeSymbol> Carriers { get; } = [];

    public List<Derivation> Derivations { get; } = [];

    /// <summary>The initialisers its classes inherit.</summary>
    public Dictionary<FunctionSymbol, Compilation.InheritedInitializer> InheritedInitializers { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>The library's accessors that are protocols' requirements.</summary>
    public HashSet<FunctionSymbol> Requirements { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>The names its functions with an <c>@autoclosure</c> parameter are called by.</summary>
    public List<string> AutoclosureCallees { get; } = [];

    /// <summary>Puts the library's declarations in the compilation, once it has its core types.</summary>
    public void Attach() {
        _library.ReadFacts(_compilation, _table, this);
        foreach (var declared in _declared.Values) {
            declared.Sort(static (a, b) => a.Source!.Start.CompareTo(b.Source!.Start));
        }

        _compilation.AttachInterface(this);
    }

    /// <summary>A declaration of a file.</summary>
    public void Add(SyntaxTree tree, Symbol symbol) => _declared[tree].Add(symbol);

    public bool Contains(SyntaxTree tree) => _declared.ContainsKey(tree);

    public IReadOnlyList<Symbol> DeclarationsOf(SyntaxTree tree) => _declared.GetValueOrDefault(tree) ?? [];

    /// <summary>Whether a type is one of the library's.</summary>
    public bool Declares(NamedTypeSymbol type) => type.ContainingModule is { } module && _modules.Contains(module);

    /// <summary>A file's scope, its imports resolved.</summary>
    public FileScope? ScopeOf(SyntaxTree tree) =>
        Contains(tree) && Modules.FirstOrDefault(m => m.Name == tree.Header.Module) is { } module ? _compilation.DeclareInterfaceScope(tree, module) : null;

    /// <summary>
    ///     Gives every declaration of the file a node is in its symbol, the
    ///     first time one of them is asked for; false when there is nothing to
    ///     record for it.
    /// </summary>
    public bool Record(SyntaxNode node) {
        if (_compilation.TreeOf(node) is not { } tree || !Contains(tree) || !_recorded.Add(tree)) {
            return false;
        }

        foreach (var symbol in _declared[tree]) {
            _compilation.RecordDeclaration(symbol.DeclaringSyntax[0], symbol);
        }

        return true;
    }
}
