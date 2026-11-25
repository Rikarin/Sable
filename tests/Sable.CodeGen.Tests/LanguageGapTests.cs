namespace Sable.CodeGen.Tests;

/// <summary>Gaps of the language that writing the library and its programs found (#297).</summary>
[Native]
public sealed class LanguageGapTests {
    /// <summary>An initialiser may be unsafe; a closure need not name what it is given; an @autoclosure may read an inout parameter.</summary>
    [Fact]
    public void UnsafeInitialisersAndUnnamedClosureParameters() =>
        Assert.Equal(
            """
            1000 1000
            3 4
            2 7 8 [3, 3] 3

            """,
            Programs.AssertAgree("""
            module I9a

            import Sable.IO

            @inline(never)
            fn check(_ c: @autoclosure () -> Bool) { if !c() { print("no") } }

            fn fill(_ xs: inout Int[>..], _ n: Int) {
                for i in 0..<n {
                    check(xs.count >= i)
                    xs.append(i)
                }
            }
            fn owned(_ n: Int) -> Int[>..] {
                var xs: Int[>..] = []
                for i in 0..<n {
                    check(xs.count >= i)
                    xs.append(i)
                }
                return xs
            }

            struct Raw {
                let n: Int
                unsafe init(_ n: Int) { self.n = n }
            }
            final class Holder {
                let n: Int
                public unsafe init(n: Int) { self.n = n }
            }

            fn made() -> Raw { unsafe { return Raw(3) } }
            fn held() -> Holder { unsafe { return Holder(n: 4) } }
            @main
            fn main() {
                var xs: Int[>..] = []
                fill(&xs, 1000)
                print("\(xs.count) \(owned(1000).count)")
                print("\(made().n) \(held().n)")
                let f = { (_: Int, x: Int) -> Int in x }
                let g: (Int, Int) -> Int = { (_: Int, _: Int) in 7 }
                let h: (Int, Int) -> Int = { _, _ in 8 }
                let k = (_: Int, x: Int) => x + 1
                print("\(f(1, 2)) \(g(1, 2)) \(h(1, 2)) \([1, 2].map { _ in 3 }) \(k(1, 2))")
            }
            """).Stdout
        );

    /// <summary>A class's extension adds convenience initialisers; a label left out is a worse match than none.</summary>
    [Fact]
    public void AClassExtensionAddsConvenienceInitialisers() =>
        Assert.Equal(
            """
            dog rex 4
            none 4
            a 2
            bad
            1 [true]
            plain 1 index 2 plain named

            """,
            Programs.AssertAgree("""
            module I9b

            open class Animal {
                var name: String
                var legs = 4
                init(name: String) { self.name = name }
                open fn say() -> String => "\(name) \(legs)"
            }
            extension Animal {
                convenience init() { self.init(name: "none") }
                convenience init(_ words: String[>..]) throws(Bad) {
                    if words.isEmpty { throw Bad() }
                    self.init(name: words[0])
                    legs = words.count
                }
            }
            struct Bad: Error { }
            final class Dog: Animal {
                override fn say() -> String => "dog " + super.say()
            }
            final class Cell<T> {
                let item: T
                init(item: T) { self.item = item }
            }
            extension Cell {
                convenience init(_ all: T[>..]) { self.init(item: all[0]) }
            }
            struct Spot {
                fn at(_ i: Int) -> String => "plain \(i)"
                fn at(index i: Int) -> String => "index \(i)"
            }
            fn find(_ x: Int) -> String => "plain"
            fn find(x: Int) -> String => "named"

            @main
            fn main() {
                print(Dog(name: "rex").say())
                print(Animal().say())
                print((try? Animal(["a", "b"]))?.say() ?? "bad")
                print((try? Animal([]))?.say() ?? "bad")
                print("\(Cell<Int>([1, 2]).item) \(Cell([true]).item)")
                print("\(Spot().at(1)) \(Spot().at(index: 2)) \(find(1)) \(find(x: 1))")
            }
            """).Stdout
        );

    /// <summary>A global that another reads through a requirement, a static one, a property or an override is set before it; a call that only might read one is no error.</summary>
    [Fact]
    public void GlobalsAreSetAfterWhatTheirInitialValuesReadThroughAProtocol() =>
        Assert.Equal(
            """
            ab MORE title abMORE spoken ab MORE titleab quiet

            """,
            Programs.AssertAgree("""
            module I9c

            protocol Gives {
                fn give() -> String
                var extra: String { get }
                static fn name() -> String
            }
            struct Rec: Gives {
                fn give() -> String => words[0]
                var extra: String => more
                static fn name() -> String => title
            }
            open class Base {
                open fn say() -> String => "base"
                init() { }
            }
            final class Derived: Base {
                override fn say() -> String => spoken
            }

            fn gather<G: Gives>(_ g: G) -> String => "\(g.give()) \(g.extra) \(G.name())"
            fn through(_ g: any Gives) -> String => g.give() + g.extra
            fn speak(_ b: Base) -> String => b.say()

            let all: String = gather(Rec())
            let boxed: String = through(Rec())
            let heard: String = speak(Derived())
            struct Holder { static let kept: String = gather(Rec()) + Rec().give() }
            let words: String[>..] = ["a" + "b", "c"]
            let more: String = "more".uppercased()
            let title: String = "ti" + "tle"
            let spoken: String = "spo" + "ken"

            // A call that may read what is being set, and does not: no error.
            struct Quiet: Gives {
                fn give() -> String => "quiet"
                var extra: String => loud
                static fn name() -> String => "q"
            }
            fn only(_ g: any Gives) -> String => g.give()
            let loud: String = only(Quiet())

            @main
            fn main() {
                print("\(all) \(boxed) \(heard) \(Holder.kept) \(loud)")
            }
            """).Stdout
        );

    /// <summary>An enum's case of a type's name hides neither its initialisers nor its static members; a field's type is read off a generic type's initialiser.</summary>
    [Fact]
    public void AnEnumsCaseDoesNotHideATypeThatIsCalled() =>
        Assert.Equal(
            """
            4 Circle 8
            0 1

            """,
            Programs.AssertAgree("""
            module I9d

            import Sable.Concurrency

            struct Circle {
                let r: Int
                static fn unit() -> Circle => Circle(r: 1)
                init(r: Int) { self.r = r }
            }
            enum Kind {
                Circle, Square
                fn make() -> Circle => Circle(r: 4)
                fn called() -> Int => Circle(r: 7).r + Circle.unit().r
                static fn other(_ c: Circle) -> Kind => .Circle
            }
            final class Counter {
                let at = Atomic<Int>(0)
                let all: Mutex<Int[>..]> = Mutex([])
                init() { }
                fn next() -> Int { return at.add(1) }
            }

            @main
            fn main() {
                print("\(Kind.Square.make().r) \(Kind.other(Circle(r: 1))) \(Kind.Square.called())")
                let c = Counter()
                print("\(c.next()) \(c.next())")
            }
            """).Stdout
        );

    /// <summary>A pointer to a fixed array's place, which holds the array; only a native program has one.</summary>
    [Fact]
    public void AFixedArraysPlaceIsPointedAt() {
        if (!Programs.CanBuild) {
            return;
        }

        foreach (var optimise in (bool[])[false, true]) {
            Assert.Equal("9 5 7\n3\ntrue\n", Programs.BuildAndRun("""
            module I9e

            import Sable.Interop

            fn bytes() -> U64 {
                var key: U64[16] = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]
                Memory.withPointer(to: &key) { p in unsafe {
                    var k = p.load()
                    k[3] = 9
                    p.store(k) } }
                return key[3]
            }


            struct Keyed { var k: U64[16], var n: Int }
            fn inStruct() -> U64 {
                var key = Keyed { k: [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15], n: 1 }
                Memory.withPointer(to: &key.k) { p in unsafe { p.store(p.load()) } }
                return key.k[5]
            }
            fn read(_ key: U64[16]) -> U64 {
                var copy = key
                return Memory.withPointer(to: &copy) { p in unsafe { return p.load()[7] } }
            }
            fn closed<T>(_ x: T) -> Int {
                var c = x
                return Memory.withPointer(to: &c) { p in 3 }
            }
            @inline(never)
            fn check(_ c: @autoclosure () -> Bool) { if !c() { print("no") } }
            fn push(_ xs: inout Int[>..], _ n: Int) {
                check(xs.count >= 0)
                xs.append(n)
            }
            @main
            fn main() {
                print("\(bytes()) \(inStruct()) \(read([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]))")
                let k: U64[16] = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]
                print("\(closed(k))")
                // An inout parameter a closure captures is moved to its box: appending to it copies nothing.
                var xs: Int[>..] = [1]
                xs.reserveCapacity(100)
                let before = xs.withPointer { $0.address }
                push(&xs, 2)
                print("\(xs.withPointer { $0.address } == before && xs.count == 2)")
            }
            """, optimise).Stdout);
        }
    }

    /// <summary>A generic type's record that leaves out a field whose default is of its type parameters, and a function named as a value where one is written out.</summary>
    [Fact]
    public void ARecordsDefaultsHaveItsTypeArgumentsAndAFunctionIsWrittenOut() =>
        Assert.Equal("a 0\n1 b 1\n<(Int) -> Int>\n", Programs.AssertAgree("""
            module M

            struct Inner<T> {
                var last: T
                var log: T[>..] = []
            }

            struct Outer<A, B> {
                var first: A
                var inner: Inner<B>? = none
                var seen: [String: A] = [:]
            }

            fn kept<T>(_ t: T) -> Inner<T> {
                var i = Inner<T> { last: t }
                i.log.append(t)
                return i
            }

            fn twice(_ n: Int) -> Int => n * 2

            @main
            fn main() {
                let i = Inner<String> { last: "a" }
                print("\(i.last) \(i.log.count)")
                var o = Outer<Int, String> { first: 1 }
                o.inner = kept("b")
                print("\(o.first) \(o.inner!.last) \(o.inner!.log.count + o.seen.count)")
                print("\(twice)")
            }
            """).Stdout);
}
