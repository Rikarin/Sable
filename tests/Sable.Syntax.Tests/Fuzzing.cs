namespace Sable.Syntax.Tests;

/// <summary>
///     The random-input tests' settings. Unset, each test runs its own fixed
///     seed for its usual count, as every build does. The nightly fuzz run
///     (.github/workflows/fuzz.yml) sets <c>SABLE_FUZZ_SEED</c>, so that each
///     night's inputs are new, and <c>SABLE_FUZZ_SCALE</c>, to run many times
///     as many; a failure names the seed, which reproduces it.
/// </summary>
internal static class Fuzzing {
    static int? Setting(string name) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : null;

    /// <summary>The generator a test draws from: its own seed, moved by the night's when there is one.</summary>
    public static Random Random(int fixedSeed) =>
        new(Setting("SABLE_FUZZ_SEED") is { } night ? unchecked(fixedSeed * 31 + night) : fixedSeed);

    /// <summary>How many inputs to try: the test's count, times the night's scale.</summary>
    public static int Iterations(int usual) => usual * Math.Max(1, Setting("SABLE_FUZZ_SCALE") ?? 1);

    /// <summary>
    ///     Every keyword and every fixed-text token the lexer knows, taken from
    ///     its own tables, so that a keyword or operator added to the language
    ///     is in the soup without anyone remembering to add it.
    /// </summary>
    public static IReadOnlyList<string> EveryFixedToken { get; } =
        SyntaxFacts.KeywordTexts
            .Concat(Enum.GetValues<SyntaxKind>().Select(SyntaxFacts.GetText).OfType<string>())
            .Where(static text => text.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    ///     Checks each input on a thread of its own and fails, naming the
    ///     input, if one takes longer than <paramref name="stall" /> (ten
    ///     seconds unless said): a parser that stops making progress fails
    ///     the test, rather than wedging the run until the job's own limit
    ///     cancels it with no word of which input it was on.
    /// </summary>
    public static void Guarded(IEnumerable<string> inputs, Action<string> check, TimeSpan? stall = null) {
        var limit = stall ?? TimeSpan.FromSeconds(10);
        var current = "";
        var done = 0L;
        Exception? failure = null;
        var finished = new ManualResetEventSlim();

        void Work() {
            try {
                foreach (var input in inputs) {
                    Volatile.Write(ref current, input);
                    check(input);
                    Interlocked.Increment(ref done);
                }
            } catch (Exception e) {
                failure = e;
            } finally {
                finished.Set();
            }
        }

        // A wedged parse cannot be stopped; a background thread at least
        // lets the test host exit once the test has failed.
        var worker = new Thread(Work, 64 * 1024 * 1024) { IsBackground = true };
        worker.Start();

        var seen = -1L;
        while (!finished.Wait(limit)) {
            var now = Interlocked.Read(ref done);
            if (now == seen) {
                Assert.Fail($"no answer within {limit.TotalSeconds}s\n  {Reproduce(Volatile.Read(ref current))}");
            }

            seen = now;
        }

        if (failure is not null) {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>What a failure says, so that it can be run again.</summary>
    public static string Reproduce(string source) =>
        $"SABLE_FUZZ_SEED={Environment.GetEnvironmentVariable("SABLE_FUZZ_SEED") ?? "(unset)"}; input: {System.Text.Json.JsonSerializer.Serialize(source)}";
}
