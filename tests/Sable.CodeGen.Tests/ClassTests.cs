using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Classes as the language describes them, end to end: inheritance and
///     overrides through 'super', abstract classes, protocols with defaults
///     in extensions, existentials, stored properties with initial values,
///     'private(set)', static stored properties and module-level variables,
///     and checked downcasts. Native and interpreted, leak-checked.
/// </summary>
public sealed class ClassTests {
    const string Declarations = """
                                protocol Named {
                                    var name: String { get }
                                    fn greet() -> String
                                }

                                extension Named {
                                    fn greet() -> String { return "hello, " + name }
                                }

                                open class Animal: Named {
                                    var name: String
                                    private var legs: Int
                                    init(_ name: String, legs: Int) {
                                        self.name = name
                                        self.legs = legs
                                    }
                                    fn speak() -> String { return "..." }
                                    fn describe() -> String { return "\(name) has \(legs) legs and says \(speak())" }
                                    deinit { print("bye \(name)") }
                                }

                                class Dog: Animal {
                                    init(_ name: String) { super.init(name, legs: 4) }
                                    override fn speak() -> String { return "woof" }
                                }

                                class Bird: Animal {
                                    var canFly: Bool
                                    init(_ name: String, canFly: Bool) {
                                        self.canFly = canFly
                                        super.init(name, legs: 2)
                                    }
                                    override fn speak() -> String { return canFly ? "tweet" : "squawk" }
                                    override fn describe() -> String { return super.describe() + (canFly ? ", flying" : "") }
                                }

                                abstract class Shape {
                                    abstract fn area() -> F64
                                    fn report() -> String { return "area \(area())" }
                                }

                                final class Square: Shape {
                                    var side: F64
                                    init(_ side: F64) { self.side = side }
                                    override fn area() -> F64 { return side * side }
                                }

                                class Counter {
                                    static var created = 0
                                    private(set) var value = 0
                                    var doubled: Int { get { return value * 2 } }
                                    init() { Counter.created = Counter.created + 1 }
                                    fn bump() { value = value + 1 }
                                }
                                var greetings = 0
                                """;

    [Fact]
    public void ClassesWorkAsTheLanguageSays() =>
        Assert.Equal(
            "rex has 4 legs and says woof\nhello, rex\ntweety has 2 legs and says tweet, flying\nhello, tweety\n"
            + "pingu has 2 legs and says squawk\nhello, pingu\nhello, fido\narea 9.0\ncast ok rex\n2 4 1\n"
            + "false true\n3\nbye fido\nbye rex\nbye tweety\nbye pingu\n",
            AssertAgree(
                Program(
                    Declarations,
                    """
                        let zoo: Animal[>..] = [Dog("rex"), Bird("tweety", canFly: true), Bird("pingu", canFly: false)]
                        for a in zoo {
                        print(a.describe())
                        print(a.greet())
                        }
                        let named: any Named = Dog("fido")
                        print(named.greet())
                        let s: Shape = Square(3.0)
                        print(s.report())
                        if let d = zoo[0] as? Dog {
                        print("cast ok \(d.name)")
                        }
                        let c = Counter()
                        c.bump()
                        c.bump()
                        print("\(c.value) \(c.doubled) \(Counter.created)")
                        print("\(zoo[0] is Bird) \(zoo[1] is Bird)")
                        greetings = greetings + zoo.count
                        print(greetings)
                    """
                )
            ).Stdout
        );

    [Fact]
    public void AFailedForcedCastTraps() {
        var execution = AssertAgree(Program(Declarations, "    let a: Animal = Dog(\"rex\")\n    let b = a as! Bird\n    print(b.name)"));
        Assert.Equal("fatal error: cast to 'Bird' failed\n", execution.Stderr);
    }

    [Theory]
    [InlineData("    let c = Counter()\n    c.value = 5", "'value' can be read here but not written: its setter is private")]
    [InlineData("    print(1)", "'later' needs a type annotation")]
    public void TheFrontEndHoldsTheRules(string body, string message) {
        var result = Pipeline.Compile(new SourceText(Program(Declarations + "\nvar later = Counter.created + 1\n", body), "test.sb"));
        Assert.Contains(result.Errors, e => e.Contains(message, StringComparison.Ordinal));
    }
}
