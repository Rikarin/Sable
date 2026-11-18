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
            "1.5\n-1.5\n1.5\n0.0\n",
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

    // ---- 'some P' results and locals --------------------------------------------

    [Fact]
    public void ASomeResultIsTheTypeItsBodyReturns() =>
        // A function's, an '=>' body's, a method's, a generic function's, one
        // passed on as another's, one in an array, and a local's.
        Prints(
            "circle 12.0\nsquare 9.0\ncircle 12.0\nlabel 0.0\n2 12.0\nsquare 25.0\ncircle 3.0\nsquare 16.0\nsquare 4.0\n",
            """
            protocol Shape {
                fn area() -> F64
                fn name() -> String
            }

            struct Circle: Shape {
                var r: F64
                fn area() -> F64 { return 3.0 * r * r }
                fn name() -> String { return "circle" }
            }

            struct Square: Shape {
                var s: F64
                fn area() -> F64 { return s * s }
                fn name() -> String { return "square" }
            }

            class Label: Shape {
                var text: String
                init(_ text: String) {
                    self.text = text
                }
                fn area() -> F64 { return 0.0 }
                fn name() -> String { return text }
            }

            fn circle() -> some Shape {
                return Circle { r: 2.0 }
            }

            fn square() -> some Shape => Square { s: 3.0 }

            fn passedOn() -> some Shape {
                return circle()
            }

            fn label() -> some Shape {
                return Label("label")
            }

            fn same<T: Shape>(_ x: T) -> some Shape {
                return x
            }

            struct Factory {
                var side: F64
                fn make() -> some Shape {
                    return Square { s: side }
                }
            }

            fn describe(_ s: some Shape) -> String {
                return s.name() + " \(s.area())"
            }
            """,
            """
                print(describe(circle()))
                print(describe(square()))
                print(describe(passedOn()))
                print(describe(label()))
                let shapes = [circle(), circle()]
                print("\(shapes.count) \(shapes[1].area())")
                print(describe(same(Square { s: 5.0 })))
                print(describe(same(Circle { r: 1.0 })))
                print(describe(Factory { side: 4.0 }.make()))
                let local: some Shape = Square { s: 2.0 }
                print(describe(local))
            """
        );

    [Fact]
    public void ASomeResultMayReturnItself() =>
        Prints(
            "9\n",
            """
            protocol Shape {
                fn area() -> Int
            }

            struct Square: Shape {
                var side: Int
                fn area() -> Int { return side * side }
            }

            fn down(_ n: Int) -> some Shape {
                if n == 0 {
                    return Square { side: 3 }
                }
                return down(n - 1)
            }
            """,
            "    print(down(3).area())"
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

    // ---- Overloads told apart by labels, and explicit implementations ------------

    [Fact]
    public void OverloadsThatDifferOnlyInLabelsAreTwoFunctions() =>
        Prints(
            "1\n2\n12\n",
            """
            fn g(a x: Int) -> Int { return x }
            fn g(b x: Int) -> Int { return x + 1 }

            protocol Scaled { fn scale(by n: Int) -> Int }

            struct Ten: Scaled {
                fn scale(to n: Int) -> Int { return n }
                fn scale(by n: Int) -> Int { return n + 10 }
            }

            fn scaled<T: Scaled>(_ value: T) -> Int { return value.scale(by: 2) }
            """,
            """
                print(g(a: 1))
                print(g(b: 1))
                print(scaled(Ten()))
            """
        );

    [Fact]
    public void ExplicitImplementationsMeetCollidingRequirements() =>
        Prints(
            "1\npage\n1\n",
            """
            protocol Drawable { fn render() -> Int }
            protocol Printable { fn render() -> String }

            class Shape: Drawable, Printable {
                fn Drawable.render() -> Int { return 1 }
                fn Printable.render() -> String { return "page" }
            }

            fn draw<T: Drawable>(_ x: T) -> Int { return x.render() }
            fn show<T: Printable>(_ x: T) -> String { return x.render() }
            """,
            """
                let s = Shape()
                print(draw(s))
                print(show(s))
                let d: any Drawable = Shape()
                print(d.render())
            """
        );
}
