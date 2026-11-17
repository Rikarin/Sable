using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>Generic functions and types, monomorphised: native and interpreted alike.</summary>
public sealed class GenericTests {
    [Fact]
    public void GenericFunctionsTypesAndProtocolCalls() =>
        Assert.Equal(
            "42 seven fine ?\nsquare 16, rect 10, 13\ncba\n",
            AssertAgree(
                Program(
                    """
                    struct Box<T> {
                        var item: T
                        fn get() -> T { return item }
                        fn replaced(with value: T) -> Box<T> { return Box<T> { item: value } }
                    }

                    union Outcome<T, E> {
                        Ok(T)
                        Err(E)
                    }

                    fn unwrapOr<T, E>(_ outcome: Outcome<T, E>, _ fallback: T) -> T {
                        return match outcome {
                            case .Ok(let value) => value
                            case .Err(_) => fallback
                        }
                    }

                    protocol Shape {
                        fn area() -> Int
                        fn name() -> String
                    }

                    struct Square: Shape {
                        var side: Int
                        fn area() -> Int { return side * side }
                        fn name() -> String { return "square" }
                    }

                    struct Rect: Shape {
                        var w: Int
                        var h: Int
                        fn area() -> Int { return w * h }
                        fn name() -> String { return "rect" }
                    }

                    fn describe<S: Shape>(_ shape: S) -> String {
                        return shape.name() + " \(shape.area())"
                    }

                    fn total<S: Shape>(_ shapes: S[>..]) -> Int {
                        var sum = 0
                        for s in shapes {
                            sum = sum + s.area()
                        }
                        return sum
                    }

                    fn reversed<T>(_ items: T[>..]) -> T[>..] {
                        var result: T[>..] = []
                        var i = items.count - 1
                        while i >= 0 {
                            result.append(items[i])
                            i = i - 1
                        }
                        return result
                    }
                    """,
                    """
                        let b = Box<Int> { item: 7 }
                        let s = Box<String> { item: "seven" }
                        let ok: Outcome<String, Int> = Outcome.Ok("fine")
                        let bad: Outcome<String, Int> = Outcome.Err(3)
                        print("\(b.replaced(with: b.get() * 6).item) \(s.get()) \(unwrapOr(ok, "?")) \(unwrapOr(bad, "?"))")
                        let squares: Square[>..] = [Square { side: 2 }, Square { side: 3 }]
                        print(describe(Square { side: 4 }) + ", " + describe(Rect { w: 2, h: 5 }) + ", \(total(squares))")
                        let letters: String[>..] = ["a", "b", "c"]
                        var joined = ""
                        for l in reversed(letters) {
                            joined = joined + l
                        }
                        print(joined)
                    """
                )
            ).Stdout
        );
}
