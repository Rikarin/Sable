using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Closures (docs/04 §2.2, item 3): code and a counted context. Locals
///     captured by reference live in boxes the frame and the closures share;
///     parameters, <c>self</c> and capture lists are copied in. Native and
///     interpreted, leak-checked.
/// </summary>
public sealed class ClosureTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    [Fact]
    public void TheCorpusClosureForms() =>
        Prints(
            "10\n6\n15\n3\ntrue\n15\n7\n11\n1\n25\n",
            """
            fn apply(_ value: Int, _ transform: (Int) -> Int) -> Int {
                return transform(value)
            }

            fn applyTwice(_ value: Int, _ transform: (Int) -> Int) -> Int {
                return transform(transform(value))
            }

            fn combine<T, U>(_ value: T, _ project: (T) -> U) -> U {
                return project(value)
            }

            fn doubled(_ n: Int) -> Int {
                let double = (x: Int) => x * 2
                return apply(n, double)
            }

            fn captures(_ n: Int) -> Int {
                let offset = 10
                let add = (x: Int) => x + offset
                return add(n)
            }

            fn explicitCapture(_ n: Int) -> Int {
                let base = 2
                let f = [copied = base] (x: Int) => x + copied
                return f(n)
            }

            fn blockBody(_ n: Int) -> Int {
                let f = (x: Int) => {
                    let doubled = x * 2
                    return doubled + 1
                }
                return f(n)
            }

            fn voidClosure() -> Int {
                var total = 0
                let bump = () => { total = total + 1 }
                bump()
                return total
            }

            fn storedInVariable(_ n: Int) -> Int {
                var transform: (Int) -> Int = x => x
                transform = x => x * x
                return transform(n)
            }
            """,
            """
                print(doubled(5))
                print(apply(5, x => x + 1))
                print(apply(5, { $0 * 3 }))
                print(applyTwice(5) { $0 - 1 })
                print(combine(5, x => x > 0))
                print(captures(5))
                print(explicitCapture(5))
                print(blockBody(5))
                print(voidClosure())
                print(storedInVariable(5))
            """
        );

    [Fact]
    public void AClosureOutlivesTheFrameItCapturedFrom() =>
        Prints(
            "1\n2\n3\n1\n",
            """
            fn counter() -> () -> Int {
                var count = 0
                return () => {
                    count = count + 1
                    return count
                }
            }
            """,
            """
                let next = counter()
                print(next())
                print(next())
                print(next())
                let other = counter()
                print(other())
            """
        );

    [Fact]
    public void ClosuresShareTheBoxesOfWhatTheyCapture() =>
        Prints(
            "hello world!\n3\n",
            """
            fn build() -> String {
                var text = "hello"
                var appends = 0
                let add = (s: String) => {
                    text = text + s
                    appends = appends + 1
                }
                add(" ")
                add("world")
                let shout = () => { add("!") }
                shout()
                print(text)
                return "\(appends)"
            }
            """,
            """
                print(build())
            """
        );

    [Fact]
    public void EachIterationCapturesItsOwnBinding() =>
        Prints(
            "0 1 4 9 \n",
            "",
            """
                var actions: (() -> Int)[>..] = []
                for i in 0..<4 {
                    let square = i * i
                    actions.append(() => square)
                }
                var line = ""
                for action in actions {
                    line = line + "\(action()) "
                }
                print(line)
            """
        );

    [Fact]
    public void ClosuresCaptureParametersSelfAndStrings() =>
        Prints(
            "Ada: 1, Ada: 2\n[a][b]\n",
            """
            struct Greeter {
                var name: String

                fn makeLabeller() -> (Int) -> String {
                    return (n: Int) => name + ": \(n)"
                }
            }

            fn mapAll(_ items: String[>..], _ f: (String) -> String) -> String[>..] {
                var result: String[>..] = []
                for item in items {
                    result.append(f(item))
                }
                return result
            }

            fn wrap(_ left: String, _ right: String) -> (String) -> String {
                return (s: String) => left + s + right
            }
            """,
            """
                let label = Greeter { name: "Ada" }.makeLabeller()
                print(label(1) + ", " + label(2))
                let wrapped = mapAll(["a", "b"], wrap("[", "]"))
                print(wrapped[0] + wrapped[1])
            """
        );

    [Fact]
    public void ANamedFunctionIsAValue() =>
        Prints(
            "12\n7\n",
            """
            fn triple(_ n: Int) -> Int {
                return n * 3
            }

            fn increment(_ n: Int) -> Int {
                return n + 1
            }

            fn apply(_ value: Int, _ transform: (Int) -> Int) -> Int {
                return transform(value)
            }
            """,
            """
                print(apply(4, triple))
                var f: (Int) -> Int = triple
                f = increment
                print(f(6))
            """
        );

    [Fact]
    public void AThrowingClosure() =>
        Prints(
            "4\nfailed: 7\n",
            """
            union Problem {
                Odd(value: Int)
            }

            fn run(_ n: Int, _ f: (Int) throws(Problem) -> Int) throws(Problem) -> Int {
                return try f(n)
            }
            """,
            """
                let halve: (Int) throws(Problem) -> Int = n => {
                    if n % 2 == 1 {
                        throw Problem.Odd(value: n)
                    }
                    return n / 2
                }
                do {
                    print(try run(8, halve))
                    print(try run(7, halve))
                } catch .Odd(let value) {
                    print("failed: \(value)")
                }
            """
        );
}
