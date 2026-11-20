using Sable.Driver;
using Sable.Syntax;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>unsafe fn</c> (SYNTAX.md §23, #264): called from unsafe code, it runs
///     as any function does; <c>--deny-unsafe</c> counts it as it counts an
///     <c>unsafe</c> block.
/// </summary>
[Native]
public sealed class UnsafeTests {
    const string Program = """
        module Main

        struct Raw {
            var n: Int

            unsafe static fn make(_ n: Int) -> Raw {
                return Raw(n: n)
            }

            unsafe fn peek() -> Int {
                return n
            }
        }

        unsafe fn twice(_ n: Int) -> Int {
            return n * 2 + Raw.make(1).peek()
        }

        fn main() {
            unsafe {
                let r = Raw.make(3)
                print(r.peek())
                let g = twice
                print(g(2))
                let h = () => twice(4)
                print(h())
            }
        }
        """;

    [Fact]
    public void AnUnsafeFunctionCalledFromUnsafeCodeRuns() =>
        Assert.Equal("3\n5\n9\n", Programs.AssertAgree(Program).Stdout);

    [Fact]
    public void DenyingUnsafeCountsEachUnsafeFunctionAndBlock() {
        var denied = Pipeline.UnsafeDenied([SyntaxTree.Parse(new SourceText(Program, "main.sb"))]);
        Assert.Equal(4, denied.Count);
        Assert.All(denied, static e => Assert.StartsWith("error[S3472]", e, StringComparison.Ordinal));
        Assert.Equal(3, denied.Count(static e => e.Contains("'unsafe fn'", StringComparison.Ordinal)));
        Assert.Single(denied, static e => e.Contains("an 'unsafe' block", StringComparison.Ordinal));
        Assert.Empty(Pipeline.UnsafeDenied([SyntaxTree.Parse(new SourceText("module Main\n\nfn main() {\n    print(1)\n}\n", "main.sb"))]));
    }

    [Fact]
    public void APackageBuiltDenyingUnsafeRefusesItsOwnUnsafeFunction() {
        using var fixture = new PackageFixture();
        fixture.Package("app", "app", "0.1.0", "", ("src/main.sb", Program));
        Assert.Null(Sable.Driver.Packages.PackageBuilder.Build(fixture.Load("app"), fixture.Environment, new() { DenyUnsafe = true }));
        Assert.Contains("S3472", fixture.Errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("'unsafe fn' 'twice'", fixture.Errors.ToString(), StringComparison.Ordinal);
    }
}
