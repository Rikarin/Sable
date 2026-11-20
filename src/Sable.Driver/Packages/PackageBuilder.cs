using System.Diagnostics;
using Sable.CodeGen;
using Sable.Driver.Build;
using Sable.Syntax;

namespace Sable.Driver.Packages;

/// <summary>What <c>sable build</c>, <c>run</c> and <c>check</c> were asked to do with a package.</summary>
public sealed record BuildRequest(
    string Profile = "dev",
    FeatureRequest? Features = null,
    string? Package = null,
    string? Bin = null,
    bool Locked = false,
    bool Timings = false) {
    /// <summary>What to build for (<c>--target</c>): null for this machine.</summary>
    public NativeTarget? Target { get; init; }

    /// <summary>clang's <c>--sysroot</c> for the target (<c>--sysroot</c>).</summary>
    public string? Sysroot { get; init; }

    /// <summary><c>--deny-reflection</c>: built without reflection, whatever the manifest says (docs/06 §6.1).</summary>
    public bool DenyReflection { get; init; }

    /// <summary>
    ///     <c>--deny-unsafe</c>: the build fails on any <c>unsafe</c> block or
    ///     <c>unsafe fn</c> in the workspace's own packages (SYNTAX.md §23).
    /// </summary>
    public bool DenyUnsafe { get; init; }
}

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

    /// <summary>
    ///     Resolves what a request builds, and runs the build scripts of the
    ///     packages that have one (docs/05 §5.6), for the request's profile:
    ///     what they write is part of the program every command compiles.
    /// </summary>
    internal static PackageGraph Resolve(Workspace workspace, PackageEnvironment environment, BuildRequest request) {
        ReportManifestWarnings(workspace, environment);
        var graph = PackageGraph.ResolveCached(workspace, environment, new(
            request.Features, null, request.Locked, [..Selected(workspace, request.Package).Select(static m => m.Name)],
            request.Target is { } target ? (target.Os, target.Arch) : null));
        graph.BuildOutputs = BuildScripts.Run(graph, workspace.RootManifest.ProfileNamed(request.Profile), environment);
        return graph;
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
    ///     Each file is followed by what the generators wrote for it (docs/08).
    /// </summary>
    public static IReadOnlyList<SyntaxTree> Program(
        PackageGraph graph,
        string member,
        BinTarget? bin,
        bool optimise = false,
        BuildTimings? timings = null,
        GeneratedSources? generated = null,
        NativeTarget? target = null
    ) {
        using var _ = BuildTimings.Of(timings, "parse");
        var trees = new List<SyntaxTree>();
        var own = graph.Packages[member];
        if (bin is not null) {
            trees.AddRange(ParseWithGenerated(graph, own, bin.Path, optimise, generated, target));
        }

        foreach (var package in graph.Closure(member)) {
            trees.AddRange(graph.LibrarySources(package).SelectMany(file => ParseWithGenerated(graph, package, file, optimise, generated, target)));
        }

        return trees;
    }

    /// <summary>
    ///     A member's own files, as <c>--deny-unsafe</c> holds them: its
    ///     library's and its binaries' entry files, not its dependencies'.
    /// </summary>
    static IEnumerable<SyntaxTree> OwnSources(PackageGraph graph, string member, IEnumerable<BinTarget> bins, bool optimise, NativeTarget? target) {
        var own = graph.Packages[member];
        return [
            ..bins.SelectMany(bin => ParseWithGenerated(graph, own, bin.Path, optimise, null, target)),
            ..graph.LibrarySources(own).SelectMany(file => ParseWithGenerated(graph, own, file, optimise, null, target)),
        ];
    }

    /// <summary>
    ///     What <c>sable test</c> and <c>sable bench</c> build for a member:
    ///     its library, the files under its <c>tests/</c> and <c>benches/</c>
    ///     (which an ordinary build leaves out), then its dependencies' libraries,
    ///     its dev-dependencies' among them.
    ///     The harness supplies the entry point, so no binary's entry file is in it.
    /// </summary>
    public static IReadOnlyList<SyntaxTree> TestProgram(PackageGraph graph, string member, bool optimise = false, GeneratedSources? generated = null) {
        var own = graph.Packages[member];
        return [
            ..graph.LibrarySources(own).SelectMany(file => ParseWithGenerated(graph, own, file, optimise, generated)),
            ..TestFiles(own).SelectMany(file => ParseWithGenerated(graph, own, file, optimise, generated)),
            ..graph.Closure(member, dev: true).Where(p => p.Name != member)
                .SelectMany(p => graph.LibrarySources(p).SelectMany(file => ParseWithGenerated(graph, p, file, optimise, generated))),
        ];
    }

    /// <summary>The files under a package's <c>tests/</c> and <c>benches/</c>, sorted.</summary>
    internal static IReadOnlyList<string> TestFiles(ResolvedPackage package) => [
        ..new[] { "tests", "benches" }
            .Select(d => Path.Combine(package.Package.Directory, d))
            .Where(Directory.Exists)
            .SelectMany(static d => Directory.GetFiles(d, "*.sb", SearchOption.AllDirectories))
            .Select(Path.GetFullPath)
            .Order(StringComparer.Ordinal),
    ];

    internal static SyntaxTree Parse(PackageGraph graph, ResolvedPackage package, string file, bool optimise) =>
        Parse(SourceOf(graph, package, file), optimise);

    /// <summary>A file parsed with its package's options, for <paramref name="target" /> (this machine when null).</summary>
    static SyntaxTree Parse(SourceFile source, bool optimise, NativeTarget? target = null) {
        var options = target is null ? source.Options : source.Options.ForTarget(target.Os, target.Arch);
        return SyntaxTree.Parse(new SourceText(File.ReadAllText(source.Path), source.Name), Pipeline.ParseOptionsFor(optimise, options));
    }

    /// <summary>A file, then what the generators wrote for it.</summary>
    static IEnumerable<SyntaxTree> ParseWithGenerated(PackageGraph graph, ResolvedPackage package, string file, bool optimise, GeneratedSources? generated, NativeTarget? target = null) =>
        WithGenerated(graph, package, file, generated).Select(source => Parse(source, optimise, target));

    /// <summary>A file of a package, then each file the generators wrote for it, as the package's.</summary>
    static IEnumerable<SourceFile> WithGenerated(PackageGraph graph, ResolvedPackage package, string file, GeneratedSources? generated) {
        var source = SourceOf(graph, package, file);
        yield return source;
        foreach (var path in generated?.For(file) ?? []) {
            yield return source with { Path = path, Name = Path.GetRelativePath(graph.Workspace.Root, path).Replace(Path.DirectorySeparatorChar, '/') };
        }
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
        return new(file, name, [..package.Features.Order(StringComparer.Ordinal)], package.IsLocal ? null : $"{package.Name}-{package.Version}", package.Name) {
            Generators = PackageGenerators.AttributesFor(graph, package),
        };
    }

    /// <summary>
    ///     A binary's units of separate compilation, in the order of
    ///     <see cref="Program" />: its entry file, then each package's library,
    ///     its own first. What the generators wrote is in the unit of the file
    ///     it was written for.
    /// </summary>
    public static IReadOnlyList<UnitPlan> Units(PackageGraph graph, string member, BinTarget bin, GeneratedSources? generated = null) {
        var own = graph.Packages[member];
        var units = new List<UnitPlan> { new($"bin {member}/{bin.Name}", [..WithGenerated(graph, own, bin.Path, generated)]) };
        foreach (var package in graph.Closure(member)) {
            units.Add(new($"lib {package.Name}-{package.Version}", [..graph.LibrarySources(package).SelectMany(file => WithGenerated(graph, package, file, generated))]));
        }

        return units;
    }

    /// <summary>
    ///     Runs the generators for a member's target and says what they
    ///     reported; null, having said why, when they failed.
    /// </summary>
    static GeneratedSources? Generate(PackageGraph graph, string member, PackageEnvironment environment, List<string> reported, bool optimise, BuildTimings? timings = null) {
        var generated = PackageGenerators.Run(graph, member, dev: false, optimise, timings);
        Report(environment, generated.Warnings);
        reported.AddRange(generated.Warnings);
        if (generated.Errors.Count == 0) {
            return generated;
        }

        Report(environment, generated.Errors);
        reported.AddRange(generated.Errors);
        return null;
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
            environment.Status.WriteLine($"    Checking {Describe(resolved, workspace)}");
            var targets = new List<BinTarget?>();
            if (member.LibrarySources().Count > 0) {
                targets.Add(null);
            }

            targets.AddRange(member.Bins);
            if (Generate(graph, member.Name, environment, reported, optimise: false) is not { } generated) {
                failed = true;
                continue;
            }

            // The lints look at this member's own files, each once: the
            // library with the library target, a binary's entry file with it.
            // What the generators wrote is not linted: nobody wrote it by hand.
            var ownLibrary = member.LibrarySources().Select(f => SourceOf(graph, resolved, f).Name).ToHashSet(StringComparer.Ordinal);
            foreach (var target in targets) {
                var program = Program(graph, member.Name, target, generated: generated);
                IReadOnlyList<SyntaxTree> linted = target is null ? [..program.Where(t => ownLibrary.Contains(t.Text.FileName))] : [program[0]];

                // Unchanged sources, the same lints: the same answer, read
                // rather than worked out again.
                var lints = member.Manifest.Lints.Fingerprint;
                var key = ResultCache.Key(program, $"check|{lints}|{string.Join(",", linted.Select(static t => t.Text.FileName))}");
                var cached = Path.Combine(workspace.TargetDirectory, "check", ResultCache.FileName($"{member.Name}-{target?.Name ?? "lib"}") + ".json");
                IReadOnlyList<string> errors, warnings;
                if (ResultCache.Read(cached, key) is { } hit) {
                    (errors, warnings) = hit;
                } else {
                    (errors, warnings) = Pipeline.CheckAndLint(program, member.Manifest.Lints, linted);
                    ResultCache.Write(cached, key, errors, warnings);
                }

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

        environment.Status.WriteLine($"    Finished checking in {clock.Elapsed.TotalSeconds:0.00}s");
        return 0;
    }

    /// <summary>
    ///     <c>sable fix</c>: applies what the lints of <c>sable check</c> say
    ///     how to fix, at the levels of each member's <c>[lints]</c> table,
    ///     to the member's own files — never a dependency's, nor what the
    ///     generators wrote. With <paramref name="checkOnly" />, says what it
    ///     would change instead, and fails if anything. Returns the exit code.
    /// </summary>
    public static int Fix(Workspace workspace, PackageEnvironment environment, BuildRequest request, bool checkOnly) {
        var graph = Resolve(workspace, environment, request);
        var failed = false;
        foreach (var member in Selected(workspace, request.Package)) {
            var resolved = graph.Packages[member.Name];
            environment.Error.WriteLine($"    Fixing {Describe(resolved, workspace)}");
            var targets = new List<BinTarget?>();
            if (member.LibrarySources().Count > 0) {
                targets.Add(null);
            }

            targets.AddRange(member.Bins);
            var reported = new List<string>();
            if (Generate(graph, member.Name, environment, reported, optimise: false) is not { } generated) {
                failed = true;
                continue;
            }

            var own = member.LibrarySources().ToDictionary(f => SourceOf(graph, resolved, f).Name, Path.GetFullPath, StringComparer.Ordinal);
            foreach (var target in targets) {
                var files = target is null ? own : new Dictionary<string, string>(StringComparer.Ordinal) { [SourceOf(graph, resolved, target.Path).Name] = Path.GetFullPath(target.Path) };
                failed |= LintFixer.Run(
                    () => Program(graph, member.Name, target, generated: generated),
                    program => [..program.Where(t => files.ContainsKey(t.Text.FileName))],
                    tree => files[tree.Text.FileName],
                    member.Manifest.Lints,
                    checkOnly,
                    environment.Error,
                    environment.Error) != 0;
            }
        }

        return failed ? 1 : 0;
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

        var profile = workspace.RootManifest.ProfileNamed(request.Profile);

        // Another machine's build goes to target/<triple>/<profile>/, as
        // Cargo's does, beside this machine's.
        var output = request.Target is { } target
            ? Path.Combine(workspace.TargetDirectory, target.Triple, profile.Name)
            : Path.Combine(workspace.TargetDirectory, profile.Name);
        if (request.Target is { HasRuntime: false } bare) {
            throw new PackageException(NativeBuild.NoRuntime(bare));
        }

        var built = new List<BuiltBinary>();
        var members = Selected(workspace, request.Package);

        // What the profile in use asks for and does not get is said, not
        // taken silently.
        foreach (var warning in workspace.RootManifest.Warnings.Where(w => w.Key.StartsWith($"profile.{profile.Name}.", StringComparison.Ordinal))) {
            environment.Error.WriteLine($"warning: {warning}");
        }

        foreach (var unmet in profile.UnmetFor(request.Target ?? NativeTarget.Host)) {
            environment.Error.WriteLine($"warning: {unmet}");
        }

        foreach (var member in members) {
            var bins = member.Bins.Where(b => request.Bin is null || b.Name == request.Bin).ToList();
            if (request.Bin is not null && bins.Count == 0) {
                continue;
            }

            foreach (var dependency in graph.Closure(member.Name).Skip(1)) {
                environment.Status.WriteLine($"   Compiling {Describe(dependency, workspace)}");
            }

            environment.Status.WriteLine($"   Compiling {Describe(graph.Packages[member.Name], workspace)}");
            if (request.DenyUnsafe && Pipeline.UnsafeDenied(OwnSources(graph, member.Name, bins, profile.Optimise, request.Target)) is { Count: > 0 } denied) {
                Report(environment, denied);
                DiagnosticDocs.WriteHint(environment.Error, denied);
                environment.Error.WriteLine($"error: could not compile {member.Name}");
                return null;
            }

            var reported = new List<string>();
            if (Generate(graph, member.Name, environment, reported, !profile.DebugAssertions, timings) is not { } generated) {
                DiagnosticDocs.WriteHint(environment.Error, reported);
                environment.Error.WriteLine($"error: could not compile {member.Name}");
                return null;
            }

            // '[lib] kind': the library, as one C links, beside the binaries.
            if (request.Bin is null && member.Manifest.LibKind is { } libraryKind && member.LibraryFile is not null) {
                if (!BuildNativeLibrary(graph, member, libraryKind, profile, request, generated, output, environment, timings)) {
                    environment.Error.WriteLine($"error: could not compile {member.Name}");
                    return null;
                }

                if (bins.Count == 0) {
                    continue;
                }
            }

            if (bins.Count == 0) {
                var (errors, warnings) = Pipeline.Diagnose(Program(graph, member.Name, null, !profile.DebugAssertions, generated: generated, target: request.Target));
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
                var executable = Path.Combine(output, bin.Name + (request.Target ?? NativeTarget.Host).ExecutableSuffix);
                var buildSettings = SettingsFor(graph, member.Name, profile, request, profile.Debug ? workspace.Root : null);
                var outcome = IncrementalBuild.Build(Units(graph, member.Name, bin, generated), buildSettings, executable, Path.Combine(output, "cache"), timings, environment.Error, environment.BuildCache);
                Report(environment, outcome.Warnings);
                if (!outcome.Succeeded) {
                    Report(environment, outcome.Errors);
                    DiagnosticDocs.WriteHint(environment.Error, outcome.Errors);
                    environment.Error.WriteLine($"error: could not compile {member.Name}");
                    return null;
                }

                if (timings is not null) {
                    environment.Status.WriteLine($"       Built {bin.Name}: {outcome.Kind}, {outcome.Compiled} unit(s) compiled, {outcome.Reused} reused");
                }

                built.Add(new(member.Name, bin.Name, executable, profile));
            }
        }

        if (request.Bin is not null && built.Count == 0) {
            throw new PackageException($"no binary called '{request.Bin}'");
        }

        var settings = $"{(profile.Optimise ? "optimised" : "unoptimised")}{(profile.Debug ? " + debuginfo" : "")}";
        environment.Status.WriteLine($"    Finished `{profile.Name}` profile [{settings}] target(s) in {clock.Elapsed.TotalSeconds:0.00}s");
        timings?.Write(environment.Error);
        return built;
    }

    /// <summary>
    ///     A member's library built as a native one (<c>[lib] kind</c>), to
    ///     <c>target/&lt;profile&gt;/</c>, named for the package with
    ///     <c>-</c> made <c>_</c>, as C's linkers take names: the library and its
    ///     dependencies, with no entry point, linked with the runtime. Built as
    ///     a binary is (<see cref="IncrementalBuild" />): each package a unit,
    ///     cached, where the profile compiles separately — the member's own
    ///     unit setting up the globals as the library loads — and else whole.
    /// </summary>
    static bool BuildNativeLibrary(
        PackageGraph graph,
        LocalPackage member,
        LibraryKind kind,
        Profile profile,
        BuildRequest request,
        GeneratedSources generated,
        string output,
        PackageEnvironment environment,
        BuildTimings? timings
    ) {
        if (!NativeObjects.CompilerAvailable) {
            environment.Error.WriteLine($"error: cannot run '{NativeBuild.CompilerPath}'; set CC to a clang that can link for this platform");
            return false;
        }

        Directory.CreateDirectory(output);
        var path = Path.Combine(output, NativeBuild.LibraryFileName(member.Name.Replace('-', '_'), kind, request.Target ?? NativeTarget.Host));
        var settings = SettingsFor(graph, member.Name, profile, request, profile.Debug ? member.Directory : null);
        settings = settings with { Library = kind, Native = settings.Native with { PositionIndependent = true } };
        var outcome = IncrementalBuild.Build(LibraryUnits(graph, member.Name, generated), settings, path, Path.Combine(output, "cache"), timings, environment.Error, environment.BuildCache);
        Report(environment, outcome.Warnings);
        if (!outcome.Succeeded) {
            Report(environment, outcome.Errors);
            DiagnosticDocs.WriteHint(environment.Error, outcome.Errors);
            return false;
        }

        environment.Status.WriteLine(timings is null
            ? $"       Built {Path.GetFileName(path)}"
            : $"       Built {Path.GetFileName(path)}: {outcome.Kind}, {outcome.Compiled} unit(s) compiled, {outcome.Reused} reused");
        return true;
    }

    /// <summary>A library's units (<see cref="Units" />): the member's own library first, which leads it, then each dependency's.</summary>
    public static IReadOnlyList<UnitPlan> LibraryUnits(PackageGraph graph, string member, GeneratedSources? generated = null) => [
        ..graph.Closure(member).Select(package =>
            new UnitPlan($"lib {package.Name}-{package.Version}", [..graph.LibrarySources(package).SelectMany(file => WithGenerated(graph, package, file, generated))])),
    ];

    /// <summary>What a member's targets are built with under <paramref name="profile" />: its settings, and what the build scripts found for the link.</summary>
    static BuildSettings SettingsFor(PackageGraph graph, string member, Profile profile, BuildRequest request, string? debugDirectory) =>
        new(profile.Name, profile.Optimise, debugDirectory, profile.Exclusivity) {
            Native = profile.Native with {
                Target = request.Target,
                Sysroot = request.Sysroot,
                LinkArguments = graph.LinkArguments(member),
                LinkInputs = graph.LinkInputs(member),
            },
            Whole = profile.CodegenUnits == 1 || !profile.Incremental,
            ArcChecks = profile.ArcChecks,
            DebugAssertions = profile.DebugAssertions,
            OverflowChecks = profile.OverflowChecks,
            DenyReflection = DeniesReflection(graph.Workspace, request),
        };

    /// <summary>
    ///     Whether the build carries no reflection: <c>--deny-reflection</c>,
    ///     or the root manifest's settings for the target built for
    ///     (<c>[targets.&lt;triple&gt;] reflection = false</c>).
    /// </summary>
    static bool DeniesReflection(Workspace workspace, BuildRequest request) =>
        request.DenyReflection || workspace.RootManifest.TargetSettingsFor(request.Target ?? NativeTarget.Host) is { Reflection: false };

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

        environment.Status.WriteLine($"     Running `{Path.GetRelativePath(workspace.Root, binary.Path)}`");
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
            environment.Status.WriteLine($"     Removed {workspace.Relative(workspace.TargetDirectory)}");
        }
    }
}
