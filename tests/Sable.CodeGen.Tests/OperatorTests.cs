using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     A type's own <c>operator []</c> and <c>operator ()</c>, compound
///     assignment from a binary operator, <c>===</c>, and <c>==</c> on tuples
///     and optionals — natively and in the interpreter, which must agree.
/// </summary>
public sealed class OperatorTests {
    static void Prints(string expected, string declarations, string body) =>
        Assert.Equal(expected, AssertAgree(Program(declarations, body)).Stdout);

    [Fact]
    public void TheWrappingOperatorsKeepTheLowBits() =>
        Prints(
            "true\n4\n255\n253\n91\n14\n",
            """
            fn mixed<T: FixedWidthInteger>(_ a: T, _ b: T) -> T { return a &* b &+ b }
            """,
            """
                print(Int.max &+ 1 == Int.min)
                var x: U8 = 250
                x &+= 10
                print(x)
                x &-= 5
                print(x)
                x &*= 3
                print(x)
                print(mixed(200 as U8, 3 as U8))
                print(2 &+ 3 &* 4)
            """
        );

    /// <summary>
    ///     A compound assignment evaluates its target once, for the read and
    ///     the store: indices, dictionary keys, subscripts' arguments and the
    ///     object a call gives, before the right side.
    /// </summary>
    [Fact]
    public void ACompoundAssignmentEvaluatesItsTargetOnce() =>
        Prints(
            """
            1 [10, 21, 30]
            2 [10, 21, 35]
            3 2
            4 7
            5 [0, 4]
            6 [[1, 2], [3, 14]]
            7 9
            9 [10, 21, 30, 1]
            10 [1, 102, 3] 2
            caught 11 2
            12 13

            """,
            """
            class Calls { var count = 0; init() { } }
            class Counter {
                var n: Int
                var items: Int[>..] = [0, 0]
                init(n: Int) { self.n = n }
            }
            struct Pair { var a: Int; var b: Int }
            struct Grid {
                var cells: Int[>..]
                subscript(at: Int) -> Int {
                    get { return cells[at] }
                    set { cells[at] = newValue }
                }
            }
            enum Failure: Error { Failed }

            fn index(_ calls: Calls, _ i: Int) -> Int {
                calls.count += 1
                return i
            }
            fn key(_ calls: Calls, _ text: String) -> String {
                calls.count += 1
                return text + "!"
            }
            fn made(_ calls: Calls, _ counter: Counter) -> Counter {
                calls.count += 1
                return counter
            }
            fn fails() throws -> Int { throw Failure.Failed }
            fn bump(_ i: inout Int) -> Int {
                i += 1
                return 100
            }
            fn addFailing(_ d: inout [String: Int], _ calls: Calls) throws {
                d[key(calls, "a"), default: 1] += try fails()
            }
            """,
            """
                let calls = Calls()
                var xs: Int[>..] = [10, 20, 30]
                xs[index(calls, 1)] += 1
                print("\(calls.count) \(xs)")
                xs[index(calls, 2)] += 5
                print("\(calls.count) \(xs)")
                xs[2] = 30
                var words: [String: Int] = ["a!": 1]
                words[key(calls, "a"), default: 0] += 1
                print("\(calls.count) \(words["a!"]!)")
                let counter = Counter(n: 3)
                made(calls, counter).n += 4
                print("\(calls.count) \(counter.n)")
                made(calls, counter).items[1] += 4
                print("\(calls.count) \(counter.items)")
                var nested: Int[>..][>..] = [[1, 2], [3, 4]]
                nested[index(calls, 1)][1] += 10
                print("\(calls.count) \(nested)")
                var pairs: Pair[>..] = [Pair { a: 1, b: 2 }, Pair { a: 3, b: 4 }]
                pairs[index(calls, 1)].b += 5
                print("\(calls.count) \(pairs[1].b)")
                var grid = Grid { cells: [10, 21, 30, 0] }
                grid[index(calls, index(calls, 3))] += 1
                print("\(calls.count) \(grid.cells)")
                var i = 1
                var ys: Int[>..] = [1, 2, 3]
                ys[i] += bump(&i)
                let _ = index(calls, 0)
                print("\(calls.count) \(ys) \(i)")
                do {
                    try addFailing(&words, calls)
                } catch {
                    print("caught \(calls.count) \(words["a!"]!)")
                }
                var optional: Int?[>..] = [none, 4]
                optional[index(calls, 0)] ??= 13
                print("\(calls.count) \(optional[0]!)")
            """
        );

    [Fact]
    public void OperatorsATypeDeclaresOrDerives() =>
        Prints(
            "8\n15\n12\ntrue\nfalse\ntrue\ntrue\nfalse\ntrue\ntrue\n",
            """
            struct Money {
                var cents: Int
                operator + (a: Money, b: Money) -> Money { return Money(cents: a.cents + b.cents) }
                operator == (a: Money, b: Money) -> Bool { return a.cents == b.cents }
                operator [] (m: Money, k: String) -> Int { return m.cents + k.count }
                operator () (m: Money, times: Int) -> Int { return m.cents * times }
            }
            class Box {
                var v: Int
                init(v: Int) { self.v = v }
            }
            """,
            """
                let m = Money(cents: 5)
                print(m["abc"])
                print(m(3))
                var total = Money(cents: 2)
                total += Money(cents: 10)
                print(total.cents)
                let a = Box(v: 1)
                let b = a
                print(a === b)
                print(a === Box(v: 1))
                print(a !== Box(v: 1))
                print((1, "x") == (1, "x"))
                print((1, "x") != (1, "x"))
                let held: Money? = Money(cents: 12)
                print(held == total)
                print(held != none)
            """
        );

    [Fact]
    public void LiteralsInArgumentsAndArraysTakeTheParametersOrTheOthersType() =>
        Prints(
            "150576608897510001227384393547382784\n255\n5\n4\n7\n16\n170141183460469231731687303715884105727\n",
            """
            fn wide(_ x: U128) -> U128 { return x }
            fn byte(_ x: U8) -> U8 { return x }
            fn count(_ xs: U8[:]) -> Int { return xs.count }
            fn twice(_ x: Int) -> Int { return x * 2 }
            fn twice(_ x: U128) -> U128 { return x * 2 }
            """,
            """
                print(wide(29 << 112))
                print(byte((200 + 55)))
                let version: U8 = 3
                var bytes: U8[>..] = []
                bytes.append(contentsOf: [0x54, 0x5A, 0x69, 0x66, version])
                print(bytes.count)
                print(count([1, 2, version, 0xFF]))
                let mixed = [4, version]
                print(mixed[0] + version)
                print(twice(1 << 3))
                let all: U128 = 340282366920938463463374607431768211455
                print(all & ~(1 << 127))
            """
        );

    [Fact]
    public void APrefixOperatorOfAGenericTypeTakesItsTypeArgumentsFromTheOperand() =>
        Prints(
            "-1.5 -2.0\n3 -4\n",
            """
            struct Pair<T: SignedNumeric> {
                var first: T
                var second: T
                prefix operator - (p: Pair<T>) -> Pair<T> { return Pair(first: -p.first, second: -p.second) }
                prefix operator ~ (p: Pair<T>) -> Pair<T> { return Pair(first: p.first, second: -p.second) }
            }
            fn flipped<T: SignedNumeric>(_ p: Pair<T>) -> Pair<T> { return -p }
            """,
            """
                let a = -Pair(first: 1.5, second: 2.0)
                print("\(a.first) \(a.second)")
                let b = ~flipped(Pair(first: -3, second: -4))
                print("\(b.first) \(b.second)")
            """
        );
}
