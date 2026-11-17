using static Sable.CodeGen.Tests.Programs;

namespace Sable.CodeGen.Tests;

/// <summary>
///     docs/04 §2.5's exit program: a 2,000-line JSON parser and pretty
///     printer, with its own test suite inside it, compiled and run —
///     natively and in the interpreter, which must agree — and leak-checked.
/// </summary>
public sealed class JsonProgramTests {
    [Fact]
    public void TheJsonProgramPassesItsOwnTests() {
        var source = File.ReadAllText(Path.Combine(Root, "tests/programs/json.sab"));
        Assert.True(source.Split('\n').Length >= 2000, "the exit program is meant to be 2,000 lines");

        var execution = AssertAgree(source, stepLimit: 50_000_000);
        Assert.EndsWith("\n237 passed, 0 failed\n", execution.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("FAIL", execution.Stdout, StringComparison.Ordinal);
    }

    static string Root {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sable.slnx"))) {
                directory = directory.Parent;
            }

            return directory!.FullName;
        }
    }
}
