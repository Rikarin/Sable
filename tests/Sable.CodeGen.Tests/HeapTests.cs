using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Strings, arrays and classes, native and interpreted: the same output,
///     the same traps, and — run with <c>SABLE_CHECK_LEAKS</c> — nothing left
///     allocated at the end.
/// </summary>
[Native]
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
            "héllo, world\n12\né\nhéllo\ntrue\n5\n-1\n12\n25.0\n-7\n2\nb\ntrue\n0.1\n",
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
            [1.0,[2.0,[3.0,[]]],{}]
            "esc
            aped"
            unexpected end at 5
            unexpected ';' at 2
            trailing text at 3
            invalid number at 0
            42.0
            true

            """,
            AssertAgree(program).Stdout
        );
    }

    /// <summary>
    ///     A program that ends with objects alive says which, and the reference
    ///     cycles that keep them — through fields, optionals and arrays, but
    ///     not through a weak reference, which keeps nothing alive (#188).
    /// </summary>
    [Fact]
    public void ALeakSaysWhichCyclesKeepItsObjects() {
        if (!CanBuild) {
            return;
        }

        var run = BuildAndRun("""
            module Test

            class Node {
                var next: Node? = none
                var children: Node[>..] = []
            }

            class Owner {
                var pet: Pet? = none
            }

            class Pet {
                weak var owner: Owner? = none
                var toy: Toy? = none
            }

            class Toy {
                var pet: Pet? = none
            }

            @main
            fn main() {
                let a = Node()
                let b = Node()
                a.next = b
                b.next = a
                let parent = Node()
                parent.children.append(Node())
                parent.children[0].next = parent
                let owner = Owner()
                let pet = Pet()
                owner.pet = pet
                pet.owner = owner
                let toy = Toy()
                pet.toy = toy
                toy.pet = pet
                print("done")
            }
            """);
        Assert.Equal("done\n", run.Stdout);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(
            """
            sable: 7 object(s) leaked
              4 Test.Node
              1 Test.Pet
              1 Test.Toy
              1 [Test.Node]

            """,
            Before(run.Stderr, "  a reference cycle")
        );

        // The owner is held by the pet only weakly: it is freed, and the pet's cycle with its toy is all that is left.
        var cycles = run.Stderr.Split('\n').Where(static l => l.StartsWith("  a reference cycle: ", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            [
                "  a reference cycle: Test.Node.next -> Test.Node.next -> Test.Node",
                "  a reference cycle: Test.Pet.toy -> Test.Toy.pet -> Test.Pet",
            ],
            cycles.Where(static c => !c.Contains('[', StringComparison.Ordinal)).Select(static c => c.Replace("Test.Toy.pet -> Test.Pet.toy -> Test.Toy", "Test.Pet.toy -> Test.Toy.pet -> Test.Pet", StringComparison.Ordinal))
        );

        // Each step names the field that holds the next object; an array's element has no name.
        Assert.Single(cycles, static c => c.Contains("Test.Node.children -> [Test.Node] -> Test.Node.next", StringComparison.Ordinal)
            || c.Contains("[Test.Node] -> Test.Node.next -> Test.Node.children -> [Test.Node]", StringComparison.Ordinal)
            || c.Contains("Test.Node.next -> Test.Node.children -> [Test.Node]", StringComparison.Ordinal));
    }

    /// <summary>Small blocks freed together give their slabs back to the system, but for one each class keeps.</summary>
    [Fact]
    public void FreedSmallBlocksGoBackToTheSystem() {
        var source = Program(
            """
            class Cell {
                var value: Int
                init(_ value: Int) {
                    self.value = value
                }
            }
            """,
            """
                var total = 0
                for round in 0..<3 {
                    var cells: Cell[>..] = []
                    for i in 0..<20_000 {
                        cells.append(Cell(i + round))
                    }

                    for cell in cells {
                        total += cell.value
                    }
                }

                print(total)
            """
        );
        Assert.Equal("600030000\n", AssertAgree(source).Stdout);
        if (!CanBuild) {
            return;
        }

        var stats = BuildAndRun(source, environment: new Dictionary<string, string> { ["SABLE_STATS"] = "1" }).Stderr;
        var line = stats.Split('\n').Single(static l => l.StartsWith("sable: slabs ", StringComparison.Ordinal));
        var numbers = line.Split(' ').Select(static w => long.TryParse(w, out var n) ? n : -1).Where(static n => n >= 0).ToList();
        var (held, most, returned) = (numbers[0], numbers[1], numbers[2]);

        // 20,000 cells of 32 bytes are about ten slabs; each round's are
        // given back as its array goes, and the next round's take new ones.
        Assert.True(most >= 10, line);
        Assert.True(returned >= 2 * 9, line);
        Assert.True(held < most, line);
    }

    static string Before(string text, string marker) => text.IndexOf(marker, StringComparison.Ordinal) is var at and >= 0 ? text[..at] : text;

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
