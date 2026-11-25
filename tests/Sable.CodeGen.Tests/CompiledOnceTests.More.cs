namespace Sable.CodeGen.Tests;

/// <summary>What completes stage 4 of #297: the kinds of generic code that were still copied for each type.</summary>
public sealed partial class CompiledOnceTests {
    /// <summary>Places reached through inout parameters, closures and objects, and autoclosures over a type parameter's values.</summary>
    [Fact]
    public void PlacesAndAutoclosuresAreCompiledOnce() => Prints(
        "ab 1\nnone\n3\n4\nx\ny\n5 5\nitems: 2\nok 2\nok 1\n[2, 3]\n",
        """
            module Test

            struct State<T> {
                var value: T?
                var log: T[>..]
                var inner: Inner<T>
            }

            struct Inner<T> {
                var last: T
                var count: Int
            }

            fn change<T>(_ state: inout State<T>, _ value: T) {
                state.value = value
                state.log.append(value)
                state.inner.last = value
                state.inner.count += 1
            }

            fn clear<T>(_ state: inout State<T>) {
                state.value = none
            }

            fn viaClosure<T>(_ state: inout State<T>, _ value: T) -> Int {
                let run = () => {
                    state.log.append(value)
                    state.inner.last = value
                }
                run()
                return state.log.count
            }

            struct Bag<T> {
                var items: T[>..] = []

                mutating fn addAll(_ more: T[]) -> Int {
                    more.forEach { items.append($0) }
                    return items.count
                }
            }

            class Holder<T> {
                var state: State<T>
                init(_ t: T) {
                    state = State<T> { value: none, log: [], inner: Inner<T> { last: t, count: 0 } }
                }
            }

            fn into<T>(_ holder: Holder<T>, _ value: T) {
                holder.state.inner.last = value
                holder.state.log.append(value)
            }

            fn swapIn<T>(_ slot: inout T?, _ value: T) -> T? {
                let old = slot
                slot = value
                return old
            }

            fn checked<T>(_ items: T[], _ at: Int) -> T {
                precondition(at < items.count, "\(at) past \(items.count)")
                return items[at]
            }

            fn lazily<T>(_ items: T[], _ message: @autoclosure () -> String) -> String => items.count > 1 ? message() : "ok \(items.count)"

            fn describe<T>(_ items: T[]) -> String => lazily(items, "items: \(items.count)")

            @main
            fn main() {
                var s = State<String> { value: none, log: [], inner: Inner<String> { last: "", count: 0 } }
                change(&s, "a" + "b")
                print("\(s.value ?? "none") \(s.inner.count)")
                clear(&s)
                print(s.value ?? "none")
                var n = State<Int> { value: 1, log: [], inner: Inner<Int> { last: 0, count: 0 } }
                change(&n, 2)
                change(&n, 3)
                print("\(n.inner.last)")
                _ = viaClosure(&n, 4)
                print("\(n.inner.last)")
                let h = Holder("x" + "")
                print("\(h.state.inner.last)")
                into(h, "y")
                print(h.state.log[0])
                var b = Bag<Int>()
                print("\(b.addAll([1, 2, 3, 4, 5])) \(b.items.count)")
                print(describe(["p", "q"]))
                var slot: Int? = 1
                _ = swapIn(&slot, 2)
                print("ok \(slot ?? 0)")
                print("ok \(checked([1, 2], 0))")
                print("\([1, 2, 3].filter { $0 > 1 })")
            }
        """,
        "Test.change", "Test.clear", "Test.viaClosure", "Test.Bag.addAll", "Test.into", "Test.swapIn", "Test.checked", "Test.lazily", "Test.describe"
    );

    /// <summary>Initialisers: of arrays, of a type that is not generic, of a protocol's extension and through a requirement.</summary>
    [Fact]
    public void InitialisersOfEveryKindAreCompiledOnce() => Prints(
        "3\n2\n6\nempty 0\nmade 2\n7 hi\n",
        """
            module Test

            extension<Element> Element[>..] {
                public init<S: Sequence>(listing s: S) where S.Element == Element, S.Failure == Never {
                    self = []
                    for x in s {
                        append(x)
                    }
                }
            }

            struct Total {
                var n: Int
                init<S: Sequence>(of s: S) where S.Element == Int, S.Failure == Never {
                    n = 0
                    for x in s {
                        n += x
                    }
                }
            }

            protocol Makes {
                init()
                var name: String { get }
                mutating fn add()
            }

            struct Counter: Makes {
                var n: Int = 0
                init() { }
                var name: String => n == 0 ? "empty \(n)" : "made \(n)"
                mutating fn add() {
                    n += 1
                }
            }

            fn make<T: Makes>(_ t: T.Type) -> T => T()

            fn makeAdding<T: Makes>(_ t: T.Type, _ n: Int) -> T {
                var made = T()
                for _ in 0..<n {
                    made.add()
                }

                return made
            }

            class Log {
                let text: String
                let n: Int
                init<T>(_ value: T, _ n: Int) {
                    text = "\(value)"
                    self.n = n
                }
            }

            @main
            fn main() {
                print("\(Int[>..](listing: [1, 2, 3]).count)")
                print("\(String[>..](listing: ["a", "b"]).count)")
                print("\(Total(of: [1, 2, 3]).n)")
                print(make(Counter.self).name)
                print(makeAdding(Counter.self, 2).name)
                print("\(Log(7, 1).text) \(Log("h" + "i", 2).text)")
            }
        """,
        "Test.make", "Test.makeAdding"
    );

    /// <summary>A 'where' clause that makes a type parameter another type.</summary>
    [Fact]
    public void AParameterAWhereClauseFixesIsThatType() => Prints(
        "a=1 b=2\n2\n3\n[[1, 2], [3]]\n",
        """
            module Test

            struct Pair<K, V> {
                var key: K
                var value: V
            }

            extension Pair where K == String {
                fn text() -> String => "\(key)=\(value)"
            }

            extension Pair where K == Int, V == Int {
                fn sum() -> Int => key + value
            }

            struct Groups<K, V> {
                var all: V[>..] = []
                init<S: Sequence>(of s: S) where V == S.Element[>..], S.Failure == Never {
                    var one: S.Element[>..] = []
                    for x in s {
                        one.append(x)
                    }

                    all.append(one)
                }
            }

            @main
            fn main() {
                print("\(Pair(key: "a", value: 1).text()) \(Pair(key: "b", value: 2).text())")
                print("\(Pair(key: 1, value: 1).sum())")
                let g = Groups<String, Int[>..]>(of: [1, 2, 3])
                print("\(g.all[0].count)")
                print("\([[1, 2], [3]])")
            }
        """,
        "Test.Pair.text", "Test.Pair.sum"
    );

    /// <summary>Arrays, slices and optionals of a type parameter given where a conformance is required.</summary>
    [Fact]
    public void ArraysOfATypeParameterConformThroughTemplates() => Prints(
        "[1, 2] a b .\n3\ntrue false\n1 2 3 \n3 \n2\n4 none\ntrue\n",
        """
            module Test

            fn joined<C: Collection>(_ c: C) -> String {
                var text = ""
                for x in c {
                    text += "\(x) "
                }

                return text
            }

            fn listed<T>(_ all: T[>..]) -> String => joined(all)

            fn sliced<T>(_ all: T[>..]) -> String => joined(all[1..<all.count])

            fn doubled<T>(_ all: T[>..]) -> T[>..] => all.map { $0 }

            fn lastOf<T>(_ all: T[>..]) -> T? => all.last

            fn same<T: Equatable>(_ a: T[>..], _ b: T[>..]) -> Bool => equal(a, b)

            fn equal<E: Equatable>(_ a: E, _ b: E) -> Bool => a == b

            fn sameOptional<T: Equatable>(_ a: T?, _ b: T?) -> Bool => equal(a, b)

            fn cast<S, T>(_ s: S, _ t: T.Type) -> Int => (s as? T[>..])?.count ?? -1

            fn counted<T: Debug>(_ t: T?) -> String => shown(t)

            fn held(_ n: Int?) -> Int? => n

            fn text(_ s: String?) -> String? => s

            fn shown<D: Debug>(_ d: D) -> String => d.debugDescription()

            @main
            fn main() {
                print("\(doubled([1, 2])) \(listed(["a", "b"])).")
                print("\(lastOf([1, 2, 3]) ?? 0)")
                print("\(same([1, 2], [1, 2])) \(same(["a"], ["b"]))")
                print(listed([1, 2, 3]))
                print(sliced(["", "3"]))
                print("\(cast([1, 2] as Int[>..], Int.self))")
                print("\(counted(held(4))) \(counted(held(none)))")
                print("\(sameOptional(text("a"), text("a")))")
            }
        """,
        "Test.joined", "Test.listed", "Test.sliced", "Test.doubled", "Test.lastOf", "Test.same", "Test.equal", "Test.sameOptional", "Test.cast", "Test.shown"
    );

    /// <summary>Async closures over a type parameter, async requirements with its values, and actors generic over one.</summary>
    [Fact]
    public void AsyncClosuresAndActorsAreCompiledOnce() => Prints(
        "2 a!\nbad 3\n5\nx 2\n6\nitem 1 item 2 end\nstopped\n",
        """
            module Test

            struct Bad: Error { var n: Int }

            fn apply<T, U>(_ t: T, _ f: async (T) -> U) async -> U => await f(t)

            fn tried<T, E: Error>(_ f: async () throws(E) -> T) async throws(E) -> T => try await f()

            protocol Source {
                associatedtype Item
                associatedtype Failure: Error
                mutating fn take() async throws(Failure) -> Item?
            }

            struct Numbers: Source {
                alias Failure = Never
                var n: Int = 0
                let limit: Int
                mutating fn take() async -> Int? {
                    await Task.yield()
                    n += 1
                    return n > limit ? none : n
                }
            }

            struct Failing: Source {
                mutating fn take() async throws(Bad) -> String? {
                    await Task.yield()
                    throw Bad { n: 1 }
                }
            }

            fn all<S: Source>(_ s: S) async throws(S.Failure) -> S.Item[>..] {
                var source = s
                var taken: S.Item[>..] = []
                while let item = try await source.take() {
                    taken.append(item)
                }

                return taken
            }

            actor Box<T: Sendable> {
                var value: T
                var changes: Int = 0
                init(_ value: T) {
                    self.value = value
                }

                fn set(_ t: T) -> T {
                    let old = value
                    value = t
                    changes += 1
                    return old
                }

                fn get() -> T => value
            }

            fn exchange<T: Sendable>(_ box: Box<T>, _ t: T) async -> T {
                _ = await box.set(t)
                _ = await box.set(t)
                return await box.get()
            }

            fn keep<T>(_ t: T) async -> T => await apply(t, (x: T) => x)

            fn failing() async throws(Bad) -> Int {
                await Task.yield()
                throw Bad { n: 3 }
            }

            fn five() async -> Int => 5

            @main
            fn main() async {
                let two = await apply(1, { n in
                    await Task.yield()
                    return n + 1
                })
                let a = await keep("a" + "!")
                print("\(two) \(a)")
                do {
                    let n = try await tried(failing)
                    print("\(n)")
                } catch {
                    print("bad \(error.n)")
                }

                print("\(await tried(five))")
                let box = Box("y" + "")
                let x = await exchange(box, "x" + "")
                print("\(x) \(await box.changes)")
                print("\((await all(Numbers(limit: 3))).reduce(0, (a, b) => a + b))")
                var text = ""
                for n in await all(Numbers(limit: 2)) {
                    text += "item \(n) "
                }

                print(text + "end")
                do {
                    _ = try await all(Failing())
                } catch {
                    print("stopped")
                }
            }
        """,
        "Test.apply", "Test.tried", "Test.all", "Test.Box.set", "Test.Box.get", "Test.exchange"
    );

    /// <summary>Errors of a union of type parameters' errors, thrown on, boxed, and thrown by a 'rethrows' callee.</summary>
    [Fact]
    public void UnionsOfTypeParametersErrorsAreCompiledOnce() => Prints(
        "1 2\na 3\nb four\n1 \nsame 5\nnever 2\nany a\nboth b\n2 1\nfailed 9\n",
        """
            module Test

            struct A: Error { var n: Int }
            struct B: Error { var s: String }

            protocol Reader {
                associatedtype Failure: Error
                fn read() throws(Failure) -> Int
            }

            struct Good: Reader {
                alias Failure = Never
                let n: Int
                fn read() -> Int => n
            }

            struct FailsA: Reader {
                let n: Int
                fn read() throws(A) -> Int => throw A { n: n }
            }

            struct FailsB: Reader {
                let s: String
                fn read() throws(B) -> Int => throw B { s: s + "" }
            }

            fn both<X: Reader, Y: Reader>(_ x: X, _ y: Y) throws(X.Failure | Y.Failure) -> Int {
                let first = try x.read()
                let second = try y.read()
                print("\(first) \(second)")
                return first + second
            }

            fn nested<X: Reader, Y: Reader>(_ x: X, _ y: Y) throws(X.Failure | Y.Failure) -> Int => try both(y, x)

            fn erased<X: Reader, Y: Reader>(_ x: X, _ y: Y) throws -> Int => try both(x, y)

            fn mapped<X: Reader>(_ x: X, _ f: (Int) throws -> Int) rethrows(X.Failure) -> Int => try f(try x.read())

            fn plain<X: Reader>(_ x: X) throws(X.Failure) -> Int => try mapped(x, { $0 + 1 })

            fn say<X: Reader, Y: Reader>(_ x: X, _ y: Y) where X.Failure == A, Y.Failure == B {
                do {
                    _ = try both(x, y)
                } catch let a as A {
                    print("a \(a.n)")
                } catch let b as B {
                    print("b \(b.s)")
                } catch {
                    print("other")
                }
            }

            @main
            fn main() {
                _ = both(Good(n: 1), Good(n: 2))
                say(FailsA(n: 3), FailsB(s: "x"))
                do {
                    _ = try both(Good(n: 1), FailsB(s: "four"))
                } catch {
                    print("b \(error.s)")
                }

                do {
                    _ = try nested(FailsA(n: 5), Good(n: 1))
                } catch {
                    print("1 ")
                }

                do {
                    _ = try both(FailsA(n: 5), FailsA(n: 6))
                } catch {
                    print("same \(error.n)")
                }

                print("never \(plain(Good(n: 1)))")
                do {
                    _ = try erased(FailsA(n: 1), FailsB(s: "x"))
                } catch let a as A {
                    print("any a")
                } catch {
                    print("other")
                }

                do {
                    _ = try nested(FailsA(n: 5), FailsB(s: "x"))
                } catch let a as A {
                    print("both a")
                } catch {
                    print("both b")
                }

                print("\([1, 2].elementsEqual([1, 2]) ? 2 : 0) \(["a", "b"].starts(with: ["a"]) ? 1 : 0)")
                do {
                    _ = try plain(FailsA(n: 9))
                } catch {
                    print("failed \(error.n)")
                }
            }
        """,
        "Test.both", "Test.nested", "Test.erased", "Test.mapped", "Test.plain"
    );

    /// <summary>A function a generic class stores optionally, and a class with a deinit.</summary>
    [Fact]
    public void StoredOptionalFunctionsAndDeinitsAreCompiledOnce() => Prints(
        "true false\nfalse\nc b a\n3\ngone 1\ngone two\n",
        """
            module Test

            class Order<T: Comparable> {
                var before: ((T, T) -> Bool)?
                init() {
                    before = none
                }

                init(_ before: @escaping (T, T) -> Bool) {
                    self.before = before
                }

                fn first(_ a: T, _ b: T) -> Bool {
                    if let before = before {
                        return before(a, b)
                    }

                    return a < b
                }

                fn isPlain() -> Bool => before == none
            }

            class Watched<T> {
                let value: T
                init(_ value: T) {
                    self.value = value
                }

                deinit {
                    print("gone \(value)")
                }
            }

            fn sorted<T: Comparable>(_ all: T[], _ order: Order<T>) -> T[>..] => all.sorted(by: { order.first($0, $1) })

            @main
            fn main() {
                let plain = Order<Int>()
                let reversed = Order<String>((a, b) => a > b)
                print("\(plain.first(1, 2)) \(reversed.first("a", "b"))")
                print("\(reversed.isPlain())")
                print(sorted(["a", "b", "c"], reversed).joined(separator: " "))
                print("\(sorted([2, 1, 3], plain)[2])")
                _ = Watched(1)
                let w = Watched("t" + "wo")
                _ = w.value
            }
        """,
        "Test.Order.first", "Test.Order.isPlain", "Test.sorted"
    );

    /// <summary>
    ///     Generic classes in a hierarchy: overridden by generic and other
    ///     subclasses, made where their arguments are known only at run time,
    ///     called through the vtable from code that knows them and code that
    ///     does not, their initialisers and deinits chained.
    /// </summary>
    [Fact]
    public void GenericClassHierarchiesAreCompiledOnce() => Prints(
        "box 1\nint 2\nint 2!\npair x/7\npair x/7!\nlist of 2\nlist of 2\nbox a\npair b/b!\n9 10\ngone pair y\ngone box y\ngone box z\nbase 3 int 4\nmade 5 int\nswap 2\n0\nthrew\n",
        """
            module Test

            struct Bad: Error { var n: Int }

            open class Box<T> {
                var value: T
                var shown: Bool = true
                init(_ value: T) {
                    self.value = value
                }

                open fn name() -> String => "box \(value)"

                open fn get() -> T => value

                open fn swap(_ t: T) -> T {
                    let old = value
                    value = t
                    return old
                }

                open fn check(_ t: T) throws -> T => t

                open var label: String => name() + "!"

                fn plain() -> T => self.get()
            }

            class IntBox: Box<Int> {
                override fn name() -> String => "int \(value)"

                override fn get() -> Int => value + 1

                override fn check(_ t: Int) throws -> Int {
                    throw Bad { n: t }
                }
            }

            class Pair<A, B>: Box<A> {
                var other: B
                init(_ a: A, _ b: B) {
                    other = b
                    super.init(a)
                }

                override fn name() -> String => "pair \(value)/\(other)"
            }

            class Listing<E>: Box<E[>..]> {
                override fn name() -> String => "list of \(value.count)"
            }

            open class Watched<T> {
                let value: T
                init(_ value: T) {
                    self.value = value
                }

                deinit {
                    print("gone box \(value)")
                }
            }

            class WatchedPair<T>: Watched<T> {
                deinit {
                    print("gone pair \(value)")
                }
            }

            class Base {
                let n: Int
                init(_ n: Int) {
                    self.n = n
                }

                fn name() -> String => "base \(n)"
            }

            class Derived<T>: Base {
                let t: T
                init(_ t: T) {
                    self.t = t
                    super.init(4)
                }

                override fn name() -> String => "\(t) \(n)"
            }

            fn nameOf<T>(_ b: Box<T>) -> String => b.name()

            fn labelOf<T>(_ b: Box<T>) -> String => b.label

            fn getOf<T>(_ b: Box<T>) -> T => b.get()

            fn swapped<T>(_ b: Box<T>, _ t: T) -> T => b.swap(t)

            fn checked<T>(_ b: Box<T>, _ t: T) throws -> T => try b.check(t)

            fn makeBox<T>(_ t: T) -> Box<T> => Box<T>(t)

            fn makePair<A, B>(_ a: A, _ b: B) -> Box<A> => Pair<A, B>(a, b)

            fn watch<T>(_ t: T, _ u: T) {
                let a = Watched<T>(u)
                let b = WatchedPair<T>(t)
                _ = a.value
                _ = b.value
            }

            fn derived<T>(_ t: T) -> Base => Derived<T>(t)

            @main
            fn main() {
                print(nameOf(Box(1)))
                let i: Box<Int> = IntBox(2)
                print(nameOf(i))
                print(labelOf(i))
                let p: Box<String> = Pair("x" + "", 7)
                print(p.name())
                print(labelOf(p))
                let all: Box<Int[>..]> = Listing([1, 2])
                print(nameOf(all))
                let bound = all.name
                print(bound())
                print(makeBox("a" + "").name())
                print(labelOf(makePair("b" + "", "b" + "")))
                print("\(getOf(Box(9))) \(i.plain() + 7)")
                watch("y" + "", "z" + "")
                print("\(Base(3).name()) \(derived("int").name())")
                let made = makeBox(5)
                print("made \(made.get()) \(makePair(1, "int").get() == 1 ? "int" : "?")")
                print("swap \(swapped(i, 8))")
                do {
                    print("\(try checked(Box(3), 3) - 3)")
                    _ = try checked(i, 1)
                } catch {
                    print("threw")
                }
            }
        """,
        "Test.nameOf", "Test.labelOf", "Test.getOf", "Test.swapped", "Test.checked", "Test.makeBox", "Test.makePair", "Test.watch", "Test.derived"
    );
}
