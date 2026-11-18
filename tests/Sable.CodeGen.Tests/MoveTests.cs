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
