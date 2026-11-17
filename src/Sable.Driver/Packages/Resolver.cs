using System.Text;

namespace Sable.Driver.Packages;

/// <summary>Where a resolver learns what versions a package has and what each depends on.</summary>
public interface IPackageSource {
    /// <summary>The versions of <paramref name="package" /> there are to choose from; empty when there is no such package.</summary>
    IReadOnlyList<SemVersion> Versions(string package);

    /// <summary>What <paramref name="package" /> at <paramref name="version" /> depends on.</summary>
    IReadOnlyList<(string Package, VersionReq Requirement)> Dependencies(string package, SemVersion version);

    /// <summary>Whether <paramref name="package" /> exists at all (it may have no usable version).</summary>
    bool Exists(string package) => Versions(package).Count > 0;

    /// <summary>How to name a package in an explanation.</summary>
    string Display(string package) => package;
}

/// <summary>A statement about a package: it is (or is not) selected at a version in the set.</summary>
public sealed record Term(string Package, VersionSet Set, bool Positive) {
    public Term Inverse => this with { Positive = !Positive };

    /// <summary>Both at once: the versions that satisfy both terms.</summary>
    public Term Intersect(Term other) => (Positive, other.Positive) switch {
        (true, true) => this with { Set = Set.Intersect(other.Set) },
        (true, false) => this with { Set = Set.Difference(other.Set) },
        (false, true) => other with { Set = other.Set.Difference(Set) },
        _ => this with { Set = Set.Union(other.Set) },
    };

    /// <summary>This, but not <paramref name="other" />; null when nothing is left.</summary>
    public Term? Difference(Term other) {
        var result = Intersect(other.Inverse);
        return result.Positive && result.Set.IsEmpty ? null : result;
    }

    /// <summary>How this term (what is known) relates to <paramref name="other" /> (what an incompatibility says).</summary>
    public SetRelation Relation(Term other) {
        if (other.Positive) {
            if (Positive) {
                return Set.IsDisjointFrom(other.Set) ? SetRelation.Disjoint : Set.IsSubsetOf(other.Set) ? SetRelation.Subset : SetRelation.Overlapping;
            }

            return other.Set.IsSubsetOf(Set) ? SetRelation.Disjoint : SetRelation.Overlapping;
        }

        if (Positive) {
            return Set.IsDisjointFrom(other.Set) ? SetRelation.Subset : Set.IsSubsetOf(other.Set) ? SetRelation.Disjoint : SetRelation.Overlapping;
        }

        return other.Set.IsSubsetOf(Set) ? SetRelation.Subset : SetRelation.Overlapping;
    }

    public bool Satisfies(Term other) => Relation(other) == SetRelation.Subset;
}

public enum SetRelation { Subset, Disjoint, Overlapping }

public enum IncompatibilityKind { Root, Dependency, NoVersions, UnknownPackage, Conflict }

/// <summary>A set of terms that cannot all be true, and why.</summary>
public sealed class Incompatibility {
    public Incompatibility(IEnumerable<Term> terms, IncompatibilityKind kind, Incompatibility? conflict = null, Incompatibility? other = null, string? root = null) {
        var list = terms.ToList();

        // The root is always selected, so a derived incompatibility need not say so.
        if (kind == IncompatibilityKind.Conflict && list.Count != 1 && root is not null && list.Any(t => t.Positive && t.Package == root)) {
            list = [..list.Where(t => !(t.Positive && t.Package == root))];
        }

        // One term per package.
        var merged = new List<Term>();
        foreach (var term in list) {
            var index = merged.FindIndex(t => t.Package == term.Package);
            if (index < 0) {
                merged.Add(term);
            } else {
                merged[index] = merged[index].Intersect(term);
            }
        }

        Terms = merged;
        Kind = kind;
        Cause = conflict;
        OtherCause = other;
    }

    public IReadOnlyList<Term> Terms { get; }
    public IncompatibilityKind Kind { get; }

    /// <summary>For a derived incompatibility: the two it was derived from.</summary>
    public Incompatibility? Cause { get; }
    public Incompatibility? OtherCause { get; }

    public bool IsDerived => Kind == IncompatibilityKind.Conflict;
}

/// <summary>Resolution failed: <see cref="Exception.Message" /> explains why, step by step.</summary>
public sealed class ResolutionException(string message, Incompatibility? incompatibility = null) : Exception(message) {
    public Incompatibility? Incompatibility { get; } = incompatibility;
}

/// <summary>
///     PubGrub (docs/05 §5.3): version solving by unit propagation and
///     conflict-driven clause learning over incompatibilities, after Natalie
///     Weizenbaum's design for pub. When no solution exists, the learned
///     incompatibilities form a derivation tree, written out as the
///     explanation of why.
/// </summary>
public sealed class Resolver {
    readonly IPackageSource _source;
    readonly string _root;
    readonly SemVersion _rootVersion;
    readonly IReadOnlyDictionary<string, SemVersion> _preferred;
    readonly Dictionary<string, List<Incompatibility>> _incompatibilities = new(StringComparer.Ordinal);
    readonly Dictionary<(string, SemVersion), IReadOnlyList<(string Package, VersionReq Requirement)>> _dependencies = [];
    readonly HashSet<string> _prereleasesAllowed = new(StringComparer.Ordinal);
    readonly PartialSolution _solution = new();

    Resolver(IPackageSource source, string root, SemVersion rootVersion, IReadOnlyDictionary<string, SemVersion>? preferred) {
        _source = source;
        _root = root;
        _rootVersion = rootVersion;
        _preferred = preferred ?? new Dictionary<string, SemVersion>();
    }

    /// <summary>
    ///     Chooses a version of every package <paramref name="root" /> needs.
    ///     A version in <paramref name="preferred" /> (the lock file's) is
    ///     chosen when it still fits.
    /// </summary>
    /// <exception cref="ResolutionException">No choice satisfies every requirement; the message says why.</exception>
    public static IReadOnlyDictionary<string, SemVersion> Resolve(
        IPackageSource source,
        string root,
        SemVersion rootVersion,
        IReadOnlyDictionary<string, SemVersion>? preferred = null) =>
        new Resolver(source, root, rootVersion, preferred).Solve();

    IReadOnlyDictionary<string, SemVersion> Solve() {
        AddIncompatibility(new([new(_root, VersionSet.Exactly(_rootVersion), false)], IncompatibilityKind.Root));
        string? next = _root;
        while (next is not null) {
            Propagate(next);
            next = Choose();
        }

        return _solution.Decisions;
    }

    void AddIncompatibility(Incompatibility incompatibility) {
        foreach (var term in incompatibility.Terms) {
            if (!_incompatibilities.TryGetValue(term.Package, out var list)) {
                _incompatibilities[term.Package] = list = [];
            }

            list.Add(incompatibility);
        }
    }

    void Propagate(string package) {
        var changed = new Stack<string>();
        changed.Push(package);
        while (changed.TryPop(out var current)) {
            if (!_incompatibilities.TryGetValue(current, out var list)) {
                continue;
            }

            for (var i = list.Count - 1; i >= 0; i--) {
                var incompatibility = list[i];
                var result = PropagateOne(incompatibility, out var derived);
                if (result == Propagation.Conflict) {
                    var root = ResolveConflict(incompatibility);
                    changed.Clear();
                    if (PropagateOne(root, out var fromRoot) == Propagation.Derived) {
                        changed.Push(fromRoot!);
                    }

                    break;
                }

                if (result == Propagation.Derived) {
                    changed.Push(derived!);
                }
            }
        }
    }

    enum Propagation { None, Derived, Conflict }

    Propagation PropagateOne(Incompatibility incompatibility, out string? derived) {
        derived = null;
        Term? unsatisfied = null;
        foreach (var term in incompatibility.Terms) {
            var relation = _solution.Relation(term);
            if (relation == SetRelation.Disjoint) {
                return Propagation.None;
            }

            if (relation == SetRelation.Overlapping) {
                if (unsatisfied is not null) {
                    return Propagation.None;
                }

                unsatisfied = term;
            }
        }

        if (unsatisfied is null) {
            return Propagation.Conflict;
        }

        _solution.Derive(unsatisfied.Inverse, incompatibility);
        derived = unsatisfied.Package;
        return Propagation.Derived;
    }

    bool IsFailure(Incompatibility incompatibility) =>
        incompatibility.Terms.Count == 0 || (incompatibility.Terms is [{ Positive: true } only] && only.Package == _root);

    Incompatibility ResolveConflict(Incompatibility incompatibility) {
        var created = false;
        while (!IsFailure(incompatibility)) {
            Term? mostRecentTerm = null;
            Assignment? mostRecentSatisfier = null;
            Term? difference = null;
            var previousSatisfierLevel = 1;

            foreach (var term in incompatibility.Terms) {
                var satisfier = _solution.Satisfier(term);
                if (mostRecentSatisfier is null) {
                    mostRecentTerm = term;
                    mostRecentSatisfier = satisfier;
                } else if (mostRecentSatisfier.Index < satisfier.Index) {
                    previousSatisfierLevel = Math.Max(previousSatisfierLevel, mostRecentSatisfier.DecisionLevel);
                    mostRecentTerm = term;
                    mostRecentSatisfier = satisfier;
                    difference = null;
                } else {
                    previousSatisfierLevel = Math.Max(previousSatisfierLevel, satisfier.DecisionLevel);
                }

                if (ReferenceEquals(mostRecentTerm, term)) {
                    // The satisfier may say more than the term needs; what
                    // it says beyond is itself satisfied by something earlier.
                    difference = mostRecentSatisfier.Term.Difference(mostRecentTerm);
                    if (difference is not null) {
                        previousSatisfierLevel = Math.Max(previousSatisfierLevel, _solution.Satisfier(difference.Inverse).DecisionLevel);
                    }
                }
            }

            if (previousSatisfierLevel < mostRecentSatisfier!.DecisionLevel || mostRecentSatisfier.Cause is null) {
                _solution.Backtrack(previousSatisfierLevel);
                if (created) {
                    AddIncompatibility(incompatibility);
                }

                return incompatibility;
            }

            var terms = incompatibility.Terms.Where(t => !ReferenceEquals(t, mostRecentTerm))
                .Concat(mostRecentSatisfier.Cause.Terms.Where(t => t.Package != mostRecentSatisfier.Term.Package))
                .ToList();
            if (difference is not null) {
                terms.Add(difference.Inverse);
            }

            incompatibility = new(terms, IncompatibilityKind.Conflict, incompatibility, mostRecentSatisfier.Cause, _root);
            created = true;
        }

        throw new ResolutionException(Explain(incompatibility), incompatibility);
    }

    /// <summary>The next package to decide, deciding it; null when every package is decided.</summary>
    string? Choose() {
        var undecided = _solution.Undecided().ToList();
        if (undecided.Count == 0) {
            return null;
        }

        // The package with the fewest versions left: its conflicts show soonest.
        var (package, set, candidates) = undecided
            .Select(t => (t.Package, t.Set, Candidates: Candidates(t.Package, t.Set)))
            .OrderBy(static c => c.Candidates.Count)
            .ThenBy(static c => c.Package, StringComparer.Ordinal)
            .First();

        if (candidates.Count == 0) {
            AddIncompatibility(new(
                [new(package, set, true)],
                _source.Exists(package) ? IncompatibilityKind.NoVersions : IncompatibilityKind.UnknownPackage));
            return package;
        }

        var version = _preferred.TryGetValue(package, out var preferred) && candidates.Contains(preferred) ? preferred : candidates[0];
        var conflict = false;
        foreach (var incompatibility in DependencyIncompatibilities(package, version)) {
            AddIncompatibility(incompatibility);
            conflict |= incompatibility.Terms.All(t => t.Package == package || _solution.Satisfies(t));
        }

        if (!conflict) {
            _solution.Decide(package, version);
        }

        return package;
    }

    /// <summary>The versions of a package in a set that may be chosen, best first: newest release, then newest pre-release if allowed.</summary>
    List<SemVersion> Candidates(string package, VersionSet set) {
        if (package == _root) {
            return set.Contains(_rootVersion) ? [_rootVersion] : [];
        }

        var all = _source.Versions(package).Where(set.Contains).ToList();
        var allowPrerelease = _prereleasesAllowed.Contains(package);
        return [
            ..all.Where(v => !v.IsPrerelease).OrderDescending(),
            ..all.Where(v => v.IsPrerelease && (allowPrerelease || _preferred.GetValueOrDefault(package) == v)).OrderDescending(),
        ];
    }

    IReadOnlyList<(string Package, VersionReq Requirement)> DependenciesOf(string package, SemVersion version) {
        if (!_dependencies.TryGetValue((package, version), out var dependencies)) {
            _dependencies[(package, version)] = dependencies = _source.Dependencies(package, version);
        }

        return dependencies;
    }

    /// <summary>
    ///     One incompatibility per dependency, each covering the run of
    ///     neighbouring versions that depend on the same thing in the same way,
    ///     so an explanation can say "image 0.6.x depends on …".
    /// </summary>
    IEnumerable<Incompatibility> DependencyIncompatibilities(string package, SemVersion version) {
        var all = package == _root ? [_rootVersion] : _source.Versions(package).Order().ToList();
        var index = all.IndexOf(version);
        foreach (var (dependency, requirement) in DependenciesOf(package, version)) {
            if (requirement.MentionsPrerelease) {
                _prereleasesAllowed.Add(dependency);
            }

            bool Same(SemVersion other) => DependenciesOf(package, other).Any(d => d.Package == dependency && d.Requirement.Text == requirement.Text);

            var low = index;
            while (low > 0 && Same(all[low - 1])) {
                low--;
            }

            var high = index;
            while (high < all.Count - 1 && Same(all[high + 1])) {
                high++;
            }

            var range = package == _root
                ? VersionSet.Exactly(version)
                : VersionSet.Between(low == 0 ? null : all[low], low != 0, high == all.Count - 1 ? null : all[high + 1], false);
            yield return new([new(package, range, true), new(dependency, requirement.Set, false)], IncompatibilityKind.Dependency);
        }
    }

    // ---- The explanation of a failure ---------------------------------------------------------------

    string Explain(Incompatibility failure) {
        var writer = new FailureWriter(this, failure);
        return writer.Write();
    }

    string Display(string package) => _source.Display(package);

    string Terse(Term term) =>
        term.Package == _root ? Display(term.Package)
        : term.Set.IsAny ? Display(term.Package)
        : $"{Display(term.Package)} {term.Set}";

    string Describe(Incompatibility incompatibility) {
        var terms = incompatibility.Terms;
        switch (incompatibility.Kind) {
            case IncompatibilityKind.Dependency:
                return $"{Terse(terms[0])} depends on {Terse(terms[1])}";
            case IncompatibilityKind.NoVersions:
                return terms[0].Set.IsAny ? $"no versions of {Display(terms[0].Package)} are available" : $"no versions of {Display(terms[0].Package)} match {terms[0].Set}";
            case IncompatibilityKind.UnknownPackage:
                return $"{Display(terms[0].Package)} doesn't exist";
            case IncompatibilityKind.Root:
                return $"{Display(_root)} is the root";
        }

        if (IsFailure(incompatibility)) {
            return "resolution failed";
        }

        if (terms is [var single]) {
            return single.Positive ? $"{Terse(single)} is forbidden" : $"{Terse(single)} is required";
        }

        var positive = terms.Where(static t => t.Positive).Select(Terse).ToList();
        var negative = terms.Where(static t => !t.Positive).Select(Terse).ToList();
        if (positive.Count > 0 && negative.Count > 0) {
            return positive.Count == 1
                ? $"{positive[0]} requires {string.Join(" or ", negative)}"
                : $"if {string.Join(" and ", positive)} then {string.Join(" or ", negative)}";
        }

        if (positive.Count == 2) {
            return $"{positive[0]} is incompatible with {positive[1]}";
        }

        return positive.Count > 0
            ? $"one of {string.Join(" or ", positive)} must be false"
            : $"one of {string.Join(" or ", negative)} must be true";
    }

    string AndToString(Incompatibility a, Incompatibility b, int? aLine, int? bLine) =>
        TryRequiresBoth(a, b, aLine, bLine)
        ?? TryRequiresThrough(a, b, aLine, bLine)
        ?? TryRequiresForbidden(a, b, aLine, bLine)
        ?? $"{Describe(a)}{Line(aLine)} and {Describe(b)}{Line(bLine)}";

    static string Line(int? line) => line is { } n ? $" ({n})" : "";

    string? TryRequiresBoth(Incompatibility a, Incompatibility b, int? aLine, int? bLine) {
        if (a.Terms.Count == 1 || b.Terms.Count == 1) {
            return null;
        }

        if (a.Terms.SingleOrDefault(static t => t.Positive) is not { } aPositive
            || b.Terms.SingleOrDefault(static t => t.Positive) is not { } bPositive
            || aPositive.Package != bPositive.Package
            || !aPositive.Set.Equals(bPositive.Set)) {
            return null;
        }

        var verb = a.Kind == IncompatibilityKind.Dependency && b.Kind == IncompatibilityKind.Dependency ? "depends on" : "requires";
        var aNegative = string.Join(" or ", a.Terms.Where(static t => !t.Positive).Select(Terse));
        var bNegative = string.Join(" or ", b.Terms.Where(static t => !t.Positive).Select(Terse));
        return $"{Terse(aPositive)} {verb} both {aNegative}{Line(aLine)} and {bNegative}{Line(bLine)}";
    }

    string? TryRequiresThrough(Incompatibility a, Incompatibility b, int? aLine, int? bLine) {
        if (a.Terms.Count == 1 || b.Terms.Count == 1) {
            return null;
        }

        foreach (var (prior, latter, priorLine, latterLine) in new[] { (a, b, aLine, bLine), (b, a, bLine, aLine) }) {
            if (prior.Terms.Where(static t => !t.Positive).ToList() is not [var priorNegative]
                || prior.Terms.Where(static t => t.Positive).ToList() is not [var priorPositive]
                || latter.Terms.Where(static t => t.Positive).ToList() is not [var latterPositive]
                || latterPositive.Package != priorNegative.Package
                || !priorNegative.Set.IsSubsetOf(latterPositive.Set)) {
                continue;
            }

            var latterNegative = latter.Terms.Where(static t => !t.Positive).ToList();
            if (latterNegative.Count == 0) {
                continue;
            }

            var priorVerb = prior.Kind == IncompatibilityKind.Dependency ? "depends on" : "requires";
            var latterVerb = latter.Kind == IncompatibilityKind.Dependency ? "depends on" : "requires";
            return $"{Terse(priorPositive)} {priorVerb} {Terse(priorNegative)}{Line(priorLine)} which {latterVerb} {string.Join(" or ", latterNegative.Select(Terse))}{Line(latterLine)}";
        }

        return null;
    }

    string? TryRequiresForbidden(Incompatibility a, Incompatibility b, int? aLine, int? bLine) {
        if (a.Terms.Count != 1 && b.Terms.Count != 1) {
            return null;
        }

        var (prior, latter, priorLine, latterLine) = a.Terms.Count == 1 ? (b, a, bLine, aLine) : (a, b, aLine, bLine);
        if (prior.Terms.Where(static t => !t.Positive).ToList() is not [var priorNegative]
            || prior.Terms.Where(static t => t.Positive).ToList() is not [var priorPositive]
            || latter.Terms[0] is not { Positive: true } latterTerm
            || latterTerm.Package != priorNegative.Package
            || !priorNegative.Set.IsSubsetOf(latterTerm.Set) && !latterTerm.Set.IsSubsetOf(priorNegative.Set)) {
            return null;
        }

        var verb = prior.Kind == IncompatibilityKind.Dependency ? "depends on" : "requires";
        var why = latter.Kind switch {
            IncompatibilityKind.NoVersions => "which doesn't match any versions",
            IncompatibilityKind.UnknownPackage => "which doesn't exist",
            _ => $"which is forbidden{Line(latterLine)}",
        };
        return $"{Terse(priorPositive)} {verb} {Terse(priorNegative)}{Line(priorLine)} {why}";
    }

    /// <summary>Writes a derivation tree as numbered paragraphs, after pub's failure writer.</summary>
    sealed class FailureWriter(Resolver resolver, Incompatibility root) {
        readonly Dictionary<Incompatibility, int> _derivations = new(ReferenceEqualityComparer.Instance);
        readonly List<(string Message, int? Number)> _lines = [];
        readonly Dictionary<Incompatibility, int> _lineNumbers = new(ReferenceEqualityComparer.Instance);

        public string Write() {
            if (root.IsDerived) {
                Count(root);
                Visit(root, true);
            } else {
                Add(root, $"Because {resolver.Describe(root)}, resolution failed.", false);
            }

            // A number only where a later line refers back to it.
            var lines = _lines.Select(l => (l.Message, Number: l.Number is { } n && _lines.Any(o => o.Message.Contains($"({n})", StringComparison.Ordinal)) ? l.Number : null)).ToList();
            var numbers = lines.Where(static l => l.Number is not null).Select(static l => l.Number!.Value).ToList();
            var padding = numbers.Count == 0 ? 0 : $"({numbers.Max()}) ".Length;
            var builder = new StringBuilder("error: cannot resolve dependencies\n\n");
            foreach (var (message, number) in lines) {
                if (message.Length == 0) {
                    builder.Append('\n');
                    continue;
                }

                var prefix = number is { } n ? $"({n}) ".PadRight(padding) : new string(' ', padding);
                builder.Append("  ").Append(prefix).Append(message).Append('\n');
            }

            if (MajorVersionNote() is { } note) {
                builder.Append('\n').Append(note).Append('\n');
            }

            return builder.ToString().TrimEnd() + "\n";
        }

        /// <summary>
        ///     When two dependencies ask for different major versions of one
        ///     package: a graph holds one major version of each package, which
        ///     Cargo users will not expect (docs/05 §5.3).
        /// </summary>
        string? MajorVersionNote() {
            var external = _derivations.Keys.Where(static i => i.Kind == IncompatibilityKind.Dependency).ToList();
            foreach (var group in external.GroupBy(i => resolver.Display(i.Terms[1].Package))) {
                var sets = group.Select(static i => i.Terms[1].Set).ToList();
                foreach (var a in sets) {
                    foreach (var b in sets) {
                        if (a.IsDisjointFrom(b) && a.Intervals is [{ Lower: { } x }, ..] && b.Intervals is [{ Lower: { } y }, ..] && x.Major < y.Major) {
                            return $"note: a dependency graph holds one major version of each package, so {group.Key} {a} and {group.Key} {b} cannot both be used (docs/05 §5.3)";
                        }
                    }
                }
            }

            return null;
        }

        void Count(Incompatibility incompatibility) {
            _derivations[incompatibility] = _derivations.GetValueOrDefault(incompatibility) + 1;
            if (_derivations[incompatibility] == 1 && incompatibility.IsDerived) {
                Count(incompatibility.Cause!);
                Count(incompatibility.OtherCause!);
            }
        }

        void Add(Incompatibility incompatibility, string message, bool numbered) {
            if (numbered) {
                var number = _lineNumbers.Count + 1;
                _lineNumbers[incompatibility] = number;
                _lines.Add((message, number));
            } else {
                _lines.Add((message, null));
            }
        }

        static bool IsSingleLine(Incompatibility incompatibility) => !incompatibility.Cause!.IsDerived && !incompatibility.OtherCause!.IsDerived;

        bool IsCollapsible(Incompatibility incompatibility) {
            if (_derivations[incompatibility] > 1) {
                return false;
            }

            var (conflict, other) = (incompatibility.Cause!, incompatibility.OtherCause!);
            if (conflict.IsDerived == other.IsDerived) {
                return false;
            }

            return !_lineNumbers.ContainsKey(conflict.IsDerived ? conflict : other);
        }

        string Describe(Incompatibility incompatibility) => resolver.Describe(incompatibility);

        void Visit(Incompatibility incompatibility, bool conclusion = false) {
            var numbered = conclusion || _derivations[incompatibility] > 1;
            var conjunction = conclusion || numbered ? "So," : "And";
            var conflict = incompatibility.Cause!;
            var other = incompatibility.OtherCause!;
            var said = Describe(incompatibility);

            if (conflict.IsDerived && other.IsDerived) {
                var conflictLine = _lineNumbers.TryGetValue(conflict, out var c) ? c : (int?)null;
                var otherLine = _lineNumbers.TryGetValue(other, out var o) ? o : (int?)null;
                if (conflictLine is not null && otherLine is not null) {
                    Add(incompatibility, $"Because {resolver.AndToString(conflict, other, conflictLine, otherLine)}, {said}.", numbered);
                } else if (conflictLine is not null || otherLine is not null) {
                    var (withLine, withoutLine, line) = conflictLine is not null ? (conflict, other, conflictLine) : (other, conflict, otherLine);
                    Visit(withoutLine);
                    Add(incompatibility, $"{conjunction} because {Describe(withLine)} ({line}), {said}.", numbered);
                } else {
                    var singleConflict = IsSingleLine(conflict);
                    var singleOther = IsSingleLine(other);
                    if (singleConflict || singleOther) {
                        var first = singleOther ? conflict : other;
                        var second = singleOther ? other : conflict;
                        Visit(first);
                        Visit(second);
                        Add(incompatibility, $"Thus, {said}.", numbered);
                    } else {
                        Visit(conflict, true);
                        _lines.Add(("", null));
                        Visit(other);
                        Add(incompatibility, $"{conjunction} because {Describe(conflict)} ({_lineNumbers[conflict]}), {said}.", numbered);
                    }
                }
            } else if (conflict.IsDerived || other.IsDerived) {
                var derived = conflict.IsDerived ? conflict : other;
                var external = conflict.IsDerived ? other : conflict;
                if (_lineNumbers.TryGetValue(derived, out var derivedLine)) {
                    Add(incompatibility, $"Because {resolver.AndToString(external, derived, null, derivedLine)}, {said}.", numbered);
                } else if (IsCollapsible(derived)) {
                    var derivedConflict = derived.Cause!;
                    var derivedOther = derived.OtherCause!;
                    var collapsedDerived = derivedConflict.IsDerived ? derivedConflict : derivedOther;
                    var collapsedExternal = derivedConflict.IsDerived ? derivedOther : derivedConflict;
                    Visit(collapsedDerived);
                    Add(incompatibility, $"{conjunction} because {resolver.AndToString(collapsedExternal, external, null, null)}, {said}.", numbered);
                } else {
                    Visit(derived);
                    Add(incompatibility, $"{conjunction} because {Describe(external)}, {said}.", numbered);
                }
            } else {
                Add(incompatibility, $"Because {resolver.AndToString(conflict, other, null, null)}, {said}.", numbered);
            }
        }
    }

    // ---- The partial solution ----------------------------------------------------------------------

    /// <summary>One step of the partial solution: a decision (no cause) or a derivation (with its cause).</summary>
    sealed record Assignment(Term Term, int DecisionLevel, int Index, Incompatibility? Cause);

    sealed class PartialSolution {
        readonly List<Assignment> _assignments = [];
        readonly Dictionary<string, SemVersion> _decisions = new(StringComparer.Ordinal);
        readonly Dictionary<string, Term> _positive = new(StringComparer.Ordinal);
        readonly Dictionary<string, Term> _negative = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, SemVersion> Decisions => _decisions;

        int DecisionLevel => _decisions.Count;

        public void Decide(string package, SemVersion version) {
            _decisions[package] = version;
            Assign(new(new(package, VersionSet.Exactly(version), true), DecisionLevel, _assignments.Count, null));
        }

        public void Derive(Term term, Incompatibility cause) => Assign(new(term, DecisionLevel, _assignments.Count, cause));

        void Assign(Assignment assignment) {
            _assignments.Add(assignment);
            Register(assignment.Term);
        }

        void Register(Term term) {
            var package = term.Package;
            if (_positive.TryGetValue(package, out var positive)) {
                _positive[package] = positive.Intersect(term);
                return;
            }

            var combined = _negative.TryGetValue(package, out var negative) ? negative.Intersect(term) : term;
            if (combined.Positive) {
                _negative.Remove(package);
                _positive[package] = combined;
            } else {
                _negative[package] = combined;
            }
        }

        public void Backtrack(int level) {
            var removed = new HashSet<string>(StringComparer.Ordinal);
            while (_assignments.Count > 0 && _assignments[^1].DecisionLevel > level) {
                var assignment = _assignments[^1];
                _assignments.RemoveAt(_assignments.Count - 1);
                removed.Add(assignment.Term.Package);
                if (assignment.Cause is null) {
                    _decisions.Remove(assignment.Term.Package);
                }
            }

            foreach (var package in removed) {
                _positive.Remove(package);
                _negative.Remove(package);
            }

            foreach (var assignment in _assignments.Where(a => removed.Contains(a.Term.Package))) {
                Register(assignment.Term);
            }
        }

        public SetRelation Relation(Term term) =>
            _positive.TryGetValue(term.Package, out var positive) ? positive.Relation(term)
            : _negative.TryGetValue(term.Package, out var negative) ? negative.Relation(term)
            : SetRelation.Overlapping;

        public bool Satisfies(Term term) => Relation(term) == SetRelation.Subset;

        /// <summary>The earliest assignment after which <paramref name="term" /> is satisfied.</summary>
        public Assignment Satisfier(Term term) {
            Term? accumulated = null;
            foreach (var assignment in _assignments) {
                if (assignment.Term.Package != term.Package) {
                    continue;
                }

                accumulated = accumulated is null ? assignment.Term : accumulated.Intersect(assignment.Term);
                if (accumulated.Satisfies(term)) {
                    return assignment;
                }
            }

            throw new InvalidOperationException($"internal error: nothing satisfies {term}");
        }

        /// <summary>Packages that must be selected but have no version decided yet.</summary>
        public IEnumerable<Term> Undecided() => _positive.Values.Where(t => !_decisions.ContainsKey(t.Package));
    }
}
