using System.Globalization;
using System.Text;

namespace Sable.Driver.Packages;

/// <summary>A semantic version, <c>major.minor.patch[-pre][+build]</c>, ordered as semver 2.0 orders them.</summary>
public sealed class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion> {
    public SemVersion(long major, long minor, long patch, IReadOnlyList<string>? prerelease = null, string build = "") {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease ?? [];
        Build = build;
    }

    public long Major { get; }
    public long Minor { get; }
    public long Patch { get; }

    /// <summary>The dot-separated pre-release identifiers; empty for a release.</summary>
    public IReadOnlyList<string> Prerelease { get; }

    /// <summary>Build metadata: kept, but ignored by ordering and equality.</summary>
    public string Build { get; }

    public bool IsPrerelease => Prerelease.Count > 0;

    /// <summary>The lowest version there is with these numbers: <c>x.y.z-0</c>, below every pre-release of it.</summary>
    public SemVersion FirstPrerelease => new(Major, Minor, Patch, ["0"]);

    public SemVersion NextMajor => new(Major + 1, 0, 0);
    public SemVersion NextMinor => new(Major, Minor + 1, 0);
    public SemVersion NextPatch => new(Major, Minor, Patch + 1);

    /// <summary>The release without pre-release or build parts.</summary>
    public SemVersion Release => new(Major, Minor, Patch);

    public static SemVersion Parse(string text) =>
        TryParse(text, out var version, out var error) ? version : throw new FormatException(error);

    public static bool TryParse(string text, out SemVersion version, out string error) {
        version = null!;
        var build = "";
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0) {
            build = text[(plus + 1)..];
            text = text[..plus];
            if (build.Length == 0 || build.Split('.').Any(static p => p.Length == 0 || !p.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-'))) {
                error = $"'{build}' is not valid build metadata";
                return false;
            }
        }

        string[] pre = [];
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0) {
            pre = text[(dash + 1)..].Split('.');
            text = text[..dash];
            foreach (var identifier in pre) {
                if (identifier.Length == 0 || !identifier.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-')) {
                    error = $"'{identifier}' is not a valid pre-release identifier";
                    return false;
                }

                if (identifier.All(char.IsAsciiDigit) && identifier.Length > 1 && identifier[0] == '0') {
                    error = $"the pre-release identifier '{identifier}' has a leading zero";
                    return false;
                }
            }
        }

        var parts = text.Split('.');
        if (parts.Length != 3) {
            error = $"'{text}' is not a version: it needs major.minor.patch";
            return false;
        }

        var numbers = new long[3];
        for (var i = 0; i < 3; i++) {
            if (!TryNumber(parts[i], out numbers[i])) {
                error = $"'{parts[i]}' in '{text}' is not a version number";
                return false;
            }
        }

        version = new(numbers[0], numbers[1], numbers[2], pre, build);
        error = "";
        return true;
    }

    internal static bool TryNumber(string part, out long number) {
        number = 0;
        return part.Length > 0 && part.All(char.IsAsciiDigit) && (part.Length == 1 || part[0] != '0')
            && long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    public int CompareTo(SemVersion? other) {
        if (other is null) {
            return 1;
        }

        var c = Major.CompareTo(other.Major);
        if (c == 0) {
            c = Minor.CompareTo(other.Minor);
        }

        if (c == 0) {
            c = Patch.CompareTo(other.Patch);
        }

        if (c != 0) {
            return c;
        }

        // A release is above every pre-release of it.
        if (!IsPrerelease || !other.IsPrerelease) {
            return other.IsPrerelease.CompareTo(IsPrerelease);
        }

        for (var i = 0; i < Math.Min(Prerelease.Count, other.Prerelease.Count); i++) {
            var a = Prerelease[i];
            var b = other.Prerelease[i];
            // Without leading zeros, the longer number is the larger, however long.
            var aNumeric = a.All(char.IsAsciiDigit);
            var bNumeric = b.All(char.IsAsciiDigit);
            c = (aNumeric, bNumeric) switch {
                (true, true) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a, b),
            };

            if (c != 0) {
                return Math.Sign(c);
            }
        }

        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    public bool Equals(SemVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', Prerelease));

    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVersion a, SemVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVersion a, SemVersion b) => a.CompareTo(b) >= 0;
    public static bool operator ==(SemVersion? a, SemVersion? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(SemVersion? a, SemVersion? b) => !(a == b);

    public override string ToString() {
        var builder = new StringBuilder($"{Major}.{Minor}.{Patch}");
        if (IsPrerelease) {
            builder.Append('-').AppendJoin('.', Prerelease);
        }

        if (Build.Length > 0) {
            builder.Append('+').Append(Build);
        }

        return builder.ToString();
    }
}

/// <summary>An interval of versions: either end may be open (null) and either may include its bound.</summary>
public readonly record struct VersionInterval(SemVersion? Lower, bool LowerInclusive, SemVersion? Upper, bool UpperInclusive) {
    public bool Contains(SemVersion v) =>
        (Lower is null || (LowerInclusive ? v >= Lower : v > Lower))
        && (Upper is null || (UpperInclusive ? v <= Upper : v < Upper));

    public bool IsEmpty =>
        Lower is not null && Upper is not null && (Lower > Upper || (Lower == Upper && !(LowerInclusive && UpperInclusive)));
}

/// <summary>
///     A set of versions as a union of disjoint intervals, closed under
///     union, intersection and complement — the algebra PubGrub needs.
/// </summary>
public sealed class VersionSet : IEquatable<VersionSet> {
    readonly IReadOnlyList<VersionInterval> _intervals;

    VersionSet(IReadOnlyList<VersionInterval> intervals) {
        _intervals = intervals;
    }

    public static readonly VersionSet Any = new([new(null, false, null, false)]);
    public static readonly VersionSet None = new([]);

    public IReadOnlyList<VersionInterval> Intervals => _intervals;

    public static VersionSet Exactly(SemVersion v) => new([new(v, true, v, true)]);

    public static VersionSet Between(SemVersion? lower, bool lowerInclusive, SemVersion? upper, bool upperInclusive) =>
        Normalise([new(lower, lowerInclusive, upper, upperInclusive)]);

    public static VersionSet AtLeast(SemVersion v) => Between(v, true, null, false);

    public static VersionSet Below(SemVersion v) => Between(null, false, v, false);

    public bool IsEmpty => _intervals.Count == 0;

    public bool IsAny => _intervals is [{ Lower: null, Upper: null }];

    /// <summary>The single version this set holds, if it holds exactly one.</summary>
    public SemVersion? Single => _intervals is [{ Lower: { } l, Upper: { } u, LowerInclusive: true, UpperInclusive: true }] && l == u ? l : null;

    public bool Contains(SemVersion v) => _intervals.Any(i => i.Contains(v));

    public VersionSet Union(VersionSet other) => Normalise([.._intervals, ..other._intervals]);

    public VersionSet Complement() {
        var result = new List<VersionInterval>();
        SemVersion? lower = null;
        var lowerInclusive = false;
        foreach (var interval in _intervals) {
            if (interval.Lower is not null) {
                result.Add(new(lower, lowerInclusive, interval.Lower, !interval.LowerInclusive));
            }

            if (interval.Upper is null) {
                return Normalise(result);
            }

            lower = interval.Upper;
            lowerInclusive = !interval.UpperInclusive;
        }

        result.Add(new(lower, lowerInclusive, null, false));
        return Normalise(result);
    }

    public VersionSet Intersect(VersionSet other) => Complement().Union(other.Complement()).Complement();

    public VersionSet Difference(VersionSet other) => Intersect(other.Complement());

    public bool IsSubsetOf(VersionSet other) => Difference(other).IsEmpty;

    public bool IsDisjointFrom(VersionSet other) => Intersect(other).IsEmpty;

    static VersionSet Normalise(IEnumerable<VersionInterval> intervals) {
        var sorted = intervals.Where(static i => !i.IsEmpty).OrderBy(static i => i, LowerComparer.Instance).ToList();
        var merged = new List<VersionInterval>();
        foreach (var interval in sorted) {
            if (merged.Count > 0 && Touches(merged[^1], interval)) {
                var last = merged[^1];
                var (upper, upperInclusive) = MaxUpper(last, interval);
                merged[^1] = last with { Upper = upper, UpperInclusive = upperInclusive };
            } else {
                merged.Add(interval);
            }
        }

        return new(merged);
    }

    /// <summary>Whether <paramref name="b" />, starting no earlier than <paramref name="a" />, overlaps or abuts it.</summary>
    static bool Touches(VersionInterval a, VersionInterval b) {
        if (a.Upper is null || b.Lower is null) {
            return true;
        }

        var c = b.Lower.CompareTo(a.Upper);
        return c < 0 || (c == 0 && (a.UpperInclusive || b.LowerInclusive));
    }

    static (SemVersion?, bool) MaxUpper(VersionInterval a, VersionInterval b) {
        if (a.Upper is null || b.Upper is null) {
            return (null, false);
        }

        var c = a.Upper.CompareTo(b.Upper);
        return c > 0 ? (a.Upper, a.UpperInclusive) : c < 0 ? (b.Upper, b.UpperInclusive) : (a.Upper, a.UpperInclusive || b.UpperInclusive);
    }

    sealed class LowerComparer : IComparer<VersionInterval> {
        public static readonly LowerComparer Instance = new();

        public int Compare(VersionInterval x, VersionInterval y) {
            if (x.Lower is null || y.Lower is null) {
                return (x.Lower is null ? 0 : 1) - (y.Lower is null ? 0 : 1);
            }

            var c = x.Lower.CompareTo(y.Lower);
            return c != 0 ? c : y.LowerInclusive.CompareTo(x.LowerInclusive);
        }
    }

    public bool Equals(VersionSet? other) =>
        other is not null && other._intervals.Count == _intervals.Count && _intervals.Zip(other._intervals).All(static p =>
            p.First.Lower == p.Second.Lower && p.First.Upper == p.Second.Upper
            && p.First.LowerInclusive == p.Second.LowerInclusive && p.First.UpperInclusive == p.Second.UpperInclusive);

    public override bool Equals(object? obj) => obj is VersionSet other && Equals(other);

    public override int GetHashCode() => _intervals.Count;

    /// <summary>Written the way a requirement would be: <c>^1.2.0</c>, <c>1.4.0</c>, <c>&gt;=1.0.0, &lt;1.5.0</c>.</summary>
    public override string ToString() {
        if (IsEmpty) {
            return "no version";
        }

        if (IsAny) {
            return "any version";
        }

        return string.Join(" or ", _intervals.Select(Describe));
    }

    static string Describe(VersionInterval i) {
        if (i is { Lower: { } l, Upper: { } u } && l == u) {
            return l.ToString();
        }

        // The upper bound x.y.z-0 is 'below x.y.z and its pre-releases': written as <x.y.z.
        static string Upper(SemVersion u) => u.Prerelease is ["0"] ? u.Release.ToString() : u.ToString();

        if (i is { Lower: { } low, LowerInclusive: true, Upper: { } high, UpperInclusive: false }) {
            var top = high.Prerelease is ["0"] ? high.Release : high;
            if (!high.IsPrerelease || high.Prerelease is ["0"]) {
                if (low.Major > 0 && top == low.Release.NextMajor) {
                    return $"^{low}";
                }

                if (low.Major == 0 && low.Minor > 0 && top == low.Release.NextMinor) {
                    return $"^{low}";
                }

                if (low.Major == 0 && low.Minor == 0 && top == low.Release.NextPatch) {
                    return $"^{low}";
                }

                if (top == low.Release.NextMinor) {
                    return $"~{low}";
                }
            }
        }

        var parts = new List<string>();
        if (i.Lower is { } lower) {
            parts.Add($"{(i.LowerInclusive ? ">=" : ">")}{lower}");
        }

        if (i.Upper is { } upper) {
            parts.Add($"{(i.UpperInclusive ? "<=" : "<")}{Upper(upper)}");
        }

        return string.Join(", ", parts);
    }
}

/// <summary>
///     A version requirement, Cargo's syntax: <c>1.2</c> (meaning <c>^1.2</c>),
///     <c>^1.2.3</c>, <c>~1.2</c>, <c>=1.2.3</c>, <c>&gt;=1.0</c>, <c>&lt;2</c>,
///     <c>1.*</c>, <c>*</c>, comma-joined for all of them at once. A
///     requirement's upper bound excludes the pre-releases of the bound, and
///     a pre-release is only chosen when a requirement names one.
/// </summary>
public sealed class VersionReq {
    VersionReq(string text, VersionSet set, bool mentionsPrerelease) {
        Text = text;
        Set = set;
        MentionsPrerelease = mentionsPrerelease;
    }

    public string Text { get; }
    public VersionSet Set { get; }

    /// <summary>A comparator names a pre-release, so pre-releases may be chosen.</summary>
    public bool MentionsPrerelease { get; }

    public static readonly VersionReq Any = new("*", VersionSet.Any, false);

    public bool Matches(SemVersion v) => Set.Contains(v) && (!v.IsPrerelease || MentionsPrerelease);

    public override string ToString() => Text;

    public static VersionReq Exactly(SemVersion v) => new($"={v}", VersionSet.Exactly(v), v.IsPrerelease);

    public static VersionReq Parse(string text) =>
        TryParse(text, out var req, out var error) ? req : throw new FormatException(error);

    public static bool TryParse(string text, out VersionReq requirement, out string error) {
        requirement = null!;
        var set = VersionSet.Any;
        var prerelease = false;
        var comparators = text.Split(',');
        foreach (var raw in comparators) {
            var comparator = raw.Trim();
            if (comparator.Length == 0) {
                error = text.Trim().Length == 0 ? "a version requirement cannot be empty" : $"'{text}' has an empty comparator";
                return false;
            }

            if (!TryComparator(comparator, out var part, out var mentions, out error)) {
                return false;
            }

            set = set.Intersect(part);
            prerelease |= mentions;
        }

        requirement = new(text.Trim(), set, prerelease);
        error = "";
        return true;
    }

    static bool TryComparator(string text, out VersionSet set, out bool prerelease, out string error) {
        set = VersionSet.None;
        prerelease = false;
        var op = text switch {
            ['>', '=', ..] => ">=",
            ['<', '=', ..] => "<=",
            ['>', ..] => ">",
            ['<', ..] => "<",
            ['=', ..] => "=",
            ['^', ..] => "^",
            ['~', ..] => "~",
            _ => "",
        };

        var rest = text[op.Length..].Trim();
        if (rest is "*" && op.Length == 0) {
            set = VersionSet.Any;
            error = "";
            return true;
        }

        // A partial version: major[.minor[.patch[-pre]]], with '*' or 'x' for what is left out.
        // Build metadata goes first: a '-' in it ('1.0.0+b-2') starts no pre-release.
        string[] pre = [];
        var plus = rest.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0) {
            // Ignored, as a version's is in comparing; but checked, so that junk after a '+' is not taken.
            var build = rest[(plus + 1)..];
            if (build.Split('.').Any(static p => p.Length == 0 || !p.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-'))) {
                error = $"'{text}' is not a version requirement: '{build}' is not valid build metadata";
                return false;
            }

            rest = rest[..plus];
        }

        var dash = rest.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0) {
            pre = rest[(dash + 1)..].Split('.');
            rest = rest[..dash];
        }

        var parts = rest.Split('.');
        if (parts.Length is 0 or > 3) {
            error = $"'{text}' is not a version requirement";
            return false;
        }

        var numbers = new List<long>();
        foreach (var part in parts) {
            if (part is "*" or "x" or "X") {
                break;
            }

            if (!SemVersion.TryNumber(part, out var n)) {
                error = $"'{text}' is not a version requirement: '{part}' is not a number";
                return false;
            }

            numbers.Add(n);
        }

        if (numbers.Count < parts.Length && parts.Skip(numbers.Count).Any(static p => p is not ("*" or "x" or "X"))) {
            error = $"'{text}' is not a version requirement: nothing may follow a wildcard";
            return false;
        }

        if (pre.Length > 0 && numbers.Count < 3) {
            error = $"'{text}' is not a version requirement: a pre-release needs major.minor.patch";
            return false;
        }

        if (numbers.Count == 0) {
            if (op.Length > 0) {
                error = $"'{text}' is not a version requirement";
                return false;
            }

            set = VersionSet.Any;
            error = "";
            return true;
        }

        var wildcard = numbers.Count < parts.Length;
        if (wildcard && op.Length > 0) {
            error = $"'{text}' is not a version requirement: a wildcard cannot follow '{op}'";
            return false;
        }

        prerelease = pre.Length > 0;
        if (prerelease && !SemVersion.TryParse($"{numbers[0]}.{numbers[1]}.{numbers[2]}-{string.Join('.', pre)}", out _, out error)) {
            return false;
        }

        var major = numbers[0];
        var minor = numbers.Count > 1 ? numbers[1] : 0;
        var patch = numbers.Count > 2 ? numbers[2] : 0;
        var low = new SemVersion(major, minor, patch, pre);

        // The first version past what a partial version names.
        SemVersion PastPartial() => checked(numbers.Count switch {
            1 => new SemVersion(major + 1, 0, 0),
            2 => new SemVersion(major, minor + 1, 0),
            _ => new SemVersion(major, minor, patch + 1),
        });

        static VersionSet Range(SemVersion low, SemVersion high) => VersionSet.Between(low, true, high.FirstPrerelease, false);

        // A range's end is the next major, minor or patch; past the largest
        // number there is none, and wrapping round to a negative one would
        // make '^9223372036854775807' match nothing, silently.
        try {
            set = wildcard ? Range(low, PastPartial()) : checked(op switch {
                "" or "^" => Range(low, numbers.Count switch {
                    _ when major > 0 => new SemVersion(major + 1, 0, 0),
                    1 => new SemVersion(1, 0, 0),
                    2 when minor > 0 => new SemVersion(0, minor + 1, 0),
                    2 => new SemVersion(0, 1, 0),
                    _ when minor > 0 => new SemVersion(0, minor + 1, 0),
                    _ => new SemVersion(0, 0, patch + 1),
                }),
                "~" => Range(low, numbers.Count == 1 ? new SemVersion(major + 1, 0, 0) : new SemVersion(major, minor + 1, 0)),
                "=" => numbers.Count == 3 ? VersionSet.Exactly(low) : Range(low, PastPartial()),
                ">=" => VersionSet.AtLeast(low),
                ">" => numbers.Count == 3 ? VersionSet.Between(low, false, null, false) : VersionSet.AtLeast(PastPartial()),
                "<" => VersionSet.Below(numbers.Count == 3 && prerelease ? low : low.FirstPrerelease),
                "<=" => numbers.Count == 3 ? VersionSet.Between(null, false, low, true) : VersionSet.Below(PastPartial().FirstPrerelease),
                _ => VersionSet.None,
            });
        } catch (OverflowException) {
            error = $"'{text}' is not a version requirement: its numbers are too large to have a next version";
            return false;
        }

        error = "";
        return true;
    }
}
