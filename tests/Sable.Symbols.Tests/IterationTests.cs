namespace Sable.Symbols.Tests;

/// <summary>
///     What <c>for</c> and <c>for await</c> accept: the sequences the compiler
///     knows by sight, and any type conforming to <c>Sequence</c> or
///     <c>AsyncSequence</c> (spec/SYNTAX.md §14, §19).
/// </summary>
public class IterationTests {
    const string Numbers = """
                           struct Numbers: Sequence {
                               alias Iter = NumbersIterator
                               fn makeIterator() -> NumbersIterator { return NumbersIterator { left: 3 } }
                           }
                           struct NumbersIterator: Iterator {
                               alias Element = Int
                               var left: Int
                               mutating fn next() -> Int? {
                                   if left == 0 { return none }
                                   left = left - 1
                                   return left
                               }
                           }
                           struct NotASequence {
                               fn makeIterator() -> NumbersIterator { return NumbersIterator { left: 3 } }
                           }
                           struct Ticks: AsyncSequence {
                               alias Iter = TicksIterator
                               fn makeAsyncIterator() -> TicksIterator { return TicksIterator { left: 2 } }
                           }
                           struct TicksIterator: AsyncIterator {
                               alias Element = Int
                               var left: Int
                               mutating fn next() async -> Int? {
                                   if left == 0 { return none }
                                   left = left - 1
                                   return left
                               }
                           }
                           """;

    static IReadOnlyList<Diagnostic> Errors(string body, bool isAsync = false) {
        var source = $"module M\n{Numbers}\nfn probe(_ n: Numbers, _ other: NotASequence, _ maybe: Numbers?, _ items: Int[], _ ticks: Ticks){(isAsync ? " async" : "")} {{\n{body}\n}}";
        return [..Compilation.Create(source).GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error)];
    }

    [Fact]
    public void ASequenceGivesItsIteratorsElements() {
        var errors = Errors("for x in n { let y: Int = x }\nfor c in \"abc\" { let d: Char = c }");
        Assert.Empty(errors);
    }

    [Fact]
    public void HavingTheMembersIsNotConforming() {
        var error = Assert.Single(Errors("for x in other { }"));
        Assert.Equal(ErrorCode.ERR_NotIterable, error.Code);
        Assert.Contains("does not conform to 'Sequence'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOptionalSequenceIsUnwrappedFirst() {
        var error = Assert.Single(Errors("for x in maybe { }"));
        Assert.Contains("unwrap it", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForAwaitNeedsAnAsyncSequence() {
        var error = Assert.Single(Errors("for await x in items { }", isAsync: true));
        Assert.Equal(ErrorCode.ERR_NotIterable, error.Code);
        Assert.Contains("is not an async sequence", error.Message, StringComparison.Ordinal);

        Assert.Contains("is not an async sequence", Assert.Single(Errors("for await x in n { }", isAsync: true)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForAwaitGoesThroughAnAsyncSequenceInAnAsyncContext() {
        Assert.Empty(Errors("for await x in ticks where x > 0 { let y: Int = x }", isAsync: true));

        var error = Assert.Single(Errors("for await x in ticks { }"));
        Assert.Equal(ErrorCode.ERR_AwaitOutsideAsync, error.Code);
    }

    [Fact]
    public void AnAsyncSequenceIsNotGoneThroughWithoutAwait() =>
        Assert.Contains("does not conform to 'Sequence'", Assert.Single(Errors("for x in ticks { }", isAsync: true)).Message, StringComparison.Ordinal);
}
