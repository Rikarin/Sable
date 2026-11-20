using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Move checking (docs/04 §3.2): <c>@noncopy</c> values are moved, never
///     copied; their deinit runs exactly once; and a use after a move is an
///     error that names the move.
/// </summary>
public sealed class MoveTests {
    const string Handle = """
                          @noncopy struct Handle {
                              var id: Int

                              fn describe() -> String {
                                  return "handle \(id)"
                              }

                              deinit {
                                  print("closed \(id)")
                              }
                          }

                          fn acquire(_ id: Int) -> Handle {
                              print("opened \(id)")
                              return Handle { id: id }
                          }

                          fn close(consuming h: Handle) {
                              print("closing " + h.describe())
                          }

                          fn peek(_ h: Handle) -> Int {
                              return h.id
                          }
                          """;

    [Fact]
    public void AResourceIsMovedAndClosedExactlyOnce() =>
        Assert.Equal(
            "opened 1\nhandle 1\n1\nclosing handle 1\nclosed 1\nafter close\nopened 2\nopened 3\nclosed 2\nhandle 3\nend\nclosed 3\n",
            AssertAgree(
                Program(
                    Handle + """

                             fn pass(consuming h: Handle) -> Handle {
                                 return h
                             }
                             """,
                    """
                        let a = acquire(1)
                        let b = a
                        print(b.describe())
                        print(peek(b))
                        close(b)
                        print("after close")
                        var c = acquire(2)
                        c = pass(acquire(3))
                        print(c.describe())
                        print("end")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void ANoncopyableGenericHoldsAResource() =>
        Assert.Equal(
            "opened 1\n1\nopened 2\nreceived\nclosed 2\nend\nclosed 1\n",
            AssertAgree(
                Program(
                    Handle + """

                             struct Box<T: ~Copyable> {
                                 var value: T
                             }

                             fn receive<T>(consuming _ box: Box<T>) where T: ~Copyable {
                                 print("received")
                             }
                             """,
                    """
                        let box = Box<Handle> { value: acquire(1) }
                        print(box.value.id)
                        let other = Box<Handle> { value: acquire(2) }
                        receive(other)
                        print("end")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void AStructHoldingAResourceIsMovedAndClosedOnce() =>
        Assert.Equal(
            "opened 1\nreceived\nclosed 1\nend\n",
            AssertAgree(
                Program(
                    Handle + """

                             struct Holder {
                                 var handle: Handle
                             }

                             fn receive(consuming _ holder: Holder) {
                                 print("received")
                             }
                             """,
                    """
                        let holder = Holder { handle: acquire(1) }
                        receive(holder)
                        print("end")
                    """
                )
            ).Stdout
        );

    /// <summary>
    ///     A method that borrows 'self', called on a move-only field, borrows
    ///     the field where it is — in a local, nested, or through an inout
    ///     parameter — and the deinit still runs once, with the whole (#235).
    /// </summary>
    [Fact]
    public void AMethodOnAMoveOnlyFieldBorrowsItInPlace() =>
        Assert.Equal(
            "opened 1\nhandle 1\nhandle 2\nopened 5\nhandle 5\nend\nclosed 5\nclosed 2\n",
            AssertAgree(
                Program(
                    Handle + """

                             @noncopy struct Box { var value: Handle }
                             @noncopy struct Outer { var box: Box, var tag: Int }

                             fn bump(_ b: inout Box) {
                                 b.value.id = b.value.id + 1
                                 print(b.value.describe())
                             }
                             """,
                    """
                        var box = Box { value: acquire(1) }
                        print(box.value.describe())
                        bump(&box)
                        let outer = Outer { box: Box { value: acquire(5) }, tag: 0 }
                        print(outer.box.value.describe())
                        print("end")
                    """
                )
            ).Stdout
        );

    /// <summary>
    ///     A move-only field of an object is borrowed where it is too —
    ///     through a temporary object, a variable, a parameter, value fields
    ///     inside the object and another object — and the object lives until
    ///     the borrow ends. An initialiser that stores a consumed parameter
    ///     moves it in, so each resource is closed once.
    /// </summary>
    [Fact]
    public void AMethodOnAMoveOnlyFieldOfAnObjectBorrowsItInPlace() =>
        Assert.Equal(
            "opened 3\nclosed 4\nclosed 3\nhandle 3\nopened 1\nopened 2\nhandle 1\nhandle 2\nhandle 1handle 2\n1\nfailed 2\nhandle 1\nend\nclosed 2\nclosed 1\n",
            AssertAgree(
                Program(
                    Handle + """

                             @noncopy struct Box { var value: Handle }
                             class C {
                                 var h: Handle
                                 var box: Box
                                 init(h: Handle, box: Box) { self.h = h; self.box = box }
                             }
                             class Outer {
                                 var c: C
                                 init(c: C) { self.c = c }
                             }
                             fn both(_ c: C) -> String { return c.h.describe() + c.box.value.describe() }
                             fn checked(_ h: Handle) throws(String) -> Int {
                                 if h.id > 1 { throw "failed \(h.id)" }
                                 return h.id
                             }
                             """,
                    """
                        let first = C(h: acquire(3), box: Box { value: Handle { id: 4 } }).h.describe()
                        print(first)
                        let c = C(h: acquire(1), box: Box { value: acquire(2) })
                        print(c.h.describe())
                        print(c.box.value.describe())
                        print(both(c))
                        do {
                            print(try checked(c.h))
                            print(try checked(c.box.value))
                        } catch {
                            print(error)
                        }
                        let o = Outer(c: c)
                        print(o.c.h.describe())
                        print("end")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void AMoveOnlyFieldOfAnObjectStillCannotBeMovedOut() =>
        Rejects(
            Handle + "\nclass C {\n    var h: Handle\n    init(h: Handle) { self.h = h }\n}\n",
            """
                let c = C(h: acquire(1))
                let moved = c.h
                print(moved.id)
            """,
            "cannot be moved out of a field or an element; move the whole value"
        );

    [Fact]
    public void AMoveOnlyFieldStillCannotBeMovedOut() =>
        Rejects(
            Handle + "\n@noncopy struct Box { var value: Handle }\n",
            """
                let box = Box { value: acquire(1) }
                let moved = box.value
                print(moved.id)
            """,
            "cannot be moved out of a field or an element; move the whole value"
        );

    [Fact]
    public void ExplicitMoveOfACopyableValue() =>
        Assert.Equal(
            "moved text\n",
            AssertAgree(
                Program(
                    "",
                    """
                        var text = "moved"
                        let other = move text
                        text = "again"
                        print(other + " " + "text")
                    """
                )
            ).Stdout
        );

    static void Rejects(string declarations, string body, string message) {
        var result = Pipeline.Compile(new SourceText(Program(declarations, body), "test.sb"));
        Assert.False(result.Succeeded, "expected the program to be rejected");
        Assert.Contains(result.Errors, e => e.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void AUseAfterAMoveNamesTheMove() =>
        Rejects(
            Handle,
            """
                let a = acquire(1)
                let b = a
                print(a.describe())
            """,
            "'a' is used after its value was moved out of it, at line"
        );

    [Fact]
    public void AMoveInALoopIsAMoveTwice() =>
        Rejects(
            Handle,
            """
                let a = acquire(1)
                for i in 0..<2 {
                    close(a)
                }
            """,
            "'a' is used after its value was moved out of it"
        );

    [Fact]
    public void ABorrowedParameterCannotBeMoved() =>
        Rejects(
            Handle + """

                     fn steal(_ h: Handle) {
                         close(h)
                     }
                     """,
            "",
            "is borrowed, so it cannot be moved; take it as 'consuming'"
        );

    // A @noncopy parameter is borrowed without saying so, and 'out' parameters lower (#132, #292).
    [Fact]
    public void ANoncopyParameterIsBorrowed() =>
        Assert.Equal(
            "opened 1\n1\n1\nclosed 1\n",
            AssertAgree(
                Program(
                    Handle + """

                             fn look(_ h: Handle) -> Int {
                                 return h.id
                             }
                             """,
                    """
                        let h = acquire(1)
                        print(look(h))
                        print(look(h))
                    """
                )
            ).Stdout
        );

    [Fact]
    public void AReferenceParameterCannotBeMovedOutOf() =>
        Rejects(
            Handle + """

                     fn steal(_ h: Handle&) {
                         close(h)
                     }
                     """,
            "",
            "cannot be moved out of"
        );

    [Fact]
    public void AnOutParameterIsWrittenWhereTheCallerSays() =>
        Assert.Equal(
            "positive 3\nnot\n7\n",
            AssertAgree(
                Program(
                    """
                    fn describe(_ n: Int, out text: String) -> Bool {
                        if n > 0 {
                            text = "positive \(n)"
                            return true
                        }
                        text = "not"
                        return false
                    }

                    fn parse(_ s: String, out value: Int) -> Bool {
                        value = s.count + 6
                        return true
                    }
                    """,
                    """
                        var text: String
                        if describe(3, out text) {
                            print(text)
                        }
                        var other = "old"
                        if !describe(0, out other) {
                            print(other)
                        }
                        var n = 0
                        if parse("x", out n) {
                            print(n)
                        }
                    """
                )
            ).Stdout
        );

    // The implicit Copyable constraint, and '~Copyable' lifting it (#132).
    [Fact]
    public void AMoveOnlyGenericParameterTakesAMoveOnlyValue() =>
        Assert.Equal(
            "opened 1\n1\nopened 2\n2\nclosed 2\nclosed 1\n",
            AssertAgree(
                Program(
                    Handle + """

                             struct Holder<T: ~Copyable> {
                                 var item: T
                             }

                             fn hold<T>(_ x: consuming T) -> Holder<T> where T: ~Copyable {
                                 return Holder(item: x)
                             }

                             fn pass<T: ~Copyable>(_ x: consuming T) -> T {
                                 return x
                             }
                             """,
                    """
                        let a = pass(acquire(1))
                        print(a.id)
                        let b = hold(acquire(2))
                        print(b.item.id)
                    """
                )
            ).Stdout
        );

    [Fact]
    public void ACopyingGenericParameterRefusesAMoveOnlyValueAtTheCall() =>
        Rejects(
            Handle + """

                     struct Holder<T> {
                         var a: T
                         var b: T
                     }

                     fn two<T>(_ x: T) -> Holder<T> {
                         return Holder(a: x, b: x)
                     }
                     """,
            """
                let h = acquire(1)
                let p = two(h)
            """,
            "'Handle' is @noncopy, so it cannot be 'T' of 'two'"
        );

    // A captured move-only variable lives in a box the closure shares (#132, #125).
    [Fact]
    public void AClosureBorrowsAMoveOnlyValueItCaptures() =>
        Assert.Equal(
            "opened 1\nhandle 1\nhandle 1\nclosed 1\n",
            AssertAgree(
                Program(
                    Handle,
                    """
                        let h = acquire(1)
                        let f = () => h.describe()
                        print(f())
                        print(f())
                    """
                )
            ).Stdout
        );

    [Fact]
    public void AMoveOnlyValueCannotBeMovedOnceCaptured() =>
        Rejects(
            Handle,
            """
                let h = acquire(1)
                let f = () => h.id
                print(f())
                close(h)
            """,
            "'h' is captured by a closure, so its @noncopy value"
        );

    [Fact]
    public void AClosureCannotMoveAMoveOnlyValueItCaptures() =>
        Rejects(
            Handle,
            """
                let h = acquire(1)
                let f = () => close(h)
                f()
                f()
            """,
            "'h' is captured by a closure, so its @noncopy value"
        );

    [Fact]
    public void AnExplicitMoveIsCheckedToo() =>
        Rejects(
            "",
            """
                let text = "x"
                let other = move text
                print(text)
            """,
            "'text' is used after its value was moved out of it"
        );
}
