using System.Collections.Immutable;

namespace Sable.Symbols;

/// <summary>
///     Computes <see cref="DataFlowAnalysis" /> and
///     <see cref="ControlFlowAnalysis" /> for one region of a bound body.
/// </summary>
internal static class RegionFlow {
    public static DataFlowAnalysis Data(BoundNode region, BoundBlock body) {
        var declared = new List<Symbol>();
        var read = new List<Symbol>();
        var written = new List<Symbol>();
        var captured = new List<Symbol>();

        Collect(region, declared, read, written, captured, false);

        // What the rest of the body does, for the "flows in/out" questions.
        var outsideRead = new List<Symbol>();
        var outsideWritten = new List<Symbol>();
        CollectOutside(body, region, outsideRead, outsideWritten);

        var declaredSet = new HashSet<Symbol>(declared);

        return new() {
            VariablesDeclared = Unique(declared),
            ReadInside = Unique(read),
            WrittenInside = Unique(written),
            Captured = Unique(captured),

            // Read inside but declared elsewhere: an extracted method's inputs.
            DataFlowsIn = Unique(read.Where(symbol => !declaredSet.Contains(symbol))),

            // Written inside, declared elsewhere, and read again afterwards.
            DataFlowsOut = Unique(
                written
                    .Where(symbol => !declaredSet.Contains(symbol))
                    .Where(outsideRead.Contains)
            ),
            Succeeded = true
        };
    }

    public static ControlFlowAnalysis Control(BoundNode region) {
        var exits = ImmutableArray.CreateBuilder<BoundStatement>();
        var returns = ImmutableArray.CreateBuilder<BoundStatement>();
        CollectExits(region, exits, returns);

        return new() {
            ExitPoints = exits.ToImmutable(),
            ReturnStatements = returns.ToImmutable(),
            EndPointIsReachable =
                region is not BoundStatement statement || !FlowAnalysis.AlwaysExits(statement),
            Succeeded = true
        };
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

    /// <summary>Walks everything but the region, for what happens around it.</summary>
    static void CollectOutside(
        BoundNode node,
        BoundNode region,
        List<Symbol> read,
        List<Symbol> written
    ) {
        if (ReferenceEquals(node, region)) {
            return;
        }

        switch (node) {
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

        foreach (var child in node.Children) {
            CollectOutside(child, region, read, written);
        }
    }

    static void CollectExits(
        BoundNode node,
        ImmutableArray<BoundStatement>.Builder exits,
        ImmutableArray<BoundStatement>.Builder returns
    ) {
        switch (node) {
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

        foreach (var child in node.Children) {
            CollectExits(child, exits, returns);
        }
    }
}
