using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
/// The scope an <c>extension</c> introduces between its members and the type
/// it extends.
/// </summary>
/// <remarks>
/// <para>Members declared in an extension belong to the extended type — that
/// is the point of an extension — so they are added to its member list. But
/// an extension may rename the type's parameters (<c>extension Array&lt;T&gt;
/// where T: Comparable</c> on a type declared as <c>Array&lt;Element&gt;</c>),
/// and those names are visible only inside the extension. This symbol holds
/// them: name lookup walks the containing chain, so a member finds the
/// extension's names first and the type's own after.</para>
/// <para>It is not a type and cannot be named. It exists so that two
/// extensions of the same type cannot see each other's parameter names.</para>
/// </remarks>
public sealed class ExtensionSymbol(
    NamedTypeSymbol extendedType,
    SyntaxNode declaration) : Symbol {
    public override SymbolKind Kind => SymbolKind.Extension;
    public override string Name => ExtendedType.Name;
    public override Symbol? ContainingSymbol => ExtendedType;
    public override ImmutableArray<SyntaxNode> DeclaringSyntax { get; } = [declaration];
    public override Accessibility DeclaredAccessibility => ExtendedType.DeclaredAccessibility;

    public NamedTypeSymbol ExtendedType { get; } = extendedType;

    /// <summary>
    ///     The extended type's parameters under the names this extension wrote,
    ///     which may differ from the ones the declaration used.
    /// </summary>
    public ImmutableArray<AliasSymbol> TypeParameterAliases { get; internal set; } = [];

    public override string QualifiedName => ExtendedType.QualifiedName;
}
