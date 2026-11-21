using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     What one file can see: the module it declares, and what it imports.
/// </summary>
/// <remarks>
///     Imports are per file, not per module, so a module spread over several files
///     does not leak one file's imports into another.
/// </remarks>
public sealed class FileScope {
    internal FileScope(SyntaxTree tree, ModuleSymbol module) {
        Tree = tree;
        Module = module;
    }

    public SyntaxTree Tree { get; }
    public ModuleSymbol Module { get; }

    /// <summary>Modules brought into scope wholesale by <c>import</c>.</summary>
    public List<ModuleSymbol> Imports { get; } = [];

    /// <summary>Aliases introduced by <c>import A.B as C</c>.</summary>
    public Dictionary<string, ModuleSymbol> ImportAliases { get; } =
        new(StringComparer.Ordinal);

    /// <summary>Names brought in individually by <c>import A.B.{ X, Y }</c>.</summary>
    public Dictionary<string, Symbol> ImportedNames { get; } = new(StringComparer.Ordinal);
}

/// <summary>
///     A set of source files analysed together, and the symbols they declare.
/// </summary>
/// <remarks>
///     <para>
///         The unit of semantic analysis, as docs/04 §1.1 describes: syntax trees
///         plus options, immutable, with <c>With…</c> producing a new one.
///     </para>
///     <para>
///         Declarations are gathered eagerly, because that pass is cheap and
///         everything else needs the table. Signatures bind lazily, so asking about
///         one declaration does not bind the program.
///     </para>
/// </remarks>
public sealed partial class Compilation {
    readonly Dictionary<string, ModuleSymbol> _modules = new(StringComparer.Ordinal);
    readonly Dictionary<SyntaxTree, FileScope> _fileScopes = [];
    readonly Dictionary<SyntaxNode, Symbol> _symbolsBySyntax = [];
    readonly Dictionary<SyntaxTree, SemanticModel> _semanticModels = [];
    readonly DiagnosticBag _declarationDiagnostics = new();

    Compilation(ImmutableArray<SyntaxTree> syntaxTrees, ParseOptions options) {
        // Sable.Core's source comes with every compilation, after the files
        // given, so that those keep their places (CoreLibrary). A
        // compilation made from another passes the core's trees back in;
        // they are not added twice. A file that declares Sable.Core itself
        // stands in for the embedded file of its name (CoreLibrary.Brought).
        // The core is parsed for the target the program's files are: what
        // its '#if os(...)' says follows what theirs does.
        var target = syntaxTrees.FirstOrDefault(static t => !CoreLibrary.Contains(t))?.Options ?? options;
        var own = syntaxTrees.Where(static t => !CoreLibrary.Contains(t)).ToList();
        var brought = CoreLibrary.Brought(own, m => m == CoreLibrary.ModuleName ? CoreLibrary.TreesFor(target) : []);
        SyntaxTrees = [..own, ..CoreLibrary.TreesFor(target).Where(t => !CoreLibrary.IsBrought(t, brought))];
        Options = options;

        var coreModule = new ModuleSymbol("Sable.Core");
        _modules[coreModule.Name] = coreModule;
        Core = new(coreModule);

        TypeBinder = new(this);

        DeclareAll();
    }

    public static Compilation Create(
        IEnumerable<SyntaxTree> syntaxTrees,
        ParseOptions? options = null
    ) =>
        new([..syntaxTrees], options ?? ParseOptions.Default);

    public static Compilation Create(params string[] sources) =>
        Create(
            sources.Select(static (text, i) =>
                SyntaxTree.Parse(new(text, $"file{i}.sb"))
            )
        );

    public ImmutableArray<SyntaxTree> SyntaxTrees { get; }
    public ParseOptions Options { get; }

    /// <summary>What binding is allowed to spend; see <see cref="BindOptions" />.</summary>
    public BindOptions BindOptions { get; init; } = BindOptions.Default;

    public CoreTypes Core { get; }

    internal TypeBinder TypeBinder { get; }

    public IEnumerable<ModuleSymbol> Modules => _modules.Values;

    public ModuleSymbol? GetModule(string name) => _modules.TryGetValue(name, out var module) ? module : null;

    /// <summary>
    ///     The module a dotted name written in a file names: in full,
    ///     <c>Sable.Math</c>, or through an alias the file imported,
    ///     <c>M</c> for <c>import Sable.Math as M</c> — whose first segment
    ///     the alias stands for, so <c>S.Math</c> works for an alias of
    ///     <c>Sable</c>'s module path too.
    /// </summary>
    public ModuleSymbol? ResolveModule(string dottedName, FileScope? scope) {
        if (scope is not null) {
            var dot = dottedName.IndexOf('.');
            var first = dot < 0 ? dottedName : dottedName[..dot];
            if (scope.ImportAliases.TryGetValue(first, out var aliased)) {
                return dot < 0 ? aliased : GetModule(aliased.Name + dottedName[dot..]);
            }
        }

        return GetModule(dottedName);
    }

    public FileScope GetFileScope(SyntaxTree tree) => _fileScopes[tree];

    /// <summary>The scope of the file a node was parsed from, if it is one of this compilation's.</summary>
    internal FileScope? FileScopeOf(SyntaxNode node) {
        var root = node;
        while (root.Parent is { } parent) {
            root = parent;
        }

        foreach (var (tree, scope) in _fileScopes) {
            if (ReferenceEquals(tree.Root, root)) {
                return scope;
            }
        }

        return null;
    }

    /// <summary>The symbol a declaration introduced, if it introduced one.</summary>
    public Symbol? GetDeclaredSymbol(SyntaxNode declaration) =>
        _symbolsBySyntax.TryGetValue(declaration, out var symbol) ? symbol : null;

    /// <summary>The semantic model for a file, which binds its bodies on demand.</summary>
    public SemanticModel GetSemanticModel(SyntaxTree tree) {
        if (_semanticModels.TryGetValue(tree, out var model)) {
            return model;
        }

        model = new(this, tree);
        _semanticModels[tree] = model;
        return model;
    }

    /// <summary>
    ///     Every diagnostic, including those from binding function bodies.
    /// </summary>
    public IReadOnlyList<Diagnostic> GetAllDiagnostics() {
        var all = new List<Diagnostic>();

        foreach (var tree in SyntaxTrees) {
            all.AddRange(tree.Diagnostics);
        }

        // Bodies first, then the lazy bags: binding a body resolves types too
        // — a local's annotation, a cast, a pattern's type — and a deferred
        // declaration check may add to the declaration bag long after the
        // table was built.
        foreach (var tree in SyntaxTrees) {
            if (!_fileScopes.ContainsKey(tree)) {
                continue;
            }

            all.AddRange(GetSemanticModel(tree).GetDiagnostics());
        }

        TypeBinder.ForceAll();
        all.AddRange(_declarationDiagnostics);
        all.AddRange(TypeBinder.Diagnostics);

        all.Sort(static (a, b) => a.Span.Start.CompareTo(b.Span.Start));
        return all;
    }

    /// <summary>
    ///     The file being declared right now. Signature binding captures it, so
    ///     that a lazily bound type resolves against the right imports.
    /// </summary>
    internal FileScope? CurrentScope { get; private set; }

    /// <summary>Adds files, returning a new compilation.</summary>
    public Compilation AddSyntaxTrees(params SyntaxTree[] trees) =>
        new([..SyntaxTrees, ..trees], Options) { BindOptions = BindOptions };

    /// <summary>Returns a compilation that binds under different limits.</summary>
    public Compilation WithBindOptions(BindOptions options) => new([..SyntaxTrees], Options) { BindOptions = options };

    /// <summary>Looks a type up by its dotted path, such as <c>App.Shapes.Circle</c>.</summary>
    public NamedTypeSymbol? GetTypeByQualifiedName(string qualifiedName) {
        foreach (var module in _modules.Values) {
            if (qualifiedName.StartsWith(module.Name, StringComparison.Ordinal)
                && qualifiedName.Length > module.Name.Length
                && qualifiedName[module.Name.Length] == '.') {
                var rest = qualifiedName[(module.Name.Length + 1)..];
                var found = LookupNested(module.Members, rest);
                if (found is not null) {
                    return found;
                }
            } else if (module.Name.Length == 0) {
                var found = LookupNested(module.Members, qualifiedName);
                if (found is not null) {
                    return found;
                }
            }
        }

        return null;
    }

    static NamedTypeSymbol? LookupNested(IReadOnlyList<Symbol> members, string path) {
        var dot = path.IndexOf('.', StringComparison.Ordinal);
        var head = dot < 0 ? path : path[..dot];

        foreach (var member in members) {
            if (member is not NamedTypeSymbol type) {
                continue;
            }

            if (!string.Equals(type.Name, head, StringComparison.Ordinal)) {
                continue;
            }

            return dot < 0 ? type : LookupNested([..type.Members], path[(dot + 1)..]);
        }

        return null;
    }

    /// <summary>
    ///     Every diagnostic: parse errors, declaration errors, and whatever
    ///     binding the signatures turns up.
    /// </summary>
    public IReadOnlyList<Diagnostic> GetDiagnostics() {
        var all = new List<Diagnostic>();

        foreach (var tree in SyntaxTrees) {
            all.AddRange(tree.Diagnostics);
        }

        // Signatures bind lazily, and binding one can add to the declaration
        // bag as well, so force before collecting either.
        TypeBinder.ForceAll();
        all.AddRange(_declarationDiagnostics);
        all.AddRange(TypeBinder.Diagnostics);

        all.Sort(static (a, b) => a.Span.Start.CompareTo(b.Span.Start));
        return all;
    }

    /// <summary>
    ///     Every diagnostic with the file it is in, where that is known: a
    ///     span is an offset into one file, and several files' offsets overlap.
    ///     Parse and body diagnostics know their file, and so do most from
    ///     declaring (<see cref="FileOf" />); those from lazily bound
    ///     signatures and late checks when they were reported with an anchor
    ///     (<see cref="Diagnostic.Anchor" />); the rest come without one.
    /// </summary>
    public IReadOnlyList<(Diagnostic Diagnostic, SyntaxTree? Tree)> GetAllDiagnosticsByFile() => GetAllDiagnosticsByFile(static _ => true);

    /// <summary>
    ///     The same, with the bodies of only the files <paramref name="bodiesOf" />
    ///     picks bound and checked: the declarations and signatures of every
    ///     file still are. What a build asks once the standard library is
    ///     known to check cleanly with whatever program it is compiled with
    ///     (<see cref="ReachesIntoLibrary" />).
    /// </summary>
    public IReadOnlyList<(Diagnostic Diagnostic, SyntaxTree? Tree)> GetAllDiagnosticsByFile(Func<SyntaxTree, bool> bodiesOf) {
        var all = new List<(Diagnostic, SyntaxTree?)>();
        foreach (var tree in SyntaxTrees) {
            all.AddRange(tree.Diagnostics.Select(d => (d, (SyntaxTree?)tree)));
        }

        foreach (var tree in SyntaxTrees) {
            if (_fileScopes.ContainsKey(tree) && bodiesOf(tree)) {
                all.AddRange(GetSemanticModel(tree).GetDiagnostics().Select(d => (d, (SyntaxTree?)tree)));
            }
        }

        TypeBinder.ForceAll();
        all.AddRange(_declarationDiagnostics.Select(d => (d, FileOf(d))));
        all.AddRange(TypeBinder.Diagnostics.Select(d => (d, FileOf(d))));
        return all;
    }

    /// <summary>
    ///     Whether the files given — a program's own — can change how the
    ///     compilation's other files, the standard library's, bind or lower:
    ///     by extending one of the library's types or protocols (which adds
    ///     members and conformances to the library's own symbols, where its
    ///     bodies' overload resolution sees them), by subclassing one of its
    ///     classes (which makes the class's methods dispatch), or by declaring
    ///     a file of one of its modules. Name lookup in a library file sees
    ///     only its module, Sable.Core and what it imports, never a program's
    ///     module, so a program that does none of these leaves the library's
    ///     bodies binding, and lowering, as they do with any other.
    /// </summary>
    public bool ReachesIntoLibrary(IReadOnlyCollection<SyntaxTree> own) {
        var libraryModules = SyntaxTrees.Where(t => !own.Contains(t)).Select(CoreLibrary.ModuleOf).OfType<string>().ToHashSet(StringComparer.Ordinal);
        libraryModules.Add(CoreLibrary.ModuleName);
        bool IsLibrary(TypeSymbol type) =>
            type is not NamedTypeSymbol named || named.OriginalDefinition.ContainingModule?.Name is not { } module || libraryModules.Contains(module);

        foreach (var tree in own) {
            if (CoreLibrary.ModuleOf(tree) is { } module && libraryModules.Contains(module)) {
                return true;
            }

            foreach (var node in tree.Root.DescendantNodes()) {
                switch (node) {
                    case ExtensionDeclarationSyntax extension:
                        if (GetDeclaredSymbol(extension) is not ExtensionSymbol { ExtendedType: var extended } || IsLibrary(extended)) {
                            return true;
                        }

                        break;
                    case ClassDeclarationSyntax or ActorDeclarationSyntax:
                        if (GetDeclaredSymbol(node) is NamedTypeSymbol type
                            && type.BaseTypes.Any(b => b is NamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Actor } && IsLibrary(b))) {
                            return true;
                        }

                        break;
                }
            }
        }

        return false;
    }

    /// <summary>A declaration diagnostic found by a later check, in the file it was captured from.</summary>
    void ReportDeclaration(Diagnostic diagnostic, SyntaxTree? tree) {
        _declarationDiagnostics.Add(diagnostic);
        if (tree is not null) {
            lock (_files) {
                _files[diagnostic] = tree;
            }
        }
    }

    internal void ReportDeclaration(ErrorCode code, TextSpan span, string message, DiagnosticLocation? related = null) {
        var diagnostic = Diagnostic.Error(code, span, message).WithRelated(related);
        _declarationDiagnostics.Add(diagnostic);

        // The file being declared is the one the span is in. A check run
        // later, with none being declared, leaves its diagnostic unplaced.
        if (CurrentScope is { Tree: var tree } && span.End <= tree.Text.Length) {
            lock (_files) {
                _files[diagnostic] = tree;
            }
        }
    }

    /// <summary>The declaration diagnostics whose file is known, by the diagnostic itself rather than its value.</summary>
    readonly Dictionary<Diagnostic, SyntaxTree> _files = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    ///     The file a diagnostic of this compilation's is in, when it is
    ///     known: a body's or a parse's always is, and a declaration's when it
    ///     was reported while its file was being declared. Null otherwise.
    /// </summary>
    public SyntaxTree? FileOf(Diagnostic diagnostic) {
        lock (_files) {
            if (_files.GetValueOrDefault(diagnostic) is { } known) {
                return known;
            }
        }

        // Reported away from its file's pass, but with a node of that file.
        if (diagnostic.Anchor is { } anchor && TreeOfRoot(anchor) is { } tree && diagnostic.Span.End <= tree.Text.Length) {
            return tree;
        }

        return null;
    }

    /// <summary>The trees by their roots: which file a node is in.</summary>
    Dictionary<SyntaxNode, SyntaxTree>? _treesByRoot;

    SyntaxTree? TreeOfRoot(SyntaxNode node) {
        while (node.Parent is { } parent) {
            node = parent;
        }

        var trees = LazyInitializer.EnsureInitialized(ref _treesByRoot, () => SyntaxTrees.ToDictionary(static SyntaxNode (t) => t.Root, static t => t, ReferenceEqualityComparer.Instance));
        return trees.GetValueOrDefault(node);
    }

    // -----------------------------------------------------------------------
    // Declaration gathering
    // -----------------------------------------------------------------------

    /// <summary>
    ///     A package or workspace manifest (docs/05 §5.2), or the code the
    ///     toolchain writes to evaluate one: these are evaluated on their own,
    ///     and need no module declaration.
    /// </summary>
    public static bool IsManifestFile(SyntaxTree tree) =>
        Path.GetFileName(tree.Text.FileName) is "package.sb" or "workspace.sb" || Path.GetFileName(tree.Text.FileName).StartsWith('<');

    /// <summary>
    ///     The module a manifest's declarations go in: one per directory, so
    ///     that a workspace's manifests, compiled together in the editor, do
    ///     not clash, and no package's code can see one.
    /// </summary>
    static string ManifestModule(SyntaxTree tree) => "Manifest:" + Path.GetDirectoryName(Path.GetFullPath(tree.Text.FileName));

    void DeclareAll() {
        var modulePackages = new Dictionary<ModuleSymbol, string>(ReferenceEqualityComparer.Instance);
        foreach (var tree in SyntaxTrees) {
            if (tree.Root is not CompilationUnitSyntax unit) {
                continue;
            }

            // "One module declaration per file, first non-trivia item"
            // (spec/SYNTAX.md §1). An empty file is let be; one that declares
            // something has to say where it belongs.
            if (unit.Module is null && unit.Members.Count > 0 && !IsManifestFile(tree)) {
                ReportDeclaration(
                    ErrorCode.ERR_NoModuleDeclaration,
                    new(unit.Members[0].Span.Start, 0),
                    "a file that declares anything needs a 'module' declaration first"
                );
            }

            var moduleName = unit.Module is not null ? NameOf(unit.Module.Name)
                : IsManifestFile(tree) ? ManifestModule(tree)
                : string.Empty;

            if (!_modules.TryGetValue(moduleName, out var module)) {
                module = new(moduleName);
                _modules[moduleName] = module;
            }

            var scope = new FileScope(tree, module);
            _fileScopes[tree] = scope;
            CurrentScope = scope;

            // A module is one package's: two declaring it would merge into
            // one module, each reaching the other's internals.
            if (tree.Options.Package is { } package && unit.Module is { } written) {
                if (!modulePackages.TryAdd(module, package) && modulePackages[module] is var first && first != package) {
                    ReportDeclaration(
                        ErrorCode.ERR_ModuleInTwoPackages,
                        written.Name.Span,
                        $"module '{moduleName}' is declared by package '{first}' and by package '{package}'; "
                        + "a module belongs to one package, so give this one a name under its own package's"
                    );
                }
            }
            CheckAttributes(tree);

            foreach (var member in unit.Members) {
                DeclareMember(member, module);
            }
        }

        CurrentScope = null;

        // Imports resolve against the finished module set.
        foreach (var tree in SyntaxTrees) {
            if (tree.Root is not CompilationUnitSyntax unit) {
                continue;
            }

            ResolveImports(unit, _fileScopes[tree]);
        }

        AttachExtensions();
        DeclareInlineLayouts();
        DeclareDerivations();
    }

    void ResolveImports(CompilationUnitSyntax unit, FileScope scope) {
        foreach (var import in unit.Imports) {
            var name = NameOf(import.Name);
            var module = GetModule(name);

            if (module is null) {
                ReportDeclaration(
                    ErrorCode.ERR_ModuleNotFound,
                    import.Name.Span,
                    $"no module named '{name}' in this compilation"
                );
                continue;
            }

            if (import.Items is { } items) {
                foreach (var item in items.Items) {
                    if (item.NameToken.Kind == SyntaxKind.StarToken) {
                        scope.Imports.Add(module);
                        continue;
                    }

                    var target = module.GetMembers(item.NameToken.Text).FirstOrDefault();
                    if (target is null) {
                        ReportDeclaration(
                            ErrorCode.ERR_NameNotFound,
                            item.NameToken.Span,
                            $"'{module.Name}' has no member named '{item.NameToken.Text}'"
                        );
                        continue;
                    }

                    var alias = item.Alias.IsNone ? item.NameToken.Text : item.Alias.Text;
                    scope.ImportedNames[alias] = target;
                }

                continue;
            }

            if (!import.Alias.IsNone) {
                scope.ImportAliases[import.Alias.Text] = module;
            } else {
                scope.Imports.Add(module);
            }
        }
    }

    void DeclareMember(SyntaxNode member, Symbol container) {
        switch (member) {
            case StructDeclarationSyntax declaration:
                DeclareType(
                    declaration,
                    declaration.Identifier,
                    TypeKind.Struct,
                    declaration.Attributes,
                    declaration.Modifiers,
                    declaration.TypeParameters,
                    declaration.Members,
                    container
                );
                return;

            case ClassDeclarationSyntax declaration:
                DeclareType(
                    declaration,
                    declaration.Identifier,
                    TypeKind.Class,
                    declaration.Attributes,
                    declaration.Modifiers,
                    declaration.TypeParameters,
                    declaration.Members,
                    container
                );
                return;

            case ActorDeclarationSyntax declaration:
                DeclareType(
                    declaration,
                    declaration.Identifier,
                    TypeKind.Actor,
                    declaration.Attributes,
                    declaration.Modifiers,
                    declaration.TypeParameters,
                    declaration.Members,
                    container
                );
                return;

            case ProtocolDeclarationSyntax declaration:
                DeclareType(
                    declaration,
                    declaration.Identifier,
                    TypeKind.Protocol,
                    declaration.Attributes,
                    declaration.Modifiers,
                    declaration.TypeParameters,
                    declaration.Members,
                    container
                );
                return;

            case UnionDeclarationSyntax declaration:
                DeclareType(
                    declaration,
                    declaration.Identifier,
                    TypeKind.Union,
                    declaration.Attributes,
                    declaration.Modifiers,
                    declaration.TypeParameters,
                    declaration.Members,
                    container,
                    !declaration.VariableSizeToken.IsNone
                );
                return;

            case EnumDeclarationSyntax declaration:
                DeclareType(
                    declaration,
                    declaration.Identifier,
                    TypeKind.Enum,
                    declaration.Attributes,
                    declaration.Modifiers,
                    null,
                    declaration.Members,
                    container
                );
                return;

            case FunctionDeclarationSyntax declaration:
                DeclareFunction(declaration, container);
                return;

            case FieldDeclarationSyntax declaration:
                DeclareField(declaration, container);
                return;

            case ConstDeclarationSyntax declaration:
                DeclareConstant(declaration, container);
                return;

            case AliasDeclarationSyntax declaration:
                DeclareAlias(declaration, container);
                return;

            case UnionCaseDeclarationSyntax declaration
                when container is NamedTypeSymbol union:
                DeclareUnionCase(declaration, union);
                return;

            case EnumCaseDeclarationSyntax declaration
                when container is NamedTypeSymbol enumType:
                DeclareEnumCase(declaration, enumType);
                return;

            case InitDeclarationSyntax declaration when container is NamedTypeSymbol type:
                DeclareInitializer(declaration, type, type);
                return;

            // Only a value type's extension's, or a growable array's, gets
            // this far (RejectInExtension).
            case InitDeclarationSyntax declaration when container is ExtensionSymbol extension:
                DeclareInitializer(declaration, extension, extension.ExtendedType);
                return;

            case OperatorDeclarationSyntax declaration:
                DeclareOperator(declaration, container);
                return;

            case SubscriptDeclarationSyntax declaration:
                DeclareSubscript(declaration, container);
                return;

            case DeinitDeclarationSyntax declaration when container is NamedTypeSymbol type:
                DeclareDeinitializer(declaration, type);
                return;

            case AssociatedTypeDeclarationSyntax declaration
                when container is NamedTypeSymbol protocol:
                DeclareAssociatedType(declaration, protocol);
                return;

            case ExtensionDeclarationSyntax declaration:
                // Members of an extension are declared on the extended type once
                // type binding exists; recorded for now so they are not lost.
                DeclareExtension(declaration, container);
                return;

            default:
                return;
        }
    }

    void DeclareType(
        SyntaxNode declaration,
        SyntaxToken identifier,
        TypeKind typeKind,
        SyntaxList<AttributeSyntax> attributes,
        SyntaxTokenList modifiers,
        GenericParameterListSyntax? typeParameters,
        SyntaxList<SyntaxNode> members,
        Symbol container,
        bool isVariableSize = false
    ) {
        var name = identifier.Text;
        if (name.Length == 0) {
            return;
        }

        if (FindExisting(container, name) is { } existing) {
            ReportDuplicate(identifier, name, existing);
            return;
        }

        var type = new NamedTypeSymbol(
            name,
            typeKind,
            container,
            declaration,
            AccessibilityOf(modifiers, container),
            isVariableSize
        ) {
            IsOpen = modifiers.Any(SyntaxKind.OpenKeyword),
            IsSealed = modifiers.Any(SyntaxKind.SealedKeyword),
            IsFinal = modifiers.Any(SyntaxKind.FinalKeyword),
            IsAbstract = modifiers.Any(SyntaxKind.AbstractKeyword),
            IsNoncopyable = typeKind is TypeKind.Struct or TypeKind.Union
                && attributes.Any(static a => a.Name.ToString().Trim() == "noncopy"),
            IsInlineLayout = typeKind is TypeKind.Struct
                && attributes.Any(static a => a.Name.ToString().Trim() == "inline_layout"),
            IsReflectable = attributes.Any(static a => a.Name.ToString().Trim() == "reflectable"),
            IsFlags = typeKind is TypeKind.Enum && attributes.Any(static a => a.Name.ToString().Trim() == "flags"),
            IsPacked = typeKind is TypeKind.Struct && attributes.Any(static a => a.Name.ToString().Trim() == "packed"),
            Alignment = typeKind is TypeKind.Struct ? AlignmentOf(attributes) : 0,
        };

        // A protocol has no values to look at, and so nothing to describe.
        if (type.IsReflectable && typeKind is not (TypeKind.Struct or TypeKind.Class or TypeKind.Actor or TypeKind.Union or TypeKind.Enum)) {
            type.IsReflectable = false;
            ReportDeclaration(
                ErrorCode.ERR_NotReflectableKind,
                attributes.First(static a => a.Name.ToString().Trim() == "reflectable").Span,
                $"only a struct, class, actor, union or enum can be @reflectable, and '{name}' is a {typeKind.ToString().ToLowerInvariant()}"
            );
        }

        // Its conformance to InlineLayout, and 'fromBytes', once Sable.Core is declared.
        if (type.IsInlineLayout) {
            _inlineLayouts.Add((type, attributes.First(static a => a.Name.ToString().Trim() == "inline_layout")));
        }

        if (typeParameters is not null) {
            type.TypeParameters = [
                ..typeParameters.Parameters.Select((parameter, i) => {
                        return new TypeParameterSymbol(parameter.Identifier.Text, type, parameter, i) {
                            Variance = parameter.VarianceToken.IsNone
                                ? VarianceKind.None
                                : parameter.VarianceToken.Kind == SyntaxKind.OutKeyword
                                    ? VarianceKind.Out
                                    : VarianceKind.In
                        };
                    }
                )
            ];
        }

        // A protocol carries 'Self' as a parameter, so that a member reached
        // through a conformer, a type parameter or an existential can be
        // substituted like any other generic. It is constrained to the
        // protocol, so that in the protocol's extensions Self has what the
        // protocol requires — 'Self.zero', 'self * self' — as a T: P does.
        if (typeKind == TypeKind.Protocol) {
            type.SelfTypeParameter = new("Self", type, declaration, -1) { ConstraintsResolver = () => [type] };
        }

        TypeBinder.BindTypeDeclaration(type, declaration);
        RecordDeclaration(declaration, type);
        AddTo(container, type);

        foreach (var member in members) {
            DeclareMember(member, type);
        }

        if (typeKind == TypeKind.Struct) {
            SynthesizeMemberwiseInitializer(type);
        }

        if (type.IsFlags) {
            DeclareFlagsMembers(type);
        }

        // '@derive(…)': the members are made once every type is declared.
        foreach (var attribute in attributes) {
            if (attribute.Name.ToString().Trim() == "derive") {
                _derivations.Add((type, attribute, CurrentScope));
            }
        }

        // A class or actor with no initialiser of its own, whose fields all
        // start with a value, is made with 'init()' — or, a subclass, with
        // its superclass's initialisers (Compilation.Initializers.cs).
        if (typeKind is TypeKind.Class or TypeKind.Actor) {
            DeclareImplicitInitializers(type, declaration);
        }
    }

    /// <summary>
    ///     Gives a struct the initialiser its fields imply, per SYNTAX.md §6.
    /// </summary>
    /// <remarks>
    ///     Only when the struct declares none of its own: writing an <c>init</c>
    ///     is how a type says its fields are not the interface. Parameters take
    ///     the fields' names as labels, in declaration order, and a field with an
    ///     initialiser makes its parameter optional.
    /// </remarks>
    void SynthesizeMemberwiseInitializer(NamedTypeSymbol type) {
        if (type.GetMembers("init").Any()) {
            return;
        }

        var fields = type.Members
            .OfType<FieldSymbol>()
            .Where(static field => field is { IsStatic: false, IsComputed: false })
            .ToList();

        // A struct with no stored fields still gets one, taking nothing:
        // 'Empty()' should mean what it looks like.
        var function = new FunctionSymbol("init", type, null, type.DeclaredAccessibility, FunctionKind.Initializer);

        function.Parameters = [
            ..fields.Select((field, i) => {
                    // An observed property's storage is passed under the
                    // property's name: 'Tracked(count: 1)'.
                    var label = field.BackedProperty is { } property && _observed.ContainsKey(property) ? property.Name : field.Name;
                    var parameter = new ParameterSymbol(field.Name, label, function, null, i) {
                        HasDefaultValue = field.HasInitializer
                    };
                    parameter.TypeResolver = () => field.Type;
                    return parameter;
                }
            )
        ];

        function.ReturnTypeResolver = () => type;
        type.AddMember(function);
    }

    void DeclareFunction(FunctionDeclarationSyntax declaration, Symbol container) {
        // An escaped identifier names it by what is between the backticks:
        // 'fn `yield`()' declares 'yield', which is reserved.
        var name = declaration.Identifier.Value as string ?? declaration.Identifier.Text;
        if (name.Length == 0) {
            return;
        }

        var function = new FunctionSymbol(
            name,
            container,
            declaration,
            AccessibilityOf(declaration.Modifiers, container),
            FunctionKind.Ordinary
        ) {
            IsStatic = declaration.Modifiers.Any(SyntaxKind.StaticKeyword),
            IsAsync = !declaration.AsyncKeyword.IsNone
                || declaration.Modifiers.Any(SyntaxKind.AsyncKeyword),
            IsMutating = declaration.Modifiers.Any(SyntaxKind.MutatingKeyword),
            IsOverride = declaration.Modifiers.Any(SyntaxKind.OverrideKeyword),
            IsOpen = declaration.Modifiers.Any(SyntaxKind.OpenKeyword),
            IsConst = declaration.Modifiers.Any(SyntaxKind.ConstKeyword),
            IsUnsafe = declaration.Modifiers.Any(SyntaxKind.UnsafeKeyword),
            IsFinal = declaration.Modifiers.Any(SyntaxKind.FinalKeyword),
            IsAbstract = declaration.Body is null && declaration.ExpressionBody is null,
            IsPolymorphic = declaration.Attributes.Any(static a => AttributeName(a) == "polymorphic"),
            Builtin = BuiltinFamilyChecked(declaration.Attributes, BuiltinOf(declaration), container)
        };

        // A builtin's body is the builtin: the function is as concrete as one
        // written in Sable, and witnesses a requirement as one would.
        if (function.Builtin is not null) {
            function.IsAbstract = false;
        }

        // C's, or seen by C (Compilation.Interop.cs).
        DeclareForeign(function, declaration, container);
        if (function.ForeignName is not null) {
            function.IsAbstract = false;
        }

        if (declaration.Body is null && declaration.ExpressionBody is null) {
            RequireBody(declaration, declaration.Identifier.Span, declaration.Attributes, declaration.Modifiers, container, $"'{name}'");
        }

        if (declaration.TypeParameters is { } typeParameters) {
            function.TypeParameters = [
                ..typeParameters.Parameters.Select((parameter, i) =>
                    new TypeParameterSymbol(parameter.Identifier.Text, function, parameter, i)
                )
            ];
        }

        function.Parameters = [
            ..declaration.Parameters.Parameters.Select((parameter, i) =>
                DeclareParameter(parameter, function, i)
            )
        ];

        TypeBinder.BindFunctionSignature(function, declaration);
        RecordDeclaration(declaration, function);
        AddTo(container, function);

        // Whether it repeats an earlier overload is known once every
        // parameter type is: after all the signatures are bound.
        var tree = CurrentScope?.Tree;
        TypeBinder.DeferLate(() => CheckRedeclaration(function, declaration, container, tree));
    }

    /// <summary>
    ///     Overloads differ in their parameters' types or labels (SYNTAX.md §5):
    ///     a function that takes what an earlier one of its name in the same
    ///     scope takes, under the same labels, is a second declaration of it —
    ///     whatever its result type, since a result alone cannot choose
    ///     between them.
    /// </summary>
    /// <remarks>
    ///     Generic functions, and functions in a constrained extension, may
    ///     differ in their constraints, which this does not compare; they are
    ///     left alone. So are a protocol's requirement and the default an
    ///     extension of it gives, which are meant to match.
    /// </remarks>
    void CheckRedeclaration(FunctionSymbol function, FunctionDeclarationSyntax declaration, Symbol container, SyntaxTree? tree) {
        if (!function.TypeParameters.IsEmpty || IsConstrained(container)) {
            return;
        }

        var members = container switch {
            ModuleSymbol module => module.GetMembers(function.Name),
            NamedTypeSymbol type => type.GetMembers(function.Name),
            ExtensionSymbol extension => extension.ExtendedType.GetMembers(function.Name),
            _ => [],
        };

        foreach (var member in members) {
            if (ReferenceEquals(member, function)) {
                return;
            }

            if (member is not FunctionSymbol { FunctionKind: FunctionKind.Ordinary, TypeParameters.IsEmpty: true } earlier
                || earlier.DeclaringSyntax.FirstOrDefault() is not FunctionDeclarationSyntax
                || IsConstrained(earlier.ContainingSymbol)
                || !SameSignature(earlier, function)
                || earlier.ContainingSymbol is not { } earlierContainer
                || (ProtocolOf(earlierContainer) is not null && !ReferenceEquals(earlierContainer, container))) {
                continue;
            }

            var labels = string.Concat(function.Parameters.Select(static p => (p.Label ?? p.Name) + ":"));
            var where = earlier.Location is { } location ? $" (already declared at offset {location.Start})" : string.Empty;
            var diagnostic = Diagnostic.Error(
                ErrorCode.ERR_DuplicateDeclaration,
                declaration.Identifier.Span,
                $"'{function.Name}({labels})' is already declared in this scope with the same parameters{where}; "
                + "overloads differ in their parameter types or labels, not in their result type alone"
            ).WithRelated(earlier.DeclaredHere("declared first here"));
            ReportDeclaration(diagnostic, tree);
            return;
        }

        static NamedTypeSymbol? ProtocolOf(Symbol container) =>
            (container is ExtensionSymbol extension ? extension.ExtendedType : container as NamedTypeSymbol)
                is { TypeKind: TypeKind.Protocol } protocol
                ? protocol
                : null;

        static bool IsConstrained(Symbol? container) =>
            container is ExtensionSymbol extension
            && (extension.DeclaringSyntax.FirstOrDefault() is ExtensionDeclarationSyntax { WhereClause: not null }
                || extension.ExtendedType.ExtendedArray is not null);
    }

    /// <summary>
    ///     True when two functions take the same thing the same way: as both
    ///     static or both not, the same parameters under the same labels,
    ///     passed alike, and implementing the same protocol, if either names one.
    /// </summary>
    static bool SameSignature(FunctionSymbol a, FunctionSymbol b) {
        if (a.IsStatic != b.IsStatic || a.Parameters.Length != b.Parameters.Length) {
            return false;
        }

        var explicitA = (a.ExplicitProtocol as NamedTypeSymbol)?.OriginalDefinition;
        var explicitB = (b.ExplicitProtocol as NamedTypeSymbol)?.OriginalDefinition;
        if (!ReferenceEquals(explicitA, explicitB)) {
            return false;
        }

        for (var i = 0; i < a.Parameters.Length; i++) {
            var (x, y) = (a.Parameters[i], b.Parameters[i]);
            if ((x.Label ?? x.Name) != (y.Label ?? y.Name)
                || x.Modifier != y.Modifier
                || x.IsVariadic != y.IsVariadic
                || x.Type.IsError
                || y.Type.IsError
                || !x.Type.Equals(y.Type)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Builds the symbol for a function declared inside a body.
    /// </summary>
    /// <remarks>
    ///     A local function is never a member of anything, so it is not added to a
    ///     container and its signature is bound against the scope passed in rather
    ///     than the declaration-table cursor, which has long since moved on.
    /// </remarks>
    /// <summary>
    ///     Declares a type or alias written inside a function body, owned by that
    ///     function, and returns it.
    /// </summary>
    /// <remarks>
    ///     The declaration goes through the same path as a file-scope one, so a
    ///     local type has everything a top-level one has — members, conformances,
    ///     a memberwise initialiser. What it may not be is an extension: an
    ///     extension adds to a type everywhere, which a body has no business
    ///     doing.
    /// </remarks>
    internal Symbol? DeclareLocalType(
        DeclarationSyntax declaration,
        FunctionSymbol container,
        FileScope scope
    ) {
        if (declaration is ExtensionDeclarationSyntax extension) {
            ReportDeclaration(
                ErrorCode.ERR_NotAllowedInBody,
                extension.Keyword.Span,
                "an extension adds to a type everywhere, so it is declared at "
                + "file scope, not inside a body"
            );
            return null;
        }

        var saved = CurrentScope;
        CurrentScope = scope;
        try {
            DeclareMember(declaration, container);
            DeclareInlineLayouts();
            DeclareDerivations();
        } finally {
            CurrentScope = saved;
        }

        return GetDeclaredSymbol(declaration);
    }

    internal FunctionSymbol DeclareLocalFunction(
        FunctionDeclarationSyntax declaration,
        Symbol container,
        FileScope scope
    ) {
        var function = new FunctionSymbol(
            declaration.Identifier.Text,
            container,
            declaration,
            Accessibility.Private,
            FunctionKind.Ordinary
        ) {
            IsAsync = !declaration.AsyncKeyword.IsNone
                || declaration.Modifiers.Any(SyntaxKind.AsyncKeyword),
            IsConst = declaration.Modifiers.Any(SyntaxKind.ConstKeyword),
            IsUnsafe = declaration.Modifiers.Any(SyntaxKind.UnsafeKeyword)
        };

        if (declaration.TypeParameters is { } typeParameters) {
            function.TypeParameters = [
                ..typeParameters.Parameters.Select((parameter, i) =>
                    new TypeParameterSymbol(parameter.Identifier.Text, function, parameter, i)
                )
            ];
        }

        function.Parameters = [
            ..declaration.Parameters.Parameters.Select((syntax, i) => {
                    var parameter = new ParameterSymbol(
                        syntax.Identifier.Text,
                        syntax.Label.IsNone ? null : syntax.Label.Text,
                        function,
                        syntax,
                        i
                    ) {
                        HasDefaultValue = syntax.DefaultValue is not null,
                        IsVariadic = !syntax.EllipsisToken.IsNone,
                        Modifier = ModifierOf(syntax)
                    };
                    parameter.TypeResolver = () => TypeBinder.BindType(syntax.Type, function, scope);
                    return parameter;
                }
            )
        ];

        function.ReturnTypeResolver = () => declaration.ReturnClause is { } clause
            ? TypeBinder.BindReturnType(clause.Type, function, scope)
            : Core.Void;

        TypeBinder.BindThrows(function, declaration.ThrowsSpec, scope);

        // Its constraints now, before its signature or body is looked at:
        // 'T: Numeric' is what lets the body multiply two T's.
        TypeBinder.BindTypeParameterConstraints(function.TypeParameters, function, scope);
        TypeBinder.BindWhereClause(declaration.WhereClause, function, scope);

        RecordDeclaration(declaration, function);
        return function;
    }

    ParameterSymbol DeclareParameter(
        ParameterSyntax syntax,
        Symbol container,
        int ordinal
    ) {
        var label = syntax.Label.IsNone ? null : syntax.Label.Text;

        var parameter = new ParameterSymbol(syntax.Identifier.Text, label, container, syntax, ordinal) {
            HasDefaultValue = syntax.DefaultValue is not null,
            IsVariadic = !syntax.EllipsisToken.IsNone,
            Modifier = ModifierOf(syntax),
            IsEscaping = syntax.Attributes.Concat((syntax.Type as AttributedTypeSyntax)?.Attributes ?? [])
                .Any(static a => a.Name.ToString().Trim() == "escaping"),
            IsSendable = syntax.Attributes.Concat((syntax.Type as AttributedTypeSyntax)?.Attributes ?? [])
                .Any(static a => a.Name.ToString().Trim() == "Sendable"),
            IsAutoclosure = syntax.Attributes.Concat((syntax.Type as AttributedTypeSyntax)?.Attributes ?? [])
                .Any(static a => a.Name.ToString().Trim() == "autoclosure"),
        };

        TypeBinder.BindParameterType(parameter, syntax);
        return parameter;
    }

    static ParameterModifier ModifierOf(ParameterSyntax syntax) {
        foreach (var modifier in syntax.Modifiers) {
            var kind = Translate(modifier.Kind);
            if (kind != ParameterModifier.None) {
                return kind;
            }
        }

        return Translate(syntax.TypeModifierToken.Kind);

        static ParameterModifier Translate(SyntaxKind kind) =>
            kind switch {
                SyntaxKind.InoutKeyword => ParameterModifier.InOut,
                SyntaxKind.OutKeyword => ParameterModifier.Out,
                SyntaxKind.ConsumingKeyword => ParameterModifier.Consuming,
                SyntaxKind.InKeyword => ParameterModifier.In,
                _ => ParameterModifier.None,
            };
    }

    void DeclareField(FieldDeclarationSyntax declaration, Symbol container) {
        if (declaration.Declarator.Pattern is not BindingPatternSyntax binding) {
            return;
        }

        // 'var `lazy`' declares 'lazy', as 'fn `yield`()' does 'yield'.
        var name = binding.Identifier.Value as string ?? binding.Identifier.Text;
        if (name.Length == 0) {
            return;
        }

        if (FindExisting(container, name) is { } existing && !IsPropertyDefault(declaration, container, existing)) {
            ReportDuplicate(binding.Identifier, name, existing);
            return;
        }

        var field = new FieldSymbol(
            name,
            container,
            declaration,
            AccessibilityOf(declaration.Modifiers, container),
            declaration.LetOrVarKeyword.Kind == SyntaxKind.VarKeyword
        ) {
            IsStatic = declaration.Modifiers.Any(SyntaxKind.StaticKeyword),
            IsOverride = declaration.Modifiers.Any(SyntaxKind.OverrideKeyword),
            IsFinal = declaration.Modifiers.Any(SyntaxKind.FinalKeyword),
            IsImmortal = declaration.Modifiers.Any(SyntaxKind.StaticKeyword) && declaration.LetOrVarKeyword.Kind == SyntaxKind.LetKeyword
                && AttributesOf(declaration).Any(static a => AttributeName(a) == "immortal"),
            SetterAccessibility = SetterAccessibilityOf(declaration.Modifiers),
            IsLazy = declaration.Modifiers.Any(SyntaxKind.LazyKeyword),
            ReferenceKind = declaration.Modifiers.Any(SyntaxKind.WeakKeyword) ? ReferenceKind.Weak
                : declaration.Modifiers.Any(SyntaxKind.UnownedKeyword) ? ReferenceKind.Unowned
                : ReferenceKind.Strong,
            IsComputed = declaration.Accessors is not null,
            // 'var value: T { async get }': reading it is awaited.
            IsAsyncGetter = declaration.Accessors is { } getters
                && getters.Accessors.Any(static a => a.Keyword.Kind == SyntaxKind.GetKeyword && a.Modifiers.Any(SyntaxKind.AsyncKeyword)),
            HasSetter = declaration.Accessors is not { } list
                || list.Accessors.Any(static a => a.Keyword.Kind == SyntaxKind.SetKeyword),
            IsAbstract = declaration.Accessors is { } accessors
                && accessors.Accessors.All(static a => a.Body is null && a.ExpressionBody is null),
            HasInitializer = declaration.Declarator.Initializer is not null,
            Builtin = BuiltinFamilyChecked(declaration.Attributes, BuiltinOf(declaration), container)
        };

        if (field.Builtin is not null) {
            field.IsAbstract = false;
        }

        TypeBinder.BindFieldType(field, declaration);
        RecordDeclaration(declaration, field);

        // Seen by C (Compilation.Interop.cs).
        DeclareExportedVariable(field, declaration, container);

        // 'var name: String = "" { set { field = newValue.trimmed() } }':
        // accessors that say 'field' have storage, laid out where the
        // property is, which the property's initial value fills.
        if (field.IsLazy) {
            DeclareLazy(declaration, field, container, binding.Identifier);
        }

        var keptInField = field.IsComputed && declaration.Accessors is { } accessorList && accessorList.Accessors.Any(SaysField);
        if (keptInField && container is ExtensionSymbol) {
            ReportDeclaration(
                ErrorCode.ERR_ExtensionStoredProperty,
                binding.Identifier.Span,
                $"'{name}' keeps its value in 'field', which is storage, and an extension cannot add stored properties, only computed ones"
            );
        } else if (keptInField) {
            var backing = new FieldSymbol(BackingFieldName(name), container, declaration, Accessibility.Private, true) {
                IsStatic = field.IsStatic,
                HasInitializer = declaration.Declarator.Initializer is not null,
                BackedProperty = field,
                TypeResolver = () => field.Type,
            };
            field.BackingField = backing;
            field.IsAbstract = false;
            AddTo(container, backing);

            if (!backing.HasInitializer) {
                ReportDeclaration(
                    ErrorCode.ERR_BackedPropertyNeedsInitializer,
                    binding.Identifier.Span,
                    $"'{name}' keeps its value in 'field', which needs one to start with: give it '= ...'"
                );
            }
        }

        // 'var tracked: Int = 0 { willSet { … } didSet { … } }': stored,
        // with stores that run its observers.
        if (declaration.Accessors is { } observed && observed.Accessors.Any(static a => IsObserver(a))) {
            DeclareObserved(declaration, field, container, binding.Identifier);
        }

        // Each accessor has a body, unless the property is a requirement or
        // something supplies one: a builtin, the compiler, or 'field'.
        foreach (var accessor in declaration.Accessors?.Accessors ?? []) {
            if (accessor is { Body: null, ExpressionBody: null }
                && !(field.BackingField is not null && accessor.Keyword.Kind is SyntaxKind.GetKeyword or SyntaxKind.SetKeyword)) {
                RequireBody(declaration, accessor.Keyword.Span, declaration.Attributes, declaration.Modifiers, container, $"the '{accessor.Keyword.Text}' of '{name}'");
            }
        }

        AddTo(container, field);
    }

    /// <summary>
    ///     Reports a member written without a body where nothing gives it
    ///     one: a protocol's requirement has none, and neither does an
    ///     'abstract' method, or one whose body the compiler or a foreign
    ///     library supplies.
    /// </summary>
    void RequireBody(SyntaxNode declaration, TextSpan at, SyntaxList<AttributeSyntax> attributes, SyntaxTokenList modifiers, Symbol container, string what) {
        // A declaration the parser could not make sense of may have lost
        // the body it was written with; that error has been given.
        if (declaration.DescendantTokens().Any(static t => t.IsMissing && t.Kind != SyntaxKind.GetKeyword)) {
            return;
        }

        if (container is NamedTypeSymbol { TypeKind: TypeKind.Protocol }
            || modifiers.Any(SyntaxKind.AbstractKeyword)
            || attributes.Any(static a => a.Name.ToString().Trim() is "builtin" or "intrinsic" or "extern")) {
            return;
        }

        ReportDeclaration(
            ErrorCode.ERR_MissingBody,
            at,
            $"{what} needs a body: only a protocol's requirements, 'abstract' methods and '@builtin', '@intrinsic' or '@extern' declarations go without"
        );
    }

    /// <summary>
    ///     <c>lazy var report: Report = build()</c>: storage of <c>Report?</c>,
    ///     none until the property is first read, and accessors the compiler
    ///     writes — a getter that fills it from the initialiser the first time,
    ///     with <c>self</c> in scope, and a setter. The bodies are bound with
    ///     the rest of the file (<see cref="SemanticModel" />).
    /// </summary>
    void DeclareLazy(FieldDeclarationSyntax declaration, FieldSymbol field, Symbol container, SyntaxToken name) {
        string? wrong = null;
        if (!field.IsMutable) {
            wrong = "is computed the first time it is read, which writes it, so it is a 'var'";
        } else if (declaration.Declarator.Initializer is null) {
            wrong = "needs the value to compute the first time it is read: give it '= ...'";
        } else if (declaration.Accessors is not null) {
            wrong = "has the accessors the compiler writes for it, so it cannot declare its own";
        } else if (field.IsStatic || container is not NamedTypeSymbol { TypeKind: TypeKind.Struct or TypeKind.Class or TypeKind.Actor }) {
            wrong = "is for a property of a struct, class or actor value, not a static or global one";
        }

        if (wrong is not null) {
            ReportDeclaration(ErrorCode.ERR_LazyProperty, name.Span, $"lazy '{name.Text}' {wrong}");
            field.IsLazy = false;
            return;
        }

        var type = (NamedTypeSymbol)container;
        var backing = new FieldSymbol(BackingFieldName(field.Name), container, declaration, Accessibility.Private, true) {
            HasInitializer = true,
            BackedProperty = field,
            TypeResolver = () => new OptionalTypeSymbol(field.Type),
        };

        // Reading it the first time writes it: a value's getter mutates.
        var getter = new FunctionSymbol("get", field, declaration, field.DeclaredAccessibility, FunctionKind.Accessor) {
            IsMutating = !type.IsReferenceType,
        };
        getter.ReturnTypeResolver = () => field.Type;

        var setter = new FunctionSymbol("set", field, declaration, field.SetterAccessibility ?? field.DeclaredAccessibility, FunctionKind.Accessor) {
            IsMutating = true,
        };
        var newValue = new ParameterSymbol("newValue", null, setter, declaration, 0) { TypeResolver = () => field.Type };
        setter.Parameters = [newValue];
        setter.ReturnTypeResolver = () => Core.Void;

        field.IsComputed = true;
        field.HasSetter = true;
        field.HasInitializer = false;
        field.BackingField = backing;
        _lazy[field] = new(getter, setter, CurrentScope!.Tree);
        AddTo(container, backing);
    }

    /// <summary>
    ///     <c>var tracked: Int = 0 { willSet { … } didSet { … } }</c>: a stored
    ///     property, whose value is kept in storage of its own, as one kept in
    ///     <c>field</c> is, and whose getter and setter the compiler writes —
    ///     the setter calls <c>willSet</c> with the new value, stores it, then
    ///     calls <c>didSet</c> with the old one. The bodies are bound with the
    ///     rest of the file (<see cref="SemanticModel" />). Within the
    ///     observers and the type's own initialisers, the property names its
    ///     storage, so neither runs the observers (<see cref="Binder" />).
    /// </summary>
    void DeclareObserved(FieldDeclarationSyntax declaration, FieldSymbol field, Symbol container, SyntaxToken name) {
        string? wrong = null;
        if (declaration.Accessors!.Accessors.Any(static a => !IsObserver(a))) {
            wrong = "is stored, with observers, so it cannot also have a 'get' or 'set'";
        } else if (!field.IsMutable) {
            wrong = "has observers, which run when it is written, so it is a 'var'";
        } else if (field.IsLazy) {
            wrong = "is lazy, and a lazy property cannot have observers";
        } else if (container is not NamedTypeSymbol { TypeKind: TypeKind.Struct or TypeKind.Class or TypeKind.Actor }) {
            wrong = container is ExtensionSymbol
                ? "has observers, which need storage, and an extension cannot add stored properties"
                : "has observers, which are for a property of a struct, class or actor";
        }

        if (wrong is not null) {
            ReportDeclaration(ErrorCode.ERR_PropertyObservers, name.Span, $"'{name.Text}' {wrong}");
            return;
        }

        var backing = new FieldSymbol(BackingFieldName(field.Name), container, declaration, Accessibility.Private, true) {
            IsStatic = field.IsStatic,
            HasInitializer = declaration.Declarator.Initializer is not null,
            BackedProperty = field,
            TypeResolver = () => field.Type,
        };

        var getter = new FunctionSymbol("get", field, declaration, field.DeclaredAccessibility, FunctionKind.Accessor) {
            IsStatic = field.IsStatic,
        };
        getter.ReturnTypeResolver = () => field.Type;

        var setter = new FunctionSymbol("set", field, declaration, field.SetterAccessibility ?? field.DeclaredAccessibility, FunctionKind.Accessor) {
            IsMutating = true,
            IsStatic = field.IsStatic,
        };
        var newValue = new ParameterSymbol("newValue", null, setter, declaration, 0) { TypeResolver = () => field.Type };
        setter.Parameters = [newValue];
        setter.ReturnTypeResolver = () => Core.Void;

        field.HasSetter = true;
        field.IsAbstract = false;
        field.BackingField = backing;
        _observed[field] = new(getter, setter, CurrentScope!.Tree);
        AddTo(container, backing);
    }

    /// <summary><c>willSet</c> or <c>didSet</c>.</summary>
    static bool IsObserver(AccessorSyntax accessor) =>
        accessor.Keyword.Kind is SyntaxKind.WillSetKeyword or SyntaxKind.DidSetKeyword;

    /// <summary>
    ///     A property's accessors, which the compiler writes — a lazy
    ///     property's, or one with observers — and the file it is declared in.
    /// </summary>
    public sealed record LazyAccessors(FunctionSymbol Getter, FunctionSymbol Setter, SyntaxTree Tree);

    readonly Dictionary<FieldSymbol, LazyAccessors> _lazy = [];
    readonly Dictionary<FieldSymbol, LazyAccessors> _observed = [];

    /// <summary>Every lazy property, with its accessors.</summary>
    public IReadOnlyDictionary<FieldSymbol, LazyAccessors> LazyProperties => _lazy;

    /// <summary>Every property with <c>willSet</c> or <c>didSet</c>, with the getter and setter written for it.</summary>
    public IReadOnlyDictionary<FieldSymbol, LazyAccessors> ObservedProperties => _observed;

    /// <summary>The body the compiler wrote for a lazy or observed property's accessor, or null for any other function.</summary>
    public BoundBlock? GetLazyBody(FunctionSymbol function) =>
        function.ContainingSymbol is FieldSymbol field && (_lazy.TryGetValue(field, out var accessors) || _observed.TryGetValue(field, out accessors))
            ? GetSemanticModel(accessors.Tree).GetLazyBody(function)
            : null;

    /// <summary>What a property's backing field is called: a name no source can write.</summary>
    public static string BackingFieldName(string property) => property + "$field";

    /// <summary>Whether an accessor's body says <c>field</c>, other than as a member's name: <c>other.field</c> is not it.</summary>
    static bool SaysField(AccessorSyntax accessor) =>
        ((SyntaxNode?)accessor.Body ?? accessor.ExpressionBody) is { } body
        && body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(static n =>
            n.Identifier.Text == "field" && !(n.Parent is MemberAccessExpressionSyntax access && access.Name == n));

    void DeclareConstant(ConstDeclarationSyntax declaration, Symbol container) {
        var name = declaration.Identifier.Text;
        if (name.Length == 0) {
            return;
        }

        if (FindExisting(container, name) is { } existing) {
            ReportDuplicate(declaration.Identifier, name, existing);
            return;
        }

        var field = new FieldSymbol(
            name,
            container,
            declaration,
            AccessibilityOf(declaration.Modifiers, container),
            false
        ) { IsStatic = true, HasInitializer = true };

        TypeBinder.BindConstantType(field, declaration);
        RecordDeclaration(declaration, field);
        AddTo(container, field);
    }

    void DeclareAlias(AliasDeclarationSyntax declaration, Symbol container) {
        var name = declaration.Identifier.Text;
        if (name.Length == 0) {
            return;
        }

        if (FindExisting(container, name) is { } existing) {
            ReportDuplicate(declaration.Identifier, name, existing);
            return;
        }

        var alias = new AliasSymbol(name, container, declaration, AccessibilityOf(declaration.Modifiers, container));

        if (declaration.TypeParameters is { } typeParameters) {
            alias.TypeParameters = [
                ..typeParameters.Parameters.Select((parameter, i) =>
                    new TypeParameterSymbol(parameter.Identifier.Text, alias, parameter, i)
                )
            ];
        }

        TypeBinder.BindAliasTarget(alias, declaration.Type);
        AddTo(container, alias);
    }

    void DeclareUnionCase(UnionCaseDeclarationSyntax declaration, NamedTypeSymbol union) {
        // A case may be named by a keyword in backticks, '`none`', as a
        // member name after '.' may be any keyword (SYNTAX.md §16).
        var name = declaration.Identifier.Value as string ?? declaration.Identifier.Text;
        if (name.Length == 0) {
            return;
        }

        if (FindExisting(union, name) is { } existing) {
            ReportDuplicate(declaration.Identifier, name, existing);
            return;
        }

        var unionCase = new UnionCaseSymbol(name, union, declaration);

        if (declaration.Payload is { } payload) {
            unionCase.Payload = [
                ..payload.Fields.Select((field, i) => {
                        var parameter = new ParameterSymbol(
                            field.NameToken.IsNone ? $"_{i}" : field.NameToken.Text,
                            field.NameToken.IsNone ? null : field.NameToken.Text,
                            unionCase,
                            field,
                            i
                        );

                        TypeBinder.BindUnionCaseFieldType(parameter, field);
                        return parameter;
                    }
                )
            ];
        }

        union.AddMember(unionCase);
    }

    void DeclareEnumCase(EnumCaseDeclarationSyntax declaration, NamedTypeSymbol enumType) {
        var name = declaration.Identifier.Value as string ?? declaration.Identifier.Text;
        if (name.Length == 0) {
            return;
        }

        if (FindExisting(enumType, name) is { } existing) {
            ReportDuplicate(declaration.Identifier, name, existing);
            return;
        }

        var value = EnumCaseValue(declaration, enumType);
        CheckDistinctValue(declaration, enumType, value);
        var enumCase = new EnumCaseSymbol(name, enumType, declaration) { HasExplicitValue = declaration.Value is not null, RawValue = value };
        enumType.AddMember(enumCase);
        RecordDeclaration(declaration, enumCase);
    }

    /// <param name="container">The type, or an extension of it, or of every growable array of a kind.</param>
    /// <param name="type">The type, or the array extension's own (whose ExtendedArray is the array).</param>
    void DeclareInitializer(InitDeclarationSyntax declaration, Symbol container, NamedTypeSymbol type) {
        var function = new FunctionSymbol(
            "init",
            container,
            declaration,
            AccessibilityOf(declaration.Modifiers, container),
            FunctionKind.Initializer
        ) {
            IsAsync = declaration.Modifiers.Any(SyntaxKind.AsyncKeyword)
        };

        // 'init<S: Sequence>(contentsOf s: S) where S.Element == T': an
        // initialiser's own parameters, inferred at the call from its
        // arguments as a generic function's are.
        if (declaration.TypeParameters is { } typeParameters) {
            function.TypeParameters = [
                ..typeParameters.Parameters.Select((parameter, i) =>
                    new TypeParameterSymbol(parameter.Identifier.Text, function, parameter, i)
                )
            ];
        }

        function.Parameters = [
            ..declaration.Parameters.Parameters.Select((parameter, i) =>
                DeclareParameter(parameter, function, i)
            )
        ];

        TypeBinder.BindInitializerConstraints(function, declaration);

        // A protocol's 'init(seed: Int)' is a requirement, met by the
        // conformer's own initialiser, and it makes a Self: through a T it
        // makes a T.
        // An array extension's makes the array, which its self starts as: '[]'.
        if (type.TypeKind == TypeKind.Protocol) {
            function.IsAbstract = declaration.Body is null;
            function.ReturnTypeResolver = () => type.SelfTypeParameter ?? (TypeSymbol)type;
        } else {
            function.ReturnTypeResolver = () => (TypeSymbol?)type.ExtendedArray ?? type;
        }

        // Outside a protocol an initialiser is code: one written without a
        // body would be called (by 'Node(value: 1)', or by a subclass's
        // implicit initialiser through 'super') with nothing to run.
        if (declaration.Body is null) {
            RequireBody(declaration, declaration.InitKeyword.Span, declaration.Attributes, declaration.Modifiers, type, "'init'");
        }

        RecordDeclaration(declaration, function);
        AddTo(container, function);
    }

    void DeclareOperator(OperatorDeclarationSyntax declaration, Symbol container) {
        // The symbol's name is the operator's spelling, so '+' is looked up
        // by the same text the source wrote.
        var name = declaration.SecondOperatorToken.IsNone
            ? declaration.OperatorToken.Text
            : declaration.OperatorToken.Text + declaration.SecondOperatorToken.Text;

        var function = new FunctionSymbol(
            name,
            container,
            declaration,
            AccessibilityOf(declaration.Modifiers, container),
            FunctionKind.Operator
        ) {
            IsStatic = true,
            Fixity = declaration.FixityToken.Kind switch {
                SyntaxKind.PrefixKeyword => OperatorFixity.Prefix,
                SyntaxKind.PostfixKeyword => OperatorFixity.Postfix,
                _ => OperatorFixity.Infix,
            },
            Builtin = BuiltinFamilyChecked(declaration.Attributes, BuiltinOf(declaration), container)
        };

        function.Parameters = [
            ..declaration.Parameters.Parameters.Select((parameter, i) =>
                DeclareParameter(parameter, function, i)
            )
        ];

        // A protocol's 'operator + (lhs: Self, rhs: Self) -> Self' is a
        // requirement, which a conformer has to meet, as a method's is. A
        // builtin family in the protocol's extension is the built-in
        // types' default for it, and no other type's (ConformanceChecker).
        function.IsAbstract = container is NamedTypeSymbol { TypeKind: TypeKind.Protocol }
                              && declaration.Body is null
                              && declaration.ExpressionBody is null
                              && function.Builtin is null;

        var scope = CurrentScope;
        function.ReturnTypeResolver = () =>
            TypeBinder.BindType(declaration.ReturnClause.Type, function, scope);

        RecordDeclaration(declaration, function);
        AddTo(container, function);
    }

    void DeclareSubscript(SubscriptDeclarationSyntax declaration, Symbol container) {
        var function = new FunctionSymbol(
            "subscript",
            container,
            declaration,
            AccessibilityOf(declaration.Modifiers, container),
            FunctionKind.Subscript
        );

        function.Parameters = [
            ..declaration.Parameters.Parameters.Select((parameter, i) =>
                DeclareParameter(parameter, function, i)
            )
        ];

        var scope = CurrentScope;
        function.ReturnTypeResolver = () =>
            TypeBinder.BindType(declaration.ReturnClause.Type, function, scope);

        foreach (var accessor in declaration.Accessors?.Accessors ?? []) {
            if (accessor is { Body: null, ExpressionBody: null }) {
                RequireBody(declaration, accessor.Keyword.Span, declaration.Attributes, declaration.Modifiers, container, $"the '{accessor.Keyword.Text}' of this subscript");
            }
        }

        // A protocol's 'subscript(i: Index) -> Element { get }' is a
        // requirement, which a conformer has to meet, as a method's is.
        function.IsAbstract = container is NamedTypeSymbol { TypeKind: TypeKind.Protocol }
                              && declaration.Accessors is { } accessors
                              && accessors.Accessors.All(static a => a.Body is null && a.ExpressionBody is null);

        RecordDeclaration(declaration, function);
        AddTo(container, function);
    }

    void DeclareDeinitializer(DeinitDeclarationSyntax declaration, NamedTypeSymbol type) {
        var function = new FunctionSymbol(
            "deinit",
            type,
            declaration,
            Accessibility.Internal,
            FunctionKind.Deinitializer
        );

        function.ReturnTypeResolver = () => Core.Void;
        RecordDeclaration(declaration, function);
        type.AddMember(function);
    }

    /// <summary>
    ///     Declares an <c>associatedtype</c>: a type the conformer supplies.
    /// </summary>
    void DeclareAssociatedType(
        AssociatedTypeDeclarationSyntax declaration,
        NamedTypeSymbol protocol
    ) {
        var name = declaration.Identifier.Text;
        if (name.Length == 0) {
            return;
        }

        var parameter = new TypeParameterSymbol(name, protocol, declaration, protocol.AssociatedTypes.Length);

        var scope = CurrentScope;

        // The constraints bind when first read, which may be before this
        // file's deferred work runs; the deferral only makes sure they do.
        if (declaration.BaseTypes is { } constraints) {
            parameter.ConstraintsResolver = () => [
                ..constraints.Types.SelectMany(syntax => CompositionTypeSymbol.PartsOf(TypeBinder.BindType(syntax, protocol, scope)))
            ];
        }

        if (declaration.DefaultType is { } fallback) {
            parameter.DefaultTypeResolver = () => TypeBinder.BindType(fallback, protocol, scope);
        }

        TypeBinder.Defer(() => {
                _ = parameter.Constraints;
                _ = parameter.DefaultType;
            }
        );

        if (declaration.WhereClause is { } clause) {
            TypeBinder.BindAssociatedTypeWhereClause(protocol, clause, scope);
        }

        protocol.AssociatedTypes = protocol.AssociatedTypes.Add(parameter);
        RecordDeclaration(declaration, parameter);
    }

    void DeclareExtension(ExtensionDeclarationSyntax declaration, Symbol container) {
        // The extended type may be declared in a file this one has not reached
        // yet, so extensions are attached once every file has been read.
        Extensions.Add((declaration, container, CurrentScope));
    }

    internal List<(
        ExtensionDeclarationSyntax Syntax,
        Symbol Container,
        FileScope? Scope)> Extensions { get; } = [];

    /// <summary>
    ///     Attaches every extension's members to the type it extends.
    /// </summary>
    /// <remarks>
    ///     This runs after all files are declared, because an extension may
    ///     precede the declaration it extends, in this file or another.
    /// </remarks>
    void AttachExtensions() {
        // By index, not 'foreach': an extension written inside another is
        // declared, and so added to the list, while its outer one is attached.
        for (var i = 0; i < Extensions.Count; i++) {
            var (declaration, container, scope) = Extensions[i];
            CurrentScope = scope;

            if (IsArrayExtension(declaration)) {
                AttachArrayExtension(declaration, scope);
                continue;
            }

            // Parameters after the keyword are for an array, which has no name to write them after.
            if (declaration.TypeParameters is { } typeParameters
                && Unparenthesised(declaration.Type) is SimpleNameTypeSyntax or QualifiedNameSyntax) {
                ReportDeclaration(
                    ErrorCode.ERR_MalformedArrayExtension,
                    typeParameters.Span,
                    $"parameters after 'extension' are an array extension's; a named type's are written after its name: 'extension {WrittenName(Unparenthesised(declaration.Type))}{typeParameters.ToString().Trim()}'"
                );
            }

            var extended = ResolveExtendedType(declaration, container, scope);
            if (extended is null) {
                continue;
            }

            var extension = new ExtensionSymbol(extended, declaration) { DeclaringModule = scope?.Module };
            extension.TypeParameterAliases = AliasTypeParameters(declaration, extension, extended);
            RecordDeclaration(declaration, extension);
            TypeBinder.BindExtensionWhereClause(extension, declaration, scope);

            // ': P' on an extension is retroactive conformance. With a 'where'
            // clause it is conditional: 'extension Box: P where T: P' makes a
            // Box<T> a P only where T is one, which the extension remembers.
            if (declaration.BaseTypes is { } baseTypes) {
                var written = scope;
                TypeBinder.Defer(() => {
                        foreach (var baseType in baseTypes.Types) {
                            var bound = TypeBinder.BindType(baseType, extension, written);
                            CheckCoherence(extended, bound, written, baseType.Span);
                            extended.BaseTypes = extended.BaseTypes.Add(bound);
                            extension.Conformances = extension.Conformances.Add(bound);
                            if (extended.ExtendedOptional is not null) {
                                RegisterOptionalConformance(extension, bound);
                            }
                        }
                    }
                );

                extended.Extensions = extended.Extensions.Add(extension);
            }

            // A value type's extension may add an initialiser that gives
            // self its value — by 'self.init(…)', 'self = …', or, a
            // struct's, field by field — as one in the type's body does
            // (SYNTAX.md §11). A class's would be a designated one.
            var initializers = extended.TypeKind is TypeKind.Struct or TypeKind.Enum or TypeKind.Union or TypeKind.Builtin;
            foreach (var member in declaration.Members) {
                if (!(initializers && member is InitDeclarationSyntax) && RejectInExtension(member)) {
                    continue;
                }

                DeclareMember(member, extension);
            }
        }

        CurrentScope = null;
    }

    /// <summary>
    ///     Gives a protocol, and those it refines, an extension of Optional
    ///     that conforms optionals to it, so that <c>T?</c> is found to conform
    ///     (<see cref="Conversions.Satisfies" />).
    /// </summary>
    static void RegisterOptionalConformance(ExtensionSymbol extension, TypeSymbol conformance) {
        var registered = new HashSet<NamedTypeSymbol>();
        var pending = new Stack<NamedTypeSymbol>(CompositionTypeSymbol.PartsOf(conformance).OfType<NamedTypeSymbol>());
        while (pending.Count > 0) {
            var protocol = pending.Pop().OriginalDefinition;
            if (protocol.TypeKind != TypeKind.Protocol || !registered.Add(protocol)) {
                continue;
            }

            if (!protocol.OptionalConformances.Contains(extension)) {
                protocol.OptionalConformances = protocol.OptionalConformances.Add(extension);
            }

            foreach (var refined in protocol.BaseTypes.OfType<NamedTypeSymbol>()) {
                pending.Push(refined);
            }
        }
    }

    /// <summary>
    ///     A conformance belongs to the type's module or the protocol's, never a
    ///     third.
    /// </summary>
    /// <remarks>
    ///     docs/03 §3.2 takes the rule from Rust: it guarantees at most one
    ///     conformance per (type, protocol) pair globally, which is what makes
    ///     `any P` sound and conformance lookup a static map rather than a
    ///     search. The escape hatch for the third-party case is a wrapper struct.
    /// </remarks>
    void CheckCoherence(
        NamedTypeSymbol extended,
        TypeSymbol conformance,
        FileScope? scope,
        TextSpan span
    ) {
        if (conformance is not NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol) {
            return;
        }

        if (scope is null) {
            return;
        }

        var here = scope.Module;
        if (ReferenceEquals(here, extended.ContainingModule)) {
            return;
        }

        if (ReferenceEquals(here, protocol.ContainingModule)) {
            return;
        }

        ReportDeclaration(
            ErrorCode.ERR_IncoherentConformance,
            span,
            $"'{extended.Name}: {protocol.Name}' may only be declared in "
            + $"'{extended.ContainingModule?.Name}' or '{protocol.ContainingModule?.Name}', "
            + "not here; wrap the type in a struct of your own instead"
        );
    }

    /// <summary>Resolves the name an extension extends, without its arguments.</summary>
    NamedTypeSymbol? ResolveExtendedType(
        ExtensionDeclarationSyntax declaration,
        Symbol container,
        FileScope? scope
    ) {
        var syntax = declaration.Type;
        while (syntax is ParenthesizedTypeSyntax parenthesized) {
            syntax = parenthesized.Type;
        }

        var found = syntax switch {
            SimpleNameTypeSyntax simple =>
                TypeBinder.Lookup(simple.Identifier.Text, container, scope, simple.Span),
            QualifiedNameSyntax qualified =>
                (Symbol)TypeBinder.BindType(qualified, container, scope),
            _ => null,
        };

        if (found is AliasSymbol alias) {
            found = (Symbol)alias.AliasedType;
        }

        if (found is NamedTypeSymbol named) {
            return named.OriginalDefinition;
        }

        ReportDeclaration(
            ErrorCode.ERR_CannotExtend,
            declaration.Type.Span,
            "only a named type or an array type can be extended"
        );
        return null;
    }

    /// <summary>
    ///     Binds the names an extension writes for the type's parameters.
    /// </summary>
    /// <remarks>
    ///     <c>extension Array&lt;T&gt;</c> on <c>struct Array&lt;Element&gt;</c>
    ///     means T is Element under another name, for the length of the extension.
    /// </remarks>
    static ImmutableArray<AliasSymbol> AliasTypeParameters(
        ExtensionDeclarationSyntax declaration,
        ExtensionSymbol extension,
        NamedTypeSymbol extended
    ) {
        var syntax = declaration.Type;
        while (syntax is ParenthesizedTypeSyntax parenthesized) {
            syntax = parenthesized.Type;
        }

        var arguments = syntax switch {
            SimpleNameTypeSyntax simple => simple.TypeArgumentList,
            QualifiedNameSyntax qualified => qualified.Right.TypeArgumentList,
            _ => null,
        };

        if (arguments is null) {
            return [];
        }

        var aliases = ImmutableArray.CreateBuilder<AliasSymbol>();
        var i = 0;
        foreach (var argument in arguments.Arguments) {
            if (i >= extended.TypeParameters.Length) {
                break;
            }

            // Only a bare name introduces one; 'Array<Int>' extends nothing new.
            if (argument is SimpleNameTypeSyntax { TypeArgumentList: null } name) {
                var target = extended.TypeParameters[i];
                var alias = new AliasSymbol(name.Identifier.Text, extension, name, Accessibility.Private);
                alias.TypeResolver = () => target;
                aliases.Add(alias);
            }

            i++;
        }

        return aliases.ToImmutable();
    }

    /// <summary>
    ///     Reports the members an extension may not add, per SYNTAX.md §11.
    /// </summary>
    bool RejectInExtension(SyntaxNode member) {
        switch (member) {
            case FieldDeclarationSyntax field when !IsComputed(field):
                ReportDeclaration(
                    ErrorCode.ERR_ExtensionStoredProperty,
                    field.Span,
                    "an extension cannot add stored properties, only computed ones"
                );
                return true;

            case InitDeclarationSyntax init:
                ReportDeclaration(
                    ErrorCode.ERR_ExtensionInitializer,
                    init.Span,
                    "an extension cannot add a designated initialiser"
                );
                return true;

            default:
                return false;
        }
    }

    static bool IsComputed(FieldDeclarationSyntax field) => field.Accessors is not null;

    // -----------------------------------------------------------------------

    internal void RecordDeclaration(SyntaxNode declaration, Symbol symbol) => _symbolsBySyntax[declaration] = symbol;

    /// <summary>What each conformer was found to bind each associated type to, and when.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<(NamedTypeSymbol, TypeParameterSymbol), (TypeSymbol? Bound, long Version)> _associated = new();

    /// <summary>
    ///     <see cref="ConformanceChecker.ResolveAssociated(NamedTypeSymbol, TypeParameterSymbol)" />,
    ///     worked out once for as long as no type's members or base types
    ///     change. A member looked up through a protocol asks it of every
    ///     associated type the protocol and those it refines have, for each
    ///     lookup, and one a conformer does not name is inferred from its
    ///     witnesses: binding Sable.Core's bodies asked it some sixteen
    ///     thousand times of fewer than two hundred pairs.
    /// </summary>
    internal TypeSymbol? ResolveAssociated(NamedTypeSymbol conformer, TypeParameterSymbol associated) {
        var version = NamedTypeSymbol.DeclarationVersion;
        if (_associated.TryGetValue((conformer, associated), out var known) && known.Version == version) {
            return known.Bound;
        }

        var bound = ConformanceChecker.ResolveAssociated(conformer, associated);
        _associated[(conformer, associated)] = (bound, version);
        return bound;
    }

    /// <summary>What a conformer binds a protocol's associated type to, or null when it does not: what its witness table lists.</summary>
    public TypeSymbol? AssociatedTypeOf(NamedTypeSymbol conformer, TypeParameterSymbol associated) => ResolveAssociated(conformer, associated);

    static void AddTo(Symbol container, Symbol member) {
        switch (container) {
            case ModuleSymbol module:
                module.AddMember(member);
                return;
            case NamedTypeSymbol type:
                type.AddMember(member);
                return;
            case ExtensionSymbol extension:
                extension.ExtendedType.AddMember(member);
                return;
            case FunctionSymbol function:
                function.AddLocalType(member);
                return;
            default: return;
        }
    }

    static Symbol? FindExisting(Symbol container, string name) {
        var members = container switch {
            ModuleSymbol module => module.GetMembers(name),
            NamedTypeSymbol type => type.GetMembers(name),
            ExtensionSymbol extension => extension.ExtendedType.GetMembers(name),
            FunctionSymbol function => function.GetLocalTypes(name),
            _ => [],
        };

        // Functions may be overloaded, so only non-function clashes count.
        foreach (var member in members) {
            if (member is not FunctionSymbol) {
                return member;
            }
        }

        return null;
    }

    /// <summary>
    ///     A property of a protocol's extension with the name of one of the
    ///     protocol's own requirements is the requirement's default, as a
    ///     method of the same name is — one declared in the protocol, the
    ///     other in its extension, whichever is read first — not a second
    ///     declaration of it.
    /// </summary>
    static bool IsPropertyDefault(FieldDeclarationSyntax declaration, Symbol container, Symbol existing) {
        if (existing is not FieldSymbol other) {
            return false;
        }

        var declaresRequirement = container is NamedTypeSymbol { TypeKind: TypeKind.Protocol }
            && declaration.Accessors is { } accessors && accessors.Accessors.All(static a => a.Body is null && a.ExpressionBody is null);
        var declaresDefault = container is ExtensionSymbol { ExtendedType.TypeKind: TypeKind.Protocol };
        return declaresDefault && other is { IsAbstract: true, ContainingSymbol: NamedTypeSymbol { TypeKind: TypeKind.Protocol } }
            || declaresRequirement && other.ContainingSymbol is ExtensionSymbol;
    }

    void ReportDuplicate(SyntaxToken identifier, string name, Symbol existing) {
        var where = existing.Location is { } location
            ? $" (already declared at offset {location.Start})"
            : string.Empty;

        ReportDeclaration(
            ErrorCode.ERR_DuplicateDeclaration,
            identifier.Span,
            $"'{name}' is already declared in this scope{where}",
            existing.DeclaredHere("declared first here")
        );
    }

    static Accessibility AccessibilityOf(SyntaxTokenList modifiers, Symbol container) {
        for (var i = 0; i < modifiers.Count; i++) {
            var modifier = modifiers[i];

            // 'private(set)' narrows who may write, not who may read.
            if (i + 1 < modifiers.Count && modifiers[i + 1].Kind == SyntaxKind.OpenParenToken) {
                i += 3;
                continue;
            }

            switch (modifier.Kind) {
                case SyntaxKind.PublicKeyword: return Accessibility.Public;
                case SyntaxKind.PackageKeyword: return Accessibility.Package;
                case SyntaxKind.InternalKeyword: return Accessibility.Internal;
                case SyntaxKind.PrivateKeyword: return Accessibility.Private;
            }
        }

        // A protocol's members are as visible as the protocol itself.
        return container is NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol
            ? protocol.DeclaredAccessibility
            : Accessibility.Internal;
    }

    /// <summary>The accessibility of <c>private(set)</c> and its kin: who may write.</summary>
    static Accessibility? SetterAccessibilityOf(SyntaxTokenList modifiers) {
        for (var i = 0; i + 2 < modifiers.Count; i++) {
            if (modifiers[i + 1].Kind == SyntaxKind.OpenParenToken && modifiers[i + 2].Kind == SyntaxKind.SetKeyword) {
                return modifiers[i].Kind switch {
                    SyntaxKind.PrivateKeyword => Accessibility.Private,
                    SyntaxKind.InternalKeyword => Accessibility.Internal,
                    SyntaxKind.PackageKeyword => Accessibility.Package,
                    _ => null,
                };
            }
        }

        return null;
    }

    internal static string NameOf(TypeSyntax name) =>
        name switch {
            SimpleNameTypeSyntax simple => simple.Identifier.Text,
            QualifiedNameSyntax qualified => $"{NameOf(qualified.Left)}.{qualified.Right.Identifier.Text}",
            _ => string.Empty,
        };
}
