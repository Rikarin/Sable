using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The language constructs lowering covers, each as a program whose output
///     is checked — natively and in the interpreter, which must agree.
/// </summary>
public sealed class LanguageTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    // ---- Structs and tuples ---------------------------------------------------

    const string Vector = """
                          struct Vec {
                              var x: F64
                              var y: F64

                              fn dot(_ other: Vec) -> F64 { return x * other.x + y * other.y }

                              fn scaled(by f: F64) -> Vec { return Vec(x: x * f, y: y * f) }

                              mutating fn scale(by f: F64) {
                                  x = x * f
                                  y = y * f
                              }
                          }
                          """;

    [Fact]
    public void AMemberwiseInitializerLeavesOutFieldsWithInitialValues() =>
        Prints(
            "1 5 1 2\n3 9 z q\n0 4 30 x\n",
            """
            struct D { var a: Int = 1; var b: Int = 5 }
            struct E { var x: Int; var y: Int = 9; var z: String = "z" }
            struct G<T> { var items: T[] = []; var n: Int = 2 }
            struct L { var k: Int; lazy var big: Int = k * 10 }
            struct F { var name: String = "x" { set { field = newValue + "!" } get => field } }
            """,
            """
                let d = D()
                let e = D(b: 2)
                print("\(d.a) \(d.b) \(e.a) \(e.b)")
                let f = E(x: 3)
                let g = E(x: 3, z: "q")
                print("\(f.x) \(f.y) \(f.z) \(g.z)")
                let h = G<String>(n: 4)
                var l = L(k: 3)
                let p = F()
                print("\(h.items.count) \(h.n) \(l.big) \(p.name)")
            """);

    [Fact]
    public void StructsFieldsAndMethods() =>
        Prints(
            "11.0\n6.0\n3.0\n9.0\n",
            Vector,
            """
                let a = Vec(x: 1.0, y: 2.0)
                let b = Vec(x: 3.0, y: 4.0)
                print(a.dot(b))
                print(a.scaled(by: 3.0).y)
                var c = a
                c.scale(by: 3.0)
                print(c.x)
                c.y = 9.0
                print(c.y)
            """
        );

    [Fact]
    public void AStructIsAValue() =>
        // Copying then changing the copy leaves the original alone.
        Prints(
            "1.0\n5.0\n",
            Vector,
            """
                let a = Vec(x: 1.0, y: 2.0)
                var b = a
                b.x = 5.0
                print(a.x)
                print(b.x)
            """
        );

    [Fact]
    public void AnInitialiserBuildsSelf() =>
        Prints(
            "7\n49\n",
            """
            struct Square {
                var side: Int
                var area: Int
                init(side: Int) {
                    self.side = side
                    self.area = side * side
                }
            }
            """,
            """
                let s = Square(side: 7)
                print(s.side)
                print(s.area)
            """
        );

    [Fact]
    public void NestedStructsAndRecordLiterals() =>
        Prints(
            "3\n4\n12\n",
            """
            struct Point { var x: Int, var y: Int }
            struct Line { var from: Point, var to: Point }
            fn length(_ l: Line) -> Int { return (l.to.x - l.from.x) + (l.to.y - l.from.y) }
            """,
            """
                var line = Line(from: Point { x: 1, y: 2 }, to: Point { x: 3, y: 4 })
                line.to.x = 3
                print(line.to.x)
                print(line.to.y)
                line.to.y = 10
                print(length(line) + 2)
            """
        );

    [Fact]
    public void TuplesAndDestructuring() =>
        Prints(
            "17\n2\n2\n17\n",
            "fn divmod(_ a: Int, _ b: Int) -> (Int, Int) { return (a / b, a % b) }",
            """
                let pair = divmod(87, 5)
                print(pair.0)
                print(pair.1)
                let (q, r) = divmod(87, 5)
                print(r)
                print(q)
            """
        );

    // ---- Unions, enums, match -------------------------------------------------

    const string Shapes = """
                          union Shape {
                              Circle(radius: F64)
                              Rect(width: F64, height: F64)
                              Empty
                          }

                          fn area(_ s: Shape) -> F64 {
                              return match s {
                                  case .Circle(let r) => 3.0 * r * r
                                  case .Rect(let w, let h) => w * h
                                  case .Empty => 0.0
                              }
                          }
                          """;

    [Fact]
    public void UnionsAndMatch() =>
        Prints(
            "12.0\n6.0\n0.0\n",
            Shapes,
            """
                print(area(.Circle(radius: 2.0)))
                print(area(.Rect(width: 2.0, height: 3.0)))
                print(area(Shape.Empty))
            """
        );

    [Fact]
    public void MatchWithLiteralsRangesGuardsAndOrPatterns() =>
        Prints(
            "zero\nsmall\nsmall\nbig even\nbig odd\nnegative\n",
            """
            fn describe(_ n: Int) -> String {
                return match n {
                    case 0 => "zero"
                    case 1...9 => "small"
                    case let k where k > 9 && k % 2 == 0 => "big even"
                    case let k where k > 9 => "big odd"
                    default => "negative"
                }
            }
            """,
            """
                print(describe(0))
                print(describe(3))
                print(describe(9))
                print(describe(10))
                print(describe(11))
                print(describe(-4))
            """
        );

    [Fact]
    public void NestedPatternsAndTuples() =>
        Prints(
            "both\nfirst\nneither\n",
            """
            fn classify(_ a: Bool, _ b: Bool) -> String {
                return match (a, b) {
                    case (true, true) => "both"
                    case (true, false) => "first"
                    case (false, true) => "second"
                    case (false, false) => "neither"
                }
            }
            """,
            """
                print(classify(true, true))
                print(classify(true, false))
                print(classify(false, false))
            """
        );

    [Fact]
    public void PlainEnums() =>
        Prints(
            "2\n0\n",
            """
            enum Direction { North, East, South, West }
            fn turns(_ d: Direction) -> Int {
                return match d {
                    case .North => 0
                    case .East => 1
                    case .South => 2
                    case .West => 3
                }
            }
            """,
            """
                print(turns(.South))
                let d = Direction.North
                print(turns(d))
            """
        );

    [Fact]
    public void IfCaseBindsThePayload() =>
        Prints(
            "radius\n2.0\nnot a circle\n",
            Shapes,
            """
                let one = Shape.Circle(radius: 2.0)
                if case .Circle(let r) = one {
                    print("radius")
                    print(r)
                }
                let two = Shape.Empty
                if case .Circle(let r) = two {
                    print(r)
                } else {
                    print("not a circle")
                }
            """
        );

    // ---- Optionals --------------------------------------------------------------

    [Fact]
    public void OptionalsIfLetCoalesceAndUnwrap() =>
        Prints(
            "4\nnothing\n-1\n4\n",
            """
            fn half(_ n: Int) -> Int? {
                if n % 2 == 0 { return n / 2 }
                return none
            }
            """,
            """
                if let h = half(8) {
                    print(h)
                }
                if let h = half(7) {
                    print(h)
                } else {
                    print("nothing")
                }
                print(half(7) ?? -1)
                print(half(8)!)
            """
        );

    [Fact]
    public void UnwrappingNoneTraps() {
        var run = AssertAgree(Program("fn nothing() -> Int? { return none }", "    print(1)\n    print(nothing()!)"));
        Assert.Equal("1\n", run.Stdout);
        Assert.Equal("fatal error: unwrapped a none\n  at test.sb:6:11\n", run.Stderr);
    }

    [Fact]
    public void GuardLet() =>
        Prints(
            "21\n0\n",
            """
            fn tripled(_ n: Int?) -> Int {
                guard let value = n else { return 0 }
                return value * 3
            }
            """,
            """
                print(tripled(7))
                print(tripled(none))
            """
        );

    // ---- Errors --------------------------------------------------------------

    const string Parsing = """
                           union ParseError { Empty, Negative(value: Int) }

                           fn check(_ n: Int) throws(ParseError) -> Int {
                               if n == 0 { throw ParseError.Empty }
                               if n < 0 { throw ParseError.Negative(value: n) }
                               return n * 10
                           }

                           fn twice(_ n: Int) throws(ParseError) -> Int {
                               let first = try check(n)
                               return first + (try check(n))
                           }
                           """;

    [Fact]
    public void ThrowsDoAndCatch() =>
        Prints(
            "140\nempty\nnegative\n-3\n",
            Parsing,
            """
                do {
                    print(try twice(7))
                    print(try twice(0))
                } catch .Empty {
                    print("empty")
                } catch {
                    print("other")
                }
                do {
                    print(try check(-3))
                } catch .Negative(let v) {
                    print("negative")
                    print(v)
                } catch {
                    print("other")
                }
            """
        );

    [Fact]
    public void TryOptionalForceAndFallback() =>
        Prints(
            "50\n-1\n99\n50\n",
            Parsing,
            """
                print((try? check(5)) ?? -1)
                print((try? check(0)) ?? -1)
                print(try check(-9) catch { 99 })
                print(try! check(5))
            """
        );

    [Fact]
    public void TryBangTraps() {
        var run = AssertAgree(Program(Parsing, "    print(1)\n    print(try! check(0))"));
        Assert.Equal("1\n", run.Stdout);
        Assert.Equal("fatal error: 'try!' caught an error\n  at test.sb:17:16\n", run.Stderr);
    }

    // ---- defer, ranges, loops -------------------------------------------------

    [Fact]
    public void DeferRunsOnEveryWayOut() =>
        Prints(
            "body\nfirst deferred\nsecond deferred\nloop\n0\nend\n0\nloop\n1\nend\n1\nend\n2\nearly\nleaving\n5\n",
            """
            fn early(_ n: Int) -> Int {
                defer { print("leaving") }
                if n > 3 {
                    print("early")
                    return n
                }
                return 0
            }
            """,
            """
                do {
                    defer { print("second deferred") }
                    defer { print("first deferred") }
                    print("body")
                }
                for i in 0..<10 {
                    defer {
                        print("end")
                        print(i)
                    }
                    if i == 2 { break }
                    print("loop")
                    print(i)
                }
                print(early(5))
            """
        );

    [Fact]
    public void ForOverRanges() =>
        Prints(
            "10\n15\n6\n",
            "",
            """
                var sum = 0
                for i in 0..<5 { sum += i }
                print(sum)
                var inclusive = 0
                for i in 1...5 { inclusive += i }
                print(inclusive)
                var evens = 0
                for i in 0..<5 where i % 2 == 0 { evens += i }
                print(evens)
            """
        );

    [Fact]
    public void ConversionsBetweenIntAndF64() =>
        Prints(
            "2.5\n3\n",
            "",
            """
                let n = 5
                print(n as F64 / 2.0)
                print(3.75 as Int)
            """
        );

    [Fact]
    public void StringEquality() =>
        Prints(
            "true\nfalse\nmatched b\n",
            """
            fn name(_ s: String) -> String {
                return match s {
                    case "a" => "matched a"
                    case "b" => "matched b"
                    default => "other"
                }
            }
            """,
            """
                print("abc" == "abc")
                print("abc" == "abd")
                print(name("b"))
            """
        );

    /// <summary>Strings order through Sable.Core's '<', which has no instruction of its own.</summary>
    [Fact]
    public void StringsOrder() =>
        Prints(
            "true false true true false true\n",
            "",
            """
                let a = "2026-03-02"
                let b = "2026-11-17"
                print("\(a < b) \(a > b) \(a <= a) \(b >= a) \("ab" < "a") \("a" < "ab")")
            """
        );

    /// <summary>
    ///     Strings of every length up to twenty, each a prefix of the next or
    ///     differing from it at one byte — around the eight-byte words the
    ///     runtime compares by — and some beyond ASCII, which order by scalar:
    ///     every pair compared, natively as interpreted.
    /// </summary>
    [Fact]
    public void StringsOrderByteByByteAtEveryLength() {
        const string Letters = "abcdefghijklmnopqrstu";
        List<string> words = ["é", "ü", "😀", "aé", "a😀b", "ab\\u{0}"];
        for (var length = 0; length <= 20; length++) {
            words.Add(Letters[..length]);
            foreach (var at in new[] { 0, length / 2, length - 1 }.Where(i => i >= 0 && i < length).Distinct()) {
                var chars = Letters[..length].ToCharArray();
                chars[at] = (char)(chars[at] - 1);
                words.Add(new(chars));
            }
        }

        var expected = new System.Text.StringBuilder();
        var scalars = words.Select(static w => w.Replace("\\u{0}", "\0", StringComparison.Ordinal)).ToList();
        foreach (var a in scalars) {
            foreach (var b in scalars) {
                var order = string.CompareOrdinal(a, b);
                expected.Append(order < 0 ? '<' : order > 0 ? '>' : '=');
            }

            expected.Append('\n');
        }

        Prints(
            expected.ToString(),
            "",
            $$"""
                let words: String[>..] = [{{string.Join(", ", words.Select(static w => $"\"{w}\""))}}]
                for a in words {
                    var line = ""
                    for b in words {
                        line = line + (a < b ? "<" : a > b ? ">" : "=")
                    }
                    print(line)
                }
            """
        );
    }

    /// <summary>
    ///     A default is filled in where the call leaves it off, bound where it
    ///     was declared; a label may pass over defaults to the parameter it names.
    /// </summary>
    [Fact]
    public void DefaultArgumentsAreFilledIn() =>
        Prints(
            "h:443:false:30\nh:80:false:30\nh:443:true:30\nh:443:false:5\nh:8443:true:30\n",
            """
            let standardPort = 443
            fn connect(_ host: String, port: Int = standardPort, secure: Bool = false, timeout: Int = 6 * 5) -> String {
                return "\(host):\(port):\(secure):\(timeout)"
            }
            struct Client {
                var base: Int
                fn connect(_ host: String, port: Int = 8443, secure: Bool = true) -> String {
                    return "\(host):\(port):\(secure):\(base)"
                }
            }
            """,
            """
                print(connect("h"))
                print(connect("h", port: 80))
                print(connect("h", secure: true))
                print(connect("h", timeout: 5))
                print(Client(base: 30).connect("h"))
            """
        );

    /// <summary>
    ///     A store into part of an array element — 'a[i].p.x = v', '+=' too —
    ///     reads the element out, stores into it, and writes it back; a copy of
    ///     the array taken before keeps what it had.
    /// </summary>
    [Fact]
    public void AStoreReachesIntoAnArrayElement() =>
        Assert.Equal(
            "a 20 2\nbee 6 9\nb 1\n",
            AssertAgree("""
                module P
                struct Point { var x: Int
                 var y: Int }
                struct Item { var name: String
                 var at: Point }
                @main
                fn main() {
                    var items: Item[>..] = [Item { name: "a", at: Point { x: 1, y: 2 } }, Item { name: "b", at: Point { x: 3, y: 4 } }]
                    let copy = items
                    items[1].name = "bee"
                    items[0].at.x = 10
                    items[1].at.y += 5
                    var i = 0
                    while i < items.count {
                        items[i].at.x = items[i].at.x * 2
                        i += 1
                    }
                    print("\(items[0].name) \(items[0].at.x) \(items[0].at.y)")
                    print("\(items[1].name) \(items[1].at.x) \(items[1].at.y)")
                    print("\(copy[1].name) \(copy[0].at.x)")
                }
                """).Stdout
        );

    /// <summary>'none' in one branch of '?:' takes the other branch's type, made optional.</summary>
    [Fact]
    public void NoneInAConditionalTakesTheOtherBranchesType() =>
        Assert.Equal(
            "5\n0\nnone\n",
            AssertAgree("""
                module P
                @main
                fn main() {
                    let c = true
                    let x: Int? = c ? 5 : none
                    let y = !c ? 5 : none
                    let z = c ? none : "s"
                    print(x ?? 0)
                    print(y ?? 0)
                    print(z ?? "none")
                }
                """).Stdout
        );

    // ---- Structural unions, error conversion, static properties --------------

    [Fact]
    public void AStructuralUnionHoldsOneOfItsTypesAndNarrows() =>
        Prints(
            "number 2.5\ntext wide\n42.0\nnumber 1.0\ntext 7\n",
            """
            alias Width = F64 | String

            fn describe(_ w: Width) -> String {
                if w is F64 { return "number \(w)" }
                if w is String { return "text " + w }
                return "neither"
            }

            fn size(_ w: Width) -> F64 {
                if w is String { return w.toF64() ?? 0 }
                if w is F64 { return w }
                return 0
            }
            """,
            """
                print(describe(2.5))
                print(describe("wide"))
                let held: Width = "42"
                print(size(held))
                let widths: Width[>..] = [1.0, "7"]
                for w in widths { print(describe(w)) }
            """
        );

    const string Layers = """
                          union StoreError {
                              Down(shard: Int)
                          }

                          union AppError {
                              Missing(key: String)
                              Store(cause: StoreError)
                          }

                          extension AppError: From<StoreError> {
                              static fn from(_ e: StoreError) -> AppError => .Store(cause: e)
                          }

                          fn load(_ shard: Int) throws(StoreError) -> String {
                              if shard > 2 { throw .Down(shard: shard) }
                              return "row \(shard)"
                          }

                          fn fetch(_ shard: Int) throws(AppError) -> String {
                              if shard < 0 { throw .Missing(key: "negative") }
                              return try load(shard)
                          }

                          fn report(_ shard: Int) -> String {
                              do {
                                  return try fetch(shard)
                              } catch .Missing(let key) {
                                  return "missing \(key)"
                              } catch .Store(let cause) {
                                  return match cause {
                                      case .Down(let s) => "shard \(s) down"
                                  }
                              }
                          }
                          """;

    [Fact]
    public void ATryConvertsAnErrorThroughFrom() =>
        Prints("row 1\nmissing negative\nshard 5 down\n", Layers, "    print(report(1))\n    print(report(-1))\n    print(report(5))");

    [Fact]
    public void TryQuestionOnACallThatGivesNothing() =>
        Prints(
            "tried row 1\ndone\n",
            Layers + "\nfn attempt(_ shard: Int) throws(StoreError) {\n    let row = try load(shard)\n    print(\"tried \" + row)\n}",
            "    try? attempt(1)\n    try? attempt(7)\n    print(\"done\")"
        );

    [Fact]
    public void AThrowingCallOnAMoveOnlyValueEndsItsBorrowOnBothEdges() =>
        Prints(
            "closed a\nclosed \na\nclosed bb\nclosed a\nfailed\n",
            """
            union E {
                Bad(code: Int)
            }

            @noncopy
            struct Handle {
                let name: String

                fn read() throws(E) -> String {
                    if name.count > 1 { throw E.Bad(code: name.count) }
                    return name
                }

                deinit { print("closed \(name)") }
            }

            fn readBoth(_ first: String, _ second: String) throws(E) -> String {
                using let a = Handle { name: first }
                using let b = Handle { name: second }
                return try a.read() + try b.read()
            }
            """,
            """
                print((try? readBoth("", "a")) ?? "failed")
                print((try? readBoth("a", "bb")) ?? "failed")
            """
        );

    [Fact]
    public void AStaticComputedPropertyIsItsGetter() =>
        Prints(
            "6.283185307179586\n3\n",
            "struct Dice {\n    static var faces: Int { get { return 3 } }\n}",
            "    print(2 * F64.pi)\n    print(Dice.faces)"
        );

    [Fact]
    public void AConditionalChoosesBetweenArraysAndIndexingThroughNoneGivesNone() =>
        Prints(
            "2 1\n7\n9 -1\n5 -1\n1 -1 -1\nnothing\n",
            """
            fn pick(_ n: Int) -> Int[>..] {
                return n > 1 ? [1, 1] : [2]
            }

            struct Grid {
                var cells: Int[>..]
                subscript(i: Int) -> Int { get => cells[i] }
            }
            """,
            """
                print("\(pick(5).count) \(pick(0).count)")
                let flag = true
                print(flag ? (3 + 4) : 0)
                let nine: Int[>..] = [9]
                let maybe: Int[>..]? = nine
                let empty: Int[>..]? = none
                print("\(maybe?[0] ?? -1) \(empty?[0] ?? -1)")
                let g: Grid? = Grid { cells: [4, 5] }
                let h: Grid? = none
                print("\(g?[1] ?? -1) \(h?[0] ?? -1)")
                let d: [String: Int]? = ["a": 1]
                let e: [String: Int]? = none
                print("\(d?["a"] ?? -1) \(d?["b"] ?? -1) \(e?["a"] ?? -1)")
                let words: String[>..]? = none
                print(words?[0] ?? "nothing")
            """);

    // ---- Modules named in expressions ------------------------------------------

    [Fact]
    public void AModuleIsReachedInFullAndThroughItsAlias() =>
        Prints(
            "6\n6\n0\n",
            "import Sable.Math as M\nimport Sable.Collections as C",
            """
                print(M.gcd(12, 18))
                print(Sable.Math.gcd(12, 18))
                let d: C.Deque<Int> = Sable.Collections.Deque<Int>()
                print(d.count)
            """);

    [Fact]
    public void AnEnumIsANamedIntegerAndAFlagsEnumASetOfThem() =>
        Prints(
            "East North\n9 1 Active Closed\n11 16\ntrue false true\ntrue All 3 3 Execute\nNone true\nread only everything 3\n1 2 4\nfalse true Color.Blue 2\n",
            """
            enum Direction { North, South, East, West }

            enum Status: U8 {
                Pending = 0
                Active = 1
                Closed = 9
            }

            enum Level { Low = 10, Mid, High = Mid + 5 }

            @flags
            enum Permissions: U32 {
                None = 0
                Read = 1
                Write = 2
                Execute = 4
                All = Read | Write | Execute
            }

            @flags
            enum Style { Bold, Italic, Underline }

            @derive(Equatable, Hashable, Comparable, Debug)
            enum Color { Red, Green, Blue }

            fn describe(_ p: Permissions) -> String {
                return match p {
                    case .Read => "read only"
                    case .All => "everything"
                    default => "\(p)"
                }
            }
            """,
            """
                let d = Direction.East
                print("\(d) \(Direction.North)")
                print("\(Status.Closed as U8) \(Status.Active as Int) \(1 as Status) \(9 as Status)")
                print("\(Level.Mid as Int) \(Level.High as Int)")
                let p: Permissions = [.Read, .Write]
                print("\(p.contains(.Write)) \(p.contains(.Execute)) \(p.contains([.Read, .Write]))")
                let q = p | .Execute
                print("\(q == .All) \(q) \(p) \(p as U32) \(~p & .All)")
                let empty: Permissions = []
                print("\(empty) \(empty == Permissions.None)")
                print("\(describe(.Read)) \(describe(q)) \(describe(p))")
                print("\(Style.Bold as Int) \(Style.Italic as Int) \(Style.Underline as Int)")
                print("\(Color.Red == Color.Green) \(Color.Red < Color.Blue) \(Color.Blue.debugDescription()) \(Color.Green.hash())")
            """);
}
