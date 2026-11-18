using System.Globalization;
using System.Text;

namespace Sable.SIL;

/// <summary>One counter: a block of code, and the lines of its file it covers (none, for a branch's).</summary>
public sealed record CoverageCounter(string File, IReadOnlyList<int> Lines);

/// <summary>One outcome of a decision: its name, and the counters whose counts add up to how often it happened.</summary>
public sealed record CoverageOutcome(string Label, IReadOnlyList<int> Counters);

/// <summary>A decision of the measured code, where it is written, and its outcomes.</summary>
public sealed record CoverageDecision(string File, int Line, int Column, string Kind, IReadOnlyList<CoverageOutcome> Outcomes);

/// <summary>A decision as a run took it: how many times each outcome happened.</summary>
public sealed record CoverageBranch(int Line, int Column, string Kind, IReadOnlyList<(string Label, long Count)> Outcomes) {
    /// <summary>Whether the decision was made at all: one of its outcomes happened.</summary>
    public bool Reached => Outcomes.Any(static o => o.Count > 0);

    /// <summary>The outcomes that never happened.</summary>
    public IEnumerable<string> Missed => Outcomes.Where(static o => o.Count == 0).Select(static o => o.Label);
}

/// <summary>What <see cref="Coverage.Instrument" /> put in a module: its counters, by number, and the decisions they count.</summary>
public sealed record CoverageMap(IReadOnlyList<CoverageCounter> Counters, IReadOnlyList<CoverageDecision> Decisions) {
    public static readonly CoverageMap Empty = new([], []);

    public CoverageMap(IReadOnlyList<CoverageCounter> counters) : this(counters, []) { }

    /// <summary>
    ///     How many times each line of each file ran, from the counts a run
    ///     wrote: a line runs as often as the most-run block it is in, and a
    ///     line of code whose blocks never ran is there with 0.
    /// </summary>
    public SortedDictionary<string, SortedDictionary<int, long>> Lines(IReadOnlyDictionary<int, long> counts) {
        var files = new SortedDictionary<string, SortedDictionary<int, long>>(StringComparer.Ordinal);
        for (var id = 0; id < Counters.Count; id++) {
            var (file, lines) = Counters[id];
            if (lines.Count == 0) {
                continue;
            }

            if (!files.TryGetValue(file, out var byLine)) {
                files[file] = byLine = [];
            }

            var count = counts.GetValueOrDefault(id);
            foreach (var line in lines) {
                byLine[line] = Math.Max(byLine.GetValueOrDefault(line), count);
            }
        }

        return files;
    }

    /// <summary>
    ///     Each file's decisions, in the order they are written, with how
    ///     often each outcome happened. A decision lowered more than once —
    ///     a generic function's, once per type it is used with; a
    ///     <c>defer</c>'s, once per way out — is one decision of the source,
    ///     its counts added up.
    /// </summary>
    public SortedDictionary<string, IReadOnlyList<CoverageBranch>> Branches(IReadOnlyDictionary<int, long> counts) {
        var files = new SortedDictionary<string, IReadOnlyList<CoverageBranch>>(StringComparer.Ordinal);
        foreach (var byFile in Decisions.GroupBy(static d => d.File)) {
            files[byFile.Key] = [
                ..byFile
                    .GroupBy(static d => (d.Line, d.Column, d.Kind))
                    .OrderBy(static g => g.Key.Line)
                    .ThenBy(static g => g.Key.Column)
                    .Select(same => {
                        var first = same.First();
                        return new CoverageBranch(first.Line, first.Column, first.Kind, [
                            ..first.Outcomes.Select((outcome, index) => (
                                outcome.Label,
                                same.Sum(d => index < d.Outcomes.Count ? d.Outcomes[index].Counters.Sum(c => counts.GetValueOrDefault(c)) : 0))),
                        ]);
                    }),
            ];
        }

        return files;
    }
}

/// <summary>
///     Line and branch coverage for <c>sable test --coverage</c>, by
///     counters in the code: each block of a function from a file being
///     measured begins with <c>coverage_hit(n)</c>, as does each block that
///     stands for an outcome of one of its decisions (lowering marks them,
///     <see cref="SilDecision" />). The runtime counts them and writes the
///     counts out when the program ends (to the file <c>SABLE_COVERAGE</c>
///     names); the interpreter counts them in <see cref="SilInterpreter.CoverageCounts" />.
///     It runs on the module as lowered, before any pass, so what the
///     optimiser inlines or merges carries its counter with it, and every
///     line that has code is counted, run or not. A counter is a call the
///     passes keep, as they keep any builtin with an effect, and that is
///     never merged with another, its number being its own.
/// </summary>
public static class Coverage {
    /// <summary>The builtin that counts a block.</summary>
    public const string Builtin = "coverage_hit";

    /// <summary>
    ///     Puts a counter at the start of each block, and of each block
    ///     standing for a decision's outcome, of the functions whose file
    ///     <paramref name="measured" /> takes.
    /// </summary>
    public static CoverageMap Instrument(SilModule module, Func<string, bool> measured) {
        var counters = new List<CoverageCounter>();
        var decisions = new List<CoverageDecision>();
        foreach (var function in module.Functions) {
            if (function.SourceFile is not { } file || !measured(file)) {
                continue;
            }

            foreach (var block in function.Blocks) {
                var lines = block.Instructions
                    .Where(static i => i.Location is not null && i.InlinedSite is null)
                    .Select(static i => i.Location!.Value.Line)
                    .Where(static l => l > 0)
                    .Distinct()
                    .Order()
                    .ToList();
                if (lines.Count == 0) {
                    continue;
                }

                Count(block, counters.Count, new SilLocation(lines[0], 1));
                counters.Add(new(file, lines));
            }

            // An outcome's counter goes before its block's line counter: both count the same entries.
            var outcomes = new Dictionary<SilDecision, List<int>[]>(ReferenceEqualityComparer.Instance);
            foreach (var decision in function.Decisions) {
                outcomes[decision] = [..decision.Outcomes.Select(static _ => new List<int>())];
            }

            foreach (var block in function.Blocks) {
                if (block.Outcome is not var (decision, outcome) || !outcomes.TryGetValue(decision, out var byOutcome)) {
                    continue;
                }

                Count(block, counters.Count, decision.At);
                byOutcome[outcome].Add(counters.Count);
                counters.Add(new(file, []));
            }

            foreach (var decision in function.Decisions) {
                var byOutcome = outcomes[decision];
                decisions.Add(new(file, decision.At.Line, decision.At.Column, decision.Kind, [
                    ..decision.Outcomes.Select((label, i) => new CoverageOutcome(label, byOutcome[i])),
                ]));
            }
        }

        return new(counters, decisions);
    }

    /// <summary>Starts a block with counter <paramref name="id" />.</summary>
    static void Count(SilBlock block, int id, SilLocation location) {
        var number = new IntegerLiteralInst(SilType.Int, id) { Block = block, Location = location };
        var hit = new BuiltinInst(Builtin, SilType.Unit, number) { Block = block, Location = location };
        block.Instructions.InsertRange(0, [number, hit]);
    }

    /// <summary>
    ///     The counts written by every run of a program, each line
    ///     <c>counter count</c>, added together: several test processes
    ///     append to one file.
    /// </summary>
    public static Dictionary<int, long> ReadCounts(string path) {
        var counts = new Dictionary<int, long>();
        if (!File.Exists(path)) {
            return counts;
        }

        foreach (var line in File.ReadLines(path)) {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var id) && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var count)) {
                counts[id] = counts.GetValueOrDefault(id) + count;
            }
        }

        return counts;
    }

    /// <summary>
    ///     The lines and branches as LCOV writes them, which coverage viewers
    ///     (genhtml, editors) read: <c>DA</c>, <c>LF</c> and <c>LH</c> for
    ///     lines; <c>BRDA:line,decision,outcome,count</c>, <c>BRF</c> and
    ///     <c>BRH</c> for branches, where the decision is numbered among those
    ///     of its line, and the count of a decision never reached is <c>-</c>.
    /// </summary>
    public static string Lcov(
        SortedDictionary<string, SortedDictionary<int, long>> files,
        IReadOnlyDictionary<string, IReadOnlyList<CoverageBranch>>? branches = null
    ) {
        var text = new StringBuilder();
        foreach (var (file, lines) in files) {
            text.Append("SF:").Append(file).Append('\n');
            foreach (var (line, count) in lines) {
                text.Append("DA:").Append(line).Append(',').Append(count).Append('\n');
            }

            text.Append("LF:").Append(lines.Count).Append('\n');
            text.Append("LH:").Append(lines.Count(static l => l.Value > 0)).Append('\n');
            if (branches?.GetValueOrDefault(file) is { Count: > 0 } decisions) {
                foreach (var onLine in decisions.GroupBy(static d => d.Line)) {
                    var number = 0;
                    foreach (var decision in onLine) {
                        for (var outcome = 0; outcome < decision.Outcomes.Count; outcome++) {
                            text.Append(CultureInfo.InvariantCulture, $"BRDA:{decision.Line},{number},{outcome},")
                                .Append(decision.Reached ? decision.Outcomes[outcome].Count.ToString(CultureInfo.InvariantCulture) : "-")
                                .Append('\n');
                        }

                        number++;
                    }
                }

                text.Append("BRF:").Append(decisions.Sum(static d => d.Outcomes.Count)).Append('\n');
                text.Append("BRH:").Append(decisions.Sum(static d => d.Outcomes.Count(static o => o.Count > 0))).Append('\n');
            }

            text.Append("end_of_record\n");
        }

        return text.ToString();
    }
}
