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
    ///     The module the extension is written in, which may not be the
    ///     extended type's: who may see its internal and private members is
    ///     decided here, not where the type was declared.
    /// </summary>
    public ModuleSymbol? DeclaringModule { get; init; }

    /// <summary>
    ///     The extended type's parameters under the names this extension wrote,
    ///     which may differ from the ones the declaration used.
    /// </summary>
    public ImmutableArray<AliasSymbol> TypeParameterAliases { get; internal set; } = [];

    /// <summary>
    ///     The <c>where T == Int</c> requirements of a constrained extension
    ///     (SYNTAX.md §11): in force inside its members, and met wherever one
    ///     of them is used.
    /// </summary>
    public ImmutableArray<SameTypeRequirement> SameTypeRequirements { get; internal set; } = [];

    /// <summary>
    ///     Its <c>where T: P</c> requirements. Unlike a type's own, these do not
    ///     go in T's constraint list: they hold only inside this extension.
    /// </summary>
    public ImmutableArray<ConformanceRequirement> ConformanceRequirements { get; internal set; } = [];

    /// <summary>The protocols this extension conforms the type to, if any (<c>extension Box: P where …</c>).</summary>
    public ImmutableArray<TypeSymbol> Conformances { get; internal set; } = [];

    /// <summary>True when the extension has requirements, so its members and conformances are conditional.</summary>
    public bool IsConstrained => !SameTypeRequirements.IsEmpty || !ConformanceRequirements.IsEmpty;

    /// <summary>
    ///     The first requirement the extended type, read through
    ///     <paramref name="substitution" />, does not meet, said as the
    ///     extension says it; null when it meets them all.
    /// </summary>
    internal string? Unmet(TypeSubstitution substitution, TypeEquivalence? context) {
        context ??= TypeEquivalence.Empty;

        foreach (var (left, right) in SameTypeRequirements) {
            if (!context.AreSame(substitution.Apply(left), substitution.Apply(right))) {
                return $"'{left.ToDisplayString()} == {right.ToDisplayString()}'";
            }
        }

        foreach (var (subject, constraint) in ConformanceRequirements) {
            if (!Conversions.Satisfies(substitution.Apply(subject), substitution.Apply(constraint), context)) {
                return $"'{subject.ToDisplayString()}: {constraint.ToDisplayString()}'";
            }
        }

        return null;
    }

    public override string QualifiedName => ExtendedType.QualifiedName;
}
