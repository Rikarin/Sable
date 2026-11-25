using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     The initialisers a class has without declaring any (SYNTAX.md §7): a
///     class with no superclass is made with <c>init()</c>; a subclass inherits
///     its superclass's, each of which initialises the subclass's own fields
///     and then calls the one it inherits, <c>super.init(…)</c>. Either way the
///     class's own stored fields must all start with a value.
/// </summary>
public sealed partial class Compilation {
    /// <summary>An initialiser a subclass inherits: the superclass's it calls, and the class declaration it is lowered at.</summary>
    public sealed record InheritedInitializer(FunctionSymbol Inherited, NamedTypeSymbol Superclass, NamedTypeSymbol Type, SyntaxTree Tree) {
        /// <summary>The class's declaration.</summary>
        public SyntaxNode Declaration => Type.DeclaringSyntax[0];
    }

    readonly Dictionary<FunctionSymbol, InheritedInitializer> _inheritedInitializers = [];
    readonly Dictionary<FunctionSymbol, BoundBlock> _inheritedBodies = [];
    readonly HashSet<NamedTypeSymbol> _implicitPending = [];

    /// <summary>Every initialiser a subclass inherits, with what it calls.</summary>
    public IReadOnlyDictionary<FunctionSymbol, InheritedInitializer> InheritedInitializers => _inheritedInitializers;

    /// <summary>
    ///     For a class or actor that declares no initialiser: <c>init()</c>
    ///     now, when nothing can be its superclass; otherwise once base types
    ///     are bound, when it is known whether one is, and before anything
    ///     checks what initialisers it has.
    /// </summary>
    void DeclareImplicitInitializers(NamedTypeSymbol type, SyntaxNode declaration) {
        if (type.GetMembers("init").Any() || !OwnFieldsStartWithAValue(type)) {
            return;
        }

        if (type.TypeKind == TypeKind.Actor || declaration is ClassDeclarationSyntax { BaseTypes: null }) {
            AddImplicitInit(type);
            return;
        }

        var tree = CurrentScope?.Tree;
        _implicitPending.Add(type);
        // Once every base list declared so far is bound — a deferral made
        // while running the deferrals runs after them all — and before
        // conformances are checked, which may ask for an 'init()'.
        TypeBinder.Defer(() => TypeBinder.Defer(() => InheritInitializers(type, declaration, tree)));
    }

    static bool OwnFieldsStartWithAValue(NamedTypeSymbol type) =>
        type.Members.OfType<FieldSymbol>().Where(static f => f is { IsStatic: false, IsComputed: false }).All(static f => f.HasInitializer);

    void AddImplicitInit(NamedTypeSymbol type) {
        var implicitInit = new FunctionSymbol("init", type, null, type.DeclaredAccessibility, FunctionKind.Initializer) {
            ReturnTypeResolver = () => type,
        };
        implicitInit.Parameters = [];
        type.AddMember(implicitInit);
    }

    /// <summary>
    ///     A subclass's initialisers, from its superclass's — whose own are
    ///     settled first, as they may be inherited too. One taking type
    ///     parameters of its own, or private to the superclass, is not.
    /// </summary>
    void InheritInitializers(NamedTypeSymbol type, SyntaxNode declaration, SyntaxTree? tree) {
        if (!_implicitPending.Remove(type)) {
            return;
        }

        var superclass = type.BaseTypes.OfType<NamedTypeSymbol>().FirstOrDefault(static b => b.TypeKind == TypeKind.Class);
        if (superclass is null) {
            AddImplicitInit(type);
            return;
        }

        if (_implicitPending.Contains(superclass.OriginalDefinition) && superclass.OriginalDefinition.DeclaringSyntax is [var superDeclaration, ..]) {
            InheritInitializers(superclass.OriginalDefinition, superDeclaration, FileScopeOf(superDeclaration)?.Tree);
        }

        if (tree is null) {
            return;
        }

        var substitution = TypeSubstitution.For(superclass);
        foreach (var inherited in superclass.OriginalDefinition.GetMembers("init").OfType<FunctionSymbol>().ToList()) {
            if (inherited.DeclaredAccessibility == Accessibility.Private || !inherited.TypeParameters.IsEmpty) {
                continue;
            }

            var init = new FunctionSymbol("init", type, null, inherited.DeclaredAccessibility, FunctionKind.Initializer) {
                IsAsync = inherited.IsAsync,
                ReturnTypeResolver = () => type,
                ThrownTypeResolver = () => inherited.ThrownType is { } thrown ? substitution.Apply(thrown) : null,
            };

            // Each parameter is the inherited one's — its label, and the
            // default its declaration gives — in the superclass's terms.
            init.Parameters = [
                ..inherited.Parameters.Select(p => new ParameterSymbol(p.Name, p.Label, init, p.DeclaringSyntax.FirstOrDefault(), p.Ordinal) {
                        HasDefaultValue = p.HasDefaultValue,
                        IsVariadic = p.IsVariadic,
                        Modifier = p.Modifier,
                        IsEscaping = p.IsEscaping,
                        IsSendable = p.IsSendable,
                        IsAutoclosure = p.IsAutoclosure,
                        TypeResolver = () => substitution.Apply(p.Type),
                    }
                )
            ];

            type.AddMember(init);
            _inheritedInitializers[init] = new(inherited, superclass, type, tree);
        }
    }

    /// <summary>
    ///     An inherited initialiser's body, <c>super.init(…)</c> with its own
    ///     parameters; the subclass's fields are stored first, as for any
    ///     initialiser of the class. Null for any other function.
    /// </summary>
    public BoundBlock? GetInheritedInitializerBody(FunctionSymbol function) {
        if (!_inheritedInitializers.TryGetValue(function, out var inherited)) {
            return null;
        }

        if (_inheritedBodies.TryGetValue(function, out var body)) {
            return body;
        }

        var at = inherited.Declaration;
        var call = new BoundCall(
            at,
            new BoundSuperAccess(at, inherited.Superclass),
            inherited.Inherited,
            [..function.Parameters.Select(p => (BoundExpression)new BoundParameterAccess(at, p))],
            Core.Void
        ) { Substitution = TypeSubstitution.For(inherited.Superclass) };

        return _inheritedBodies[function] = new BoundBlock(at, [], [new BoundExpressionStatement(at, call)]);
    }
}
