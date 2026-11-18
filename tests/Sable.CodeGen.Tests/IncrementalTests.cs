using System.Text.RegularExpressions;
using Sable.Driver.Build;
using Sable.Driver.Packages;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Incremental builds (docs/05 §5.4): after each kind of edit, a build that
///     reuses what it can makes a program that behaves as a clean build of the
///     same sources does — compiled whole, from nothing — and an unchanged
///     build does nothing.
/// </summary>
public sealed class IncrementalTests {
    static readonly bool CanBuild = NativeBuild.IsAvailable();

    /// <summary>
    ///     Three packages: <c>shapes</c> (a class others subclass, a generic
    ///     function, a global, a computed property, a feature), <c>geometry</c>
    ///     (a subclass of a class of another package), and <c>app</c>.
    /// </summary>
    sealed class Scenario : IDisposable {
        public PackageFixture Fixture { get; } = new();

        public Scenario() {
            Fixture.Package("shapes", "shapes", "0.1.0", "[features]\nfancy = []\n",
                ("src/lib.sb", """
                    module Shapes

                    /// A shape: its area, and a name.
                    public open class Shape {
                        public var label: String

                        public init(_ label: String) {
                            self.label = label
                            created = created + 1
                        }

                        public open fn area() -> Int {
                            return 0
                        }

                        public fn describe() -> String {
                            return "\(label) of area \(area())"
                        }
                    }

                    public class Circle: Shape {
                        public var radius: Int

                        public init(radius: Int) {
                            self.radius = radius
                            super.init("circle")
                        }

                        public override fn area() -> Int {
                            return scale(radius * radius * 3)
                        }
                    }

                    /// How many shapes have been made.
                    public var created = 0

                    fn scale(_ x: Int) -> Int {
                        return x
                    }

                    /// The largest of the values, or none.
                    public fn largest<T: Comparable>(_ xs: T[>..]) -> T? {
                        if xs.count == 0 {
                            return none
                        }
                        var best = xs[0]
                        for x in xs {
                            if x > best {
                                best = x
                            }
                        }
                        return best
                    }

                    public struct Point {
                        public var x: Int
                        public var y: Int

                        public var sum: Int { get { return x + y } }
                    }

                    #if feature(fancy)
                    public fn flourish() -> String {
                        return "~fancy~"
                    }
                    #else
                    public fn flourish() -> String {
                        return "plain"
                    }
                    #endif
                    """));
            Fixture.Package("geometry", "geometry", "0.1.0", "[dependencies]\nshapes = { path = \"../shapes\" }\n",
                ("src/lib.sb", Geometry("largest")));
            Fixture.Package("app", "app", "0.1.0", "[dependencies]\nshapes = { path = \"../shapes\" }\ngeometry = { path = \"../geometry\" }\n\n[features]\nfancy = [\"shapes/fancy\"]\n",
                ("src/main.sb", """
                    module App

                    import Sable.IO
                    import Shapes
                    import Geometry

                    fn main() {
                        let shapes: Shape[>..] = [Circle(radius: 2), Square(side: 3), Shape("blob")]
                        for shape in shapes {
                            print(shape.describe())
                            print("\(shape is Square) \(shape is Circle)")
                        }
                        print(survey(shapes))
                        let words: String[>..] = ["pear", "apple", "zucchini"]
                        print(largest(words) ?? "none")
                        let p = Point { x: 3, y: 4 }
                        print("\(p.sum) \(created) \(flourish())")
                    }
                    """));
        }

        public static string Geometry(string word) => $$"""
            module Geometry

            import Shapes

            public class Square: Shape {
                public var side: Int

                public init(side: Int) {
                    self.side = side
                    super.init("square")
                }

                public override fn area() -> Int {
                    return side * side
                }
            }

            /// The shapes' areas, and the largest.
            public fn survey(_ shapes: Shape[>..]) -> String {
                var areas: Int[>..] = []
                for shape in shapes {
                    areas.append(shape.area())
                }
                let top = largest(areas) ?? -1
                return "areas \(areas.count), {{word}} \(top)"
            }
            """;

        public string PathOf(string relative) => Path.Combine(Fixture.Root, relative);

        public void Edit(string relative, string old, string replacement) {
            var path = PathOf(relative);
            var text = File.ReadAllText(path);
            Assert.Contains(old, text);
            File.WriteAllText(path, text.Replace(old, replacement, StringComparison.Ordinal));
        }

        /// <summary>
        ///     Builds incrementally and runs the program; checks that a clean,
        ///     whole build of the same sources prints the same; and says what
        ///     the incremental build did.
        /// </summary>
        public Built Check(BuildRequest? request = null) {
            request ??= new();
            var built = Build(request);
            Assert.Equal(Clean(request), built.Output);
            return built;
        }

        public Built Build(BuildRequest request) {
            Fixture.Errors.GetStringBuilder().Clear();
            var binaries = PackageBuilder.Build(Fixture.Load("app"), Fixture.Environment, request with { Timings = true });
            Assert.True(binaries is [_], Fixture.Errors.ToString());
            var report = Regex.Match(Fixture.Errors.ToString(), @"Built app: (\w+), (\d+) unit\(s\) compiled, (\d+) reused");
            Assert.True(report.Success, Fixture.Errors.ToString());
            var (output, exitCode) = PackageFixture.Execute(binaries[0].Path);
            Assert.Equal(0, exitCode);
            return new(output, report.Groups[1].Value, int.Parse(report.Groups[2].Value), int.Parse(report.Groups[3].Value));
        }

        /// <summary>The build's errors, when it fails.</summary>
        public string Fails(BuildRequest? request = null) {
            Fixture.Errors.GetStringBuilder().Clear();
            Assert.Null(PackageBuilder.Build(Fixture.Load("app"), Fixture.Environment, request ?? new()));
            return Fixture.Errors.ToString();
        }

        /// <summary>What the program prints built whole, with a cache and an executable of its own.</summary>
        public string Clean(BuildRequest request) {
            var workspace = Fixture.Load("app");
            var graph = PackageBuilder.Resolve(workspace, Fixture.Environment, request);
            var member = workspace.Members[0];
            var profile = workspace.RootManifest.ProfileFor(request.Release);
            var directory = Directory.CreateTempSubdirectory("sable-clean-").FullName;
            try {
                var executable = Path.Combine(directory, "app");
                var settings = new BuildSettings(profile.Name, profile.Optimise, profile.Debug ? workspace.Root : null, profile.Exclusivity) { Whole = true };
                var outcome = IncrementalBuild.Build(PackageBuilder.Units(graph, member.Name, member.Bins[0]), settings, executable, Path.Combine(directory, "cache"));
                Assert.True(outcome.Succeeded, string.Join("\n", outcome.Errors));
                Assert.Equal("whole", outcome.Kind);
                var (output, exitCode) = PackageFixture.Execute(executable);
                Assert.Equal(0, exitCode);
                return output;
            } finally {
                Directory.Delete(directory, true);
            }
        }

        public void Dispose() => Fixture.Dispose();
    }

    sealed record Built(string Output, string Kind, int Compiled, int Reused);

    const string Expected = """
        circle of area 12
        false true
        square of area 9
        true false
        blob of area 0
        false false
        areas 3, largest 12
        zucchini
        7 3 plain

        """;

    // The program's units: app's entry file, geometry, shapes, and the standard library.
    const int Units = 4;

    [Fact]
    public void AnUnchangedBuildDoesNothing() {
        if (!CanBuild) {
            return;
        }

        using var scenario = new Scenario();
        var first = scenario.Check();
        Assert.Equal(Expected, first.Output);
        Assert.Equal(("incremental", Units, 0), (first.Kind, first.Compiled, first.Reused));

        var again = scenario.Build(new());
        Assert.Equal(("fresh", 0), (again.Kind, again.Compiled));
        Assert.Equal(Expected, again.Output);

        // A newer time is not a change: the cache goes by content.
        File.SetLastWriteTimeUtc(scenario.PathOf("shapes/src/lib.sb"), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal("fresh", scenario.Build(new()).Kind);
    }

    [Fact]
    public void APrivateBodyChangeRecompilesOnlyItsOwnPackage() {
        if (!CanBuild) {
            return;
        }

        using var scenario = new Scenario();
        scenario.Check();
        scenario.Edit("shapes/src/lib.sb", "    return x\n", "    return x + 1\n");
        var built = scenario.Check();
        Assert.Contains("circle of area 13", built.Output);
        Assert.Contains("areas 3, largest 13", built.Output);
        Assert.Equal(("incremental", 1, Units - 1), (built.Kind, built.Compiled, built.Reused));
    }

    [Fact]
    public void APublicSignatureChangeRecompilesWhatDependsOnIt() {
        if (!CanBuild) {
            return;
        }

        using var scenario = new Scenario();
        scenario.Check();

        // A default argument is code at every call: the callers are compiled again.
        scenario.Edit("shapes/src/lib.sb", "public fn flourish() -> String {\n    return \"plain\"", "public fn flourish(_ times: Int = 2) -> String {\n    return \"plain x\\(times)\"");
        scenario.Edit("shapes/src/lib.sb", "public fn flourish() -> String {\n    return \"~fancy~\"", "public fn flourish(_ times: Int = 2) -> String {\n    return \"~fancy~ x\\(times)\"");
        var built = scenario.Check();
        Assert.Contains("7 3 plain x2", built.Output);
        Assert.Equal(Units, built.Compiled);

        // So is a global's initial value, set up where the program starts.
        scenario.Edit("shapes/src/lib.sb", "public var created = 0", "public var created = 10");
        Assert.Contains("7 13 plain x2", scenario.Check().Output);
    }

    [Fact]
    public void AGenericFunctionsChangeReachesTheUnitsThatUseIt() {
        if (!CanBuild) {
            return;
        }

        using var scenario = new Scenario();
        scenario.Check();
        scenario.Edit("shapes/src/lib.sb", "if x > best {", "if x < best {");
        var built = scenario.Check();
        Assert.Contains("areas 3, largest 0", built.Output);
        Assert.Contains("\napple\n", built.Output);
        Assert.True(built.Compiled >= 3, $"{built.Compiled} unit(s) compiled");
    }

    [Fact]
    public void AddingAndRemovingFiles() {
        if (!CanBuild) {
            return;
        }

        using var scenario = new Scenario();
        scenario.Check();

        scenario.Fixture.Write("shapes/src/extra.sb", "module Shapes\n\npublic fn extra() -> Int {\n    return 42\n}\n");
        scenario.Edit("app/src/main.sb", "print(survey(shapes))", "print(survey(shapes))\n    print(\"extra \\(extra())\")");
        Assert.Contains("extra 42", scenario.Check().Output);

        // A file removed while it is still used: an error, never the last binary.
        File.Delete(scenario.PathOf("shapes/src/extra.sb"));
        Assert.Contains("extra", scenario.Fails());

        scenario.Edit("app/src/main.sb", "\n    print(\"extra \\(extra())\")", "");
        Assert.Equal(Expected, scenario.Check().Output);
    }

    [Fact]
    public void ChangingADependency() {
        if (!CanBuild) {
            return;
        }

        using var scenario = new Scenario();
        scenario.Check();

        // Another geometry, at another path: resolved afresh.
        scenario.Fixture.Package("geometry2", "geometry", "0.2.0", "[dependencies]\nshapes = { path = \"../shapes\" }\n", ("src/lib.sb", Scenario.Geometry("biggest")));
        scenario.Edit("app/sable.toml", "path = \"../geometry\"", "path = \"../geometry2\"");
        Assert.Contains("areas 3, biggest 12", scenario.Check().Output);

        scenario.Edit("app/sable.toml", "path = \"../geometry2\"", "path = \"../geometry\"");
        Assert.Equal(Expected, scenario.Check().Output);
    }

    [Fact]
    public void TogglingAFeature() {
        if (!CanBuild) {
            return;
        }

        using var scenario = new Scenario();
        var fancy = new BuildRequest(Features: new(["fancy"]));
        Assert.Contains("7 3 ~fancy~", scenario.Check(fancy).Output);
        Assert.Contains("7 3 plain", scenario.Check().Output);

        // Each state's objects are still there: going back compiles nothing.
        var back = scenario.Check(fancy);
        Assert.Contains("7 3 ~fancy~", back.Output);
        Assert.Equal(0, back.Compiled);
    }

    [Fact]
    public void ChangingTheProfile() {
        if (!CanBuild) {
            return;
        }

        using var scenario = new Scenario();
        Assert.Equal(Expected, scenario.Check().Output);
        var release = new BuildRequest(Release: true);
        var built = scenario.Check(release);
        Assert.Equal(Expected, built.Output);
        Assert.Equal("whole", built.Kind);
        Assert.Equal("fresh", scenario.Build(release).Kind);
        Assert.Equal("fresh", scenario.Build(new()).Kind);

        scenario.Edit("shapes/src/lib.sb", "    return x\n", "    return x * 2\n");
        Assert.Contains("circle of area 24", scenario.Check(release).Output);
        Assert.Contains("circle of area 24", scenario.Check().Output);
    }

    [Fact]
    public void ARevertedEditReusesWhatWasBuiltBefore() {
        if (!CanBuild) {
            return;
        }

        using var scenario = new Scenario();
        scenario.Check();
        scenario.Edit("geometry/src/lib.sb", "return side * side", "return side * side + 1");
        Assert.Contains("square of area 10", scenario.Check().Output);
        scenario.Edit("geometry/src/lib.sb", "return side * side + 1", "return side * side");
        var reverted = scenario.Check();
        Assert.Equal(Expected, reverted.Output);
        Assert.Equal(("incremental", 0), (reverted.Kind, reverted.Compiled));
    }

    [Fact]
    public void AnErrorFailsTheBuildAndItsFixBuilds() {
        if (!CanBuild) {
            return;
        }

        using var scenario = new Scenario();
        scenario.Check();
        scenario.Edit("geometry/src/lib.sb", "return side * side", "return side * \"side\"");
        Assert.Contains("geometry/src/lib.sb", scenario.Fails());
        scenario.Edit("geometry/src/lib.sb", "return side * \"side\"", "return side * side * 2");
        Assert.Contains("square of area 18", scenario.Check().Output);
    }

    [Fact]
    public void BrokenCachedObjectsAreNotLinked() {
        if (!CanBuild) {
            return;
        }

        using var scenario = new Scenario();
        scenario.Check();

        // Every cached object emptied: the link fails, and the program is built whole.
        foreach (var file in Directory.GetFiles(scenario.PathOf("app/target/dev/cache/objects"), "*.o")) {
            File.WriteAllBytes(file, []);
        }

        scenario.Edit("app/src/main.sb", "print(survey(shapes))", "print(\"surveyed: \" + survey(shapes))");
        var built = scenario.Check();
        Assert.Contains("surveyed: areas 3, largest 12", built.Output);
        Assert.Equal("whole", built.Kind);
    }

    [Fact]
    public void WhatAUnitCannotCheckAloneTheWholeProgramDecides() {
        if (!CanBuild) {
            return;
        }

        // Alone, the app's unit cannot see what 'first' returns a reference
        // into, and must assume either argument; the whole program knows it is 'a'.
        using var fixture = new PackageFixture();
        fixture.Package("refs", "refs", "0.1.0", "",
            ("src/lib.sb", "module Refs\n\npublic fn first(ref a: String, ref b: String) -> ref String {\n    return a\n}\n"));
        fixture.Package("app", "app", "0.1.0", "[dependencies]\nrefs = { path = \"../refs\" }\n",
            ("src/main.sb", """
                module App

                import Sable.IO
                import Refs

                fn choose(ref a: String) -> ref String {
                    let local = "local"
                    return first(a, local)
                }

                fn main() {
                    let s = "kept"
                    print(choose(s))
                }
                """));
        var built = PackageBuilder.Build(fixture.Load("app"), fixture.Environment, new(Timings: true));
        Assert.True(built is [_], fixture.Errors.ToString());
        Assert.Contains("compiling the program whole", fixture.Errors.ToString());
        Assert.Equal(("kept\n", 0), PackageFixture.Execute(built[0].Path));
    }

    [Fact]
    public void UnitsThatDisagreeAboutAClassAreNotLinked() {
        if (!CanBuild) {
            return;
        }

        // A class declared in a body, subclassing another unit's class: the
        // other unit cannot see it, and takes the superclass to have no
        // subclasses. Their records of it disagree, and the program is built whole.
        var directory = Directory.CreateTempSubdirectory("sable-disagree-").FullName;
        try {
            File.WriteAllText(Path.Combine(directory, "base.sb"), """
                module T

                import Sable.IO

                open class Base {
                    init() { }
                    open fn name() -> String { return "base" }
                }

                fn show(_ b: Base) {
                    print(b.name())
                }
                """);
            File.WriteAllText(Path.Combine(directory, "main.sb"), """
                module T

                fn main() {
                    class Local: Base {
                        init() { super.init() }
                        override fn name() -> String { return "local" }
                    }
                    show(Local())
                    show(Base())
                }
                """);
            UnitPlan Unit(string name) => new(name, [new SourceFile(Path.Combine(directory, name + ".sb"), name + ".sb", [])]);
            var log = new StringWriter();
            var executable = Path.Combine(directory, "program");
            var outcome = IncrementalBuild.Build([Unit("main"), Unit("base")], new BuildSettings("dev", false, directory, true), executable, Path.Combine(directory, "cache"), null, log);
            Assert.True(outcome.Succeeded, string.Join("\n", outcome.Errors));
            Assert.Equal("whole", outcome.Kind);
            Assert.Contains("disagree about class T.Base", log.ToString());
            Assert.Equal(("local\nbase\n", 0), PackageFixture.Execute(executable));
        } finally {
            Directory.Delete(directory, true);
        }
    }

    // ---- Interface fingerprints ---------------------------------------------

    static string Fingerprint(string source) {
        var tree = SyntaxTree.Parse(new SourceText(source, "f.sb"));
        return InterfaceFingerprint.Of(tree, InterfaceFingerprint.ConcreteTypeNames([tree]));
    }

    const string Interface = """
        module M

        public struct Box {
            var value: Int

            fn get() -> Int {
                return value
            }
        }

        extension Box {
            fn twice() -> Int {
                return value * 2
            }
        }

        fn plain(_ x: Int) -> Int {
            return x + 1
        }

        public fn generic<T>(_ x: T) -> T {
            return x
        }

        fn last() -> Int {
            return 1
        }
        """;

    [Theory]
    [InlineData("return x + 1", "return x + 2")]
    [InlineData("return value * 2", "return value * 3")]
    [InlineData("return value\n", "return value + 0\n")]
    [InlineData("fn last() -> Int {\n    return 1", "fn last() -> Int {\n    let y = 2\n    return y")]
    public void AnOrdinaryBodyIsNotInterface(string old, string replacement) =>
        Assert.Equal(Fingerprint(Interface), Fingerprint(Interface.Replace(old, replacement, StringComparison.Ordinal)));

    [Theory]
    [InlineData("return x\n", "let y = x\n    return y\n")]
    [InlineData("fn plain(_ x: Int) -> Int", "fn plain(_ x: Int, _ y: Int = 0) -> Int")]
    [InlineData("var value: Int", "var value: Int\n    var other: Int")]
    [InlineData("fn plain(_ x: Int) -> Int {\n    return x + 1", "fn plain(_ x: Int) -> Int {\n    let y = x\n    return y + 1")]
    [InlineData("public struct Box", "public struct Box<T>")]
    public void WhatOtherUnitsSeeIsInterface(string old, string replacement) {
        var changed = Interface.Replace(old, replacement, StringComparison.Ordinal);
        Assert.NotEqual(Interface, changed);
        Assert.NotEqual(Fingerprint(Interface), Fingerprint(changed));
    }

    // ---- Separate against whole, on the repository's programs ---------------

    static string Repository([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    [Theory]
    [InlineData("examples/ssg/ssg.sb", 1)]
    [InlineData("examples/ssg/ssg.sb", 6)]
    [InlineData("benchmarks/shapes.sb", 3)]
    [InlineData("benchmarks/words.sb", 3)]
    [InlineData("benchmarks/sort.sb", 3)]
    [InlineData("benchmarks/binary-trees.sb", 4)]
    [InlineData("benchmarks/orders.sb", 4)]
    public void SeparatelyCompiledProgramsBehaveAsWholeOnes(string file, int parts) {
        if (!CanBuild) {
            return;
        }

        // The file's declarations dealt into files of their own, each a unit.
        var text = File.ReadAllText(Path.Combine(Repository(), file));
        var root = (CompilationUnitSyntax)SyntaxTree.Parse(new SourceText(text, "program.sb")).Root;
        var members = root.Members.ToList();
        var header = text[..members[0].FullSpan.Start];
        var bodies = Enumerable.Range(0, Math.Min(parts, members.Count)).Select(_ => new System.Text.StringBuilder(header)).ToList();
        for (var i = 0; i < members.Count; i++) {
            bodies[i % bodies.Count].Append(text.AsSpan(members[i].FullSpan.Start, members[i].FullSpan.Length)).Append('\n');
        }

        var directory = Directory.CreateTempSubdirectory("sable-separate-").FullName;
        try {
            var units = bodies.Select((body, i) => {
                var path = Path.Combine(directory, $"part{i}.sb");
                File.WriteAllText(path, body.ToString());
                return new UnitPlan($"part{i}", [new SourceFile(path, $"part{i}.sb", [])]);
            }).ToList();

            var settings = new BuildSettings("dev", false, directory, true);
            var separate = IncrementalBuild.Build(units, settings, Path.Combine(directory, "separate"), Path.Combine(directory, "cache"));
            Assert.True(separate.Succeeded, string.Join("\n", separate.Errors));
            Assert.Equal(("incremental", units.Count + 1), (separate.Kind, separate.Compiled));
            var whole = IncrementalBuild.Build(units, settings with { Whole = true }, Path.Combine(directory, "whole"), Path.Combine(directory, "cache"));
            Assert.True(whole.Succeeded, string.Join("\n", whole.Errors));
            Assert.Equal(PackageFixture.Execute(Path.Combine(directory, "whole")), PackageFixture.Execute(Path.Combine(directory, "separate")));
        } finally {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    ///     A separate build's link takes what the whole program's build does on
    ///     each platform: on Linux, <c>-rdynamic</c> for a trap's backtrace and
    ///     libm for the <c>fmod</c> a float's remainder calls. Checked by its
    ///     arguments, as this may not run where Linux does.
    /// </summary>
    [Theory]
    [InlineData(NativePlatform.Linux, new[] { "-rdynamic", "-lm" })]
    [InlineData(NativePlatform.MacOS, new string[0])]
    public void ASeparateLinkTakesThePlatformsLibraries(NativePlatform platform, string[] flags) {
        Assert.Equal(flags, NativeBuild.PlatformLinkFlags(platform));
        Assert.Equal(
            ["a.o", "runtime.o", "-o", "program", ..flags],
            NativeObjects.LinkArguments(["a.o", "runtime.o"], "program", platform)
        );
    }
}
