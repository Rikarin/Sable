using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Closures (docs/04 §2.2, item 3): code and a counted context. Locals
///     captured by reference live in boxes the frame and the closures share;
///     parameters, <c>self</c> and capture lists are copied in. Native and
///     interpreted, leak-checked.
/// </summary>
public sealed class ClosureTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    [Fact]
    public void TheCorpusClosureForms() =>
        Prints(
            "10\n6\n15\n3\ntrue\n15\n7\n11\n1\n25\n",
            """
            fn apply(_ value: Int, _ transform: (Int) -> Int) -> Int {
                return transform(value)
            }

            fn applyTwice(_ value: Int, _ transform: (Int) -> Int) -> Int {
                return transform(transform(value))
            }

            fn combine<T, U>(_ value: T, _ project: (T) -> U) -> U {
                return project(value)
            }

            fn doubled(_ n: Int) -> Int {
                let double = (x: Int) => x * 2
                return apply(n, double)
            }

            fn captures(_ n: Int) -> Int {
                let offset = 10
                let add = (x: Int) => x + offset
                return add(n)
            }

            fn explicitCapture(_ n: Int) -> Int {
                let base = 2
                let f = [copied = base] (x: Int) => x + copied
                return f(n)
            }

            fn blockBody(_ n: Int) -> Int {
                let f = (x: Int) => {
                    let doubled = x * 2
                    return doubled + 1
                }
                return f(n)
            }

            fn voidClosure() -> Int {
                var total = 0
                let bump = () => { total = total + 1 }
                bump()
                return total
            }

            fn storedInVariable(_ n: Int) -> Int {
                var transform: (Int) -> Int = x => x
                transform = x => x * x
                return transform(n)
            }
            """,
            """
                print(doubled(5))
                print(apply(5, x => x + 1))
                print(apply(5, { $0 * 3 }))
                print(applyTwice(5) { $0 - 1 })
                print(combine(5, x => x > 0))
                print(captures(5))
                print(explicitCapture(5))
                print(blockBody(5))
                print(voidClosure())
                print(storedInVariable(5))
            """
        );

    [Fact]
    public void AClosureOutlivesTheFrameItCapturedFrom() =>
        Prints(
            "1\n2\n3\n1\n",
            """
            fn counter() -> () -> Int {
                var count = 0
                return () => {
                    count = count + 1
                    return count
                }
            }
            """,
            """
                let next = counter()
                print(next())
                print(next())
                print(next())
                let other = counter()
                print(other())
            """
        );

    [Fact]
    public void ClosuresShareTheBoxesOfWhatTheyCapture() =>
        Prints(
            "hello world!\n3\n",
            """
            fn build() -> String {
                var text = "hello"
                var appends = 0
                let add = (s: String) => {
                    text = text + s
                    appends = appends + 1
                }
                add(" ")
                add("world")
                let shout = () => { add("!") }
                shout()
                print(text)
                return "\(appends)"
            }
            """,
            """
                print(build())
            """
        );

    [Fact]
    public void EachIterationCapturesItsOwnBinding() =>
        Prints(
            "0 1 4 9 \n",
            "",
            """
                var actions: (() -> Int)[>..] = []
                for i in 0..<4 {
                    let square = i * i
                    actions.append(() => square)
                }
                var line = ""
                for action in actions {
                    line = line + "\(action()) "
                }
                print(line)
            """
        );

    [Fact]
    public void ClosuresCaptureParametersSelfAndStrings() =>
        Prints(
            "Ada: 1, Ada: 2\n[a][b]\n",
            """
            struct Greeter {
                var name: String

                fn makeLabeller() -> (Int) -> String {
                    return (n: Int) => name + ": \(n)"
                }
            }

            fn mapAll(_ items: String[>..], _ f: (String) -> String) -> String[>..] {
                var result: String[>..] = []
                for item in items {
                    result.append(f(item))
                }
                return result
            }

            fn wrap(_ left: String, _ right: String) -> (String) -> String {
                return (s: String) => left + s + right
            }
            """,
            """
                let label = Greeter { name: "Ada" }.makeLabeller()
                print(label(1) + ", " + label(2))
                let wrapped = mapAll(["a", "b"], wrap("[", "]"))
                print(wrapped[0] + wrapped[1])
            """
        );

    [Fact]
    public void ANamedFunctionIsAValue() =>
        Prints(
            "12\n7\n",
            """
            fn triple(_ n: Int) -> Int {
                return n * 3
            }

            fn increment(_ n: Int) -> Int {
                return n + 1
            }

            fn apply(_ value: Int, _ transform: (Int) -> Int) -> Int {
                return transform(value)
            }
            """,
            """
                print(apply(4, triple))
                var f: (Int) -> Int = triple
                f = increment
                print(f(6))
            """
        );

    [Fact]
    public void AThrowingClosure() =>
        Prints(
            "4\nfailed: 7\n",
            """
            union Problem {
                Odd(value: Int)
            }

            fn run(_ n: Int, _ f: (Int) throws(Problem) -> Int) throws(Problem) -> Int {
                return try f(n)
            }
            """,
            """
                let halve: (Int) throws(Problem) -> Int = n => {
                    if n % 2 == 1 {
                        throw Problem.Odd(value: n)
                    }
                    return n / 2
                }
                do {
                    print(try run(8, halve))
                    print(try run(7, halve))
                } catch .Odd(let value) {
                    print("failed: \(value)")
                }
            """
        );

    [Fact]
    public void AWeakCaptureDoesNotKeepItsObjectAndReadsNoneOnceItIsGone() =>
        Prints(
            "24\n11\n11\n11\ncounter gone\n-1\n-3\n-2\n",
            """
            class Counter {
                var total: Int = 10
                init() { }
                fn bump() -> Int {
                    total += 1
                    return total
                }

                fn adder() -> (Int) -> Int {
                    let localCount = 2
                    return [weak self, count = localCount] (x: Int) => {
                        guard let self else { return -1 }
                        return self.total + count + x + bump()
                    }
                }

                fn peek() -> () -> Int {
                    return [unowned self] () => total
                }

                fn nested() -> () -> Int {
                    return [weak self] () => {
                        let inner = [weak self] () => self?.total ?? -2
                        return inner()
                    }
                }

                deinit { print("counter gone") }
            }
            """,
            """
                var f: (Int) -> Int = (x: Int) => x
                var g: () -> Int = () => 0
                var h: () -> Int = () => 0
                if true {
                    let c = Counter()
                    f = c.adder()
                    g = [weak c] () => c?.total ?? -3
                    h = c.nested()
                    print(f(1))
                    print(g())
                    print(h())
                    print(c.peek()())
                }
                print(f(1))
                print(g())
                print(h())
            """);

    /// <summary>
    ///     A function type's parameter may be inout: a closure, a function or
    ///     '$0' given the caller's storage changes it where it is, called
    ///     with '&' as a declared function is.
    /// </summary>
    [Fact]
    public void AFunctionTypeWithAnInoutParameter() =>
        Prints(
            "2\n7\n11\n101\n42 42 42\n3 6\n",
            """
            fn apply(_ change: (inout Int) -> ()) -> Int {
                var n = 1
                change(&n)
                return n
            }

            fn bump(_ x: inout Int) {
                x += 10
            }

            fn fold<R>(_ start: R, _ xs: Int[>..], _ f: (inout R, Int) -> Void) -> R {
                var r = start
                for x in xs {
                    f(&r, x)
                }
                return r
            }

            struct P {
                var a: Int
            }
            """,
            """
                print(apply { (v: inout Int) in v += 1 })
                print(apply { $0 *= 7 })
                print(apply(bump))
                let k = 100
                print(apply { v in v += k })
                let f: (inout Int) -> () = { (v: inout Int) in v = 42 }
                var m = 0
                f(&m)
                var p = P(a: 3)
                f(&p.a)
                var arr: Int[>..] = [1, 2]
                f(&arr[1])
                print("\(m) \(p.a) \(arr[1])")
                let parts = fold([] as String[>..], [1, 2, 3], { (acc: inout String[>..], x: Int) in acc.append("\(x)") })
                print("\(parts.count) \(fold(0, [1, 2, 3], { $0 += $1 }))")
            """);

    [Fact]
    public void ATrailingClosureFillsALabelledParameterAndADotFillsAValue() =>
        Prints(
            "4\n40\n8\n16\n3\nvalue\nclosure\n",
            """
            fn replacing(_ r: Int, with f: (Int) -> Int) -> Int { return f(r) }
            fn both(_ r: Int, then a: (Int) -> Int, finally b: (Int) -> Int) -> Int { return b(a(r)) }
            fn scaled(_ r: Int, scale: Int = 2, by f: (Int) -> Int) -> Int { return f(r) * scale }
            fn maybe(_ r: Int, with f: ((Int) -> Int)? = none) -> Int { return f?(r) ?? r }

            enum Mode { Fast, Slow }
            fn pick(_ m: Mode) -> String { return "value" }
            fn pick(_ m: () -> Mode) -> String { return "closure" }
            """,
            """
                print(replacing(3) { $0 + 1 })
                print(both(3) { $0 + 1 } finally: { $0 * 10 })
                print(scaled(3) { $0 + 1 })
                print(scaled(3, scale: 4) { $0 + 1 })
                print(maybe(3))
                print(pick(.Fast))
                print(pick { .Slow })
            """
        );

    /// <summary>
    ///     A closure that is a stored property's initial value takes no
    ///     'self': it is a closure like any other, its parameters its own.
    /// </summary>
    [Fact, Native]
    public void AClosureAsAPropertysInitialValueTakesItsOwnParameters() =>
        Prints(
            "2\nset 5\n",
            """
            final class Hooks {
                var onValue: (Int) -> Void = { value in }
                var double: (Int) -> Int = { $0 * 2 }

                init() { }
            }
            """,
            """
                let hooks = Hooks()
                hooks.onValue(1)
                print(hooks.double(1))
                hooks.onValue = { value in print("set \(value)") }
                hooks.onValue(5)
            """
        );

    /// <summary>
    ///     A closure that does not escape, inside a mutating method, changes
    ///     self as Swift's does: by assignment, through a mutating method of
    ///     self or of a part of it, nested, and with the method leaving early
    ///     or throwing — the change reaches the caller's value however the
    ///     call ends.
    /// </summary>
    [Fact]
    public void ANonEscapingClosureInAMutatingMethodChangesSelf() =>
        Prints(
            "10 4 xxxx\n13\n3\n7 [\"a\", \"b\"]\n5\n",
            """
            fn each(_ n: Int, _ body: (Int) -> Void) {
                for i in 0..<n {
                    body(i)
                }
            }

            fn attempt(_ body: () throws(String) -> Void) throws(String) {
                try body()
            }

            struct Counter {
                var n: Int
                var log: String[>..]

                mutating fn bump(by k: Int) {
                    n += k
                }

                mutating fn run() {
                    each(4) { i in
                        bump(by: i)
                        n += 1
                        self.log.append("x")
                    }
                }

                mutating fn nested() -> Int {
                    each(1) { i in
                        each(3) { j in n += 1 }
                    }
                    return n
                }

                mutating fn failing() throws(String) {
                    try attempt {
                        n = 3
                        throw "stop"
                    }
                }
            }

            struct Box<T> {
                var items: T[>..]
                var count: Int

                mutating fn add(_ xs: T[>..]) {
                    each(xs.count) { i in
                        items.append(xs[i])
                        count += 1
                    }
                }
            }
            """,
            """
                var c = Counter { n: 0, log: [] }
                c.run()
                print("\(c.n) \(c.log.count) \(c.log[0])\(c.log[1])\(c.log[2])\(c.log[3])")
                print(c.nested())
                do {
                    try c.failing()
                } catch {
                    print(c.n)
                }
                var b = Box<String> { items: [], count: 5 }
                b.add(["a", "b"])
                print("\(b.count) \(b.items)")
                var d = Counter { n: 0, log: [] }
                each(5) { i in d.bump(by: 1) }
                print(d.n)
            """);

    /// <summary>
    ///     Through an inout argument, an element and self itself; while self
    ///     is being walked; of a move-only self; and beside an escaping
    ///     closure, which holds a copy of self rather than the shared one.
    ///     Self is moved in and out, so its arrays keep their buffers.
    /// </summary>
    [Fact]
    public void AClosuresChangesToSelfTakeEveryForm() =>
        Prints(
            "6\n[\"e\", \"two2\"]\n11 [\"z7\"]\n[\"qz7\"]\n99 [\"w\"]\ntrue\nn1! n1!\ngone 4\n",
            """
            fn each(_ n: Int, _ body: (Int) -> Void) {
                for i in 0..<n { body(i) }
            }
            fn inc(_ x: inout Int) { x += 1 }

            struct S {
                var n: Int
                var items: String[>..]

                mutating fn viaInout() {
                    each(3) { i in inc(&n) }
                }
                mutating fn viaInoutSelf() {
                    each(3) { i in inc(&self.n) }
                }
                mutating fn replaceWhileWalking() {
                    items.forEach { s in
                        items = ["z\(n)"]
                        n += s.count
                    }
                }
                fn walk(_ f: (String) -> Void) {
                    for s in items { f(s) }
                }
                mutating fn replaceInWalk() {
                    walk { s in items = ["q\(s)"] }
                }
                mutating fn whole() {
                    each(1) { i in self = S { n: 99, items: ["w"] } }
                }
                mutating fn element() {
                    each(1) { i in items[0] = "e" }
                }
                mutating fn many() {
                    each(1) { i in items.append("a") }
                }
            }

            @noncopy
            struct R {
                var n: Int
                deinit { print("gone \(n)") }
                mutating fn run() { each(3) { i in n += 1 } }
            }

            struct Reader {
                var name: String
                mutating fn both() -> () -> String {
                    each(1) { i in name = name + "!" }
                    return () => name
                }
            }

            struct Waiter {
                var names: String[>..]
                mutating fn go() async {
                    each(2) { i in names.append("w\(i)") }
                    await Task.yield()
                    each(1) { i in names.append("end") }
                }
            }
            """,
            """
                var s = S { n: 0, items: ["one\(1)", "two\(2)"] }
                s.viaInout()
                s.viaInoutSelf()
                print(s.n)
                s.element()
                print(s.items)
                s.replaceWhileWalking()
                print("\(s.n) \(s.items)")
                s.replaceInWalk()
                print(s.items)
                s.whole()
                print("\(s.n) \(s.items)")
                s.items.reserveCapacity(64)
                s.many()
                print(s.items.capacity >= 64)
                var r = R { n: 1 }
                r.run()
                var reader = Reader { name: "n\(1)" }
                let later = reader.both()
                print("\(later()) \(reader.name)")
            """);

    /// <summary>What a closure throws is its own: not what one inside it throws, and not lost to one that throws nothing.</summary>
    [Fact]
    public void AClosureInsideAClosureThrowsForItself() =>
        Prints(
            "caught\nthrew\n",
            """
            struct Bad: Error {}
            fn risky() throws(Bad) { throw Bad {} }
            fn run<E>(_ body: () throws(E) -> Void) throws(E) { try body() }
            fn caught(_ body: () throws(Bad) -> Void) -> String {
                do { try body(); return "fine" } catch { return "caught" }
            }
            """,
            """
                let quiet = { () -> String in caught({ try risky() }) }
                print(quiet())
                do {
                    try run({ try risky(); let _ = caught({ }) })
                } catch { print("threw") }
            """);
}
