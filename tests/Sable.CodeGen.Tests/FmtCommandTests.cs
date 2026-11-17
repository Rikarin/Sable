using Sable.Driver.Commands;

namespace Sable.CodeGen.Tests;

/// <summary><c>sable fmt</c> on a directory of files: what it changes, what it skips, what <c>--check</c> says.</summary>
[Collection("Console")]
public sealed class FmtCommandTests : IDisposable {
    readonly string _root = Directory.CreateTempSubdirectory("sable-fmt-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string Write(string relative, string text) {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    static (int Exit, string Output, string Errors) Run(params string[] arguments) {
        var output = new StringWriter();
        var errors = new StringWriter();
        var (oldOut, oldError) = (Console.Out, Console.Error);
        Console.SetOut(output);
        Console.SetError(errors);
        try {
            return (new FmtCommand().Run(arguments), output.ToString(), errors.ToString());
        } finally {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
        }
    }

    [Fact]
    public void FormatsADirectoryInPlaceSkippingTargetAndHiddenDirectories() {
        var messy = Write("src/main.sab", "module M\nfn f(){let x=1}");
        var tidy = Write("src/tidy.sab", "module M\n\nfn g() { }\n");
        var built = Write("target/gen.sab", "module M\nfn f(){}");
        var hidden = Write(".cache/x.sab", "module M\nfn f(){}");
        var other = Write("notes.txt", "fn f(){}");

        var (exit, _, errors) = Run(_root);

        Assert.Equal(0, exit);
        Assert.Equal("", errors);
        Assert.Equal("module M\nfn f() {\n    let x = 1\n}\n", File.ReadAllText(messy));
        Assert.Equal("module M\n\nfn g() { }\n", File.ReadAllText(tidy));
        Assert.Equal("module M\nfn f(){}", File.ReadAllText(built));
        Assert.Equal("module M\nfn f(){}", File.ReadAllText(hidden));
        Assert.Equal("fn f(){}", File.ReadAllText(other));
    }

    [Fact]
    public void CheckListsUnformattedFilesAndChangesNothing() {
        var messy = Write("a.sab", "module M\nfn f(){}");
        Write("b.sab", "module M\n\nfn g() { }\n");

        var (exit, output, _) = Run("--check", _root);

        Assert.Equal(1, exit);
        Assert.Equal(messy + Environment.NewLine, output);
        Assert.Equal("module M\nfn f(){}", File.ReadAllText(messy));

        Run(_root);
        Assert.Equal(0, Run("--check", _root).Exit);
    }

    [Fact]
    public void RefusesAFileWithSyntaxErrors() {
        var broken = Write("broken.sab", "module M\nfn f( {");
        var messy = Write("fine.sab", "module M\nfn f(){}");

        var (exit, _, errors) = Run(_root);

        Assert.Equal(1, exit);
        Assert.Contains("has syntax errors", errors, StringComparison.Ordinal);
        Assert.Equal("module M\nfn f( {", File.ReadAllText(broken));
        Assert.Equal("module M\nfn f() { }\n", File.ReadAllText(messy));
    }

    [Fact]
    public void AFileNamedDirectlyIsFormattedAndAMissingOneIsAnError() {
        var file = Write("one.sab", "module M\nfn f(){}");
        Assert.Equal(0, Run(file).Exit);
        Assert.Equal("module M\nfn f() { }\n", File.ReadAllText(file));
        Assert.Equal(1, Run(Path.Combine(_root, "missing.sab")).Exit);
    }
}
