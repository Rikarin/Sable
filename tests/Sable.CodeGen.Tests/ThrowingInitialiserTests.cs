using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Initialisers that throw (#287): a struct's, which builds self field
///     by field, and a class's, which builds an object its allocator made —
///     through a superclass's initialiser and a delegating one — generic,
///     async, and met as a protocol's requirement. A throw part way through
///     destroys the fields set so far and nothing else, and an object whose
///     initialiser threw is let go of without its deinit. Native and
///     interpreted, held to the same output and to freeing everything.
/// </summary>
public sealed class ThrowingInitialiserTests {
    const string Declarations = """
                                struct Fail: Error { let at: Int }

                                class Tracked {
                                    let name: String
                                    init(_ name: String) {
                                        self.name = name
                                        print("made \(name)")
                                    }
                                    deinit { print("gone \(name)") }
                                }

                                fn check(_ step: Int, _ failAt: Int) throws(Fail) {
                                    if step == failAt {
                                        throw Fail { at: step }
                                    }
                                }
                                """;

    [Fact]
    public void AStructsInitialiserDestroysTheFieldsItHadSet() =>
        Assert.Equal(
            "caught 0\nmade a\ngone a\ncaught 1\nmade a\nmade b\ngone a\ngone b\ncaught 2\n"
            + "made a\nmade b\nmade c\ngone a\ngone b\ngone c\ncaught 3\n"
            + "made a\nmade b\nmade c\nok c\ngone a\ngone b\ngone c\n",
            AssertAgree(
                Program(
                    Declarations + """

                                   struct Three {
                                       var a: Tracked
                                       var b: Tracked
                                       var c: Tracked
                                       init(failAt: Int) throws(Fail) {
                                           try check(0, failAt)
                                           self.a = Tracked("a")
                                           try check(1, failAt)
                                           self.b = Tracked("b")
                                           try check(2, failAt)
                                           self.c = Tracked("c")
                                           try check(3, failAt)
                                       }
                                   }
                                   """,
                    """
                        for failAt in [0, 1, 2, 3, 4] {
                            do {
                                let three = try Three(failAt: failAt)
                                print("ok \(three.c.name)")
                            } catch {
                                print("caught \(error.at)")
                            }
                        }
                    """
                )
            ).Stdout
        );

    [Fact]
    public void AFieldSetOnOnlySomePathsIsDestroyedWhereItWasSet() =>
        Assert.Equal(
            "made early\ngone early\ncaught 1\ncaught 1\nmade early\nmade late\ngone early\nok late\ngone late\n",
            AssertAgree(
                Program(
                    Declarations + """

                                   struct Either {
                                       var a: Tracked
                                       init(early: Bool, failAt: Int) throws(Fail) {
                                           if early {
                                               self.a = Tracked("early")
                                           }
                                           try check(1, failAt)
                                           self.a = Tracked("late")
                                       }
                                   }
                                   """,
                    """
                        for (early, failAt) in [(true, 1), (false, 1), (true, 2)] {
                            do {
                                let either = try Either(early: early, failAt: failAt)
                                print("ok \(either.a.name)")
                            } catch {
                                print("caught \(error.at)")
                            }
                        }
                    """
                )
            ).Stdout
        );

    /// <summary>
    ///     A subclass's initialiser sets its own fields and then calls its
    ///     superclass's: a throw before, inside or after that call, and a
    ///     convenience initialiser's before or after it delegates. Neither
    ///     deinit runs for an object whose initialiser threw; one made whole
    ///     goes as any does.
    /// </summary>
    [Fact]
    public void AClassInitialiserDestroysThePartlyBuiltObject() =>
        Assert.Equal(
            "made y\nmade x\ngone y\ngone x\ncaught 10\nmade y\ngone y\ncaught 20\n"
            + "made y\nmade x\ngone y\ngone x\ncaught 21\ncaught 30\n"
            + "made y\nmade x\ngone y\ngone x\ncaught 31\n"
            + "made y\nmade x\nok y\nderived deinit\nbase deinit\ngone y\ngone x\n",
            AssertAgree(
                Program(
                    Declarations + """

                                   open class Base {
                                       var x: Tracked
                                       init(failAt: Int) throws(Fail) {
                                           self.x = Tracked("x")
                                           try check(10, failAt)
                                       }
                                       deinit { print("base deinit") }
                                   }

                                   class Derived: Base {
                                       var y: Tracked
                                       init(failAt: Int) throws(Fail) {
                                           self.y = Tracked("y")
                                           try check(20, failAt)
                                           try super.init(failAt: failAt)
                                           try check(21, failAt)
                                       }
                                       convenience init(twice failAt: Int) throws(Fail) {
                                           try check(30, failAt)
                                           try self.init(failAt: failAt)
                                           try check(31, failAt)
                                       }
                                       deinit { print("derived deinit") }
                                   }
                                   """,
                    """
                        for failAt in [10, 20, 21, 30, 31, 99] {
                            do {
                                let derived = try Derived(twice: failAt)
                                print("ok \(derived.y.name)")
                            } catch {
                                print("caught \(error.at)")
                            }
                        }
                    """
                )
            ).Stdout
        );

    [Fact]
    public void AStructInitialiserThatDelegatesThrowsThroughTheOneItCalls() =>
        Assert.Equal(
            "caught 0\nmade a\ngone a\ncaught 1\nmade a\nok a\ngone a\n",
            AssertAgree(
                Program(
                    Declarations + """

                                   struct One {
                                       var a: Tracked
                                       var label: String
                                       init(_ name: String, failAt: Int) throws(Fail) {
                                           self.a = Tracked(name)
                                           try check(1, failAt)
                                           self.label = name
                                       }
                                       init(failAt: Int) throws(Fail) {
                                           try check(0, failAt)
                                           try self.init("a", failAt: failAt)
                                       }
                                   }
                                   """,
                    """
                        for failAt in [0, 1, 2] {
                            do {
                                let one = try One(failAt: failAt)
                                print("ok \(one.label)")
                            } catch {
                                print("caught \(error.at)")
                            }
                        }
                    """
                )
            ).Stdout
        );

    /// <summary>
    ///     Bare <c>throws</c> and <c>rethrows</c>, a generic type's
    ///     initialiser, an async one, and a protocol's requirement met by a
    ///     throwing initialiser and called through a type parameter.
    /// </summary>
    [Fact]
    public void GenericAsyncAndRequiredInitialisersThrowToo() =>
        Assert.Equal(
            "made a\nbox\nmade b\ngone b\ncaught any\nmade c\neach c\ncaught 3\n"
            + "5\ncaught 4\nmade l\nloaded l\nloader gone l\ngone l\nmade m\ngone m\ncaught 5\ngone c\ngone a\n",
            AssertAgree(
                Program(
                    Declarations + """

                                   struct Box<T> {
                                       var items: T[>..]
                                       var label: String
                                       init(_ item: T, fail: Bool) throws {
                                           self.items = [item]
                                           if fail {
                                               throw Fail { at: 2 }
                                           }
                                           self.label = "box"
                                       }
                                       init(each make: () throws(Fail) -> T) rethrows {
                                           self.items = [try make()]
                                           self.label = "each"
                                       }
                                   }

                                   protocol Seeded {
                                       init(seed: Int) throws(Fail)
                                   }

                                   struct Number: Seeded {
                                       var value: Int
                                       init(seed: Int) throws(Fail) {
                                           try check(4, seed)
                                           self.value = seed
                                       }
                                   }

                                   fn make<T: Seeded>(_ seed: Int) throws(Fail) -> T {
                                       return try T(seed: seed)
                                   }

                                   class Loader {
                                       var held: Tracked
                                       async init(_ name: String, failAt: Int) throws(Fail) {
                                           self.held = Tracked(name)
                                           await Task.yield()
                                           try check(5, failAt)
                                       }
                                       deinit { print("loader gone \(held.name)") }
                                   }

                                   async fn load(_ name: String, failAt: Int) {
                                       do {
                                           let loader = try await Loader(name, failAt: failAt)
                                           print("loaded \(loader.held.name)")
                                       } catch {
                                           print("caught \(error.at)")
                                       }
                                   }
                                   """,
                    """
                        let kept = try! Box(Tracked("a"), fail: false)
                        print(kept.label)
                        do {
                            let lost = try Box(Tracked("b"), fail: true)
                            print(lost.label)
                        } catch {
                            print("caught any")
                        }
                        let each = Box<Tracked>(each: { Tracked("c") })
                        print("each \(each.items[0].name)")
                        do {
                            let failed = try Box<Tracked>(each: { () throws(Fail) -> Tracked in throw Fail { at: 3 } })
                            print(failed.label)
                        } catch {
                            print("caught \(error.at)")
                        }
                        do {
                            let five: Number = try make(5)
                            print(five.value)
                            let four: Number = try make(4)
                            print(four.value)
                        } catch {
                            print("caught \(error.at)")
                        }
                        await load("l", failAt: 0)
                        await load("m", failAt: 5)
                    """
                ).Replace("fn main() {", "fn main() async {")
            ).Stdout
        );
}
