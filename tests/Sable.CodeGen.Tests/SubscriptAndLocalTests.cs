using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Subscripts a type declares, functions declared inside functions,
///     inline arrays as locals, the compile-time builtins and recursive
///     generic unions — each as a program whose output is checked, natively
///     and in the interpreter, which must agree.
/// </summary>
public sealed class SubscriptAndLocalTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    // ---- Subscripts -------------------------------------------------------------

    const string Grid = """
                        struct Grid {
                            var cells: Int[>..]
                            var width: Int

                            subscript(row: Int, column: Int) -> Int {
                                get => cells[row * width + column]
                                set { cells[row * width + column] = newValue }
                            }

                            subscript(row: Int) -> Int[>..] {
                                get {
                                    var picked: Int[>..] = []
                                    var i = 0
                                    while i < width {
                                        picked.append(cells[row * width + i])
                                        i = i + 1
                                    }
                                    return picked
                                }
                            }
                        }
                        """;

    [Fact]
    public void ASubscriptReadsAndWrites() =>
        Prints(
            "5\n40\n41\n3\n5\n",
            Grid,
            """
                var grid = Grid(cells: [0, 1, 2, 3, 4, 5], width: 3)
                print(grid[1, 2])
                grid[1, 2] = 40
                print(grid[1, 2])
                grid[1, 2] += 1
                print(grid[1, 2])
                print(grid[1].count)
                grid[0, 0] = grid[0, 1] + grid[1, 1]
                print(grid[0, 0])
            """
        );

    [Fact]
    public void ASubscriptWithAnExpressionBodyAndAGenericOwner() =>
        Prints(
            "b\nc\nzz\n",
            """
            struct Row<Value> {
                var values: Value[>..]

                subscript(index: Int) -> Value => values[index]

                subscript(from index: Int, or fallback: Value) -> Value {
                    get {
                        if index < values.count {
                            return values[index]
                        }
                        return fallback
                    }
                    set { values[index] = newValue }
                }
            }
            """,
            """
                var row = Row<String>(values: ["a", "b", "c"])
                print(row[1])
                print(row[from: 2, or: "x"])
                row[from: 0, or: ""] = "zz"
                print(row[0])
            """
        );

    [Fact]
    public void ASubscriptOfAClassWritesThroughTheReference() =>
        Prints(
            "14\n",
            """
            class Box {
                var items: Int[>..] = []
                init() {}
                subscript(i: Int) -> Int {
                    get => items[i]
                    set { items[i] = newValue * 2 }
                }
            }
            """,
            """
                let box = Box()
                box.items.append(0)
                box[0] = 7
                print(box[0])
            """
        );

    // ---- Local functions --------------------------------------------------------

    [Fact]
    public void ALocalFunctionThatCapturesNothingIsAPlainFunction() =>
        Prints(
            "4\n120\n",
            """
            fn nested(_ n: Int) -> Int {
                fn helper(_ x: Int) -> Int {
                    return x + 1
                }
                return helper(n) + helper(n)
            }

            fn factorial(_ n: Int) -> Int {
                fn go(_ k: Int, _ product: Int) -> Int {
                    if k <= 1 {
                        return product
                    }
                    return go(k - 1, product * k)
                }
                return go(n, 1)
            }
            """,
            """
                print(nested(1))
                print(factorial(5))
            """
        );

    [Fact]
    public void ALocalFunctionCapturesWhatIsAroundIt() =>
        // Locals by reference, as a closure does: 'bump' changes what 'helper' reads.
        Prints(
            "181\n15\n3\n",
            """
            fn outer(_ n: Int) -> Int {
                var offset = 10
                fn helper(_ x: Int) -> Int {
                    return x + offset
                }
                fn bump() {
                    offset = offset + 1
                }
                fn sum(_ k: Int) -> Int {
                    if k <= 0 {
                        return 0
                    }
                    return helper(k) + sum(k - 1)
                }
                fn fact(_ k: Int) -> Int {
                    if k <= 1 {
                        return 1
                    }
                    return k * fact(k - 1)
                }
                bump()
                let f = (y: Int) => helper(y) * 2
                return helper(n) + fact(5) + sum(2) + f(1)
            }

            struct S {
                var x: Int
                fn m(_ by: Int) -> Int {
                    fn h() -> Int { return x + by }
                    fn g() -> Int { return self.x * h() }
                    return g()
                }
            }

            fn repeated<T>(_ value: T, _ count: Int) -> T[>..] {
                var items: T[>..] = []
                fn add() {
                    items.append(value)
                }
                var i = 0
                while i < count {
                    add()
                    i = i + 1
                }
                return items
            }
            """,
            """
                print(outer(1))
                print(S(x: 3).m(2))
                print(repeated("a", 3).count)
            """
        );

    [Fact]
    public void ALocalFunctionThatCapturesCanThrowAndBePassedOn() =>
        Prints(
            "6\nnegative\n",
            """
            union Problem { Negative }

            fn apply(_ f: (Int) -> Int, _ x: Int) -> Int { return f(x) }

            fn checked(_ limit: Int, _ x: Int) throws(Problem) -> Int {
                fn check(_ v: Int) throws(Problem) -> Int {
                    if v < 0 {
                        throw Problem.Negative
                    }
                    return v + limit
                }
                fn twice(_ v: Int) -> Int { return v * limit }
                return try check(x) + apply(twice, 1) - limit
            }
            """,
            """
                do {
                    print(try checked(2, 4))
                    print(try checked(2, -1))
                } catch {
                    print("negative")
                }
            """
        );

    [Fact]
    public void AGenericLocalFunctionIsInstantiatedForEachCall() =>
        // Its own type arguments per call; those around it per instance of
        // the function it is in; and calling itself.
        Prints(
            "3\nthree\n1 x true\n1 x 2.5\n4\n10\n",
            """
            fn outer<U>(_ u: U) -> String {
                fn pair<T>(_ a: T, _ b: U) -> (T, U) {
                    return (a, b)
                }
                let p = pair(1, u)
                let q = pair("x", u)
                return "\(p.0) \(q.0) \(q.1)"
            }
            """,
            """
                fn id<T>(_ x: T) -> T {
                    return x
                }
                fn count<T>(_ n: Int, _ x: T) -> Int {
                    if n == 0 {
                        return 0
                    }
                    return 1 + count(n - 1, x)
                }
                print(id(3))
                print(id("three"))
                print(outer(true))
                print(outer(2.5))
                print(count(4, "a"))
                let twice = { (n: Int) -> Int in id(n) * 2 }
                print(twice(5))
            """
        );

    [Fact]
    public void ALocalFunctionThatCapturesTakesInoutParameters() =>
        Prints(
            "22 axx 12\n34 axxx 13\n",
            "",
            """
                var total = 10
                var label = "x"
                fn bump(_ n: inout Int, _ s: inout String) {
                    n = n + total
                    s = s + label
                    total = total + 1
                }
                var k = 1
                var text = "a"
                bump(&k, &text)
                bump(&k, &text)
                print("\(k) \(text) \(total)")
                let again = { bump(&k, &text) }
                again()
                print("\(k) \(text) \(total)")
            """
        );

    [Fact]
    public void AGenericLocalFunctionThatCapturesIsGivenItsContext() =>
        // Its captures are built once, where it is declared, and every
        // instance shares them: a local it changes is changed for all; a
        // closure, a recursive call and another instance of the function
        // around it each reach their own.
        Prints(
            "12\n13\n1;s;\n13\n1/true/7/1 s/true/7/2 2.5/true/7/3 3\n1/u/8/1 s/u/8/2 2.5/u/8/3 3\n",
            """
            fn outer<U>(_ u: U, _ k: Int) -> String {
                var hits = 0
                fn tag<T>(_ x: T) -> String {
                    hits += 1
                    return "\(x)/\(u)/\(k)/\(hits)"
                }
                let a = tag(1)
                let b = tag("s")
                let c = { () -> String in tag(2.5) }
                return "\(a) \(b) \(c()) \(hits)"
            }
            """,
            """
                let k = 10
                var log = ""
                fn add<T>(_ x: T, _ y: Int) -> Int {
                    log = log + "\(x);"
                    return y + k
                }
                fn count<T>(_ n: Int, _ x: T) -> Int {
                    if n == 0 {
                        return k
                    }
                    return 1 + count(n - 1, x)
                }
                print(add(1, 2))
                print(add("s", 3))
                print(log)
                print(count(3, true))
                print(outer(true, 7))
                print(outer("u", 8))
            """
        );

    [Fact]
    public void AClosureCapturesAnInoutParameterByCopyInCopyOut() =>
        // The closures share a box holding the parameter's value; it is
        // written back to the caller's storage when the call ends, by a
        // return or a throw. A closure run after that changes only the box.
        Prints(
            "13\n13\nxabc\nxabcabc\n5\n105\n5\n",
            """
            union Oops: Error { Bad }

            fn bump(_ x: inout Int) {
                let f = () => { x += 1 }
                f()
                f()
                x += 10
                print(x)
            }

            fn names(_ s: inout String, _ fail: Bool) throws {
                let add = (t: String) => { s = s + t }
                add("a")
                fn more() {
                    add("b")
                    s = s + "c"
                }
                more()
                if fail {
                    throw Oops.Bad
                }
            }

            fn later(_ x: inout Int) -> () -> Int {
                let g = () => {
                    x += 100
                    return x
                }
                x = 5
                return g
            }
            """,
            """
                var a = 1
                bump(&a)
                print(a)
                var s = "x"
                try? names(&s, false)
                print(s)
                try? names(&s, true)
                print(s)
                var b = 0
                let g = later(&b)
                print(b)
                print(g())
                print(b)
            """
        );

    // ---- Bounded arrays outside an inline struct --------------------------------

    [Fact]
    public void ABoundedArrayGrowsWithinItsBoundAndIsAValue() =>
        Prints(
            "3\n6\n1\n2\n0\n",
            """
            struct Small {
                var items: Int[..4]
            }

            fn total(_ values: Int[..4]) -> Int {
                var sum = 0
                for value in values {
                    sum = sum + value
                }
                return sum
            }
            """,
            """
                var inline: Int[..8] = []
                inline.append(1)
                inline.append(2)
                inline.append(3)
                print(inline.count)
                var small = Small(items: [1, 2])
                let copy = small
                small.items.append(3)
                print(total(small.items))
                print(small.items[0])
                print(copy.items.count)
                inline.clear()
                print(inline.count)
            """
        );

    [Fact]
    public void ABoundedArrayTrapsWhenItIsFull() {
        var run = AssertAgree(Program("", "    var pair: Int[..2] = [1, 2]\n    print(pair.count)\n    pair.append(3)\n    print(pair.count)"));
        Assert.Equal("2\n", run.Stdout);
        Assert.Equal("fatal error: inline array is full\n  at test.sb:7:5\n", run.Stderr);
    }

    // ---- Recursive unions -------------------------------------------------------

    [Fact]
    public void ARecursiveUnionBoxesTheCaseThatHoldsItself() =>
        Prints(
            "10\n3\n",
            """
            union Expr {
                Num(Int)
                Add(Expr, Expr)
                Neg(of: Expr)
            }

            fn eval(_ e: Expr) -> Int {
                return match e {
                    case .Num(let n) => n
                    case .Add(let a, let b) => eval(a) + eval(b)
                    case .Neg(let inner) => 0 - eval(inner)
                }
            }

            fn size(_ e: Expr) -> Int {
                if case .Add(.Num(let a), _) = e {
                    return a
                }
                return 0
            }
            """,
            """
                let e = Expr.Add(Expr.Num(3), Expr.Add(Expr.Num(9), Expr.Neg(of: Expr.Num(2))))
                let copy = e
                print(eval(copy))
                print(size(e))
            """
        );

    [Fact]
    public void IndirectBoxesACaseThatDoesNotNeedIt() =>
        Prints(
            "4\n7\nBig.Large(1, 2, 3, 4)\n",
            """
            union Big {
                @indirect Large(Int, Int, Int, Int)
                Small(Int)
            }

            fn last(_ b: Big) -> Int {
                return match b {
                    case .Large(_, _, _, let d) => d
                    case .Small(let s) => s
                }
            }
            """,
            """
                let b = Big.Large(1, 2, 3, 4)
                print(last(b))
                print(last(Big.Small(7)))
                print(Mirror(of: b).description)
            """
        );

    [Fact]
    public void AGenericRecursiveUnion() =>
        Prints(
            "3\n3\n2\n3\nTree<Int>.Node(value: 1, left: Tree<Int>.Leaf, right: Tree<Int>.Leaf)\n",
            """
            union Tree<Value> {
                Leaf
                Node(value: Value, left: Tree<Value>, right: Tree<Value>)
            }

            fn depth<Value>(_ tree: Tree<Value>) -> Int {
                return match tree {
                    case .Leaf => 0
                    case .Node(_, let left, let right) => {
                        let l = depth(left)
                        let r = depth(right)
                        if l > r {
                            return 1 + l
                        }
                        return 1 + r
                    }
                }
            }

            fn count<Value>(_ tree: Tree<Value>) -> Int {
                return match tree {
                    case .Leaf => 0
                    case .Node(_, let left, let right) => 1 + count(left) + count(right)
                }
            }

            fn sample() -> Tree<Int> {
                return Tree.Node(
                    value: 1,
                    left: Tree.Node(value: 2, left: Tree.Node(value: 3, left: Tree.Leaf, right: Tree.Leaf), right: Tree.Leaf),
                    right: Tree.Leaf)
            }

            union List {
                Empty
                Cons(head: String, tail: List?)
            }

            fn length(_ list: List?) -> Int {
                guard let present = list else {
                    return 0
                }
                return match present {
                    case .Empty => 0
                    case .Cons(_, let tail) => 1 + length(tail)
                }
            }
            """,
            """
                let tree = sample()
                print(depth(tree))
                print(count(tree))
                let words = Tree<String>.Node(value: "a", left: Tree.Leaf, right: Tree.Node(value: "b", left: Tree.Leaf, right: Tree.Leaf))
                print(count(words))
                let end: List? = none
                print(length(List.Cons(head: "x", tail: List.Cons(head: "y", tail: List.Cons(head: "z", tail: end)))))
                print(Mirror(of: Tree.Node(value: 1, left: Tree<Int>.Leaf, right: Tree.Leaf)).description)
            """
        );

    // ---- Compiler builtins ------------------------------------------------------

    [Fact]
    public void TheCompilerBuiltinsSayWhereTheyAre() =>
        Prints(
            "test.sb:5:50 in main of Test\nwhere\n",
            "fn whereAmI() -> String { return #function }",
            """
                print(#file + ":" + #line.toString() + ":" + #column.toString() + " in " + #function + " of " + #module)
                print(whereAmI().count > 0 ? "where" : "nowhere")
            """
        );
}
