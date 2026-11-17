using System.Collections.Immutable;

namespace Sable.Symbols;

/// <summary>
/// What a region of code does with the names around it.
/// </summary>
/// <remarks>
/// docs/04 §1.1 lists <c>AnalyzeDataFlow</c> among the things the
/// <see cref="SemanticModel"/> owes its consumers: "extract method" needs to
/// know what a selection reads and writes, the linter needs to know what is
/// written and never read, and the language server needs both to highlight
/// usages.
/// </remarks>
public sealed class DataFlowAnalysis
{
    /// <summary>Locals declared inside the region.</summary>
    public required ImmutableArray<Symbol> VariablesDeclared { get; init; }

    /// <summary>Names the region reads, wherever they were declared.</summary>
    public required ImmutableArray<Symbol> ReadInside { get; init; }

    /// <summary>Names the region assigns to.</summary>
    public required ImmutableArray<Symbol> WrittenInside { get; init; }

    /// <summary>Names a closure in the region captures.</summary>
    public required ImmutableArray<Symbol> Captured { get; init; }

    /// <summary>
    /// Names declared outside and read inside: what an extracted method would
    /// have to take as parameters.
    /// </summary>
    public required ImmutableArray<Symbol> DataFlowsIn { get; init; }

    /// <summary>
    /// Names declared outside, written inside, and read after: what an
    /// extracted method would have to give back.
    /// </summary>
    public required ImmutableArray<Symbol> DataFlowsOut { get; init; }

    /// <summary>False when the region could not be analysed.</summary>
    public required bool Succeeded { get; init; }

    internal static DataFlowAnalysis Failed { get; } = new()
    {
        VariablesDeclared = [],
        ReadInside = [],
        WrittenInside = [],
        Captured = [],
        DataFlowsIn = [],
        DataFlowsOut = [],
        Succeeded = false,
    };
}

/// <summary>How control enters and leaves a region.</summary>
public sealed class ControlFlowAnalysis
{
    /// <summary>Statements that leave the region early.</summary>
    public required ImmutableArray<BoundStatement> ExitPoints { get; init; }

    /// <summary>The <c>return</c> statements in the region.</summary>
    public required ImmutableArray<BoundStatement> ReturnStatements { get; init; }

    /// <summary>False when control cannot fall off the end of the region.</summary>
    public required bool EndPointIsReachable { get; init; }

    /// <summary>False when the region could not be analysed.</summary>
    public required bool Succeeded { get; init; }

    internal static ControlFlowAnalysis Failed { get; } = new()
    {
        ExitPoints = [],
        ReturnStatements = [],
        EndPointIsReachable = true,
        Succeeded = false,
    };
}
