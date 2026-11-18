using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>any P</c>: the value boxed, with a closure per requirement that
///     calls the concrete type's member — through the vtable for a class a
///     subclass may override — or the protocol's default. Native and
///     interpreted, leak-checked.
/// </summary>
public sealed class ExistentialTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    [Fact]
    public void ExistentialsOfStructsAndClasses() =>
        Prints(
            "circle 12.56: a circle\nsquare 4.0: a square\nlabel 0.0: tagged label\ntotal 16.560000000000002\nwolf says howl\npup says yip\n",
            """
            protocol Shape {
                var name: String { get }
                fn area() -> F64

                fn summary() -> String {
                    return "a " + name
                }
            }

            struct Circle: Shape {
                var radius: F64
                var name: String
                fn area() -> F64 { return 3.14 * radius * radius }
            }

            struct Square: Shape {
                var side: F64
                var name: String
                fn area() -> F64 { return side * side }
            }

            class Label: Shape {
                var name: String
                init(_ name: String) {
                    self.name = name
                }
                fn area() -> F64 { return 0.0 }
                fn summary() -> String { return "tagged " + name }
            }

            protocol Speaker {
                fn speak() -> String
            }

            open class Dog: Speaker {
                var name: String
                init(_ name: String) {
                    self.name = name
                }
                fn speak() -> String { return name + " says " + sound() }
                open fn sound() -> String { return "howl" }
            }

            class Puppy: Dog {
                init() {
                    super.init("pup")
                }
                override fn sound() -> String { return "yip" }
            }

            fn report(_ shape: any Shape) -> String {
                return shape.name + " \(shape.area()): " + shape.summary()
            }
            """,
            """
                let shapes: (any Shape)[>..] = [Circle { radius: 2.0, name: "circle" }, Square { side: 2.0, name: "square" }, Label("label")]
                var total = 0.0
                for shape in shapes {
                    print(report(shape))
                    total = total + shape.area()
                }
                print("total \(total)")
                let speakers: (any Speaker)[>..] = [Dog("wolf"), Puppy()]
                for s in speakers {
                    print(s.speak())
                }
            """
        );

    /// <summary>
    ///     A value of a generic type, boxed: the slots call the methods'
    ///     instances for its type arguments — a struct's, a generic class's,
    ///     and one inherited by a subclass that fixes them (#232).
    /// </summary>
    [Fact]
    public void ExistentialsOfGenericTypes() =>
        Prints(
            "1\nG of s 2\nbase 3 10\nbase d 20\nG of 1 5\nbase true 10\n",
            """
            protocol P {
                fn get() -> Int
                fn name() -> String
            }
            struct G<T>: P {
                var item: T
                var n: Int
                fn get() -> Int { return n }
                fn name() -> String { return "G of \(item)" }
            }
            class Base<T>: P {
                var v: T
                init(v: T) { self.v = v }
                open fn get() -> Int { return 10 }
                fn name() -> String { return "base \(v)" }
            }
            class Derived: Base<String> {
                init() { super.init(v: "d") }
                override fn get() -> Int { return 20 }
            }
            fn show(_ p: any P) { print("\(p.name()) \(p.get())") }
            """,
            """
                let x: any P = G<Int>(item: 4, n: 1)
                print(x.get())
                show(G<String>(item: "s", n: 2))
                show(Base<Int>(v: 3))
                show(Derived())
                let xs: (any P)[>..] = [G<Int>(item: 1, n: 5), Base<Bool>(v: true)]
                for p in xs { show(p) }
            """
        );

    [Fact]
    public void AValueBecomesAnExistentialThroughAsAndInsideAnOptional() =>
        Prints(
            "4\n9\nnone\n",
            """
            protocol Shape {
                fn area() -> Int
            }

            struct Square: Shape {
                var side: Int
                fn area() -> Int { return side * side }
            }

            fn areaOf(_ shape: (any Shape)?) -> String {
                if let s = shape {
                    return "\(s.area())"
                }
                return "none"
            }
            """,
            """
                let shape = Square { side: 2 } as any Shape
                print(shape.area())
                let maybe: (any Shape)? = Square { side: 3 }
                print(areaOf(maybe))
                print(areaOf(none))
            """
        );

    /// <summary>
    ///     docs/03 §3.2: a protocol extension's member that is not a
    ///     requirement is chosen by the static type. Through 'any P' and
    ///     through 'T: P' that is the extension's, even where the concrete type
    ///     declares one of the same name; a requirement is still the type's own.
    /// </summary>
    [Fact]
    public void AnExtensionMemberThatIsNoRequirementIsChosenByTheStaticType() =>
        Assert.Equal(
            "10 10 20\n10 10 20\n1 1 5 5\n11\n",
            AssertAgree(
                Program(
                    """
                    protocol P { fn f() -> Int }

                    extension P {
                        fn g() -> Int { return 10 }
                        fn f() -> Int { return 5 }
                        var k: Int { get => 10 }
                        fn h() -> Int { return g() + f() }
                    }

                    struct B: P {
                        fn f() -> Int { return 1 }
                        fn g() -> Int { return 20 }
                        var k: Int { get => 20 }
                    }

                    struct C: P { }

                    fn existentialG(_ x: any P) -> Int { return x.g() }
                    fn existentialF(_ x: any P) -> Int { return x.f() }
                    fn existentialK(_ x: any P) -> Int { return x.k }
                    fn genericG<T: P>(_ x: T) -> Int { return x.g() }
                    fn genericF<T: P>(_ x: T) -> Int { return x.f() }
                    fn genericK<T: P>(_ x: T) -> Int { return x.k }
                    """,
                    """
                        print("\(existentialG(B())) \(genericG(B())) \(B().g())")
                        print("\(existentialK(B())) \(genericK(B())) \(B().k)")
                        print("\(existentialF(B())) \(genericF(B())) \(existentialF(C())) \(genericF(C()))")
                        print(B().h())
                    """
                )
            ).Stdout
        );

    /// <summary>'any A &amp; B' has the slots of both, and converts to either.</summary>
    [Fact]
    public void ACompositionHasEveryPartsRequirements() =>
        Prints(
            "sq 4\n16\nsq\n",
            """
            protocol Named { fn name() -> String }
            protocol Shape { fn area() -> Int }

            struct Sq: Shape, Named {
                var side: Int
                fn area() -> Int { return side * side }
                fn name() -> String { return "sq" }
            }

            fn both(_ x: any Shape & Named) -> String { return "\(x.name()) \(x.area())" }
            fn justShape(_ x: any Shape & Named) -> any Shape { return x }
            fn justNamed(_ x: any Shape & Named) -> any Named { return x }
            """,
            """
                print(both(Sq(side: 2)))
                print(justShape(Sq(side: 4)).area())
                print(justNamed(Sq(side: 1)).name())
            """
        );

    /// <summary>
    ///     A property stored, and a mutating method called, through an
    ///     existential: a copy made before keeps the value it had, a class's
    ///     instance stays shared.
    /// </summary>
    [Fact]
    public void AnExistentialIsChangedThroughItsSlotsAndCopiedOnWrite() =>
        Prints(
            "6\n6\n11\n23\n5\n",
            """
            protocol Sized {
                var size: Int { get set }
                mutating fn grow(by n: Int)
            }

            struct Box: Sized {
                var size: Int
                mutating fn grow(by n: Int) { size += n }
            }

            struct Temp: Sized {
                var c: Int
                var size: Int {
                    get => c
                    set { c = newValue * 10 }
                }
                mutating fn grow(by n: Int) { c += n }
            }

            class Counter: Sized {
                var size: Int = 0
                init() {}
                fn grow(by n: Int) { size += n }
            }
            """,
            """
                var b: any Sized = Box(size: 1)
                b.size = 5
                b.size += 1
                print(b.size)
                let b2 = b
                b.size = 9
                b.grow(by: 2)
                print(b2.size)
                print(b.size)
                var t: any Sized = Temp(c: 1)
                t.size = 2
                t.grow(by: 3)
                print(t.size)
                var k: any Sized = Counter()
                let k2 = k
                k.size = 4
                k.grow(by: 1)
                print(k2.size)
            """
        );

    /// <summary>
    ///     'as?', 'as!', 'is' and a type pattern on an existential: to a
    ///     concrete type, a class through its superclass's box, and another
    ///     protocol, which the box's type may or may not conform to.
    /// </summary>
    [Fact]
    public void AnExistentialIsCastAndTestedAsTheProgramRuns() =>
        Prints(
            "sq\nnot named\ntrue\nfalse\ntrue\n3\n9\nfalse\ntrue\ndog has area 7\nan animal\nsq has area 4\ntrue\ntrue\n7\nmatched dog\n",
            """
            protocol Named { fn name() -> String }
            protocol Shape { fn area() -> Int }

            struct Sq: Shape, Named {
                var side: Int
                fn area() -> Int { return side * side }
                fn name() -> String { return "sq" }
            }

            struct Circle: Shape {
                var r: Int
                fn area() -> Int { return 3 * r * r }
            }

            class Animal: Named {
                init() {}
                fn name() -> String { return "animal" }
            }

            class Dog: Animal, Shape {
                override init() {}
                override fn name() -> String { return "dog" }
                fn area() -> Int { return 7 }
            }

            fn describe(_ n: any Named) -> String {
                if let s = n as? any Shape & Named { return "\(s.name()) has area \(s.area())" }
                if let d = n as? Dog { return "a dog \(d.area())" }
                if n is Animal { return "an animal" }
                return "just \(n.name())"
            }
            """,
            """
                let s: any Shape = Sq(side: 3)
                let c: any Shape = Circle(r: 1)
                if let n = s as? any Named { print(n.name()) }
                if let n = c as? any Named { print(n.name()) } else { print("not named") }
                print(s is Sq)
                print(c is Sq)
                print(s is any Named)
                if let q = s as? Sq { print(q.side) }
                let q2 = s as! Sq
                print(q2.area())
                let circle = Circle(r: 2)
                print(circle is Named)
                print(circle is Shape)
                let a: Animal = Dog()
                print(describe(a))
                print(describe(Animal()))
                print(describe(Sq(side: 2)))
                let n: any Named = a
                print(n is Dog)
                print(n is Shape)
                if let shaped = n as? any Shape { print(shaped.area()) }
                match n {
                    case let d as Dog => print("matched \(d.name())")
                    case _ => print("no")
                }
            """
        );
}

/// <summary>Computed properties: a getter lowered as a method, read directly, generically and through existentials.</summary>
public sealed class ComputedPropertyTests {
    [Fact]
    public void GettersAreReadEveryWay() =>
        Assert.Equal(
            "6\n12\nbox of 3\ncircle: round circle\nsquare: plain square\n",
            AssertAgree(
                Program(
                    """
                    protocol Named {
                        var name: String { get }
                        fn label() -> String {
                            return "plain " + name
                        }
                    }

                    struct Rect {
                        var w: Int
                        var h: Int
                        var area: Int { get => w * h }
                    }

                    class Counter {
                        var count: Int
                        init(_ count: Int) {
                            self.count = count
                        }
                        var doubled: Int {
                            get {
                                return count * 2
                            }
                        }
                    }

                    struct Box<T> {
                        var items: T[>..]
                        var summary: String { get => "box of \(items.count)" }
                    }

                    struct Circle: Named {
                        var name: String { get => "circle" }
                        fn label() -> String { return "round " + name }
                    }

                    struct Square: Named {
                        var name: String { get => "square" }
                    }
                    """,
                    """
                        print(Rect { w: 2, h: 3 }.area)
                        print(Counter(6).doubled)
                        print(Box<Int> { items: [1, 2, 3] }.summary)
                        let named: (any Named)[>..] = [Circle {}, Square {}]
                        for n in named {
                            print(n.name + ": " + n.label())
                        }
                    """
                )
            ).Stdout
        );

    /// <summary>A property the protocol computes by default, read through an existential of a type that leaves it alone.</summary>
    [Fact]
    public void ADefaultGetterIsReadThroughAnExistential() =>
        Assert.Equal(
            "Square of 2: 4\nround\n",
            AssertAgree(
                Program(
                    """
                    protocol Shape {
                        var area: Int { get }
                        var summary: String { get => "\(Reflect.typeName(self)) of \(area)" }
                    }

                    struct Square: Shape {
                        var side: Int
                        var area: Int { get => side * side }
                        var summary: String { get => "Square of \(side): \(area)" }
                    }

                    struct Circle: Shape {
                        var area: Int { get => 3 }
                    }
                    """,
                    """
                        let shapes: (any Shape)[>..] = [Square { side: 2 }, Circle {}]
                        print(shapes[0].summary)
                        print(shapes[1].summary == "Circle of 3" ? "round" : shapes[1].summary)
                    """
                )
            ).Stdout
        );
}
