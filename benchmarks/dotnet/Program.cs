// The .NET side of the Phase 4 exit criterion "Dictionary/List/String within
// 15% of their .NET equivalents" (docs/04 §4.4): each benchmark here is a
// port of ../versus-dotnet/<name>.sb — the same steps, sizes and output.
//
//     dotnet run -c Release -- <name> [runs]
//
// Runs the benchmark 'runs' times in this process (default 5) and prints its
// output once, then 'ms <fastest>' on standard error: the processor time
// (user and system) of the fastest run — the steady state, after the JIT has
// tiered up, which is the hardest .NET time to match. Processor time rather
// than wall time, as tools/bench/versus_dotnet.py measures Sable, so that a
// busy machine skews both sides less. Integer arithmetic is checked, as
// Sable's is.

using System.Diagnostics;

var benchmarks = new Dictionary<string, Func<string>> {
    ["map-int"] = MapInt,
    ["map-string"] = MapString,
    ["list"] = List,
    ["sort"] = Sort,
    ["string"] = Strings,
    ["linq"] = Linq,
};

if (args.Length == 0 || !benchmarks.TryGetValue(args[0], out var run)) {
    Console.Error.WriteLine($"usage: <{string.Join("|", benchmarks.Keys)}> [runs]");
    return 2;
}

var runs = args.Length > 1 ? int.Parse(args[1]) : 5;
string? output = null;
var best = double.MaxValue;
var process = Process.GetCurrentProcess();
for (var i = 0; i < runs; i++) {
    process.Refresh();
    var before = process.TotalProcessorTime;
    output = run();
    process.Refresh();
    best = Math.Min(best, (process.TotalProcessorTime - before).TotalMilliseconds);
}

Console.WriteLine(output);
Console.Error.WriteLine($"ms {best:F1}");
return 0;

static string MapInt() {
    checked {
        const int n = 1000000;
        var keys = new List<long>();
        long x = 12345;
        for (var i = 0; i < n; i++) {
            x = (x * 1103515245 + 12345) % 2147483648;
            keys.Add(x);
        }

        var map = new Dictionary<long, long>();
        for (var i = 0; i < n; i++) {
            map[keys[i]] = i;
        }

        long sum = 0;
        for (var round = 0; round < 3; round++) {
            foreach (var key in keys) {
                sum += map.TryGetValue(key, out var value) ? value : 0;
            }
        }

        long misses = 0;
        for (var i = 0; i < n; i++) {
            if (!map.ContainsKey(keys[i] + 2147483648)) {
                misses++;
            }
        }

        for (var i = 0; i < n; i += 2) {
            map.Remove(keys[i]);
        }

        long hits = 0;
        foreach (var key in keys) {
            if (map.ContainsKey(key)) {
                hits++;
            }
        }

        return $"{map.Count} {sum} {misses} {hits}";
    }
}

static string MapString() {
    checked {
        const int n = 200000;
        var keys = new List<string>();
        for (long i = 0; i < n; i++) {
            keys.Add($"key-{i * 7919 % 1000003}");
        }

        var map = new Dictionary<string, long>();
        for (var i = 0; i < n; i++) {
            map[keys[i]] = i;
        }

        long sum = 0;
        for (var round = 0; round < 5; round++) {
            foreach (var key in keys) {
                sum += map.TryGetValue(key, out var value) ? value : 0;
            }
        }

        for (var i = 0; i < n; i += 2) {
            map.Remove(keys[i]);
        }

        long hits = 0;
        foreach (var key in keys) {
            if (map.ContainsKey(key)) {
                hits++;
            }
        }

        return $"{map.Count} {sum} {hits}";
    }
}

static string List() {
    checked {
        long total = 0;
        for (long round = 0; round < 3; round++) {
            var values = new List<long>();
            for (long i = 0; i < 10000000; i++) {
                values.Add(i ^ round);
            }

            foreach (var v in values) {
                total += v;
            }

            for (var i = 0; i < values.Count; i++) {
                total += values[i] % 7;
            }

            while (values.Count > 5000000) {
                total -= values[^1];
                values.RemoveAt(values.Count - 1);
            }

            total += values.Count;
        }

        return total.ToString();
    }
}

static string Linq() {
    checked {
        var values = new List<long>();
        for (long i = 0; i < 10000000; i++) {
            values.Add(i);
        }

        long chained = 0;
        for (long round = 0; round < 5; round++) {
            var modulus = round + 3;
            chained += values.Where(v => v % modulus != 0).Select(v => v * 2).Sum();
        }

        long looped = 0;
        for (long round = 0; round < 5; round++) {
            var modulus = round + 3;
            foreach (var value in values) {
                if (value % modulus != 0) {
                    looped += value * 2;
                }
            }
        }

        return $"{chained} {looped}";
    }
}

static string Sort() {
    checked {
        var numbers = new List<long>();
        long x = 42;
        for (var i = 0; i < 1000000; i++) {
            x = (x * 1103515245 + 12345) % 2147483648;
            numbers.Add(x);
        }

        var words = new List<string>();
        for (var i = 0; i < 200000; i++) {
            x = (x * 1103515245 + 12345) % 2147483648;
            words.Add($"w{x % 1000000}");
        }

        // Sable orders strings by Unicode scalar, as ordinal comparison does for these.
        var a = new List<long>(numbers);
        a.Sort();
        var b = new List<string>(words);
        b.Sort(string.CompareOrdinal);
        var ordered = true;
        for (var i = 1; i < a.Count; i++) {
            if (a[i] < a[i - 1]) { ordered = false; }
        }

        for (var i = 1; i < b.Count; i++) {
            if (string.CompareOrdinal(b[i], b[i - 1]) < 0) { ordered = false; }
        }

        return $"{(ordered ? "true" : "false")} {a[0]} {a[a.Count / 2]} {b[0]} {b[^1]}";
    }
}

static string Strings() {
    checked {
        string[] names = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta"];
        var lines = new List<string>();
        for (var i = 0; i < 300000; i++) {
            lines.Add($"{names[i % 8]} {i} {names[i * 3 % 8]} end");
        }

        long characters = 0;
        long matches = 0;
        long words = 0;
        foreach (var line in lines) {
            foreach (var word in line.Split(' ')) {
                words++;
                characters += word.Length;
                if (word == "gamma") {
                    matches++;
                }
            }
        }

        long joinedLength = 0;
        for (var i = 0; i < lines.Count; i += 16) {
            var block = "";
            for (var j = 0; j < 16 && i + j < lines.Count; j++) {
                block = block + lines[i + j] + "\n";
            }

            joinedLength += block.Length;
        }

        long sliced = 0;
        foreach (var line in lines) {
            if (line[..4] == "alph") {
                sliced++;
            }
        }

        return $"{words} {characters} {matches} {joinedLength} {sliced}";
    }
}
