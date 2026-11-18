namespace Sable.SIL;

/// <summary>
///     What a program's effects gave, in the order it made them, for a later
///     run of the same program to replay rather than make again (see
///     <see cref="SilInterpreter.Journal" />).
/// </summary>
/// <remarks>
///     A program that the interpreter runs is deterministic but for these:
///     input, files, the environment, other processes, the clock. Given the
///     same answers it takes the same path, so a run that replays them ends
///     where the first did without touching the world — no file written
///     twice, no line read again, the clock where it was. Writing to the
///     program's own output is not one: it is part of the run's result.
/// </remarks>
public sealed class EffectJournal {
    internal sealed record Effect(string Name, object? Result, bool IoFailed, long LastStatus, string LastErrorOutput);

    internal List<Effect> Entries { get; } = [];

    /// <summary>How many effects are written down.</summary>
    public int Count => Entries.Count;

    /// <summary>Forgets the effects after the first <paramref name="count" />: those of a run whose statements are not kept.</summary>
    public void Truncate(int count) => Entries.RemoveRange(count, Entries.Count - count);

    public void Clear() => Entries.Clear();

    /// <summary>Whether a runtime operation is an effect the journal records.</summary>
    internal static bool Records(string builtin) =>
        builtin is not ("io_write" or "io_write_error" or "io_argument_count" or "io_argument")
        && (builtin.StartsWith("io_", StringComparison.Ordinal) || builtin.StartsWith("process_", StringComparison.Ordinal) || builtin.StartsWith("time_", StringComparison.Ordinal));
}
