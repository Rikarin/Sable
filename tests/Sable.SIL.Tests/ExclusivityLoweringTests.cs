namespace Sable.SIL.Tests;

/// <summary>
///     Where lowering puts a dynamic exclusivity check (docs/02 §2.3): before
///     every plain access to an object's field, but a read of one declared
///     <c>let</c>, which nothing changes once it can be read.
/// </summary>
public sealed class ExclusivityLoweringTests {
    static (int Reads, int Changes) Checks(string function) {
        var compilation = Compilation.Create(
            """
            module M
            struct Pair {
                var a: Int
                var b: Int
            }
            open class Base {
                let fixed: Int
                var changing: Int
                init(_ n: Int) {
                    self.fixed = n
                    self.changing = n
                }
            }
            final class Derived: Base {
                let own: Pair
                var other: Pair
                lazy var late: Int = 3
                init() {
                    self.own = Pair(a: 1, b: 2)
                    self.other = Pair(a: 3, b: 4)
                    super.init(1)
                }
            }
            fn inherited(_ d: Derived) -> Int { return d.fixed }
            fn part(_ d: Derived) -> Int { return d.own.b }
            fn variable(_ d: Derived) -> Int { return d.changing }
            fn variablePart(_ d: Derived) -> Int { return d.other.b }
            fn change(_ d: Derived) { d.changing = 2 }
            """
        );
        Assert.DoesNotContain(compilation.GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
        var (module, diagnostics) = SilGen.Lower(compilation);
        Assert.Empty(diagnostics);
        var builtins = module.Find($"M.{function}")!.Blocks.SelectMany(static b => b.Instructions).OfType<BuiltinInst>().ToList();
        return (builtins.Count(static b => b.Name == "access_check_read"), builtins.Count(static b => b.Name == "access_check_modify"));
    }

    [Theory]
    [InlineData("inherited")]
    [InlineData("part")]
    public void AReadOfALetFieldIsNotChecked(string function) => Assert.Equal((0, 0), Checks(function));

    [Theory]
    [InlineData("variable")]
    [InlineData("variablePart")]
    public void AReadOfAVarFieldIs(string function) => Assert.Equal((1, 0), Checks(function));

    [Fact]
    public void AWriteIs() => Assert.Equal((0, 1), Checks("change"));
}
