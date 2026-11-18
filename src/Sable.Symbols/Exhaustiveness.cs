using System.Collections.Immutable;
using System.Numerics;

namespace Sable.Symbols;

/// <summary>
///     Exhaustiveness and redundancy for <c>match</c>, by Maranget's usefulness
///     algorithm (<i>Warnings for pattern matching</i>, JFP 2007), per docs/03 §3.3.
/// </summary>
/// <remarks>
///     <para>
///         Both questions are one: is a pattern vector <i>useful</i> after a
///         matrix of earlier rows — does it match some value none of them do?
///         An arm is redundant when its patterns are not useful after the arms
///         before it; a match is exhaustive when a wildcard is not useful after
///         all of them. When a vector is useful the algorithm also returns a
///         witness, a value it matches and the rows do not, which is what the
///         diagnostic shows.
///     </para>
///     <para>
///         Patterns are first lowered to three shapes: a wildcard, a
///         constructor applied to sub-patterns, and an or-pattern. Types with a
///         finite set of constructors — booleans, enums, unions, optionals,
///         structural unions, tuples and records (one constructor), arrays (one
///         per length that matters, plus "longer") — can be covered without a
///         wildcard. An integer is split into intervals: every literal and
///         range a column names cuts the type's domain, and the pieces are its
///         constructors, so <c>0..&lt;10</c> and <c>10...255</c> cover a
///         <c>U8</c>. Other literals, ranges and open type tests have
///         infinitely many constructors and never are.
///     </para>
/// </remarks>
internal sealed class Exhaustiveness(CoreTypes core) {
    // ---- Lowered patterns ---------------------------------------------------

    internal abstract class Pat;

    sealed class Wild : Pat {
        public static readonly Wild Instance = new();
    }

    sealed class CtorPat(Ctor ctor, ImmutableArray<Pat> args) : Pat {
        public Ctor Ctor { get; } = ctor;
        public ImmutableArray<Pat> Args { get; } = args;
    }

    sealed class OrPat(ImmutableArray<Pat> alternatives) : Pat {
        public ImmutableArray<Pat> Alternatives { get; } = alternatives;
    }

    /// <summary><c>[a, ...rest, b]</c>: every array at least as long as its fixed parts.</summary>
    sealed class SlicePat(ImmutableArray<Pat> prefix, ImmutableArray<Pat> suffix) : Pat {
        public ImmutableArray<Pat> Prefix { get; } = prefix;
        public ImmutableArray<Pat> Suffix { get; } = suffix;
        public int MinLength => Prefix.Length + Suffix.Length;
    }

    enum CtorKind {
        Bool,
        EnumCase,
        UnionCase,
        None,
        Some,
        Tuple,
        Record,
        TypeCase,
        Literal,
        Interval,
        Length,
        LengthAtLeast,
        Opaque,
    }

    /// <summary>A constructor: two are the same constructor exactly when their keys are equal.</summary>
    sealed class Ctor(CtorKind kind, string key, string name, ImmutableArray<TypeSymbol> argTypes) {
        public CtorKind Kind { get; } = kind;
        public string Key { get; } = key;
        public string Name { get; } = name;
        public ImmutableArray<TypeSymbol> ArgTypes { get; } = argTypes;
        public int Arity => ArgTypes.Length;

        /// <summary>For an array length constructor, the length.</summary>
        public int Length { get; init; }

        /// <summary>For an integer interval, its least and greatest values.</summary>
        public BigInteger Low { get; init; }

        /// <inheritdoc cref="Low" />
        public BigInteger High { get; init; }

        /// <summary>For a record, the field names in argument order.</summary>
        public ImmutableArray<string> FieldNames { get; init; } = [];

        public override bool Equals(object? obj) => obj is Ctor other && other.Key == Key;
        public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);
    }

    /// <summary>The work allowed before giving up and calling a match exhaustive.</summary>
    const int Budget = 200_000;

    int _steps;
    int _opaque;

    /// <summary>True when the budget ran out and the answers stopped being exact.</summary>
    public bool GaveUp => _steps > Budget;

    /// <summary>True when a lowered pattern had an error, already reported, that makes "not covered" a cascade.</summary>
    public bool HasErrors { get; private set; }

    // ---- Lowering -----------------------------------------------------------

    public Pat Lower(BoundPattern pattern) {
        switch (pattern) {
            case BoundWildcardPattern or BoundBindingPattern:
                return Wild.Instance;

            // A pattern that did not bind matches nothing anyone can name;
            // treating it as a wildcard would make the arms after it redundant.
            case BoundErrorPattern:
                HasErrors = true;
                return Opaque();

            case BoundOrPattern or:
                return new OrPat([Lower(or.Left), Lower(or.Right)]);

            case BoundLiteralPattern { Value: bool value }:
                return Leaf(BoolCtor(value));

            case BoundLiteralPattern { Value: null } when pattern.InputType is OptionalTypeSymbol:
                return Leaf(NoneCtor());

            // An integer literal is the interval of one value; a range, of all
            // it spans. Each splits the type's domain where it begins and ends.
            case BoundLiteralPattern { Value: long or int or ulong } literal when Domain(pattern.InputType) is (var min, var max): {
                var value = Integer(literal);
                return value < min || value > max ? Opaque() : Leaf(IntervalCtor(value, value, min, max));
            }

            case BoundRangePattern range when Domain(pattern.InputType) is (var min, var max)
                                              && range.Lower is null or BoundLiteralPattern { Value: long or int or ulong }
                                              && range.Upper is null or BoundLiteralPattern { Value: long or int or ulong }: {
                var low = range.Lower is BoundLiteralPattern lower ? Integer(lower) : min;
                var high = range.Upper is BoundLiteralPattern upper ? Integer(upper) - (range.IsHalfOpen ? 1 : 0) : max;
                low = BigInteger.Max(low, min);
                high = BigInteger.Min(high, max);

                // An empty range matches nothing, so it covers nothing either.
                return low > high ? Opaque() : Leaf(IntervalCtor(low, high, min, max));
            }

            case BoundLiteralPattern literal when pattern.InputType is not StructuralUnionTypeSymbol: {
                var text = $"{(literal.IsNegative ? "-" : "")}{literal.Value}";
                var key = $"lit:{literal.Value?.GetType().Name}:{text}";
                return Leaf(new Ctor(CtorKind.Literal, key, Quote(literal.Value, text), []));
            }

            case BoundEnumCasePattern enumCase:
                return Leaf(EnumCtor(enumCase.EnumCase));

            case BoundSomePattern some when some.InputType is OptionalTypeSymbol optional:
                return new CtorPat(SomeCtor(optional), [Lower(some.Payload)]);

            case BoundUnionCasePattern unionCase: {
                var ctor = UnionCtor(unionCase.UnionCase, pattern.InputType);
                return new CtorPat(ctor, Fit(unionCase.Payload.Select(Lower), ctor.Arity));
            }

            case BoundTuplePattern tuple when pattern.InputType is TupleTypeSymbol tupleType: {
                var ctor = TupleCtor(tupleType);
                return new CtorPat(ctor, Fit(tuple.Elements.Select(Lower), ctor.Arity));
            }

            case BoundRecordPattern { TestsType: false, IsIncomplete: false } record
                when pattern.InputType is NamedTypeSymbol named: {
                var ctor = RecordCtor(named);
                var args = ctor.FieldNames
                    .Select(name => record.Fields.FirstOrDefault(f => f.Field.Name == name) is { Pattern: { } sub }
                        ? Lower(sub)
                        : Wild.Instance
                    )
                    .ToImmutableArray();
                return new CtorPat(ctor, args);
            }

            case BoundArrayPattern array when pattern.InputType is ArrayTypeSymbol arrayType: {
                if (array.RestIndex < 0) {
                    return new CtorPat(LengthCtor(arrayType, array.Elements.Length), [..array.Elements.Select(Lower)]);
                }

                return new SlicePat(
                    [..array.Elements.Take(array.RestIndex).Select(Lower)],
                    [..array.Elements.Skip(array.RestIndex + 1).Select(Lower)]
                );
            }

            case BoundTypeTestPattern test: {
                if (test.TestedType.Equals(pattern.InputType)) {
                    return Wild.Instance;
                }

                if (pattern.InputType is StructuralUnionTypeSymbol union
                    && union.Cases.Any(c => c.Equals(test.TestedType))) {
                    return Leaf(TypeCaseCtor(test.TestedType));
                }

                return Opaque();
            }

            default:
                // Ranges, type-testing records and anything else that matches
                // some values of an unbounded set.
                return Opaque();
        }
    }

    static CtorPat Leaf(Ctor ctor) => new(ctor, []);

    static BigInteger Integer(BoundLiteralPattern literal) {
        var value = literal.Value switch {
            ulong u => (BigInteger)u,
            long l => l,
            int i => i,
            _ => BigInteger.Zero,
        };

        return literal.IsNegative ? -value : value;
    }

    /// <summary>The least and greatest values of an integer type, or null for any other type.</summary>
    (BigInteger Min, BigInteger Max)? Domain(TypeSymbol type) {
        if (!core.IsInteger(type)) {
            return null;
        }

        var name = ((NamedTypeSymbol)type).Name;

        // Int and UInt are the platform word, 64 bits on every target.
        var signed = name[0] == 'I';
        var bits = name is "Int" or "UInt" ? 64 : int.Parse(name.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);
        return signed
            ? (-(BigInteger.One << (bits - 1)), (BigInteger.One << (bits - 1)) - 1)
            : (BigInteger.Zero, (BigInteger.One << bits) - 1);
    }

    CtorPat Opaque() => Leaf(new Ctor(CtorKind.Opaque, $"opaque:{_opaque++}", "_", []));

    /// <summary>Pads or trims sub-patterns to the constructor's arity, so a wrong count reported elsewhere does not crash this.</summary>
    static ImmutableArray<Pat> Fit(IEnumerable<Pat> patterns, int arity) {
        var list = patterns.Take(arity).ToList();
        while (list.Count < arity) {
            list.Add(Wild.Instance);
        }

        return [..list];
    }

    static string Quote(object? value, string text) =>
        value switch {
            string s => $"\"{s}\"",
            char c => $"'{c}'",
            _ => text,
        };

    // ---- Constructors -------------------------------------------------------

    static Ctor BoolCtor(bool value) => new(CtorKind.Bool, value ? "true" : "false", value ? "true" : "false", []);

    static Ctor NoneCtor() => new(CtorKind.None, "none", "none", []);

    static Ctor SomeCtor(OptionalTypeSymbol optional) => new(CtorKind.Some, "some", "Some", [optional.UnderlyingType]);

    static Ctor EnumCtor(EnumCaseSymbol enumCase) => new(CtorKind.EnumCase, $"enum:{enumCase.Name}", enumCase.Name, []);

    static Ctor UnionCtor(UnionCaseSymbol unionCase, TypeSymbol unionType) {
        var substitution = TypeSubstitution.For(unionType);
        return new Ctor(
            CtorKind.UnionCase,
            $"case:{unionCase.Name}",
            unionCase.Name,
            [..unionCase.Payload.Select(p => substitution.Apply(p.Type))]
        );
    }

    /// <summary>The integers from <paramref name="low" /> to <paramref name="high" />, both included, of a type whose values run from <paramref name="min" /> to <paramref name="max" />.</summary>
    static Ctor IntervalCtor(BigInteger low, BigInteger high, BigInteger min, BigInteger max) {
        var name = low == high ? $"{low}"
            : low == min ? $"...{high}"
            : high == max ? $"{low}..."
            : $"{low}...{high}";
        return new(CtorKind.Interval, $"int:{low}:{high}", name, []) { Low = low, High = high };
    }

    /// <summary>
    ///     Cuts <paramref name="low" />...<paramref name="high" /> wherever an
    ///     interval in the column begins or ends, so each piece is inside every
    ///     interval it touches.
    /// </summary>
    static List<Ctor> Split(BigInteger low, BigInteger high, IEnumerable<Pat> column, BigInteger min, BigInteger max) {
        var cuts = new SortedSet<BigInteger> { low, high + 1 };
        foreach (var pattern in column) {
            if (pattern is CtorPat { Ctor: { Kind: CtorKind.Interval } interval }) {
                if (interval.Low > low && interval.Low <= high) {
                    cuts.Add(interval.Low);
                }

                if (interval.High >= low && interval.High < high) {
                    cuts.Add(interval.High + 1);
                }
            }
        }

        var pieces = new List<Ctor>();
        var start = low;
        foreach (var cut in cuts.Skip(1)) {
            pieces.Add(IntervalCtor(start, cut - 1, min, max));
            start = cut;
        }

        return pieces;
    }

    static Ctor TupleCtor(TupleTypeSymbol tuple) => new(CtorKind.Tuple, "tuple", "", tuple.ElementTypes);

    static Ctor RecordCtor(NamedTypeSymbol type) {
        var substitution = TypeSubstitution.For(type);
        var fields = type.Members.OfType<FieldSymbol>().Where(static f => !f.IsStatic).ToList();
        return new Ctor(
            CtorKind.Record,
            "record",
            type.ToDisplayString(),
            [..fields.Select(f => substitution.Apply(f.Type))]
        ) { FieldNames = [..fields.Select(static f => f.Name)] };
    }

    static Ctor TypeCaseCtor(TypeSymbol type) =>
        new(CtorKind.TypeCase, $"type:{type.ToDisplayString()}", type.ToDisplayString(), []);

    static Ctor LengthCtor(ArrayTypeSymbol array, int length) =>
        new(CtorKind.Length, $"len:{length}", "", [..Enumerable.Repeat(array.ElementType, length)]) { Length = length };

    /// <summary>Stands for every array of this length or longer.</summary>
    static Ctor LengthAtLeastCtor(ArrayTypeSymbol array, int length) =>
        new(CtorKind.LengthAtLeast, $"len>={length}", "", [..Enumerable.Repeat(array.ElementType, length)]) {
            Length = length
        };

    /// <summary>
    ///     Every constructor of a type, or null when there are too many to list
    ///     and only a wildcard covers it.
    /// </summary>
    List<Ctor>? Signature(TypeSymbol type, IReadOnlyList<Pat> column) {
        switch (type) {
            case NamedTypeSymbol named when named.Equals(core.Bool):
                return [BoolCtor(false), BoolCtor(true)];

            case OptionalTypeSymbol optional:
                return [NoneCtor(), SomeCtor(optional)];

            case TupleTypeSymbol tuple:
                return [TupleCtor(tuple)];

            case StructuralUnionTypeSymbol union:
                return [..union.Cases.Select(TypeCaseCtor)];

            case NamedTypeSymbol { TypeKind: TypeKind.Union } named:
                return [..named.Members.OfType<UnionCaseSymbol>().Select(c => UnionCtor(c, named))];

            // A '@flags' enum's values are any set of its cases, which its
            // cases alone do not cover: it splits no more than an integer does.
            case NamedTypeSymbol { TypeKind: TypeKind.Enum, IsFlags: false } named:
                return [..named.Members.OfType<EnumCaseSymbol>().Select(EnumCtor)];

            // An integer named by literals and ranges: its whole domain, cut
            // where each of them begins and ends.
            case NamedTypeSymbol when Domain(type) is (var min, var max)
                                      && column.Any(static p => p is CtorPat { Ctor.Kind: CtorKind.Interval }):
                return Split(min, max, column, min, max);

            // A record has one constructor, so a pattern that names its fields
            // can cover it; without one there is nothing to split on.
            case NamedTypeSymbol named when column.Any(static p => p is CtorPat { Ctor.Kind: CtorKind.Record }):
                return [RecordCtor(named)];

            // Arrays split on length: each length a pattern names exactly, and
            // one more for every array longer than all of them.
            case ArrayTypeSymbol array when column.Any(static p => p is CtorPat { Ctor.Kind: CtorKind.Length } or SlicePat): {
                var longest = column
                    .Select(static p => p switch {
                            CtorPat { Ctor.Kind: CtorKind.Length } fixedLength => fixedLength.Ctor.Length,
                            SlicePat slice => slice.MinLength,
                            _ => 0,
                        }
                    )
                    .DefaultIfEmpty(0)
                    .Max();

                var ctors = Enumerable.Range(0, longest + 1).Select(n => LengthCtor(array, n)).ToList();
                ctors.Add(LengthAtLeastCtor(array, longest + 1));
                return ctors;
            }

            default:
                return null;
        }
    }

    // ---- Usefulness ---------------------------------------------------------

    /// <summary>A value <paramref name="pattern" /> matches and no earlier row does, or null when there is none.</summary>
    public bool IsUseful(IReadOnlyList<Pat> rows, Pat pattern, TypeSymbol type) =>
        Useful([..rows.Select(static r => (IReadOnlyList<Pat>)[r])], [pattern], [type]) is not null;

    /// <summary>
    ///     Up to <paramref name="limit" /> distinct values no row matches, as
    ///     source text. Empty when the rows are exhaustive.
    /// </summary>
    /// <remarks>
    ///     Each witness found is added as a row before looking again, so the
    ///     next one is a value it does not cover either.
    /// </remarks>
    public List<string> Missing(IReadOnlyList<Pat> rows, TypeSymbol type, int limit = 4) {
        var matrix = rows.Select(static r => (IReadOnlyList<Pat>)[r]).ToList();
        var missing = new List<string>();

        while (missing.Count < limit && Useful(matrix, [Wild.Instance], [type]) is { } witness) {
            missing.Add(Display(witness[0]));
            matrix.Add([witness[0]]);
        }

        return missing;
    }

    static List<Pat> ExpandColumn(IEnumerable<IReadOnlyList<Pat>> rows) {
        var column = new List<Pat>();
        foreach (var row in rows) {
            Flatten(row[0], column);
        }

        return column;
    }

    static void Flatten(Pat pattern, List<Pat> into) {
        if (pattern is OrPat or) {
            foreach (var alternative in or.Alternatives) {
                Flatten(alternative, into);
            }
        } else {
            into.Add(pattern);
        }
    }

    /// <summary>
    ///     Maranget's 𝒰 with witnesses: a vector of values that <paramref name="q" />
    ///     matches and no row does, or null.
    /// </summary>
    List<Pat>? Useful(
        IReadOnlyList<IReadOnlyList<Pat>> rows,
        IReadOnlyList<Pat> q,
        IReadOnlyList<TypeSymbol> types
    ) {
        // Out of budget: say "not useful", which reads as exhaustive and not
        // redundant — silence rather than a wrong error.
        if (++_steps > Budget) {
            return null;
        }

        if (q.Count == 0) {
            return rows.Count == 0 ? [] : null;
        }

        var head = q[0];
        var rest = q.Skip(1).ToList();
        var restTypes = types.Skip(1).ToList();

        switch (head) {
            case OrPat or:
                foreach (var alternative in or.Alternatives) {
                    if (Useful(rows, [alternative, ..rest], types) is { } witness) {
                        return witness;
                    }
                }

                return null;

            // An interval the rows cut is the or of its pieces: '3...8' after
            // '0...5' and '6...10' is useful only if one of the pieces is.
            case CtorPat { Ctor: { Kind: CtorKind.Interval } interval } when Domain(types[0]) is (var min, var max): {
                foreach (var piece in Split(interval.Low, interval.High, ExpandColumn(rows), min, max)) {
                    if (UsefulThrough(rows, piece, rest, types) is { } witness) {
                        return witness;
                    }
                }

                return null;
            }

            case CtorPat constructed:
                return UsefulThrough(rows, constructed.Ctor, [..constructed.Args, ..rest], types);

            case SlicePat slice: {
                // A slice is the or of every length it can match.
                var column = ExpandColumn(rows);
                column.Add(slice);
                var signature = Signature(types[0], column)!;

                foreach (var ctor in signature.Where(c => c.Length >= slice.MinLength)) {
                    if (UsefulThrough(rows, ctor, [..Specialize(slice, ctor)!, ..rest], types) is { } witness) {
                        return witness;
                    }
                }

                return null;
            }
        }

        // A wildcard: split on constructors if the rows name all of them,
        // otherwise only the rows that match anything matter.
        var firstColumn = ExpandColumn(rows);
        var named = firstColumn
            .Where(static p => p is CtorPat or SlicePat)
            .ToList();

        var all = named.Count == 0 ? null : Signature(types[0], firstColumn);
        var covered = all is not null
            && all.All(ctor => firstColumn.Any(p => p is SlicePat ? Specialize(p, ctor) is not null : Covers(p, ctor)));

        if (covered) {
            foreach (var ctor in all!) {
                var wilds = Enumerable.Repeat<Pat>(Wild.Instance, ctor.Arity);
                if (UsefulThrough(rows, ctor, [..wilds, ..rest], types) is { } witness) {
                    return witness;
                }
            }

            return null;
        }

        var defaults = rows
            .SelectMany(ExpandRow)
            .Where(static r => r[0] is Wild)
            .Select(static r => (IReadOnlyList<Pat>)r.Skip(1).ToList())
            .ToList();

        if (Useful(defaults, rest, restTypes) is not { } tail) {
            return null;
        }

        // The head of the witness: a constructor nothing names, when the type
        // has a list of them, and otherwise any value at all.
        Pat witnessHead = Wild.Instance;
        if (all is not null && named.Count > 0) {
            var unnamed = all.FirstOrDefault(ctor => !firstColumn.Any(p => Covers(p, ctor)));
            if (unnamed is not null) {
                witnessHead = new CtorPat(unnamed, [..Enumerable.Repeat<Pat>(Wild.Instance, unnamed.Arity)]);
            }
        }

        return [witnessHead, ..tail];
    }

    /// <summary>Usefulness of a vector whose head is <paramref name="ctor" />, its arguments already spread into <paramref name="q" />.</summary>
    List<Pat>? UsefulThrough(
        IReadOnlyList<IReadOnlyList<Pat>> rows,
        Ctor ctor,
        IReadOnlyList<Pat> q,
        IReadOnlyList<TypeSymbol> types
    ) {
        var specialized = new List<IReadOnlyList<Pat>>();
        foreach (var row in rows.SelectMany(ExpandRow)) {
            if (Specialize(row[0], ctor) is { } args) {
                specialized.Add([..args, ..row.Skip(1)]);
            }
        }

        var witness = Useful(specialized, q, [..ctor.ArgTypes, ..types.Skip(1)]);
        if (witness is null) {
            return null;
        }

        var head = new CtorPat(ctor, [..witness.Take(ctor.Arity)]);
        return [head, ..witness.Skip(ctor.Arity)];
    }

    static bool Covers(Pat pattern, Ctor ctor) => pattern is CtorPat constructed && Includes(constructed.Ctor, ctor);

    /// <summary>
    ///     Whether every value <paramref name="ctor" /> builds is one
    ///     <paramref name="pattern" /> matches: the same constructor, or for
    ///     intervals, one inside the other.
    /// </summary>
    static bool Includes(Ctor pattern, Ctor ctor) =>
        pattern.Kind == CtorKind.Interval && ctor.Kind == CtorKind.Interval
            ? pattern.Low <= ctor.Low && ctor.High <= pattern.High
            : pattern.Equals(ctor);

    /// <summary>Rows with an or-pattern at their head, split into one row per alternative.</summary>
    static IEnumerable<IReadOnlyList<Pat>> ExpandRow(IReadOnlyList<Pat> row) {
        if (row[0] is not OrPat) {
            yield return row;
            yield break;
        }

        var heads = new List<Pat>();
        Flatten(row[0], heads);
        foreach (var head in heads) {
            yield return [head, ..row.Skip(1)];
        }
    }

    /// <summary>
    ///     The sub-patterns a pattern gives a constructor's arguments, or null
    ///     when it cannot match a value built by that constructor.
    /// </summary>
    static ImmutableArray<Pat>? Specialize(Pat pattern, Ctor ctor) {
        switch (pattern) {
            case Wild:
                return [..Enumerable.Repeat<Pat>(Wild.Instance, ctor.Arity)];

            case CtorPat constructed when Includes(constructed.Ctor, ctor):
                return constructed.Args;

            case SlicePat slice when ctor.Kind is CtorKind.Length or CtorKind.LengthAtLeast
                                     && ctor.Length >= slice.MinLength: {
                var middle = ctor.Length - slice.MinLength;
                return [..slice.Prefix, ..Enumerable.Repeat<Pat>(Wild.Instance, middle), ..slice.Suffix];
            }

            default:
                return null;
        }
    }

    // ---- Witnesses ----------------------------------------------------------

    static string Display(Pat pattern) {
        if (pattern is not CtorPat { Ctor: var ctor } constructed) {
            return "_";
        }

        var args = constructed.Args.Select(Display).ToList();

        return ctor.Kind switch {
            CtorKind.UnionCase => args.Count == 0 ? $".{ctor.Name}" : $".{ctor.Name}({string.Join(", ", args)})",
            CtorKind.EnumCase => $".{ctor.Name}",
            CtorKind.Some => $".Some({args[0]})",
            CtorKind.Tuple => $"({string.Join(", ", args)})",
            CtorKind.Record => DisplayRecord(ctor, args),
            CtorKind.Length => $"[{string.Join(", ", args)}]",
            CtorKind.LengthAtLeast => $"[{string.Join(", ", [..args, "..."])}]",
            _ => ctor.Name,
        };
    }

    static string DisplayRecord(Ctor ctor, List<string> args) {
        var named = ctor.FieldNames
            .Select((name, i) => (name, value: args[i]))
            .Where(static f => f.value != "_")
            .Select(static f => $"{f.name}: {f.value}")
            .ToList();

        return named.Count == 0 ? $"{ctor.Name} {{ }}" : $"{ctor.Name} {{ {string.Join(", ", named)} }}";
    }
}
