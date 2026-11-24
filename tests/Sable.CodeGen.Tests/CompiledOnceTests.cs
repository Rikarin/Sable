using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     What generic code compiled once serves (#297 stage 4): each program
///     runs natively and in the interpreter, leak-checked, with its generic
///     functions compiled once and specialised, and — in CI
///     (<c>SABLE_TEST_NATIVE=all</c>) — optimised and built in units; all
///     must print the same, and the functions named must have been compiled once.
/// </summary>
[Native]
public sealed class CompiledOnceTests {
    static void Prints(string expected, string program, params string[] once) {
        foreach (var mode in (GenericsMode[])[GenericsMode.Unspecialised, GenericsMode.Specialised]) {
            using var _ = SilGen.UseGenerics(mode);
            Assert.Equal((mode, expected), (mode, AssertAgree(program).Stdout));
            if (CanBuild && NativeMode.All) {
                var separate = BuildSeparatelyAndRun(program);
                Assert.Equal((mode, expected, 0), (mode, separate.Stdout + separate.Stderr, separate.ExitCode));
            }

            if (mode == GenericsMode.Unspecialised) {
                var result = Pipeline.Compile(new SourceText(program, "test.sb"), optimised: false);
                Assert.All(once, f => Assert.True(result.Sil!.Find(f + "$generic") is not null, $"{f} was not compiled once"));
                Assert.All(once, f => Assert.DoesNotContain(result.Sil!.Functions,
                    g => g.Name.StartsWith(f + "$", StringComparison.Ordinal) && !g.Name.StartsWith(f + "$generic", StringComparison.Ordinal) && !g.Name.Contains("$closure", StringComparison.Ordinal)));
            }
        }
    }

    /// <summary>A generic struct that holds its type parameters' values by value: its initialisers, memberwise, its own, throwing and delegating.</summary>
    [Fact]
    public void AStructHoldingItsParametersByValueIsInitialised() => Prints(
        "xy 4 3 made\n3 2 3 both\n7 s checked\nbad 9\nhello! hello!\ntrue true\n",
        """
            module Test

            struct Bad: Error { var n: Int }

            struct Pair<A, B> {
                var a: A
                var b: B
                var n: Int = 3
                var note: String

                init(_ a: A, _ b: B) {
                    self.a = a
                    self.b = b
                    self.note = "made"
                }

                init(checked a: A, _ b: B, _ ok: Bool) throws {
                    self.a = a
                    if !ok {
                        throw Bad { n: 9 }
                    }

                    self.b = b
                    self.note = "checked"
                }

                init(both a: A, _ b: B) {
                    self.init(a, b)
                    self.note = "both"
                }
            }

            struct Wrap<T> {
                var value: T
                var extra: T?
            }

            fn pair<A, B>(_ a: A, _ b: B) -> Pair<A, B> => Pair<A, B>(a, b)

            fn both<A, B>(_ a: A, _ b: B) -> Pair<A, B> => Pair<A, B>(both: a, b)

            fn checked<A, B>(_ a: A, _ b: B, _ ok: Bool) throws -> Pair<A, B> => try Pair<A, B>(checked: a, b, ok)

            fn wrap<T>(_ t: T) -> Wrap<T> => Wrap(value: t, extra: t)

            class Thing {
                var n: Int
                init(_ n: Int) {
                    self.n = n
                }
            }

            @main
            fn main() {
                let p = pair("x" + "y", 4)
                print("\(p.a) \(p.b) \(p.n) \(p.note)")
                let q = both(Thing(3), [1, 2])
                print("\(q.a.n) \(q.b.count) \(q.n) \(q.note)")
                for ok in [true, false] {
                    do {
                        let r = try checked(Thing(7), "s", ok)
                        print("\(r.a.n) \(r.b) \(r.note)")
                    } catch let bad as Bad {
                        print("bad \(bad.n)")
                    } catch {
                        print("other")
                    }
                }

                let w = wrap("hello" + "!")
                print("\(w.value) \(w.extra ?? "none")")
                let v = wrap(true)
                print("\(v.value) \(v.extra ?? false)")
            }
        """,
        "Test.pair", "Test.both", "Test.checked", "Test.wrap"
    );

    /// <summary>A generic type's own operators, applied to values of it and through a constraint.</summary>
    [Fact]
    public void AnOperatorOfAGenericTypeIsCompiledOnce() => Prints(
        "less\nmore\nsame\n4 6\ntrue\nfalse true true\n",
        """
        module Test

        struct Pair<A, B> {
            var a: A
            var b: B
        }

        extension Pair: Equatable where A: Equatable, B: Equatable {
            operator == (left: Pair<A, B>, right: Pair<A, B>) -> Bool => left.a == right.a && left.b == right.b
        }

        extension Pair: Comparable where A: Comparable, B: Comparable {
            operator < (left: Pair<A, B>, right: Pair<A, B>) -> Bool {
                if left.a == right.a {
                    return left.b < right.b
                }

                return left.a < right.a
            }
        }

        extension Pair where A: Numeric, B: Numeric {
            operator + (left: Pair<A, B>, right: Pair<A, B>) -> Pair<A, B> => Pair(a: left.a + right.a, b: left.b + right.b)
        }

        fn order<A: Comparable, B: Comparable>(_ x: Pair<A, B>, _ y: Pair<A, B>) -> String =>
            x == y ? "same" : x != y && x < y ? "less" : x > y ? "more" : "never"

        fn sum<A: Numeric>(_ x: Pair<A, A>, _ y: Pair<A, A>) -> Pair<A, A> => x + y

        fn among<T: Equatable>(_ x: T, _ all: T[]) -> Bool {
            for each in all {
                if each == x {
                    return true
                }
            }

            return false
        }

        @main
        fn main() {
            print(order(Pair(a: 1, b: "x"), Pair(a: 1, b: "y")))
            print(order(Pair(a: 2, b: "x"), Pair(a: 1, b: "y")))
            print(order(Pair(a: 2, b: "x"), Pair(a: 2, b: "x")))
            let s = sum(Pair(a: 1, b: 2), Pair(a: 3, b: 4))
            print("\(s.a) \(s.b)")
            print("\(among(Pair(a: 1, b: "q"), [Pair(a: 1, b: "p"), Pair(a: 1, b: "q")]))")
            let a: Int? = 4
            let b: Int? = none
            print("\(a == b) \(a == 4) \(1..<3 == 1..<3)")
        }
        """, "Test.order", "Test.sum", "Test.Pair.+"
    );

    /// <summary>A generic class with a deinit, made and destroyed by code that knows its arguments only by their metadata, one whose initialiser throws too.</summary>
    [Fact]
    public void AClassWithADeinitIsMadeAndDestroyed() => Prints(
        "cell of 3m after 2\n2\ncell of a after 1\nholder a gone\n1\n1\n1\ncell of 9m after 1\ncell of b after 1\nholder b gone\ncell of 1m after 1\nholder c gone\ntrue false\ncell of only here after 1\nonly: 1\nlog of 1\nend\nlog of 4\n",
        """
        module Test

        protocol Named {
            fn name() -> String
        }

        struct Meters: Named {
            var value: Int
            fn name() -> String => "\(value)m"
        }

        class Holder: Named {
            var text: String
            init(_ text: String) {
                self.text = text
            }

            fn name() -> String => text
            deinit {
                print("holder \(text) gone")
            }
        }

        struct Only: Named {
            fn name() -> String => "only here"
        }

        struct Bad: Error {}

        final class Cell<T: Named> {
            var item: T
            var count: Int = 0

            init(_ item: T) {
                self.item = item
            }

            init(checked item: T, _ ok: Bool) throws {
                self.item = item
                if !ok {
                    throw Bad {}
                }
            }

            fn bump() -> Int {
                count = count + 1
                return count
            }

            deinit {
                print("cell of \(item.name()) after \(count)")
            }
        }

        final class Log<T> {
            var items: T[>..] = []

            init() {}

            fn add(_ item: T) {
                items.append(item)
            }

            deinit {
                print("log of \(items.count)")
            }
        }

        fn cell<T: Named>(_ item: T) -> Cell<T> => Cell(item)

        fn use<T: Named>(_ item: T, _ times: Int) -> Int {
            let made = Cell(item)
            var last = 0
            for _ in 0..<times {
                last = made.bump()
            }

            return last
        }

        fn tryMake<T: Named>(_ item: T, _ ok: Bool) -> Bool {
            do {
                let made = try Cell(checked: item, ok)
                return made.bump() == 1
            } catch {
                return false
            }
        }

        fn log<T>(_ items: T[]) -> Log<T> {
            let made = Log<T>()
            for item in items {
                made.add(item)
            }

            return made
        }

        @main
        fn main() {
            print("\(use(Meters { value: 3 }, 2))")
            print("\(use(Holder("a"), 1))")
            do {
                let kept = cell(Holder("b"))
                print("\(kept.bump())")
                let other: Cell<Meters> = Cell(Meters { value: 9 })
                print("\(other.bump())")
            }

            print("\(tryMake(Meters { value: 1 }, true)) \(tryMake(Holder("c"), false))")
            print("only: \(use(Only(), 1))")
            let l = log([1, 2, 3])
            l.add(4)
            let _ = log(["x"])
            print("end")
        }
        """, "Test.use", "Test.tryMake"
    );

    /// <summary>A generic type's stored functions of its parameters' values: called, read, replaced and captured, by code that knows them and code that does not.</summary>
    [Fact]
    public void AGenericTypeStoresFunctions() => Prints(
        "top 9\ntop 3\ntrue false\na fig\nn is 4\nn is 5\n63\nbox 8\nchanged 1\nn is 6!\n60\n2\n3\n",
        """
        module Test

        struct Step<A, B> {
            var apply: (A) -> B
            var label: String

            fn run(_ a: A) -> B => apply(a)

            fn then<C>(_ next: Step<B, C>) -> Step<A, C> {
                let first = self
                return Step<A, C>(apply: { next.run(first.run($0)) }, label: "\(label)+\(next.label)")
            }
        }

        final class Heap<T> {
            var items: T[>..] = []
            var before: (T, T) -> Bool
            let show: (T) -> String?

            init(before: @escaping (T, T) -> Bool, show: @escaping (T) -> String?) {
                self.before = before
                self.show = show
            }

            fn add(_ item: T) {
                items.append(item)
            }

            fn top() -> T? {
                var best: T? = none
                for item in items {
                    if let b = best {
                        if before(item, b) {
                            best = item
                        }
                    } else {
                        best = item
                    }
                }

                return best
            }

            fn describe() -> String {
                guard let t = top() else {
                    return "empty"
                }

                return show(t) ?? "unshown"
            }
        }

        fn heap<T>(_ items: T[], _ before: @escaping (T, T) -> Bool, _ show: @escaping (T) -> String?) -> Heap<T> {
            let made = Heap<T>(before: before, show: show)
            for item in items {
                made.add(item)
            }

            return made
        }

        fn step<A>(_ f: @escaping (A) -> String) -> Step<A, String> => Step(apply: f, label: "step")

        fn twice<A>(_ s: Step<A, A>, _ a: A) -> A => s.apply(s.run(a))

        class Box {
            var n: Int
            init(_ n: Int) {
                self.n = n
            }
        }

        @main
        fn main() {
            let h = heap([3, 9, 4], { $0 > $1 }, { "top \($0)" })
            print(h.describe())
            h.before = { $0 < $1 }
            print(h.describe())
            let b = h.before
            print("\(b(1, 2)) \(h.before(2, 1))")
            let words = heap(["pear", "fig"], { $0.count < $1.count }, { $0 == "fig" ? "a fig" : none })
            print(words.describe())
            let s = step({ (n: Int) in "n is \(n)" })
            print(s.run(4))
            print(s.apply(5))
            print(twice(Step<Int, Int>(apply: { $0 * 3 }, label: "times"), 7))
            let boxes = heap([Box(1), Box(8)], { $0.n > $1.n }, { "box \($0.n)" })
            print(boxes.describe())
            var copy = s
            copy.apply = { "changed \($0)" }
            print(copy.apply(1))
            print(s.then(Step<String, String>(apply: { $0 + "!" }, label: "bang")).run(6))
            let total = [1, 2, 3, 4].lazy.filter({ $0 % 2 == 0 }).map({ $0 * 10 }).reduce(0, { $0 + $1 })
            print("\(total)")
            let texts = ["a", "bb", ""].lazy.compactMap({ $0.isEmpty ? none : $0 + "." }).map({ $0.count })
            for t in texts {
                print("\(t)")
            }
        }
        """, "Test.Heap.top", "Test.Step.run", "Test.Step.then", "Test.heap"
    );

    /// <summary>A requirement generic over parameters of its own, called through a table, and through one the library has.</summary>
    [Fact]
    public void ARequirementGenericOverItsOwnParameters() => Prints(
        "a!a! b!b! 5m!\nquiet 1m 2 quiet 5m 3\nx\ny\n4\nthrew\ngood 3\nbad 3\n2\n200\nh\n3 3 4\n",
        """
        module Test

        protocol Visitor {
            fn visit<T: Named>(_ item: T, _ times: Int) -> String
            fn pick<T>(_ a: T, _ b: T) -> T
            fn pair<A: Named, B>(_ a: A, _ b: B) throws -> (A, B)
            mutating fn note<T: Named>(_ item: T) -> Self
        }

        protocol Named {
            fn name() -> String
        }

        struct Meters: Named {
            var value: Int
            fn name() -> String => "\(value)m"
        }

        class Holder: Named {
            var text: String
            init(_ text: String) {
                self.text = text
            }

            fn name() -> String => text
        }

        struct Bad: Error {}

        struct Loud: Visitor {
            var notes: String[>..] = []

            fn visit<T: Named>(_ item: T, _ times: Int) -> String {
                var text = ""
                for _ in 0..<times {
                    text = text + item.name() + "!"
                }

                return text
            }

            fn pick<T>(_ a: T, _ b: T) -> T => a

            fn pair<A: Named, B>(_ a: A, _ b: B) throws -> (A, B) {
                if a.name() == "bad" {
                    throw Bad {}
                }

                return (a, b)
            }

            mutating fn note<T: Named>(_ item: T) -> Loud {
                notes.append(item.name())
                return self
            }
        }

        class Quiet: Visitor {
            var count: Int = 0
            init() {}

            fn visit<T: Named>(_ item: T, _ times: Int) -> String {
                count = count + times
                return "quiet \(item.name()) \(count)"
            }

            fn pick<T>(_ a: T, _ b: T) -> T => b

            fn pair<A: Named, B>(_ a: A, _ b: B) throws -> (A, B) => (a, b)

            fn note<T: Named>(_ item: T) -> Self {
                count = count + 100
                return self
            }
        }

        fn all<V: Visitor, N: Named>(_ visitor: V, _ items: N[]) -> String {
            var text = ""
            for item in items {
                text = text + visitor.visit(item, 2) + " "
            }

            return text + visitor.visit(Meters { value: 5 }, 1)
        }

        fn first<V: Visitor, T>(_ visitor: V, _ a: T, _ b: T) -> T => visitor.pick(a, b)

        fn both<V: Visitor, N: Named>(_ visitor: V, _ n: N) -> String {
            do {
                let (a, b) = try visitor.pair(n, 3)
                return "\(a.name()) \(b)"
            } catch {
                return "threw"
            }
        }

        fn noted<V: Visitor, N: Named>(_ visitor: V, _ n: N) -> V {
            var mine = visitor
            let _ = mine.note(n)
            return mine.note(Meters { value: 1 })
        }

        @main
        fn main() {
            print(all(Loud(), [Holder("a"), Holder("b")]))
            print(all(Quiet(), [Meters { value: 1 }]))
            print(first(Loud(), "x", "y"))
            print(first(Quiet(), "x", "y"))
            print("\(first(Quiet(), 3, 4))")
            print(both(Loud(), Holder("bad")))
            print(both(Loud(), Holder("good")))
            print(both(Quiet(), Holder("bad")))
            print("\(noted(Loud(), Holder("h")).notes.count)")
            print("\(noted(Quiet(), Holder("h")).count)")
            let text = "hello world"
            let cut = text[text.startIndex..<text.index(after: text.startIndex)]
            print("\(cut)")
            let xs = [1, 2, 3, 4, 5]
            print("\(xs[1...3].count) \(xs[2...].count) \(xs[..<^1].count)")
        }
        """, "Test.all", "Test.both", "Test.noted"
    );

    /// <summary>Functions that throw an error of a type parameter: called, caught, thrown on, stored and made optional; and arrays of them changed in place.</summary>
    [Fact]
    public void AnErrorOfATypeParameterIsCaught() => Prints(
        "bad 4\nworse w\nfine\n-1 5\ngot again\n3\nout 8\n6\nkept 2\n3 y 7\n6\noutcome 12\n",
        """
        module Test

        struct Bad: Error {
            var n: Int
        }

        struct Worse: Error {
            var text: String
        }

        fn caught<T, E: Error>(_ body: () throws(E) -> T) -> Outcome<T, E> {
            do {
                return .Ok(try body())
            } catch {
                return .Err(error)
            }
        }

        fn quietly<T, E: Error>(_ body: () throws(E) -> T) -> T? => try? body()

        fn passed<T, E: Error>(_ body: () throws(E) -> T, _ times: Int) throws(E) -> T {
            for _ in 1..<times {
                let _ = try body()
            }

            return try body()
        }

        fn recovered<T, E: Error>(_ body: () throws(E) -> T, _ other: (E) -> T) -> T {
            do {
                return try passed(body, 2)
            } catch {
                return other(error)
            }
        }

        struct Try<T, E: Error> {
            var body: () throws(E) -> T

            fn run() throws(E) -> T => try body()
            fn either(_ d: T) -> T {
                do {
                    return try run()
                } catch {
                    return d
                }
            }
        }

        fn swapped<T>(_ items: inout T[>..], _ i: Int, _ j: Int) {
            let kept = items[i]
            items[i] = items[j]
            items[j] = kept
            items.append(kept)
        }

        struct Node<T> {
            var value: T?
            var next: Int
        }

        struct Chain<T> {
            var nodes: Node<T>[>..] = []

            mutating fn add(_ t: T) {
                nodes.append(Node<T>(value: none, next: nodes.count))
                nodes[nodes.count - 1].value = t
                nodes[nodes.count - 1].next = 7
            }

            fn all() -> T[>..] {
                var items: T[>..] = []
                for node in nodes {
                    if let v = node.value {
                        items.append(v)
                    }
                }

                return items
            }

            mutating fn rotate() {
                var items = all()
                swapped(&items, 0, 1)
                nodes = []
                for item in items {
                    add(item)
                }
            }
        }

        fn sum(_ xs: Int[:]) -> Int {
            var s = 0
            for x in xs {
                s += x
            }

            return s
        }

        fn view<T>(_ items: T[>..], _ f: (T[:]) -> Int) -> Int => f(items)

        @main
        fn main() {
            let a = caught({ () throws(Bad) -> Int in throw Bad { n: 4 } })
            if case .Err(let bad) = a {
                print("bad \(bad.n)")
            }

            let b = caught({ () throws(Worse) -> String in throw Worse { text: "w" } })
            if case .Err(let worse) = b {
                print("worse \(worse.text)")
            }

            let c: Outcome<String, Bad> = caught({ () throws(Bad) -> String in "fine" })
            if case .Ok(let s) = c {
                print(s)
            }

            print("\(quietly({ () throws(Bad) -> Int in throw Bad { n: 1 } }) ?? -1) \(quietly({ () throws(Bad) -> Int in 5 }) ?? -1)")
            print(recovered({ () throws(Worse) -> String in throw Worse { text: "again" } }, { (e: Worse) in "got \(e.text)" }))
            print("\(passed({ 3 }, 3))")
            do {
                print(try passed({ () throws(Bad) -> String in throw Bad { n: 8 } }, 2))
            } catch {
                print("out \(error.n)")
            }

            let t = Try<Int, Bad>(body: { () throws(Bad) -> Int in throw Bad { n: 2 } })
            print("\(t.either(6))")
            do {
                print("\(try t.body())")
            } catch {
                print("kept \(error.n)")
            }

            var chain = Chain<String>()
            chain.add("x")
            chain.add("y")
            chain.rotate()
            print("\(chain.all().count) \(chain.all()[0]) \(chain.nodes[1].next)")
            print("\(view([1, 2, 3], { sum($0) }))")
            let parsed = Outcome.catching({ () throws(Bad) -> Int in throw Bad { n: 12 } })
            if case .Err(let e) = parsed {
                print("outcome \(e.n)")
            }
        }
        """, "Test.caught", "Test.recovered", "Test.Try.either", "Test.Chain.add", "Test.swapped", "Test.view"
    );

    /// <summary>A requirement generic over parameters of its own called through an existential, and an existential opened for an inout parameter, which is changed in its box, its own.</summary>
    [Fact]
    public void AGenericRequirementIsCalledThroughAnExistential() => Prints(
        "2m x3\na\n1\nquiet\nb\n2\n2 0\n5 5 0\n20 20\n",
        """
        module Test

        protocol Named {
            fn name() -> String
        }

        struct Meters: Named {
            var value: Int
            fn name() -> String => "\(value)m"
        }

        struct Word: Named {
            var text: String
            fn name() -> String => text
        }

        protocol Visitor {
            fn visit<T: Named>(_ item: T, _ times: Int) -> String
            fn pick<T>(_ a: T, _ b: T) -> T
            mutating fn note<T: Named>(_ item: T)
            fn count() -> Int
        }

        struct Loud: Visitor {
            var notes: String[>..] = []
            fn visit<T: Named>(_ item: T, _ times: Int) -> String => "\(item.name()) x\(times)"
            fn pick<T>(_ a: T, _ b: T) -> T => a
            mutating fn note<T: Named>(_ item: T) {
                notes.append(item.name())
            }

            fn count() -> Int => notes.count
        }

        struct Quiet: Visitor {
            var n: Int = 0
            fn visit<T: Named>(_ item: T, _ times: Int) -> String => "quiet"
            fn pick<T>(_ a: T, _ b: T) -> T => b
            mutating fn note<T: Named>(_ item: T) {
                n = n + 10
            }

            fn count() -> Int => n
        }

        fn bump<V: Visitor>(_ v: inout V, _ times: Int) -> Int {
            for _ in 0..<times {
                v.note(Meters { value: 1 })
            }

            return v.count()
        }

        @main
        fn main() {
            var all: (any Visitor)[>..] = [Loud(), Quiet()]
            for v in all {
                print(v.visit(Meters { value: 2 }, 3))
                print(v.pick("a", "b"))
                print("\(v.pick(1, 2))")
            }

            var one: any Visitor = all[0]
            one.note(Word { text: "w" })
            one.note(Meters { value: 4 })
            print("\(one.count()) \(all[0].count())")
            print("\(bump(&one, 3)) \(one.count()) \(all[0].count())")
            var two: any Visitor = Quiet()
            print("\(bump(&two, 2)) \(two.count())")
            all.append(two)
        }
        """, "Test.bump"
    );

    /// <summary>'any Any': a value of any type, cast back, with its dynamic type.</summary>
    [Fact]
    public void AnyAnyHoldsAValueOfAnyType() => Prints(
        "int 3\nmeters 4\nother\nInt\nString\nTest.Meters\n",
        """
        module Test

        struct Meters {
            var value: Int
        }

        fn f(_ x: any Any) -> String {
            if let i = x as? Int {
                return "int \(i)"
            }

            if let m = x as? Meters {
                return "meters \(m.value)"
            }

            return "other"
        }

        @main
        fn main() {
            let a: any Any = 3
            let b: any Any = Meters { value: 4 }
            print(f(a))
            print(f(b))
            print(f("x"))
            let all: (any Any)[] = [1, "x", Meters { value: 2 }]
            for x in all {
                let t: Type = type(of: x)
                print("\(t)")
            }
        }
        """
    );

    /// <summary>An optional of an opened type's value and an array of them are erased, as Self's are through an existential, and an unconstrained type's values as 'any Any'.</summary>
    [Fact]
    public void OptionalsAndArraysOfAnOpenedTypeAreErased() => Prints(
        "meters 5\na Meters of 5\nnone\nmeters 5\nmeters 5\n0\nnone\nnone\nnone\nnone\n0\n2\n  meters 3\n  meters 9\ntwin meters 4\nsame meters 4\n2\n  meters 4\n  meters 9\ntwin meters 5\nsame word\n0\nint 3\nInt\nInt\nstring word\nString\n",
        """
        module Test

        protocol Zero {
            static fn zero() -> Self
            fn label() -> String
            fn same() -> Self?
            fn many() -> Self[]
            var twin: Self? { get }
        }

        protocol Source {
            associatedtype Item
            fn item() -> Item
            fn items() -> Item[>..]
        }

        struct Meters: Zero, Source {
            var value: Int
            static fn zero() -> Meters => Meters { value: 5 }
            fn label() -> String => "meters \(value)"
            fn same() -> Meters? => value > 3 ? self : none
            fn many() -> Meters[] => [self, Meters { value: 9 }]
            var twin: Meters? => Meters { value: value + 1 }
            fn item() -> Int => value
            fn items() -> Int[>..] => [value, value]
        }

        struct Label: Zero, Source {
            var text: String
            static fn zero() -> Label => Label { text: "no" + "ne" }
            fn label() -> String => text
            fn same() -> Label? => self
            fn many() -> Label[] => []
            var twin: Label? => none
            fn item() -> String => text
            fn items() -> String[>..] => [text + "!"]
        }

        fn maybe<T: Zero>(_ t: T.Type, _ yes: Bool) -> T? => yes ? T.zero() : none

        fn several<T: Zero>(_ t: T.Type) -> T[>..] => [T.zero(), T.zero()]

        fn last<T: Zero>(_ all: T[], _ t: T.Type) -> Int => all.count

        fn bare<T>(_ t: T.Type, _ n: Int) -> T[>..] => []

        @main
        fn main() {
            let types: Type[] = [Meters.self, Label.self]
            for t in types {
                if let z = maybe(t, true) {
                    print(z.label())
                    if let m = z as? Meters {
                        print("a Meters of \(m.value)")
                    }
                }

                print(maybe(t, false) == none ? "none" : "some")
                for each in several(t) {
                    print(each.label())
                }

                print("\(bare(t, 3).count)")
            }

            let all: (any Zero)[] = [Meters { value: 3 }, Meters { value: 4 }, Label { text: "wo" + "rd" }]
            for z in all {
                if let s = z.same() {
                    print("same \(s.label())")
                }

                print("\(z.many().count)")
                for each in z.many() {
                    print("  \(each.label())")
                }

                if let w = z.twin {
                    print("twin \(w.label())")
                }
            }

            let sources: (any Source)[] = [Meters { value: 3 }, Label { text: "wo" + "rd" }]
            for s in sources {
                if let i = s.item() as? Int {
                    print("int \(i)")
                }

                if let i = s.item() as? String {
                    print("string \(i)")
                }

                for each in s.items() {
                    let t: Type = type(of: each)
                    print("\(t)")
                }
            }
        }
        """
    );
}
