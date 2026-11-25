using Sable.SIL;
using Sable.Syntax;
using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>@reflectable(callable)</c> (docs/06 §6.3): a type's public members
///     described, and called with boxed arguments, and the types that ask for
///     it listed by what they conform to; native and interpreted alike, in
///     both ways of compiling generic code, leak-checked.
/// </summary>
[Native]
public sealed class CallableTests {
    const string Members = """
            module Test

            struct Bad: Error { var n: Int }

            protocol Plugin {
                fn run(_ text: String) throws(Bad) -> String
            }

            @reflectable(callable)
            struct Upper: Plugin {
                public init() { }
                public fn run(_ text: String) throws(Bad) -> String {
                    if text.isEmpty { throw Bad { n: 1 } }
                    return text.uppercased()
                }
                fn hidden() { }
                private fn secret() { }
            }

            @reflectable(callable)
            class Wrap: Plugin {
                var with: String
                public init(with: String) { self.with = with }
                public fn run(_ text: String) throws(Bad) -> String => with + text + with
                public fn rename(to name: String) { with = name }
                deinit { print("bye \(with)") }
            }

            @reflectable(callable)
            public actor Tally {
                var total = 0
                public init() { }
                public fn add(_ n: Int) -> Int { total += n; return total }
                public nonisolated fn name() -> String => "tally"
            }

            @MainActor
            @reflectable(callable)
            public class Screen {
                var lines = 0
                public init() { }
                public fn show(_ text: String) -> Int { lines += 1; return lines }
                public nonisolated fn size() -> Int => 80
            }

            public class Token { }

            extension Tally {
                public fn take(_ token: Token) { }
            }

            @reflectable
            struct NotCallable: Plugin {
                public fn run(_ text: String) throws(Bad) -> String => text
            }

            @reflectable(callable)
            @derive(Equatable) struct Point {
                public var x: Int
                public var y: Int
                public init(x: Int, y: Int) { self.x = x; self.y = y }
                public mutating fn move(by d: Int, _ twice: Bool) -> Int { x += twice ? 2 * d : d; return x }
                public static fn origin() -> Point => Point(x: 0, y: 0)
                public operator + (a: Point, b: Point) -> Point => Point(x: a.x + b.x, y: a.y + b.y)
                public fn maybe(_ n: Int?) -> Int? => n == none ? none : x
                public fn names() -> String[>..] => ["x", "y"]
                public fn later(_ n: Int) async throws -> Int {
                    await Task.yield()
                    if n < 0 { throw Bad { n: n } }
                    return x + n
                }
                public mutating fn grow() async { await Task.yield(); x += 100 }
                public fn of<T>(_ t: T) -> T => t
                public fn swap(_ n: inout Int) { n = x }
                public fn with(_ plugin: any Plugin) throws -> String => try plugin.run("p")
                public fn nothing() { print("nothing \(x)") }
                public fn total(_ n: Int...) -> Int => n.count
            }

            extension Point {
                public fn doubled() -> Point => Point(x: x * 2, y: y * 2)
            }

            @reflectable(callable)
            public enum Colour {
                Red
                Green
                public fn next() -> Colour => self == .Red ? .Green : .Red
            }

            @reflectable(callable)
            struct Box<T> {
                var item: T
                public init(_ item: T) { self.item = item }
                public mutating fn put(_ t: T) -> T { let old = item; item = t; return old }
            }

            fn say(_ what: () throws -> any Any) {
                do {
                    let value = try what()
                    if let n = value as? Int { print("\(n)") }
                    else if let s = value as? String { print(s) }
                    else if let p = value as? Point { print("(\(p.x), \(p.y))") }
                    else if let o = value as? Int? { print(o == none ? "none" : "some \(o!)") }
                    else if let a = value as? String[>..] { print(a.joined(separator: ",")) }
                    else { print(type(of: value).name) }
                } catch let e as CallError {
                    print("! \(e.description())")
                } catch let e as Bad {
                    print("bad \(e.n)")
                } catch {
                    print("other")
                }
            }

            @main
            fn main() async {
                for m in Point.callableMembers() {
                    print("\(m.signature) \(m.isCallable ? "" : "-- \(m.refusal!)")")
                }

                var p: any Any = Point.origin()
                let t = type(of: p)
                say { try t.callable("init")!.call(3, 4) }
                say { try t.callable("init")!.call(3) }
                say { try t.callable("init")!.call(3, "4") }
                say { try t.callable("move")!.call(mutating: &p, 5, true) }
                say { try t.callable("move")!.call(on: p, 5, true) }
                say { try t.callable("move")!.call(5, true) }
                say { try t.callable("origin")!.call() }
                say { try t.callable("+")!.call(p, p) }
                say { try t.callable("maybe")!.call(on: p, none as Int?) }
                say { try t.callable("maybe")!.call(on: p, 1 as Int?) }
                say { try t.callable("names")!.call(on: p) }
                say { try t.callable("names")!.call(on: 3) }
                say { try t.callable("later")!.call(on: p, 1) }
                say { try t.callable("of")!.call(on: p, 1) }
                say { try t.callable("with")!.call(on: p, Upper()) }
                say { try t.callable("with")!.call(on: p, 3) }
                say { try t.callable("nothing")!.call(on: p) }
                say { try t.callable("doubled")!.call(on: p) }

                do {
                    print("\(try await t.callable("later")!.callAsync(on: p, 5) as! Int)")
                    _ = try await t.callable("grow")!.callAsync(mutating: &p)
                    print("\((p as! Point).x)")
                    print("\(try await t.callable("doubled")!.callAsync(on: p) as! Point == Point(x: 220, y: 0))")
                    _ = try await t.callable("later")!.callAsync(on: p, -3)
                } catch let e as Bad {
                    print("bad \(e.n)")
                } catch {
                    print("unexpected")
                }

                let stars: (any Any)[>..] = ["*"]
                let plugins: Type = (any Plugin).self
                for type in plugins.callableSubtypes() {
                    print(type.name)
                    say {
                        let made = try type.callable("init")!.call(with: stars[0..<type.callable("init")!.parameterTypes.count])
                        say { try type.callable("run")!.call(on: made, "") }
                        say { try type.callable("run")!.call(on: made, "hi") }
                        return try (made as! any Plugin).run("x")
                    }
                }

                for m in Upper.callableMembers() { print(m.name) }
                do {
                    let tally = try Tally.callable("init")!.call()
                    _ = try await type(of: tally).callable("add")!.callAsync(on: tally, 3)
                    print("\(try await type(of: tally).callable("add")!.callAsync(on: tally, 4) as! Int)")
                    print(try type(of: tally).callable("name")!.call(on: tally) as! String)
                    say { try type(of: tally).callable("add")!.call(on: tally, 4) }
                    print(Tally.callable("take")!.refusal!)
                    let screen = try Screen.callable("init")!.call()
                    print("\(Screen.callable("show")!.isAsync) \(Screen.callable("size")!.isAsync)")
                    print("\(try await Screen.callable("show")!.callAsync(on: screen, "a") as! Int + try Screen.callable("size")!.call(on: screen) as! Int)")
                    let next = try Colour.callable("next")!.call(on: Colour.Red)
                    print("\(next as! Colour == .Green)")
                    var box = try Box<String>.callable("init")!.call("a")
                    print(try Box<String>.callable("put")!.call(mutating: &box, "b") as! String + (box as! Box<String>).item)
                    say { try Box<String>.callable("put")!.call(mutating: &box, 1) }
                    print("\(type(of: 3).callableMembers.count) \(type(of: box).callableMembers.count)")
                } catch {
                    print("unexpected \(error)")
                }
            }
        """;

    const string Expected =
        "init(x: Int, y: Int) \n"
        + "mutating fn move(by d: Int, _ twice: Bool) -> Int \n"
        + "static fn origin() -> Point \n"
        + "operator + (a: Point, b: Point) -> Point \n"
        + "fn maybe(_ n: Int?) -> Int? \n"
        + "fn names() -> String[>..] \n"
        + "fn later(_ n: Int) async throws -> Int \n"
        + "mutating fn grow() async \n"
        + "fn of(_ t: T) -> T -- it is generic over type parameters of its own\n"
        + "fn swap(_ n: inout Int) -- it changes its argument 'n'\n"
        + "fn with(_ plugin: any Plugin) throws -> String \n"
        + "fn nothing() \n"
        + "fn total(_ n: Int...) -> Int -- its parameter 'n' is variadic\n"
        + "fn doubled() -> Point \n"
        + "(3, 4)\n"
        + "! it takes 2 arguments and was given 1\n"
        + "! argument 1 is 'Int', and was given 'String'\n"
        + "10\n"
        + "! it is mutating: 'call(mutating:)' calls it\n"
        + "! it is a method, called on a value: 'call(on:)' calls it\n"
        + "(0, 0)\n"
        + "(20, 0)\n"
        + "none\n"
        + "some 10\n"
        + "x,y\n"
        + "! it is called on 'Test.Point', not 'Int'\n"
        + "! its call waits: 'callAsync' calls it\n"
        + "! it cannot be called with boxed arguments: it is generic over type parameters of its own\n"
        + "P\n"
        + "! argument 0 is 'any Test.Plugin', and was given 'Int'\n"
        + "nothing 10\n"
        + "()\n"
        + "(20, 0)\n"
        + "15\n"
        + "110\n"
        + "true\n"
        + "bad -3\n"
        + "Test.Upper\n"
        + "bad 1\n"
        + "HI\n"
        + "X\n"
        + "Test.Wrap\n"
        + "**\n"
        + "*hi*\n"
        + "bye *\n"
        + "*x*\n"
        + "init\n"
        + "run\n"
        + "7\n"
        + "tally\n"
        + "! its call waits: 'callAsync' calls it\n"
        + "its parameter 'token' is of type 'Token', which is not Sendable, and it is isolated to its actor\n"
        + "true false\n"
        + "81\n"
        + "true\n"
        + "ab\n"
        + "! argument 0 is 'String', and was given 'Int'\n"
        + "0 0\n"
        + "";

    [Theory]
    [InlineData(GenericsMode.Specialised)]
    [InlineData(GenericsMode.Unspecialised)]
    public void PublicMembersAreCalledWithBoxedArguments(GenericsMode mode) {
        using var _ = SilGen.UseGenerics(mode);
        Assert.Equal(Expected, AssertAgree(Members).Stdout);
        if (CanBuild) {
            var separate = BuildSeparatelyAndRun(Members);
            Assert.Equal((Expected, 0), (separate.Stdout + separate.Stderr, separate.ExitCode));
        }
    }

    /// <summary>A program none of whose types asks holds none of it: nothing registered, no descriptor, no member kept for it.</summary>
    [Fact]
    public void AProgramThatDoesNotAskHoldsNoneOfIt() {
        const string Plain = """
            module Test

            @reflectable
            struct Point {
                public var x: Int
                public fn doubled() -> Int => x * 2
                public fn unused() -> Int => x * 3
            }

            @main
            fn main() {
                print(TypeInfo.of(Point.self).name)
            }
            """;
        SilModule Compiled(string source) => Pipeline.Compile(new SourceText(source, "test.sb"), optimised: true).Sil!;
        HashSet<string> Kept(string source) => SilPassManager.Reachable(Compiled(source))!;

        Assert.Empty(Compiled(Plain).Callables);
        Assert.DoesNotContain(Kept(Plain), static f => f.Contains("allable", StringComparison.Ordinal));

        // One that asks, and whose members nothing looks for, keeps none either when compiled whole.
        var asking = Plain.Replace("@reflectable", "@reflectable(callable)");
        Assert.Single(Compiled(asking).Callables);
        Assert.DoesNotContain(Kept(asking), static f => f.Contains("allable", StringComparison.Ordinal));

        // One that does, its members.
        var using_ = asking.Replace("print(TypeInfo", "print(\"\\((Point.self as Type).callableMembers.count)\")\n    print(TypeInfo");
        Assert.Contains(Kept(using_), static f => f == "$callable$Test.Point");
    }

    [Fact]
    public void AnEmbeddedBuildHasNone() {
        var result = Pipeline.Compile([SyntaxTree.Parse(new SourceText("""
            module Test

            @reflectable(callable)
            struct Point {
                public fn x() -> Int => 1
            }

            fn main() { }
            """, "test.sb"))], denyReflection: true);
        Assert.Contains(result.Errors, static e => e.Contains("S3450", StringComparison.Ordinal));
    }
}
