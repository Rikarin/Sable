using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Constructs the corpus used before lowering handled them: enum equality,
///     F64 remainder, untyped and union throws, rethrows, function conversions
///     that add a throw, and 'some P' parameters — natively and interpreted.
/// </summary>
public sealed class LoweringGapTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    // ---- Enum equality -------------------------------------------------------

    [Fact]
    public void EnumsWithoutPayloadsCompareByCase() =>
        Prints(
            "true\nfalse\nfalse\ntrue\ntrue\n",
            """
            enum Method { Get, Post, Put, Delete }

            fn same(_ a: Method, _ b: Method) -> Bool { return a == b }

            fn differ(_ a: Method, _ b: Method) -> Bool { return a != b }
            """,
            """
                print(same(Method.Get, .Get))
                print(same(Method.Get, .Delete))
                print(differ(Method.Put, .Put))
                print(differ(Method.Put, .Post))
                let m = Method.Delete
                print(m == Method.Delete)
            """
        );

    [Fact]
    public void AnEnumWithRawValuesComparesByCase() =>
        Prints(
            "true\nfalse\n",
            """
            enum Status: U16 {
                Ok = 200
                Missing = 404
            }
            """,
            """
                let s = Status.Ok
                print(s == Status.Ok)
                print(s == Status.Missing)
            """
        );

    [Fact]
    public void AnEnumsOwnEqualityIsTheOneUsed() =>
        // Every Shade is equal to every other: its '==' says so.
        Prints(
            "true\nfalse\n",
            """
            enum Shade {
                Light
                Dark

                operator == (a: Shade, b: Shade) -> Bool { return true }
            }
            """,
            """
                print(Shade.Light == Shade.Dark)
                print(Shade.Light != Shade.Dark)
            """
        );

    [Fact]
    public void AnEquatableEnumMeetsAGenericConstraint() =>
        Prints(
            "true\nfalse\n",
            """
            enum Suit: Equatable { Hearts, Spades }

            fn has<T: Equatable>(_ values: T[], _ wanted: T) -> Bool {
                for v in values {
                    if v == wanted { return true }
                }
                return false
            }
            """,
            """
                print(has([Suit.Hearts], Suit.Hearts))
                print(has([Suit.Hearts], Suit.Spades))
            """
        );

    // ---- F64 remainder -------------------------------------------------------

    [Fact]
    public void TheRemainderOfFloatsIsFmod() =>
        Prints(
            "1.5\n-1.5\n1.5\n0\n",
            """
            fn rem(_ a: F64, _ b: F64) -> F64 { return a % b }
            """,
            """
                print(rem(7.5, 2.0))
                print(rem(-7.5, 2.0))
                print(rem(7.5, -2.0))
                print(rem(6.0, 3.0))
            """
        );

    // ---- 'some P' parameters -------------------------------------------------

    [Fact]
    public void ASomeParameterIsAnAnonymousGeneric() =>
        // One instance per type passed; one 'some P' passed on to another.
        Prints(
            "point 1\nlabel hi\n<point 1>\n<label hi>\n",
            """
            protocol Describable {
                fn describe() -> String
            }

            struct Point: Describable {
                var x: Int
                fn describe() -> String { return "point \(x)" }
            }

            struct Label: Describable {
                var text: String
                fn describe() -> String { return "label " + text }
            }

            fn describeAny(_ value: some Describable) -> String {
                return value.describe()
            }

            fn bracketed(_ value: some Describable) -> String {
                return "<" + describeAny(value) + ">"
            }
            """,
            """
                print(describeAny(Point(x: 1)))
                print(describeAny(Label(text: "hi")))
                print(bracketed(Point(x: 1)))
                print(bracketed(Label(text: "hi")))
            """
        );

    // ---- Untyped and union throws ----------------------------------------------

    const string Errors = """
                          union IoError { NotFound(path: String), Denied }
                          union NetError { Down(code: Int) }

                          fn read(_ path: String) throws(IoError) -> String {
                              if path == "" { throw IoError.Denied }
                              if path == "?" { throw IoError.NotFound(path: "nowhere") }
                              return path
                          }

                          fn fetch(_ url: String) throws(NetError) -> String {
                              if url == "down" { throw NetError.Down(code: 503) }
                              return url
                          }

                          fn bare(_ path: String) throws -> String {
                              if path == "net" { return try fetch("down") }
                              return try read(path)
                          }

                          fn both(_ path: String) throws(IoError | NetError) -> String {
                              if path == "net" { throw NetError.Down(code: 7) }
                              return try read(path)
                          }
                          """;

    [Fact]
    public void BareThrowsKeepsTheErrorItWas() =>
        Prints(
            "ok\nnot found nowhere\ndown 503\ndenied\n",
            Errors,
            """
                for path in ["ok", "?", "net", ""] {
                    do {
                        print(try bare(path))
                    } catch let e as IoError {
                        match e {
                            case .NotFound(let p): print("not found " + p)
                            case .Denied: print("denied")
                        }
                    } catch let n as NetError {
                        match n {
                            case .Down(let code): print("down \(code)")
                        }
                    } catch {
                        print("other")
                    }
                }
            """
        );

    [Fact]
    public void AnUntypedErrorIsCaughtByACatchAll() =>
        Prints(
            "fallback\n-\nnone\n",
            Errors,
            """
                print(try bare("") catch { "fallback" })
                print(try bare("net") catch { "-" })
                print((try? bare("?")) ?? "none")
            """
        );

    [Fact]
    public void AnInlineCatchBlockRunsItsStatementsBeforeItsValue() =>
        Prints(
            "in the block\ndeferred\nfallback!\n",
            Errors,
            """
                let a = try bare("") catch {
                    defer { print("deferred") }
                    print("in the block")
                    var s = "fallback"
                    s += "!"
                    s
                }
                print(a)
            """
        );

    [Fact]
    public void AUnionOfErrorsCarriesEither() =>
        Prints(
            "x\nnet 7\nio\n",
            Errors,
            """
                for path in ["x", "net", ""] {
                    do {
                        print(try both(path))
                    } catch let n as NetError {
                        match n {
                            case .Down(let code): print("net \(code)")
                        }
                    } catch {
                        print("io")
                    }
                }
            """
        );

    // ---- rethrows and function conversions -------------------------------------

    const string Rethrowing = Errors + """

                              fn twice(_ body: () throws -> Int) rethrows -> Int {
                                  return try body() + body()
                              }

                              fn twiceTyped(_ body: () throws(IoError) -> Int) rethrows -> Int {
                                  return try body() + body()
                              }

                              fn apply(_ f: (Int) throws(IoError) -> Int, _ x: Int) throws(IoError) -> Int {
                                  return try f(x)
                              }

                              fn length(_ path: String) throws(IoError) -> Int {
                                  return (try read(path)).count
                              }
                              """;

    [Fact]
    public void RethrowsThrowsOnlyWhatItsArgumentDoes() =>
        Prints(
            "6\n4\nthrew\n10\nthrew denied\n",
            Rethrowing,
            """
                print(twice(() => 3))
                print(try! twice(() => { return try length("ab") }))
                print(try twice(() => { return try length("") }) catch { -1 } == -1 ? "threw" : "no")
                print(try! twiceTyped(() => { return try length("hello") }))
                do {
                    print(try twiceTyped(() => { return try length("") }))
                } catch .Denied {
                    print("threw denied")
                } catch {
                    print("other")
                }
            """
        );

    [Fact]
    public void AFunctionThatCannotThrowIsPassedWhereOneThatMayIsWanted() =>
        Prints(
            "5\n12\nnot found\n",
            Rethrowing,
            """
                print(try! apply((x: Int) => x + 1, 4))
                let triple = (x: Int) => x * 3
                print(try! apply(triple, 4))
                do {
                    print(try apply((x: Int) => { return try length("?") }, 1))
                } catch .NotFound(let p) {
                    print("not found")
                } catch {
                    print("other")
                }
            """
        );
}
