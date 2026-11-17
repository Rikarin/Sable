using System.Collections;
using System.Collections.Immutable;

namespace Sable.Symbols;

/// <summary>
///     Computes <see cref="DataFlowAnalysis" /> and
///     <see cref="ControlFlowAnalysis" /> for one region of a bound body.
/// </summary>
internal static class RegionFlow {
    /// <summary>
    ///     What a run of statements declares, reads, writes and captures, and
    ///     what flows across its boundary.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Declared, read, written and captured are facts about the region's
    ///         own text, collected by walking it.
    ///     </para>
    ///     <para>
    ///         Flowing in and out are facts about control flow, so they are
    ///         reaching definitions over the whole body's
    ///         <see cref="ControlFlowGraph" />. A binding flows in when a read
    ///         inside can see a value written outside — or the value it had on
    ///         entry, for a parameter or a capture. It flows out when a read
    ///         outside can see a value written inside, wherever that read is: after
    ///         the region, or before it round a loop. A value written before the
    ///         region and overwritten in it before any read does not flow in, and
    ///         one overwritten after it before any read does not flow out.
    ///     </para>
    /// </remarks>
    public static DataFlowAnalysis Data(IReadOnlyList<BoundStatement> region, BoundBlock body) {
        var declared = new List<Symbol>();
        var read = new List<Symbol>();
        var written = new List<Symbol>();
        var captured = new List<Symbol>();

        foreach (var statement in region) {
            Collect(statement, declared, read, written, captured, false);
        }

        var inside = new HashSet<BoundNode>(ReferenceEqualityComparer.Instance);
        foreach (var statement in region) {
            AddAll(statement, inside);
        }

        var (flowsIn, flowsOut) = Flows(ControlFlowGraph.Build(body), inside);

        return new() {
            VariablesDeclared = Unique(declared),
            ReadInside = Unique(read),
            WrittenInside = Unique(written),
            Captured = Unique(captured),
            DataFlowsIn = Unique(read.Where(flowsIn.Contains)),
            DataFlowsOut = Unique(written.Where(flowsOut.Contains)),
            Succeeded = true
        };
    }

    /// <summary>How control enters and leaves a run of statements.</summary>
    public static ControlFlowAnalysis Control(IReadOnlyList<BoundStatement> region) {
        var graph = ControlFlowGraph.Build(AsOne(region));

        return new() {
            ExitPoints = [..graph.ExitingJumps.OrderBy(static s => s.Syntax.Span.Start)],
            ReturnStatements = [
                ..graph.ExitingJumps.OfType<BoundReturn>()
                    .Cast<BoundStatement>()
                    .OrderBy(static s => s.Syntax.Span.Start)
            ],
            EndPointIsReachable = graph.EndIsReachable,
            Succeeded = true
        };
    }

    /// <summary>A run of statements as one, so a graph can be built for it.</summary>
    static BoundStatement AsOne(IReadOnlyList<BoundStatement> region) =>
        region.Count == 1 ? region[0] : new BoundBlock(region[0].Syntax, [], [..region]);

    static void AddAll(BoundNode node, HashSet<BoundNode> into) {
        into.Add(node);
        foreach (var child in node.Children) {
            AddAll(child, into);
        }

        // A catch clause's pieces are not among a 'do''s children.
        if (node is BoundDo doStatement) {
            foreach (var clause in doStatement.Catches) {
                if (clause.Pattern is { } pattern) {
                    AddAll(pattern, into);
                }

                if (clause.Guard is { } guard) {
                    AddAll(guard, into);
                }
            }
        }
    }

    /// <summary>
    ///     Reaching definitions over the graph, then the reads that cross the
    ///     region's boundary.
    /// </summary>
    static (HashSet<Symbol> In, HashSet<Symbol> Out) Flows(ControlFlowGraph graph, HashSet<BoundNode> inside) {
        // Every write is a definition. A binding the body never declares — a
        // parameter, or a capture from outside — also has one on entry, which
        // is outside any region.
        var definitions = new List<(Symbol Symbol, bool Inside)>();
        var definitionAt = new Dictionary<(BasicBlock, int), int>();
        var declaredHere = new HashSet<Symbol>();
        var symbols = new HashSet<Symbol>();

        foreach (var (block, index, op) in graph.AllOps()) {
            if (op.Symbol is not { } symbol) {
                continue;
            }

            symbols.Add(symbol);
            if (op.Kind == FlowOpKind.Declare) {
                declaredHere.Add(symbol);
            }

            if (op.Kind == FlowOpKind.Write) {
                definitionAt[(block, index)] = definitions.Count;
                definitions.Add((symbol, inside.Contains(op.Node)));
            }
        }

        // A closure's or local function's own parameters are written where
        // its body starts, not on entry to the function around it.
        var nestedParameters = new HashSet<Symbol>();
        foreach (var (_, _, op) in graph.AllOps()) {
            if (op is { Kind: FlowOpKind.Write, Symbol: ParameterSymbol parameter, Node: BoundClosure or BoundLocalFunction }) {
                nestedParameters.Add(parameter);
            }
        }

        var entryDefinitions = new Dictionary<Symbol, int>();
        foreach (var symbol in symbols) {
            if (nestedParameters.Contains(symbol)) {
                continue;
            }

            if (symbol is ParameterSymbol || !declaredHere.Contains(symbol)) {
                entryDefinitions[symbol] = definitions.Count;
                definitions.Add((symbol, false));
            }
        }

        var bySymbol = new Dictionary<Symbol, List<int>>();
        for (var i = 0; i < definitions.Count; i++) {
            if (!bySymbol.TryGetValue(definitions[i].Symbol, out var list)) {
                bySymbol[definitions[i].Symbol] = list = [];
            }

            list.Add(i);
        }

        var reachable = graph.Reachable();
        var count = definitions.Count;
        var outSets = new BitArray[graph.Blocks.Count];
        for (var i = 0; i < outSets.Length; i++) {
            outSets[i] = new BitArray(count);
        }

        BitArray InOf(BasicBlock block) {
            var state = new BitArray(count);
            if (ReferenceEquals(block, graph.Entry)) {
                foreach (var definition in entryDefinitions.Values) {
                    state[definition] = true;
                }
            }

            foreach (var predecessor in block.Predecessors) {
                if (reachable.Contains(predecessor)) {
                    state.Or(outSets[predecessor.Ordinal]);
                }
            }

            return state;
        }

        void Kill(BitArray state, Symbol symbol) {
            foreach (var definition in bySymbol.GetValueOrDefault(symbol) ?? []) {
                state[definition] = false;
            }
        }

        // A "may" problem: union at joins, iterated to a fixpoint.
        var changed = true;
        while (changed) {
            changed = false;
            foreach (var block in graph.Blocks) {
                if (!reachable.Contains(block)) {
                    continue;
                }

                var state = InOf(block);
                for (var i = 0; i < block.Ops.Count; i++) {
                    var op = block.Ops[i];
                    if (op.Symbol is null) {
                        continue;
                    }

                    if (op.Kind is FlowOpKind.Declare or FlowOpKind.Write) {
                        Kill(state, op.Symbol);
                    }

                    if (op.Kind == FlowOpKind.Write) {
                        state[definitionAt[(block, i)]] = true;
                    }
                }

                if (!SameBits(state, outSets[block.Ordinal])) {
                    outSets[block.Ordinal] = state;
                    changed = true;
                }
            }
        }

        var flowsIn = new HashSet<Symbol>();
        var flowsOut = new HashSet<Symbol>();

        foreach (var block in graph.Blocks) {
            if (!reachable.Contains(block)) {
                continue;
            }

            var state = InOf(block);
            for (var i = 0; i < block.Ops.Count; i++) {
                var op = block.Ops[i];
                if (op.Symbol is not { } symbol) {
                    continue;
                }

                switch (op.Kind) {
                    case FlowOpKind.Read: {
                        var readInside = inside.Contains(op.Node);
                        foreach (var definition in bySymbol.GetValueOrDefault(symbol) ?? []) {
                            if (!state[definition]) {
                                continue;
                            }

                            if (readInside && !definitions[definition].Inside) {
                                flowsIn.Add(symbol);
                            } else if (!readInside && definitions[definition].Inside) {
                                flowsOut.Add(symbol);
                            }
                        }

                        break;
                    }

                    case FlowOpKind.Declare:
                        Kill(state, symbol);
                        break;

                    case FlowOpKind.Write:
                        Kill(state, symbol);
                        state[definitionAt[(block, i)]] = true;
                        break;
                }
            }
        }

        // An 'inout' or 'out' parameter is read by the caller when the
        // function ends, so a value written inside that reaches the end
        // flows out too.
        foreach (var exit in new[] { graph.End, graph.Return }) {
            if (!reachable.Contains(exit)) {
                continue;
            }

            var state = InOf(exit);
            for (var definition = 0; definition < count; definition++) {
                if (state[definition]
                    && definitions[definition].Inside
                    && definitions[definition].Symbol is ParameterSymbol {
                        Modifier: ParameterModifier.InOut or ParameterModifier.Out
                    } parameter) {
                    flowsOut.Add(parameter);
                }
            }
        }

        return (flowsIn, flowsOut);
    }

    static bool SameBits(BitArray a, BitArray b) {
        for (var i = 0; i < a.Length; i++) {
            if (a[i] != b[i]) {
                return false;
            }
        }

        return true;
    }

    static ImmutableArray<Symbol> Unique(IEnumerable<Symbol> symbols) {
        var seen = new HashSet<Symbol>();
        var result = ImmutableArray.CreateBuilder<Symbol>();
        foreach (var symbol in symbols) {
            if (seen.Add(symbol)) {
                result.Add(symbol);
            }
        }

        return result.ToImmutable();
    }

    static void Collect(
        BoundNode node,
        List<Symbol> declared,
        List<Symbol> read,
        List<Symbol> written,
        List<Symbol> captured,
        bool insideClosure
    ) {
        switch (node) {
            case BoundLocalDeclaration declaration:
                declared.Add(declaration.Local);
                if (declaration.Initializer is { } initializer) {
                    written.Add(declaration.Local);
                    Collect(initializer, declared, read, written, captured, insideClosure);
                }

                return;

            case BoundPatternDeclaration declaration:
                Collect(declaration.Initializer, declared, read, written, captured, insideClosure);
                DeclarePattern(declaration.Pattern, declared, written);
                return;

            case BoundLocalAccess access:
                read.Add(access.Local);
                // A name the closure declared itself is not a capture.
                if (insideClosure && !declared.Contains(access.Local)) {
                    captured.Add(access.Local);
                }

                return;

            case BoundParameterAccess access:
                read.Add(access.Parameter);
                if (insideClosure && !declared.Contains(access.Parameter)) {
                    captured.Add(access.Parameter);
                }

                return;

            case BoundAssignment assignment: {
                Collect(assignment.Value, declared, read, written, captured, insideClosure);

                switch (assignment.Target) {
                    case BoundLocalAccess target:
                        written.Add(target.Local);
                        if (insideClosure && !declared.Contains(target.Local)) {
                            captured.Add(target.Local);
                        }

                        return;

                    case BoundParameterAccess target:
                        written.Add(target.Parameter);
                        if (insideClosure && !declared.Contains(target.Parameter)) {
                            captured.Add(target.Parameter);
                        }

                        return;

                    default:
                        // 'q.x = 1' writes q, since a struct's field is part
                        // of it; the receiver itself is still evaluated.
                        if (BaseBinding(assignment.Target) is { } owner) {
                            written.Add(owner);
                            if (insideClosure && !declared.Contains(owner)) {
                                captured.Add(owner);
                            }

                            return;
                        }

                        Collect(assignment.Target, declared, read, written, captured, insideClosure);
                        return;
                }
            }

            case BoundClosure closure:
                foreach (var parameter in closure.Symbol.Parameters) {
                    declared.Add(parameter);
                    written.Add(parameter);
                }

                foreach (var capture in closure.Captures) {
                    if (capture.Initializer is { } value) {
                        Collect(value, declared, read, written, captured, insideClosure);
                    }

                    // '[copied = n]' declares a name of its own.
                    if (capture.CaptureKind == CaptureKind.ByValue
                        && capture.Symbol is LocalSymbol introduced) {
                        declared.Add(introduced);
                        written.Add(introduced);
                    }

                    captured.Add(capture.Symbol);
                }

                Collect(closure.Body, declared, read, written, captured, true);
                return;

            case BoundForEach loop:
                if (loop.Element is { } element) {
                    declared.Add(element);
                    written.Add(element);
                }

                break;

            case BoundConditionBinding binding:
                if (binding.Local is { } bound) {
                    declared.Add(bound);
                    written.Add(bound);
                }

                if (binding.Pattern is { } conditionPattern) {
                    DeclarePattern(conditionPattern, declared, written);
                }

                break;

            case BoundMatch match:
                foreach (var arm in match.Arms) {
                    foreach (var pattern in arm.Patterns) {
                        DeclarePattern(pattern, declared, written);
                    }
                }

                break;

            case BoundBlock block:
                foreach (var local in block.Locals) {
                    _ = local;
                }

                break;
        }

        foreach (var child in node.Children) {
            Collect(child, declared, read, written, captured, insideClosure);
        }
    }

    /// <summary>The binding a member or element access ultimately reads from.</summary>
    static Symbol? BaseBinding(BoundExpression expression) =>
        expression switch {
            BoundLocalAccess local => local.Local,
            BoundParameterAccess parameter => parameter.Parameter,
            BoundFieldAccess { Receiver: { } receiver } => BaseBinding(receiver),
            BoundIndexAccess index => BaseBinding(index.Receiver),
            BoundTupleElementAccess tuple => BaseBinding(tuple.Receiver),
            _ => null,
        };

    /// <summary>Records every name a pattern brings into scope.</summary>
    static void DeclarePattern(
        BoundPattern pattern,
        List<Symbol> declared,
        List<Symbol> written
    ) {
        switch (pattern) {
            case BoundBindingPattern binding:
                declared.Add(binding.Local);
                written.Add(binding.Local);
                break;

            case BoundTypeTestPattern { Local: { } tested }:
                declared.Add(tested);
                written.Add(tested);
                break;
        }

        foreach (var child in pattern.Children) {
            if (child is BoundPattern nested) {
                DeclarePattern(nested, declared, written);
            }
        }
    }
}
