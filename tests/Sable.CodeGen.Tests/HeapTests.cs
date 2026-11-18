using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Strings, arrays and classes, native and interpreted: the same output,
///     the same traps, and — run with <c>SABLE_CHECK_LEAKS</c> — nothing left
///     allocated at the end.
/// </summary>
public sealed class HeapTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    [Fact]
    public void StringsAreBuiltJoinedAndCompared() =>
        Prints(
            "Hello, Sable!\n13\ntrue\nfalse\nn=42 x=1.5 ok=true\n",
            """
            fn greet(_ name: String) -> String {
                return "Hello, " + name + "!"
            }
            """,
            """
                let g = greet("Sable")
                print(g)
                print(g.count)
                print(g == "Hello, Sable!")
                print(g == "Hello")
                let n = 42
                print("n=\(n) x=\(1.5) ok=\(n > 0)")
            """
        );

    [Fact]
    public void TheCoreStringMembersMatchTheInterpreter() =>
        Prints(
            "héllo, world\n12\né\nhéllo\ntrue\n5\n-1\n12\n25\n-7\n2\nb\ntrue\n0.1\n",
            "",
            """
                let t = "  héllo, world  ".trimmed()
                print(t)
                print(t.count)
                print(String.fromChar(t.at(1)))
                print(t.slice(from: 0, to: 5))
                print(t.contains('w'))
                print(t.indexOf(',') ?? -1)
                print(t.indexOf('z') ?? -1)
                print("12".toInt() ?? 0)
                print("2.5e1".toF64() ?? 0.0)
                print("x1".toInt() ?? -7)
                print(t.split(',').count)
                print("a,b,c".split(',')[1])
                print('a'.isLetter && '7'.isDigit && ' '.isWhitespace)
                print(0.1.toString())
            """
        );

    [Fact]
    public void ALoopBuildingAStringFreesEveryIntermediate() =>
        Prints(
            "0123456789\n",
            "",
            """
                var s = ""
                for i in 0..<10 {
                    let next = s + "\(i)"
                    s = next
                }
                print(s)
            """
        );

    [Fact]
    public void ArraysGrowAreCopiedOnWriteAndIndexed() =>
        Prints(
            "5\n2 3 x changed\n100\n30\n",
            "",
            """
                var xs: Int[>..] = []
                for i in 1...5 {
                    xs.append(i * i)
                }
                print(xs.count)
                var a: String[>..] = ["x", "y"]
                var b = a
                b.append("z")
                b[0] = "changed"
                print("\(a.count) \(b.count) \(a[0]) \(b[0])")
                xs[0] = 100
                print(xs[0])
                var total = 0
                for x in xs {
                    total = total + x
                }
                print(total - 124)
            """
        );

    [Fact]
    public void AnArrayIndexOutOfRangeTrapsTheSameWay() {
        var execution = AssertAgree(Program("", "    let xs: Int[>..] = [1, 2]\n    print(xs[0])\n    print(xs[2])"));
        Assert.Equal("1\n", execution.Stdout);
    }

    [Fact]
    public void ARecursiveUnionThroughAnArray() =>
        Prints(
            "(a(b)c)\n",
            """
            union Tree {
                Leaf(label: String)
                Node(children: Tree[>..])
            }

            fn render(_ tree: Tree) -> String {
                return match tree {
                    case .Leaf(let label) => label
                    case .Node(let children) => renderAll(children)
                }
            }

            fn renderAll(_ children: Tree[>..]) -> String {
                var result = "("
                for child in children {
                    result = result + render(child)
                }
                return result + ")"
            }
            """,
            """
                let tree = Tree.Node(children: [Tree.Leaf(label: "a"), Tree.Node(children: [Tree.Leaf(label: "b")]), Tree.Leaf(label: "c")])
                print(render(tree))
            """
        );

    [Fact]
    public void ErrorsCarryingStringsFreeWhatTheyPassOver() =>
        Prints(
            "first-partok\ncaught: got bad\nnone\n",
            """
            union Failure {
                Bad(reason: String)
            }

            fn check(_ s: String) throws(Failure) -> String {
                if s == "bad" {
                    throw Failure.Bad(reason: "got " + s)
                }
                return s
            }

            fn join(_ a: String, _ b: String) -> String {
                return a + b
            }

            fn attempt(_ second: String) -> String {
                do {
                    return join("first-" + "part", try check(second))
                } catch .Bad(let reason) {
                    return "caught: " + reason
                }
            }
            """,
            """
                print(attempt("ok"))
                print(attempt("bad"))
                print((try? check("bad")) ?? "none")
            """
        );

    [Fact]
    public void ClassesAreSharedAndDeinitRunsAtTheLastRelease() =>
        Prints(
            "before\n2\nfree 1\nfree 2\nfree 3\n6\nbye c\n",
            """
            class Counter {
                var count: Int
                var name: String

                init(_ name: String) {
                    self.count = 0
                    self.name = name
                }

                fn bump() {
                    count = count + 1
                }

                deinit {
                    print("bye " + name)
                }
            }

            class Node {
                var value: Int
                var next: Node?

                init(_ value: Int, _ next: Node?) {
                    self.value = value
                    self.next = next
                }

                deinit {
                    print("free \(value)")
                }
            }

            fn sum(_ list: Node?) -> Int {
                var total = 0
                var current = list
                while let node = current {
                    total = total + node.value
                    current = node.next
                }
                return total
            }

            fn total() -> Int {
                return sum(Node(1, Node(2, Node(3, none))))
            }
            """,
            """
                let a = Counter("c")
                let b = a
                a.bump()
                b.bump()
                print("before")
                print(a.count)
                print(total())
            """
        );

    // ---- The corpus's JSON parser, run ------------------------------------

    [Fact]
    public void TheCorpusJsonParserParsesAndRenders() {
        var parser = File.ReadAllText(Path.Combine(Root, "tests/corpus/11-json.sb"));
        var program = parser + """

                               fn show(_ text: String) {
                                   do {
                                       let value = try parse(text)
                                       print(render(value))
                                   } catch .UnexpectedEnd(let at) {
                                       print("unexpected end at \(at)")
                                   } catch .UnexpectedCharacter(let at, let found) {
                                       print("unexpected '" + String.fromChar(found) + "' at \(at)")
                                   } catch .InvalidNumber(let at) {
                                       print("invalid number at \(at)")
                                   } catch .TrailingText(let at) {
                                       print("trailing text at \(at)")
                                   }
                               }

                               @main
                               fn main() {
                                   show("{ \"name\": \"Sable\", \"tags\": [\"fast\", \"safe\"], \"version\": 2.5, \"ok\": true, \"none\": null }")
                                   show("[1, [2, [3, []]], {}]")
                                   show("  \"esc\\naped\"  ")
                                   show("[1, 2")
                                   show("[1; 2]")
                                   show("{} x")
                                   show("-")
                                   do {
                                       let doc = try parse("{\"a\": {\"b\": 41}}")
                                       if let inner = lookup(doc, "a") {
                                           if let b = lookup(inner, "b") {
                                               print((asNumber(b) ?? 0.0) + 1.0)
                                           }
                                       }
                                       print(lookup(doc, "missing") == none)
                                   } catch {
                                       print("failed")
                                   }
                               }
                               """;

        Assert.Equal(
            """
            {"name":"Sable","tags":["fast","safe"],"version":2.5,"ok":true,"none":null}
            [1,[2,[3,[]]],{}]
            "esc
            aped"
            unexpected end at 5
            unexpected ';' at 2
            trailing text at 3
            invalid number at 0
            42
            true

            """,
            AssertAgree(program).Stdout
        );
    }

    static string Root {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sable.slnx"))) {
                directory = directory.Parent;
            }

            return directory!.FullName;
        }
    }
}
