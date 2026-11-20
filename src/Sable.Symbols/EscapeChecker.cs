using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     What may not outlive a call, does not (SYNTAX.md §17, docs/02 §2.3):
///     a closure parameter not marked <c>@escaping</c>, and an
///     <c>out</c> or <c>ref</c> parameter, which is the caller's storage.
/// </summary>
/// <remarks>
///     <para>
///         A closure parameter without <c>@escaping</c> may only be called, or
///         passed on to another parameter that does not escape. Returned,
///         stored, assigned to a variable or captured by a closure that may
///         escape, it could be called after the call that lent it has ended —
///         and whatever it captured with it.
///     </para>
///     <para>
///         A closure escapes unless it is written straight into a call, as the
///         argument of a closure parameter that does not escape; one that
///         escapes may not capture a closure parameter that does not. A local
///         function is taken to escape, since it can be called from anywhere
///         in the body. No closure may capture an <c>out</c> or <c>ref</c>
///         parameter: its captures are kept in an object. An <c>inout</c> one
///         is captured by copy-in, copy-out: the closures share a box holding
///         its value, written back to the caller's storage as the call ends.
///     </para>
/// </remarks>
internal static class EscapeChecker {
    public static void Check(BoundNode body, FunctionSymbol function, DiagnosticBag diagnostics) {
        var walker = new Walker(diagnostics);
        walker.Frames.Add((function, false));
        walker.Visit(body);
    }

    /// <summary>A closure parameter that is not '@escaping'.</summary>
    static bool IsNonEscapingClosure(ParameterSymbol parameter) =>
        parameter is { IsEscaping: false, IsCallersStorage: false }
        && parameter.Type is FunctionTypeSymbol
        && !parameter.DeclaringSyntax.IsEmpty;

    static bool IsByAddress(ParameterSymbol parameter) => parameter.IsCallersStorage;

    /// <summary>A closure, through the conversions that adapt it to the parameter it is passed to.</summary>
    static BoundExpression Unwrapped(BoundExpression expression) =>
        expression is BoundConversion { Type: FunctionTypeSymbol } conversion ? Unwrapped(conversion.Operand) : expression;

    sealed class Walker(DiagnosticBag diagnostics) {
        /// <summary>The function, then each closure or local function inside it, innermost last, with whether it escapes.</summary>
        public List<(Symbol Owner, bool Escapes)> Frames { get; } = [];

        /// <summary>
        ///     Uses of a closure that does not escape that keep it so: calling
        ///     it, passing it to a parameter that does not escape, and naming
        ///     it with a 'let'.
        /// </summary>
        readonly HashSet<BoundExpression> _allowed = new(ReferenceEqualityComparer.Instance);

        /// <summary>Closures written straight into a call, for a parameter that does not escape.</summary>
        readonly HashSet<BoundClosure> _contained = new(ReferenceEqualityComparer.Instance);

        /// <summary>'let k = g', for a 'g' that does not escape: 'k' does not either.</summary>
        readonly HashSet<LocalSymbol> _nonEscapingLocals = new(ReferenceEqualityComparer.Instance);

        readonly HashSet<Symbol> _reported = new(ReferenceEqualityComparer.Instance);

        public void Visit(BoundNode node) {
            switch (node) {
                case BoundCall call:
                    for (var i = 0; i < call.Arguments.Length && i < call.Function.Parameters.Length; i++) {
                        var parameter = call.Function.Parameters[i];
                        if (parameter.IsVariadic || parameter.IsEscaping || parameter.Type is not FunctionTypeSymbol || parameter.DeclaringSyntax.IsEmpty) {
                            continue;
                        }

                        switch (Unwrapped(call.Arguments[i])) {
                            case BoundClosure closure:
                                _contained.Add(closure);
                                break;
                            case var passed:
                                _allowed.Add(passed);
                                break;
                        }
                    }

                    break;

                case BoundIndirectCall { Callee: var callee }:
                    _allowed.Add(Unwrapped(callee));
                    break;

                case BoundLocalDeclaration { Local: { IsMutable: false, IsAsyncLet: false }, Initializer: { } initializer } declaration
                    when NonEscaping(Unwrapped(initializer)) is not null:
                    _allowed.Add(Unwrapped(initializer));
                    _nonEscapingLocals.Add(declaration.Local);
                    break;

                case BoundClosure closure:
                    Nested(closure, closure.Symbol, !_contained.Contains(closure));
                    return;

                case BoundLocalFunction local:
                    Nested(local, local.Symbol, true);
                    return;

                // An inout parameter may be captured: it is copied in to a box
                // the closures share, and back out when the call ends. An
                // 'out' parameter has nothing to copy in, and a 'T&' one is
                // the caller's storage itself, which a copy would not be.
                case BoundParameterAccess { Parameter: { Modifier: not ParameterModifier.InOut } parameter } access when IsByAddress(parameter):
                    Captured(access, parameter);
                    break;

                case BoundParameterAccess or BoundLocalAccess when NonEscaping((BoundExpression)node) is { } symbol:
                    Use((BoundExpression)node, symbol);
                    break;
            }

            foreach (var child in node.Children) {
                Visit(child);
            }
        }

        void Nested(BoundNode node, Symbol owner, bool escapes) {
            Frames.Add((owner, escapes));
            foreach (var child in node.Children) {
                Visit(child);
            }

            Frames.RemoveAt(Frames.Count - 1);
        }

        /// <summary>The closure parameter, or the 'let' naming one, that an expression reads and that may not escape.</summary>
        Symbol? NonEscaping(BoundExpression expression) =>
            expression switch {
                BoundParameterAccess { Parameter: var parameter } when IsNonEscapingClosure(parameter) => parameter,
                BoundLocalAccess { Local: var local } when _nonEscapingLocals.Contains(local) => local,
                _ => null,
            };

        /// <summary>The frame a symbol is declared in, or -1.</summary>
        int OwnerOf(Symbol symbol) {
            for (var container = symbol.ContainingSymbol; container is not null; container = container.ContainingSymbol) {
                var frame = Frames.FindLastIndex(f => ReferenceEquals(f.Owner, container));
                if (frame >= 0) {
                    return frame;
                }
            }

            return -1;
        }

        /// <summary>
        ///     A closure's captures live in its context, an object, which
        ///     nothing stops from outliving the call — so none may hold the
        ///     caller's storage.
        /// </summary>
        void Captured(BoundParameterAccess access, ParameterSymbol parameter) {
            var owner = OwnerOf(parameter);
            if (owner < 0 || owner == Frames.Count - 1 || !_reported.Add(parameter)) {
                return;
            }

            var kind = parameter.Modifier == ParameterModifier.None ? "a reference" : parameter.Modifier.ToString().ToLowerInvariant();
            diagnostics.Error(
                ErrorCode.ERR_Escapes,
                access.Syntax.Span,
                $"'{parameter.Name}' is {kind}, the caller's storage for the length of the call, so a closure cannot capture it; "
                + $"copy it into a local for the closure, and assign the local back to '{parameter.Name}' after if the closure changes it"
            );
        }

        void Use(BoundExpression access, Symbol symbol) {
            var owner = OwnerOf(symbol);
            if (owner < 0) {
                return;
            }

            // Captured by a closure between where it is declared and here that may escape.
            if (Frames.Skip(owner + 1).Any(static f => f.Escapes)) {
                diagnostics.Error(
                    ErrorCode.ERR_Escapes,
                    access.Syntax.Span,
                    $"'{symbol.Name}' is a closure that is not @escaping, so a closure that may outlive the call cannot capture it; "
                    + (symbol is ParameterSymbol ? $"mark '{symbol.Name}' '@escaping'" : "mark the parameter it names '@escaping'")
                );
                return;
            }

            if (!_allowed.Contains(access)) {
                diagnostics.Error(
                    ErrorCode.ERR_Escapes,
                    access.Syntax.Span,
                    $"'{symbol.Name}' is a closure that is not @escaping, so it can only be called, or passed to a parameter that is not @escaping either; "
                    + (symbol is ParameterSymbol ? "mark it '@escaping' to store or return it" : "mark the parameter it names '@escaping' to store or return it")
                );
            }
        }
    }
}
