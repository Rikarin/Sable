namespace Sable.Symbols;

/// <summary>
///     Reachability and definite return, read off the control-flow graph.
/// </summary>
/// <remarks>
///     This is the definite-return half of docs/04 §1.3. Both questions are
///     the same question about <see cref="ControlFlowGraph" />: can control get
///     there from the start? A statement nothing reaches is dead; a body whose
///     end nothing reaches always exits — by returning, throwing, jumping out
///     of the statement sequence, or calling something that returns
///     <c>Never</c>.
/// </remarks>
internal static class FlowAnalysis {
    /// <summary>
    ///     True when control cannot fall off the end of the statement: every
    ///     way through it returns, throws, breaks, continues or never comes back.
    /// </summary>
    public static bool AlwaysExits(BoundStatement statement) => !ControlFlowGraph.Build(statement).EndIsReachable;

    /// <summary>The first statement, in source order, that control can never reach.</summary>
    public static BoundStatement? FirstUnreachable(BoundStatement body) => FirstUnreachable(ControlFlowGraph.Build(body));

    public static BoundStatement? FirstUnreachable(ControlFlowGraph graph) {
        var reachable = graph.Reachable();
        BoundStatement? first = null;

        foreach (var (block, _, op) in graph.AllOps()) {
            if (op.Kind != FlowOpKind.Statement || reachable.Contains(block)) {
                continue;
            }

            if (op.Node is not BoundStatement statement) {
                continue;
            }

            if (first is null || statement.Syntax.Span.Start < first.Syntax.Span.Start) {
                first = statement;
            }
        }

        return first;
    }
}
