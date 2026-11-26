using Sable.Driver;
using Sable.Syntax;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Sable.Testing's <c>@mockable</c> generator: what it says of a protocol
///     it cannot make a mock of, and the protocols it can. What a mock does
///     is tests/lib/Sable.Testing/MockTests.sb's to test.
/// </summary>
public sealed class MockableTests {
    /// <summary>The errors of one file with the library's generators' output, the first line of each with its place: <c>7:10 …</c>.</summary>
    static string Errors(string source) {
        var errors = Pipeline.Check([SyntaxTree.Parse(new SourceText("module App\n\nimport Sable.Testing\n\n" + source, "app.sb"))]);
        return string.Join("\n", errors.Select(static e => {
            var lines = e.Split('\n');
            var place = lines[1].Trim().Replace("--> app.sb:", "", StringComparison.Ordinal);
            return $"{place} {lines[0][(lines[0].IndexOf(": ", StringComparison.Ordinal) + 2)..]}";
        }));
    }

    [Fact]
    public void AProtocolOfMethodsAndPropertiesIsMocked() =>
        Assert.Equal("", Errors("""
            public protocol Named {
                var name: String { get set }
            }

            @mockable
            public protocol Gateway: Named, Sendable {
                fn charge(amount: Int, _ card: String) throws -> Int?
                async fn refund(_ id: Int) throws(CancellationError)
                mutating fn reset()
                fn each(_ body: @escaping (Int) -> Void)
                fn note(_ text: @autoclosure () -> String)
            }

            fn use() -> Int?? {
                let gateway = MockGateway(.Strict)
                gateway.on.charge(amount: .any, .equal("x")).returns(3)
                gateway.on.charge(amount: 1, "y").throwing(CancellationError())
                gateway.on.setName(.matching { $0.isEmpty }).verify(.never)
                gateway.on.each().does { body in body(1) }
                return try? gateway.charge(amount: 1, "x")
            }
            """));

    [Theory]
    [InlineData("@mockable\nstruct S { }", "6:8 mockable: '@mockable' makes a mock of a protocol; 'S' is a struct")]
    [InlineData("@mockable\nprotocol P { static fn make() -> Int }", "'P' cannot be mocked: 'make' is static")]
    [InlineData("@mockable\nprotocol P { fn pick<T>(_ x: T) -> T }", "'P' cannot be mocked: 'pick' is generic")]
    [InlineData("@mockable\nprotocol P { fn add(_ x: inout Int) }", "'P' cannot be mocked: the parameter 'x' of 'add' is 'inout'")]
    [InlineData("@mockable\nprotocol P { fn sum(_ xs: Int...) -> Int }", "'P' cannot be mocked: the parameter 'xs' of 'sum' is variadic")]
    [InlineData("@mockable\nprotocol P { fn each(_ body: (Int) -> Void) }", "'P' cannot be mocked: the parameter 'body' of 'each' is a closure that does not escape")]
    [InlineData("@mockable\nprotocol P { associatedtype Item\n fn first() -> Item }", "'P' cannot be mocked: its associated type 'Item' is no type a mock can choose")]
    [InlineData("@mockable\nprotocol P { init(x: Int) }", "'P' cannot be mocked: it requires an initialiser")]
    [InlineData("protocol Q { static fn make() -> Int }\n@mockable\nprotocol P: Q { }", "'P' cannot be mocked: 'make' is static")]
    public void WhatCannotBeMockedIsSaid(string source, string message) => Assert.Contains(message, Errors(source));

    [Fact]
    public void ARefinedProtocolsRequirementIsSaidAtTheMockedOnesName() =>
        Assert.StartsWith("10:10 ", Errors("protocol Q {\n    static fn make() -> Int\n}\n\n@mockable\nprotocol P: Q { }"));

    [Fact]
    public void AVoidAGenericTypeHoldsIsNoStorageToCheck() =>
        Assert.Equal("1\n", Programs.AssertAgree("""
            module App

            struct Held<A> {
                var value: A
                var count: Int
            }

            fn main() {
                var all: Held<Void>[>..] = [Held(value: (), count: 0)]
                all[0].count += 1
                print(all[0].count)
            }
            """).Stdout);
}
