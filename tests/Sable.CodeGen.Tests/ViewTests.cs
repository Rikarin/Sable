using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Safe typed views over memory (#311, docs/02 §2.4 and §2.5): a slice of
///     plain values seen as another plain type's, and back, with nothing
///     copied, read and written where the elements are; and references a
///     mutating member returns into its receiver, which hold an access to it
///     until their last use and cannot outlive it. Natively and interpreted.
/// </summary>
[Native]
public sealed class ViewTests {
    const string Types = """
                         struct Vector3 {
                             var x: F32
                             var y: F32
                             var z: F32
                         }

                         struct Node {
                             var v: Int
                             mutating fn bump() { v += 1 }
                         }

                         struct Chunk {
                             var items: Node[>..]
                             var one: Node

                             mutating fn at(_ i: Int) -> Node& {
                                 return &self.items[i]
                             }

                             mutating fn first() -> Node& {
                                 return &self.one
                             }
                         }

                         fn scale(_ xs: inout F32[:], by k: F32) {
                             var i = 0
                             while i < xs.count {
                                 xs[i] = xs[i] * k
                                 i += 1
                             }
                         }
                         """;

    static void Prints(string expected, string body, string declarations = "") =>
        Assert.Equal(expected, AssertAgree(Program(Types + "\n" + declarations, body)).Stdout);

    static void Rejects(string body, string message, string declarations = "") {
        var result = Pipeline.Compile(new SourceText(Program(Types + "\n" + declarations, body), "test.sb"));
        Assert.False(result.Succeeded, "expected the program to be rejected");
        Assert.Contains(result.Errors, e => e.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void ASliceOfVectorsIsSeenAsFloatsAndBackWithNothingCopied() =>
        Prints(
            "6 5.0 6.0 0\n[1, 2, 3, 4, 5, 6, 7, 8]\n[513, 1027, 1541, 2055]\n[4.0, 5.0, 6.0]\n24\ntrue\n",
            """
                let points: Vector3[>..] = [Vector3 { x: 1, y: 2, z: 3 }, Vector3 { x: 4, y: 5, z: 6 }]
                let n = Runtime.allocationCount()
                let floats = points.reinterpreted(as: F32.self)
                let back = floats.reinterpreted(as: Vector3.self)
                let made = Runtime.allocationCount() - n
                print("\(floats.count) \(floats[4]) \(back[1].z) \(made)")
                let words: U32[>..] = [0x0403_0201, 0x0807_0605]
                print(words.bytes)
                print(words.reinterpreted(as: U16.self))
                print(points[1..<2].reinterpreted(as: F32.self))
                print(points.bytes.count)
                print(back.count == 2 && back[0].x == points[0].x && back[1].z == points[1].z)
            """
        );

    [Fact]
    public void AViewOfAPlaceChangesTheElementsWhereTheyAre() =>
        Prints(
            "50.0 5.0\n2.0 12.0\n12.0\n[256, 0]\n9 [1, 0, 0, 0, 9, 0, 0, 0]\n12.0 -1.0\n0 0 0\n",
            """
                var points: Vector3[>..] = [Vector3 { x: 1, y: 2, z: 3 }, Vector3 { x: 4, y: 5, z: 6 }]
                let before = points.reinterpreted(as: F32.self)
                points.reinterpreted(as: F32.self)[4] = 50
                print("\(points[1].y) \(before[4])")
                var n = Runtime.allocationCount()
                scale(&points.reinterpreted(as: F32.self), by: 2)
                let a = Runtime.allocationCount() - n
                print("\(points[0].x) \(points[1].z)")
                n = Runtime.allocationCount()
                points.reinterpreted(as: F32.self).reverse()
                let b = Runtime.allocationCount() - n
                print(points[0].x)
                var halves: U16[>..] = [0, 0]
                halves.reinterpreted(as: U8.self)[1] = 1
                print(halves)
                var fixed: U32[2] = [1, 2]
                n = Runtime.allocationCount()
                fixed.reinterpreted(as: U8.self)[4] = 9
                let c = Runtime.allocationCount() - n
                print("\(fixed[1]) \(fixed.bytes)")
                var copy = points
                copy.reinterpreted(as: F32.self)[0] = -1
                print("\(points[0].x) \(copy[0].x)")
                print("\(a) \(b) \(c)")
            """
        );

    [Fact]
    public void ABufferIsGivenBackAsAnArrayOnlyOfItsOwnSize() =>
        Prints(
            "[1, 2, 3, 4]\n[1, 2, 3, 4, 5]\n[513, 1027]\n",
            """
                let halves: U16[>..] = [0x0201, 0x0403]
                var bytes = halves.reinterpreted(as: U8.self).toArray()
                print(bytes)
                bytes.append(5)
                print(bytes)
                print(halves)
            """
        );

    [Theory]
    [InlineData("_ = [1, 2, 3].reinterpreted(as: U8.self)[0..<3].reinterpreted(as: U16.self)", "a slice reinterpreted as a type its bytes are not a whole number of")]
    [InlineData("let points: Vector3[>..] = [Vector3 { x: 1, y: 2, z: 3 }, Vector3 { x: 4, y: 5, z: 6 }]\n    _ = points[1..<2].reinterpreted(as: U64.self)", "a slice reinterpreted as a type its bytes are not a whole number of")]
    [InlineData("_ = ([1, 2, 3, 4] as U8[>..])[1..<3].reinterpreted(as: U16.self)", "a slice reinterpreted as a type it does not start at a multiple of the stride of")]
    public void AViewOfBytesThatAreNotWholeValuesTraps(string line, string message) {
        var execution = AssertAgree(Program(Types, "    " + line));
        Assert.Contains($"fatal error: {message}", execution.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AViewGivenBackWithAnotherCountTraps() {
        var execution = AssertAgree(Program(Types + """

                                                    fn replace(_ xs: inout U8[:]) {
                                                        xs = [1, 2, 3, 4, 5, 6, 7, 8]
                                                    }
                                                    """, """
                                                             var words: U32[>..] = [7]
                                                             replace(&words.reinterpreted(as: U8.self))
                                                         """));
        Assert.Contains("fatal error: a view of another count of bytes given back", execution.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AMutatingMemberReturnsAReferenceIntoItsReceiver() =>
        Prints(
            "7 70\n21\n100\n6\n6 9\n",
            """
                var c = Chunk { items: [Node(v: 1), Node(v: 2)], one: Node(v: 7) }
                let r = c.first()
                let seen = r.v
                r.v = 70
                print("\(seen) \(c.one.v)")
                let s = c.at(1)
                s.v = 20
                s.bump()
                print(c.items[1].v)
                c.at(0).v = 10
                c.at(0).bump()
                c.at(0) = Node(v: 100)
                print(c.items[0].v)
                var xs: Node[>..] = [Node(v: 5)]
                let e = &xs[0]
                e.v = 6
                print(xs[0].v)
                let copy = xs
                let f = &xs[0]
                f.v = 9
                print("\(copy[0].v) \(xs[0].v)")
            """
        );

    [Fact]
    public void AReferenceIntoALocalCannotBeReturned() =>
        Rejects(
            "",
            "cannot return a reference rooted in local 'c'",
            """
            fn escapes() -> Node& {
                var c = Chunk { items: [Node(v: 1)], one: Node(v: 2) }
                return c.at(0)
            }
            """
        );

    [Fact]
    public void AnElementReferenceIntoALocalCannotBeReturned() =>
        Rejects(
            "",
            "cannot return a reference rooted in local 'xs'",
            """
            fn escapes() -> Node& {
                var xs: Node[>..] = [Node(v: 1)]
                return &xs[0]
            }
            """
        );

    [Fact]
    public void AReferenceCannotOutliveItsReceiversScope() =>
        Rejects(
            """
                var outer = Chunk { items: [Node(v: 1)], one: Node(v: 2) }
                var r = outer.at(0)
                if r.v > 0 {
                    var inner = Chunk { items: [Node(v: 3)], one: Node(v: 4) }
                    r = inner.at(0)
                }
                r.v = 5
            """,
            "this reference refers to local 'inner', which is gone"
        );

    [Theory]
    [InlineData("c.items.append(Node(v: 2))")]
    [InlineData("print(c.items.count)")]
    [InlineData("let copy = c\n    print(copy.one.v)")]
    [InlineData("_ = c.at(0)")]
    public void TheReceiverIsNotUsedWhileAReferenceIntoItIs(string use) =>
        Rejects(
            $$"""
                  var c = Chunk { items: [Node(v: 1), Node(v: 2)], one: Node(v: 3) }
                  let r = c.at(1)
                  {{use}}
                  r.v = 4
              """,
            "'c' is used while a reference into it, made at line"
        );

    [Fact]
    public void TheArrayIsNotUsedWhileAReferenceToItsElementIs() =>
        Rejects(
            """
                var xs: Node[>..] = [Node(v: 1)]
                let first = &xs[0]
                xs.append(Node(v: 2))
                first.v = 3
            """,
            "'xs' is used while a reference into it, made at line"
        );

    [Fact]
    public void AnInoutReceiverIsHeldTooInTheMemberThatReturnsIntoIt() =>
        Rejects(
            "",
            "'self' is used while a reference into it",
            """
            extension Chunk {
                mutating fn grows() -> Node& {
                    let r = &self.items[0]
                    self.items.append(Node(v: 9))
                    return r
                }
            }
            """
        );

    [Theory]
    [InlineData("let r = &h.items[0]\n    r.v = 2")]
    [InlineData("let s = h.chunk.at(0)\n    s.v = 3")]
    public void AReferenceIntoAnObjectsArrayIsNotKept(string body) =>
        Rejects(
            "    let h = Holder()\n    " + body,
            "a reference into an element of an object's or a global's array is used only where it is made",
            """
            class Holder {
                var chunk = Chunk { items: [Node(v: 1)], one: Node(v: 2) }
                var items: Node[>..] = [Node(v: 1)]
            }
            """
        );

    [Fact]
    public void AReferenceIntoAnObjectsArrayIsUsedWhereItIsMade() =>
        Prints(
            "4\n",
            """
                let h = Holder()
                h.chunk.at(0).v = 4
                print(h.chunk.items[0].v)
            """,
            """
            class Holder {
                var chunk = Chunk { items: [Node(v: 1)], one: Node(v: 2) }
            }
            """
        );
}
