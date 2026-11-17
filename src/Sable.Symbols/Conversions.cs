namespace Sable.Symbols;

/// <summary>
/// Decides whether one type may be used where another is expected.
/// </summary>
/// <remarks>
/// The relation is deliberately small, per docs/03-type-system.md §3.8: no
/// implicit numeric widening, no array covariance, no structural record
/// subtyping. A numeric literal is the one thing that bends, because it has
/// no type of its own until the context gives it one.
/// </remarks>
internal sealed class Conversions(CoreTypes core)
{
    /// <summary>The conversion from source to target, or null when there is none.</summary>
    public ConversionKind? Classify(TypeSymbol source, TypeSymbol target, bool sourceIsLiteral)
    {
        // An error on either side converts, so one mistake stays one mistake.
        if (source.IsError || target.IsError) return ConversionKind.Identity;

        if (source.Equals(target)) return ConversionKind.Identity;

        // 'Never' is the bottom type: an expression that does not return fits
        // anywhere.
        if (source.Equals(core.Never)) return ConversionKind.Bottom;

        if (sourceIsLiteral && IsLiteralCompatible(source, target))
            return ConversionKind.LiteralTyping;

        if (target is OptionalTypeSymbol optional)
        {
            if (source.Equals(optional.UnderlyingType)) return ConversionKind.OptionalWrap;
            if (sourceIsLiteral && IsLiteralCompatible(source, optional.UnderlyingType))
                return ConversionKind.OptionalWrap;
        }

        if (target is StructuralUnionTypeSymbol union)
        {
            foreach (var unionCase in union.Cases)
            {
                if (source.Equals(unionCase)) return ConversionKind.UnionInjection;
                if (sourceIsLiteral && IsLiteralCompatible(source, unionCase))
                    return ConversionKind.UnionInjection;
            }
        }

        if (target is ExistentialTypeSymbol) return ConversionKind.Existential;

        if (IsDerivedFrom(source, target)) return ConversionKind.Upcast;

        return null;
    }

    public bool HasImplicitConversion(
        TypeSymbol source, TypeSymbol target, bool sourceIsLiteral = false) =>
        Classify(source, target, sourceIsLiteral) is not null;

    /// <summary>How much a conversion costs, for choosing between overloads.</summary>
    public static int Cost(ConversionKind kind) => kind switch
    {
        ConversionKind.Identity => 0,
        ConversionKind.LiteralTyping => 1,
        ConversionKind.Upcast => 2,
        ConversionKind.OptionalWrap => 3,
        ConversionKind.UnionInjection => 4,
        ConversionKind.Existential => 5,
        ConversionKind.Bottom => 0,
        _ => 10,
    };

    /// <summary>
    /// A literal has no type of its own; an integer literal fits any integer
    /// type and a floating one any float type, provided the value fits.
    /// </summary>
    private bool IsLiteralCompatible(TypeSymbol source, TypeSymbol target)
    {
        if (source.Equals(core.Int)) return core.IsInteger(target) || core.Floats.Contains(target);
        if (source.Equals(core.F64)) return core.Floats.Contains(target);
        return false;
    }

    private static bool IsDerivedFrom(TypeSymbol source, TypeSymbol target)
    {
        if (source is not NamedTypeSymbol named) return false;

        var visited = new HashSet<NamedTypeSymbol>();
        var queue = new Queue<NamedTypeSymbol>();
        queue.Enqueue(named);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current)) continue;

            foreach (var baseType in current.BaseTypes)
            {
                if (baseType.Equals(target)) return true;
                if (baseType is NamedTypeSymbol baseNamed) queue.Enqueue(baseNamed);
            }
        }
        return false;
    }
}
