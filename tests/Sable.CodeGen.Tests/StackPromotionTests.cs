using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     docs/04 §3.7: objects that do not outlive their frame are made in it.
///     Nothing observable changes — the deinit runs at the last release, in
///     order, and nothing leaks — only where the memory came from.
/// </summary>
public sealed class StackPromotionTests {
    [Fact]
    public void APromotedObjectIsDeinitedWhereItWouldHaveBeen() =>
        Assert.Equal(
            "made a\nmade b\nsum 3\nbye b\nbye a\nmade c\nescaped c\nafter\nbye c\n",
            AssertAgree(
                Program(
                    """
                    class Noisy {
                        var name: String
                        var value: Int
                        init(_ name: String, _ value: Int) {
                            self.name = name
                            self.value = value
                            print("made " + name)
                        }
                        deinit {
                            print("bye " + name)
                        }
                    }

                    fn sum(_ x: Noisy, _ y: Noisy) -> Int {
                        return x.value + y.value
                    }

                    fn local() {
                        let a = Noisy("a", 1)
                        let b = Noisy("b", 2)
                        print("sum \(sum(a, b))")
                    }

                    fn escaping() -> Noisy {
                        let c = Noisy("c", 3)
                        print("escaped " + c.name)
                        return c
                    }
                    """,
                    """
                        local()
                        let kept = escaping()
                        print("after")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void AnInlineValueOutgrowingItsFrameMovesToTheHeap() =>
        Assert.Equal(
            "200 1\n",
            AssertAgree(
                Program(
                    """
                    @inline_layout
                    struct Bag {
                        var items: U8[>..]
                    }

                    fn fill() -> String {
                        var bag = Bag { items: [] }
                        for i in 0..<200 {
                            bag.items.append(U8(i % 7))
                        }
                        var ones = 0
                        for item in bag.items {
                            if item == 1 {
                                ones = ones + 1
                            }
                        }
                        return "\(bag.items.count) \(ones / 29)"
                    }
                    """,
                    "    print(fill())"
                )
            ).Stdout
        );
}
