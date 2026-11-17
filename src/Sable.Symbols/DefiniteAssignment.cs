using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
/// Reports locals read before anything has written to them.
/// </summary>
/// <remarks>
/// <para>This is the definite-assignment half of docs/04 §1.3, done on the
/// bound tree. It is a forward walk carrying one set — the locals that are
/// assigned on every path to this point — and the rule at a join is
/// intersection: a name counts as assigned after an <c>if</c> only when both
/// branches set it, unless one of them does not come back.</para>
/// <para>It moves to SIL in Phase 2. Doing it here first is faster to build
/// and the tests carry over, which is the reason docs/04 asks for it in this
/// order.</para>
/// </remarks>
internal sealed class DefiniteAssignment
{
    private readonly DiagnosticBag _diagnostics;
    private readonly HashSet<LocalSymbol> _reported = [];

    private DefiniteAssignment(DiagnosticBag diagnostics) => _diagnostics = diagnostics;

    public static void Analyze(
        BoundBlock body, IEnumerable<Symbol> alreadyAssigned, DiagnosticBag diagnostics)
    {
        var analysis = new DefiniteAssignment(diagnostics);
        var state = new State();
        foreach (var symbol in alreadyAssigned)
            if (symbol is LocalSymbol local) state.Assign(local);

        analysis.Walk(body, state);
    }

    /// <summary>
    /// What is known at a point: which locals are assigned, and whether the
    /// point can be reached at all.
    /// </summary>
    private sealed class State
    {
        private HashSet<LocalSymbol> _assigned = [];

        public bool Reachable { get; private set; } = true;

        public bool IsAssigned(LocalSymbol local) => !Reachable || _assigned.Contains(local);

        public void Assign(LocalSymbol local) => _assigned.Add(local);

        public void Exit() => Reachable = false;

        public State Fork()
        {
            var copy = new State { _assigned = [.. _assigned] };
            if (!Reachable) copy.Exit();
            return copy;
        }

        /// <summary>
        /// Joins a branch into this one: a name survives only if both paths
        /// set it, and a path that does not come back contributes nothing.
        /// </summary>
        public void Join(State other)
        {
            if (!other.Reachable) return;

            if (!Reachable)
            {
                _assigned = [.. other._assigned];
                Reachable = true;
                return;
            }

            _assigned.IntersectWith(other._assigned);
        }

        /// <summary>Takes a branch's result wholesale, for a path that must run.</summary>
        public void Adopt(State other)
        {
            _assigned = [.. other._assigned];
            Reachable = other.Reachable;
        }
    }

    // -----------------------------------------------------------------------
    // Statements
    // -----------------------------------------------------------------------

    private void Walk(BoundStatement statement, State state)
    {
        switch (statement)
        {
            case BoundBlock block:
                foreach (var child in block.Statements) Walk(child, state);
                return;

            case BoundLocalDeclaration declaration:
                if (declaration.Initializer is { } initializer)
                {
                    Walk(initializer, state);
                    state.Assign(declaration.Local);
                }
                return;

            case BoundExpressionStatement expression:
                Walk(expression.Expression, state);
                return;

            case BoundIf ifStatement:
            {
                Walk(ifStatement.Condition, state);

                // Names a condition binds are set inside the branch it guards.
                var consequence = state.Fork();
                foreach (var local in BindingsIn(ifStatement.Condition))
                    consequence.Assign(local);
                Walk(ifStatement.Consequence, consequence);

                if (ifStatement.Alternative is { } alternative)
                {
                    var other = state.Fork();
                    Walk(alternative, other);
                    state.Adopt(consequence);
                    state.Join(other);
                    return;
                }

                // 'guard c else { return }' is an 'if' with no else whose body
                // does not come back, so what the condition bound is assigned
                // from here on.
                if (!consequence.Reachable)
                {
                    foreach (var local in BindingsIn(ifStatement.Condition)) state.Assign(local);
                    return;
                }

                state.Join(consequence);
                return;
            }

            case BoundWhile loop:
            {
                Walk(loop.Condition, state);

                // The body may not run, so nothing it assigns is certain.
                var body = state.Fork();
                foreach (var local in BindingsIn(loop.Condition)) body.Assign(local);
                Walk(loop.Body, body);
                return;
            }

            case BoundRepeat loop:
            {
                // A 'repeat' body always runs once, so what it assigns holds.
                Walk(loop.Body, state);
                Walk(loop.Condition, state);
                return;
            }

            case BoundForEach loop:
            {
                Walk(loop.Sequence, state);

                var body = state.Fork();
                if (loop.Element is { } element) body.Assign(element);
                if (loop.Filter is { } filter) Walk(filter, body);
                Walk(loop.Body, body);
                return;
            }

            case BoundDo doStatement:
            {
                var body = state.Fork();
                Walk(doStatement.Body, body);

                // A catch runs from somewhere in the middle of the body, so
                // nothing the body assigned can be relied on inside it.
                foreach (var clause in doStatement.Catches)
                {
                    var handler = state.Fork();
                    if (clause.ErrorLocal is { } error) handler.Assign(error);
                    if (clause.Pattern is { } pattern)
                        foreach (var local in LocalsIn(pattern)) handler.Assign(local);
                    if (clause.Guard is { } guard) Walk(guard, handler);
                    Walk(clause.Body, handler);
                    body.Join(handler);
                }

                state.Adopt(body);
                return;
            }

            case BoundReturn returnStatement:
                if (returnStatement.Value is { } value) Walk(value, state);
                state.Exit();
                return;

            case BoundThrow throwStatement:
                Walk(throwStatement.Value, state);
                state.Exit();
                return;

            case BoundBreak or BoundContinue:
                state.Exit();
                return;

            case BoundDefer defer:
            {
                // A defer runs later, so it sees no more than this point does.
                Walk(defer.Body, state.Fork());
                return;
            }

            case BoundLocalFunction localFunction:
                if (localFunction.Body is { } functionBody) Walk(functionBody, state.Fork());
                return;

            default:
                foreach (var child in statement.Children)
                {
                    switch (child)
                    {
                        case BoundStatement nested: Walk(nested, state); break;
                        case BoundExpression expression: Walk(expression, state); break;
                    }
                }
                return;
        }
    }

    // -----------------------------------------------------------------------
    // Expressions
    // -----------------------------------------------------------------------

    private void Walk(BoundExpression expression, State state)
    {
        switch (expression)
        {
            case BoundLocalAccess access:
                if (!state.IsAssigned(access.Local) && _reported.Add(access.Local))
                {
                    _diagnostics.Error(
                        ErrorCode.ERR_UseBeforeAssignment, access.Syntax.Span,
                        $"'{access.Local.Name}' is read here before anything assigns to it");
                }
                return;

            case BoundAssignment assignment:
            {
                Walk(assignment.Value, state);

                if (assignment.Target is BoundLocalAccess target)
                {
                    state.Assign(target.Local);
                    return;
                }

                Walk(assignment.Target, state);
                return;
            }

            case BoundBinaryOperator
                { OperatorKind: BinaryOperatorKind.LogicalAnd } conjunction:
            {
                // The right side runs only when the left was true, so what the
                // left bound is in scope and assigned for it.
                Walk(conjunction.Left, state);
                var right = state.Fork();
                foreach (var local in BindingsIn(conjunction.Left)) right.Assign(local);
                Walk(conjunction.Right, right);
                return;
            }

            case BoundConditional conditional:
            {
                Walk(conditional.Condition, state);
                var whenTrue = state.Fork();
                Walk(conditional.WhenTrue, whenTrue);
                var whenFalse = state.Fork();
                Walk(conditional.WhenFalse, whenFalse);
                state.Adopt(whenTrue);
                state.Join(whenFalse);
                return;
            }

            case BoundMatch match:
            {
                Walk(match.Subject, state);

                State? joined = null;
                foreach (var arm in match.Arms)
                {
                    var armState = state.Fork();
                    foreach (var pattern in arm.Patterns)
                        foreach (var local in LocalsIn(pattern)) armState.Assign(local);

                    if (arm.Guard is { } guard) Walk(guard, armState);

                    switch (arm.Body)
                    {
                        case BoundStatement statement: Walk(statement, armState); break;
                        case BoundExpression value: Walk(value, armState); break;
                    }

                    if (joined is null) { joined = armState; continue; }
                    joined.Join(armState);
                }

                if (joined is not null) state.Adopt(joined);
                return;
            }

            case BoundClosure closure:
            {
                foreach (var capture in closure.Captures)
                    if (capture.Initializer is { } value) Walk(value, state);

                // The body runs later, so it can assign nothing for us; but it
                // must not read what is not set yet either.
                var inside = state.Fork();
                foreach (var capture in closure.Captures)
                    if (capture.Symbol is LocalSymbol local) inside.Assign(local);
                foreach (var parameter in closure.Symbol.Parameters) _ = parameter;
                Walk(closure.Body, inside);
                return;
            }

            case BoundConditionBinding binding:
                Walk(binding.Source, state);
                return;

            default:
                foreach (var child in expression.Children)
                {
                    switch (child)
                    {
                        case BoundStatement statement: Walk(statement, state); break;
                        case BoundExpression value: Walk(value, state); break;
                    }
                }
                return;
        }
    }

    // -----------------------------------------------------------------------
    // What a condition or a pattern brings into scope
    // -----------------------------------------------------------------------

    /// <summary>The locals a condition binds when it holds.</summary>
    private static IEnumerable<LocalSymbol> BindingsIn(BoundExpression condition)
    {
        switch (condition)
        {
            case BoundConditionBinding binding:
                if (binding.Local is { } local) yield return local;
                if (binding.Pattern is { } pattern)
                    foreach (var bound in LocalsIn(pattern)) yield return bound;
                yield break;

            case BoundBinaryOperator { OperatorKind: BinaryOperatorKind.LogicalAnd } conjunction:
                foreach (var bound in BindingsIn(conjunction.Left)) yield return bound;
                foreach (var bound in BindingsIn(conjunction.Right)) yield return bound;
                yield break;

            default:
                yield break;
        }
    }

    /// <summary>Every name a pattern binds.</summary>
    private static IEnumerable<LocalSymbol> LocalsIn(BoundPattern pattern)
    {
        if (pattern is BoundBindingPattern binding) yield return binding.Local;

        foreach (var child in pattern.Children)
        {
            if (child is not BoundPattern nested) continue;
            foreach (var local in LocalsIn(nested)) yield return local;
        }
    }
}
