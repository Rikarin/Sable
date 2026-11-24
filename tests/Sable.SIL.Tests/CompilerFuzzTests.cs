namespace Sable.SIL.Tests;

/// <summary>
///     A mutation fuzzer for the whole front end: real programs from the
///     repository, cut up at token boundaries, through the parser, the
///     binder and type checker, constant evaluation and, for what still
///     type-checks, lowering to SIL, the verifier and the passes.
/// </summary>
/// <remarks>
///     <para>
///         What it holds the compiler to: whatever the input, every stage
///         ends, within a time limit, by returning — with diagnostics, or
///         with none — and never by throwing; and a program that type-checks
///         and lowers with nothing to say lowers to SIL the verifier accepts,
///         before and after the passes (<see cref="FrontEndCheck" />, which
///         the coverage-guided fuzzer in tools/fuzz/Sable.Fuzz shares).
///         Mutating real programs rather than generating random tokens is
///         what gets past the parser: most mutants are nearly programs, so
///         they reach the binder and, often enough, lowering.
///     </para>
///     <para>
///         It is feedback-guided, though not by code coverage, which needs
///         the compiler instrumented (tools/fuzz/coverage.sh does that, for
///         long runs): each mutant's outcome is summed up
///         as features — the syntax kinds in its tree, the diagnostic codes
///         reported, how far down the pipeline it got — and a mutant that
///         shows a feature no input has shown before joins the pool that
///         later mutants are drawn from. That steers the run towards inputs
///         that reach new diagnostics and deeper stages, which is the part
///         of coverage-guided fuzzing that matters most here.
///     </para>
///     <para>
///         Settings are the parser fuzzer's: <c>SABLE_FUZZ_SEED</c> moves the
///         seed, <c>SABLE_FUZZ_SCALE</c> multiplies the case count (each build
///         runs a few dozen; the nightly run, thousands). When
///         <c>SABLE_FUZZ_CRASH_DIR</c> is set, each input is written there
///         before it runs, so that one that takes the process down — a stack
///         overflow cannot be caught — is still on disk afterwards.
///     </para>
/// </remarks>
public sealed class CompilerFuzzTests(Xunit.Abstractions.ITestOutputHelper output) {
    /// <summary>
    ///     How long one input may take through every stage: a mutant that is
    ///     slower than this has found a hang, or something close. An input
    ///     takes 2–3s warm, but with every test project running at once a
    ///     seed has gone past 20s, so the limit leaves room for a loaded machine.
    /// </summary>
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(Setting("SABLE_FUZZ_CASE_SECONDS") ?? 60);

    /// <summary>
    ///     How long the first input may take: it pays for the whole compiler
    ///     being JIT-compiled and Sable.Core being bound for the first time,
    ///     which on a CI runner shared with the other test projects can take
    ///     <see cref="Limit" /> by itself (a seed that type-checks takes about
    ///     7s cold on a fast machine, and 2–3s warm).
    /// </summary>
    static readonly TimeSpan ColdLimit = Limit * 4;

    /// <summary>The stack an input runs on: the 8 MB a process's main thread gets, so that a recursion too deep here is too deep for 'sable'.</summary>
    const int StackSize = 8 * 1024 * 1024;

    static int? Setting(string name) => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : null;

    /// <summary>A token with its trivia, and its kind, so that a mutation can trade it for another of the same kind.</summary>
    readonly record struct Piece(string Leading, string Core, string Trailing, SyntaxKind Kind) {
        public string Text => Leading + Core + Trailing;
    }

    [Fact]
    [Trait("Category", "Fuzz")]
    public void MutatedProgramsNeverCrashTheCompiler() {
        var random = new Random(Setting("SABLE_FUZZ_SEED") is { } night ? unchecked(20261121 * 31 + night) : 20261121);
        var cases = 24 * Math.Max(1, Setting("SABLE_FUZZ_SCALE") ?? 1);
        var crashDirectory = Environment.GetEnvironmentVariable("SABLE_FUZZ_CRASH_DIR");

        var seeds = Seeds().Select(Pieces).Where(static t => t.Count > 0).ToList();
        Assert.NotEmpty(seeds);
        var vocabulary = seeds.SelectMany(static t => t).Where(static p => p.Core.Length > 0).DistinctBy(static p => (p.Kind, p.Core))
            .GroupBy(static p => p.Kind).ToDictionary(static g => g.Key, static g => g.ToArray());

        // Two pools: every input that showed something new, and those of them
        // that type-check. Most mutants of a program do not, so half the
        // parents come from the second pool, or lowering would rarely be reached.
        var pool = new List<List<Piece>>();
        var checkedPool = new List<List<Piece>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stages = new Dictionary<string, int>(StringComparer.Ordinal);

        void Try(List<Piece> input, TimeSpan limit) {
            var source = string.Concat(input.Select(static p => p.Text));
            if (crashDirectory is not null) {
                Directory.CreateDirectory(crashDirectory);
                File.WriteAllText(Path.Combine(crashDirectory, "current.sb"), source);
            }

            var (features, failure) = Run(source, limit);
            if (failure is not null) {
                var smallest = failure.StartsWith("hang", StringComparison.Ordinal) ? input : Minimise(input, Signature(failure));
                Assert.Fail($"{failure}\n{Reproduce(source)}\n\nminimised to:\n{string.Concat(smallest.Select(static p => p.Text))}");
            }

            ArgumentNullException.ThrowIfNull(features);
            foreach (var stage in features.Where(static f => f.StartsWith("stage:", StringComparison.Ordinal))) {
                stages[stage] = stages.GetValueOrDefault(stage) + 1;
            }

            if (features.Count(seen.Add) > 0 && input.Count < 4000) {
                pool.Add(input);
                if (features.Contains("stage:type-checked")) {
                    checkedPool.Add(input);
                }
            }
        }

        // The seeds themselves first: they set the features a mutant has to
        // beat. The first of them runs on a cold compiler, so it has longer.
        for (var i = 0; i < seeds.Count; i++) {
            Try(seeds[i], i == 0 ? ColdLimit : Limit);
        }

        var fromSeeds = stages.ToDictionary();
        for (var i = 0; i < cases; i++) {
            var parents = checkedPool.Count > 0 && random.Next(2) == 0 ? checkedPool : pool;
            Try(Mutate(random, parents[random.Next(parents.Count)], pool, vocabulary), Limit);
        }

        output.WriteLine($"{seeds.Count} seeds ({Stages(fromSeeds)}), then {cases} mutants: {seen.Count} features, a pool of {pool.Count} ({checkedPool.Count} type-checking); "
            + $"in all, {Stages(stages)}");
    }

    static string Stages(Dictionary<string, int> stages) =>
        string.Join(", ", stages.OrderBy(static s => s.Value).Select(static s => $"{s.Value} {s.Key[6..]}"));

    /// <summary>
    ///     One input through every stage, on a thread of its own so that a
    ///     hang is reported, not waited out: its features, or what went wrong.
    /// </summary>
    static (HashSet<string>? Features, string? Failure) Run(string source, TimeSpan limit) {
        HashSet<string>? features = null;
        Exception? failure = null;
        var thread = new Thread(() => {
            try {
                features = FrontEndCheck.Compile(source);
            } catch (Exception e) {
                failure = e;
            }
        }, StackSize) { IsBackground = true };
        thread.Start();
        if (!thread.Join(limit)) {
            return (null, $"hang: the compiler took over {limit.TotalSeconds}s");
        }

        return failure is null ? (features, null) : (null, $"the compiler threw {failure}");
    }

    /// <summary>What makes two failures the same one: the exception and where it was thrown.</summary>
    static string Signature(string failure) {
        var lines = failure.Split('\n');
        var at = lines.FirstOrDefault(static l => l.TrimStart().StartsWith("at ", StringComparison.Ordinal)) ?? "";
        var paren = at.IndexOf('(');
        return lines[0].Split(':')[0] + "|" + (paren < 0 ? at : at[..paren]).Trim();
    }

    /// <summary>
    ///     Delta debugging over tokens: the fewest tokens, removed a chunk at a
    ///     time, that still fail the same way — so that a report is a repro a
    ///     person can read, not a mangled 300-line program.
    /// </summary>
    static List<Piece> Minimise(List<Piece> tokens, string signature) {
        bool StillFails(List<Piece> candidate) =>
            Run(string.Concat(candidate.Select(static p => p.Text)), Limit).Failure is { } f && !f.StartsWith("hang", StringComparison.Ordinal) && Signature(f) == signature;

        var current = tokens;
        for (var chunk = current.Count / 2; chunk >= 1; chunk /= 2) {
            for (var at = 0; at < current.Count;) {
                var candidate = new List<Piece>(current);
                candidate.RemoveRange(at, Math.Min(chunk, current.Count - at));
                if (candidate.Count > 0 && StillFails(candidate)) {
                    current = candidate;
                } else {
                    at += chunk;
                }
            }
        }

        return current;
    }

    /// <summary>
    ///     One edit, sometimes a few, at token boundaries: trade a token for
    ///     another of its kind (a literal for another literal, a name for
    ///     another name — the edit most likely to leave a program that still
    ///     type-checks), delete a run, duplicate a run, splice in a run from
    ///     another program, or swap two tokens.
    /// </summary>
    static List<Piece> Mutate(Random random, List<Piece> parent, List<List<Piece>> pool, Dictionary<SyntaxKind, Piece[]> vocabulary) {
        var tokens = new List<Piece>(parent);
        var edits = random.Next(4) == 0 ? random.Next(2, 5) : 1;
        for (; edits > 0; edits--) {
            var at = random.Next(tokens.Count + 1);
            var length = Math.Min(random.Next(1, 9), tokens.Count - Math.Min(at, tokens.Count));
            switch (random.Next(8)) {
                case 0 or 1 or 2 when at < tokens.Count && vocabulary.TryGetValue(tokens[at].Kind, out var kin):
                    // Keep this token's trivia: only the token's own text changes.
                    tokens[at] = tokens[at] with { Core = kin[random.Next(kin.Length)].Core };
                    break;
                case 3 when length > 0:
                    tokens.RemoveRange(at, length);
                    break;
                case 4 when length > 0:
                    tokens.InsertRange(at, tokens.GetRange(at, length));
                    break;
                case 5: {
                    var donor = pool[random.Next(pool.Count)];
                    var from = random.Next(donor.Count);
                    tokens.InsertRange(at, donor.GetRange(from, Math.Min(random.Next(1, 25), donor.Count - from)));
                    break;
                }
                case 6 when tokens.Count > 1: {
                    var other = random.Next(tokens.Count);
                    var here = Math.Min(at, tokens.Count - 1);
                    (tokens[here], tokens[other]) = (tokens[other], tokens[here]);
                    break;
                }
                case 7 when at < tokens.Count: {
                    // Any token at all, of any kind.
                    var kinds = vocabulary.Keys.ToArray();
                    var kin = vocabulary[kinds[random.Next(kinds.Length)]];
                    tokens.Insert(at, kin[random.Next(kin.Length)]);
                    break;
                }
            }

            if (tokens.Count == 0) {
                tokens.Add(parent[0]);
            }
        }

        return tokens;
    }

    /// <summary>A program as its tokens, each with its trivia, so that putting them back together gives the text.</summary>
    static List<Piece> Pieces(string source) =>
        [..SyntaxTree.Parse(new SourceText(source)).Root.DescendantTokens()
            .Select(static t => new Piece(Text(t.LeadingTrivia), t.Text, Text(t.TrailingTrivia), t.Kind))
            .Where(static p => p.Text.Length > 0)];

    static string Text(SyntaxTriviaList trivia) => string.Concat(trivia.Select(static t => t.Text));

    /// <summary>The programs mutants start from: every single-file program in the repository.</summary>
    static IEnumerable<string> Seeds() {
        foreach (var directory in new[] { "tests/corpus", "tests/programs", "tests/negative", "examples" }) {
            foreach (var file in Directory.GetFiles(Path.Combine(Root, directory), "*.sb").Order(StringComparer.Ordinal)) {
                yield return File.ReadAllText(file);
            }
        }
    }

    /// <summary>
    ///     Every kind of syntax node is in some seed. Mutants are cut from the
    ///     seeds and spliced with their pieces, so syntax no seed uses is
    ///     syntax the fuzzer never writes; new syntax needs a seed that uses
    ///     it (tests/corpus/19-syntax-coverage.sb and
    ///     tests/negative/75-syntax-coverage.sb are where the otherwise unused
    ///     forms go).
    /// </summary>
    [Fact]
    public void EveryKindOfNodeIsInASeed() {
        var seen = Seeds()
            .SelectMany(static source => SyntaxTree.Parse(source).Root.DescendantNodesAndSelf())
            .Select(static node => node.GetType())
            .ToHashSet();
        var missing = typeof(SyntaxNode).Assembly.GetTypes()
            .Where(static t => t.IsPublic && !t.IsAbstract && t.IsSubclassOf(typeof(SyntaxNode)))
            .Where(t => !seen.Contains(t))
            .Select(static t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0, "no seed program uses: " + string.Join(", ", missing));
    }

    static string Reproduce(string source) =>
        $"SABLE_FUZZ_SEED={Environment.GetEnvironmentVariable("SABLE_FUZZ_SEED") ?? "(unset)"}; input:\n{source}";

    static string Root {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sable.slnx"))) {
                directory = directory.Parent;
            }

            return directory!.FullName;
        }
    }
}
