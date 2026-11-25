using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     What the threads of a <c>Thread.scope</c> share with the function
///     around it (docs/06 §6.7): every thread a scope starts ends before the
///     scope does, so one may use a <c>var</c> of that function where it is —
///     if, for the length of the scope, it is that thread's alone.
/// </summary>
/// <remarks>
///     A <c>var</c> that a thread's closure captures is in a box, shared as it
///     crosses, which the function and the thread both reach; no count or
///     object is at risk, and what is checked here is that the two do not use
///     it at once. It is, conservatively, all the thread's:
///     <list type="bullet">
///         <item>the thread is spawned on the scope the closure of the
///         innermost <c>Thread.scope</c> was given, in that closure itself,
///         and not in a loop, so that the closure runs once;</item>
///         <item>the <c>var</c> is one of the function that calls
///         <c>Thread.scope</c>, which it outlives;</item>
///         <item>nothing else in the scope mentions it, read or written — the
///         scope's own code or another thread's closure;</item>
///         <item>no other closure or local function of the function mentions
///         it, which the scope's code might call, and no reference is made
///         of it.</item>
///     </list>
///     A <c>let</c>, or a capture list's, is captured by its value, and any
///     number of threads may.
/// </remarks>
internal static class ScopeChecker {
    /// <summary>'Thread.scope', whose closure's threads end before it returns.</summary>
    static bool IsScope(FunctionSymbol function) => function.Name == "scope" && IsThreads(function.ContainingSymbol, "Thread");

    /// <summary>'ThreadScope.spawn', of a thread that ends before its scope does.</summary>
    public static bool IsScopedSpawn(FunctionSymbol function) => function.Name == "spawn" && IsThreads(function.ContainingSymbol, "ThreadScope");

    static bool IsThreads(Symbol? type, string name) =>
        type is NamedTypeSymbol { ContainingSymbol: ModuleSymbol { Name: "Sable.Concurrency" } } named && named.Name == name;

    public static void Check(BoundNode body, FunctionSymbol function, DiagnosticBag diagnostics) => Visit(body, function, body, null, diagnostics);

    /// <summary>
    ///     The closure of the innermost 'Thread.scope' around some code, the
    ///     function that calls it and that function's body; whether the code
    ///     is in a closure inside the scope's, and whether it may run more
    ///     than once.
    /// </summary>
    sealed record Scope(BoundClosure Closure, Symbol Function, BoundNode Body, bool Nested = false, bool Repeated = false);

    static void Visit(BoundNode node, Symbol function, BoundNode body, Scope? scope, DiagnosticBag diagnostics) {
        switch (node) {
            case BoundClosure closure:
                (function, body) = (closure.Symbol, closure.Body);
                scope = scope is null ? null : scope with { Nested = true };
                break;

            case BoundLocalFunction { Body: { } local } declared:
                (function, body) = (declared.Symbol, local);
                scope = scope is null ? null : scope with { Nested = true };
                break;

            case BoundWhile or BoundRepeat or BoundForEach or BoundDefer when scope is not null:
                scope = scope with { Repeated = true };
                break;

            case BoundCall { Function: var called } call when IsScope(called):
                foreach (var child in call.Children) {
                    if (child is BoundExpression argument && Unwrapped(argument) is BoundClosure closure) {
                        Visit(closure.Body, closure.Symbol, closure.Body, new Scope(closure, function, body), diagnostics);
                    } else {
                        Visit(child, function, body, scope, diagnostics);
                    }
                }

                return;

            case BoundCall { Function: var called } call when IsScopedSpawn(called):
                foreach (var argument in call.Arguments) {
                    if (Unwrapped(argument) is BoundClosure closure) {
                        CheckThread(call, closure, scope, diagnostics);
                    }
                }

                break;
        }

        foreach (var child in node.Children) {
            Visit(child, function, body, scope, diagnostics);
        }
    }

    static BoundExpression Unwrapped(BoundExpression expression) =>
        expression is BoundConversion conversion ? Unwrapped(conversion.Operand) : expression;

    static void CheckThread(BoundCall spawn, BoundClosure thread, Scope? scope, DiagnosticBag diagnostics) {
        var reported = new HashSet<Symbol>(ReferenceEqualityComparer.Instance);
        var inside = Owners(thread);
        var listed = thread.Captures.Select(static c => c.Symbol).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var node in Descendants(thread.Body)) {
            if (Variable(node) is { } variable && variable.ContainingSymbol is { } owner && !inside.Contains(owner) && !listed.Contains(variable)
                && !InUnsafe(node.Syntax) && reported.Add(variable) && Why(variable, spawn, thread, scope) is { } reason) {
                diagnostics.Error(
                    ErrorCode.ERR_TaskCaptureNotSendable,
                    node.Syntax.Span,
                    $"the thread cannot share '{variable.Name}', a 'var' of the code around it: {reason}; "
                    + $"copy it into a 'let' to capture, or capture its value in a capture list, '[{variable.Name} = {variable.Name}]'"
                );
            }
        }
    }

    /// <summary>Why a 'var' that a thread's closure captures is not that thread's alone; null where it is.</summary>
    static string? Why(Symbol variable, BoundCall spawn, BoundClosure thread, Scope? scope) {
        if (scope is not { Nested: false } || spawn.Receiver is not { } receiver
            || Unwrapped(receiver) is not BoundParameterAccess { Parameter: var parameter } || !ReferenceEquals(parameter.ContainingSymbol, scope.Closure.Symbol)) {
            return "the thread is not spawned on a scope in the closure that 'Thread.scope' gave it to";
        }

        if (!ReferenceEquals(variable.ContainingSymbol, scope.Function)) {
            return ReferenceEquals(variable.ContainingSymbol, scope.Closure.Symbol)
                ? "it ends with the scope's closure, before the thread has"
                : "it is one of a function further out than the one that calls 'Thread.scope'";
        }

        if (scope.Repeated) {
            return "the thread is spawned in a loop, so that several would change it at once";
        }

        foreach (var node in Descendants(scope.Closure.Body, except: thread)) {
            if (ReferenceEquals(Variable(node), variable)) {
                return "the scope uses it elsewhere, while the thread may be changing it";
            }

            // A reference made outside may be to it.
            if (node is BoundLocalAccess { Local: { Type: ReferenceTypeSymbol } reference } && !ReferenceEquals(reference.ContainingSymbol, scope.Closure.Symbol)) {
                return $"the scope uses '{reference.Name}', a reference, which may be to it";
            }
        }

        foreach (var node in Descendants(scope.Body, except: scope.Closure)) {
            if (node is BoundClosure or BoundLocalFunction && Descendants(node).Any(n => ReferenceEquals(Variable(n), variable))) {
                return "another closure of the function uses it, which the scope may call";
            }
        }

        return null;
    }

    /// <summary>The 'var', or the 'inout' parameter, that an expression is of.</summary>
    static Symbol? Variable(BoundNode node) =>
        node switch {
            BoundLocalAccess { Local: { IsMutable: true } local } => local,
            BoundParameterAccess { Parameter: { Modifier: ParameterModifier.InOut } parameter } => parameter,
            _ => null,
        };

    /// <summary>The closure's own symbol, and those of the closures and local functions inside it.</summary>
    static HashSet<Symbol> Owners(BoundClosure closure) {
        var owners = new HashSet<Symbol>(ReferenceEqualityComparer.Instance) { closure.Symbol };
        foreach (var node in Descendants(closure.Body)) {
            switch (node) {
                case BoundClosure nested:
                    owners.Add(nested.Symbol);
                    break;
                case BoundLocalFunction local:
                    owners.Add(local.Symbol);
                    break;
            }
        }

        return owners;
    }

    static IEnumerable<BoundNode> Descendants(BoundNode root, BoundNode? except = null) {
        var pending = new Stack<BoundNode>([root]);
        while (pending.TryPop(out var node)) {
            if (ReferenceEquals(node, except)) {
                continue;
            }

            yield return node;
            foreach (var child in node.Children) {
                pending.Push(child);
            }
        }
    }

    static bool InUnsafe(SyntaxNode node) {
        for (SyntaxNode? at = node; at is not null; at = at.Parent) {
            if (at is UnsafeStatementSyntax) {
                return true;
            }
        }

        return false;
    }
}
