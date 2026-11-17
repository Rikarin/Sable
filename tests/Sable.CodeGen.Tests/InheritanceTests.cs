using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Class inheritance: fields laid out after the superclass's, methods
///     found through the vtable, initialisers split so that 'super.init'
///     initialises the object a subclass allocated, and deinits run from the
///     subclass up. Native and interpreted, leak-checked.
/// </summary>
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
}
