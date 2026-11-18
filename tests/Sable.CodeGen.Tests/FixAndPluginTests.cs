using Sable.Driver.Commands;
using Sable.Driver.Packages;
using Sable.Symbols;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>sable fix</c>, which applies the fixes the lints carry, and lint
///     plugins named in a manifest or on <c>sable lint</c>'s command line (#204).
/// </summary>
public sealed class FixAndPluginTests {
    const string Library = """
        module Shapes

        import Sable.Text

        public fn area(_ side: Int) -> Int {
            var result = side * side
            result = result
            return result
        }

        """;

    [Fact]
    public void FixAppliesTheFixesUntilNothingIsLeft() {
        using var fixture = new PackageFixture();
        fixture.Package("shapes", "shapes", "0.1.0", "", ("src/lib.sb", Library));

        Assert.Equal(0, PackageBuilder.Fix(fixture.Load("shapes"), fixture.Environment, new BuildRequest(), checkOnly: false));

        // The import goes, the assignment goes, and then the 'var' that no
        // longer changes is a 'let' — a second round's fix.
        var text = File.ReadAllText(Path.Combine(fixture.Root, "shapes", "src", "lib.sb"));
        Assert.Equal("module Shapes\n\npublic fn area(_ side: Int) -> Int {\n    let result = side * side\n    return result\n}\n", text);
        Assert.Contains("Fixed 3", fixture.Errors.ToString(), StringComparison.Ordinal);

        fixture.Errors.GetStringBuilder().Clear();
        Assert.Equal(0, PackageBuilder.Check(fixture.Load("shapes"), fixture.Environment, new BuildRequest()));
        Assert.DoesNotContain("warning", fixture.Errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FixCheckChangesNothingAndFails() {
        using var fixture = new PackageFixture();
        var file = Path.Combine(fixture.Package("shapes", "shapes", "0.1.0", "", ("src/lib.sb", Library)), "src", "lib.sb");

        Assert.Equal(1, PackageBuilder.Fix(fixture.Load("shapes"), fixture.Environment, new BuildRequest(), checkOnly: true));
        Assert.Equal(Library, File.ReadAllText(file));
        Assert.Contains("Remove the import", fixture.Errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FixRespectsTheLintsTable() {
        using var fixture = new PackageFixture();
        var file = Path.Combine(fixture.Package("shapes", "shapes", "0.1.0", "[lints]\nunused_import = \"allow\"\n", ("src/lib.sb", Library)), "src", "lib.sb");

        Assert.Equal(0, PackageBuilder.Fix(fixture.Load("shapes"), fixture.Environment, new BuildRequest(), checkOnly: false));
        Assert.Contains("import Sable.Text", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void FixLeavesAProgramWithErrorsAlone() {
        using var fixture = new PackageFixture();
        const string broken = "module Shapes\n\npublic fn area(_ side: Int) -> Int {\n    var unused = 1\n    return true\n}\n";
        var file = Path.Combine(fixture.Package("shapes", "shapes", "0.1.0", "", ("src/lib.sb", broken)), "src", "lib.sb");

        Assert.Equal(1, PackageBuilder.Fix(fixture.Load("shapes"), fixture.Environment, new BuildRequest(), checkOnly: false));
        Assert.Equal(broken, File.ReadAllText(file));
        Assert.Contains("does not compile", fixture.Errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void APluginNamedInTheManifestRunsWithCheck() {
        using var fixture = new PackageFixture();
        var plugin = typeof(NoTodoLint).Assembly.Location.Replace("\\", "/", StringComparison.Ordinal);
        const string source = "module Shapes\n\n// TODO: square it\npublic fn area(_ side: Int) -> Int {\n    return side\n}\n";
        fixture.Package("shapes", "shapes", "0.1.0", $"[lints]\nplugins = [\"{plugin}\"]\nno_todo = \"error\"\n", ("src/lib.sb", source));

        Assert.Equal(1, PackageBuilder.Check(fixture.Load("shapes"), fixture.Environment, new BuildRequest()));
        Assert.Contains("error[S3397]: a TODO is left in [no_todo]", fixture.Errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void APluginThatCannotBeLoadedIsAManifestError() {
        using var fixture = new PackageFixture();
        fixture.Package("shapes", "shapes", "0.1.0", "[lints]\nplugins = [\"tools/missing.dll\"]\n", ("src/lib.sb", "module Shapes\n"));

        var failure = Assert.Throws<ManifestException>(() => fixture.Load("shapes"));
        Assert.Contains("no lint plugin at", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LintListsAPluginsLints() {
        var output = new StringWriter();
        var saved = Console.Out;
        Console.SetOut(output);
        try {
            Assert.Equal(0, new LintCommand().Run(["--plugin", typeof(NoTodoLint).Assembly.Location, "--list"]));
        } finally {
            Console.SetOut(saved);
        }

        Assert.Contains("no_todo", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("S3397", output.ToString(), StringComparison.Ordinal);
    }
}

/// <summary>A plugin's lint, as a third party would write one: a TODO comment left in a file.</summary>
public sealed class NoTodoLint : Lint {
    public override string Name => "no_todo";
    public override LintLevel DefaultLevel => LintLevel.Warn;
    public override string Description => "a TODO comment";

    public override void Check(LintContext context) {
        foreach (var token in context.Tree.Root.DescendantTokens()) {
            foreach (var trivia in token.LeadingTrivia) {
                if (trivia.Kind == SyntaxKind.LineCommentTrivia && trivia.Text.Contains("TODO", StringComparison.Ordinal)) {
                    context.Report(token.Parent ?? context.Tree.Root, "a TODO is left in");
                }
            }
        }
    }
}
