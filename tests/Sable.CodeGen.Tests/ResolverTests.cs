using Sable.Driver.Packages;
using Xunit.Abstractions;

namespace Sable.CodeGen.Tests;

/// <summary>
///     The PubGrub resolver, on the examples of pub's solver documentation
///     and the design document's own, and the explanations it gives.
/// </summary>
public sealed class ResolverTests(ITestOutputHelper output) {
    /// <summary>Packages in memory: name → version → dependency → requirement.</summary>
    sealed class Source(Dictionary<string, Dictionary<string, Dictionary<string, string>>> packages) : IPackageSource {
        public IReadOnlyList<SemVersion> Versions(string package) =>
            packages.TryGetValue(package, out var versions) ? [..versions.Keys.Select(SemVersion.Parse)] : [];

        public IReadOnlyList<(string Package, VersionReq Requirement)> Dependencies(string package, SemVersion version) =>
            [..packages[package].First(v => SemVersion.Parse(v.Key) == version).Value.Select(static d => (d.Key, VersionReq.Parse(d.Value)))];
    }

    static Dictionary<string, string> Deps(params string[] pairs) =>
        pairs.Select(static p => p.Split(' ', 2)).ToDictionary(static p => p[0], static p => p[1]);

    static Dictionary<string, Dictionary<string, string>> Package(params (string Version, Dictionary<string, string> Dependencies)[] versions) =>
        versions.ToDictionary(static v => v.Version, static v => v.Dependencies);

    static IReadOnlyDictionary<string, SemVersion> Solve(Dictionary<string, Dictionary<string, Dictionary<string, string>>> packages, IReadOnlyDictionary<string, SemVersion>? preferred = null) =>
        Resolver.Resolve(new Source(packages), "root", SemVersion.Parse("1.0.0"), preferred);

    string Failure(Dictionary<string, Dictionary<string, Dictionary<string, string>>> packages) {
        var message = Assert.Throws<ResolutionException>(() => Solve(packages)).Message;
        output.WriteLine(message);
        return message;
    }

    static void Chosen(IReadOnlyDictionary<string, SemVersion> solution, params string[] expected) {
        var actual = solution.Where(static p => p.Key != "root").Select(static p => $"{p.Key} {p.Value}").Order(StringComparer.Ordinal);
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    [Fact]
    public void NoConflicts() {
        Chosen(Solve(new() {
            ["root"] = Package(("1.0.0", Deps("foo ^1.0.0"))),
            ["foo"] = Package(("1.0.0", Deps("bar ^1.0.0"))),
            ["bar"] = Package(("1.0.0", Deps()), ("2.0.0", Deps())),
        }), "foo 1.0.0", "bar 1.0.0");
    }

    [Fact]
    public void AvoidingAConflictWhileDeciding() {
        Chosen(Solve(new() {
            ["root"] = Package(("1.0.0", Deps("foo ^1.0.0", "bar ^1.0.0"))),
            ["foo"] = Package(("1.1.0", Deps("bar ^2.0.0")), ("1.0.0", Deps())),
            ["bar"] = Package(("1.0.0", Deps()), ("1.1.0", Deps()), ("2.0.0", Deps())),
        }), "foo 1.0.0", "bar 1.1.0");
    }

    [Fact]
    public void PerformingConflictResolution() {
        Chosen(Solve(new() {
            ["root"] = Package(("1.0.0", Deps("foo >=1.0.0"))),
            ["foo"] = Package(("2.0.0", Deps("bar ^1.0.0")), ("1.0.0", Deps())),
            ["bar"] = Package(("1.0.0", Deps("foo ^1.0.0"))),
        }), "foo 1.0.0");
    }

    [Fact]
    public void ConflictResolutionWithAPartialSatisfier() {
        Chosen(Solve(new() {
            ["root"] = Package(("1.0.0", Deps("foo ^1.0.0", "target ^2.0.0"))),
            ["foo"] = Package(("1.1.0", Deps("left ^1.0.0", "right ^1.0.0")), ("1.0.0", Deps())),
            ["left"] = Package(("1.0.0", Deps("shared >=1.0.0"))),
            ["right"] = Package(("1.0.0", Deps("shared <2.0.0"))),
            ["shared"] = Package(("2.0.0", Deps()), ("1.0.0", Deps("target ^1.0.0"))),
            ["target"] = Package(("2.0.0", Deps()), ("1.0.0", Deps())),
        }), "foo 1.0.0", "target 2.0.0");
    }

    [Fact]
    public void CircularDependenciesResolve() {
        Chosen(Solve(new() {
            ["root"] = Package(("1.0.0", Deps("foo 1.0.0"))),
            ["foo"] = Package(("1.0.0", Deps("bar 1.0.0"))),
            ["bar"] = Package(("1.0.0", Deps("foo 1.0.0"))),
        }), "foo 1.0.0", "bar 1.0.0");
    }

    [Fact]
    public void TheLockFilesVersionIsPreferredWhileItFits() {
        var packages = new Dictionary<string, Dictionary<string, Dictionary<string, string>>> {
            ["root"] = Package(("1.0.0", Deps("foo ^1.0.0"))),
            ["foo"] = Package(("1.0.0", Deps()), ("1.1.0", Deps()), ("1.2.0", Deps()), ("2.0.0", Deps())),
        };

        Chosen(Solve(packages), "foo 1.2.0");
        Chosen(Solve(packages, new Dictionary<string, SemVersion> { ["foo"] = SemVersion.Parse("1.1.0") }), "foo 1.1.0");
        Chosen(Solve(packages, new Dictionary<string, SemVersion> { ["foo"] = SemVersion.Parse("2.0.0") }), "foo 1.2.0");
    }

    [Fact]
    public void APreReleaseIsChosenOnlyWhenARequirementNamesOne() {
        Chosen(Solve(new() {
            ["root"] = Package(("1.0.0", Deps("foo >=1.0.0"))),
            ["foo"] = Package(("1.0.0", Deps()), ("2.0.0-beta.1", Deps())),
        }), "foo 1.0.0");

        Chosen(Solve(new() {
            ["root"] = Package(("1.0.0", Deps("foo ^2.0.0-beta.1"))),
            ["foo"] = Package(("1.0.0", Deps()), ("2.0.0-beta.1", Deps())),
        }), "foo 2.0.0-beta.1");
    }

    [Fact]
    public void LinearErrorReporting() {
        var message = Failure(new() {
            ["root"] = Package(("1.0.0", Deps("foo ^1.0.0", "baz ^1.0.0"))),
            ["foo"] = Package(("1.0.0", Deps("bar ^2.0.0"))),
            ["bar"] = Package(("2.0.0", Deps("baz ^3.0.0"))),
            ["baz"] = Package(("1.0.0", Deps()), ("3.0.0", Deps())),
        });

        Assert.Equal("""
            error: cannot resolve dependencies

              Because foo depends on bar ^2.0.0 which depends on baz ^3.0.0, foo requires baz ^3.0.0.
              So, because root depends on both foo ^1.0.0 and baz ^1.0.0, resolution failed.

            note: a dependency graph holds one major version of each package, so baz ^1.0.0 and baz ^3.0.0 cannot both be used (docs/05 §5.3)

            """, message);
    }

    [Fact]
    public void BranchingErrorReporting() {
        var message = Failure(new() {
            ["root"] = Package(("1.0.0", Deps("foo ^1.0.0"))),
            ["foo"] = Package(("1.0.0", Deps("a ^1.0.0", "b ^1.0.0")), ("1.1.0", Deps("x ^1.0.0", "y ^1.0.0"))),
            ["a"] = Package(("1.0.0", Deps("b ^2.0.0"))),
            ["b"] = Package(("1.0.0", Deps()), ("2.0.0", Deps())),
            ["x"] = Package(("1.0.0", Deps("y ^2.0.0"))),
            ["y"] = Package(("1.0.0", Deps()), ("2.0.0", Deps())),
        });

        // Both branches are explained, the first numbered so the second can refer to it.
        Assert.Equal("""
            error: cannot resolve dependencies

                  Because foo <1.1.0 depends on a ^1.0.0 which depends on b ^2.0.0, foo <1.1.0 requires b ^2.0.0.
              (1) So, because foo <1.1.0 depends on b ^1.0.0, foo <1.1.0 is forbidden.

                  Because foo >=1.1.0 depends on x ^1.0.0 which depends on y ^2.0.0, foo >=1.1.0 requires y ^2.0.0.
                  And because foo >=1.1.0 depends on y ^1.0.0, foo >=1.1.0 is forbidden.
                  And because foo <1.1.0 is forbidden (1), foo is forbidden.
                  So, because root depends on foo ^1.0.0, resolution failed.

            note: a dependency graph holds one major version of each package, so b ^1.0.0 and b ^2.0.0 cannot both be used (docs/05 §5.3)

            """, message);
    }

    [Fact]
    public void TheDesignDocumentsExample() {
        // docs/05 §5.3: image needs color-space 2, platform 1.4 needs 1.8.
        var message = Failure(new() {
            ["root"] = Package(("1.0.0", Deps("image ^0.6", "platform ^1.4"))),
            ["image"] = Package(("0.6.0", Deps("color-space ^2.0")), ("0.6.1", Deps("color-space ^2.0"))),
            ["platform"] = Package(("1.4.0", Deps("color-space ^1.8")), ("2.0.0-beta.1", Deps("color-space ^2.0"))),
            ["color-space"] = Package(("1.8.0", Deps()), ("1.9.0", Deps()), ("2.0.0", Deps()), ("2.1.0", Deps())),
        });

        Assert.Contains("image depends on color-space ^2.0.0", message, StringComparison.Ordinal);
        Assert.Contains("platform <2.0.0-beta.1 depends on color-space ^1.8.0", message, StringComparison.Ordinal);
        Assert.Contains("root depends on both image ^0.6.0 and platform ^1.4.0", message, StringComparison.Ordinal);
        Assert.Contains("resolution failed", message, StringComparison.Ordinal);
        Assert.Contains("one major version of each package", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingPackageIsNamed() {
        var message = Failure(new() {
            ["root"] = Package(("1.0.0", Deps("foo ^1.0.0"))),
            ["foo"] = Package(("1.0.0", Deps("ghost ^1.0.0"))),
        });

        Assert.Contains("foo depends on ghost ^1.0.0 which doesn't exist", message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoMatchingVersionIsNamed() {
        var message = Failure(new() {
            ["root"] = Package(("1.0.0", Deps("foo ^3.0.0"))),
            ["foo"] = Package(("1.0.0", Deps()), ("2.0.0", Deps())),
        });

        Assert.Contains("Because root depends on foo ^3.0.0 which doesn't match any versions, resolution failed.", message, StringComparison.Ordinal);
    }
}
