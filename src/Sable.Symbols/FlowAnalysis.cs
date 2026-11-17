namespace Sable.Symbols;

/// <summary>
///     Answers whether control can fall off the end of a statement.
/// </summary>
/// <remarks>
///     This is the definite-return half of docs/04 §1.3. It is deliberately
///     conservative: it says "always returns" only when it can see that it does,
///     so a function it complains about may still be fine in some exotic shape,
///     and the fix is always to make the exit explicit.
/// </remarks>
internal static class FlowAnalysis {
    public static bool AlwaysExits(BoundStatement statement) =>
        statement switch {
            BoundReturn => true,
            BoundThrow => true,

            // Not an exit from the function, but an exit from the statement
            // sequence it sits in, which is what "unreachable after" means.
            BoundBreak or BoundContinue => true,

            BoundBlock block => block.Statements.Any(AlwaysExits),

            BoundIf ifStatement =>
                ifStatement.Alternative is not null
                && AlwaysExits(ifStatement.Consequence)
                && AlwaysExits(ifStatement.Alternative),

            // 'while true' only ends by breaking out, and a 'break' is not an exit.
            BoundWhile loop => IsAlwaysTrue(loop.Condition) && !ContainsBreak(loop.Body),

            BoundRepeat loop =>
                AlwaysExits(loop.Body)
                || (IsAlwaysTrue(loop.Condition) && !ContainsBreak(loop.Body)),

            BoundExpressionStatement expression => AlwaysExits(expression.Expression),

            // 'unsafe', 'comptime' and 'region' are scopes and nothing more.
            BoundScopedBlock scoped => AlwaysExits(scoped.Body),

            // A switch exits when it has a default and every case does. A case
            // that falls through is continued by the next one, so it does not
            // have to exit on its own.
            BoundSwitch switchStatement =>
                switchStatement.Cases.Any(static c => c.Values.IsEmpty)
                && switchStatement.Cases.All(static c =>
                    c.Statements.Any(AlwaysExits)
                    || c.Statements.Any(static s => s is BoundFallthrough)
                ),

            // A 'do' exits only if every way out of it does, the catches too.
            BoundDo doStatement =>
                AlwaysExits(doStatement.Body)
                && doStatement.Catches.All(static clause => AlwaysExits(clause.Body)),


            _ => false,
        };

    static bool AlwaysExits(BoundExpression expression) =>
        expression switch {
            // An expression of type 'Never' does not come back.
            _ when expression.Type is NamedTypeSymbol { Name: "Never", TypeKind: TypeKind.Builtin }
                => true,

            // 'try e' is as final as what it wraps; a fallback gives it a way back.
            BoundTry { TryKind: not TryKind.Fallback } tryExpression =>
                AlwaysExits(tryExpression.Expression),

            BoundMatch match =>
                match.Arms.Length > 0
                && match.Arms.All(static arm => arm.Body switch {
                        BoundStatement statement => AlwaysExits(statement),
                        BoundExpression value => AlwaysExits(value),
                        _ => false,
                    }
                ),

            _ => false,
        };

    static bool IsAlwaysTrue(BoundExpression condition) => condition is BoundLiteral { Value: true };

    /// <summary>True when a loop body can break out of that loop.</summary>
    static bool ContainsBreak(BoundNode node) {
        if (node is BoundBreak) {
            return true;
        }

        foreach (var child in node.Children) {
            // A nested loop's breaks belong to it, not to this one.
            if (child is BoundWhile or BoundRepeat or BoundForEach) {
                continue;
            }

            if (ContainsBreak(child)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The first statement anywhere in a body that can never be reached.
    /// </summary>
    /// <remarks>
    ///     Every block is looked at, not only the outermost: a <c>break</c>
    ///     followed by a statement is just as dead inside a loop as a
    ///     <c>return</c> is at the top.
    /// </remarks>
    public static BoundStatement? FirstUnreachable(BoundNode node) {
        if (node is BoundBlock block) {
            for (var i = 0; i < block.Statements.Length - 1; i++) {
                if (AlwaysExits(block.Statements[i])) {
                    return block.Statements[i + 1];
                }
            }
        }

        foreach (var child in node.Children) {
            // A closure or a local function has its own body and its own
            // reachability; it is checked when it is bound.
            if (child is BoundClosure or BoundLocalFunction) {
                continue;
            }

            if (FirstUnreachable(child) is { } found) {
                return found;
            }
        }

        return null;
    }
}
