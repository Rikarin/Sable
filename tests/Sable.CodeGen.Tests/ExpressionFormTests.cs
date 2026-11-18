using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Expression forms of grammar.ebnf §7 that once stopped short of running:
///     tuple elements assigned, and calls with several trailing closures —
///     natively and in the interpreter, which must agree.
/// </summary>
public sealed class ExpressionFormTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    [Fact]
    public void ATupleElementCanBeAssigned() =>
        Prints(
            "9\n2\n10\nhi\n",
            """
            struct Holder {
                var pair: (Int, String)
            }
            """,
            """
                var sw = (1, 2)
                sw.0 = 9
                print(sw.0)
                print(sw.1)
                var h = Holder(pair: (0, "x"))
                h.pair.0 = 10
                h.pair.1 = "hi"
                print(h.pair.0)
                print(h.pair.1)
            """
        );

    [Fact]
    public void ACallTakesSeveralTrailingClosures() =>
        Prints(
            "3\n7\n",
            """
            fn both(_ body: () -> Int, onFail: () -> Int) -> Int {
                return body() + onFail()
            }
            """,
            """
                print(both { 1 } onFail: { 2 })
                let n = both {
                    3
                } onFail: {
                    4
                }
                print(n)
            """
        );

    [Fact]
    public void AnIfGivesTheValueOfTheBranchItTakes() =>
        Prints(
            "A\nB+\nC\nann!\nnobody\nleaving\n1\n2\n8\n-1\n10\n",
            """
            fn grade(_ n: Int) -> String {
                return if n >= 90 {
                    "A"
                } else if n >= 80 {
                    let letter = "B"
                    letter + "+"
                } else {
                    "C"
                }
            }

            fn name(_ o: String?) -> String {
                let s = if let v = o { v + "!" } else { "nobody" }
                return s
            }

            fn noisy(_ flag: Bool) -> Int {
                let n = if flag {
                    defer { print("leaving") }
                    1
                } else {
                    2
                }
                return n
            }

            fn checked(_ n: Int) -> Int {
                let m = if n > 0 { n } else { return -1 }
                return m * 2
            }
            """,
            """
                print(grade(95))
                print(grade(85))
                print(grade(10))
                print(name("ann"))
                print(name(none))
                print(noisy(true))
                print(noisy(false))
                print(checked(4))
                print(checked(-4))
                print(if 1 < 2 { 10 } else { 20 })
            """
        );

    [Fact]
    public void AMethodWithItsReceiverIsAValue() =>
        // The receiver is taken when the method is: a struct's copy then,
        // an object's reference, whose override is the one called.
        Prints(
            "4\n4\nhi ann\ndog\n12\ncaught\n",
            """
            struct Counter {
                var count: Int
                fn get() -> Int { return count }
            }

            class Animal {
                let name: String
                init(name: String) { self.name = name }
                fn greet(_ greeting: String) -> String { return greeting + " " + name }
                fn kind() -> String { return "animal" }
            }

            class Dog: Animal {
                init() { super.init(name: "rex") }
                override fn kind() -> String { return "dog" }
            }

            struct Scale {
                let factor: Int
                fn apply(_ n: Int) throws -> Int {
                    if n < 0 { throw Oops.Negative }
                    return n * factor
                }
            }

            union Oops: Error { Negative }

            fn run(_ f: (Int) throws -> Int, _ n: Int) -> String {
                return match try? f(n) {
                    case let v? => "\(v)"
                    case none => "caught"
                }
            }
            """,
            """
                var c = Counter(count: 4)
                let read = c.get
                c.count = 9
                print(read())
                print(read())
                let ann = Animal(name: "ann")
                let greet = ann.greet
                print(greet("hi"))
                let animal: Animal = Dog()
                let kind = animal.kind
                print(kind())
                let triple = Scale(factor: 3).apply
                print(run(triple, 4))
                print(run(triple, -1))
            """
        );

    [Fact]
    public void AGenericFunctionOrMethodIsAValueOfTheTypeWanted() =>
        // The type wanted says what a generic function's own type parameters
        // are; a method of a generic type has its receiver's.
        Prints(
            "4\nb\nwoof!\n7\n2\n5\nn=2\n",
            """
            fn id<T>(_ x: T) -> T { return x }

            class Stack<T> {
                var items: T[>..] = []
                init() {}
                fn push(_ x: T) { items.append(x) }
                fn top() -> T? { return items.isEmpty ? none : items[items.count - 1] }
            }

            protocol Shout { fn shout() -> String }
            struct Dog: Shout { fn shout() -> String { return "woof" } }
            fn loud<T: Shout>(_ x: T) -> String { return x.shout() + "!" }

            fn pair<A, B>(_ a: A, _ b: B) -> (A, B) { return (a, b) }
            fn apply(_ f: (Int) -> Int, _ x: Int) -> Int { return f(x) }

            // Inside a generic function, the reference's types are this instance's.
            fn twice<T>(_ x: T) -> T[>..] {
                let make: (T, T) -> (T, T) = pair
                let p = make(x, x)
                return [p.0, p.1]
            }

            struct Wrap<T> {
                var value: T
                static fn of(_ v: T) -> Wrap<T> { return Wrap(value: v) }
                fn map<U>(_ f: (T) -> U) -> Wrap<U> { return Wrap<U>(value: f(value)) }
            }
            """,
            """
                let f: (Int) -> Int = id
                print(f(4))
                let s = Stack<String>()
                let push = s.push
                push("a")
                push("b")
                let top = s.top
                print(top() ?? "-")
                let l: (Dog) -> String = loud
                print(l(Dog()))
                print(apply(id, 7))
                print(twice("z").count)
                let make: (Int) -> Wrap<Int> = Wrap<Int>.of
                print(make(5).value)
                let w = Wrap<Int>(value: 2)
                let m: ((Int) -> String) -> Wrap<String> = w.map
                print(m({ n in "n=\(n)" }).value)
            """
        );

    [Fact]
    public void AMethodOfAGenericClassOrATypeParameterIsAValue() =>
        // A generic class's method a subclass overrides is found through the
        // vtable; a requirement named on a type parameter is the concrete
        // type's own.
        Prints(
            "woof\n6\n",
            """
            protocol Shout { fn shout() -> String }
            struct Dog: Shout { fn shout() -> String { return "woof" } }

            fn f<T: Shout>(_ x: T) -> String {
                let s = x.shout
                return s()
            }

            class Base<T> {
                var v: T
                init(_ v: T) { self.v = v }
                fn get() -> T { return v }
            }

            class Derived: Base<Int> {
                init() { super.init(5) }
                override fn get() -> Int { return 6 }
            }
            """,
            """
                print(f(Dog()))
                let b: Base<Int> = Derived()
                let g = b.get
                print(g())
            """
        );

    [Fact]
    public void ATypeIsAValueOfItsMetatype() =>
        // 'T.self' is a value of type 'T.Type', which infers T where it is
        // passed and reaches T's static members; TypeInfo.of takes one.
        Prints(
            "Point\n2\n0\n0\nInt.Type\nInt[>..].Type\nInt?.Type\n",
            """
            protocol Zero { static fn zero() -> Self }
            struct Meters: Zero {
                var v: Int
                static fn zero() -> Meters { return Meters(v: 0) }
            }

            @reflectable
            struct Point { var x: Int, var y: Int }

            fn make<T: Zero>(_ type: T.Type) -> T { return type.zero() }
            fn name<T>(_ type: T.Type) -> String { return Reflect.typeName(type) }

            struct Holder { var kind: Meters.Type }
            """,
            """
                let c = Point.self
                print(TypeInfo.of(c).name)
                print(TypeInfo.of(Point.self).fields.count)
                print(make(Meters.self).v)
                let m = Holder(kind: Meters.self).kind
                print(m.zero().v)
                print(name(Int.self))
                print(name(Int[>..].self))
                print(Reflect.typeName(Int?.self))
            """
        );
}
