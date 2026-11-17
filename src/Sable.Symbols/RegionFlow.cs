using System.Collections.Immutable;

namespace Sable.Symbols;

/// <summary>
/// Computes <see cref="DataFlowAnalysis"/> and
/// <see cref="ControlFlowAnalysis"/> for one region of a bound body.
/// </summary>
internal static class RegionFlow
{
    public static DataFlowAnalysis Data(BoundNode region, BoundBlock body)
    {
        var declared = new List<Symbol>();
        var read = new List<Symbol>();
        var written = new List<Symbol>();
        var captured = new List<Symbol>();

        Collect(region, declared, read, written, captured, insideClosure: false);

        // What the rest of the body does, for the "flows in/out" questions.
        var outsideRead = new List<Symbol>();
        var outsideWritten = new List<Symbol>();
        CollectOutside(body, region, outsideRead, outsideWritten);

        var declaredSet = new HashSet<Symbol>(declared);

        return new DataFlowAnalysis
        {
            VariablesDeclared = Unique(declared),
            ReadInside = Unique(read),
            WrittenInside = Unique(written),
            Captured = Unique(captured),

            // Read inside but declared elsewhere: an extracted method's inputs.
            DataFlowsIn = Unique(read.Where(symbol => !declaredSet.Contains(symbol))),

            // Written inside, declared elsewhere, and read again afterwards.
            DataFlowsOut = Unique(written
                .Where(symbol => !declaredSet.Contains(symbol))
                .Where(outsideRead.Contains)),

            Succeeded = true,
        };
    }

    public static ControlFlowAnalysis Control(BoundNode region)
    {
        var exits = ImmutableArray.CreateBuilder<BoundStatement>();
        var returns = ImmutableArray.CreateBuilder<BoundStatement>();
        CollectExits(region, exits, returns);

        return new ControlFlowAnalysis
        {
            ExitPoints = exits.ToImmutable(),
            ReturnStatements = returns.ToImmutable(),
            EndPointIsReachable =
                region is not BoundStatement statement || !FlowAnalysis.AlwaysExits(statement),
            Succeeded = true,
        };
    }

    private static ImmutableArray<Symbol> Unique(IEnumerable<Symbol> symbols)
    {
        var seen = new HashSet<Symbol>();
        var result = ImmutableArray.CreateBuilder<Symbol>();
        foreach (var symbol in symbols)
            if (seen.Add(symbol)) result.Add(symbol);
        return result.ToImmutable();
    }

    private static void Collect(
        BoundNode node,
        List<Symbol> declared,
        List<Symbol> read,
        List<Symbol> written,
        List<Symbol> captured,
        bool insideClosure)
    {
        switch (node)
        {
            case BoundLocalDeclaration declaration:
                declared.Add(declaration.Local);
                if (declaration.Initializer is { } initializer)
                {
                    written.Add(declaration.Local);
                    Collect(initializer, declared, read, written, captured, insideClosure);
                }
                return;

            case BoundLocalAccess access:
                read.Add(access.Local);
                if (insideClosure) captured.Add(access.Local);
                return;

            case BoundParameterAccess access:
                read.Add(access.Parameter);
                if (insideClosure) captured.Add(access.Parameter);
                return;

            case BoundAssignment assignment:
            {
                Collect(assignment.Value, declared, read, written, captured, insideClosure);

                switch (assignment.Target)
                {
                    case BoundLocalAccess target:
                        written.Add(target.Local);
                        if (insideClosure) captured.Add(target.Local);
                        return;
                    case BoundParameterAccess target:
                        written.Add(target.Parameter);
                        if (insideClosure) captured.Add(target.Parameter);
                        return;
                    default:
                        Collect(
                            assignment.Target, declared, read, written, captured, insideClosure);
                        return;
                }
            }

            case BoundClosure closure:
                foreach (var capture in closure.Captures)
                {
                    if (capture.Initializer is { } value)
                        Collect(value, declared, read, written, captured, insideClosure);
                    captured.Add(capture.Symbol);
                }
                Collect(closure.Body, declared, read, written, captured, insideClosure: true);
                return;

            case BoundForEach loop:
                if (loop.Element is { } element) declared.Add(element);
                break;

            case BoundBlock block:
                foreach (var local in block.Locals) _ = local;
                break;
        }

        foreach (var child in node.Children)
            Collect(child, declared, read, written, captured, insideClosure);
    }

    /// <summary>Walks everything but the region, for what happens around it.</summary>
    private static void CollectOutside(
        BoundNode node, BoundNode region, List<Symbol> read, List<Symbol> written)
    {
        if (ReferenceEquals(node, region)) return;

        switch (node)
        {
            case BoundLocalAccess access:
                read.Add(access.Local);
                return;

            case BoundParameterAccess access:
                read.Add(access.Parameter);
                return;

            case BoundAssignment { Target: BoundLocalAccess target } assignment:
                CollectOutside(assignment.Value, region, read, written);
                written.Add(target.Local);
                return;
        }

        foreach (var child in node.Children) CollectOutside(child, region, read, written);
    }

    private static void CollectExits(
        BoundNode node,
        ImmutableArray<BoundStatement>.Builder exits,
        ImmutableArray<BoundStatement>.Builder returns)
    {
        switch (node)
        {
            case BoundReturn returnStatement:
                exits.Add(returnStatement);
                returns.Add(returnStatement);
                return;

            case BoundThrow or BoundBreak or BoundContinue:
                exits.Add((BoundStatement)node);
                return;

            // A closure's exits are its own.
            case BoundClosure or BoundLocalFunction:
                return;
        }

        foreach (var child in node.Children) CollectExits(child, exits, returns);
    }
}
