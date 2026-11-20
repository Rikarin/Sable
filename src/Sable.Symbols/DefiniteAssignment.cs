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
    /// <param name="self">
    ///     For an initialiser, the fields its body has to set, built into the
    ///     graph as steps: each is tracked as a local is, every use of self
    ///     as a whole needs them all, and so does every way out but a throw.
    /// </param>
    /// <param name="initializer">Where an initialiser that leaves a field unset is reported.</param>
    public static void Analyze(
        ControlFlowGraph graph,
        DiagnosticBag diagnostics,
        Symbol? owner = null,
        InitializedSelf? self = null,
        TextSpan initializer = default
    ) {
        // Locals, an initialiser's fields, and 'out' parameters, which come
        // in with no value and must go out with one. Any other parameter has
        // a value.
        var index = new Dictionary<Symbol, int>();
        foreach (var field in self?.Fields ?? []) {
            index[field] = index.Count;
        }

        if (self?.Whole is { } whole) {
            index[whole] = index.Count;
        }

        if (self?.Superclass is { } superclass) {
            index[superclass] = index.Count;
        }

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
                Transfer(block, state, index, self, null);

                if (!SameBits(state, assignedOut[block.Ordinal])) {
                    assignedOut[block.Ordinal] = state;
                    changed = true;
                }
            }
        }

        // Then once more, reporting the first read of each unassigned binding.
        var first = new Dictionary<Symbol, (BoundNode Node, string Message)>();
        foreach (var block in blocks) {
            if (!reachable.Contains(block)) {
                continue;
            }

            Transfer(block, In(block, graph, assignedOut, index.Count, reachable), index, self, first);
        }

        foreach (var (_, (node, message)) in first.OrderBy(static p => p.Value.Node.Syntax.Span.Start)) {
            diagnostics.Error(ErrorCode.ERR_UseBeforeAssignment, node.Syntax.Span, message);
        }

        if (self is not null) {
            ReportUnset(graph, assignedOut, index, self, reachable, diagnostics, initializer);
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
    ///     The fields an initialiser leaves unset on some way out of it: off
    ///     the end, or by a <c>return</c>. A throw builds nothing, so it need not.
    /// </summary>
    static void ReportUnset(
        ControlFlowGraph graph,
        BitArray[] assignedOut,
        Dictionary<Symbol, int> index,
        InitializedSelf self,
        HashSet<BasicBlock> reachable,
        DiagnosticBag diagnostics,
        TextSpan at
    ) {
        var missing = new List<string>();
        foreach (var field in self.Fields) {
            foreach (var exit in new[] { graph.End, graph.Return }) {
                if (reachable.Contains(exit) && !In(exit, graph, assignedOut, index.Count, reachable)[index[field]]) {
                    missing.Add(field.Name);
                    break;
                }
            }
        }

        // The superclass's part of the object: built by 'super.init(…)' on
        // every way out — or, by a body that never calls it, implicitly, when
        // all the superclass's 'init()' does is give its fields their
        // initial values.
        if (self.Superclass is { } superclass) {
            var called = graph.AllOps().Any(o => o.Op.Kind == FlowOpKind.Write && ReferenceEquals(o.Op.Symbol, superclass));
            var unbuilt = new[] { graph.End, graph.Return }
                .Where(reachable.Contains)
                .Any(exit => !In(exit, graph, assignedOut, index.Count, reachable)[index[superclass]]);
            if (unbuilt && (called || !self.SuperclassInitIsImplicit)) {
                diagnostics.Error(
                    ErrorCode.ERR_MissingSuperInit,
                    at,
                    called
                        ? $"'super.init' is not called on every path through this initialiser; '{superclass.Name}' is left unbuilt"
                        : $"this initialiser never calls 'super.init', which '{superclass.Name}' needs to build its part of the object"
                );
            }
        }

        if (self.Whole is { } whole) {
            foreach (var exit in new[] { graph.End, graph.Return }) {
                if (reachable.Contains(exit) && !In(exit, graph, assignedOut, index.Count, reachable)[index[whole]]) {
                    diagnostics.Error(
                        ErrorCode.ERR_MissingFieldInitializer,
                        at,
                        "'self' is not given a value on every path through this initialiser: assign 'self = …', or call 'self.init(…)'"
                    );
                    break;
                }
            }
        }

        if (missing.Count == 0) {
            return;
        }

        diagnostics.Error(
            ErrorCode.ERR_MissingFieldInitializer,
            at,
            missing.Count == 1
                ? $"'{missing[0]}' is not initialised on every path through this initialiser"
                : $"'{string.Join("', '", missing)}' are not initialised on every path through this initialiser"
        );
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
        InitializedSelf? self,
        Dictionary<Symbol, (BoundNode Node, string Message)>? report
    ) {
        foreach (var op in block.Ops) {
            // Self as a whole: a method called on it, or self passed on, may
            // read any field, so all of them must have their values by now.
            if (self is not null && ReferenceEquals(op.Symbol, self.Type) && op.Kind == FlowOpKind.Read) {
                if (report is not null && !report.ContainsKey(self.Type)) {
                    var unset = self.Fields.Where(f => !state[index[f]]).Select(static f => f.Name).ToList();
                    if (unset.Count > 0) {
                        report[self.Type] = (op.Node, $"'self' is used here before {Names(unset)} {(unset.Count == 1 ? "is" : "are")} initialised");
                    } else if (self.Whole is { } whole && !state[index[whole]]) {
                        report[self.Type] = (op.Node, "'self' is used here before the initialiser gives it a value");
                    }
                }

                continue;
            }

            if (op.Symbol is not { } symbol || !index.TryGetValue(symbol, out var bit)) {
                continue;
            }

            switch (op.Kind) {
                case FlowOpKind.Declare:
                    state[bit] = false;
                    break;

                case FlowOpKind.Write:
                    state[bit] = true;
                    break;

                case FlowOpKind.Read when !state[bit] && report is not null:
                    if (!report.TryGetValue(symbol, out var earlier)
                        || op.Node.Syntax.Span.Start < earlier.Node.Syntax.Span.Start) {
                        report[symbol] = (op.Node, symbol is FieldSymbol
                            ? $"'{symbol.Name}' is read here before the initialiser assigns it"
                            : $"'{symbol.Name}' is read here before anything assigns to it");
                    }

                    break;
            }
        }
    }

    static string Names(List<string> names) => "'" + string.Join("', '", names) + "'";

    static bool SameBits(BitArray a, BitArray b) {
        for (var i = 0; i < a.Length; i++) {
            if (a[i] != b[i]) {
                return false;
            }
        }

        return true;
    }
}
