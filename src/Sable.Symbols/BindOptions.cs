namespace Sable.Symbols;

/// <summary>
/// What the semantic layer is allowed to spend on one expression.
/// </summary>
/// <remarks>
/// docs/03 §3.5 asks for a hard limit on solver steps from the first commit,
/// and docs/04 §1.3 repeats it. Swift's "expression too complex" errors come
/// from not having designed for this: without a limit an unlucky expression
/// takes unbounded time, and the user is told only that it failed. A limit
/// with a diagnostic naming the subexpression that ran out, and suggesting an
/// annotation, is strictly better than an exponential type checker.
/// </remarks>
public sealed record BindOptions
{
    public static readonly BindOptions Default = new();

    /// <summary>
    /// How many expressions one statement may bind before the compiler gives
    /// up on it. Counted per statement, so a long body costs nothing extra.
    /// </summary>
    public int StepBudget { get; init; } = 10_000;
}
