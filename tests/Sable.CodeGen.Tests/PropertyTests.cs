using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Computed properties written to through their setters, properties
///     written <c>=&gt; e</c>, and ones kept in <c>field</c> — natively and in
///     the interpreter, which must agree, leak-checked.
/// </summary>
public sealed class PropertyTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    [Fact]
    public void ASetterIsCalledByAStoreAndByACompoundAssignment() =>
        Prints(
            "212 0\n5\n111\n",
            """
            struct Temp {
                var celsius: Int
                var fahrenheit: Int {
                    get => celsius * 9 / 5 + 32
                    set { celsius = (newValue - 32) * 5 / 9 }
                }
            }

            class Box {
                var n: Int = 0
                var doubled: Int {
                    get => n * 2
                    set => n = newValue / 2
                }
                init() { }
            }

            struct Counter {
                var raw: Int = 0
                var count: Int {
                    get => raw
                    set { raw = newValue }
                }
                mutating fn bump() {
                    count = count + 1
                    self.count += 10
                }
            }
            """,
            """
                var t = Temp { celsius: 100 }
                let hot = t.fahrenheit
                t.fahrenheit = 32
                print("\(hot) \(t.celsius)")
                let b = Box()
                b.doubled = 10
                print(b.n)
                var c = Counter { }
                c.bump()
                c.count += 100
                print(c.raw)
            """);

    [Fact]
    public void AnArrowPropertyIsItsGetter() =>
        Prints(
            "6 false true\ntrue\n",
            """
            struct Rect {
                var width: Int
                var height: Int
                var area: Int => width * height
                public var isSquare: Bool => width == height
                static var unit: Rect => Rect { width: 1, height: 1 }
            }

            class Door {
                var closed: Bool = true
                public var isClosed: Bool => closed
                init() { }
            }
            """,
            """
                let r = Rect { width: 2, height: 3 }
                print("\(r.area) \(r.isSquare) \(Rect.unit.isSquare)")
                print(Door().isClosed)
            """);

    [Fact]
    public void FieldKeepsAPropertysValue() =>
        Prints(
            "[ada] 0\n[grace] 14\n5\n15\nkept: x\n",
            """
            struct Name {
                var text: String = "" {
                    get => field
                    set => field = newValue.trimmed()
                }
                var hits: Int = 0 { get => field * 2 }
                var label: String
            }

            class Thermostat {
                var target: Int = 20 {
                    get => field
                    set {
                        if newValue < 5 {
                            field = 5
                        } else {
                            field = newValue
                        }
                    }
                }
                var log: String[>..] = []
                init() { }
            }

            fn rename(_ name: Name, _ to: String) -> Name {
                var copy = name
                copy.text = to
                return copy
            }
            """,
            """
                var n = Name { label: "x" }
                n.text = "  ada  "
                print("[\(n.text)] \(n.hits)")
                let m = rename(n, " grace ")
                print("[\(m.text)] \(m.text.count + 9)")
                let t = Thermostat()
                t.target = 2
                print(t.target)
                t.target += 10
                print(t.target)
                print("kept: \(n.label)")
            """);

    [Fact]
    public void GetAndSetAloneArePlainAndDescriptionsNameTheProperty() =>
        Prints(
            "[ada] 5\nAccount(owner: \"ada\", limit: 5, id: 7)\nowner,limit,id\nowner true\n",
            """
            @derive(Debug)
            struct Account: Reflectable {
                var owner: String = "" {
                    get
                    set => field = newValue.trimmed()
                }
                var limit: Int = 100 {
                    get => field
                    set
                }
                var id: Int
            }
            """,
            """
                var a = Account { id: 7 }
                a.owner = "  ada "
                a.limit = 5
                print("[\(a.owner)] \(a.limit)")
                print(a.debugDescription())
                print(Runtime.joinStrings(Reflect.fieldNames(a), ","))
                let info = TypeInfo.of(a)
                print("\(info.fields[0].name) \(info.fields[0].isMutable)")
            """);

    [Fact]
    public void ALazyPropertyIsComputedOnItsFirstReadAndKept() =>
        Prints(
            "before: 0\n42 42\nafter: 1\n7 report 21 1\nabc: [a, c] 2\n1\nbox: x\n",
            """
            var computed = 0

            fn expensive(_ n: Int) -> Int {
                computed = computed + 1
                return n * 2
            }

            class Box {
                var name: String
                init(_ name: String) { self.name = name }
            }

            class Report {
                var base: Int = 21
                lazy var total: Int = expensive(self.base)
                lazy var label: String = "report \(base)"
                lazy var box: Box = Box("x")
                init() { }
            }

            struct Doc {
                var name: String
                lazy var letters: String[>..] = name.split(separator: 'b')

                mutating fn describe() -> String {
                    return "\(name): [\(Runtime.joinStrings(letters, ", "))]"
                }
            }
            """,
            """
                let r = Report()
                print("before: \(computed)")
                let first = r.total
                print("\(first) \(r.total)")
                print("after: \(computed)")
                r.total = 7
                print("\(r.total) \(r.label) \(computed)")
                var d = Doc { name: "abc" }
                print("\(d.describe()) \(d.letters.count)")
                var copy = d
                copy.letters = ["x"]
                print(d.letters.count - copy.letters.count)
                print("box: \(r.box.name)")
            """);
}

