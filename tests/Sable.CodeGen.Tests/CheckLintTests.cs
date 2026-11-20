using Sable.Driver.Packages;

namespace Sable.CodeGen.Tests;

/// <summary>
///     <c>sable check</c> runs the lint set (docs/05 §5.7) over the package's
///     own files, at the levels its <c>[lints]</c> table gives.
/// </summary>
public sealed class CheckLintTests {
    const string Library = "module Shapes\n\npublic fn area(_ side: Int) -> Int {\n    let unused = 3\n    return side * side\n}\n";

    [Fact]
    public void ALintIsAWarningByDefault() {
        using var fixture = new PackageFixture();
        fixture.Package("shapes", "shapes", "0.1.0", "", ("src/lib.sb", Library));

        var code = PackageBuilder.Check(fixture.Load("shapes"), fixture.Environment, new BuildRequest());

        Assert.Equal(0, code);
        Assert.Contains("S3300", fixture.Errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLintsTableMakesItAnError() {
        using var fixture = new PackageFixture();
        fixture.Package("shapes", "shapes", "0.1.0", "[lints]\nunused_local = \"error\"\n", ("src/lib.sb", Library));

        var code = PackageBuilder.Check(fixture.Load("shapes"), fixture.Environment, new BuildRequest());

        Assert.Equal(1, code);
        Assert.Contains("S3300", fixture.Errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLintsTableCanTurnItOff() {
        using var fixture = new PackageFixture();
        fixture.Package("shapes", "shapes", "0.1.0", "[lints]\nunused_local = \"allow\"\n", ("src/lib.sb", Library));

        Assert.Equal(0, PackageBuilder.Check(fixture.Load("shapes"), fixture.Environment, new BuildRequest()));
        Assert.DoesNotContain("S3300", fixture.Errors.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     The compiler's own warnings are reported by check and build, and
    ///     by check even beside an error, where the lints do not run.
    /// </summary>
    [Fact]
    public void TheCompilersWarningsAreReported() {
        const string unreachable = "module Shapes\n\npublic fn area(_ side: Int) -> Int {\n    return side * side\n    print(side)\n}\n";
        using var fixture = new PackageFixture();
        fixture.Package("shapes", "shapes", "0.1.0", "", ("src/lib.sb", unreachable));

        Assert.Equal(0, PackageBuilder.Check(fixture.Load("shapes"), fixture.Environment, new BuildRequest()));
        Assert.Contains("warning[S6005]", fixture.Errors.ToString(), StringComparison.Ordinal);

        fixture.Errors.GetStringBuilder().Clear();
        Assert.Empty(PackageBuilder.Build(fixture.Load("shapes"), fixture.Environment, new BuildRequest())!);
        Assert.Contains("warning[S6005]", fixture.Errors.ToString(), StringComparison.Ordinal);

        fixture.Errors.GetStringBuilder().Clear();
        fixture.Write("shapes/src/lib.sb", unreachable + "\nfn broken() -> Int {\n    return true\n}\n");
        Assert.Equal(1, PackageBuilder.Check(fixture.Load("shapes"), fixture.Environment, new BuildRequest()));
        Assert.Contains("warning[S6005]", fixture.Errors.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A binary's build reports its warnings, separately compiled (dev) or
    ///     whole (release), and is built with DEBUG or RELEASE defined.
    /// </summary>
    [Fact]
    public void ABinarysBuildReportsItsWarnings() {
        if (!Programs.CanBuild) {
            return;
        }

        using var fixture = new PackageFixture();
        fixture.Package("app", "app", "0.1.0", "",
            ("src/main.sb", "module Main\n\nfn main() {\n#warning(\"look here\")\n#if DEBUG\n    print(\"debug\")\n#elseif RELEASE\n    print(\"release\")\n#endif\n}\n"));

        foreach (var release in new[] { false, true }) {
            fixture.Errors.GetStringBuilder().Clear();
            var built = PackageBuilder.Build(fixture.Load("app"), fixture.Environment, new BuildRequest(Profile: release ? "release" : "dev"));
            var binary = Assert.Single(built!);
            Assert.Contains("warning[S0030]: look here", fixture.Errors.ToString(), StringComparison.Ordinal);
            Assert.Equal((release ? "release\n" : "debug\n", 0), PackageFixture.Execute(binary.Path));
        }
    }

    [Fact]
    public void ADependencysLintsAreNotReported() {
        using var fixture = new PackageFixture();
        fixture.Package("shapes", "shapes", "0.1.0", "", ("src/lib.sb", Library));
        fixture.Package("app", "app", "0.1.0", "[dependencies]\nshapes = { path = \"../shapes\" }\n",
            ("src/main.sb", "module Main\n\nimport Shapes\n\nfn main() {\n    print(\"\\(area(2))\")\n}\n"));

        Assert.Equal(0, PackageBuilder.Check(fixture.Load("app"), fixture.Environment, new BuildRequest()));
        Assert.DoesNotContain("S3300", fixture.Errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownLintInTheManifestIsAnError() {
        using var fixture = new PackageFixture();
        fixture.Package("shapes", "shapes", "0.1.0", "[lints]\nno_such_lint = \"warn\"\n", ("src/lib.sb", Library));

        var failure = Assert.Throws<ManifestException>(() => fixture.Load("shapes"));
        Assert.Contains("no lint named 'no_such_lint'", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <c>sable check</c> keeps what it found under a key over the sources
    ///     and the lints: run again on the same, it reads the answer back
    ///     (here, one planted in the cache, to see that it is read); after an
    ///     edit, or with other lints, it checks again.
    /// </summary>
    [Fact]
    public void CheckingUnchangedSourcesAgainReadsTheAnswer() {
        using var fixture = new PackageFixture();
        fixture.Package("shapes", "shapes", "0.1.0", "", ("src/lib.sb", Library));
        Assert.Equal(0, PackageBuilder.Check(fixture.Load("shapes"), fixture.Environment, new BuildRequest()));
        var first = fixture.Errors.ToString();
        Assert.Contains("S3300", first, StringComparison.Ordinal);

        var cached = Path.Combine(fixture.Root, "shapes", "target", "check", "shapes-lib.json");
        Assert.True(File.Exists(cached));
        File.WriteAllText(cached, File.ReadAllText(cached).Replace("S3300", "S3300 (from the cache)", StringComparison.Ordinal));
        fixture.Errors.GetStringBuilder().Clear();
        Assert.Equal(0, PackageBuilder.Check(fixture.Load("shapes"), fixture.Environment, new BuildRequest()));
        Assert.Contains("S3300 (from the cache)", fixture.Errors.ToString(), StringComparison.Ordinal);

        // An edit is checked again.
        fixture.Write("shapes/src/lib.sb", Library.Replace("let unused = 3", "let unused = 4", StringComparison.Ordinal));
        fixture.Errors.GetStringBuilder().Clear();
        Assert.Equal(0, PackageBuilder.Check(fixture.Load("shapes"), fixture.Environment, new BuildRequest()));
        Assert.Contains("S3300", fixture.Errors.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("from the cache", fixture.Errors.ToString(), StringComparison.Ordinal);

        // So are other lints.
        File.AppendAllText(Path.Combine(fixture.Root, "shapes", "sable.toml"), "[lints]\nunused_local = \"error\"\n");
        fixture.Errors.GetStringBuilder().Clear();
        Assert.Equal(1, PackageBuilder.Check(fixture.Load("shapes"), fixture.Environment, new BuildRequest()));
    }
}
