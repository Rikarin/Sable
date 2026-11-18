using System.Diagnostics;
using Sable.CodeGen;
using Sable.Driver.Build;
using Sable.Syntax;

namespace Sable.Driver.Packages;

/// <summary>What <c>sable build</c>, <c>run</c> and <c>check</c> were asked to do with a package.</summary>
public sealed record BuildRequest(
    bool Release = false,
    FeatureRequest? Features = null,
    string? Package = null,
    string? Bin = null,
    bool Locked = false,
    bool Timings = false);

/// <summary>An executable a build made.</summary>
public sealed record BuiltBinary(string Package, string Name, string Path, Profile Profile);

/// <summary>
///     Builds packages: each binary is its entry file, the package's library
///     files, and every dependency's library files, compiled as one program —
///     incrementally (<see cref="IncrementalBuild" />, docs/05 §5.4): in a
///     debug build each package separately, and only what changed.
///     Executables go to <c>target/&lt;profile&gt;/</c>.
/// </summary>
public static class PackageBuilder {
    /// <summary>The members a request is about: the one named, the one the command ran in, or all of them.</summary>
    public static IReadOnlyList<LocalPackage> Selected(Workspace workspace, string? package) =>
        package is not null ? [workspace.Member(package)]
        : workspace.Current is { } current ? [current]
        : workspace.Members;

    internal static PackageGraph Resolve(Workspace workspace, PackageEnvironment environment, BuildRequest request) {
        ReportManifestWarnings(workspace, environment);
        return PackageGraph.ResolveCached(workspace, environment, new(request.Features, null, request.Locked, [..Selected(workspace, request.Package).Select(static m => m.Name)]));
    }

    /// <summary>
    ///     What the workspace's manifests say that this toolchain accepts but
    ///     does not act on — an unknown key, a setting not built yet — so that
    ///     nothing in a manifest is ignored without a word.
    /// </summary>
    static void ReportManifestWarnings(Workspace workspace, PackageEnvironment environment) {
        IEnumerable<Manifest> manifests = [workspace.RootManifest, ..workspace.LocalPackages().Values.Select(static p => p.Manifest)];
        foreach (var warning in manifests.DistinctBy(static m => m.File).SelectMany(static m => m.Warnings).Distinct()) {
            environment.Error.WriteLine($"warning: {warning}");
        }
    }

    /// <summary>
    ///     The trees of one target: the binary's entry file first (where the
    ///     entry point is looked for), then the package's library files, then
    ///     its dependencies' — each parsed with its own package's features, and
    ///     <c>DEBUG</c> or <c>RELEASE</c> as <paramref name="optimise" /> says.
    /// </summary>
    public static IReadOnlyList<SyntaxTree> Program(PackageGraph graph, string member, BinTarget? bin, bool optimise = false, BuildTimings? timings = null) {
        using var _ = BuildTimings.Of(timings, "parse");
        var trees = new List<SyntaxTree>();
        var own = graph.Packages[member];
        if (bin is not null) {
            trees.Add(Parse(graph, own, bin.Path, optimise));
        }

        foreach (var package in graph.Closure(member)) {
            trees.AddRange(package.Package.LibrarySources().Select(file => Parse(graph, package, file, optimise)));
        }

        return trees;
    }

    /// <summary>
    ///     What <c>sable test</c> and <c>sable bench</c> build for a member:
    ///     its library, the files under its <c>tests/</c> and <c>benches/</c>
    ///     (which an ordinary build leaves out), then its dependencies' libraries,
    ///     its dev-dependencies' among them.
    ///     The harness supplies the entry point, so no binary's entry file is in it.
    /// </summary>
    public static IReadOnlyList<SyntaxTree> TestProgram(PackageGraph graph, string member, bool optimise = false) {
        var own = graph.Packages[member];
        var extra = new[] { "tests", "benches" }
            .Select(d => Path.Combine(own.Package.Directory, d))
            .Where(Directory.Exists)
            .SelectMany(static d => Directory.GetFiles(d, "*.sb", SearchOption.AllDirectories))
            .Select(Path.GetFullPath)
            .Order(StringComparer.Ordinal)
            .Select(file => Parse(graph, own, file, optimise))
            .ToList();
        return [
            ..own.Package.LibrarySources().Select(file => Parse(graph, own, file, optimise)),
            ..extra,
            ..graph.Closure(member, dev: true).Where(p => p.Name != member).SelectMany(p => p.Package.LibrarySources().Select(file => Parse(graph, p, file, optimise))),
        ];
    }

    static SyntaxTree Parse(PackageGraph graph, ResolvedPackage package, string file, bool optimise) {
        var source = SourceOf(graph, package, file);
        return SyntaxTree.Parse(new SourceText(File.ReadAllText(file), source.Name), Pipeline.ParseOptionsFor(optimise, source.Options));
    }

    /// <summary>A file of a package, with the name its diagnostics give it and the features it is parsed with.</summary>
    static SourceFile SourceOf(PackageGraph graph, ResolvedPackage package, string file) {
        if (!File.Exists(file)) {
            throw new PackageException($"{package.Name}: no such file '{file}'");
        }

        var name = package.IsLocal
            ? Path.GetRelativePath(graph.Workspace.Root, file).Replace(Path.DirectorySeparatorChar, '/')
            : $"{package.Name}-{package.Version}/{Path.GetRelativePath(package.Package.Directory, file).Replace(Path.DirectorySeparatorChar, '/')}";
        // A workspace's own packages share 'package' members; a registry
        // dependency is a workspace of its own.
        return new(file, name, [..package.Features.Order(StringComparer.Ordinal)], package.IsLocal ? null : $"{package.Name}-{package.Version}");
    }

    /// <summary>
    ///     A binary's units of separate compilation, in the order of
    ///     <see cref="Program" />: its entry file, then each package's library,
    ///     its own first.
    /// </summary>
    public static IReadOnlyList<UnitPlan> Units(PackageGraph graph, string member, BinTarget bin) {
        var own = graph.Packages[member];
        var units = new List<UnitPlan> { new($"bin {member}/{bin.Name}", [SourceOf(graph, own, bin.Path)]) };
        foreach (var package in graph.Closure(member)) {
            units.Add(new($"lib {package.Name}-{package.Version}", [..package.Package.LibrarySources().Select(file => SourceOf(graph, package, file))]));
        }

        return units;
    }

    static string Describe(ResolvedPackage package, Workspace workspace) =>
        package.IsLocal ? $"{package.Name} v{package.Version} ({workspace.Relative(package.Package.Directory)})"
        : package.Git is { } git ? $"{package.Name} v{package.Version} ({git.Url}#{git.ShortCommit})"
        : $"{package.Name} v{package.Version}";

    static void Report(PackageEnvironment environment, IEnumerable<string> errors) {
        foreach (var error in errors) {
            environment.Error.WriteLine(error);
            environment.Error.WriteLine();
        }
    }

    /// <summary><c>sable check</c>: type-checks every target of the selected members, generating no code. Returns the exit code.</summary>
    public static int Check(Workspace workspace, PackageEnvironment environment, BuildRequest request) {
        var clock = Stopwatch.StartNew();
        var graph = Resolve(workspace, environment, request);
        var failed = false;
        var reported = new List<string>();
        foreach (var member in Selected(workspace, request.Package)) {
            var resolved = graph.Packages[member.Name];
            environment.Error.WriteLine($"    Checking {Describe(resolved, workspace)}");
            var targets = new List<BinTarget?>();
            if (member.LibrarySources().Count > 0) {
                targets.Add(null);
            }

            targets.AddRange(member.Bins);
            foreach (var target in targets) {
                var program = Program(graph, member.Name, target);
                // The lints look at this member's own files, each once: the
                // library with the library target, a binary's entry file with it.
                var ownLibrary = member.LibrarySources().Count;
                IReadOnlyList<SyntaxTree> linted = target is null ? [..program.Skip(program.Count - ownLibrary)] : [program[0]];
                var (errors, warnings) = Pipeline.CheckAndLint(program, member.Manifest.Lints, linted);
                Report(environment, errors);
                Report(environment, warnings);
                reported.AddRange(errors.Concat(warnings));
                failed |= errors.Count > 0;
            }
        }

        DiagnosticDocs.WriteHint(environment.Error, reported);
        if (failed) {
            environment.Error.WriteLine("error: could not check the package: it has errors");
            return 1;
        }

        environment.Error.WriteLine($"    Finished checking in {clock.Elapsed.TotalSeconds:0.00}s");
        return 0;
    }

    /// <summary>
    ///     <c>sable build</c>: every binary of the selected members (or the one
    ///     <see cref="BuildRequest.Bin" /> names), to <c>target/&lt;profile&gt;/</c>;
    ///     a member with only a library is type-checked. Null binaries mean it failed.
    /// </summary>
    public static IReadOnlyList<BuiltBinary>? Build(Workspace workspace, PackageEnvironment environment, BuildRequest request) {
        var clock = Stopwatch.StartNew();
        var timings = request.Timings ? new BuildTimings() : null;

        // What the build cache is keyed by — the compiler's assemblies, clang's
        // version — is worked out while the packages are resolved.
        _ = Task.Run(() => IncrementalBuild.Toolchain);
        PackageGraph graph;
        using (BuildTimings.Of(timings, "resolve")) {
            graph = Resolve(workspace, environment, request);
        }

        var profile = workspace.RootManifest.ProfileFor(request.Release);
        var output = Path.Combine(workspace.TargetDirectory, profile.Name);
        var built = new List<BuiltBinary>();
        var members = Selected(workspace, request.Package);

        // What the profile in use asks for and does not get is said, not
        // taken silently.
        foreach (var warning in workspace.RootManifest.Warnings.Where(w => w.Key.StartsWith($"profile.{profile.Name}.", StringComparison.Ordinal))) {
            environment.Error.WriteLine($"warning: {warning}");
        }

        if (members.Any(static m => m.Manifest.Build is not null)) {
            environment.Error.WriteLine("warning: [build] scripts are not run yet (docs/05 §5.6); building without it");
        }

        foreach (var member in members) {
            var bins = member.Bins.Where(b => request.Bin is null || b.Name == request.Bin).ToList();
            if (request.Bin is not null && bins.Count == 0) {
                continue;
            }

            foreach (var dependency in graph.Closure(member.Name).Skip(1)) {
                environment.Error.WriteLine($"   Compiling {Describe(dependency, workspace)}");
            }

            environment.Error.WriteLine($"   Compiling {Describe(graph.Packages[member.Name], workspace)}");
            if (bins.Count == 0) {
                var (errors, warnings) = Pipeline.Diagnose(Program(graph, member.Name, null, profile.Optimise));
                Report(environment, warnings);
                if (errors.Count > 0) {
                    Report(environment, errors);
                    DiagnosticDocs.WriteHint(environment.Error, errors);
                    environment.Error.WriteLine($"error: could not compile {member.Name}");
                    return null;
                }

                continue;
            }

            foreach (var bin in bins) {
                if (!NativeObjects.CompilerAvailable) {
                    environment.Error.WriteLine($"error: cannot run '{NativeBuild.CompilerPath}'; set CC to a clang that can link for this platform");
                    return null;
                }

                // Incrementally (docs/05 §5.4): the cache in target/<profile>/cache.
                Directory.CreateDirectory(output);
                var executable = Path.Combine(output, bin.Name);
                var buildSettings = new BuildSettings(profile.Name, profile.Optimise, profile.Debug ? workspace.Root : null, profile.Exclusivity) {
                    Native = profile.Native,
                    Whole = profile.CodegenUnits == 1,
                };
                var outcome = IncrementalBuild.Build(Units(graph, member.Name, bin), buildSettings, executable, Path.Combine(output, "cache"), timings, environment.Error);
                Report(environment, outcome.Warnings);
                if (!outcome.Succeeded) {
                    Report(environment, outcome.Errors);
                    DiagnosticDocs.WriteHint(environment.Error, outcome.Errors);
                    environment.Error.WriteLine($"error: could not compile {member.Name}");
                    return null;
                }

                if (timings is not null) {
                    environment.Error.WriteLine($"       Built {bin.Name}: {outcome.Kind}, {outcome.Compiled} unit(s) compiled, {outcome.Reused} reused");
                }

                built.Add(new(member.Name, bin.Name, executable, profile));
            }
        }

        if (request.Bin is not null && built.Count == 0) {
            throw new PackageException($"no binary called '{request.Bin}'");
        }

        var settings = $"{(profile.Optimise ? "optimised" : "unoptimised")}{(profile.Debug ? " + debuginfo" : "")}";
        environment.Error.WriteLine($"    Finished `{profile.Name}` profile [{settings}] target(s) in {clock.Elapsed.TotalSeconds:0.00}s");
        timings?.Write(environment.Error);
        return built;
    }

    /// <summary><c>sable run</c>: builds the one binary there is (or the one asked for), runs it, and returns its exit code.</summary>
    public static int Run(Workspace workspace, PackageEnvironment environment, BuildRequest request, IReadOnlyList<string> arguments) {
        var candidates = Selected(workspace, request.Package).SelectMany(m => m.Bins.Select(b => (m, b))).Where(c => request.Bin is null || c.b.Name == request.Bin).ToList();
        if (candidates.Count == 0) {
            throw new PackageException(request.Bin is null ? "there is no binary to run: a package needs src/main.sb or a [[bin]]" : $"no binary called '{request.Bin}'");
        }

        if (candidates.Count > 1) {
            throw new PackageException($"there are several binaries; say which with --bin: {string.Join(", ", candidates.Select(static c => c.b.Name))}");
        }

        var (member, bin) = candidates[0];
        if (Build(workspace, environment, request with { Package = member.Name, Bin = bin.Name }) is not [var binary]) {
            return 1;
        }

        environment.Error.WriteLine($"     Running `{Path.GetRelativePath(workspace.Root, binary.Path)}`");
        var start = new ProcessStartInfo(binary.Path);
        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        // leak-check: the runtime walks the heap at exit and reports what is left.
        if (binary.Profile.LeakCheck) {
            start.Environment["SABLE_CHECK_LEAKS"] = "1";
        }

        using var process = Process.Start(start) ?? throw new PackageException($"could not run '{binary.Path}'");
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary><c>sable clean</c>: removes <c>target/</c>.</summary>
    public static void Clean(Workspace workspace, PackageEnvironment environment) {
        if (Directory.Exists(workspace.TargetDirectory)) {
            Directory.Delete(workspace.TargetDirectory, true);
            environment.Error.WriteLine($"     Removed {workspace.Relative(workspace.TargetDirectory)}");
        }
    }
}
