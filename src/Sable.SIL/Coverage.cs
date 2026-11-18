namespace Sable.SIL;

/// <summary>One counter: a block of code, and the lines of its file it covers.</summary>
public sealed record CoverageCounter(string File, IReadOnlyList<int> Lines);

/// <summary>What <see cref="Coverage.Instrument" /> put in a module: its counters, by number.</summary>
public sealed record CoverageMap(IReadOnlyList<CoverageCounter> Counters) {
    public static readonly CoverageMap Empty = new([]);

    /// <summary>
    ///     How many times each line of each file ran, from the counts a run
    ///     wrote: a line runs as often as the most-run block it is in, and a
    ///     line of code whose blocks never ran is there with 0.
    /// </summary>
    public SortedDictionary<string, SortedDictionary<int, long>> Lines(IReadOnlyDictionary<int, long> counts) {
        var files = new SortedDictionary<string, SortedDictionary<int, long>>(StringComparer.Ordinal);
        for (var id = 0; id < Counters.Count; id++) {
            var (file, lines) = Counters[id];
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
}

/// <summary>
///     Line coverage for <c>sable test --coverage</c>, by counters in the
///     code: each block of a function from a file being measured begins
///     with <c>coverage_hit(n)</c>, which the runtime counts and writes out
///     when the program ends (to the file <c>SABLE_COVERAGE</c> names). It
///     runs on the module as lowered, before any pass, so what the
///     optimiser inlines or merges carries its counter with it, and every
///     line that has code is counted, run or not.
/// </summary>
public static class Coverage {
    /// <summary>The builtin that counts a block.</summary>
    public const string Builtin = "coverage_hit";

    /// <summary>Puts a counter at the start of each block, of the functions whose file <paramref name="measured" /> takes.</summary>
    public static CoverageMap Instrument(SilModule module, Func<string, bool> measured) {
        var counters = new List<CoverageCounter>();
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

                var location = new SilLocation(lines[0], 1);
                var id = new IntegerLiteralInst(SilType.Int, counters.Count) { Block = block, Location = location };
                var hit = new BuiltinInst(Builtin, SilType.Unit, id) { Block = block, Location = location };
                block.Instructions.InsertRange(0, [id, hit]);
                counters.Add(new(file, lines));
            }
        }

        return new(counters);
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
            if (parts.Length == 2 && int.TryParse(parts[0], out var id) && long.TryParse(parts[1], out var count)) {
                counts[id] = counts.GetValueOrDefault(id) + count;
            }
        }

        return counts;
    }

    /// <summary>The lines as LCOV writes them (<c>SF</c>, <c>DA</c>, <c>LF</c>, <c>LH</c>), which coverage viewers read.</summary>
    public static string Lcov(SortedDictionary<string, SortedDictionary<int, long>> files) {
        var text = new System.Text.StringBuilder();
        foreach (var (file, lines) in files) {
            text.Append("SF:").Append(file).Append('\n');
            foreach (var (line, count) in lines) {
                text.Append("DA:").Append(line).Append(',').Append(count).Append('\n');
            }

            text.Append("LF:").Append(lines.Count).Append('\n');
            text.Append("LH:").Append(lines.Count(static l => l.Value > 0)).Append('\n');
            text.Append("end_of_record\n");
        }

        return text.ToString();
    }
}
