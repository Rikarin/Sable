using System.Runtime.InteropServices;
using System.Text;

namespace Sable.Fuzz;

/// <summary>
///     A coverage-guided fuzzing loop in this process, for where libFuzzer
///     cannot run (macOS: Apple's clang has no libFuzzer runtime, so
///     libfuzzer-dotnet cannot be built there).
/// </summary>
/// <remarks>
///     <para>
///         The coverage is SharpFuzz's: the Sable assemblies, rewritten by
///         <c>sharpfuzz</c>, count each branch edge taken into a 64 KB map,
///         AFL's way. This loop owns the map. After each input it sorts each
///         edge's count into AFL's buckets (1, 2, 3, 4–7, 8–15, 16–31,
///         32–127, 128+) and keeps the input when it shows an edge, or an
///         edge's bucket, no input has shown before; mutants are drawn from
///         what it kept. That is libFuzzer's loop, without its
///         refinements (no value profile, no input-to-state comparisons).
///     </para>
///     <para>
///         Mutations are libFuzzer's byte-level ones and the dictionary's
///         tokens, inserted and overwritten, plus line-level ones, as the
///         inputs are mostly source text. What it keeps goes to the corpus
///         directory, which libFuzzer reads too, and the other way about.
///     </para>
///     <para>
///         Failures are kept as <c>crash-&lt;sha1&gt;</c> in the artifacts
///         directory, with a <c>.txt</c> that says what was thrown — the first
///         input of each distinct failure, not every one. A hang is kept as
///         <c>timeout-&lt;sha1&gt;</c> and ends the process (exit code 3), as its
///         thread cannot be stopped: tools/fuzz/coverage.sh starts it again.
///         Each input is written to <c>.current</c> there before it runs, so one
///         that takes the process down (a stack overflow cannot be caught) is
///         still on disk; the next start keeps it as a crash.
///     </para>
/// </remarks>
public sealed unsafe class CoverageLoop(Target target, string corpus, IReadOnlyList<string> seeds, string artifacts, IReadOnlyList<byte[]> dictionary, int seed) {
    const int MapSize = 1 << 16;

    readonly byte[] _seen = new byte[MapSize];
    readonly List<byte[]> _pool = [];
    readonly HashSet<string> _signatures = new(StringComparer.Ordinal);
    readonly Random _random = new(seed);
    int _edges;
    long _executions;
    int _failures;
    DateTime _start;

    string Current => Path.Combine(artifacts, ".current");

    public int Run(TimeSpan duration) {
        Directory.CreateDirectory(corpus);
        Directory.CreateDirectory(artifacts);
        var map = (byte*)NativeMemory.AllocZeroed(MapSize);
        SharpFuzz.Common.Trace.SharedMem = map;
        using var runner = new Runner(target);
        _start = DateTime.UtcNow;
        var deadline = _start + duration;

        if (File.Exists(Current)) {
            var died = File.ReadAllBytes(Current);
            Keep("crash", died, "the process died on this input: a stack overflow, or the runtime failing fast. Replay it in a process of its own to see how.\n");
            File.Delete(Current);
        }

        // The corpus so far, then the seeds, smallest first: each kept only if
        // it shows something new. A corpus grows night by night, so reading it
        // is held to the deadline too: what is not read by then waits for the next run.
        var inputs = new[] { corpus }.Concat(seeds).Where(Directory.Exists)
            .SelectMany(static d => Directory.GetFiles(d).Where(static f => !Path.GetFileName(f).StartsWith('.')))
            .Select(static f => (Path: f, Size: new FileInfo(f).Length)).OrderBy(static f => f.Size).ToList();
        var read = 0;
        foreach (var (path, _) in inputs) {
            if (DateTime.UtcNow >= deadline) {
                break;
            }

            read++;
            var data = File.ReadAllBytes(path);
            if (data.Length > target.MaxLength) {
                data = data[..target.MaxLength];
            }

            switch (Try(runner, map, data, persist: !path.StartsWith(corpus, StringComparison.Ordinal))) {
                case Runner.Outcome.Hung:
                    return 3;
                case Runner.Outcome.Stopped:
                    return Stop();
            }
        }

        if (_pool.Count == 0) {
            _pool.Add("\n"u8.ToArray());
        }

        Console.WriteLine($"{target.Name}: {read} of {inputs.Count} inputs read, {_pool.Count} kept; {_edges} edges");
        var report = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline) {
            var parent = _pool[_random.Next(_pool.Count)];
            var child = Mutate(parent);
            switch (Try(runner, map, child, persist: true)) {
                case Runner.Outcome.Hung:
                    return 3;
                case Runner.Outcome.Stopped:
                    return Stop();
            }

            if (DateTime.UtcNow > report) {
                Status();
                report = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            }
        }

        Status();
        File.Delete(Current);
        return 0;
    }

    int Stop() {
        // Stopped from outside: whatever was running is not a result.
        File.Delete(Current);
        Console.WriteLine($"{target.Name}: stopped");
        Status();
        return 0;
    }

    void Status() {
        var seconds = Math.Max(1, (DateTime.UtcNow - _start).TotalSeconds);
        Console.WriteLine($"#execs: {_executions}  edges: {_edges}  corpus: {_pool.Count}  failures: {_failures}  exec/s: {_executions / seconds:F0}");
    }

    Runner.Outcome Try(Runner runner, byte* map, byte[] input, bool persist) {
        File.WriteAllBytes(Current, input);
        NativeMemory.Clear(map, MapSize);
        SharpFuzz.Common.Trace.PrevLocation = 0;
        var (outcome, failure) = runner.Run(input);
        _executions++;
        switch (outcome) {
            case Runner.Outcome.Hung:
                Keep("timeout", input, $"no answer within {target.Limit.TotalSeconds}s\n");
                File.Delete(Current);
                return outcome;
            case Runner.Outcome.Stopped:
                return outcome;
            case Runner.Outcome.Failed:
                _failures++;
                var signature = Runner.Signature(failure!);
                if (_signatures.Add(signature)) {
                    Keep("crash", input, Runner.Report(failure!, input));
                }

                return outcome;
        }

        if (NewCoverage(map)) {
            _pool.Add(input);
            if (persist) {
                File.WriteAllBytes(Path.Combine(corpus, Runner.Hash(input)), input);
            }
        }

        return outcome;
    }

    void Keep(string kind, byte[] input, string what) {
        var path = Path.Combine(artifacts, $"{kind}-{Runner.Hash(input)}");
        File.WriteAllBytes(path, input);
        File.WriteAllText(path + ".txt", what);
        Console.WriteLine($"{target.Name}: {kind}, kept as {path}: {what.Split('\n')[0]}");
    }

    /// <summary>AFL's bucket for an edge's count, as one bit of a byte.</summary>
    static byte Bucket(byte count) => count switch {
        0 => 0,
        1 => 1,
        2 => 2,
        3 => 4,
        <= 7 => 8,
        <= 15 => 16,
        <= 31 => 32,
        <= 127 => 64,
        _ => 128,
    };

    bool NewCoverage(byte* map) {
        var found = false;
        var words = (ulong*)map;
        for (var w = 0; w < MapSize / 8; w++) {
            if (words[w] == 0) {
                continue;
            }

            for (var i = w * 8; i < w * 8 + 8; i++) {
                var bucket = Bucket(map[i]);
                if ((bucket & ~_seen[i]) != 0) {
                    if (_seen[i] == 0) {
                        _edges++;
                    }

                    _seen[i] |= bucket;
                    found = true;
                }
            }
        }

        return found;
    }

    static readonly long[] Interesting = [0, 1, -1, 2, 7, 8, 16, 31, 32, 64, 127, 128, 255, 256, 1000, 1024, 4096, 32767, 65535, 65536, int.MaxValue, uint.MaxValue, long.MaxValue];

    /// <summary>A mutant: one to sixteen edits, stacked, no longer than the target takes.</summary>
    byte[] Mutate(byte[] parent) {
        var bytes = new List<byte>(parent);
        for (var edits = 1 << _random.Next(5); edits > 0; edits--) {
            Edit(bytes);
            if (bytes.Count == 0) {
                bytes.Add((byte)'\n');
            }
        }

        if (bytes.Count > target.MaxLength) {
            bytes.RemoveRange(target.MaxLength, bytes.Count - target.MaxLength);
        }

        return [..bytes];
    }

    void Edit(List<byte> bytes) {
        var at = _random.Next(bytes.Count + 1);
        var length = Math.Min(1 + _random.Next(_random.Next(2) == 0 ? 8 : 64), bytes.Count - Math.Min(at, bytes.Count));
        switch (_random.Next(16)) {
            case 0 when length > 0:
                bytes.RemoveRange(at, length);
                break;
            case 1:
                bytes.Insert(at, (byte)_random.Next(256));
                break;
            case 2:
                bytes.InsertRange(at, Enumerable.Repeat((byte)(_random.Next(2) == 0 ? _random.Next(256) : " \n({["[_random.Next(5)]), 1 + _random.Next(32)));
                break;
            case 3 when at < bytes.Count:
                bytes[at] = (byte)_random.Next(256);
                break;
            case 4 when at < bytes.Count:
                bytes[at] ^= (byte)(1 << _random.Next(8));
                break;
            case 5 when length > 1: {
                // Shuffle a run.
                var run = bytes.GetRange(at, length).OrderBy(_ => _random.Next()).ToList();
                bytes.RemoveRange(at, length);
                bytes.InsertRange(at, run);
                break;
            }
            case 6: {
                // A number in the text changed to another, maybe an interesting one.
                var digit = bytes.FindIndex(Math.Min(at, bytes.Count), static b => b is >= (byte)'0' and <= (byte)'9');
                if (digit < 0) {
                    break;
                }

                var end = digit;
                while (end < bytes.Count && bytes[end] is >= (byte)'0' and <= (byte)'9') {
                    end++;
                }

                var number = _random.Next(3) == 0 ? _random.Next(1000) : Interesting[_random.Next(Interesting.Length)];
                bytes.RemoveRange(digit, end - digit);
                bytes.InsertRange(digit, Encoding.ASCII.GetBytes(number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                break;
            }
            case 7 when length > 0: {
                // A run copied elsewhere in the same input.
                var run = bytes.GetRange(at, length);
                bytes.InsertRange(_random.Next(bytes.Count + 1), run);
                break;
            }
            case 8 or 9 when dictionary.Count > 0:
                bytes.InsertRange(at, dictionary[_random.Next(dictionary.Count)]);
                break;
            case 10 when dictionary.Count > 0: {
                var token = dictionary[_random.Next(dictionary.Count)];
                bytes.RemoveRange(at, Math.Min(token.Length, bytes.Count - at));
                bytes.InsertRange(at, token);
                break;
            }
            case 11: {
                // A run of another input inserted.
                var donor = _pool[_random.Next(_pool.Count)];
                var from = _random.Next(donor.Length);
                bytes.InsertRange(at, donor[from..Math.Min(donor.Length, from + 1 + _random.Next(128))]);
                break;
            }
            case 12: {
                // Crossover: this input's start, another's end.
                var donor = _pool[_random.Next(_pool.Count)];
                bytes.RemoveRange(at, bytes.Count - at);
                bytes.AddRange(donor[_random.Next(donor.Length + 1)..]);
                break;
            }
            case 13 or 14: {
                // A whole line deleted, duplicated, or moved: the unit of most programs.
                var lines = Split(bytes);
                if (lines.Count < 2) {
                    break;
                }

                var line = _random.Next(lines.Count);
                var picked = lines[line];
                switch (_random.Next(3)) {
                    case 0:
                        lines.RemoveAt(line);
                        break;
                    case 1:
                        lines.Insert(_random.Next(lines.Count + 1), picked);
                        break;
                    default:
                        lines.RemoveAt(line);
                        lines.Insert(_random.Next(lines.Count + 1), picked);
                        break;
                }

                bytes.Clear();
                foreach (var l in lines) {
                    bytes.AddRange(l);
                }

                break;
            }
            case 15 when length > 0: {
                // A run swapped for one from another input of the pool.
                var donor = _pool[_random.Next(_pool.Count)];
                var from = _random.Next(donor.Length);
                bytes.RemoveRange(at, length);
                bytes.InsertRange(at, donor[from..Math.Min(donor.Length, from + length)]);
                break;
            }
        }
    }

    static List<byte[]> Split(List<byte> bytes) {
        var lines = new List<byte[]>();
        var start = 0;
        for (var i = 0; i < bytes.Count; i++) {
            if (bytes[i] == '\n') {
                lines.Add([..bytes.GetRange(start, i + 1 - start)]);
                start = i + 1;
            }
        }

        if (start < bytes.Count) {
            lines.Add([..bytes.GetRange(start, bytes.Count - start)]);
        }

        return lines;
    }
}
