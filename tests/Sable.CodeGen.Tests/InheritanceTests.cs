using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Class inheritance: fields laid out after the superclass's, methods
///     found through the vtable, initialisers split so that 'super.init'
///     initialises the object a subclass allocated, and deinits run from the
///     subclass up. Native and interpreted, leak-checked.
/// </summary>
[Native]
public sealed class InheritanceTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    const string Creatures = """
                             open class Creature {
                                 var name: String
                                 var health: Int

                                 init(name: String, health: Int) {
                                     self.name = name
                                     self.health = health
                                 }

                                 fn describe() -> String {
                                     return name + " (\(health))"
                                 }

                                 open fn attackPower() -> Int {
                                     return 1
                                 }

                                 fn hit(_ other: Creature) {
                                     other.health = other.health - attackPower()
                                 }

                                 deinit {
                                     print("creature " + name + " gone")
                                 }
                             }

                             class Goblin: Creature {
                                 var rage: Int

                                 init() {
                                     super.init(name: "goblin", health: 8)
                                     self.rage = 2
                                 }

                                 override fn attackPower() -> Int {
                                     return 2 + rage
                                 }

                                 deinit {
                                     print("goblin gone")
                                 }
                             }

                             class Hero: Creature {
                                 var potions: Int

                                 init(potions: Int) {
                                     super.init(name: "hero", health: 20)
                                     self.potions = potions
                                 }

                                 override fn attackPower() -> Int {
                                     return super.attackPower() + 2
                                 }

                                 override fn describe() -> String {
                                     return "the " + super.describe() + " with \(potions) potions"
                                 }
                             }
                             """;

    [Fact]
    public void OverridesAreFoundThroughTheBaseClass() =>
        Prints(
            "goblin (8)\nthe hero (20) with 2 potions\n4\n3\nhero hits: goblin (5)\ngoblin hits: the hero (16) with 2 potions\ncreature hero gone\ngoblin gone\ncreature goblin gone\n",
            Creatures,
            """
                let goblin = Goblin()
                let hero = Hero(potions: 2)
                print(goblin.describe())
                print(hero.describe())
                print(goblin.attackPower())
                print(hero.attackPower())
                hero.hit(goblin)
                print("hero hits: " + goblin.describe())
                goblin.hit(hero)
                print("goblin hits: " + hero.describe())
            """
        );

    [Fact]
    public void SubclassesInAnArrayOfTheBaseAndTheirDeinitsInOrder() =>
        Prints(
            "goblin (8): 4\nthe hero (20) with 1 potions: 3\ncreature rat (1): 1\ntotal 8\ngoblin gone\ncreature goblin gone\ncreature hero gone\ncreature rat gone\n",
            Creatures,
            """
                var all: Creature[>..] = [Goblin(), Hero(potions: 1)]
                all.append(Creature(name: "rat", health: 1))
                var total = 0
                for c in all {
                    let label = c.name == "rat" ? "creature " : ""
                    print(label + c.describe() + ": \(c.attackPower())")
                    total = total + c.attackPower()
                }
                print("total \(total)")
                all.clear()
            """
        );

    [Fact]
    public void ASubclassIsMadeWithTheInitialisersItInherits() =>
        Prints(
            "1 2 s\n1 2\nsq 4 4\nr 0\nblank 4\n9 1\n6\n",
            """
            open class A {
                var a: Int = 1
                var s: String = "s"
            }

            open class B: A {
                var b: Int = 2
            }

            class C: B { }

            open class Shape {
                var name: String
                var sides: Int
                init(name: String, sides: Int = 0) {
                    self.name = name
                    self.sides = sides
                }
                convenience init() { self.init(name: "blank") }
            }

            class Square: Shape {
                var side: Int = 4
            }

            open class Box<T> {
                var item: T
                init(item: T) { self.item = item }
            }

            class IntBox: Box<Int> {
                var extra: Int = 1
            }

            class D: A {
                var d: Int = 5
                init() {
                    super.init()
                    d = d + a
                }
            }
            """,
            """
                let b = B()
                print("\(b.a) \(b.b) \(b.s)")
                let c = C()
                print("\(c.a) \(c.b)")
                let q = Square(name: "sq", sides: 4)
                print("\(q.name) \(q.sides) \(q.side)")
                let r = Square(name: "r")
                print("\(r.name) \(r.sides)")
                let e = Square()
                print("\(e.name) \(e.side)")
                let i = IntBox(item: 9)
                print("\(i.item) \(i.extra)")
                let d = D()
                print(d.d)
            """
        );

    [Fact]
    public void AnInitialiserHandsItsWorkToAnotherAndSelfMakesTheType() =>
        Prints(
            "7 2 made\n9 0 q\n",
            """
            class Node {
                var name: String
                var children: Int = 0
                init(name: String) { self.name = name }
                convenience init() {
                    self.init(name: "7")
                    children = 2
                }
                static fn make() -> Self => Self(name: "made")
            }

            struct Q {
                var n: Int
                var tag: String
                init(n: Int, tag: String) {
                    self.n = n
                    self.tag = tag
                }
                init() { self.init(n: 9, tag: "q") }
                static fn zero() -> Self { return Self(n: 0, tag: "z") }
            }
            """,
            """
                let n = Node()
                print("\(n.name) \(n.children) \(Node.make().name)")
                let q = Q()
                print("\(q.n) \(Q.zero().n) \(q.tag)")
            """
        );

    [Fact]
    public void AnOverriddenPropertyIsReadAndWrittenThroughTheVTable() =>
        Prints(
            "2 der\nset 7\n1 base\n",
            """
            open class Base {
                var stored: Int = 0
                open var q: Int { get => 1 }
                open var label: String { get => "base" }
                open var level: Int {
                    get => stored
                    set { stored = newValue }
                }
            }

            class Der: Base {
                override var q: Int { get => 2 }
                override var label: String { get => "der" }
                override var level: Int {
                    get => stored
                    set { print("set \(newValue)") }
                }
            }
            """,
            """
                let b: Base = Der()
                print("\(b.q) \(b.label)")
                b.level = 7
                let plain = Base()
                print("\(plain.q) \(plain.label)")
            """
        );

    [Fact]
    public void AGenericClassIsInheritedAndItsMethodsOverridden() =>
        Prints(
            "1\n5\n5\n9\n",
            """
            open class GBase<T> {
                open fn take(_ x: T) -> Int { return 1 }
                fn name() -> Int { return 9 }
            }

            class GDer: GBase<Int> {
                override fn take(_ x: Int) -> Int { return x + 1 }
            }

            class GDer2<T>: GBase<T> {
                override fn take(_ x: T) -> Int { return 5 }
            }
            """,
            """
                let plain = GBase<Int>()
                print(plain.take(1))
                let g: GBase<Int> = GDer()
                print(g.take(4))
                let g2: GBase<String> = GDer2<String>()
                print(g2.take("a"))
                print(g2.name())
            """
        );
}
