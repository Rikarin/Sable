using System.Text.RegularExpressions;
using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Sable.Math.Geometry's promise that what is made of arithmetic alone is
///     the same bits everywhere: DeterminismTests.sb, whose bits are written
///     down, in the interpreter as in native code.
/// </summary>
[Native]
public sealed class GeometryTests {
    [Fact]
    public void ArithmeticIsTheSameBitsInTheInterpreter() {
        var test = File.ReadAllText(Path.Combine(Root, "tests/lib/Sable.Math.Geometry/DeterminismTests.sb"));
        var program = Regex.Replace(test, "^@test$", "", RegexOptions.Multiline)
            + "\n@main\nfn main() {\n    arithmeticIsTheSameBitsEverywhere()\n    print(\"same\")\n}\n";
        Assert.Equal("same\n", AssertAgree(program).Stdout);
    }

    static string Root {
        get {
            var directory = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(directory, "Sable.slnx"))) {
                directory = Path.GetDirectoryName(directory)!;
            }

            return directory;
        }
    }
}
