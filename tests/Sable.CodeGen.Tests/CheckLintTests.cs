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
}
