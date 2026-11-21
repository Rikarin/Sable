using System.Text.RegularExpressions;
using Sable.Driver.Build;
using Sable.Driver.Packages;
using Sable.Symbols;

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
            return new(output, report.Groups[1].Value, int.Parse(report.Groups[2].Value), int.Parse(report.Groups[3].Value), Fixture.Errors.ToString());
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
            var profile = workspace.RootManifest.ProfileNamed(request.Profile);
            var directory = Directory.CreateTempSubdirectory("sable-clean-").FullName;
            try {
                var executable = Platform.Executable(Path.Combine(directory, "app"));
                var settings = new BuildSettings(profile.Name, profile.Optimise, profile.Debug ? workspace.Root : null, profile.Exclusivity) { Whole = true };
                var outcome = IncrementalBuild.Build(PackageBuilder.Units(graph, member.Name, member.Bins[0]), settings, executable, Path.Combine(directory, "cache"), cache: new BuildCache(Path.Combine(directory, "shared")));
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

    sealed record Built(string Output, string Kind, int Compiled, int Reused, string Log = "") {
        /// <summary>A count the build's timings report.</summary>
        public int Counted(string what) =>
            Regex.Match(Log, $@"{Regex.Escape(what)}\s+(\d+)") is { Success: true } found ? int.Parse(found.Groups[1].Value) : 0;
    }

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

    /// <summary>
    ///     A release build — ThinLTO, the profile's default — is compiled
    ///     separately too: each unit's SIL passes run on the unit, its object
    ///     is LLVM bitcode, and the link optimises across units. A body change
    ///     compiles one unit again, and the program behaves as the whole
    ///     program built from nothing does. Whole-program LTO stays whole.
    /// </summary>
    [Fact]
    public void AThinLtoReleaseBuildIsCompiledSeparately() {
        if (!CanBuild) {
            return;
        }

        using var scenario = new Scenario();
        var release = new BuildRequest(Profile: "release");
        File.AppendAllText(scenario.PathOf("app/sable.toml"), "\n[profile.release]\nlto = \"fat\"\n");
        Assert.Equal("whole", scenario.Check(release).Kind);

        // ThinLTO is the release profile's default.
        scenario.Edit("app/sable.toml", "lto = \"fat\"\n", "");
        var first = scenario.Check(release);
        Assert.Equal(Expected, first.Output);
        Assert.Equal(("incremental", Units, 0), (first.Kind, first.Compiled, first.Reused));

        scenario.Edit("shapes/src/lib.sb", "    return x\n", "    return x + 1\n");
        var built = scenario.Check(release);
        Assert.Contains("circle of area 13", built.Output);
        Assert.Equal(("incremental", 1, Units - 1), (built.Kind, built.Compiled, built.Reused));

        // Without link-time optimisation, the units are native objects.
        File.AppendAllText(scenario.PathOf("app/sable.toml"), "lto = \"off\"\n");
        Assert.Equal(("incremental", Units), scenario.Check(release) is var off ? (off.Kind, off.Compiled) : default);
    }

    /// <summary>
    ///     Each unit's interface is written as a binary <c>.sbi</c> file — its
    ///     files' fingerprints, and their source with the bodies other units
    ///     cannot see blanked — and read back by the next build: a unit whose
    ///     object is kept is parsed and bound from it, not from its source; a
    ///     fingerprint planted in one is what the units are keyed by; and one
    ///     cut short, corrupted or of another version is worked out again.
    /// </summary>
    [Fact]
    public void EachUnitsInterfaceIsWrittenAndReadBack() {
        if (!CanBuild) {
            return;
        }

        using var scenario = new Scenario();
        scenario.Check();
        var interfaces = scenario.PathOf("app/target/dev/cache/interfaces");
        string Sbi(string unit, string file) => Assert.Single(Directory.GetFiles(interfaces, "*.sbi"), f => InterfaceFile.Read(f, unit, [file]) is not null);
        InterfaceFile Read(string unit, string file) => InterfaceFile.Read(Sbi(unit, file), unit, [file])!;

        // The program's three units (the standard library has none of its own).
        Assert.Equal(Units - 1, Directory.GetFiles(interfaces, "*.sbi").Length);
        var shapesFile = Read("lib shapes-0.1.0", "../shapes/src/lib.sb");
        var shapes = Assert.Single(shapesFile.Files).Interface;
        Assert.Equal("SBI"u8.ToArray(), File.ReadAllBytes(Sbi("lib shapes-0.1.0", "../shapes/src/lib.sb"))[..3]);
        // Its source: every line where it is, an ordinary body blanked.
        var source = File.ReadAllText(scenario.PathOf("shapes/src/lib.sb"));
        Assert.Equal(source.Length, shapes.Source.Length);
        Assert.Equal(source.Count(static c => c == '\n'), shapes.Source.Count(static c => c == '\n'));
        Assert.StartsWith("module Shapes\n", shapes.Source, StringComparison.Ordinal);
        Assert.Matches(@"fn scale\(_ x: Int\) -> Int \{\s+\}", shapes.Source);
        Assert.DoesNotContain("return x\n", shapes.Source, StringComparison.Ordinal);
        // A generic body is lowered where it is used: it is interface.
        Assert.Contains("if x > best {", shapes.Source, StringComparison.Ordinal);
        // The dotted names it writes, from which what depends on what is found.
        Assert.Contains("xs.count", shapes.References);

        // Each declaration's own fingerprint and what it mentions, and the
        // names the file writes, by which other units depend on it.
        Assert.Contains(shapes.Declarations, static d => d is { Name: "largest" } && d.Mentions.Contains("Comparable"));
        Assert.Contains(shapes.Declarations, static d => d.Name == DeclarationPrint.Everyone);
        Assert.Contains("scale", shapes.Uses);

        // A body change writes the changed unit's interface beside the old
        // one, and the units kept are bound from theirs.
        scenario.Edit("shapes/src/lib.sb", "    return x\n", "    return x + 1\n");
        var changed = scenario.Check();
        Assert.Equal(1, changed.Compiled);
        Assert.Equal(Units, Directory.GetFiles(interfaces, "*.sbi").Length);
        Assert.Equal(2, changed.Counted("files parsed from interfaces"));

        // What the next build reads is what the units are keyed by: a
        // fingerprint planted in geometry's compiles the unit that depends
        // on geometry again, and no other. (The executable is removed, so
        // that the build is not merely fresh.)
        var executable = Platform.Executable(scenario.PathOf("app/target/dev/app"));
        var geometryPath = Sbi("lib geometry-0.1.0", "../geometry/src/lib.sb");
        var geometry = Read("lib geometry-0.1.0", "../geometry/src/lib.sb");
        var bytes = File.ReadAllBytes(geometryPath);
        var face = geometry.Files[0].Interface;
        (geometry with { Files = [(geometry.Files[0].File, face with { Fingerprint = new string('0', 64) })] }).WriteTo(geometryPath);
        File.Delete(executable);
        var planted = scenario.Check();
        Assert.Equal(("incremental", 1), (planted.Kind, planted.Compiled));

        // One cut short, corrupted or of another version is not read, but
        // worked out again: the units are keyed as they were, and nothing is compiled.
        byte[][] broken = [bytes[..^10], [..bytes[..40], (byte)(bytes[40] ^ 1), ..bytes[41..]], [..bytes[..3], 99, 0, 0, 0, ..bytes[7..]]];
        foreach (var damaged in broken) {
            Assert.Null(InterfaceFile.Read(Write(geometryPath, damaged), "lib geometry-0.1.0", ["../geometry/src/lib.sb"]));
            File.Delete(executable);
            var rebuilt = scenario.Check();
            Assert.Equal(Expected.Replace("12", "13", StringComparison.Ordinal), rebuilt.Output);
            Assert.Equal(0, rebuilt.Compiled);
            var again = Read("lib geometry-0.1.0", "../geometry/src/lib.sb").Files[0].Interface;
            Assert.Equal(face, again with { References = face.References, Declarations = face.Declarations, Uses = face.Uses });
            Assert.Equal(face.References, again.References);
            Assert.Equal(face.Uses, again.Uses);
            Assert.Equal(face.Declarations.Select(static d => (d.Name, d.Fingerprint, string.Join(",", d.Mentions))), again.Declarations.Select(static d => (d.Name, d.Fingerprint, string.Join(",", d.Mentions))));
        }
    }

    static string Write(string path, byte[] bytes) {
        File.WriteAllBytes(path, bytes);
        return path;
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
        // Shapes and the app, which calls it; not geometry, which does not
        // name it (AnInterfaceChangeRecompilesOnlyTheUnitsThatUseWhatChanged),
        // nor the standard library.
        Assert.Equal(Units - 2, built.Compiled);

        // So is a global's initial value, set up where the program starts.
        scenario.Edit("shapes/src/lib.sb", "public var created = 0", "public var created = 10");
        Assert.Contains("7 13 plain x2", scenario.Check().Output);
    }

    /// <summary>
    ///     A change to what other units see of a package compiles again only
    ///     the units that depend on it: a new public function in geometry, the
    ///     app's unit and geometry's — not shapes', which geometry depends on,
    ///     nor the standard library's.
    /// </summary>
    [Fact]
    public void AnInterfaceChangeRecompilesOnlyItsDependents() {
        if (!CanBuild) {
            return;
        }

        using var scenario = new Scenario();
        scenario.Check();
        scenario.Edit("geometry/src/lib.sb", "/// The shapes' areas", "public fn sides() -> Int {\n    return 4\n}\n\n/// The shapes' areas");
        var built = scenario.Check();
        Assert.Equal(Expected, built.Output);
        Assert.Equal(("incremental", 2, Units - 2), (built.Kind, built.Compiled, built.Reused));

        // Used by the app: its unit alone.
        scenario.Edit("app/src/main.sb", "print(survey(shapes))", "print(survey(shapes))\n    print(sides())");
        var used = scenario.Check();
        Assert.Contains("largest 12\n4\n", used.Output);
        Assert.Equal(1, used.Compiled);

        // A class's layout, which every unit that makes or reads one lays
        // out: shapes', and the two that depend on it.
        scenario.Edit("shapes/src/lib.sb", "public var label: String\n", "public var label: String\n    public var sides = 0\n");
        var layout = scenario.Check();
        Assert.Contains("circle of area 12", layout.Output);
        Assert.Equal(Units - 1, layout.Compiled);
    }

    /// <summary>
    ///     A unit depends on the declarations of another that it may use, by
    ///     name (#182, docs/05 §5.4): a new public function in shapes, which
    ///     geometry depends on but does not name, compiles again shapes' unit
    ///     and the app's — which sets up every global, and takes every unit's
    ///     whole interface — not geometry's. One named as geometry writes a
    ///     name (an overload of 'largest' it calls) compiles geometry's too,
    ///     and so does a change to what geometry reaches through what it
    ///     names: the class it subclasses.
    /// </summary>
    [Fact]
    public void AnInterfaceChangeRecompilesOnlyTheUnitsThatUseWhatChanged() {
        if (!CanBuild) {
            return;
        }

        using var scenario = new Scenario();
        scenario.Check();
        scenario.Edit("shapes/src/lib.sb", "public struct Point {", "public fn corners() -> Int {\n    return 4\n}\n\npublic struct Point {");
        var unused = scenario.Check();
        Assert.Equal(Expected, unused.Output);
        Assert.Equal(("incremental", 2, Units - 2), (unused.Kind, unused.Compiled, unused.Reused));

        scenario.Edit("shapes/src/lib.sb", "public struct Point {", "public fn largest(_ xs: String[>..], or fallback: String) -> String {\n    return fallback\n}\n\npublic struct Point {");
        var named = scenario.Check();
        Assert.Equal(Expected, named.Output);
        Assert.Equal(Units - 1, named.Compiled);

        scenario.Edit("shapes/src/lib.sb", "public fn describe() -> String {", "public fn kind() -> Int {\n        return 1\n    }\n\n    public fn describe() -> String {");
        var reached = scenario.Check();
        Assert.Equal(Expected, reached.Output);
        Assert.Equal(Units - 1, reached.Compiled);
    }

    /// <summary>
    ///     An extension adds to a type that units which do not depend on it can
    ///     see — a conformance, an overload that changes how the type's
    ///     methods are named — so changing one compiles every unit again.
    /// </summary>
    [Fact]
    public void AnExtensionReachesEveryUnit() {
        if (!CanBuild) {
            return;
        }

        using var scenario = new Scenario();
        scenario.Check();
        scenario.Edit("geometry/src/lib.sb", "/// The shapes' areas", "extension Shape {\n    public fn twice() -> Int {\n        return area() * 2\n    }\n}\n\n/// The shapes' areas");
        scenario.Edit("app/src/main.sb", "print(survey(shapes))", "print(survey(shapes))\n    print(Circle(radius: 1).twice())");
        var built = scenario.Check();
        Assert.Contains("largest 12\n6\n", built.Output);
        Assert.Equal(Units, built.Compiled);

        // Its body is its own unit's, as any other.
        scenario.Edit("geometry/src/lib.sb", "return area() * 2", "return area() * 3");
        var body = scenario.Check();
        Assert.Contains("largest 12\n9\n", body.Output);
        Assert.Equal(1, body.Compiled);
    }

    /// <summary>
    ///     A type's metadata and a conformance's witness table are one for
    ///     the program however many units write them (docs/06 §6.2): metadata
    ///     asked for in two packages is equal, and an existential made in one
    ///     is cast, in another, to a protocol a type of a third conforms to —
    ///     through the registry, whichever unit's copy of the records the
    ///     linker kept.
    /// </summary>
    [Fact]
    public void MetadataAndWitnessTablesAreOneForTheProgram() {
        if (!CanBuild) {
            return;
        }

        using var scenario = new Scenario();
        scenario.Edit("shapes/src/lib.sb", "/// How many shapes have been made.", """
            public protocol Labelled {
                var label: String { get }
            }

            public protocol Sized {
                fn area() -> Int
            }

            extension Shape: Labelled, Sized {}

            public fn labelled(_ shape: Shape) -> any Labelled => shape

            public fn pointMetadata() -> Type => Point.self

            /// How many shapes have been made.
            """);
        scenario.Edit("app/src/main.sb", "print(survey(shapes))", """
            print(survey(shapes))
                print(pointMetadata() == Point.self)
                if let sized = labelled(Square(side: 5)) as? any Sized {
                    print(sized.area())
                }
            """);
        var built = scenario.Check();
        Assert.Contains("largest 12\ntrue\n25\n", built.Output);
        Assert.True(built.Compiled > 1, "the program was built in units");
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
        var release = new BuildRequest(Profile: "release");
        var built = scenario.Check(release);
        Assert.Equal(Expected, built.Output);
        // Its own units: the profile's settings key them.
        Assert.Equal(("incremental", Units, 0), (built.Kind, built.Compiled, built.Reused));
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
            ("src/lib.sb", "module Refs\n\npublic fn first(a: String&, b: String&) -> String& {\n    return a\n}\n"));
        fixture.Package("app", "app", "0.1.0", "[dependencies]\nrefs = { path = \"../refs\" }\n",
            ("src/main.sb", """
                module App

                import Sable.IO
                import Refs

                fn choose(a: String&) -> String& {
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

    /// <summary>
    ///     Two units of one module, <c>T</c>: <c>base</c> declares a class with
    ///     a method a subclass may override, and <c>main</c> a subclass of it
    ///     inside a body, which <c>base</c> does not see.
    /// </summary>
    static (BuildOutcome Outcome, string Log, string Output) BuildBaseAndLocal(string modifiers) {
        var directory = Directory.CreateTempSubdirectory("sable-local-").FullName;
        try {
            File.WriteAllText(Path.Combine(directory, "base.sb"), $$"""
                module T

                import Sable.IO

                {{modifiers}}class Base {
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
            var executable = Platform.Executable(Path.Combine(directory, "program"));
            var outcome = IncrementalBuild.Build([Unit("main"), Unit("base")], new BuildSettings("dev", false, directory, true), executable, Path.Combine(directory, "cache"), null, log, new BuildCache(Path.Combine(directory, "shared")));
            Assert.True(outcome.Succeeded, string.Join("\n", outcome.Errors));
            return (outcome, log.ToString(), PackageFixture.Execute(executable).Output);
        } finally {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void UnitsThatDisagreeAboutAClassAreNotLinked() {
        if (!CanBuild) {
            return;
        }

        // A class that is not open, subclassed in a body of another unit of
        // its module: its own unit cannot see the subclass, and takes the
        // class to have none. Their records of it disagree, and the program
        // is built whole.
        var (outcome, log, output) = BuildBaseAndLocal("");
        Assert.Equal("whole", outcome.Kind);
        Assert.Contains("disagree about class T.Base", log);
        Assert.Equal("local\nbase\n", output);
    }

    /// <summary>
    ///     Separately, an open class's methods are dispatched whatever this
    ///     unit sees of its subclasses — another unit may add one — so a
    ///     subclass in a body the class's unit cannot see changes nothing
    ///     there, and the units agree.
    /// </summary>
    [Fact]
    public void AnOpenClassIsDispatchedWhateverAUnitSeesOfItsSubclasses() {
        if (!CanBuild) {
            return;
        }

        var (outcome, log, output) = BuildBaseAndLocal("open ");
        Assert.Equal(("incremental", 3), (outcome.Kind, outcome.Compiled));
        Assert.DoesNotContain("disagree", log);
        Assert.Equal("local\nbase\n", output);
    }

    /// <summary>
    ///     A unit's lowering is a function of what it can name: lowered with a
    ///     unit that depends on it, whatever that unit declares — a subclass
    ///     of its open class, globals of its own — it comes out as it does
    ///     alone. (Each unit's object is kept until the units it depends on
    ///     change.)
    /// </summary>
    [Fact]
    public void AUnitsLoweringDoesNotDependOnItsDependents() {
        const string shapes = """
            module Shapes

            public open class Shape {
                public init() { }
                public open fn area() -> Int { return 0 }
            }

            public fn total(_ shapes: Shape[>..]) -> Int {
                var sum = 0
                for shape in shapes {
                    sum = sum + shape.area()
                }
                return sum
            }

            public var made = 0
            """;
        static string Lower(params string[] files) {
            var trees = files.Select((text, i) => SyntaxTree.Parse(new SourceText(text, $"f{i}.sb"))).ToList();
            var compilation = Compilation.Create(trees);
            Assert.DoesNotContain(compilation.GetDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
            var (module, diagnostics) = SilGen.LowerUnit(compilation, new SilUnit(t => t == trees[0]), SilProgram.Index(compilation));
            Assert.Empty(diagnostics);
            return SilPrinter.Print(module);
        }

        var alone = Lower(shapes);
        Assert.Equal(alone, Lower(shapes, """
            module App

            import Shapes

            public class Square: Shape {
                var side: Int
                public init(side: Int) { self.side = side; super.init() }
                public override fn area() -> Int { return side * side }
            }

            public var squares: Square[>..] = []
            public var count = 0

            fn main() {
                made = total(squares)
            }
            """));
    }

    // ---- Interface fingerprints ---------------------------------------------

    static string Fingerprint(string source) {
        var tree = SyntaxTree.Parse(new SourceText(source, "f.sb"));
        return InterfaceFingerprint.Of(tree, InterfaceFingerprint.ConcreteTypeNames([tree]));
    }

    const string Interface = """
        module M

        public const fn seed() -> Int {
            return 7
        }

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
    // Another unit's constant may be computed from it.
    [InlineData("return 7", "return 8")]
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
    // Optimised with ThinLTO: the SIL passes run on each unit apart.
    [InlineData("examples/ssg/ssg.sb", 6, true)]
    [InlineData("benchmarks/shapes.sb", 3, true)]
    [InlineData("benchmarks/words.sb", 3, true)]
    [InlineData("benchmarks/sort.sb", 3, true)]
    [InlineData("benchmarks/binary-trees.sb", 4, true)]
    [InlineData("benchmarks/orders.sb", 4, true)]
    public void SeparatelyCompiledProgramsBehaveAsWholeOnes(string file, int parts, bool release = false) {
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

            var settings = release
                ? new BuildSettings("release", true, null, false) { Native = new NativeOptions { OptLevel = "3", Lto = LtoMode.Thin } }
                : new BuildSettings("dev", false, directory, true);
            var separate = IncrementalBuild.Build(units, settings, Path.Combine(directory, "separate"), Path.Combine(directory, "cache"), cache: new BuildCache(Path.Combine(directory, "shared")));
            Assert.True(separate.Succeeded, string.Join("\n", separate.Errors));
            Assert.Equal(("incremental", units.Count + 1), (separate.Kind, separate.Compiled));
            var whole = IncrementalBuild.Build(units, settings with { Whole = true }, Path.Combine(directory, "whole"), Path.Combine(directory, "cache"), cache: new BuildCache(Path.Combine(directory, "shared")));
            Assert.True(whole.Succeeded, string.Join("\n", whole.Errors));
            Assert.Equal(PackageFixture.Execute(Path.Combine(directory, "whole")), PackageFixture.Execute(Path.Combine(directory, "separate")));

            // Each part changed in turn — but not its interface — is compiled
            // alone, against the others bound from their .sbi files.
            for (var i = units.Count - 1; i >= 0; i--) {
                File.AppendAllText(units[i].Files[0].Path, "\n// changed\n");
                var timings = new BuildTimings();
                var again = IncrementalBuild.Build(units, settings, Path.Combine(directory, "separate"), Path.Combine(directory, "cache"), timings, cache: new BuildCache(Path.Combine(directory, "shared")));
                Assert.True(again.Succeeded, string.Join("\n", again.Errors));
                Assert.Equal(("incremental", 1), (again.Kind, again.Compiled));
                Assert.Equal(units.Count - 1, timings.CountOf("files parsed from interfaces"));
                Assert.Equal(PackageFixture.Execute(Path.Combine(directory, "whole")), PackageFixture.Execute(Path.Combine(directory, "separate")));
            }
        } finally {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    ///     A separate build's link takes what the whole program's build does on
    ///     each platform: on Linux, <c>-rdynamic</c> for a trap's backtrace,
    ///     libm for the <c>fmod</c> a float's remainder calls, and
    ///     <c>-pthread</c> for the task pool's threads. Checked by its
    ///     arguments, as this may not run where Linux does.
    /// </summary>
    [Theory]
    [InlineData(NativePlatform.Linux, new[] { "-rdynamic", "-lm", "-pthread" })]
    [InlineData(NativePlatform.MacOS, new string[0])]
    public void ASeparateLinkTakesThePlatformsLibraries(NativePlatform platform, string[] flags) {
        Assert.Equal(flags, NativeBuild.PlatformLinkFlags(platform));
        Assert.Equal(
            ["a.o", "runtime.o", "-o", "program", ..flags],
            NativeObjects.LinkArguments(["a.o", "runtime.o"], "program", platform)
        );
    }

    /// <summary>
    ///     A profile's settings as clang's flags: each opt-level its own, debug
    ///     information at any of them, link-time optimisation on Linux through
    ///     lld (where it links), and what the link strips on each platform.
    /// </summary>
    [Fact]
    public void AProfilesSettingsAreClangsFlags() {
        Assert.Equal(["-Os"], NativeBuild.CompileFlags(new NativeOptions { OptLevel = "s" }));
        Assert.Equal(["-g", "-O3"], NativeBuild.CompileFlags(new NativeOptions { OptLevel = "3", DebugInfo = true }));
        Assert.Equal(["-g", "-O0"], NativeBuild.CompileFlags(NativeOptions.Of(optimise: true, debug: true)));

        var release = new NativeOptions { OptLevel = "3" };
        Assert.Equal(["-flto"], NativeBuild.LtoFlags(release, NativePlatform.MacOS, linksBitcode: true));
        Assert.Equal(["-flto=thin", "-fuse-ld=lld"], NativeBuild.LtoFlags(release with { Lto = LtoMode.Thin }, NativePlatform.Linux, linksBitcode: true));
        Assert.Empty(NativeBuild.LtoFlags(release, NativePlatform.Linux, linksBitcode: false));
        Assert.Empty(NativeBuild.LtoFlags(release with { Lto = LtoMode.Off }, NativePlatform.MacOS, linksBitcode: true));
        Assert.Empty(NativeBuild.LtoFlags(release with { OptLevel = "0" }, NativePlatform.MacOS, linksBitcode: true));

        Assert.Equal(["-rdynamic", "-lm", "-pthread", "-Wl,--strip-all", "-Wl,-rpath,$ORIGIN"],
            NativeBuild.LinkFlags(release with { Strip = StripMode.Symbols, Rpath = true }, NativePlatform.Linux));
        Assert.Equal(["-Wl,-S"], NativeBuild.LinkFlags(release with { Strip = StripMode.DebugInfo }, NativePlatform.MacOS));
        Assert.Equal(["a.o", "-o", "program", "-rdynamic", "-lm", "-pthread", "-Wl,--strip-debug"],
            NativeObjects.LinkArguments(["a.o"], "program", NativePlatform.Linux, release with { Strip = StripMode.DebugInfo }));
    }
}
