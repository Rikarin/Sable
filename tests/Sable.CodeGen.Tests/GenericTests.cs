using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>Generic functions and types, monomorphised: native and interpreted alike.</summary>
public sealed class GenericTests {
    [Fact]
    public void GenericFunctionsTypesAndProtocolCalls() =>
        Assert.Equal(
            "42 seven fine ?\nsquare 16, rect 10, 13\ncba\n",
            AssertAgree(
                Program(
                    """
                    struct Box<T> {
                        var item: T
                        fn get() -> T { return item }
                        fn replaced(with value: T) -> Box<T> { return Box<T> { item: value } }
                    }

                    union Outcome<T, E> {
                        Ok(T)
                        Err(E)
                    }

                    fn unwrapOr<T, E>(_ outcome: Outcome<T, E>, _ fallback: T) -> T {
                        return match outcome {
                            case .Ok(let value) => value
                            case .Err(_) => fallback
                        }
                    }

                    protocol Shape {
                        fn area() -> Int
                        fn name() -> String
                    }

                    struct Square: Shape {
                        var side: Int
                        fn area() -> Int { return side * side }
                        fn name() -> String { return "square" }
                    }

                    struct Rect: Shape {
                        var w: Int
                        var h: Int
                        fn area() -> Int { return w * h }
                        fn name() -> String { return "rect" }
                    }

                    fn describe<S: Shape>(_ shape: S) -> String {
                        return shape.name() + " \(shape.area())"
                    }

                    fn total<S: Shape>(_ shapes: S[>..]) -> Int {
                        var sum = 0
                        for s in shapes {
                            sum = sum + s.area()
                        }
                        return sum
                    }

                    fn reversed<T>(_ items: T[>..]) -> T[>..] {
                        var result: T[>..] = []
                        var i = items.count - 1
                        while i >= 0 {
                            result.append(items[i])
                            i = i - 1
                        }
                        return result
                    }
                    """,
                    """
                        let b = Box<Int> { item: 7 }
                        let s = Box<String> { item: "seven" }
                        let ok: Outcome<String, Int> = Outcome.Ok("fine")
                        let bad: Outcome<String, Int> = Outcome.Err(3)
                        print("\(b.replaced(with: b.get() * 6).item) \(s.get()) \(unwrapOr(ok, "?")) \(unwrapOr(bad, "?"))")
                        let squares: Square[>..] = [Square { side: 2 }, Square { side: 3 }]
                        print(describe(Square { side: 4 }) + ", " + describe(Rect { w: 2, h: 5 }) + ", \(total(squares))")
                        let letters: String[>..] = ["a", "b", "c"]
                        var joined = ""
                        for l in reversed(letters) {
                            joined = joined + l
                        }
                        print(joined)
                    """
                )
            ).Stdout
        );

    [Fact]
    public void AnOperatorRequirementThroughACompositionAndAnAssociatedType() =>
        Assert.Equal(
            "3 10\n",
            AssertAgree(
                Program(
                    """
                    protocol Addable { operator + (a: Self, b: Self) -> Self }
                    protocol Zero { static fn zero() -> Self }

                    protocol Holder {
                        associatedtype Item: Addable & Zero
                        fn item() -> Item
                    }

                    struct N: Addable, Zero {
                        var v: Int
                        operator + (a: N, b: N) -> N => N(v: a.v + b.v)
                        static fn zero() -> N => N(v: 0)
                    }

                    struct H: Holder {
                        alias Item = N
                        fn item() -> N => N(v: 5)
                    }

                    fn two<T: Addable & Zero>(_ a: T) -> T => a + T.zero()
                    fn twice<C: Holder>(_ h: C) -> C.Item => h.item() + h.item()
                    """,
                    """
                        print("\(two(N(v: 3)).v) \(twice(H()).v)")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void AConstrainedExtensionAndAConditionalConformance() =>
        Assert.Equal(
            "box of box of cat\n42\n",
            AssertAgree(
                Program(
                    """
                    protocol Describable { fn describe() -> String }
                    struct Cat: Describable { fn describe() -> String => "cat" }

                    struct Box<T> { var v: T }

                    extension Box: Describable where T: Describable {
                        fn describe() -> String => "box of " + v.describe()
                    }

                    struct Wrapper<T> { var v: T }

                    extension Wrapper where T == Int {
                        fn doubled() -> Int => v * 2
                    }

                    fn show<D: Describable>(_ d: D) -> String => d.describe()
                    """,
                    """
                        print(show(Box(v: Box(v: Cat()))))
                        print("\(Wrapper(v: 21).doubled())")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void InitialiserSubscriptAndSettablePropertyRequirementsThroughAParameter() =>
        Assert.Equal(
            "s3 cell4 renamed+ renamed!+! 12\n",
            AssertAgree(
                Program(
                    """
                    protocol Makeable { init(seed: Int) }
                    protocol Named { var name: String { get set } }
                    protocol Grid { subscript(i: Int) -> Int { get set } }

                    struct Stored: Named, Makeable {
                        var name: String
                        init(seed: Int) { self.name = "s\(seed)" }
                    }

                    struct Computed: Named {
                        var inner: String
                        var name: String {
                            get => inner
                            set { inner = newValue + "!" }
                        }
                    }

                    class Cell: Makeable, Named {
                        var name: String
                        init(seed: Int) { self.name = "cell\(seed)" }
                    }

                    struct Cells: Grid {
                        var items: Int[>..]
                        subscript(i: Int) -> Int {
                            get => items[i]
                            set { items[i] = newValue }
                        }
                    }

                    fn mk<T: Makeable>(_ seed: Int) -> T => T(seed: seed)

                    fn rename<T: Named>(_ x: inout T) {
                        x.name = "renamed"
                        x.name += "+"
                    }

                    fn bump<G: Grid>(_ g: inout G) {
                        g[1] = g[1] + 10
                    }
                    """,
                    """
                        let a: Stored = mk(3)
                        let c: Cell = mk(4)
                        var s = a
                        rename(&s)
                        var k = Computed(inner: "x")
                        rename(&k)
                        var cs = Cells(items: [1, 2, 3])
                        bump(&cs)
                        print("\(a.name) \(c.name) \(s.name) \(k.name) \(cs[1])")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void GenericProtocolsAndVariantExistentials() =>
        Assert.Equal(
            "5 w2 4 4 none 3\n",
            AssertAgree(
                Program(
                    """
                    protocol Source<T> { fn next() -> T? }
                    protocol Counted<U>: Source<U> { fn count() -> Int }

                    struct Rep<E>: Counted<E> {
                        var value: E
                        fn next() -> E? => value
                        fn count() -> Int => 1
                    }

                    struct Words: Counted<String> {
                        fn next() -> String? => "w"
                        fn count() -> Int => 2
                    }

                    class Animal {
                        var legs: Int
                        init(legs: Int) { self.legs = legs }
                    }

                    class Dog: Animal {
                        init() { super.init(legs: 4) }
                    }

                    protocol Producer<out T> { fn next() -> T? }
                    protocol Consumer<in T> { fn accept(_ x: T) -> Int }

                    struct DogProducer: Producer<Dog> {
                        var empty: Bool
                        fn next() -> Dog? => empty ? none : Dog()
                    }

                    struct AnimalConsumer: Consumer<Animal> {
                        fn accept(_ x: Animal) -> Int => x.legs - 1
                    }

                    fn first<S: Source<Int>>(_ s: S) -> Int => s.next() ?? 0
                    fn firstOf(_ s: any Counted<String>) -> String => (s.next() ?? "") + "\(s.count())"

                    fn legsOf(_ p: any Producer<Animal>) -> String {
                        if let animal = p.next() {
                            return "\(animal.legs)"
                        }
                        return "none"
                    }
                    """,
                    """
                        let p: any Producer<Animal> = DogProducer(empty: false)
                        let c: any Consumer<Dog> = AnimalConsumer()
                        print("\(first(Rep(value: 5))) \(firstOf(Words())) \(legsOf(p)) \(legsOf(DogProducer(empty: false))) \(legsOf(DogProducer(empty: true))) \(c.accept(Dog()))")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void InferenceFromFunctionsLiteralsContextStaticsAndWitnesses() =>
        Assert.Equal(
            "2 7 1.5 3.5 1 4 3 3\n",
            AssertAgree(
                Program(
                    """
                    fn map<T, U>(_ xs: T[>..], _ f: (T) -> U) -> U[>..] {
                        var result: U[>..] = []
                        for x in xs {
                            result.append(f(x))
                        }
                        return result
                    }

                    fn dbl(_ x: Int) -> Int => x * 2

                    fn apply<T, E>(_ f: () throws(E) -> T) throws(E) -> T => try f()

                    fn pick<T>(_ a: T, _ b: T) -> T => b

                    fn wrap<T>(_ x: T) -> T[>..] => [x]

                    struct Box<T> {
                        var v: T
                        static fn make(_ x: T) -> Box<T> => Box(v: x)
                    }

                    protocol Bag {
                        associatedtype Item
                        fn get() -> Item
                    }

                    struct IntBag: Bag {
                        fn get() -> Int => 3
                    }

                    fn takesBag<B: Bag>(_ b: B) -> B.Item => b.get()
                    """,
                    """
                        let xs: Int[>..] = [1, 2]
                        let small: I8[>..] = wrap(1)
                        let n: Int? = pick(5, none)
                        print("\(map(xs, dbl)[0]) \(apply { 7 }) \(pick(1, 1.5)) \(1 + 2.5) \(small[0]) \(Box.make(4).v) \(takesBag(IntBag())) \(n == none ? 3 : 0)")
                    """
                )
            ).Stdout
        );

    [Fact]
    public void SelfInAGenericTypeIsTheTypeWithItsOwnParameters() =>
        Assert.Equal(
            "3\n3\n",
            AssertAgree(
                Program(
                    """
                    struct Nest<T> {
                        var inner: T
                        fn wrap() -> Nest<Nest<T>> { return Nest<Nest<T>> { inner: self } }
                        fn me() -> Self { return self }
                        fn same(_ other: Nest<T>) -> Nest<T> { return other }
                    }
                    """,
                    """
                        let n = Nest<Int> { inner: 3 }
                        print(n.wrap().inner.inner)
                        print(n.me().same(n).inner)
                    """
                )
            ).Stdout
        );

    /// <summary>
    ///     Polymorphic recursion, which no set of copies can compile: the
    ///     copy that would ask for an endless series calls the one instance
    ///     that serves every type instead, boxing its arguments and unboxing
    ///     its result — with the witnesses of what a type parameter is
    ///     constrained to (#105).
    /// </summary>
    [Fact]
    public void PolymorphicRecursionRunsThroughOneInstanceForEveryType() =>
        Assert.Equal(
            "3\n5\n1\n2\n3\nw\n4\n32\n",
            AssertAgree(
                Program(
                    """
                    fn deep<T>(_ x: T, _ n: Int) -> Int {
                        if n == 0 {
                            return 0
                        }
                        return 1 + deep((x, x), n - 1)
                    }

                    fn wrap<T>(_ x: T, _ n: Int) -> T {
                        if n == 0 {
                            return x
                        }
                        let y = wrap((x, n), n - 1)
                        print(y.1)
                        return y.0
                    }

                    fn count<T>(_ xs: T, _ n: Int) -> Int {
                        if n == 0 {
                            return 0
                        }
                        return 1 + count([xs, xs], n - 1)
                    }

                    protocol Shape { fn area() -> Int }

                    struct Sq: Shape {
                        var side: Int
                        fn area() -> Int { return side * side }
                    }

                    struct Two<A: Shape>: Shape {
                        var a: A
                        var b: A
                        fn area() -> Int { return a.area() + b.area() }
                    }

                    fn total<T: Shape>(_ x: T, _ n: Int) -> Int {
                        if n == 0 {
                            return x.area()
                        }
                        return total(Two(a: x, b: x), n - 1)
                    }
                    """,
                    """
                        print(deep(1, 3))
                        print(deep("s", 5))
                        print(wrap("w", 3))
                        print(count(1, 4))
                        print(total(Sq(side: 2), 3))
                    """
                )
            ).Stdout
        );

    /// <summary>
    ///     A value generic parameter: the number each instance is given, read
    ///     in the body, sizing an array, passed on to another generic type,
    ///     and inferred from an argument's type (#158).
    /// </summary>
    [Fact]
    public void AValueGenericParameterIsTheNumberEachInstanceIsGiven() =>
        Assert.Equal(
            "8\n6\nb\n3\n3\n",
            AssertAgree(
                Program(
                    """
                    struct Buf<T, N: Int> where N > 0, N <= 64 {
                        var items: T[N]
                        fn size() -> Int { return N * 2 }
                    }

                    struct Pair<N: Int> {
                        var inner: Buf<String, N>
                        fn total() -> Int { return inner.size() + N }
                    }

                    fn count<T, N: Int>(_ b: Buf<T, N>) -> Int { return N }
                    """,
                    """
                        let b = Buf<Int, 4> { items: [1, 2, 3, 4] }
                        print(b.size())
                        let p = Pair<2> { inner: Buf<String, 2> { items: ["a", "b"] } }
                        print(p.total())
                        print(p.inner.items[1])
                        let c = Buf<Int, 3> { items: [7, 8, 9] }
                        print(count(c))
                        print(c.items.count)
                    """
                )
            ).Stdout
        );

    /// <summary>
    ///     A protocol extension's methods that return, take or compute with an
    ///     associated type, specialised for the conformer: Item is Int for
    ///     Pair and T for Wrap&lt;T&gt;, whose own members need T known — also
    ///     through a refining protocol and a generic caller (#233).
    /// </summary>
    [Fact]
    public void AProtocolExtensionComputesWithAnAssociatedType() =>
        Assert.Equal(
            "2\ntrue\nfalse\n2\n2\npair: 2\nz\n9\n2\n",
            AssertAgree(
                Program(
                    """
                    protocol Container {
                        associatedtype Item: Comparable
                        fn first() -> Item
                        fn second() -> Item
                    }
                    protocol Named: Container {
                        fn label() -> String
                    }
                    fn larger<T: Comparable>(_ a: T, _ b: T) -> T { return a < b ? b : a }
                    extension Container {
                        fn bigger() -> Item {
                            let a = first()
                            let b = second()
                            return a < b ? b : a
                        }
                        fn holds(_ x: Item) -> Bool { return x == first() || x == second() }
                        fn both() -> Item[] { return [first(), second()] }
                        fn viaGeneric() -> Item { return larger(first(), second()) }
                    }
                    extension Named {
                        fn describe() -> String { return "\(label()): \(bigger())" }
                    }
                    struct Pair: Named {
                        fn first() -> Int { return 1 }
                        fn second() -> Int { return 2 }
                        fn label() -> String { return "pair" }
                    }
                    struct Wrap<T: Comparable>: Container {
                        alias Item = T
                        var a: T
                        var b: T
                        fn first() -> T { return a }
                        fn second() -> T { return b }
                    }
                    fn top<C: Container>(_ c: C) -> C.Item { return c.bigger() }
                    """,
                    """
                        print(Pair().bigger())
                        print(Pair().holds(2))
                        print(Pair().holds(3))
                        print(Pair().both().count)
                        print(Pair().viaGeneric())
                        print(Pair().describe())
                        print(Wrap<String>(a: "z", b: "a").bigger())
                        print(top(Wrap<Int>(a: 9, b: 3)))
                        print(top(Pair()))
                    """
                )
            ).Stdout
        );

    const string PolymorphicDeclarations = """
        protocol Shape {
            fn area() -> Int
            fn name() -> String
        }

        struct Square: Shape {
            var side: Int
            fn area() -> Int { return side * side }
            fn name() -> String { return "square" }
        }

        class Circle: Shape {
            var r: Int
            init(_ r: Int) { self.r = r }
            fn area() -> Int { return 3 * r * r }
            fn name() -> String { return "circle" }
        }

        @polymorphic
        fn describe<S: Shape>(_ s: S, _ times: Int) -> String {
            var total = 0
            for i in 0..<times {
                total += s.area()
            }
            return "\(s.name()) \(total)"
        }

        @polymorphic
        fn larger<A: Shape, B: Shape>(_ a: A, _ b: B) -> A {
            if a.area() >= b.area() {
                return a
            }
            return a
        }
        """;

    /// <summary>
    ///     '@polymorphic' compiles a generic function once: each value of a
    ///     type parameter passed in a box with its witness table, a result
    ///     taken back out as the type the call gives it, whatever the types.
    /// </summary>
    [Fact]
    public void APolymorphicFunctionIsCompiledOnce() {
        var source = Program(PolymorphicDeclarations, """
            print(describe(Square(side: 2), 3))
            print(describe(Circle(2), 1))
            let s: any Shape = Square(side: 5)
            print(describe(s, 2))
            let big = larger(Square(side: 3), Circle(1))
            print(big.side)
        """);
        Assert.Equal("square 12\ncircle 12\nsquare 50\n3\n", AssertAgree(source).Stdout);
        var sil = SilPrinter.Print(Pipeline.Compile(new SourceText(source, "test.sb")).Sil!);
        Assert.Contains("sil @Test.describe$polymorphic : $(any$Test.Shape, Int) -> String", sil, StringComparison.Ordinal);
        Assert.Contains("sil @Test.larger$polymorphic : $(any$Test.Shape, any$Test.Shape) -> any$Test.Shape", sil, StringComparison.Ordinal);
        Assert.DoesNotContain("sil @Test.describe$of$", sil, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An initialiser with type parameters of its own is specialised for
    ///     each set of them, and infers its type's parameters through its
    ///     'where' clause: 'Groups(grouping:by:)' makes V an array of E.
    /// </summary>
    [Fact]
    public void AnInitializerWithItsOwnTypeParameters() =>
        Assert.Equal(
            "2\n3 1\n2 0\n[3, 1] [2]\n",
            AssertAgree(
                Program(
                    """
                    struct Counter {
                        var count: Int
                        init<Element>(counting values: Element[:]) { count = values.count }
                    }

                    struct Bag<T> {
                        var items: T[>..] = []
                        init<S: Sequence>(from s: S) where S.Element == T, S.Failure == Never {
                            for x in s { items.append(x) }
                        }
                    }

                    struct Groups<K: Hashable, V> {
                        var table: [K: V] = [:]
                        init<E>(grouping values: E[:], by key: (E) -> K) where V == E[>..] {
                            for v in values {
                                table[key(v), default: []].append(v)
                            }
                        }
                    }
                    """,
                    """
                        let words: String[>..] = ["a", "b", "c"]
                        print(Counter(counting: words[0..<2]).count)
                        let b = Bag(from: 1...3)
                        let s = Bag<String>(from: words[2..<3])
                        print("\(b.items.count) \(s.items.count)")
                        let numbers: Int[>..] = [3, 2, 1]
                        let g = Groups(grouping: numbers[0..<3], by: { (n: Int) -> Int in n % 2 })
                        print("\(g.table.count) \(g.table.map({ entry in entry.0 })[1])")
                        print("\(g.table[1]!) \(g.table[0]!)")
                    """
                )
            ).Stdout
        );

    /// <summary>
    ///     'T?' meets a constraint where its T does, through Sable.Core's
    ///     conditional conformances of Optional: generic code given one
    ///     calls the Optional extension's '==', '<', 'hash' and
    ///     'debugDescription', and so does an 'any Debug' holding one.
    /// </summary>
    [Fact]
    public void AnOptionalMeetsAConstraintWhereWhatItHoldsDoes() =>
        Assert.Equal(
            "false true\nnone \"a\"\ntrue true false\nnothing 2\nnone13\n3 none\n",
            AssertAgree(
                Program(
                    """
                    fn same<T: Equatable>(_ a: T, _ b: T) -> Bool { return a == b }
                    fn smaller<T: Comparable>(_ a: T, _ b: T) -> T { return a < b ? a : b }
                    fn describe<T: Debug>(_ a: T) -> String { return a.debugDescription() }
                    fn hashOf<T: Hashable>(_ a: T) -> Int { return a.hashValue }
                    """,
                    """
                        let x: Int? = 3
                        let n: Int? = none
                        let s: String? = "a"
                        print("\(same(x, n)) \(same(n, n))")
                        print("\(describe(n)) \(describe(s))")
                        let three: Int? = 3
                        print("\(smaller(x, n) == none) \(hashOf(x) == hashOf(three)) \(hashOf(x) == hashOf(n))")
                        var d: [Int?: String] = [:]
                        d[x] = "three"
                        d[n] = "nothing"
                        print("\(d[n] ?? "?") \(d.count)")
                        let xs: Int?[>..] = [3, none, 1]
                        let sorted = xs.sorted()
                        print(describe(sorted[0]) + describe(sorted[1]) + describe(sorted[2]))
                        let boxed: (any Debug)[>..] = [x, n]
                        print("\(boxed[0].debugDescription()) \(boxed[1].debugDescription())")
                    """
                )
            ).Stdout
        );

    /// <summary>
    ///     A built-in type conforming to a program's protocol by an empty
    ///     extension: its witnesses are what the conformance checker found —
    ///     the built-in operators, and the numeric protocols' builtin families,
    ///     generically and through an existential.
    /// </summary>
    [Fact]
    public void ABuiltInTypesOperatorsMeetAProgramsProtocol() =>
        Assert.Equal(
            "3\n3.75\n7\n3 a\n-1\n3\n",
            AssertAgree(
                Program(
                    """
                    protocol Summable {
                        operator + (lhs: Self, rhs: Self) -> Self
                    }

                    protocol Ordered {
                        operator < (lhs: Self, rhs: Self) -> Bool
                        operator == (lhs: Self, rhs: Self) -> Bool
                    }

                    protocol Signed {
                        fn signum() -> Self
                    }

                    protocol Wordy {
                        var trailingZeroCount: Int { get }
                    }

                    extension Int: Summable {}
                    extension F64: Summable {}
                    extension U8: Summable {}
                    extension Int: Ordered {}
                    extension String: Ordered {}
                    extension Int: Signed {}
                    extension Int: Wordy {}

                    fn total<T: Summable>(_ a: T, _ b: T) -> T { return a + b }

                    fn least<T: Ordered>(_ a: T, _ b: T) -> T {
                        if a == b {
                            return a
                        }
                        return a < b ? a : b
                    }

                    fn sign<T: Signed>(_ a: T) -> T { return a.signum() }
                    """,
                    """
                        print(total(1, 2))
                        print(total(1.5, 2.25))
                        print(total(U8(3), U8(4)))
                        print("\(least(5, 3)) \(least("b", "a"))")
                        print(sign(-5))
                        let w: any Wordy = 8
                        print(w.trailingZeroCount)
                    """
                )
            ).Stdout
        );

    /// <summary>
    ///     A generic type's parameters, inferred where its initialiser's
    ///     argument types do not name them: from a 'where S.Element == (K, V)'
    ///     once S is known, and from what a closure gives back (#296).
    /// </summary>
    [Fact]
    public void AnInitialiserInfersItsTypeFromRequirementsAndClosures() =>
        Assert.Equal(
            "2 b 1\nz\nlate 3\n",
            AssertAgree(
                Program(
                    """
                    struct Pairs<K, V> {
                        var keys: K[>..] = []
                        var values: V[>..] = []
                        init<S: Sequence>(_ pairs: S) throws(S.Failure) where S.Element == (K, V) {
                            for try (k, v) in pairs {
                                keys.append(k)
                                values.append(v)
                            }
                        }
                    }

                    struct Made<T> {
                        var item: T
                        init(each make: () -> T) {
                            self.item = make()
                        }
                    }

                    struct Fail: Error { let at: Int }

                    struct Late<T> {
                        var item: T
                        init(each make: () throws(Fail) -> T) rethrows {
                            self.item = try make()
                        }
                    }
                    """,
                    """
                        let pairs = Pairs([("b", 1), ("a", 2)])
                        print("\(pairs.keys.count) \(pairs.keys[0]) \(pairs.values[0])")
                        let made = Made(each: { "z" })
                        print(made.item)
                        let late = Late(each: { 3 })
                        print("late \(late.item)")
                    """
                )
            ).Stdout
        );

    /// <summary>
    ///     'as?', 'as!' and 'is' from a type parameter to an array or another
    ///     value type: decided in each instance, as S is known there (#296).
    /// </summary>
    [Fact]
    public void ACastFromATypeParameterIsDecidedInEachInstance() =>
        Assert.Equal(
            "some 3\nnone\ntrue false\n4\n",
            AssertAgree(
                Program(
                    """
                    fn asArray<S: Sequence>(_ s: S) -> Int[>..]? where S.Element == Int {
                        return s as? Int[>..]
                    }

                    fn isArray<S: Sequence>(_ s: S) -> Bool where S.Element == Int {
                        return s is Int[>..]
                    }

                    fn forced<T>(_ t: T) -> Int {
                        return t as! Int
                    }
                    """,
                    """
                        let xs: Int[>..] = [1, 2, 3]
                        if let ys = asArray(xs) {
                            print("some \(ys.count)")
                        }
                        if asArray(0..<3) == none {
                            print("none")
                        }
                        print("\(isArray(xs)) \(isArray(0..<3))")
                        print(forced(4))
                    """
                )
            ).Stdout
        );

    [Fact]
    public void AGenericsParametersComeFromTheContext() =>
        Assert.Equal(
            "1.0\n3.0\n3.0\n3.0\n6.0\n6.0\n3.0\n<1.0, 1.0, 1.0, 1.0>\n-1.0\n1.0\ntrue\n",
            AssertAgree(
                Program(
                    """
                    struct Cx<T: FloatingPoint> {
                        var re: T
                        var im: T
                        init(_ re: T, _ im: T) { self.re = re; self.im = im }
                        static var i: Cx<T> => Cx(T.zero, T.one)
                        static fn make() -> Cx<T> { return Cx(T.one, T.one) }
                        static operator + (a: Cx<T>, b: Cx<T>) -> Cx<T> { return Cx(a.re + b.re, a.im + b.im) }
                        static operator * (a: Cx<T>, b: Cx<T>) -> Cx<T> { return Cx(a.re * b.re - a.im * b.im, a.re * b.im + a.im * b.re) }
                        static operator == (a: Cx<T>, b: Cx<T>) -> Bool { return a.re == b.re && a.im == b.im }
                    }

                    fn two<T: Numeric>() -> T { return T.one + T.one }
                    """,
                    """
                        let a: Cx<F64> = Cx.i
                        print(a.im)
                        let z: Cx<F64> = Cx(1.0, 2.0) + Cx.i
                        print(z.im)
                        let w = Cx(1.0, 2.0) + Cx.i
                        print(w.im)
                        let q = Cx.i + Cx(1.0, 2.0)
                        print(q.im)
                        let x: F64 = 3
                        print(x * two())
                        print(two() * x)
                        let y: F64 = two() + 1
                        print(y)
                        let v: SIMD<F32, 4> = SIMD(repeating: 1)
                        print(v)
                        let m: Cx<F32> = Cx.make() * Cx.i
                        print(m.re)
                        let k: Cx<F32> = .i
                        print(k.im)
                        print(Cx.i == Cx(0.0, 1.0))
                    """
                )
            ).Stdout
        );

    [Fact]
    [Native]
    public void ADefaultNamesTheGenericParametersItsCallGives() =>
        Assert.Equal(
            "0\n4\n1\n0\n2\n",
            AssertAgree(
                Program(
                    """
                    struct Q<E> {
                        var items: E[>..] = []
                        init() { }
                    }

                    struct S<E, N: Int> {
                        var q: Q<E> = Q<E>()
                        var size: Int = N
                        var n: Int = 0
                    }

                    fn count<E>(_ x: E, _ q: Q<E> = Q<E>()) -> Int { return q.items.count }

                    fn wrapped<T>(_ x: T) -> Int {
                        var s = S<T, 2>()
                        s.q.items.append(x)
                        s.q.items.append(x)
                        return s.q.items.count
                    }
                    """,
                    """
                        let s = S<String, 4>(n: 1)
                        print(s.q.items.count)
                        print(s.size)
                        print(s.n)
                        print(count(1.5))
                        print(wrapped("a"))
                    """
                )
            ).Stdout
        );

    /// <summary>
    ///     A generic function's closure that captures an object of a generic
    ///     class with a deinit, over its type parameter's associated types —
    ///     'Box<S.Element>' — whose deinit instance is the one for the
    ///     function's instance, once S is known.
    /// </summary>
    [Fact]
    public void AClosureCapturesAGenericObjectOverAnAssociatedType() {
        var run = AssertAgree(Program(
            """
            final class Box<T> {
                let value: T

                init(_ value: T) {
                    self.value = value
                }

                deinit {
                    print("box gone")
                }
            }

            fn firstBoxed<S: Sequence>(_ values: S) -> () -> Int where S.Failure == Never, S.Element == Int {
                var iterator = values.makeIterator()
                let box = Box<S.Element>(iterator.next() ?? 0)
                return { box.value }
            }
            """,
            """
                let first = firstBoxed([7, 8])
                print(first())
            """
        ));
        Assert.Equal("7\nbox gone\n", run.Stdout);
    }
}
