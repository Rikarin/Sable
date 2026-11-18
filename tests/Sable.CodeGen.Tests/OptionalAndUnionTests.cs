using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Optionals and structural unions, natively and in the interpreter:
///     promotion that takes a step first, recursive unions, narrowing after
///     an early return, and chains through optional members.
/// </summary>
public sealed class OptionalAndUnionTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    [Fact]
    public void PromotionThroughALiteralOrAUnion() =>
        Prints(
            "2\nx\n7\n",
            "",
            """
                let xs: Int[>..]? = [9, 8]
                print(xs?.count ?? 0)
                let u: (Int | String)? = "x"
                if let v = u {
                    if v is String { print(v) }
                }
                print((none as Int?) ?? 7)
            """
        );

    [Fact]
    public void ARecursiveUnionAndNarrowingAfterAReturn() =>
        Prints(
            "8\n[3]\n{2}\nnum\n",
            """
            alias Json = Bool | F64 | String | Json[] | [String: Json]

            fn leaves(_ j: Json) -> Int {
                if j is Json[] {
                    var n = 0
                    for x in j { n = n + leaves(x) }
                    return n
                }
                if j is [String: Json] {
                    var n = 0
                    for (k, v) in j { n = n + leaves(v) }
                    return n
                }
                return 1
            }

            fn render(_ j: Json) -> String {
                if j is Bool { return "bool" }
                if j is F64 { return "num" }
                if j is String { return j }
                if j is Json[] { return "[\(j.count)]" }
                return "{\(j.count)}"
            }
            """,
            """
                let inner: Json = [1.5, "x", false]
                let object: Json = ["a": inner, "b": "y"]
                let all: Json = [object, inner, true]
                print(leaves(all))
                print(render(all))
                print(render(object))
                print(render(1.5))
            """
        );

    [Fact]
    public void ChainsThroughOptionalMembersCallsAndStores() =>
        Prints(
            "Prague\nunknown\n42\n7\n9\n3\n10\n4\n",
            """
            struct Address { var city: String }
            struct Point { var x: Int, var y: Int }
            class User {
                var address: Address?
                init(address: Address?) { self.address = address }
            }
            class Node {
                var v: Int
                var next: Node?
                init(v: Int, next: Node?) { self.v = v; self.next = next }
            }
            fn city(_ user: User?) -> String { return user?.address?.city ?? "unknown" }
            fn plusOne(_ x: Int!) -> Int { return x + 1 }
            """,
            """
                print(city(User(address: Address { city: "Prague" })))
                print(city(User(address: none)))
                let f: ((Int) -> Int)? = (x: Int) => x * 2
                print(f?(21) ?? 0)
                let g: (() -> Int)? = none
                print(g?() ?? 7)
                let a = Node(v: 1, next: Node(v: 2, next: none))
                a.next?.v = 9
                print(a.next?.v ?? 0)
                a.next?.next = Node(v: 3, next: none)
                print(a.next?.next?.v ?? 0)
                var p: Point? = Point { x: 1, y: 2 }
                p?.x = 10
                print(p?.x ?? 0)
                print(plusOne(3))
            """
        );
    const string Layouts = """
                           union Color {
                               Red
                               Green
                               Blue
                           }

                           union Shape {
                               Circle(r: F64)
                               Square(n: I32)
                               Flag(f: Bool)
                               Named(t: String)
                               Empty
                           }

                           union Small {
                               A(f: Bool)
                               B(n: U8)
                               C
                           }

                           struct Fields {
                               var flag: Bool?
                               var letter: Char?
                               var name: String?
                               var color: Color
                           }

                           fn describe(_ b: Bool?) -> String {
                               if let v = b {
                                   return v ? "yes" : "no"
                               }
                               return "none"
                           }

                           fn letter(_ c: Char?) -> String {
                               if let v = c {
                                   return String.fromChar(v)
                               }
                               return "-"
                           }

                           fn name(_ s: String?) -> String { return s ?? "anon" }

                           fn area(_ s: Shape) -> String {
                               return match s {
                                   case .Circle(let r) => "circle \(r)"
                                   case .Square(let n) => "square \(n)"
                                   case .Flag(let f) => "flag \(f)"
                                   case .Named(let t) => "named " + t
                                   case .Empty => "empty"
                               }
                           }

                           fn small(_ s: Small) -> String {
                               return match s {
                                   case .A(let f) => "a \(f)"
                                   case .B(let n) => "b \(n)"
                                   case .C => "c"
                               }
                           }
                           """;

    /// <summary>
    ///     Enums laid out in their payloads' niches, or with a byte's tag:
    ///     each case made, stored in an array and a struct, and taken apart
    ///     again, natively and in the interpreter.
    /// </summary>
    [Fact]
    public void EnumsInNichesAndSmallTagsKeepEveryCase() =>
        Prints(
            "yes no none\nx - é \U0010FFFF\na anon c\ncircle 1.5 square 7 flag true named x empty\na false b 200 c\nnoneqn\ntrue true false true\n",
            Layouts,
            """
                var line = ""
                let bools: Bool?[>..] = [true, false, none]
                for b in bools {
                    line = line + (line.count > 0 ? " " : "") + describe(b)
                }
                print(line)
                line = ""
                let chars: Char?[>..] = ['x', none, 'é', '\u{10FFFF}']
                for c in chars {
                    line = line + (line.count > 0 ? " " : "") + letter(c)
                }
                print(line)
                var names: String?[>..] = ["a", none]
                names.append("c")
                print(name(names[0]) + " " + name(names[1]) + " " + name(names[2]))
                let shapes: Shape[>..] = [Shape.Circle(r: 1.5), Shape.Square(n: 7), Shape.Flag(f: true), Shape.Named(t: "x"), Shape.Empty]
                line = ""
                for s in shapes {
                    line = line + (line.count > 0 ? " " : "") + area(s)
                }
                print(line)
                print(small(Small.A(f: false)) + " " + small(Small.B(n: 200)) + " " + small(Small.C))
                let f = Fields(flag: none, letter: 'q', name: "n", color: Color.Blue)
                print(describe(f.flag) + letter(f.letter) + name(f.name))
                let colors: Color[>..] = [Color.Red, Color.Green, Color.Blue]
                let t: Bool? = true
                let w: Char? = none
                print("\(colors[2] == Color.Blue) \(t == true) \(t == none) \(w == none)")
            """
        );

    /// <summary>
    ///     An optional a comparison with 'none' narrowed reads as its payload:
    ///     in the else of '== none', inside '!= none', and after '== none'
    ///     returns — for a value, a class and a parameter (#231).
    /// </summary>
    [Fact]
    public void AComparisonWithNoneNarrowsTheOptional() =>
        Prints(
            "none\nyo\nabc\n3\n4\n-1\n6\n",
            """
            class Node {
                var v: Int
                init(v: Int) { self.v = v }
            }
            fn f(_ s: String) { print(s) }
            fn g(_ s: String?) {
                if s == none {
                    return
                }
                f(s)
                print(s.count)
            }
            fn h(_ n: Node?) -> Int {
                if n == none { return -1 } else { return n.v }
            }
            """,
            """
                var x: String? = none
                if x == none { print("none") } else { f(x) }
                x = "yo"
                if x == none { print("none") } else { f(x) }
                g("abc")
                g(none)
                print(h(Node(v: 4)))
                print(h(none))
                let i: Int? = 5
                if i != none { print(i + 1) }
            """
        );

    /// <summary>
    ///     What the layouts are: 'Bool?' a byte, 'Char?' four, 'String?' the
    ///     pointer, an enum of cases alone its byte's tag, and a payload area
    ///     in units of the most aligned payload — where every enum was once
    ///     an eight-byte tag and eight-byte words.
    /// </summary>
    [Fact]
    public void EnumsTakeTheLeastRoomTheirCasesAllow() {
        var result = Pipeline.Compile(new SourceText(Program(Layouts, "    let f = Fields(flag: none, letter: none, name: none, color: Color.Red)\n    print(describe(f.flag) + letter(f.letter) + name(f.name) + area(Shape.Empty) + small(Small.C))"), "test.sb"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var ir = result.LlvmIr!;
        Assert.Contains("@\"sable.Test.describe\"(i8 %p0)", ir, StringComparison.Ordinal);
        Assert.Contains("@\"sable.Test.letter\"(i32 %p0)", ir, StringComparison.Ordinal);
        Assert.Contains("@\"sable.Test.name\"(ptr %p0)", ir, StringComparison.Ordinal);
        Assert.Contains("@\"sable.Test.area\"({ i8, [1 x i64] } %p0)", ir, StringComparison.Ordinal);
        Assert.Contains("@\"sable.Test.small\"({ i8, [1 x i8] } %p0)", ir, StringComparison.Ordinal);
        Assert.Contains("{ i8, i32, ptr, { i8 } }", ir, StringComparison.Ordinal);
    }
}
