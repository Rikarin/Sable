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
}
