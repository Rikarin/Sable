using Sable.Driver.Packages;

namespace Sable.CodeGen.Tests;

/// <summary>Versions, version sets and Cargo-style requirements.</summary>
public sealed class SemverTests {
    static SemVersion V(string text) => SemVersion.Parse(text);

    [Fact]
    public void VersionsOrderAsSemverSays() {
        string[] ordered = ["0.9.9", "1.0.0-0", "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0"];
        for (var i = 0; i + 1 < ordered.Length; i++) {
            Assert.True(V(ordered[i]) < V(ordered[i + 1]), $"{ordered[i]} < {ordered[i + 1]}");
        }

        Assert.Equal(V("1.0.0+build.5"), V("1.0.0"));
        Assert.Equal("1.2.3-rc.1+meta", V("1.2.3-rc.1+meta").ToString());
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-01")]
    public void NotVersions(string text) => Assert.False(SemVersion.TryParse(text, out _, out _));

    [Theory]
    // Caret, written or implied: compatible updates.
    [InlineData("1.2.3", "1.2.3 1.9.0", "1.2.2 2.0.0 2.0.0-rc.1")]
    [InlineData("^1.2", "1.2.0 1.99.0", "1.1.9 2.0.0")]
    [InlineData("1", "1.0.0 1.5.0", "0.9.0 2.0.0")]
    [InlineData("0.2.3", "0.2.3 0.2.9", "0.3.0 0.2.2")]
    [InlineData("0.0.3", "0.0.3", "0.0.4 0.0.2")]
    [InlineData("^0.0", "0.0.0 0.0.9", "0.1.0")]
    [InlineData("0", "0.0.0 0.9.0", "1.0.0")]
    // Tilde: patch updates.
    [InlineData("~1.2.3", "1.2.3 1.2.9", "1.3.0 1.2.2")]
    [InlineData("~1.2", "1.2.0 1.2.9", "1.3.0")]
    [InlineData("~1", "1.0.0 1.9.0", "2.0.0")]
    // Exact, comparisons and wildcards.
    [InlineData("=1.2.3", "1.2.3", "1.2.4")]
    [InlineData("=1.2", "1.2.0 1.2.7", "1.3.0")]
    [InlineData(">=1.2", "1.2.0 9.0.0", "1.1.9")]
    [InlineData(">1.2", "1.3.0", "1.2.9")]
    [InlineData(">1.2.3", "1.2.4", "1.2.3")]
    [InlineData("<2", "1.9.9", "2.0.0 2.0.0-beta")]
    [InlineData("<=1.2", "1.2.9", "1.3.0")]
    [InlineData("*", "0.0.1 9.9.9", "")]
    [InlineData("1.*", "1.0.0 1.9.0", "2.0.0")]
    [InlineData("1.2.*", "1.2.0 1.2.9", "1.3.0")]
    // Comma-joined: all at once.
    [InlineData(">=1.2, <1.5", "1.2.0 1.4.9", "1.5.0 1.1.0")]
    // Pre-releases only when named.
    [InlineData("^2.0.0-beta.1", "2.0.0-beta.1 2.0.0-beta.2 2.0.0 2.1.0", "2.0.0-alpha 3.0.0-rc.1")]
    [InlineData("^1.0", "1.0.0", "1.1.0-beta")]
    public void Requirements(string requirement, string matching, string notMatching) {
        var req = VersionReq.Parse(requirement);
        foreach (var version in matching.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
            Assert.True(req.Matches(V(version)), $"'{requirement}' should match {version}");
        }

        foreach (var version in notMatching.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
            Assert.False(req.Matches(V(version)), $"'{requirement}' should not match {version}");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData(">=1.2.*")]
    [InlineData("1.*.3")]
    [InlineData("1.2,")]
    [InlineData("^1.2-beta")]
    public void NotRequirements(string text) => Assert.False(VersionReq.TryParse(text, out _, out _));

    /// <summary>
    ///     Found by the resolver fuzzer: the '-' was looked for before build
    ///     metadata was cut off, so one inside it was past the end of what was
    ///     left, and parsing threw.
    /// </summary>
    [Fact]
    public void ADashInBuildMetadataIsNotAPrerelease() {
        Assert.True(VersionReq.TryParse("1.0.0+b-2", out var req, out var error), error);
        Assert.True(req.Matches(V("1.0.0")));
        Assert.False(req.MentionsPrerelease);
        Assert.False(VersionReq.TryParse("1+27X-.a>8", out _, out _));
    }

    /// <summary>
    ///     Found by the resolver fuzzer: a range ending past the largest number
    ///     wrapped round to a negative one, so '~1.9223372036854775807.0'
    ///     matched nothing, not even 1.9223372036854775807.0, without a word.
    /// </summary>
    [Theory]
    [InlineData("^9223372036854775807")]
    [InlineData("~1.9223372036854775807.0")]
    [InlineData("0.0.9223372036854775807")]
    [InlineData("<=1.9223372036854775807")]
    public void ARangePastTheLargestNumberIsAnError(string text) {
        Assert.False(VersionReq.TryParse(text, out _, out var error));
        Assert.Contains("too large", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLargestNumberIsFineWhereNoRangeEndsPastIt() {
        Assert.True(VersionReq.Parse(">=9223372036854775807.0.0").Matches(V("9223372036854775807.0.0")));
        Assert.True(VersionReq.Parse("=1.2.9223372036854775807").Matches(V("1.2.9223372036854775807")));
    }

    [Fact]
    public void SetsAreClosedUnderTheBooleanOperations() {
        var a = VersionReq.Parse("^1.2").Set;
        var b = VersionReq.Parse(">=1.5, <3").Set;

        var both = a.Intersect(b);
        Assert.True(both.Contains(V("1.5.0")));
        Assert.False(both.Contains(V("1.4.0")));
        Assert.False(both.Contains(V("2.1.0")));

        var either = a.Union(b);
        Assert.True(either.Contains(V("1.2.0")) && either.Contains(V("2.5.0")));

        var neither = either.Complement();
        Assert.True(neither.Contains(V("1.0.0")) && neither.Contains(V("3.0.0")));
        Assert.False(neither.Contains(V("2.0.0")));
        Assert.True(neither.Complement().Equals(either));

        Assert.True(VersionSet.Any.Complement().IsEmpty);
        Assert.True(VersionSet.None.Complement().IsAny);
        Assert.True(both.IsSubsetOf(a));
        Assert.True(a.Difference(b).IsDisjointFrom(b));
    }

    [Theory]
    [InlineData("1.2", "^1.2.0")]
    [InlineData("0.6", "^0.6.0")]
    [InlineData("~1.2.3", "~1.2.3")]
    [InlineData("=1.4.0", "1.4.0")]
    [InlineData(">=1.0, <1.5", ">=1.0.0, <1.5.0")]
    [InlineData("*", "any version")]
    public void SetsAreWrittenLikeRequirements(string requirement, string written) =>
        Assert.Equal(written, VersionReq.Parse(requirement).Set.ToString());
}
