using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>blackBox(value)</c> (Sable.Core): the value back unchanged, of any
///     type — a counted one too, neither leaked nor freed twice — through an
///     assembly statement the optimiser cannot see into.
/// </summary>
[Native]
public sealed class BlackBoxTests {
    const string Source = """
        module Example

        class Counter {
            var count: Int = 0
        }

        struct Pair {
            var name: String
            var counter: Counter
        }

        fn sum(_ n: Int) -> Int {
            var total = 0
            for i in 0..<blackBox(n) {
                total = total + blackBox(i)
            }
            return total
        }

        fn keep<T>(_ value: T) -> T {
            return blackBox(value)
        }

        fn main() {
            print(blackBox(sum(1000)))
            let counter = blackBox(Counter())
            counter.count = 3
            let pair = blackBox(Pair { name: "a" + "b", counter: counter })
            print("\(pair.name) \(pair.counter.count)")
            let words = blackBox(["x", "y"])
            print(words.count)
            let missing: String? = none
            print(blackBox(missing) == none)
            print(keep("kept") + keep("!"))
        }
        """;

    [Fact]
    public void ItGivesBackWhatItIsGiven() =>
        Assert.Equal("499500\nab 3\n2\ntrue\nkept!\n", AssertAgree(Source).Stdout);

    [Fact]
    public void NativelyItIsAnAssemblyStatementOnTheValuesSlot() {
        var result = Pipeline.Compile(new SourceText(Source, "test.sb"), optimised: true);
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Contains("call void asm sideeffect \"\", \"r,~{memory}\"(ptr", result.LlvmIr!, StringComparison.Ordinal);
    }
}
