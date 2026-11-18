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

    /// <summary>What a failure says, so that it can be run again.</summary>
    public static string Reproduce(string source) =>
        $"SABLE_FUZZ_SEED={Environment.GetEnvironmentVariable("SABLE_FUZZ_SEED") ?? "(unset)"}; input: {System.Text.Json.JsonSerializer.Serialize(source)}";
}
