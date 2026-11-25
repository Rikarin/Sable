using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>Key paths (SYNTAX.md §16), and what generic code over them needs of classes: native and interpreted.</summary>
public sealed class KeyPathTests {
    [Fact]
    [Native]
    public void AKeyPathReadsAndNamesItsMembers() =>
        Assert.Equal(
            "address.city London\naddress 6\nplain name\noptional address.zip\noptional other.city\npath value\neach items each names\nAda\nb\nAda\nnone\n3\n",
            AssertAgree("""
                module P

                struct Address { var city: String, var zip: String?, var lines: Int { get => 3 } }
                struct Customer { var name: String, var address: Address, var other: Address?, var pair: (Int, String) }
                struct Bag { var items: Int[], var names: String[>..] }

                struct Paths<T> {
                    fn field<V>(_ p: KeyPath<T, V>) -> String => "plain \(p.path)"
                    fn field<V>(_ p: KeyPath<T, V?>) -> String => "optional \(p.path)"
                    fn equal(_ p: String) -> String => "value"
                    fn equal(_ p: KeyPath<T, String>) -> String => "path"
                    fn each<C: Sequence>(_ p: KeyPath<T, C>) -> String => "each \(p.path)"
                }

                let global: KeyPath<Customer, Int> = \.address.lines

                @main
                fn main() {
                    let c = Customer(name: "Ada", address: Address(city: "London", zip: none), other: none, pair: (1, "b"))
                    let city: KeyPath<Customer, String> = \.address.city
                    print("\(city.path) \(city.value(of: c))")
                    let address = \Customer.address
                    print("\(address.path) \(address.appending(\.city.count).value(of: c))")
                    let paths = Paths<Customer>()
                    print(paths.field(\.name))
                    print(paths.field(\.address.zip))
                    print(paths.field(\.other?.city))
                    print("\(paths.equal(\.name)) \(paths.equal("x"))")
                    print("\(Paths<Bag>().each(\.items)) \(Paths<Bag>().each(\.names))")
                    print([c, c].map(\.name)[1])
                    print((\Customer.pair.1).value(of: c))
                    let wide: KeyPath<Customer, String?> = \.name
                    print(wide.value(of: c) ?? "-")
                    print((\Customer.other?.city).value(of: c) ?? "none")
                    print(global.value(of: c))
                }
                """).Stdout
        );

    [Fact]
    public void AMethodNamedAloneInAClosureIsCapturedSelfs() =>
        Assert.Equal(
            "3\n3\n4\n",
            AssertAgree("""
                module P

                final class Box<T> {
                    var n: Int = 2
                    init() { }
                    fn add(_ a: Int) -> Int => a + n
                    fn adder() -> (Int) -> Int { return { add($0) } }
                    fn arrow() -> (Int) -> Int { return (a: Int) => add(a) }
                }

                struct Three {
                    fn three() -> Int => 3
                    fn made() -> () -> Int { return { three() + 1 } }
                }

                @main
                fn main() {
                    print(Box<String>().adder()(1))
                    print(Box<Int>().arrow()(1))
                    print(Three().made()())
                }
                """).Stdout
        );

    [Fact]
    public void AGenericClassHasAbstractMethods() =>
        Assert.Equal(
            "true\nfalse\n",
            AssertAgree("""
                module P

                abstract class Rule<T> {
                    abstract fn holds(_ t: T) -> Bool
                }

                final class Field<T, V>: Rule<T> {
                    let read: (T) -> V?
                    init(_ read: @escaping (T) -> V?) { self.read = read }
                    override fn holds(_ t: T) -> Bool { return read(t) != none }
                }

                struct Rules<T> {
                    var all: Rule<T>[>..] = []
                    fn hold(_ t: T) -> Bool { return all.allSatisfy { $0.holds(t) } }
                }

                extension Field {
                    // A closure that holds a 'Rules<V>', lowered to ask what it may capture.
                    fn nested(_ rules: Rules<V>) -> Field<T, Bool> {
                        let held = rules
                        let read = self.read
                        return Field<T, Bool>({ (t: T) in held.hold(read(t)!) ? true : none })
                    }
                }

                @main
                fn main() {
                    var inner = Rules<String>()
                    inner.all.append(Field<String, Int>((s: String) => s.isEmpty ? none : 1))
                    let outer = Field<Int, String>((n: Int) => n > 1 ? "x" : "").nested(inner)
                    print(outer.holds(2))
                    print(outer.holds(0))
                }
                """).Stdout
        );

    [Fact]
    public void AGenericClassOfEveryKindOfArrayIsOneClass() =>
        Assert.Equal(
            "[1, 2]\n[3, 4]\n",
            AssertAgree("""
                module P

                class Base<T> {
                    let value: T
                    init(_ value: T) { self.value = value }
                    open fn read() -> T { return value }
                }

                class Box<T>: Base<T> {
                    override fn read() -> T { return value }
                }

                struct Holder<T> { var base: Base<T> }

                fn show<T>(_ holder: Holder<T>) { print("\(holder.base.read())") }

                @main
                fn main() {
                    let fixed: Int[] = [1, 2]
                    var grown: Int[>..] = [3]
                    grown.append(4)
                    show(Holder<Int[]>(base: Box<Int[]>(fixed)))
                    show(Holder<Int[>..]>(base: Box<Int[>..]>(grown)))
                }
                """).Stdout
        );

    [Fact]
    public void AnArgumentsConformanceSaysWhatATypeIsOf() =>
        Assert.Equal(
            "3\n3\n",
            AssertAgree("""
                module P

                protocol Source<T> {
                    fn get1() -> T
                }

                struct Reader<T> {
                    let value: T
                    init<S: Source<T>>(_ source: S) { value = source.get1() }
                }

                extension Source {
                    // Self is a 'Source<T>' here.
                    fn reader() -> Reader<T> { return Reader<T>(self) }
                }

                struct Three: Source<Int> {
                    fn get1() -> Int => 3
                }

                @main
                fn main() {
                    let reader = Reader(Three())
                    print(reader.value)
                    print(Three().reader().value)
                }
                """).Stdout
        );
}
