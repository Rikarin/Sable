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
            "circle 12.56: a circle\nsquare 4: a square\nlabel 0: tagged label\ntotal 16.560000000000002\nwolf says howl\npup says yip\n",
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
