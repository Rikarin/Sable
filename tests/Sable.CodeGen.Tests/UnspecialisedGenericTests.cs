using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Unspecialised generic code (#297 stage 4): each program is built with
///     its generic functions compiled once for every type — boxes, metadata
///     and witness tables — and again specialised, and both run natively and
///     in the interpreter, leak-checked; all four must print the same.
/// </summary>
[Native]
public sealed class UnspecialisedGenericTests {
    static void Prints(string expected, string declarations, string body) {
        var program = Program(declarations, body);
        string unspecialised;
        using (SilGen.UseGenerics(GenericsMode.Unspecialised)) {
            unspecialised = AssertAgree(program).Stdout;
        }

        string specialised;
        using (SilGen.UseGenerics(GenericsMode.Specialised)) {
            specialised = AssertAgree(program).Stdout;
        }

        Assert.Equal(expected, specialised);
        Assert.Equal(specialised, unspecialised);

        // And the program's own generic functions were compiled once, not specialised.
        Assert.Contains(Unspecialised(declarations, body), static f => f.StartsWith("Test.", StringComparison.Ordinal));
    }

    /// <summary>The functions a program's unspecialised build compiles once: their SIL names.</summary>
    static List<string> Unspecialised(string declarations, string body) {
        using var _ = SilGen.UseGenerics(GenericsMode.Unspecialised);
        var result = Pipeline.Compile(new SourceText(Program(declarations, body), "test.sb"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        return [..result.Sil!.Functions.Select(static f => f.Name).Where(static n => n.EndsWith("$generic", StringComparison.Ordinal))];
    }

    const string Shapes = """
        protocol Shape {
            fn area() -> Int
            var name: String { get }
            static var sides: Int { get }
        }

        struct Square: Shape {
            var side: Int
            fn area() -> Int => side * side
            var name: String => "square"
            static var sides: Int => 4
        }

        final class Triangle: Shape {
            var base: Int
            var height: Int
            init(base: Int, height: Int) {
                self.base = base
                self.height = height
            }
            fn area() -> Int => base * height / 2
            var name: String => "triangle"
            static var sides: Int => 3
        }

        enum Dot: Shape {
            Small, Large

            fn area() -> Int => 1
            var name: String => "dot"
            static var sides: Int => 0
        }

        fn describe<S: Shape>(_ shape: S) -> String {
            return "\(shape.name) of \(S.sides) sides, area \(shape.area())"
        }

        fn larger<S: Shape>(_ a: S, _ b: S) -> S {
            return a.area() >= b.area() ? a : b
        }

        fn both<A: Shape, B: Shape>(_ a: A, _ b: B) -> String {
            var text = describe(a)
            text = text + "; " + describe(b)
            return text
        }
        """;

    [Fact]
    public void RequirementsAndPropertiesOfStructsClassesAndEnums() =>
        Prints(
            "square of 4 sides, area 9\ntriangle of 3 sides, area 6\ndot of 0 sides, area 1\n"
            + "square of 4 sides, area 25\ntriangle of 3 sides, area 10\nsquare of 4 sides, area 4; dot of 0 sides, area 1\n",
            Shapes,
            """
                print(describe(Square { side: 3 }))
                print(describe(Triangle(base: 4, height: 3)))
                print(describe(Dot.Large))
                print(describe(larger(Square { side: 2 }, Square { side: 5 })))
                print(describe(larger(Triangle(base: 4, height: 5), Triangle(base: 2, height: 2))))
                print(both(Square { side: 2 }, Dot.Small))
            """);

    [Fact]
    public void EachGenericFunctionIsCompiledOnce() {
        var functions = Unspecialised(Shapes, """
                print(describe(Square { side: 3 }))
                print(describe(Triangle(base: 4, height: 3)))
                print(both(Square { side: 2 }, Dot.Small))
            """);
        Assert.Contains("Test.describe$generic", functions);
        Assert.Contains("Test.both$generic", functions);
    }

    [Fact]
    public void StaticRequirementsOperatorsAndMetatypes() =>
        Prints(
            "0 5\ntrue false\nTest.Meters Int\n<7> <seven> <Meters(value: 7)>\n",
            """
            protocol Measure: Equatable {
                static fn zero() -> Self
                operator + (a: Self, b: Self) -> Self
                var value: Int { get }
            }

            struct Meters: Measure {
                var value: Int
                static fn zero() -> Meters => Meters { value: 0 }
                operator + (a: Meters, b: Meters) -> Meters => Meters { value: a.value + b.value }
                operator == (a: Meters, b: Meters) -> Bool => a.value == b.value
            }

            fn make<T: Measure>(_ type: T.Type) -> T { return type.zero() }

            fn total<T: Measure>(_ a: T, _ b: T, _ c: T) -> T { return a + b + c + T.zero() }

            fn same<T: Measure>(_ a: T, _ b: T) -> Bool { return a == b }

            fn isInt<T>(_ value: T) -> Bool { return T.self == Int.self }

            fn kind<T>(_ value: T) -> Type { return type(of: value) }

            fn show<T>(_ value: T) -> String { return "<\(value)>" }
            """,
            """
                print("\(make(Meters.self).value) \(total(Meters { value: 2 }, Meters { value: 3 }, Meters { value: 0 }).value)")
                print("\(same(Meters { value: 1 }, Meters { value: 1 })) \(isInt("one"))")
                print("\(kind(Meters { value: 1 })) \(kind(3))")
                print("\(show(7)) \(show("seven")) \(show(Meters { value: 7 }))")
            """);

    [Fact]
    public void AValueChangedInGenericCodeIsItsOwn() =>
        Prints(
            "1 3\n10 12\n",
            """
            protocol Counter {
                mutating fn bump()
                fn count() -> Int
            }

            struct Clicks: Counter {
                var n: Int
                mutating fn bump() { n = n + 1 }
                fn count() -> Int => n
            }

            final class Tally: Counter {
                var n: Int
                init(n: Int) { self.n = n }
                fn bump() { n = n + 1 }
                fn count() -> Int => n
            }

            fn bumped<C: Counter>(_ counter: C) -> C {
                var copy = counter
                copy.bump()
                copy.bump()
                return copy
            }
            """,
            """
                let start = Clicks { n: 1 }
                let after = bumped(start)
                print("\(start.count()) \(after.count())")
                let tally = Tally(n: 10)
                let same = bumped(tally)
                print("\(10) \(same.count())")
            """);

    [Fact]
    public void ARequirementThatThrowsThrowsThroughGenericCode() =>
        Prints(
            "4\nfailed: too big\n",
            """
            union Problem: Error {
                TooBig
            }

            protocol Checked {
                fn checked() throws(Problem) -> Int
            }

            struct Small: Checked {
                var n: Int
                fn checked() throws(Problem) -> Int {
                    if n > 9 { throw Problem.TooBig }
                    return n
                }
            }

            fn run<C: Checked>(_ value: C) throws(Problem) -> Int { return try value.checked() }
            """,
            """
                print(try! run(Small { n: 4 }))
                do {
                    print(try run(Small { n: 40 }))
                } catch {
                    print("failed: too big")
                }
            """);

    /// <summary>A generic function that recurses, and one refining protocol reached through another's table, by the registry.</summary>
    [Fact]
    public void RecursionAndRefinedProtocols() =>
        Prints(
            "6 6\nhello hello hello \n",
            """
            protocol Named {
                fn name() -> String
            }

            protocol Greeter: Named {
                fn greeting() -> String
            }

            struct English: Greeter {
                fn name() -> String => "hello"
                fn greeting() -> String => "hi"
            }

            fn nameOf<N: Named>(_ value: N) -> String { return value.name() }

            fn repeated<G: Greeter>(_ value: G, _ times: Int) -> String {
                if times == 0 { return "" }
                return nameOf(value) + " " + repeated(value, times - 1)
            }

            fn sum<T: AdditiveArithmetic>(_ a: T, _ n: Int) -> T {
                if n == 0 { return a }
                return a + sum(a, n - 1)
            }
            """,
            """
                print("\(sum(2, 2)) \(sum(1, 5))")
                print(repeated(English(), 3))
            """);

    const string Extended = """
        protocol Shape {
            fn area() -> Int
            mutating fn grow()
        }

        extension Shape {
            fn doubled() -> Int => area() * 2

            fn describe() -> String { return "area \(area()), doubled \(self.doubled())" }

            mutating fn growTwice() {
                grow()
                self.grow()
            }

            fn same(as other: Self) -> Bool { return area() == other.area() }

            fn bigger(_ other: Self) -> Self { return area() >= other.area() ? self : other }
        }

        struct Square: Shape {
            var side: Int
            fn area() -> Int => side * side
            mutating fn grow() { side = side + 1 }
        }

        final class Circle: Shape {
            var r: Int
            init(r: Int) { self.r = r }
            fn area() -> Int => 3 * r * r
            fn grow() { r = r + 1 }
        }

        fn report<S: Shape>(_ shape: S) -> String { return shape.describe() }
        """;

    const string UsesExtended = """
            var square = Square { side: 2 }
            print(square.describe())
            square.growTwice()
            print(square.doubled())
            print(report(Circle(r: 1)))
            print(square.same(as: Square { side: 4 }))
            print(square.bigger(Square { side: 9 }).area())
        """;

    /// <summary>
    ///     A protocol extension's members are generic over Self: compiled once,
    ///     given self where its caller holds it — its storage, for a mutating
    ///     one — and Self's metadata and table; one calls another on self.
    /// </summary>
    [Fact]
    public void ProtocolExtensionMembersAreCompiledOnce() {
        Prints("area 4, doubled 8\n32\narea 3, doubled 6\ntrue\n81\n", Extended, UsesExtended);
        var functions = Unspecialised(Extended, UsesExtended);
        foreach (var member in new[] { "doubled", "describe", "growTwice", "same", "bigger" }) {
            Assert.Contains($"Test.Shape.{member}$generic", functions);
        }
    }

    const string Arrays = """
        struct Named {
            var name: String
        }

        final class Counter {
            var n: Int
            init(n: Int) { self.n = n }
        }

        fn first<T>(_ items: T[>..]) -> T { return items[0] }

        fn twice<T>(_ value: T) -> T[>..] { return [value, value] }

        fn counted<T>(_ items: T[>..]) -> Int {
            var n = 0
            for item in items {
                let kept = item
                n = n + 1
            }
            return n + items.count
        }

        fn appended<T>(_ items: T[>..], _ value: T) -> T[>..] {
            var copy = items
            copy.append(value)
            return copy
        }

        fn last<T>(_ items: T[>..]) -> T {
            var copy = items
            return copy.removeLast()
        }

        fn show<T>(_ items: T[>..]) -> String {
            var text = ""
            for item in items {
                text = text + "<\(item)>"
            }
            return text
        }
        """;

    /// <summary>
    ///     An array of a type parameter's values is a buffer of them, laid out
    ///     by the element's metadata: made, read, walked, appended to and
    ///     popped in code compiled once, with the array's metadata made at
    ///     run time from the element's, and handed to code that knows the type.
    /// </summary>
    [Fact]
    public void ArraysOfATypeParametersValues() {
        Prints(
            "1\nb\n6\n4\n2\n<x><y><z>\n<Named(name: \"p\")><Named(name: \"q\")>\nq\n7\n",
            Arrays,
            """
                print(first([1, 2]))
                print(first(appended(["a"], "b").slice(from: 1, to: 2).toArray()))
                print(counted(["a", "b", "c"]))
                print(counted(twice(Named { name: "n" })))
                print(last(appended([1], 2)))
                print(show(appended(["x", "y"], "z")))
                print(show(appended(twice(Named { name: "p" }).slice(from: 0, to: 1).toArray(), Named { name: "q" })))
                print(last([Named { name: "p" }, Named { name: "q" }]).name)
                let counters = appended([Counter(n: 5)], Counter(n: 7))
                print(last(counters).n)
            """);
        var functions = Unspecialised(Arrays, """
                print(first(twice(1)))
                print(counted(appended([1], 2)))
                print(last([1]))
                print(show([1]))
            """);
        foreach (var name in new[] { "first", "twice", "counted", "appended", "last", "show" }) {
            Assert.Contains($"Test.{name}$generic", functions);
        }
    }

    const string Optionals = """
        final class Box {
            var label: String
            init(label: String) {
                self.label = label
            }
        }

        struct Pair {
            var name: String
            var count: Int
        }

        fn firstOf<T>(_ items: T[]) -> T? {
            if items.count == 0 {
                return none
            }
            return items[0]
        }

        fn orElse<T>(_ value: T?, _ fallback: T) -> T {
            if let present = value {
                return present
            }
            return fallback
        }

        fn either<T>(_ value: T?, _ fallback: T) -> T => value ?? fallback

        fn holds<T>(_ value: T?) -> Bool {
            return match value {
                case .Some(_) => true
                case .None => false
            }
        }

        fn isNone<T>(_ value: T?) -> Bool => value == none

        fn forced<T>(_ value: T?) -> T => value!

        fn wrapped<T>(_ value: T) -> T? => value

        fn twiceWrapped<T>(_ value: T) -> T?? => wrapped(wrapped(value))

        fn typeName<T>(_ value: T) -> String => "\(type(of: value))"

        fn namesOf<T>(_ value: T) -> String => "\(typeName(wrapped(value))) \(typeName((value, 1))) \(typeName(twiceWrapped(value)))"

        fn swapped<T, U>(_ pair: (T, U)) -> (U, T) => (pair.1, pair.0)

        fn split<T>(_ items: T[]) -> (T?, Int) {
            let (head, count) = (firstOf(items), items.count)
            return (head, count)
        }
        """;

    /// <summary>
    ///     An optional and a tuple of a type parameter's values are laid out
    ///     at run time from its metadata, as code that knows the type lays
    ///     out its own — a tag and a payload, a null reference, a Bool's or a
    ///     Char's spare values — so each passes between the two as it is.
    /// </summary>
    [Fact]
    public void OptionalsAndTuplesOfATypeParametersValues() {
        Prints(
            "3\n-1\na\nz\nlabel\nfalse\ntrue\nfalse\nx\ntrue\nn 2\nq\n2\nfalse\ntrue\n"
            + "Int? (Int, Int) Int??\nString? (String, Int) String??\n"
            + "b 1\n(2, \"s\")\n(3, 2)\n(none, 0)\n7\n",
            Optionals,
            """
                let empty: Int[] = []
                print(firstOf([3, 4]) ?? -1)
                print(firstOf(empty) ?? -1)
                print(orElse(firstOf(["a", "b"]), "z"))
                print(either(none, "z"))
                print(orElse(firstOf([Box(label: "label")]), Box(label: "other")).label)
                print(firstOf([false]) ?? true)
                print(holds(firstOf([true])))
                print(holds(firstOf(empty)))
                print(forced(wrapped('x')))
                let noBoxes: Box[] = []
                print(isNone(firstOf(noBoxes)))
                let pair = forced(firstOf([Pair(name: "n", count: 2)]))
                print("\(pair.name) \(pair.count)")
                print(either(wrapped("q"), "r"))
                print(forced(forced(twiceWrapped(2))))
                print(isNone(wrapped(Box(label: "c"))))
                let noPairs: Pair[] = []
                print(isNone(firstOf(noPairs)))
                print(namesOf(1))
                print(namesOf("s"))
                let (label, number) = swapped((1, Box(label: "b")))
                print("\(label.label) \(number)")
                print(swapped(("s", 2)))
                print(split([3, 4]))
                print(split(empty))
                print(forced(split([7]).0))
            """);
        var functions = Unspecialised(Optionals, """
                print(namesOf(1))
                print(either(firstOf([1]), 2) + forced(split([1]).0) + swapped((1, 2)).0)
                print(orElse(none, 1))
                print(holds(wrapped(1)) && isNone(wrapped(1)))
            """);
        foreach (var name in new[] { "firstOf", "orElse", "either", "holds", "isNone", "forced", "wrapped", "twiceWrapped", "typeName", "namesOf", "swapped", "split" }) {
            Assert.Contains($"Test.{name}$generic", functions);
        }
    }

    const string GenericTypes = """
        struct Stack<T> {
            var items: T[>..]
            var label: String

            fn peek() -> T? {
                if items.count == 0 {
                    return none
                }
                return items[items.count - 1]
            }

            mutating fn push(_ item: T) {
                items.append(item)
            }

            mutating fn pop() -> T? {
                return items.removeLast()
            }

            fn size() -> Int => items.count

            fn described() -> String => "\(label) of \(size())"
        }

        struct Pair<A, B> {
            var first: A
            var second: B

            fn swapped() -> Pair<B, A> => Pair<B, A> { first: second, second: first }
        }

        union Either<L, R> {
            Left(L)
            Right(R)

            fn left() -> L? => match self {
                case .Left(let value) => value
                case .Right(_) => none
            }

            fn isRight() -> Bool => match self {
                case .Left(_) => false
                case .Right(_) => true
            }
        }

        final class Token {
            var name: String
            init(name: String) {
                self.name = name
            }
        }

        fn wrap<T>(_ value: T) -> Pair<T, Int> => Pair<T, Int> { first: value, second: 1 }

        fn eitherOf<T>(_ value: T, _ left: Bool) -> Either<T, String> => left ? .Left(value) : .Right("none")

        fn typeName<T>(_ value: T) -> String => "\(type(of: value))"
        """;

    /// <summary>
    ///     A generic struct's or union's members are compiled once: the type is
    ///     laid out at run time from its type arguments' metadata, as code that
    ///     knows them lays it out, so a value passes between the two as it is.
    /// </summary>
    [Fact]
    public void MembersOfGenericStructsAndUnionsAreCompiledOnce() {
        Prints(
            "b\nwords of 2\nb\n1\nx 1\nw\n3\nfalse\ntrue\nt\nfalse\n2\nTest.Pair<String, Int> Test.Either<Int, String>\n",
            GenericTypes,
            """
                var stack = Stack<String> { items: [], label: "words" }
                stack.push("a")
                stack.push("b")
                print(stack.peek() ?? "-")
                print(stack.described())
                print(stack.pop() ?? "-")
                print(stack.size())
                let pair = Pair<Int, String> { first: 1, second: "x" }
                let other = pair.swapped()
                print("\(other.first) \(other.second)")
                print(wrap("w").first)
                let e: Either<Int, String> = .Left(3)
                print(e.left() ?? 0)
                print(e.isRight())
                print(eitherOf(Token(name: "t"), false).isRight())
                print(eitherOf(Token(name: "t"), true).left()!.name)
                var tokens = Stack<Token> { items: [Token(name: "u")], label: "tokens" }
                tokens.push(Token(name: "v"))
                print(tokens.pop()!.name == "u")
                print(tokens.size() + wrap(Token(name: "w")).second)
                print("\(typeName(wrap("p"))) \(typeName(eitherOf(1, true)))")
            """);
        var functions = Unspecialised(GenericTypes, """
                var stack = Stack<Int> { items: [], label: "n" }
                stack.push(1)
                print(stack.pop() ?? stack.peek() ?? stack.size())
                print(stack.described())
                print(Pair<Int, Int> { first: 1, second: 2 }.swapped().first)
                print(eitherOf(1, true).left() ?? 0)
                print(eitherOf(1, true).isRight())
            """);
        foreach (var name in new[] { "Stack.push", "Stack.pop", "Stack.peek", "Stack.size", "Stack.described", "Pair.swapped", "Either.left", "Either.isRight", "eitherOf" }) {
            Assert.Contains($"Test.{name}$generic", functions);
        }
    }

    /// <summary>
    ///     A requirement that takes or gives an optional or a tuple of Self is
    ///     called through the witness table as one of Self is: the value where
    ///     its caller holds it, the result written where the caller says.
    /// </summary>
    [Fact]
    public void RequirementsOfOptionalsAndTuplesOfSelf() {
        const string declarations = """
            protocol Halving {
                fn halved() -> (Self, Bool)
                fn exactHalf() -> Self?
                fn joined(_ other: (Self, Int)) -> Self
                var doubled: (Self, Self) { get }
            }

            struct Count: Halving {
                var n: Int
                fn halved() -> (Count, Bool) => (Count(n: n / 2), n % 2 == 0)
                fn exactHalf() -> Self? => n % 2 == 0 ? Count(n: n / 2) : none
                fn joined(_ other: (Count, Int)) -> Count => Count(n: n + other.0.n * other.1)
                var doubled: (Count, Count) => (self, self)
            }

            final class Word: Halving {
                var text: String
                init(text: String) {
                    self.text = text
                }
                fn exactHalf() -> Self? => text.count % 2 == 0 ? Word(text: "half") : none
                var doubled: (Self, Self) => (self, Word(text: text + text))
            }

            extension Halving {
                fn halved() -> (Self, Bool) => (self.exactHalf() ?? self, self.exactHalf() != none)
                fn joined(_ other: (Self, Int)) -> Self => other.1 > 2 ? other.0 : self
            }

            fn report<H: Halving>(_ value: H) -> String {
                let (half, even) = value.halved()
                let exact = value.exactHalf()
                let pair = value.doubled
                let again = pair.1.joined((half, 3))
                return "\(even) \(exact == none) \(half.halved().1) \(again.halved().1) \(pair.0.exactHalf() != none)"
            }
            """;
        Prints("true false true true true\nfalse true false false false\n", declarations, """
                print(report(Count(n: 4)))
                print(report(Word(text: "abc")))
            """);
        Assert.Contains("Test.report$generic", Unspecialised(declarations, "print(report(Count(n: 4)))"));
    }

    /// <summary>
    ///     Polymorphic recursion — a generic function calling itself with an
    ///     array of its type parameter — has no end of specialised copies (a
    ///     specialised build makes it one instance over existentials);
    ///     compiled once it is one function, given the array's metadata made
    ///     at run time.
    /// </summary>
    [Fact]
    public void PolymorphicRecursionWorksUnspecialised() {
        const string declarations = """
            fn nest<T>(_ value: T, _ depth: Int) -> String {
                if depth == 0 {
                    return "\(type(of: value))"
                }
                return nest([value], depth - 1)
            }

            fn count<T>(_ value: T, _ depth: Int) -> Int {
                if depth == 0 {
                    return 0
                }
                return 1 + count([value, value], depth - 1)
            }
            """;
        var program = Program(declarations, """
                print(nest(1, 0))
                print(nest(1, 3))
                print(nest("s", 2))
                print(count(1, 5))
            """);
        using (SilGen.UseGenerics(GenericsMode.Unspecialised)) {
            Assert.Equal("Int\n[[[Int]]]\n[[String]]\n5\n", AssertAgree(program).Stdout);
        }

        // Specialised, it is the one instance that takes existentials, whose arrays are of 'any Any'.
        using (SilGen.UseGenerics(GenericsMode.Specialised)) {
            Assert.Equal("Int\n[any Any]\n[any Any]\n5\n", AssertAgree(program).Stdout);
        }

        Assert.Contains("Test.nest$generic", Unspecialised(declarations, "print(nest(1, 3))"));
    }

    const string Sequences = """
        enum Stop: Error {
            Early
        }

        struct Countdown: Sequence {
            let from: Int
            fn makeIterator() -> CountdownIterator => CountdownIterator { left: from }
        }

        struct CountdownIterator: Iterator {
            var left: Int
            mutating fn next() -> Int? {
                if left == 0 {
                    return none
                }
                left -= 1
                return left + 1
            }
        }

        struct Failing: Sequence {
            let after: Int
            fn makeIterator() -> FailingIterator => FailingIterator { left: after }
        }

        struct FailingIterator: Iterator {
            var left: Int
            mutating fn next() throws(Stop) -> String? {
                if left == 0 {
                    throw Stop.Early
                }
                left -= 1
                return "x\(left)"
            }
        }

        protocol Bag {
            associatedtype Item: Equatable
            fn items() -> Item[>..]
            fn first() -> Item?
        }

        struct Words: Bag {
            let words: String[>..]
            fn items() -> String[>..] => words
            fn first() -> String? => words.count > 0 ? words[0] : none
        }

        extension Bag {
            fn holds(_ item: Item) -> Bool {
                for each in items() {
                    if each == item {
                        return true
                    }
                }
                return false
            }

            fn firstOrNone() -> String => first().map { "\($0)" } ?? "none"
        }

        fn collect<S: Sequence>(_ s: S) throws(S.Failure) -> S.Element[>..] {
            var result: S.Element[>..] = []
            for try x in s {
                result.append(x)
            }
            return result
        }

        fn joined<S: Sequence>(_ s: S) -> String where S.Failure == Never {
            var text = ""
            for x in s {
                text += "\(x) "
            }
            return text
        }

        fn firstOf<C: Collection>(_ c: C) -> C.Element? {
            var it = c.makeIterator()
            return it.next()
        }

        fn count<S: Sequence>(_ s: S, of wanted: S.Element) -> Int where S.Element: Equatable, S.Failure == Never {
            var n = 0
            for x in s {
                if x == wanted {
                    n += 1
                }
            }
            return n
        }

        fn kindOf<S: Sequence>(_ s: S) -> String where S.Failure == Never {
            var it = s.makeIterator()
            guard let x = it.next() else {
                return "empty"
            }
            return "\(type(of: x))"
        }
        """;

    [Fact]
    public void AssociatedTypesAreReadFromTheWitnessTables() {
        Prints(
            "3 2 1 \n[3, 2, 1]\n2\n1\nInt String\n0 1 2 \ntrue\nfalse\na\n",
            Sequences,
            """
                print(joined(Countdown(from: 3)))
                print(collect(Countdown(from: 3)))
                print(count(Countdown(from: 4), of: 2) + count(Countdown(from: 1), of: 1))
                print(firstOf(1..<5) ?? 0)
                print(kindOf(Countdown(from: 1)) + " " + kindOf(["s"].lazy))
                print(joined(0..<3))
                let words = Words { words: ["a", "b"] }
                print(words.holds("b"))
                print(words.holds("c"))
                print(words.firstOrNone())
            """);
    }

    [Fact]
    public void ComputedPropertiesOfAProtocolExtensionAreCompiledOnce() {
        const string declarations = """
            protocol Sized {
                var size: Int { get }
                static var unit: Int { get }
            }

            extension Sized {
                var doubled: Int => size * 2
                static var units: Int => Self.unit * 10
                var big: Bool => doubled > Self.units
            }

            struct Box: Sized {
                var size: Int
                static var unit: Int => 3
            }

            fn report<T: Sized>(_ x: T) -> String {
                return "\(x.doubled) \(T.units) \(x.big)"
            }

            fn widths<T: FixedWidthInteger>(_ x: T) -> String => "\(T.bitWidth) \(x.nextPowerOfTwo())"
            """;
        Prints(
            "8 30 false\n80 30 true\nfalse\n8 4\n64 16\n",
            declarations,
            """
                print(report(Box(size: 4)))
                print(report(Box(size: 40)))
                print(Box(size: 1).big)
                let small: U8 = 3
                print(widths(small))
                print(widths(16))
            """);
        Assert.Contains(Unspecialised(declarations, "print(report(Box(size: 4)))"), static f => f.Contains("big", StringComparison.Ordinal));
    }

    [Fact]
    public void AnErrorOfAnAssociatedTypeIsWrittenWhereTheCallerSays() {
        Prints(
            "[2, 1]\nstopped: Early\n",
            Sequences,
            """
                print(collect(Countdown(from: 2)))
                do {
                    let got = try collect(Failing(after: 3))
                    print(got)
                } catch {
                    print("stopped: \(error)")
                }
            """);
    }

    [Fact]
    public void ClosuresOverATypeParametersValuesAreCalledInTheGenericConvention() {
        const string declarations = """
            enum Oops: Error {
                Bad
            }

            fn count<T>(_ items: T[], _ test: (T) -> Bool) -> Int {
                var n = 0
                for item in items {
                    if test(item) {
                        n += 1
                    }
                }
                return n
            }

            fn occurrences<T: Equatable>(_ items: T[], _ wanted: T) -> Int {
                return count(items, { $0 == wanted })
            }

            fn mapped<T, U>(_ items: T[], _ transform: (T) -> U) -> U[>..] {
                var result: U[>..] = []
                for item in items {
                    result.append(transform(item))
                }
                return result
            }

            fn pairs<T, U>(_ items: T[], _ other: U) -> (T, U)[>..] {
                let kept = other
                return mapped(items, { x in (x, kept) })
            }

            fn update<T>(_ value: T, _ change: (inout T) -> Void) -> T {
                var copy = value
                change(&copy)
                return copy
            }

            fn firstWhere<T>(_ items: T[], _ test: (T) throws -> Bool) rethrows -> T? {
                for item in items {
                    if try test(item) {
                        return item
                    }
                }
                return none
            }

            fn through<A, B>(_ x: A, _ f: (A) -> B) -> B {
                return f(x)
            }

            fn lengths<T>(_ items: T[], _ f: (T) -> Int) -> Int {
                return through(items, { all in
                    var total = 0
                    for item in all {
                        total += f(item)
                    }
                    return total
                })
            }
            """;
        Prints(
            "2\n2\n[\"1!\", \"2!\", \"3!\"]\n2 y 7\n7\nabc\nbb\ncaught\n6\nhey!\n-1\n",
            declarations,
            """
                print(occurrences([1, 2, 1, 3], 1))
                print(occurrences(["a", "b", "b"], "b"))
                print(mapped([1, 2, 3], { "\($0)!" }))
                let p = pairs(["x", "y"], 7)
                print("\(p.count) \(p[1].0) \(p[1].1)")
                print(update(3, { $0 += 4 }))
                print(update("ab", { s in s = s + "c" }))
                print(firstWhere(["a", "bb", "ccc"], { $0.count == 2 }) ?? "none")
                do {
                    let found = try firstWhere([1, 2, 3], { x in
                        if x == 2 {
                            throw Oops.Bad
                        }
                        return false
                    })
                    print(found ?? 0)
                } catch {
                    print("caught")
                }
                print(lengths(["a", "bb", "ccc"], { $0.count }))
                let s: String? = "hey"
                print(s.map({ $0 + "!" }) ?? "")
                let n: Int? = none
                print(n.map({ $0 * 2 }) ?? -1)
            """);
        var compiled = Unspecialised(declarations, """
                print(occurrences([1], 1))
                print(pairs([1], "a").count)
                print(lengths(["a"], { $0.count }))
                print(update(1, { $0 += 1 }))
            """);
        Assert.Contains("Test.occurrences$generic", compiled);
        Assert.Contains("Test.pairs$generic", compiled);
        Assert.Contains("Test.lengths$generic", compiled);
        Assert.Contains("Test.update$generic", compiled);
        Assert.Contains(compiled, static f => f.StartsWith("Test.occurrences$generic$closure", StringComparison.Ordinal));
    }

    [Fact]
    public void AnErrorOfAnAssociatedTypeIsThrownOnAsAnyError() {
        Prints(
            "[1]\n[30, 20, 10]\n[3, 1]\n6\nstopped: Early\nstopped: Early\n",
            Sequences,
            """
                print(collect(Countdown(from: 1)))
                print(Countdown(from: 3).map({ $0 * 10 }))
                print(Countdown(from: 3).filter({ $0 != 2 }))
                print(Countdown(from: 3).reduce(0, { $0 + $1 }))
                do {
                    let got = try Failing(after: 2).map({ $0 + "!" })
                    print(got)
                } catch {
                    print("stopped: \(error)")
                }
                do {
                    let got = try Failing(after: 1).filter({ $0.count > 0 })
                    print(got)
                } catch {
                    print("stopped: \(error)")
                }
            """);
        var compiled = Unspecialised(Sequences, "print(Countdown(from: 1).map({ $0 }))");
        Assert.Contains("Sable.Core.Sequence.map$generic", compiled);
    }

    const string Slices = """
        struct Named {
            var name: String
        }

        fn firstOf<T>(_ items: T[:]) -> T? {
            if items.count == 0 {
                return none
            }
            return items[0]
        }

        fn total<T>(_ items: T[:], _ weigh: (T) -> Int) -> Int {
            var sum = 0
            for item in items {
                sum += weigh(item)
            }
            return sum
        }

        fn middle<T>(_ items: T[:]) -> T[:] => items.slice(from: 1, to: items.count - 1)

        // Each element written over itself, in a copy of the run: the
        // assignment hands its reference over though the object is the same.
        fn rewritten<T>(_ items: T[:]) -> T[:] {
            var copy = items
            var k = 0
            while k < items.count {
                copy[k] = items[k]
                k = k + 1
            }
            return copy
        }

        fn overwrite<T>(_ items: T[>..], with value: T) -> T[>..] {
            var changed = items
            changed.fill(value)
            return changed
        }

        struct Window<T> {
            var items: T[:]
            fn last() -> T? => items.count == 0 ? none : items[items.count - 1]
        }
        """;

    /// <summary>
    ///     A slice of a type parameter's values is a view of an array of them,
    ///     its elements where the element's metadata says: passed, sliced,
    ///     walked and written through in code compiled once, held in a generic
    ///     struct, and the slice extensions' members (sort, reverse, swapAt,
    ///     iterators) compiled once for every element type.
    /// </summary>
    [Fact]
    public void SlicesOfATypeParametersValues() {
        Prints(
            "2\nb\n6\n[2, 3]\nyy\n[\"z\", \"z\", \"z\"]\n[1, 2, 3]\n[\"c\", \"b\", \"a\"]\n[3, 2, 1]\nzz\n3\nq\nq\n",
            Slices,
            """
                let numbers = [1, 2, 3, 4]
                print(firstOf(numbers.slice(from: 1, to: 4)) ?? 0)
                print(firstOf(["a", "b"].slice(from: 1, to: 2)) ?? "-")
                print(total(["x", "yy", "zzz"][0...], { $0.count }))
                print(middle(numbers[0...]).toArray())
                print(middle(["x", "yy", "zzz"][0...])[0])
                print(overwrite(["x", "y", "z"], with: "z"))
                var sorted = [3, 1, 2]
                sorted.sort()
                print(sorted)
                var reversed = ["a", "b", "c"]
                reversed.reverse()
                print(reversed)
                var swapped = [1, 3, 2]
                swapped.swapAt(0, 1)
                swapped.swapAt(1, 2)
                print(swapped)
                print(Window<String> { items: ["x", "zz"][0...] }.last() ?? "-")
                var it = numbers.slice(from: 2, to: 4).makeIterator()
                print(it.next() ?? 0)
                let names = [Named { name: "p" }, Named { name: "q" }]
                print(firstOf(names.slice(from: 1, to: 2))!.name)
                print(rewritten(names[0...])[1].name)
            """);
        var functions = Unspecialised(Slices, """
                print(firstOf([1][0...]) ?? total([1][0...], { $0 }))
                print(middle([1, 2, 3][0...]).count)
                print(overwrite([1], with: 2))
                print(Window<Int> { items: [1][0...] }.last() ?? 0)
                var xs = [2, 1]
                xs.sort()
                print(xs)
                var it = xs.makeIterator()
                print(it.next() ?? 0)
            """);
        foreach (var name in new[] { "Test.firstOf", "Test.total", "Test.middle", "Test.overwrite", "Test.Window.last", "Sable.Core.Array$Slice$8.sort", "Sable.Core.ArrayIterator.next" }) {
            Assert.Contains($"{name}$generic", functions);
        }
    }

    const string Classes = """
        import Sable.Collections

        class Bag<T> {
            var items: T[>..] = []
            var label: String

            init(label: String) {
                self.label = label
            }

            fn add(_ item: T) {
                items.append(item)
            }

            fn first() -> T? {
                if items.count == 0 {
                    return none
                }
                return items[0]
            }

            fn take() -> T? {
                if items.count == 0 {
                    return none
                }
                return items.removeLast()
            }

            var size: Int => items.count

            fn describe() -> String => "\(label) of \(size)"
        }

        struct Grid<T> {
            var cells: T[>..]
            var width: Int

            subscript(x: Int, y: Int) -> T {
                get { return cells[y * width + x] }
                set { cells[y * width + x] = newValue }
            }
        }

        fn fill<T>(_ bag: Bag<T>, _ a: T, _ b: T) -> Int {
            bag.add(a)
            bag.add(b)
            return bag.size
        }

        fn corner<T>(_ grid: Grid<T>) -> T => grid[x: 0, y: 0]

        fn named<V>(_ table: [String: V], _ key: String) -> V? => table[key]
        """;

    /// <summary>
    ///     A generic class whose fields hold its type parameters' values only
    ///     behind references is laid out alike for every argument, so its
    ///     methods are compiled once, as Sable.Collections' Stack, Queue and
    ///     Deque are; so are a generic type's subscripts, a dictionary's among them.
    /// </summary>
    [Fact]
    public void MembersOfGenericClassesAndSubscriptsAreCompiledOnce() {
        Prints(
            "a\nwords of 2\nb\n3\n2\n2\ny\ny\n1\nl\nm\n1\n9\n7\n3\nnone\n",
            Classes,
            """
                let words = Bag<String>(label: "words")
                words.add("a")
                words.add("b")
                print(words.first() ?? "-")
                print(words.describe())
                print(words.take() ?? "-")
                print(fill(words, "c", "d"))
                let numbers = Bag<Int>(label: "numbers")
                print(fill(numbers, 1, 2))
                print(numbers.take() ?? 0)
                let stack = Stack<String>()
                stack.push("x")
                stack.push("y")
                print(stack.peek() ?? "-")
                print(stack.pop() ?? "-")
                let queue = Queue<Int>()
                queue.enqueue(1)
                queue.enqueue(2)
                print(queue.dequeue() ?? 0)
                let deque = Deque<String>()
                deque.append("m")
                deque.prepend("l")
                print(deque.first ?? "-")
                print(deque.removeLast() ?? "-")
                var grid = Grid<Int> { cells: [1, 2, 3, 4], width: 2 }
                print(corner(grid))
                grid[x: 1, y: 1] = 9
                print(grid[x: 1, y: 1])
                let ages = ["ann": 7, "bo": 3]
                print(named(ages, "ann") ?? 0)
                print(ages["bo"] ?? 0)
                print(named(ages, "cy") == none ? "none" : "some")
            """);
        var functions = Unspecialised(Classes, """
                let bag = Bag<Int>(label: "n")
                print(fill(bag, 1, 2))
                print(bag.first() ?? bag.take() ?? 0)
                print(bag.describe())
                var grid = Grid<Int> { cells: [1], width: 1 }
                grid[x: 0, y: 0] = 2
                print(corner(grid))
                print(named(["a": 1], "a") ?? 0)
                let stack = Stack<Int>()
                stack.push(1)
                print(stack.pop() ?? 0)
            """);
        foreach (var name in new[] { "Test.Bag.add", "Test.Bag.first", "Test.Bag.take", "Test.Bag.describe", "Test.fill", "Test.corner", "Test.named",
                     "Sable.Collections.Stack.push", "Sable.Collections.Stack.pop" }) {
            Assert.Contains($"{name}$generic", functions);
        }

        Assert.Contains(functions, static f => f.StartsWith("Test.Grid.subscript", StringComparison.Ordinal) && f.EndsWith(".set$generic", StringComparison.Ordinal));
        Assert.Contains(functions, static f => f.StartsWith("Sable.Core.Dictionary.subscript", StringComparison.Ordinal) && f.EndsWith(".get$generic", StringComparison.Ordinal));
    }

    const string Initialisers = """
        import Sable.Collections

        struct Box<T> {
            var items: T[>..]
            var label: String = "box"

            init() {
                items = []
            }

            init(one: T) {
                items = [one]
            }
        }

        fn make<T>(_ x: T) -> Box<T> {
            var box = Box<T>()
            box.items.append(x)
            return box
        }

        fn distinct<T: Hashable>(_ xs: T[>..]) -> Int {
            var seen = Set<T>()
            for x in xs {
                seen.insert(x)
            }
            return seen.count
        }

        fn tally<K: Hashable>(_ keys: K[>..]) -> [K: Int] {
            var counts: [K: Int] = [:]
            for key in keys {
                counts[key] = (counts[key] ?? 0) + 1
            }
            return counts
        }
        """;

    /// <summary>
    ///     A generic struct's own initialiser is compiled once, its fields'
    ///     initial values with it — a set's, a dictionary's, its literal's —
    ///     borrowing what it is given as every function compiled once does.
    /// </summary>
    [Fact]
    public void InitialisersOfGenericStructsAreCompiledOnce() {
        Prints(
            "[\"a\"] box\n[3]\n2\n2\n2\n1\n",
            Initialisers,
            """
                let made = make("a")
                print("\(made.items) \(made.label)")
                print(Box<Int>(one: 3).items)
                print(distinct([1, 2, 1]))
                print(distinct(["x", "y"]))
                let counts = tally(["a", "b", "a"])
                print(counts["a"] ?? 0)
                print(counts["b"] ?? 0)
            """);
        var functions = Unspecialised(Initialisers, """
                print(make(1).items)
                print(distinct([1]))
                print(tally([1])[1] ?? 0)
            """);
        foreach (var name in new[] { "Test.make", "Test.distinct", "Test.tally", "Test.Box.init", "Sable.Collections.Set.init" }) {
            Assert.Contains($"{name}$generic", functions);
        }
    }

    /// <summary>Every function a program's unspecialised build lowers: their SIL names.</summary>
    static List<string> Lowered(string declarations, string body) {
        using var _ = SilGen.UseGenerics(GenericsMode.Unspecialised);
        var result = Pipeline.Compile(new SourceText(Program(declarations, body), "test.sb"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        return [..result.Sil!.Functions.Select(static f => f.Name)];
    }

    const string Witnesses = """
        protocol Named {
            fn name() -> String
            mutating fn rename(_ to: String)
            fn throwing(_ fail: Bool) throws -> Int
        }

        extension Named {
            fn shout() -> String => name() + "!"
        }

        protocol Loud {
            fn shout() -> String
        }

        enum Failure: Error { Bad }

        struct Tag<T>: Named, Loud {
            var value: T
            var label: String

            fn name() -> String => "\(label)=\(value)"
            mutating fn rename(_ to: String) {
                label = to
            }
            fn throwing(_ fail: Bool) throws -> Int {
                if fail {
                    throw Failure.Bad
                }
                return label.count
            }
        }

        extension Int: Named {
            fn name() -> String => "int"
            mutating fn rename(_ to: String) {}
            fn throwing(_ fail: Bool) throws -> Int => 1
        }

        extension Int: Loud {}
        """;

    /// <summary>
    ///     An existential's slot whose witness is generic code compiled once —
    ///     a generic type's method, a protocol's default — calls that code with
    ///     the boxed type's metadata, and no instance is made for the slot.
    /// </summary>
    [Fact]
    public void ExistentialWitnessesCallCodeCompiledOnce() {
        const string body = """
                var named: any Named = Tag<Int> { value: 3, label: "n" }
                print(named.name())
                named.rename("m")
                print(named.name())
                print(try! named.throwing(false))
                do {
                    print(try named.throwing(true))
                } catch {
                    print("caught")
                }
                let loud: any Loud = Tag<String> { value: "s", label: "l" }
                print(loud.shout())
                let other: any Loud = 5
                print(other.shout())
            """;
        Prints("n=3\nm=3\n1\ncaught\nl=s!\nint!\n", Witnesses, body);
        var functions = Lowered(Witnesses, body);
        Assert.Contains("Test.Tag.name$generic", functions);
        Assert.Contains("Test.Named.shout$generic", functions);
        Assert.DoesNotContain(functions, static f => f.StartsWith("Test.Tag.", StringComparison.Ordinal) && f.Contains("$of$", StringComparison.Ordinal));
        Assert.DoesNotContain(functions, static f => f.StartsWith("Test.Named.shout$of$", StringComparison.Ordinal));
    }

    const string SomeParameters = """
        protocol Shape {
            fn area() -> Int
        }

        struct Square: Shape {
            var side: Int
            fn area() -> Int => side * side
        }

        struct Circle: Shape {
            var radius: Int
            fn area() -> Int => 3 * radius * radius
        }

        fn total(_ a: some Shape, _ b: some Shape) -> Int {
            return a.area() + b.area()
        }

        fn twice(_ shape: some Shape) -> Int {
            return total(shape, shape)
        }

        fn describe(_ value: some Equatable, _ other: some Hashable) -> String {
            return "\(value) \(other)"
        }
        """;

    /// <summary>
    ///     A 'some P' parameter is an anonymous type parameter: its function
    ///     is compiled once, taking its metadata and its table, as a generic
    ///     one is — and so are StringProtocol's searches over a 'some Pattern'.
    /// </summary>
    [Fact]
    public void SomeParametersAreCompiledOnce() {
        const string body = """
                print(total(Square { side: 2 }, Square { side: 1 }) + Circle { radius: 1 }.area())
                print(twice(Square { side: 3 }))
                print(describe(1, "a"))
                print("hello".contains("ll"))
                print("hello".firstIndex(of: 'l')!.utf8Offset)
                print("a,b,c".split(",").count)
                print("hello".contains(where: { $0 == 'e' }))
            """;
        Prints("8\n18\n1 a\ntrue\n2\n3\ntrue\n", SomeParameters, body);
        var functions = Unspecialised(SomeParameters, body);
        foreach (var name in new[] { "Test.total", "Test.twice", "Test.describe", "Sable.Core.StringProtocol.contains$some$Pattern", "Sable.Core.StringProtocol.split$some$Pattern$Int$Bool", "Sable.Core.StringProtocol.contains$$Char$$$$$Bool" }) {
            Assert.Contains($"{name}$generic", functions);
        }
    }

    const string Requirements = """
        protocol Counter {
            mutating fn next() -> Int
        }

        struct Up: Counter {
            var n: Int
            mutating fn next() -> Int {
                n += 1
                return n
            }
        }

        fn twice<C: Counter>(_ c: inout C) -> Int {
            return c.next() + c.next()
        }

        fn advance<C: Counter>(_ c: inout C) -> Int {
            return twice(&c)
        }

        fn at<C: Collection>(_ c: C, _ i: C.Index) -> C.Element {
            return c[i]
        }

        fn firstOf<C: Collection>(_ c: C) -> C.Element? {
            return c.first
        }

        fn mix<T: Hashable>(_ x: T, into hasher: inout Hasher) {
            hasher.combine(x)
        }

        fn sameHash<T: Hashable>(_ x: T) -> Bool {
            var a = Hasher(seed: 1)
            mix(x, into: &a)
            var b = Hasher(seed: 1)
            b.combine(x)
            return a.finalize() == b.finalize()
        }
        """;

    /// <summary>
    ///     An 'inout' parameter of a type parameter's type is the address of
    ///     its caller's storage, changed there; a protocol's subscript
    ///     requirement is read through the table, as a method is; and an
    ///     inout parameter of a known type may be changed by a method.
    /// </summary>
    [Fact]
    public void InoutParametersAndSubscriptRequirementsAreCompiledOnce() {
        const string body = """
                var up = Up { n: 0 }
                print(advance(&up))
                print(up.n)
                print(at([7, 8], 1))
                print(at("abc", "abc".startIndex))
                print(firstOf([4, 5]) ?? 0)
                print(firstOf("") ?? '-')
                let three = 3
                let nothing = ""
                print(sameHash(three))
                print(sameHash(nothing))
            """;
        Prints("3\n2\n8\na\n4\n-\ntrue\ntrue\n", Requirements, body);
        var functions = Unspecialised(Requirements, body);
        foreach (var name in new[] { "Test.twice", "Test.advance", "Test.at", "Test.firstOf", "Test.mix", "Test.sameHash" }) {
            Assert.Contains($"{name}$generic", functions);
        }

        Assert.Contains(functions, static f => f.StartsWith("Sable.Core.Collection.first", StringComparison.Ordinal));
    }

    const string Setters = """
        protocol Sized {
            var size: Int { get set }
        }

        struct Box: Sized {
            var size: Int
        }

        final class Shelf: Sized {
            var width: Int = 1
            var size: Int {
                get { return width }
                set { width = newValue * 10 }
            }
        }

        fn grow<T: Sized>(_ x: inout T) {
            x.size = x.size + 1
        }

        fn swapEnds<C: MutableCollection>(_ c: inout C) {
            let first = c.startIndex
            var last = first
            var i = first
            while i != c.endIndex {
                last = i
                i = c.index(after: i)
            }
            let kept = c[first]
            c[first] = c[last]
            c[last] = kept
        }

        fn bumped<C: MutableCollection>(_ items: C) -> C where C.Element == Int {
            var changed = items
            var i = changed.startIndex
            while i != changed.endIndex {
                changed[i] = changed[i] + 1
                i = changed.index(after: i)
            }
            return changed
        }

        struct Oops: Error {
            let code: Int
        }

        @immutable
        final class Loud: Error {
            let code: Int
            init(_ code: Int) { self.code = code }
            deinit { print("loud \(code) gone") }
        }

        fn rethrown<E: Error>(_ error: E, _ really: Bool) throws(E) -> Int {
            if really {
                throw error
            }
            return 1
        }

        fn sameLength<S: Sequence, U: Sequence>(_ s: S, _ u: U) -> Bool where S.Failure == Never, U.Failure == Never {
            var mine = s.makeIterator()
            var theirs = u.makeIterator()
            while true {
                let a = mine.next()
                let b = theirs.next()
                if a == none || b == none {
                    return a == none && b == none
                }
            }
        }
        """;

    /// <summary>
    ///     A protocol's settable property and subscript are set through the
    ///     table's entries for their setters, as code that knows the type
    ///     sets them — a struct's stored field, a class's setter, an array's
    ///     element; and 'x == none' on an associated type's optional, read
    ///     through another path, tests its case; and an error of a type
    ///     parameter's type is thrown where the caller says.
    /// </summary>
    [Fact]
    public void SetterRequirementsNoneTestsAndThrownErrorsAreCompiledOnce() {
        const string body = """
                var b = Box { size: 3 }
                grow(&b)
                print(b.size)
                var s = Shelf()
                grow(&s)
                print(s.width)
                var xs = [1, 2, 3]
                swapEnds(&xs)
                print(xs)
                var words = ["a", "b", "c", "d"]
                swapEnds(&words)
                print(words)
                print(bumped([4, 5]))
                print(sameLength([1, 2], "ab"))
                print(sameLength([1, 2], "abc"))
                do {
                    print(try rethrown(Oops { code: 1 }, false))
                    print(try rethrown(Oops { code: 2 }, true))
                } catch {
                    print("caught \(error.code)")
                }
                do {
                    print(try rethrown(Loud(3), true))
                } catch {
                    print("caught \(error.code)")
                }
                let good: Outcome<Int, Oops> = .Ok(5)
                let bad: Outcome<Int, Oops> = .Err(Oops { code: 6 })
                do {
                    print(try good.get())
                    print(try bad.get())
                } catch {
                    print("caught \(error.code)")
                }
            """;
        Prints("4\n20\n[3, 2, 1]\n[\"d\", \"b\", \"c\", \"a\"]\n[5, 6]\ntrue\nfalse\n1\ncaught 2\ncaught 3\n5\ncaught 6\nloud 3 gone\n", Setters, body);
        var functions = Unspecialised(Setters, body);
        Assert.Contains(functions, static f => f.StartsWith("Sable.Core.Outcome.get", StringComparison.Ordinal));
        foreach (var name in new[] { "Test.grow", "Test.swapEnds", "Test.bumped", "Test.sameLength", "Test.rethrown" }) {
            Assert.Contains($"{name}$generic", functions);
        }
    }

    /// <summary>
    ///     An async program's prints, both ways, on one thread — the
    ///     interleaving the interpreter's — and its own generic functions compiled once.
    /// </summary>
    static void AsyncPrints(string expected, string declarations, string body, params string[] compiledOnce) {
        var program = $"module Test\n{declarations}\n@main\nfn main() async {{\n{body}\n}}\n";
        string unspecialised;
        using (SilGen.UseGenerics(GenericsMode.Unspecialised)) {
            unspecialised = AssertAgree(program, environment: OneThread).Stdout;

            // Optimised, the specialisation pass makes copies where the types
            // are known: unoptimised, the native program runs the code compiled once.
            if (CanBuild && NativeMode.Wanted) {
                var debug = BuildAndRun(program, optimise: false, environment: OneThread);
                Assert.Equal(0, debug.ExitCode);
                Assert.Equal(unspecialised, debug.Stdout);
            }
        }

        string specialised;
        using (SilGen.UseGenerics(GenericsMode.Specialised)) {
            specialised = AssertAgree(program, environment: OneThread).Stdout;
        }

        Assert.Equal(expected, specialised);
        Assert.Equal(specialised, unspecialised);
        using var _ = SilGen.UseGenerics(GenericsMode.Unspecialised);
        var result = Pipeline.Compile(new SourceText(program, "test.sb"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        foreach (var name in compiledOnce) {
            Assert.Contains($"Test.{name}$generic", result.Sil!.Functions.Select(static f => f.Name));
        }
    }

    const string Waiting = """
        protocol Named {
            var name: String { get }
        }

        struct Tag: Named {
            var label: String
            var name: String => "tag \(label)"
        }

        final class Noisy: Named {
            let id: Int
            init(_ id: Int) { self.id = id }
            deinit { print("noisy \(id) gone") }
            var name: String => "noisy \(id)"
        }

        struct Failure: Error {
            let code: Int
        }

        fn later<T>(_ value: T) async -> T {
            await Task.yield()
            return value
        }

        fn swapped<A, B>(_ a: A, _ b: B) async -> (B, A) {
            var first = a
            let second = await later(b)
            await Task.yield()
            first = await later(first)
            return (second, first)
        }

        fn names<T: Named>(_ items: T[]) async -> String {
            var text = ""
            for item in items {
                let held = await later(item)
                await Task.yield()
                text = text + held.name + ";"
            }
            return text
        }

        fn checked<T>(_ value: T, fail: Bool) async throws(Failure) -> T {
            let kept = value
            await Task.yield()
            if fail {
                throw Failure { code: 7 }
            }
            return kept
        }

        fn sleepy<T: Named>(_ value: T) async -> String {
            let kept = value
            try? await Task.sleep(nanoseconds: 1_000_000_000_000)
            return kept.name
        }
        """;

    /// <summary>
    ///     An async function generic over its types is compiled once: its
    ///     frame keeps the metadata and tables it was given, for each time
    ///     it is resumed, and its values of a type parameter's type in
    ///     buffers sized by them after the frame's fixed part — its locals,
    ///     what it holds across an await, what it throws.
    /// </summary>
    [Fact]
    public void AsyncFunctionsAreCompiledOnceWithTheirValuesInTheFrame() =>
        AsyncPrints(
            "3\ntext\none 1\n[2, 3] noisy 1\ntag a;tag b;\nnoisy 2 gone\nnoisy 3 gone\nnoisy 2;noisy 3;\nfine\nnoisy 6 gone\nfailed 7\nnoisy 8 gone\nnoisy 8\nnoisy 1 gone\n",
            Waiting,
            """
                print(await later(3))
                print(await later("text"))
                let pair = await swapped(1, "one")
                print("\(pair.0) \(pair.1)")
                let pair2 = await swapped(Noisy(1), [2, 3])
                print("\(pair2.0) \(pair2.1.name)")
                print(await names([Tag { label: "a" }, Tag { label: "b" }]))
                print(await names([Noisy(2), Noisy(3)]))
                do {
                    print(try await checked("fine", fail: false))
                    print(try await checked(Noisy(6), fail: true).name)
                } catch {
                    print("failed \(error.code)")
                }
                let task = Task.spawn { await sleepy(Noisy(8)) }
                await Task.yield()
                task.cancel()
                print(await task.value)
            """,
            "later", "swapped", "names", "checked", "sleepy");

    const string Slots = """
        import Sable.Collections

        final class Noisy {
            let name: String
            init(_ name: String) { self.name = name }
            deinit { print("\(name) gone") }
        }

        protocol Labeled {
            var summary: String { get }
        }

        class Slot<A, B>: Labeled {
            var first: A
            var second: B?
            var pair: (A, Int)
            var tag: String = "slot"

            init(_ first: A, _ count: Int) {
                self.first = first
                second = none
                pair = (first, count)
            }

            fn put(_ b: B) {
                second = b
            }

            fn take() -> B? {
                let b = second
                second = none
                return b
            }

            var summary: String => "\(tag) \(pair.1)"
        }

        fn fill<A, B>(_ a: A, _ b: B) -> Slot<A, B> {
            let slot = Slot<A, B>(a, 2)
            slot.put(b)
            return slot
        }
        """;

    /// <summary>
    ///     A generic class whose fields hold its type parameters' values by
    ///     value is laid out at run time, from its arguments' metadata, and its
    ///     initialisers are compiled once with its methods: the object they
    ///     make is of the same class — the same metadata, which 'as?' and
    ///     'is' compare — as one code that knows its types makes, and its
    ///     fields are destroyed through their value witnesses.
    /// </summary>
    [Fact]
    public void GenericClassesAreLaidOutAtRunTimeAndMadeByCodeCompiledOnce() {
        Prints(
            "a\nb\n-\nslot 2\n1.5\nc\nsame class 7\ntrue\nd\nq\nd gone\nc gone\na gone\n",
            Slots,
            """
                let s = fill(Noisy("a"), "b")
                print(s.first.name)
                print(s.take() ?? "-")
                print(s.take() ?? "-")
                print(s.summary)
                let t = fill(1.5, Noisy("c"))
                print(t.pair.0)
                if let n = t.take() {
                    print(n.name)
                }
                let labeled: any Labeled = fill(7, 8)
                if let back = labeled as? Slot<Int, Int> {
                    print("same class \(back.first)")
                }
                let direct = Slot<Int, Int>(1, 1)
                let other: any Labeled = direct
                print(other is Slot<Int, Int>)
                let stack = Stack<Noisy>()
                stack.push(Noisy("d"))
                print(stack.pop()?.name ?? "-")
                let queue = Queue<String>()
                queue.enqueue("q")
                print(queue.dequeue() ?? "-")
            """);
        var functions = Unspecialised(Slots, """
                let s = fill(1, "b")
                print(s.take() ?? "-")
                print(s.summary)
                let stack = Stack<Int>()
                stack.push(1)
            """);
        foreach (var name in new[] { "Test.Slot.init", "Test.Slot.put", "Test.Slot.take", "Test.fill", "Sable.Collections.Stack.init" }) {
            Assert.Contains($"{name}$generic", functions);
        }
    }

    const string Debugged = """
        struct Point {
            var x: Int
            var y: Int
        }

        fn debugged<T>(_ value: T) -> String {
            return "<\(value:?)> <\(value)>"
        }

        fn waits<T>(_ value: T) async -> T {
            do {
                try await Task.sleep(nanoseconds: 1000)
            } catch {
                print("cancelled")
            }
            if Task.isCancelled {
                print("cancelled")
            }
            return value
        }
        """;

    /// <summary>
    ///     <c>"\(x:?)"</c> of a type parameter's value is written by its
    ///     type's debug describer, made beside its describer where the type
    ///     is known; and a static member of a generic type that names none of
    ///     its type parameters — <c>Task.sleep</c>, <c>Task.isCancelled</c> —
    ///     is compiled once, given none of its type's metadata.
    /// </summary>
    [Fact]
    public void DebugHolesAndStaticMembersNamingNoTypeParameterAreCompiledOnce() {
        AsyncPrints(
            "3\nx\n<\"text\"> <text>\n<42> <42>\n<Point(x: 1, y: 2)> <Point(x: 1, y: 2)>\n<[1, 2]> <[1, 2]>\n<none> <none>\n\"r\"\n",
            Debugged,
            """
                print(await waits(3))
                print(await waits("x"))
                print(debugged("text"))
                print(debugged(42))
                print(debugged(Point { x: 1, y: 2 }))
                print(debugged([1, 2]))
                let missing: Int? = none
                print(debugged(missing))
                print(String.reflecting("r"))
            """,
            "debugged", "waits");
        using var _ = SilGen.UseGenerics(GenericsMode.Unspecialised);
        var result = Pipeline.Compile(new SourceText($"module Test\n{Debugged}\n@main\nfn main() async {{\n print(await waits(1))\n print(String.reflecting(2))\n}}\n", "test.sb"));
        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        var functions = result.Sil!.Functions.Select(static f => f.Name).ToList();
        foreach (var name in new[] { "Sable.Core.Task.sleep", "Sable.Core.String.reflecting" }) {
            Assert.Contains(functions, f => f.StartsWith(name, StringComparison.Ordinal) && f.EndsWith("$generic", StringComparison.Ordinal));
        }
    }

    /// <summary>
    ///     Tasks that can never go on are dropped as the program ends: what
    ///     the frames of async functions compiled once held — values of a type
    ///     parameter's type in their buffers, a chain of calls deep — is
    ///     destroyed through its metadata, so nothing leaks.
    /// </summary>
    [Fact]
    public void AFrameCompiledOnceDroppedPartWayDestroysWhatItHolds() =>
        AsyncPrints(
            "main ends\nlevel 0 gone\nlevel 1 gone\nlevel 2 gone\n",
            """
            class Box {
                var task: Task<Int>? = none
            }

            final class Noisy {
                let name: String
                init(_ name: String) { self.name = name }
                deinit { print("\(name) gone") }
            }

            fn deep<T>(_ level: Int, _ value: T, _ blocker: Task<Int>) async -> Int {
                let mine = value
                var spare: T? = none
                if level == 0 {
                    return await blocker.value
                }
                spare = mine
                let below = await deep(level - 1, Noisy("level \(level - 1)"), blocker)
                if let _ = spare {
                    return below + 1
                }
                return below
            }
            """,
            """
                let box = Box()
                let a = Task.detached {
                    var other: Task<Int>? = none
                    unsafe {
                        other = box.task
                        box.task = none
                    }
                    return await deep(2, Noisy("level 2"), other!)
                }
                let b = Task.detached {
                    let n = await a.value
                    return n
                }
                box.task = b
                print("main ends")
            """,
            "deep");
}
