using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     What an editor asks of the semantic model and a compiler does not:
///     one member bound on its own, a member's edited text bound against the
///     symbols it had, the names in scope at a position, the members a value
///     offers, and every reference to a symbol.
/// </summary>
/// <remarks>
///     <para>
///         The language server answers a keystroke without binding the
///         program. A <i>member model</i> binds the one declaration it was made
///         for — a function, an initialiser, a property with its accessors —
///         and nothing else, once the compilation's signatures are bound. A
///         <i>speculative</i> member model binds a declaration's new text,
///         from a newer tree, as the declaration the compilation already has:
///         the server keeps one compilation while the user types and rebuilds
///         it only when they pause.
///     </para>
///     <para>
///         Like the rest of the model, none of this is thread-safe: a
///         compilation binds lazily, and its caches are written as it does.
///         One thread at a time per compilation.
///     </para>
/// </remarks>
public sealed partial class SemanticModel {
    /// <summary>For a member model, the only declaration it binds.</summary>
    readonly SyntaxNode? _member;

    /// <summary>For a speculative model, the declaration whose symbol <see cref="_member" /> is bound as.</summary>
    readonly SyntaxNode? _original;

    /// <summary>The position to record the names in scope at, or negative.</summary>
    readonly int _capturePosition = -1;

    TextSpan? _capturedFor;
    IReadOnlyList<Symbol>? _capturedScope;

    readonly Dictionary<SyntaxNode, SemanticModel> _memberModels = [];

    SemanticModel(Compilation compilation, SyntaxTree tree, SyntaxNode member, SyntaxNode? original, int capturePosition) {
        _compilation = compilation;
        SyntaxTree = tree;
        _member = member;
        _original = original;
        _capturePosition = capturePosition;
    }

    /// <summary>The declaration a member model binds, or null for a whole file's model.</summary>
    public SyntaxNode? Member => _member;

    /// <summary>The symbol a declaration introduced — for a speculative model's own declaration, the original's.</summary>
    Symbol? DeclaredSymbol(SyntaxNode declaration) =>
        _original is not null && ReferenceEquals(declaration, _member)
            ? _compilation.GetDeclaredSymbol(_original)
            : _compilation.GetDeclaredSymbol(declaration);

    Binder NewBinder(FileScope scope, Symbol owner) => new(_compilation, scope, owner) { CapturePosition = _capturePosition };

    /// <summary>Keeps the innermost scope any of this model's binders recorded.</summary>
    void CollectScope(Binder binder) {
        if (binder is not { ScopeAtCapture: { } scope, CapturedFor: { } span }) {
            return;
        }

        if (_capturedFor is { } previous && previous.Length < span.Length) {
            return;
        }

        _capturedFor = span;
        _capturedScope = scope;
    }

    // -----------------------------------------------------------------------
    // Members
    // -----------------------------------------------------------------------

    /// <summary>True for a declaration a member model can bind: one with a body or an initial value.</summary>
    public static bool IsBindableMember(SyntaxNode node) =>
        node is FunctionDeclarationSyntax
            or InitDeclarationSyntax
            or DeinitDeclarationSyntax
            or OperatorDeclarationSyntax
            or SubscriptDeclarationSyntax
            or FieldDeclarationSyntax
            or ConstDeclarationSyntax;

    /// <summary>
    ///     The member declaration a position sits in: the outermost bindable
    ///     declaration around it, so that a local function or a method of a
    ///     local type answers with the function whose body declares it.
    /// </summary>
    public static SyntaxNode? FindMember(SyntaxNode root, int position) {
        var node = FindToken(root, position).Parent;
        SyntaxNode? member = null;
        for (; node is not null; node = node.Parent) {
            if (IsBindableMember(node)) {
                member = node;
            }
        }

        return member;
    }

    /// <summary>
    ///     The token at a position: the one whose span holds it, or the word
    ///     ending there — the caret just after a name finds the name.
    /// </summary>
    public static SyntaxToken FindToken(SyntaxNode root, int position) {
        var at = FindTokenAt(root, position);
        if (!at.IsNone && at.Span.Start <= position && position < at.Span.End && IsWord(at)) {
            return at;
        }

        if (position > 0 && FindTokenAt(root, position - 1) is { IsNone: false } before && before.Span.End == position && IsWord(before)) {
            return before;
        }

        return at;
    }

    static bool IsWord(SyntaxToken token) =>
        token.Kind == SyntaxKind.IdentifierToken || token.IsKeyword || token.IsLiteral;

    /// <summary>The token whose full span, trivia included, holds a position; the last one for the end of the text.</summary>
    public static SyntaxToken FindTokenAt(SyntaxNode root, int position) {
        var node = root;
        while (true) {
            SyntaxNodeOrToken? last = null;
            SyntaxNodeOrToken? holder = null;
            foreach (var child in node.ChildNodesAndTokens()) {
                if (child.FullSpan.Length == 0) {
                    continue;
                }

                last = child;
                if (child.FullSpan.Start <= position && position < child.FullSpan.End) {
                    holder = child;
                    break;
                }
            }

            var next = holder ?? last;
            if (next is not { } chosen) {
                return default;
            }

            if (chosen.IsToken) {
                return chosen.AsToken();
            }

            node = chosen.AsNode()!;
        }
    }

    /// <summary>
    ///     A model that binds only <paramref name="member" />, a bindable
    ///     declaration of this file: what a request about one position needs.
    ///     Kept, so that asking again binds nothing.
    /// </summary>
    public SemanticModel GetMemberModel(SyntaxNode member) {
        if (!IsBindableMember(member)) {
            throw new ArgumentException("not a member with a body", nameof(member));
        }

        if (!_memberModels.TryGetValue(member, out var model)) {
            model = new(_compilation, SyntaxTree, member, null, -1);
            _memberModels[member] = model;
        }

        return model;
    }

    /// <summary>
    ///     A model that binds <paramref name="replacement" />, a newer text of
    ///     <paramref name="member" /> from another tree, as the declaration
    ///     the compilation has: its body is new, its symbol and everything
    ///     around it are the compilation's.
    /// </summary>
    /// <param name="capturePosition">
    ///     A position in <paramref name="replacement" />'s tree to record
    ///     the names in scope at, for <see cref="LookupSymbols" />; negative for none.
    /// </param>
    public SemanticModel GetSpeculativeModel(SyntaxNode member, SyntaxNode replacement, int capturePosition = -1) {
        if (!IsBindableMember(member) || member.Kind != replacement.Kind) {
            throw new ArgumentException("a member is replaced by a declaration of the same kind", nameof(replacement));
        }

        return new(_compilation, SyntaxTree, replacement, member, capturePosition);
    }

    // -----------------------------------------------------------------------
    // Scope
    // -----------------------------------------------------------------------

    /// <summary>
    ///     Every name that can be used bare at a position, nearest first: the
    ///     locals and parameters in scope there, the enclosing types' members
    ///     and type parameters, the module's declarations, what the file
    ///     imports, and <c>Sable.Core</c>. A name declared twice is listed once,
    ///     as the nearer declaration.
    /// </summary>
    public IReadOnlyList<Symbol> LookupSymbols(int position) {
        var member = _member ?? FindMember(SyntaxTree.Root, position);

        IReadOnlyList<Symbol> locals = [];
        Symbol? context = null;
        if (member is not null) {
            var probe = _original is not null
                ? new SemanticModel(_compilation, SyntaxTree, member, _original, position)
                : new SemanticModel(_compilation, SyntaxTree, member, null, position);
            probe.EnsureBound();
            locals = probe._capturedScope ?? [];
            context = probe.DeclaredSymbol(member);
        } else {
            for (var node = FindToken(SyntaxTree.Root, position).Parent; node is not null && context is null; node = node.Parent) {
                context = _compilation.GetDeclaredSymbol(node);
            }
        }

        var symbols = new List<Symbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(Symbol symbol) {
            if (symbol.Name.Length > 0 && seen.Add(symbol.Name)) {
                symbols.Add(symbol);
            }
        }

        foreach (var local in locals) {
            Add(local);
        }

        for (var symbol = context; symbol is not null; symbol = symbol.ContainingSymbol) {
            switch (symbol) {
                case FunctionSymbol function:
                    foreach (var parameter in function.TypeParameters) {
                        Add(parameter);
                    }

                    break;

                case NamedTypeSymbol type:
                    foreach (var parameter in type.TypeParameters) {
                        Add(parameter);
                    }

                    foreach (var inherited in GetMembersOf(type)) {
                        Add(inherited);
                    }

                    break;

                case ExtensionSymbol extension:
                    foreach (var alias in extension.TypeParameterAliases) {
                        Add(alias);
                    }

                    break;
            }
        }

        var scope = _compilation.GetFileScope(SyntaxTree);
        foreach (var declared in scope.Module.Members) {
            Add(declared);
        }

        foreach (var imported in scope.ImportedNames.Values) {
            Add(imported);
        }

        foreach (var alias in scope.ImportAliases.Values) {
            Add(alias);
        }

        foreach (var module in scope.Imports) {
            foreach (var imported in module.Members) {
                if (imported.DeclaredAccessibility == Accessibility.Public) {
                    Add(imported);
                }
            }
        }

        foreach (var core in _compilation.Core.Module.Members) {
            if (core.DeclaredAccessibility == Accessibility.Public) {
                Add(core);
            }
        }

        return symbols;
    }

    /// <summary>
    ///     The members a value of a type offers after a dot: its own, those
    ///     it inherits, and — for a type parameter, an existential or an opaque
    ///     type — those of what it is constrained to. A name declared at
    ///     several levels is listed once, as the nearest declaration.
    /// </summary>
    public IReadOnlyList<Symbol> GetMembersOf(TypeSymbol type) {
        // Base types and extensions bind with the signatures.
        _compilation.TypeBinder.ForceAll();

        var members = new List<Symbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Collect(type, []);
        return members;

        void Collect(TypeSymbol current, HashSet<TypeSymbol> visited) {
            if (!visited.Add(current)) {
                return;
            }

            switch (current) {
                case NamedTypeSymbol named:
                    foreach (var member in named.OriginalDefinition.Members) {
                        if (member is not ExtensionSymbol && member.Name.Length > 0 && seen.Add(member.Name + Arity(member))) {
                            members.Add(member);
                        }
                    }

                    foreach (var baseType in named.OriginalDefinition.BaseTypes) {
                        Collect(baseType, visited);
                    }

                    break;

                case ArrayTypeSymbol array:
                    foreach (var member in _compilation.ArrayMembers(array)) {
                        if (seen.Add(member.Name + Arity(member))) {
                            members.Add(member);
                        }
                    }

                    break;

                case ReferenceTypeSymbol reference:
                    Collect(reference.ReferencedType, visited);
                    break;

                case TypeParameterSymbol or DependentMemberTypeSymbol:
                    foreach (var constraint in AssociatedTypes.ConstraintsOf(current)) {
                        Collect(constraint, visited);
                    }

                    break;

                case OpaqueTypeSymbol opaque:
                    Collect(opaque.Constraint, visited);
                    break;

                case ExistentialTypeSymbol existential:
                    Collect(existential.Constraint, visited);
                    break;

                case CompositionTypeSymbol composition:
                    foreach (var part in composition.Parts) {
                        Collect(part, visited);
                    }

                    break;
            }
        }

        // Overloads are listed apiece, so a signature help can offer each.
        static string Arity(Symbol member) =>
            member is FunctionSymbol function ? "/" + string.Join(",", function.Parameters.Select(static p => p.Label ?? "_")) : "";
    }

    // -----------------------------------------------------------------------
    // Navigation
    // -----------------------------------------------------------------------

    /// <summary>
    ///     The symbol a name at a position refers to or declares, binding only
    ///     the member it is in.
    /// </summary>
    public Symbol? GetSymbolAt(int position) {
        var token = FindToken(_member ?? SyntaxTree.Root, position);
        return token.IsNone ? null : GetSymbolOfToken(token);
    }

    /// <summary>The symbol an identifier token names, as a declaration or as a use.</summary>
    public Symbol? GetSymbolOfToken(SyntaxToken token) {
        if (token.Kind != SyntaxKind.IdentifierToken && !token.IsContextualKeyword) {
            return null;
        }

        // A declaration's own name, or one of its parameters'.
        for (var node = token.Parent; node is not null; node = node.Parent) {
            if (node is StatementSyntax or ExpressionSyntax) {
                break;
            }

            if (DeclaredSymbol(node) is not { } declared) {
                continue;
            }

            if (NameTokenOf(declared, node) == token) {
                return declared;
            }

            foreach (var parameter in ParametersOf(declared)) {
                if (!parameter.DeclaringSyntax.IsEmpty && NameTokenOf(parameter, parameter.DeclaringSyntax[0]) == token) {
                    return parameter;
                }
            }

            break;
        }

        var model = ModelFor(token.SpanStart);

        // 'Point { x: 1 }': the label names the field it initialises.
        if (token.Parent is FieldInitializerSyntax { ColonToken.IsNone: false } initializer && initializer.NameToken == token) {
            for (var node = initializer.Parent; node is not null; node = node.Parent) {
                if (node is RecordExpressionSyntax record) {
                    return model.GetBoundNode(record) is { } bound && Unconverted(bound) is BoundRecordExpression built
                        ? built.Fields.Select(static f => f.Field).FirstOrDefault(f => f.Name == token.Text)
                        : null;
                }
            }
        }

        foreach (var candidate in UsesOf(token)) {
            if (model.GetSymbolOf(candidate) is { } symbol) {
                return symbol;
            }
        }

        // A local or a pattern's binding at its declaration.
        return model.LocalDeclaredBy(token);
    }

    static IEnumerable<Symbol> ParametersOf(Symbol symbol) =>
        symbol switch {
            FunctionSymbol function => [..function.TypeParameters, ..function.Parameters],
            NamedTypeSymbol type => type.TypeParameters,
            UnionCaseSymbol unionCase => unionCase.Payload,
            AliasSymbol alias => alias.TypeParameters,
            _ => [],
        };

    /// <summary>The model that has bound the member around a position: this one, or a member model of it.</summary>
    SemanticModel ModelFor(int position) {
        if (_member is not null || _bodiesBound) {
            return this;
        }

        return FindMember(SyntaxTree.Root, position) is { } member ? GetMemberModel(member) : this;
    }

    /// <summary>The nodes a use of a name may have been bound on, innermost first.</summary>
    static IEnumerable<SyntaxNode> UsesOf(SyntaxToken token) {
        if (token.Parent is not { } name) {
            yield break;
        }

        yield return name;

        // 'a.b' is bound on the access, not on 'b'; '.b' on itself.
        if (name.Parent is MemberAccessExpressionSyntax access && ReferenceEquals(access.Name, name)) {
            yield return access;
        }

        if (name.Parent is QualifiedNameSyntax qualified && ReferenceEquals(qualified.Right, name)) {
            yield return qualified;
        }
    }

    /// <summary>The local whose declaration this token names, if binding the member declared one there.</summary>
    Symbol? LocalDeclaredBy(SyntaxToken token) {
        EnsureBound();
        foreach (var node in _bound.Values) {
            foreach (var local in DeclaredLocals(node)) {
                if (!local.DeclaringSyntax.IsEmpty && NameTokenOf(local, local.DeclaringSyntax[0]) == token) {
                    return local;
                }
            }
        }

        return null;
    }

    static IEnumerable<Symbol> DeclaredLocals(BoundNode node) =>
        node switch {
            BoundBlock block => block.Locals,
            BoundLocalAccess access => [access.Local],
            _ => [],
        };

    /// <summary>
    ///     The token that names a symbol in its declaration: the first
    ///     identifier spelling its name, which is what a rename rewrites.
    /// </summary>
    public static SyntaxToken NameTokenOf(Symbol symbol, SyntaxNode declaration) {
        foreach (var token in declaration.DescendantTokens()) {
            if (token.Kind == SyntaxKind.IdentifierToken && token.Text == symbol.Name) {
                return token;
            }

            // Not into a body: a name there is a use, not the declaration's.
            if (token.Kind == SyntaxKind.OpenBraceToken || token.Kind == SyntaxKind.EqualsToken) {
                break;
            }
        }

        return default;
    }

    /// <summary>
    ///     True when two symbols are the same declaration: the same object,
    ///     or — for a constructed generic type, or a local bound twice — the
    ///     same declaring syntax.
    /// </summary>
    public static bool SameSymbol(Symbol? a, Symbol? b) {
        if (a is null || b is null) {
            return false;
        }

        if (ReferenceEquals(a, b)) {
            return true;
        }

        if (a is NamedTypeSymbol x && b is NamedTypeSymbol y) {
            return ReferenceEquals(x.OriginalDefinition, y.OriginalDefinition);
        }

        return a.Kind == b.Kind
               && !a.DeclaringSyntax.IsEmpty
               && !b.DeclaringSyntax.IsEmpty
               && ReferenceEquals(a.DeclaringSyntax[0], b.DeclaringSyntax[0])
               && a.Name == b.Name;
    }

    /// <summary>
    ///     Every place in this file that names <paramref name="symbol" />:
    ///     its declaration, if it is here, and each use. Only the members
    ///     whose text mentions the name are bound.
    /// </summary>
    public IReadOnlyList<TextSpan> FindReferences(Symbol symbol) {
        var name = symbol.Name;
        var root = _member ?? SyntaxTree.Root;
        if (name.Length == 0 || !root.ToFullString().Contains(name, StringComparison.Ordinal)) {
            return [];
        }

        var found = new List<TextSpan>();
        foreach (var token in root.DescendantTokens()) {
            if (token.Kind != SyntaxKind.IdentifierToken || token.Text != name) {
                continue;
            }

            if (SameSymbol(GetSymbolOfToken(token), symbol)) {
                found.Add(token.Span);
            }
        }

        return found;
    }

    /// <summary>
    ///     The names in scope where this model's binders were asked to record
    ///     them, for a model made with a capture position.
    /// </summary>
    internal IReadOnlyList<Symbol>? CapturedScope {
        get {
            EnsureBound();
            return _capturedScope;
        }
    }
}
