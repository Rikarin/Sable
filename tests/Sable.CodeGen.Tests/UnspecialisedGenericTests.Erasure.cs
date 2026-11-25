namespace Sable.CodeGen.Tests;

/// <summary>
///     Values of a type parameter's type made existentials and cast to them in
///     code compiled once, moved, put in raw memory and shared between tasks;
///     calls with an error that is none (#297 stage 4).
/// </summary>
public sealed partial class UnspecialisedGenericTests {
    const string Erasing = """
        protocol Named { fn n() -> String }
        protocol Closing { fn close() -> String }
        extension Int: Named { fn n() -> String { return "int" } }
        open class Animal { init() {} }
        final class Dog: Animal, Named, Closing {
            override init() { super.init() }
            fn n() -> String { return "dog" }
            fn close() -> String { return "closed dog" }
        }
        struct Big: Closing {
            let a: String
            let b: Int
            let c: String
            fn close() -> String { return "closed \(a)" }
        }
        fn box<T>(_ x: T) -> any Any { return x }
        fn named<T: Named>(_ x: T) -> any Named { return x }
        fn close<T>(_ x: T) -> String {
            if let c = x as? any Closing {
                return c.close()
            }
            return "-"
        }
        fn forced<T>(_ x: T) -> any Closing { return x as! any Closing }
        fn kind<T>(_ x: T) -> Type { return type(of: x) }
        struct Held<T> {
            let item: T
            fn any() -> any Any { return item }
        }
        fn swapped<T>(_ a: T, _ b: T) -> (T, T) {
            var first = a
            var second = b
            let kept = move first
            first = move second
            second = kept
            return (first, second)
        }
        """;

    [Fact]
    public void AValueIsErasedToAnExistential() => Prints(
        """
        3 s true true c Int Test.Dog
        3 Int Int Int
        int dog 5
        q
        """ + "\n",
        Erasing,
        """
            let a = box(3)
            let b = box("s")
            let d = box(Dog())
            let g = box(Big { a: "big", b: 1, c: "c" })
            print("\(a as? Int ?? -1) \(b as? String ?? "-") \((d as? Dog) != none) \((d as? Animal) != none) \((g as? Big)?.c ?? "-") \(type(of: a)) \(type(of: d))")
            let p = box(a)
            print("\(p as? Int ?? -1) \(type(of: p)) \(kind(a)) \(kind(named(4)))")
            print("\(named(4).n()) \(named(Dog()).n()) \((named(5) as? Int) ?? 0)")
            print("\((Held { item: "q" }.any() as? String) ?? "-")")
        """
    );

    [Fact]
    public void AValueIsCastToAnExistential() => Prints(
        "closed dog - closed x closed big -\nclosed f\n",
        Erasing,
        """
            let a = box(3)
            let g = box(Big { a: "big", b: 1, c: "c" })
            print("\(close(Dog())) \(close(3)) \(close(Big { a: "x", b: 1, c: "" })) \(close(g)) \(close(a))")
            print(forced(Big { a: "f", b: 1, c: "" }).close())
        """
    );

    [Fact]
    public void AValueIsMoved() => Prints(
        "2 1 b a c\n",
        Erasing,
        """
            let n = swapped(1, 2)
            let s = swapped("a", "b")
            let big = swapped(Big { a: "", b: 1, c: "" }, Big { a: "", b: 2, c: "c" })
            print("\(n.0) \(n.1) \(s.0) \(s.1) \(big.0.c)")
        """
    );

    /// <summary>Mutex, OnceCell and the concurrent collections, whose values are in raw memory, and their closures, which throw nothing.</summary>
    [Fact]
    public void ValuesAreKeptInRawMemory() => Prints(
        "start! 6 failed once once d 1 one true\n",
        "import Sable.Concurrency\n" + Erasing + """

            struct Bad: Error { }
            fn guarded<T>(_ value: T) -> Mutex<T> { return Mutex(value) }
            fn both<T>(_ m: Mutex<T>, _ change: (inout T) -> Void) -> T {
                m.withLock { change(&$0) }
                return m.withLock { $0 }
            }
            fn once<T>(_ cell: OnceCell<T>, _ value: T) -> T { return cell.getOrInit { value } }
            """,
        """
            let m = guarded("start")
            let text = both(m, { $0 = $0 + "!" })
            let n = guarded(5)
            let more = both(n, { $0 += 1 })
            var failed = "-"
            do {
                try n.withLock { (v: inout Int) throws(Bad) -> Void in
                    v = 9
                    throw Bad { }
                }
            } catch {
                failed = "failed"
            }
            let cell = OnceCell<Big>()
            let a = once(cell, Big { a: "once", b: 1, c: "" })
            let b = once(cell, Big { a: "twice", b: 1, c: "" })
            let d = ConcurrentDictionary<String, Big>()
            let made = d.getOrInsert("k", with: { Big { a: "d", b: 1, c: "" } })
            let q = ConcurrentQueue<String>()
            q.enqueue("one")
            let first = q.dequeue() ?? "-"
            print("\(text) \(more) \(failed) \(a.a) \(b.a) \(made.a) \(d.count) \(first) \(q.dequeue() == none)")
        """
    );

    const string Reaching = """
        protocol Named {
            fn n() -> String
            var size: Int { get }
        }
        extension Int: Named {
            fn n() -> String { return "int" }
            var size: Int => 8
        }
        extension String: Named {
            fn n() -> String { return "string \(self)" }
            var size: Int => count
        }
        struct Pair<A> {
            let a: A
            fn first() -> A { return a }
        }
        extension Pair: Named where A: Named {
            fn n() -> String { return "pair of \(a.n())" }
            var size: Int => a.size
        }
        final class Cell<T> {
            var item: T
            init(_ item: T) { self.item = item }
            fn get() -> T { return item }
            fn maybe() -> T? { return item }
            fn times<U>(_ f: (T) -> U) -> U[>..] { return [1, 2].map { _i in f(item) }.toArray() }
        }
        extension Cell: Named where T: Named {
            fn n() -> String { return "cell of \(item.n())" }
            var size: Int => item.size
        }
        struct Box<T> {
            var item: T
            fn mapped<U>(_ f: (T) -> U) -> U[>..] { return [1, 2].map { _i in f(item) }.toArray() }
            fn nested(_ f: (T, Int) -> Int) -> Int { return [1, 2].map { i in [3, 4].map { j in f(item, i * j) }.toArray()[1] }.toArray()[1] }
        }
        fn chain<T: Named>(_ x: T?) -> String? { return x?.n() }
        fn property<T: Named>(_ x: T?) -> Int { return x?.size ?? 0 }
        fn field<T>(_ x: Pair<T>?) -> T? { return x?.a }
        fn call<T>(_ x: Cell<T>?) -> T? { return x?.get() }
        fn flat<T>(_ x: Cell<T>?) -> T? { return x?.maybe() }
        fn useNamed<T: Named>(_ x: T) -> String { return x.n() }
        fn viaPair<T: Named>(_ x: T) -> String { return useNamed(Pair { a: x }) }
        fn viaCell<T: Named>(_ x: T) -> String { return useNamed(Cell(Pair { a: x })) }
        fn isNamed<T>(_ x: T) -> Bool { return x is Named }
        fn isInt<T>(_ x: T) -> Bool { return x is Int }
        fn asInt<T>(_ x: T) -> Int { return x as? Int ?? -1 }
        """;

    [Fact]
    public void AMemberIsReachedThroughAnOptional() => Prints(
        "int string x 3 4 c d\nnone 0 none none none\n",
        Reaching,
        """
            print("\(chain(3 as Int?)!) \(chain("x" as String?)!) \(property("abc" as String?)) \(field(Pair { a: 4 } as Pair<Int>?)!) \(call(Cell("c") as Cell<String>?)!) \(flat(Cell("d") as Cell<String>?)!)")
            let i: Int? = none
            let p: Pair<String>? = none
            let c: Cell<String>? = none
            print("\(chain(i) ?? "none") \(property(i)) \(field(p) ?? "none") \(call(c) ?? "none") \(flat(c) ?? "none")")
        """
    );

    [Fact]
    public void AGenericTypeOfATypeParameterConforms() => Prints(
        "pair of int pair of string z cell of pair of int\n",
        Reaching,
        """
            print("\(viaPair(3)) \(viaPair("z")) \(viaCell(3))")
        """
    );

    [Fact]
    public void AValuesTypeIsTested() => Prints(
        "true false true false 3 -1 true\n",
        Reaching,
        """
            let boxed: any Any = 3
            print("\(isNamed(3)) \(isNamed([1])) \(isInt(3)) \(isInt("3")) \(asInt(3)) \(asInt("3")) \(isInt(boxed))")
        """
    );

    [Fact]
    public void AClosureCapturesSelfAndIsInsideAnother() => Prints(
        "b! b! c? c? 8\n",
        Reaching,
        """
            let box = Box { item: "b" }
            let mapped = box.mapped { $0 + "!" }
            let times = Cell("c").times { $0 + "?" }
            print("\(mapped[0]) \(mapped[1]) \(times[0]) \(times[1]) \(Box { item: 0 }.nested { $0 + $1 })")
        """
    );

    [Fact]
    public void RawMemoryIsCompiledOnce() {
        var once = Unspecialised(
            "fn at<T>(_ value: T) -> T {\n    unsafe {\n        let p = RawPtr<T>.allocate(count: 1)\n        p.initialize(to: value)\n        let moved = p.move()\n        p.deallocate()\n        return moved\n    }\n}",
            "    print(at(\"a\"))"
        );
        Assert.Contains("Test.at$generic", once);
    }
}
