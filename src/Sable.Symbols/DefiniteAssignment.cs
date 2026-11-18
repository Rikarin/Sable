using System.Collections;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     Reports locals read before anything has written to them.
/// </summary>
/// <remarks>
///     <para>
///         This is the definite-assignment half of docs/04 §1.3: a forward
///         "must" problem over <see cref="ControlFlowGraph" />. A local is
///         assigned at a point when every path from the entry to it writes the
///         local after its last declaration. The meet at a join is
///         intersection, and blocks start at "everything assigned", so a loop's
///         back edge settles by iteration rather than by guessing — and a path
///         that cannot be reached constrains nothing.
///     </para>
///     <para>
///         It moves to SIL in Phase 2. Doing it here first is faster to build
///         and the tests carry over, which is the reason docs/04 asks for it in
///         this order.
///     </para>
/// </remarks>
internal static class DefiniteAssignment {
    /// <param name="owner">
    ///     The function whose body this is. A local function reads the locals
    ///     of the one around it, which were checked there, where it is
    ///     declared; here only its own are.
    /// </param>
    public static void Analyze(ControlFlowGraph graph, DiagnosticBag diagnostics, Symbol? owner = null) {
        // Locals are tracked, and 'out' parameters: they come in with no
        // value, and must go out with one. Any other parameter has a value.
        var index = new Dictionary<Symbol, int>();
        var outs = owner is FunctionSymbol function
            ? function.Parameters.Where(static p => p.Modifier == ParameterModifier.Out).ToList()
            : [];
        foreach (var parameter in outs) {
            index[parameter] = index.Count;
        }

        foreach (var (_, _, op) in graph.AllOps()) {
            if (op.Symbol is LocalSymbol local && !index.ContainsKey(local) && (owner is null || IsWithin(local, owner))) {
                index[local] = index.Count;
            }
        }

        if (index.Count == 0) {
            return;
        }

        var reachable = graph.Reachable();
        var blocks = graph.Blocks;

        var assignedOut = new BitArray[blocks.Count];
        for (var i = 0; i < blocks.Count; i++) {
            assignedOut[i] = new BitArray(index.Count, true);
        }

        // Iterate to a fixpoint, in block order — which is roughly source
        // order, so most bodies settle in one or two passes.
        var changed = true;
        while (changed) {
            changed = false;
            foreach (var block in blocks) {
                if (!reachable.Contains(block)) {
                    continue;
                }

                var state = In(block, graph, assignedOut, index.Count, reachable);
                Transfer(block, state, index, null);

                if (!SameBits(state, assignedOut[block.Ordinal])) {
                    assignedOut[block.Ordinal] = state;
                    changed = true;
                }
            }
        }

        // Then once more, reporting the first read of each unassigned local.
        var first = new Dictionary<Symbol, BoundNode>();
        foreach (var block in blocks) {
            if (!reachable.Contains(block)) {
                continue;
            }

            Transfer(block, In(block, graph, assignedOut, index.Count, reachable), index, first);
        }

        foreach (var (local, node) in first.OrderBy(static p => p.Value.Syntax.Span.Start)) {
            diagnostics.Error(
                ErrorCode.ERR_UseBeforeAssignment,
                node.Syntax.Span,
                $"'{local.Name}' is read here before anything assigns to it"
            );
        }

        // Every way back to the caller — a 'return', or falling off the end —
        // gives each 'out' parameter a value. A throw need not.
        foreach (var parameter in outs) {
            var unassigned = new[] { graph.Return, graph.End }
                .Where(reachable.Contains)
                .Any(exit => !In(exit, graph, assignedOut, index.Count, reachable)[index[parameter]]);
            if (unassigned && parameter.Location is { } at) {
                diagnostics.Error(
                    ErrorCode.ERR_OutNotAssigned,
                    at,
                    $"'out' parameter '{parameter.Name}' is not assigned on every path that returns; the caller reads it afterwards"
                );
            }
        }
    }

    /// <summary>
    ///     A local of the function analysed, or of a closure in it — not of
    ///     a function around it, nor of a local function in it, whose own
    ///     analysis reports it.
    /// </summary>
    static bool IsWithin(LocalSymbol local, Symbol owner) {
        for (var container = local.ContainingSymbol; container is not null; container = container.ContainingSymbol) {
            if (ReferenceEquals(container, owner)) {
                return true;
            }

            if (container is FunctionSymbol { FunctionKind: FunctionKind.Ordinary, Name.Length: > 0, ContainingSymbol: FunctionSymbol }) {
                return false;
            }
        }

        return false;
    }

    /// <summary>What holds on entry to a block: the meet of what its reachable predecessors leave.</summary>
    static BitArray In(
        BasicBlock block,
        ControlFlowGraph graph,
        BitArray[] assignedOut,
        int count,
        HashSet<BasicBlock> reachable
    ) {
        if (ReferenceEquals(block, graph.Entry)) {
            return new BitArray(count, false);
        }

        var state = new BitArray(count, true);
        foreach (var predecessor in block.Predecessors) {
            if (reachable.Contains(predecessor)) {
                state.And(assignedOut[predecessor.Ordinal]);
            }
        }

        return state;
    }

    static void Transfer(
        BasicBlock block,
        BitArray state,
        Dictionary<Symbol, int> index,
        Dictionary<Symbol, BoundNode>? report
    ) {
        foreach (var op in block.Ops) {
            if (op.Symbol is not { } local || !index.ContainsKey(local)) {
                continue;
            }

            var bit = index[local];
            switch (op.Kind) {
                case FlowOpKind.Declare:
                    state[bit] = false;
                    break;

                case FlowOpKind.Write:
                    state[bit] = true;
                    break;

                case FlowOpKind.Read when !state[bit] && report is not null:
                    if (!report.TryGetValue(local, out var earlier)
                        || op.Node.Syntax.Span.Start < earlier.Syntax.Span.Start) {
                        report[local] = op.Node;
                    }

                    break;
            }
        }
    }

    static bool SameBits(BitArray a, BitArray b) {
        for (var i = 0; i < a.Length; i++) {
            if (a[i] != b[i]) {
                return false;
            }
        }

        return true;
    }
}
