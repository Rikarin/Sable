using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>What one step of a basic block does.</summary>
internal enum FlowOpKind {
    /// <summary>A statement starts here; marks where it is, for reachability.</summary>
    Statement,

    /// <summary>A binding's value is read.</summary>
    Read,

    /// <summary>A binding is given a value.</summary>
    Write,

    /// <summary>A binding comes into scope with no value, as on each pass round a loop.</summary>
    Declare,
}

/// <summary>One step: what happens, to which binding, and the bound node it came from.</summary>
internal readonly record struct FlowOp(FlowOpKind Kind, Symbol? Symbol, BoundNode Node);

/// <summary>A straight run of steps, entered only at the top and left only at the bottom.</summary>
internal sealed class BasicBlock(int ordinal) {
    public int Ordinal { get; } = ordinal;
    public List<FlowOp> Ops { get; } = [];
    public List<BasicBlock> Successors { get; } = [];
    public List<BasicBlock> Predecessors { get; } = [];

    public override string ToString() => $"B{Ordinal}";
}

/// <summary>
///     The control-flow graph of a bound body: basic blocks of reads, writes
///     and declarations, joined by every way control can go.
/// </summary>
/// <remarks>
///     <para>
///         docs/04 §1.3 asks for definite assignment, reachability and definite
///         return on the bound tree before SIL exists. Each used to be its own
///         walk over the tree's shape, and each approximated control flow
///         differently. This is the one place control flow is worked out — loops
///         with back edges, labelled jumps, <c>throw</c> to the nearest
///         <c>catch</c>, short-circuit operators, <c>try</c> fallbacks — and the
///         analyses are ordinary data-flow problems over it.
///     </para>
///     <para>
///         A closure or local function body is a side branch at the point it is
///         defined: control may go through it or round it, and its own
///         <c>return</c> leads back rather than out. That is how it reads what is
///         assigned where it is written, and how what it writes counts as
///         maybe-written afterwards.
///     </para>
/// </remarks>
internal sealed class ControlFlowGraph {
    public required BasicBlock Entry { get; init; }

    /// <summary>Where control arrives by falling off the end of the body.</summary>
    public required BasicBlock End { get; init; }

    /// <summary>Where a <c>return</c> goes.</summary>
    public required BasicBlock Return { get; init; }

    /// <summary>Where an error no <c>catch</c> in the body handles goes.</summary>
    public required BasicBlock Throw { get; init; }

    /// <summary>Where a <c>break</c> or <c>continue</c> to a loop outside the body goes.</summary>
    public required BasicBlock Escape { get; init; }

    public required IReadOnlyList<BasicBlock> Blocks { get; init; }

    /// <summary>
    ///     The jumps that leave the body: returns, uncaught throws, and breaks
    ///     and continues to loops outside it.
    /// </summary>
    public required IReadOnlyList<BoundStatement> ExitingJumps { get; init; }

    /// <summary>The bound node of every op, so a region can ask which ops are its own.</summary>
    public IEnumerable<(BasicBlock Block, int Index, FlowOp Op)> AllOps() {
        foreach (var block in Blocks) {
            for (var i = 0; i < block.Ops.Count; i++) {
                yield return (block, i, block.Ops[i]);
            }
        }
    }

    /// <summary>The blocks control can reach from the entry.</summary>
    public HashSet<BasicBlock> Reachable() {
        var seen = new HashSet<BasicBlock> { Entry };
        var pending = new Stack<BasicBlock>();
        pending.Push(Entry);

        while (pending.Count > 0) {
            foreach (var next in pending.Pop().Successors) {
                if (seen.Add(next)) {
                    pending.Push(next);
                }
            }
        }

        return seen;
    }

    /// <summary>True when control can fall off the end of the body.</summary>
    public bool EndIsReachable => Reachable().Contains(End);

    public static ControlFlowGraph Build(BoundStatement body, IEnumerable<Symbol>? parameters = null) =>
        new Builder().Build(body, parameters ?? []);

    // -----------------------------------------------------------------------

    sealed class Builder {
        readonly List<BasicBlock> _blocks = [];
        readonly List<BoundStatement> _exiting = [];

        BasicBlock _current = null!;
        BasicBlock _returnTarget = null!;
        BasicBlock _escape = null!;
        BasicBlock _throwExit = null!;

        /// <summary>The <c>catch</c> dispatches in scope, innermost last.</summary>
        List<BasicBlock> _throwTargets = [];

        /// <summary>The loops in scope, innermost last.</summary>
        List<Loop> _loops = [];

        /// <summary>True inside a closure or local function, whose jumps do not leave the body.</summary>
        int _nestedBodies;

        /// <summary>
        ///     The cases a <c>fallthrough</c> goes to, innermost <c>switch</c>
        ///     last: null in a last case, where the binder has reported it.
        /// </summary>
        List<BasicBlock?> _fallthroughs = [];

        /// <summary>A loop, or a <c>switch</c>, which a <c>break</c> leaves but a <c>continue</c> passes through: its Continue is null.</summary>
        sealed record Loop(string? Label, BasicBlock Break, BasicBlock? Continue);

        public ControlFlowGraph Build(BoundStatement body, IEnumerable<Symbol> parameters) {
            var entry = NewBlock();
            var end = NewBlock();
            _returnTarget = NewBlock();
            _throwExit = NewBlock();
            _escape = NewBlock();
            _current = entry;

            foreach (var parameter in parameters) {
                Op(FlowOpKind.Write, parameter, body);
            }

            Statement(body);
            Edge(_current, end);

            return new() {
                Entry = entry,
                End = end,
                Return = _returnTarget,
                Throw = _throwExit,
                Escape = _escape,
                Blocks = _blocks,
                ExitingJumps = _exiting,
            };
        }

        BasicBlock NewBlock() {
            var block = new BasicBlock(_blocks.Count);
            _blocks.Add(block);
            return block;
        }

        static void Edge(BasicBlock from, BasicBlock to) {
            from.Successors.Add(to);
            to.Predecessors.Add(from);
        }

        void Op(FlowOpKind kind, Symbol? symbol, BoundNode node) => _current.Ops.Add(new(kind, symbol, node));

        /// <summary>Control leaves for <paramref name="target" />; what follows is unreachable until something jumps to it.</summary>
        void Jump(BasicBlock target) {
            Edge(_current, target);
            _current = NewBlock();
        }

        // -------------------------------------------------------------------
        // Statements
        // -------------------------------------------------------------------

        void Statement(BoundStatement statement) {
            Op(FlowOpKind.Statement, null, statement);

            switch (statement) {
                case BoundBlock block:
                    foreach (var child in block.Statements) {
                        Statement(child);
                    }

                    return;

                case BoundLocalDeclaration declaration:
                    Op(FlowOpKind.Declare, declaration.Local, declaration);
                    if (declaration.Initializer is { } initializer) {
                        Expression(initializer);
                        Op(FlowOpKind.Write, declaration.Local, declaration);
                    }

                    return;

                case BoundPatternDeclaration declaration:
                    Expression(declaration.Initializer);
                    PatternWrites(declaration.Pattern);
                    return;

                case BoundExpressionStatement expression:
                    Expression(expression.Expression);
                    return;

                case BoundIf ifStatement:
                    If(ifStatement);
                    return;

                case BoundWhile loop: {
                    var header = NewBlock();
                    var body = NewBlock();
                    var after = NewBlock();
                    Edge(_current, header);

                    _current = header;
                    Condition(loop.Condition, body, after, fold: true);

                    _current = body;
                    InLoop(loop, after, header, () => Statement(loop.Body));
                    Edge(_current, header);

                    _current = after;
                    return;
                }

                case BoundRepeat loop: {
                    var body = NewBlock();
                    var test = NewBlock();
                    var after = NewBlock();
                    Edge(_current, body);

                    _current = body;
                    InLoop(loop, after, test, () => Statement(loop.Body));
                    Edge(_current, test);

                    _current = test;
                    Condition(loop.Condition, body, after, fold: true);

                    _current = after;
                    return;
                }

                case BoundForEach loop: {
                    Expression(loop.Sequence);

                    var header = NewBlock();
                    var body = NewBlock();
                    var after = NewBlock();
                    Edge(_current, header);
                    Edge(header, body);
                    Edge(header, after);

                    _current = body;
                    if (loop.Element is { } element) {
                        Op(FlowOpKind.Write, element, loop);
                    }

                    if (loop.Filter is { } filter) {
                        // 'for x in xs where f(x)': a failed filter skips to the next element.
                        var filtered = NewBlock();
                        Condition(filter, filtered, header);
                        _current = filtered;
                    }

                    InLoop(loop, after, header, () => Statement(loop.Body));
                    Edge(_current, header);

                    _current = after;
                    return;
                }

                case BoundReturn returnStatement:
                    if (returnStatement.Value is { } value) {
                        Expression(value);
                    }

                    if (_nestedBodies == 0) {
                        _exiting.Add(returnStatement);
                    }

                    Jump(_returnTarget);
                    return;

                case BoundThrow throwStatement:
                    Expression(throwStatement.Value);
                    if (_throwTargets.Count == 0 && _nestedBodies == 0) {
                        _exiting.Add(throwStatement);
                    }

                    Jump(ThrowTarget);
                    return;

                case BoundBreak jump:
                    Jump(FindLoop(jump.Label, jump) is { } breakLoop ? breakLoop.Break : _escape);
                    return;

                case BoundContinue jump:
                    Jump(FindLoop(jump.Label, jump, continues: true)?.Continue ?? _escape);
                    return;

                case BoundFallthrough:
                    if (_fallthroughs.Count > 0 && _fallthroughs[^1] is { } nextCase) {
                        Jump(nextCase);
                    }

                    return;

                case BoundDefer defer:
                    // It runs when the scope ends, with what holds then. Taken
                    // here as a branch that may or may not run, which is what
                    // the code after it can rely on.
                    SideBranch(() => Statement(defer.Body));
                    return;

                case BoundDo doStatement:
                    Do(doStatement);
                    return;

                case BoundSwitch switchStatement:
                    Switch(switchStatement);
                    return;

                case BoundScopedBlock scoped:
                    if (scoped.Source is { } source) {
                        Expression(source);
                    }

                    if (scoped.Name is { } name) {
                        Op(FlowOpKind.Write, name, scoped);
                    }

                    Statement(scoped.Body);
                    return;

                case BoundLocalFunction function:
                    if (function.Body is { } functionBody) {
                        NestedBody(function.Symbol.Parameters, functionBody, function);
                    }

                    return;

                default:
                    Children(statement);
                    return;
            }
        }

        BasicBlock ThrowTarget => _throwTargets.Count > 0 ? _throwTargets[^1] : _throwExit;

        void InLoop(BoundStatement loop, BasicBlock breakTarget, BasicBlock continueTarget, Action body) {
            _loops.Add(new(LabelOf(loop), breakTarget, continueTarget));
            body();
            _loops.RemoveAt(_loops.Count - 1);
        }

        /// <summary>The loop a jump leaves, or null when it is outside the body — and so leaves the body too.</summary>
        Loop? FindLoop(string? label, BoundStatement jump, bool continues = false) {
            for (var i = _loops.Count - 1; i >= 0; i--) {
                if (continues && _loops[i].Continue is null) {
                    continue;
                }

                if (label is null || string.Equals(_loops[i].Label, label, StringComparison.Ordinal)) {
                    return _loops[i];
                }
            }

            if (_nestedBodies == 0) {
                _exiting.Add(jump);
            }

            return null;
        }

        static string? LabelOf(BoundStatement loop) =>
            loop.Syntax.Parent is LabeledStatementSyntax { Label.Text: { Length: > 0 } label } ? label : null;

        void If(BoundIf ifStatement) {
            var after = NewBlock();

            // 'guard c else { body }': the body runs when c is false, and the
            // way on is with it true.
            if (ifStatement.Syntax is GuardStatementSyntax) {
                var elseBody = NewBlock();
                Condition(ifStatement.Condition, after, elseBody);
                _current = elseBody;
                Statement(ifStatement.Consequence);
                Edge(_current, after);
                _current = after;
                return;
            }

            var consequence = NewBlock();
            var alternative = ifStatement.Alternative is null ? after : NewBlock();
            Condition(ifStatement.Condition, consequence, alternative);

            _current = consequence;
            Statement(ifStatement.Consequence);
            Edge(_current, after);

            if (ifStatement.Alternative is { } otherwise) {
                _current = alternative;
                Statement(otherwise);
                Edge(_current, after);
            }

            _current = after;
        }

        void Do(BoundDo doStatement) {
            var dispatch = NewBlock();
            var after = NewBlock();

            _throwTargets.Add(dispatch);
            Statement(doStatement.Body);
            _throwTargets.RemoveAt(_throwTargets.Count - 1);
            Edge(_current, after);

            // Each catch is tried in turn; one that cannot match, or whose
            // guard fails, passes the error on to the next.
            _current = dispatch;
            foreach (var clause in doStatement.Catches) {
                var handler = NewBlock();
                var next = NewBlock();
                Edge(_current, handler);
                if (clause.Pattern is not null || clause.Guard is not null) {
                    Edge(_current, next);
                }

                _current = handler;
                if (clause.ErrorLocal is { } error) {
                    Op(FlowOpKind.Write, error, doStatement);
                }

                if (clause.Pattern is { } pattern) {
                    PatternWrites(pattern);
                }

                if (clause.Guard is { } guard) {
                    var guarded = NewBlock();
                    Condition(guard, guarded, next);
                    _current = guarded;
                }

                Statement(clause.Body);
                Edge(_current, after);
                _current = next;
            }

            // Nothing caught it: on to whatever is outside.
            Jump(ThrowTarget);
            _current = after;
        }

        void Switch(BoundSwitch switchStatement) {
            Expression(switchStatement.Subject);

            // The values are tested in order, each after the last; any of
            // them, or 'default' after all of them, chooses a case.
            var after = NewBlock();
            var bodies = switchStatement.Cases.Select(_ => NewBlock()).ToList();
            BasicBlock? defaultBody = null;
            for (var i = 0; i < switchStatement.Cases.Length; i++) {
                var switchCase = switchStatement.Cases[i];
                if (switchCase.Values.IsEmpty) {
                    defaultBody = bodies[i];
                }

                foreach (var value in switchCase.Values) {
                    Expression(value);
                    Edge(_current, bodies[i]);
                }
            }

            Jump(defaultBody ?? after);

            // A 'break' leaves the switch; a 'continue' goes on to the loop
            // around it.
            _loops.Add(new(null, after, null));
            for (var i = 0; i < switchStatement.Cases.Length; i++) {
                _current = bodies[i];
                _fallthroughs.Add(i + 1 < bodies.Count ? bodies[i + 1] : null);
                foreach (var statement in switchStatement.Cases[i].Statements) {
                    Statement(statement);
                }

                _fallthroughs.RemoveAt(_fallthroughs.Count - 1);
                Edge(_current, after);
            }

            _loops.RemoveAt(_loops.Count - 1);
            _current = after;
        }

        // -------------------------------------------------------------------
        // Conditions: lowered to branches, so '&&', '||' and '!' short-circuit
        // and a binding is set only on the edge where it matched.
        // -------------------------------------------------------------------

        /// <remarks>
        ///     A literal <c>true</c> or <c>false</c> is folded only for a loop
        ///     (<paramref name="fold" />): <c>while true</c> ends only by a jump,
        ///     which is what makes code after it unreachable. An <c>if true</c>
        ///     is checked as if either branch could run, so the code is right
        ///     whichever way the condition is later edited.
        /// </remarks>
        void Condition(BoundExpression condition, BasicBlock whenTrue, BasicBlock whenFalse, bool fold = false) {
            switch (condition) {
                case BoundLiteral { Value: true } when fold:
                    Edge(_current, whenTrue);
                    return;

                case BoundLiteral { Value: false } when fold:
                    Edge(_current, whenFalse);
                    return;

                case BoundBinaryOperator { OperatorKind: BinaryOperatorKind.LogicalAnd } conjunction: {
                    var right = NewBlock();
                    Condition(conjunction.Left, right, whenFalse);
                    _current = right;
                    Condition(conjunction.Right, whenTrue, whenFalse);
                    return;
                }

                case BoundBinaryOperator { OperatorKind: BinaryOperatorKind.LogicalOr } disjunction: {
                    var right = NewBlock();
                    Condition(disjunction.Left, whenTrue, right);
                    _current = right;
                    Condition(disjunction.Right, whenTrue, whenFalse);
                    return;
                }

                case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Not } negation:
                    Condition(negation.Operand, whenFalse, whenTrue);
                    return;

                case BoundConditionBinding binding: {
                    Expression(binding.Source);
                    var matched = NewBlock();
                    Edge(_current, matched);
                    Edge(_current, whenFalse);

                    _current = matched;
                    if (binding.Local is { } local) {
                        Op(FlowOpKind.Write, local, binding);
                    }

                    if (binding.Pattern is { } pattern) {
                        PatternWrites(pattern);
                    }

                    Edge(_current, whenTrue);
                    return;
                }

                default:
                    Expression(condition);
                    Edge(_current, whenTrue);
                    Edge(_current, whenFalse);
                    return;
            }
        }

        // -------------------------------------------------------------------
        // Expressions, in evaluation order
        // -------------------------------------------------------------------

        void Expression(BoundExpression expression) {
            ExpressionCore(expression);

            // An expression of type 'Never' does not come back: 'abort(...)',
            // 'unreachable()', a call to a function declared '-> Never'.
            if (expression.Type is NamedTypeSymbol { Name: "Never", TypeKind: TypeKind.Builtin }) {
                _current = NewBlock();
            }
        }

        void ExpressionCore(BoundExpression expression) {
            switch (expression) {
                case BoundLocalAccess access:
                    Op(FlowOpKind.Read, access.Local, access);
                    return;

                case BoundParameterAccess access:
                    Op(FlowOpKind.Read, access.Parameter, access);
                    return;

                case BoundAssignment assignment:
                    Expression(assignment.Value);
                    switch (assignment.Target) {
                        case BoundLocalAccess target:
                            Op(FlowOpKind.Write, target.Local, target);
                            return;

                        case BoundParameterAccess target:
                            Op(FlowOpKind.Write, target.Parameter, target);
                            return;

                        default:
                            // 'q.x = 1' changes q: its receiver chain is
                            // evaluated, and q is written in part.
                            Expression(assignment.Target);
                            if (BaseBinding(assignment.Target) is { } owner) {
                                Op(FlowOpKind.Write, owner, assignment.Target);
                            }

                            return;
                    }

                case BoundBinaryOperator { OperatorKind: BinaryOperatorKind.LogicalAnd or BinaryOperatorKind.LogicalOr }: {
                    var after = NewBlock();
                    Condition(expression, after, after);
                    _current = after;
                    return;
                }

                case BoundBinaryOperator { OperatorKind: BinaryOperatorKind.Coalesce } coalesce: {
                    // The right side runs only when the left is none.
                    Expression(coalesce.Left);
                    SideBranch(() => Expression(coalesce.Right));
                    return;
                }

                case BoundConditional conditional: {
                    var whenTrue = NewBlock();
                    var whenFalse = NewBlock();
                    var after = NewBlock();
                    Condition(conditional.Condition, whenTrue, whenFalse);

                    _current = whenTrue;
                    Expression(conditional.WhenTrue);
                    Edge(_current, after);

                    _current = whenFalse;
                    Expression(conditional.WhenFalse);
                    Edge(_current, after);

                    _current = after;
                    return;
                }

                case BoundConditionBinding: {
                    var after = NewBlock();
                    Condition(expression, after, after);
                    _current = after;
                    return;
                }

                case BoundTry tryExpression:
                    Try(tryExpression);
                    return;

                case BoundMatch match:
                    Match(match);
                    return;

                case BoundClosure closure:
                    foreach (var capture in closure.Captures) {
                        if (capture.Initializer is { } value) {
                            Expression(value);
                            if (capture.Symbol is LocalSymbol copied) {
                                Op(FlowOpKind.Write, copied, closure);
                            }
                        }
                    }

                    NestedBody(closure.Symbol.Parameters, closure.Body, closure);
                    return;

                default:
                    Children(expression);
                    return;
            }
        }

        void Try(BoundTry tryExpression) {
            Expression(tryExpression.Expression);

            switch (tryExpression.TryKind) {
                // 'try e' may leave for the nearest catch, or the caller.
                // The block ends here, so that what comes after the 'try' is
                // not on the way to the catch: edges leave from a block's end.
                case TryKind.Propagate: {
                    var next = NewBlock();
                    Edge(_current, ThrowTarget);
                    Edge(_current, next);
                    _current = next;
                    return;
                }

                // 'try e catch { fallback }': the fallback runs instead, from
                // wherever in e the error came.
                case TryKind.Fallback when tryExpression.Fallback is { } fallback: {
                    var after = NewBlock();
                    var handler = NewBlock();
                    Edge(_current, after);
                    Edge(_current, handler);

                    _current = handler;
                    if (tryExpression.ErrorLocal is { } error) {
                        Op(FlowOpKind.Write, error, tryExpression);
                    }

                    Expression(fallback);
                    Edge(_current, after);
                    _current = after;
                    return;
                }
            }
        }

        void Match(BoundMatch match) {
            Expression(match.Subject);

            var after = NewBlock();
            var test = _current;

            foreach (var arm in match.Arms) {
                var armBlock = NewBlock();
                var next = NewBlock();
                Edge(test, armBlock);
                Edge(test, next);

                _current = armBlock;
                foreach (var pattern in arm.Patterns) {
                    PatternWrites(pattern);
                }

                if (arm.Guard is { } guard) {
                    var guarded = NewBlock();
                    Condition(guard, guarded, next);
                    _current = guarded;
                }

                switch (arm.Body) {
                    case BoundStatement statement: Statement(statement); break;
                    case BoundExpression value: Expression(value); break;
                }

                Edge(_current, after);
                test = next;
            }

            // No arm matched. Exhaustiveness is checked elsewhere, and a
            // match it rejects is already an error, so this way out is not
            // taken: a missing-return error on top would only repeat it.
            _current = after;
        }

        /// <summary>A closure or local function: a side branch with its own returns, loops and throws.</summary>
        void NestedBody(IEnumerable<ParameterSymbol> parameters, BoundBlock body, BoundNode owner) {
            var savedReturn = _returnTarget;
            var savedLoops = _loops;
            var savedFallthroughs = _fallthroughs;
            var savedThrows = _throwTargets;

            SideBranch(() => {
                    var end = NewBlock();
                    _returnTarget = end;
                    _loops = [];
                    _fallthroughs = [];
                    _throwTargets = [end];
                    _nestedBodies++;

                    foreach (var parameter in parameters) {
                        Op(FlowOpKind.Write, parameter, owner);
                    }

                    Statement(body);
                    Edge(_current, end);
                    _current = end;

                    _nestedBodies--;
                    _throwTargets = savedThrows;
                    _loops = savedLoops;
                    _fallthroughs = savedFallthroughs;
                    _returnTarget = savedReturn;
                }
            );
        }

        /// <summary>Code that may or may not run here: control goes through it or round it.</summary>
        void SideBranch(Action body) {
            var start = NewBlock();
            var after = NewBlock();
            Edge(_current, start);
            Edge(_current, after);

            _current = start;
            body();
            Edge(_current, after);

            _current = after;
        }

        void Children(BoundNode node) {
            foreach (var child in node.Children) {
                switch (child) {
                    case BoundStatement statement: Statement(statement); break;
                    case BoundExpression value: Expression(value); break;
                }
            }
        }

        void PatternWrites(BoundPattern pattern) {
            switch (pattern) {
                case BoundBindingPattern binding:
                    Op(FlowOpKind.Write, binding.Local, binding);
                    break;

                case BoundTypeTestPattern { Local: { } tested }:
                    Op(FlowOpKind.Write, tested, pattern);
                    break;
            }

            foreach (var child in pattern.Children) {
                if (child is BoundPattern nested) {
                    PatternWrites(nested);
                }
            }
        }

        /// <summary>The binding a member or element access ultimately reads from.</summary>
        internal static Symbol? BaseBinding(BoundExpression expression) =>
            expression switch {
                BoundLocalAccess local => local.Local,
                BoundParameterAccess parameter => parameter.Parameter,
                BoundFieldAccess { Receiver: { } receiver } => BaseBinding(receiver),
                BoundIndexAccess index => BaseBinding(index.Receiver),
                BoundTupleElementAccess tuple => BaseBinding(tuple.Receiver),
                _ => null,
            };
    }
}
