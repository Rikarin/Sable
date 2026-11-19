namespace Sable.SIL.Tests;

/// <summary>
///     Strings, arrays and classes: lowered with explicit ownership, which the
///     verifier checks, and run by the interpreter, which counts every object
///     — so each test checks what the program computes <i>and</i> that it
///     frees exactly what it allocated.
/// </summary>
public sealed class HeapLoweringTests {
    static SilModule Lower(string source) {
        var compilation = Compilation.Create($"module M\n{source}");
        var errors = compilation.GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));

        var (module, diagnostics) = SilGen.Lower(compilation);
        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics.Select(static d => $"{d.Id}: {d.Message}")));

        var problems = SilVerifier.Verify(module);
        Assert.True(problems.Count == 0, string.Join("\n", problems) + "\n\n" + SilPrinter.Print(module));

        var text = SilPrinter.Print(module);
        var reparsed = SilParser.Parse(text);
        Assert.Equal(text, SilPrinter.Print(reparsed));
        Assert.Empty(SilVerifier.Verify(reparsed));

        SilPassManager.Run(reparsed, SilPassManager.Default);
        return reparsed;
    }

    /// <summary>Runs a function, and insists that everything it allocated was freed.</summary>
    static (object? Result, string Output) Run(SilModule module, string function, params object[] arguments) {
        var interpreter = new SilInterpreter(module);
        var result = interpreter.Run($"M.{function}", arguments);
        Assert.True(interpreter.LiveObjects == 0, $"{interpreter.LiveObjects} object(s) leaked");
        return (result, interpreter.Output.ToString());
    }

    // ---- Strings -----------------------------------------------------------

    [Fact]
    public void StringsAreBuiltAndFreed() {
        var module = Lower(
            """
            fn greet(_ name: String) -> String {
                let greeting = "Hello, " + name
                return greeting + "!"
            }
            """
        );

        Assert.Equal("Hello, Sable!", Run(module, "greet", "Sable").Result);
    }

    [Fact]
    public void InterpolationWritesEachHole() {
        var module = Lower(
            """
            fn describe(_ n: Int, _ x: F64, _ ok: Bool, _ name: String) -> String {
                return "n=\(n), x=\(x), ok=\(ok), name=\(name)."
            }
            """
        );

        Assert.Equal("n=42, x=1.5, ok=true, name=Sable.", Run(module, "describe", 42L, 1.5, true, "Sable").Result);
    }

    [Fact]
    public void ALoopThatReassignsAStringReleasesEachOldValue() {
        var module = Lower(
            """
            fn repeat(_ s: String, _ n: Int) -> String {
                var result = ""
                var i = 0
                while i < n {
                    let next = result + s
                    result = next
                    i = i + 1
                }
                return result
            }
            """
        );

        Assert.Equal("ababab", Run(module, "repeat", "ab", 3L).Result);
    }

    [Fact]
    public void TheCoreStringMembersWork() {
        var module = Lower(
            """
            fn test() {
                let s = "  héllo, world  "
                let t = s.trimmed()
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
                print(t.appending("!"))
                print(t == "héllo, world")
                print(t != "héllo, world")
                print('a'.isLetter && '7'.isDigit && ' '.isWhitespace)
                print(7.abs() + (-7).abs() + 3.min(9) + 3.max(9))
            }
            """
        );

        Assert.Equal(
            "héllo, world\n12\né\nhéllo\ntrue\n5\n-1\n12\n25.0\n-7\n2\nhéllo, world!\ntrue\nfalse\ntrue\n26\n",
            Run(module, "test").Output
        );
    }

    // ---- Arrays ------------------------------------------------------------

    [Fact]
    public void ArraysGrowAndAreIteratedAndIndexed() {
        var module = Lower(
            """
            fn test() -> Int {
                var xs: Int[>..] = []
                for i in 1...5 {
                    xs.append(i * i)
                }
                xs[0] = 100
                var total = 0
                for x in xs {
                    total = total + x
                }
                return total + xs.count * 1000 + xs[4]
            }
            """
        );

        Assert.Equal(154L + 5000 + 25, Run(module, "test").Result);
    }

    [Fact]
    public void AnArrayIsAValueCopiedOnWrite() {
        var module = Lower(
            """
            fn test() -> String {
                var a: String[>..] = ["x", "y"]
                var b = a
                b.append("z")
                b[0] = "changed"
                return "\(a.count) \(b.count) \(a[0]) \(b[0])"
            }
            """
        );

        Assert.Equal("2 3 x changed", Run(module, "test").Result);
    }

    [Fact]
    public void RemoveLastAndClear() {
        var module = Lower(
            """
            fn test() -> String {
                var names: String[>..] = ["a", "b", "c"]
                let last = names.removeLast()
                let count = names.count
                names.clear()
                return last + "\(count)\(names.count)\(names.isEmpty)"
            }
            """
        );

        Assert.Equal("c20true", Run(module, "test").Result);
    }

    [Fact]
    public void AnIndexOutOfRangeTraps() {
        var module = Lower(
            """
            fn test(_ i: Int) -> Int {
                let xs: Int[>..] = [1, 2, 3]
                return xs[i]
            }
            """
        );

        Assert.Equal(3L, Run(module, "test", 2L).Result);
        Assert.Equal("array index out of range", Assert.Throws<SilTrap>(() => new SilInterpreter(module).Run("M.test", 3L)).Message);
    }

    // ---- Unions holding references ---------------------------------------

    [Fact]
    public void ARecursiveUnionThroughAnArrayIsBuiltMatchedAndFreed() {
        var module = Lower(
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

            fn test() -> String {
                let tree = Tree.Node(children: [Tree.Leaf(label: "a"), Tree.Node(children: [Tree.Leaf(label: "b")]), Tree.Leaf(label: "c")])
                return render(tree)
            }
            """
        );

        Assert.Equal("(a(b)c)", Run(module, "test").Result);
    }

    [Fact]
    public void OptionalsOfStringsAreUnwrappedAndCoalesced() {
        var module = Lower(
            """
            fn find(_ names: String[>..], _ initial: Char) -> String? {
                for name in names {
                    if name.at(0) == initial {
                        return name
                    }
                }
                return none
            }

            fn test() -> String {
                let names: String[>..] = ["ada", "grace", "barbara"]
                let a = find(names, 'g') ?? "nobody"
                let b = find(names, 'z') ?? "nobody"
                if let c = find(names, 'b') {
                    return a + " " + b + " " + c + " " + find(names, 'a')!
                }
                return "?"
            }
            """
        );

        Assert.Equal("grace nobody barbara ada", Run(module, "test").Result);
    }

    [Fact]
    public void OptionalsCompareByCaseAndValue() {
        var module = Lower(
            """
            fn first(_ s: String) -> Char? {
                if s.isEmpty {
                    return none
                }
                return s.at(0)
            }

            fn test() -> String {
                return "\(first("x") == 'x') \(first("y") == 'x') \(first("") == 'x') \(first("") == none) \(first("q") != none)"
            }
            """
        );

        Assert.Equal("true false false true true", Run(module, "test").Result);
    }

    // ---- Errors carrying references ---------------------------------------

    [Fact]
    public void AnErrorThrownMidExpressionFreesTheTemporaries() {
        var module = Lower(
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

            fn test() -> String {
                return attempt("ok") + " / " + attempt("bad") + " / " + ((try? check("bad")) ?? "none")
            }
            """
        );

        Assert.Equal("first-partok / caught: got bad / none", Run(module, "test").Result);
    }

    [Fact]
    public void DefersRunOnEveryPathWithLocalsStillFreed() {
        var module = Lower(
            """
            fn test(_ n: Int) -> String {
                var log = "start"
                var i = 0
                while true {
                    let label = "step\(i)"
                    defer {
                        log = log + ";" + label
                    }
                    if i == n {
                        break
                    }
                    i = i + 1
                }
                return log
            }
            """
        );

        Assert.Equal("start;step0;step1;step2", Run(module, "test", 2L).Result);
    }

    // ---- Structs holding references ---------------------------------------

    [Fact]
    public void AStructWithAStringIsBuiltChangedAndCopied() {
        var module = Lower(
            """
            struct Person {
                var name: String
                var age: Int

                mutating fn rename(_ to: String) {
                    name = to
                }

                fn describe() -> String {
                    return name + " (\(age))"
                }
            }

            fn test() -> String {
                var p = Person { name: "Ada", age: 36 }
                let q = p
                p.rename("Grace")
                p.age = p.age + 1
                return p.describe() + ", " + q.describe() + ", " + q.name
            }
            """
        );

        Assert.Equal("Grace (37), Ada (36), Ada", Run(module, "test").Result);
    }

    [Fact]
    public void AnInitialiserFillsStringFields() {
        var module = Lower(
            """
            struct Label {
                var text: String
                var size: Int

                init(_ text: String) {
                    self.text = text + "!"
                    self.size = text.count
                }
            }

            fn test() -> String {
                let l = Label("hi")
                return l.text + "\(l.size)"
            }
            """
        );

        Assert.Equal("hi!2", Run(module, "test").Result);
    }

    // ---- Classes -----------------------------------------------------------

    [Fact]
    public void AClassIsSharedByReferenceAndFreedWithItsLastOne() {
        var module = Lower(
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

            fn test() -> Int {
                let a = Counter("c")
                let b = a
                a.bump()
                b.bump()
                print("before")
                return a.count
            }
            """
        );

        var (result, output) = Run(module, "test");
        Assert.Equal(2L, result);
        Assert.Equal("before\nbye c\n", output);
    }

    [Fact]
    public void ALinkedListOfObjectsIsFreedNodeByNode() {
        var module = Lower(
            """
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

            fn test() -> Int {
                let list = Node(1, Node(2, Node(3, none)))
                return sum(list)
            }
            """
        );

        var (result, output) = Run(module, "test");
        Assert.Equal(6L, result);
        Assert.Equal("free 1\nfree 2\nfree 3\n", output);
    }

    // Found by the compiler fuzzer (SABLE_FUZZ_SEED=1842128005, a mutated
    // Corpus.Game): an assignment is the value it stored, but a store of a
    // String, or through a setter, subscript or array element, gave nothing
    // back, so 'self.name = self.other = name' stored () to a String field.
    [Fact]
    public void AnAssignmentUsedAsAValueIsTheValueItStored() {
        var module = Lower(
            """
            class Pair {
                var name: String
                var other: String

                init(_ name: String) {
                    self.other = ""
                    self.name = self.other = name
                }
            }

            struct Box {
                var inner: String
                var shown: String {
                    get { return inner }
                    set { inner = newValue }
                }
            }

            fn test() -> String {
                let pair = Pair("a")
                var s = ""
                var t = ""
                var i = 0
                while i < 2 {
                    s = t = pair.name + pair.other
                    i = i + 1
                }
                var list = ["x"]
                let element = list[0] = "b"
                var box = Box(inner: "")
                let shown = box.shown = "c"
                let grown = s += "d"
                var n = 0
                let counted = list[0].count + (n = 1)
                return s + t + element + list[0] + shown + box.inner + grown + "\(counted)"
            }
            """
        );

        Assert.Equal("aadaabbccaad2", Run(module, "test").Result);
    }

    // Through '?.' an assignment stores only when there is something to store
    // into, so its value is the stored value, wrapped, or none.
    [Fact]
    public void AnAssignmentThroughAnOptionalChainIsTheValueItStoredIfAny() {
        var module = Lower(
            """
            class Link {
                var next: Link?
                var label: String

                init(_ next: Link?) {
                    self.next = next
                    self.label = ""
                }
            }

            fn test() -> String {
                let head = Link(Link(none))
                let stored = (head.next?.label = "e") ?? "-"
                let skipped = (head.next?.next?.label = "f") ?? "-"
                return stored + skipped + head.next!.label
            }
            """
        );

        Assert.Equal("e-e", Run(module, "test").Result);
    }

    // ---- The verifier's ownership rule -------------------------------------

    [Fact]
    public void TheLoweredSilIsWhatTheVerifierChecks() {
        // A leak, written by hand: the verifier rejects it, by the value.
        var problems = SilVerifier.Verify(
            SilParser.Parse(
                """
                sil @leak : $(String) -> () {
                bb0(%0 : $String):
                  %1 = copy_value %0
                  return
                }
                """
            )
        );

        Assert.Contains(problems, static p => p.Contains("never consumed: %1 (copy_value)", StringComparison.Ordinal));
    }
}
