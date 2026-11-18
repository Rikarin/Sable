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
    ///     What <c>extension Range where Bound == Int</c> asks of the extended
    ///     type's arguments: in force in its members' bodies, and met by a
    ///     type before those members are found on it.
    /// </summary>
    public ImmutableArray<SameTypeRequirement> SameTypeRequirements {
        get {
            BindWhereClause();
            return _sameType;
        }
        internal set => _sameType = value;
    }

    /// <inheritdoc cref="SameTypeRequirements" />
    public ImmutableArray<ConformanceRequirement> ConformanceRequirements {
        get {
            BindWhereClause();
            return _conformances;
        }
        internal set => _conformances = value;
    }

    /// <summary>True when the extension's members are there only for some of the type's arguments.</summary>
    public bool IsConditional => !SameTypeRequirements.IsEmpty || !ConformanceRequirements.IsEmpty;

    ImmutableArray<SameTypeRequirement> _sameType = [];
    ImmutableArray<ConformanceRequirement> _conformances = [];

    /// <summary>Binds the <c>where</c> clause, the first time either list is asked for; null once it has.</summary>
    internal Action? PendingWhereClause { get; set; }

    void BindWhereClause() {
        if (PendingWhereClause is { } bind) {
            PendingWhereClause = null;
            bind();
        }
    }

    public override string QualifiedName => ExtendedType.QualifiedName;
}
