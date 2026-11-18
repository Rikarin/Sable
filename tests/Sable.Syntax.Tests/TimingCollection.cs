namespace Sable.Syntax.Tests;

/// <summary>
///     The tests that measure time: run alone, after the others, so that the
///     rest of the suite running in parallel does not slow what they measure.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection {
    public const string Name = "Timing";
}
