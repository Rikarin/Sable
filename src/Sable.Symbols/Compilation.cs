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
public sealed class Compilation {
    readonly Dictionary<string, ModuleSymbol> _modules = new(StringComparer.Ordinal);
    readonly Dictionary<SyntaxTree, FileScope> _fileScopes = [];
    readonly Dictionary<SyntaxNode, Symbol> _symbolsBySyntax = [];
    readonly Dictionary<SyntaxTree, SemanticModel> _semanticModels = [];
    readonly DiagnosticBag _declarationDiagnostics = new();

    Compilation(ImmutableArray<SyntaxTree> syntaxTrees, ParseOptions options) {
        SyntaxTrees = syntaxTrees;
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
                SyntaxTree.Parse(new(text, $"file{i}.sab"))
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

    public FileScope GetFileScope(SyntaxTree tree) => _fileScopes[tree];

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
    ///     Parse and body diagnostics know their file; those from declaring
    ///     and from lazily bound signatures do not yet, and come without one.
    /// </summary>
    public IReadOnlyList<(Diagnostic Diagnostic, SyntaxTree? Tree)> GetAllDiagnosticsByFile() {
        var all = new List<(Diagnostic, SyntaxTree?)>();
        foreach (var tree in SyntaxTrees) {
            all.AddRange(tree.Diagnostics.Select(d => (d, (SyntaxTree?)tree)));
        }

        foreach (var tree in SyntaxTrees) {
            if (_fileScopes.ContainsKey(tree)) {
                all.AddRange(GetSemanticModel(tree).GetDiagnostics().Select(d => (d, (SyntaxTree?)tree)));
            }
        }

        TypeBinder.ForceAll();
        all.AddRange(_declarationDiagnostics.Select(static d => (d, (SyntaxTree?)null)));
        all.AddRange(TypeBinder.Diagnostics.Select(static d => (d, (SyntaxTree?)null)));
        return all;
    }

    internal void ReportDeclaration(ErrorCode code, TextSpan span, string message) =>
        _declarationDiagnostics.Error(code, span, message);

    // -----------------------------------------------------------------------
    // Declaration gathering
    // -----------------------------------------------------------------------

    void DeclareAll() {
        foreach (var tree in SyntaxTrees) {
            if (tree.Root is not CompilationUnitSyntax unit) {
                continue;
            }

            // "One module declaration per file, first non-trivia item"
            // (spec/SYNTAX.md §1). An empty file is let be; one that declares
            // something has to say where it belongs.
            if (unit.Module is null && unit.Members.Count > 0) {
                ReportDeclaration(
                    ErrorCode.ERR_NoModuleDeclaration,
                    new(unit.Members[0].Span.Start, 0),
                    "a file that declares anything needs a 'module' declaration first"
                );
            }

            var moduleName = unit.Module is null
                ? string.Empty
                : NameOf(unit.Module.Name);

            if (!_modules.TryGetValue(moduleName, out var module)) {
                module = new(moduleName);
                _modules[moduleName] = module;
            }

            var scope = new FileScope(tree, module);
            _fileScopes[tree] = scope;
            CurrentScope = scope;

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

            case TypealiasDeclarationSyntax declaration:
                DeclareTypealias(declaration, container);
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
                DeclareInitializer(declaration, type);
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
            IsAbstract = modifiers.Any(SyntaxKind.AbstractKeyword),
            IsNoncopyable = typeKind is TypeKind.Struct or TypeKind.Union
                && attributes.Any(static a => a.Name.ToString().Trim() == "noncopy"),
            IsInlineLayout = typeKind is TypeKind.Struct
                && attributes.Any(static a => a.Name.ToString().Trim() == "inline_layout")
        };

        if (type.IsInlineLayout) {
            type.AddMember(Core.FromBytes(type));
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
        // substituted like any other generic.
        if (typeKind == TypeKind.Protocol) {
            type.SelfTypeParameter = new("Self", type, declaration, -1);
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

        // A class or actor with no initialiser of its own, whose fields all
        // start with a value, is made with 'init()'.
        if (typeKind is TypeKind.Class or TypeKind.Actor && !type.GetMembers("init").Any()
            && type.Members.OfType<FieldSymbol>().Where(static f => f is { IsStatic: false, IsComputed: false }).All(static f => f.HasInitializer)) {
            var implicitInit = new FunctionSymbol("init", type, null, type.DeclaredAccessibility, FunctionKind.Initializer) {
                ReturnTypeResolver = () => type,
            };
            implicitInit.Parameters = [];
            type.AddMember(implicitInit);
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
                    var parameter = new ParameterSymbol(field.Name, field.Name, function, null, i) {
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
        var name = declaration.Identifier.Text;
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
            IsAbstract = declaration.Body is null && declaration.ExpressionBody is null
        };

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
                || declaration.Modifiers.Any(SyntaxKind.AsyncKeyword)
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
            ? TypeBinder.BindType(clause.Type, function, scope)
            : Core.Void;

        function.IsRethrows =
            declaration.ThrowsSpec?.ThrowsKeyword.Kind == SyntaxKind.RethrowsKeyword;

        function.ThrownTypeResolver = () => {
            if (declaration.ThrowsSpec is not { } spec) {
                return null;
            }

            if (function.IsRethrows) {
                return TypeBinder.RethrownType(function.Parameters.Select(static p => p.Type));
            }

            return spec.ErrorType is { } errorType
                ? TypeBinder.BindType(errorType, function, scope)
                : Core.AnyError;
        };

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
            Modifier = ModifierOf(syntax)
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
                SyntaxKind.BorrowingKeyword => ParameterModifier.Borrowing,
                SyntaxKind.RefKeyword => ParameterModifier.Ref,
                SyntaxKind.InKeyword => ParameterModifier.In,
                _ => ParameterModifier.None,
            };
    }

    void DeclareField(FieldDeclarationSyntax declaration, Symbol container) {
        if (declaration.Declarator.Pattern is not BindingPatternSyntax binding) {
            return;
        }

        var name = binding.Identifier.Text;
        if (name.Length == 0) {
            return;
        }

        if (FindExisting(container, name) is { } existing) {
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
            SetterAccessibility = SetterAccessibilityOf(declaration.Modifiers),
            IsLazy = declaration.Modifiers.Any(SyntaxKind.LazyKeyword),
            ReferenceKind = declaration.Modifiers.Any(SyntaxKind.WeakKeyword) ? ReferenceKind.Weak
                : declaration.Modifiers.Any(SyntaxKind.UnownedKeyword) ? ReferenceKind.Unowned
                : ReferenceKind.Strong,
            IsComputed = declaration.Accessors is not null,
            HasSetter = declaration.Accessors is not { } list
                || list.Accessors.Any(static a => a.Keyword.Kind == SyntaxKind.SetKeyword),
            IsAbstract = declaration.Accessors is { } accessors
                && accessors.Accessors.All(static a => a.Body is null && a.ExpressionBody is null),
            HasInitializer = declaration.Declarator.Initializer is not null
        };

        TypeBinder.BindFieldType(field, declaration);
        RecordDeclaration(declaration, field);
        AddTo(container, field);
    }

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

    void DeclareTypealias(TypealiasDeclarationSyntax declaration, Symbol container) {
        var name = declaration.Identifier.Text;
        if (name.Length == 0) {
            return;
        }

        if (FindExisting(container, name) is { } existing) {
            ReportDuplicate(declaration.Identifier, name, existing);
            return;
        }

        var alias = new AliasSymbol(name, container, declaration, AccessibilityOf(declaration.Modifiers, container));

        TypeBinder.BindAliasTarget(alias, declaration.Type);
        AddTo(container, alias);
    }

    void DeclareUnionCase(UnionCaseDeclarationSyntax declaration, NamedTypeSymbol union) {
        var name = declaration.Identifier.Text;
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
        var name = declaration.Identifier.Text;
        if (name.Length == 0) {
            return;
        }

        if (FindExisting(enumType, name) is { } existing) {
            ReportDuplicate(declaration.Identifier, name, existing);
            return;
        }

        enumType.AddMember(
            new EnumCaseSymbol(name, enumType, declaration) { HasExplicitValue = declaration.Value is not null }
        );
    }

    void DeclareInitializer(InitDeclarationSyntax declaration, NamedTypeSymbol type) {
        var function = new FunctionSymbol(
            "init",
            type,
            declaration,
            AccessibilityOf(declaration.Modifiers, type),
            FunctionKind.Initializer
        );

        function.Parameters = [
            ..declaration.Parameters.Parameters.Select((parameter, i) =>
                DeclareParameter(parameter, function, i)
            )
        ];

        function.ReturnTypeResolver = () => type;
        RecordDeclaration(declaration, function);
        type.AddMember(function);
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
            }
        };

        function.Parameters = [
            ..declaration.Parameters.Parameters.Select((parameter, i) =>
                DeclareParameter(parameter, function, i)
            )
        ];

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

        TypeBinder.Defer(() => {
                if (declaration.BaseTypes is { } constraints) {
                    parameter.Constraints = [
                        ..constraints.Types.Select(syntax => TypeBinder.BindType(syntax, protocol, scope))
                    ];
                }

                if (declaration.DefaultType is { } fallback) {
                    parameter.DefaultType = TypeBinder.BindType(fallback, protocol, scope);
                }
            }
        );

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
        foreach (var (declaration, container, scope) in Extensions) {
            CurrentScope = scope;

            var extended = ResolveExtendedType(declaration, container, scope);
            if (extended is null) {
                continue;
            }

            var extension = new ExtensionSymbol(extended, declaration);
            extension.TypeParameterAliases = AliasTypeParameters(declaration, extension, extended);
            RecordDeclaration(declaration, extension);

            // ': P' on an extension is retroactive conformance.
            if (declaration.BaseTypes is { } baseTypes) {
                var written = scope;
                TypeBinder.Defer(() => {
                        foreach (var baseType in baseTypes.Types) {
                            var bound = TypeBinder.BindType(baseType, extension, written);
                            CheckCoherence(extended, bound, written, baseType.Span);
                            extended.BaseTypes = extended.BaseTypes.Add(bound);
                        }
                    }
                );
            }

            foreach (var member in declaration.Members) {
                if (RejectInExtension(member)) {
                    continue;
                }

                DeclareMember(member, extension);
            }
        }

        CurrentScope = null;
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
            "only a named type can be extended"
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

    void ReportDuplicate(SyntaxToken identifier, string name, Symbol existing) {
        var where = existing.Location is { } location
            ? $" (already declared at offset {location.Start})"
            : string.Empty;

        ReportDeclaration(
            ErrorCode.ERR_DuplicateDeclaration,
            identifier.Span,
            $"'{name}' is already declared in this scope{where}"
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
