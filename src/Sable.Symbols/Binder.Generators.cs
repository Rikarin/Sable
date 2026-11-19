using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     Generators (spec/SYNTAX.md §5, "Generators"): a body that yields, in a
///     function, method, computed property or closure whose result is
///     <c>some Sequence&lt;T&gt;</c>, <c>some Iterator&lt;T&gt;</c> or, async,
///     <c>some AsyncSequence&lt;T&gt;</c> or <c>some AsyncIterator&lt;T&gt;</c>.
///     Each <c>yield</c> gives a T; <c>return</c>, with no value, ends it.
/// </summary>
public sealed partial class Binder {
    /// <summary>The generator whose body is being bound; null in any other body.</summary>
    GeneratorSymbols? _generator;

    /// <summary>
    ///     Why the body being bound may not yield, when it does and is not a
    ///     generator: said at each <c>yield</c>.
    /// </summary>
    string? _notGenerator;

    /// <summary>Depth of <c>defer</c> bodies being bound, which run as their scope ends and so cannot yield.</summary>
    int _deferDepth;

    /// <summary>
    ///     True when a body yields: a <c>yield</c> in it, and not in a closure
    ///     or a local function inside it, which are bodies of their own.
    /// </summary>
    internal static bool Yields(SyntaxNode body) {
        var pending = new Stack<SyntaxNode>();
        pending.Push(body);
        while (pending.Count > 0) {
            var node = pending.Pop();
            if (node is YieldStatementSyntax) {
                return true;
            }

            foreach (var child in node.ChildNodes()) {
                if (child is ClosureExpressionSyntax or BraceClosureExpressionSyntax or DeclarationStatementSyntax) {
                    continue;
                }

                pending.Push(child);
            }
        }

        return false;
    }

    /// <summary>True for a declared function whose body yields: its call runs nothing, so neither waits nor throws.</summary>
    static bool IsGenerator(FunctionSymbol function) =>
        function.DeclaringSyntax.FirstOrDefault() switch {
            FunctionDeclarationSyntax { Body: { } body } => Yields(body),
            AccessorSyntax { Body: { } body } => Yields(body),
            _ => false,
        };

    /// <summary>
    ///     Starts binding a body: when it yields, the generator its result
    ///     makes it — or, when its result makes it none, why not, for each
    ///     yield to say.
    /// </summary>
    (GeneratorSymbols? Generator, string? NotGenerator) StartGenerator(FunctionSymbol function, TypeSymbol result, SyntaxNode body) {
        if (!Yields(body)) {
            return (null, null);
        }

        if (result.IsError) {
            return (null, null);
        }

        var what = function.Name.Length == 0 ? "this closure" : $"'{function.Name}'";
        if (function.FunctionKind is FunctionKind.Initializer or FunctionKind.Deinitializer or FunctionKind.Subscript) {
            return (null, $"{what} is not a function, method, computed property or closure whose result is a sequence it could yield");
        }

        if (_compilation.GeneratorKindOf(result) is not var (kind, element)) {
            return (null, result is OpaqueTypeSymbol { Arguments.IsEmpty: true } bare
                ? $"{what} yields, so its result must name what it yields: '{bare.ToDisplayString()}<T>'"
                : $"{what} yields, so its result must be 'some Sequence<T>' or 'some Iterator<T>' (or, async, 'some AsyncSequence<T>' or 'some AsyncIterator<T>'), "
                  + (result.Equals(Core.Void) ? "but it returns nothing" : $"not '{result.ToDisplayString()}'"));
        }

        var isAsync = kind is GeneratorKind.AsyncSequence or GeneratorKind.AsyncIterator;
        if (isAsync != function.IsAsync) {
            return (null, function.IsAsync
                ? $"{what} is async, so what it yields is waited for: its result must be 'some AsyncSequence<T>' or 'some AsyncIterator<T>'"
                : $"{what} is not async, so it cannot give '{result.ToDisplayString()}', whose elements are waited for: declare it 'async', or give 'some Sequence<T>'");
        }

        // Its arguments are captured by value as it is called, and the body
        // runs later: there is no caller's variable left to change.
        foreach (var parameter in function.Parameters) {
            if (parameter.Modifier is ParameterModifier.InOut or ParameterModifier.Out && parameter.DeclaringSyntax.FirstOrDefault() is { } declared) {
                _diagnostics.Error(
                    ErrorCode.ERR_InoutParameterInGenerator,
                    declared.Span,
                    $"'{parameter.Name}' is '{(parameter.Modifier == ParameterModifier.InOut ? "inout" : "out")}', but {what} yields: "
                    + "a generator's body runs as its elements are asked for, after the call has returned, so it takes its arguments by value"
                );
            }
        }

        // 'next()' is what would throw, and Iterator's does not: an error
        // from the body has nowhere to go but the body's own 'catch'.
        if (function.ThrownType is { } thrown && !thrown.Equals(Core.Never)) {
            _diagnostics.Error(
                ErrorCode.ERR_ThrowingGenerator,
                function.DeclaringSyntax.FirstOrDefault() is FunctionDeclarationSyntax { ThrowsSpec: { } spec } ? spec.Span : body.Span,
                $"{what} yields, so its errors would come out of its iterator's 'next()', which an Iterator's does not let out: "
                + "catch them in the body, or yield what each step made of them ('Outcome<T, E>')"
            );
        }

        var generator = _compilation.DeclareGenerator(function, kind, element);
        function.HiddenResultType = generator.Result;

        // A computed property's 'some Sequence<T>' hides what its getter makes.
        if (result is OpaqueTypeSymbol { Origin: FieldSymbol property } && ReferenceEquals(function.ContainingSymbol, property)) {
            property.HiddenType = generator.Result;
        }
        return (generator, null);
    }

    BoundStatement BindDefer(DeferStatementSyntax defer) {
        _deferDepth++;
        try {
            return Record(defer, new BoundDefer(defer, BindStatement(defer.Body)));
        } finally {
            _deferDepth--;
        }
    }

    BoundStatement BindYield(YieldStatementSyntax statement) {
        if (_generator is not { } generator) {
            _diagnostics.Error(
                ErrorCode.ERR_YieldOutsideGenerator,
                statement.YieldKeyword.Span,
                _notGenerator ?? "'yield' gives a generator's next element, and is written only in the body of one: "
                + "a function, method, computed property or closure whose result is 'some Sequence<T>' or 'some Iterator<T>'"
            );
            return Record(statement, new BoundYield(statement, BindExpression(statement.Expression)));
        }

        if (_deferDepth > 0) {
            _diagnostics.Error(
                ErrorCode.ERR_YieldInDefer,
                statement.YieldKeyword.Span,
                "a 'defer' body runs as its scope is left — when the generator finishes, or its iterator is dropped — and cannot give an element then"
            );
        }

        var value = Convert(BindExpression(statement.Expression, generator.Element), generator.Element, statement.Expression);
        return Record(statement, new BoundYield(statement, value));
    }
}
