using System.Diagnostics;
using Sable.CodeGen;
using Sable.SIL;
using Sable.Driver.Build;
using Sable.Driver.Generators;
using Sable.Driver.Packages;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Driver.Commands;

/// <summary>
///     What <c>sable test</c> and <c>sable bench</c> share: build the
///     files with the harness the compiler writes (<see cref="TestHarness" />),
///     run it with the filters and options given, and exit as it does.
///     <c>sable fuzz</c> finds its program the same way, and builds it its own.
/// </summary>
public abstract class HarnessCommand : Command {
    protected abstract TestKind Kind { get; }

    /// <summary>Optimised unless asked otherwise: a benchmark is, a test is not.</summary>
    protected abstract bool OptimiseByDefault { get; }

    /// <summary>The harness's options this command passes on as they are, and whether each takes a value.</summary>
    protected abstract IReadOnlyDictionary<string, bool> HarnessOptions { get; }

    /// <summary>Whether the build counts the lines the program's code runs (<c>sable test --coverage</c>).</summary>
    protected virtual bool MeasureCoverage => false;

    /// <summary>
    ///     Which of the standard library's files to count coverage in, by the
    ///     names they are embedded with (<c>sable test --cover</c>); null to
    ///     count the program's own code.
    /// </summary>
    protected virtual Func<string, bool>? CoveredLibrary => null;

    /// <summary>The counters of the harness just built, when it measures coverage.</summary>
    protected CoverageMap? CoverageCounters { get; private set; }

    /// <summary>The directory of what is built: the workspace's root, or the first file's.</summary>
    protected string ProgramDirectory { get; private set; } = ".";

    /// <summary>Where what is built of it goes: its <c>target/</c>.</summary>
    protected string TargetDirectory { get; private set; } = "target";

    /// <summary>'--bless': what is compared with a file is written to it instead.</summary>
    protected bool Bless { get; private set; }

    /// <summary>What the command line asked for.</summary>
    protected sealed record Options(
        IReadOnlyList<string> Files,
        IReadOnlyList<string> HarnessArguments,
        bool DocExamples,
        bool Optimise,
        bool Debug,
        bool? Exclusivity,
        bool Bless = false) {
        /// <summary>'--harness-out': where to write the harness, built and not run (to debug a test with).</summary>
        public string? Output { get; init; }
    }

    protected Options? Parse(IReadOnlyList<string> args) {
        var files = new List<string>();
        var harness = new List<string>();
        var doc = false;
        var optimise = OptimiseByDefault;
        var debug = false;
        var bless = false;
        bool? exclusivity = null;
        string? output = null;
        for (var i = 0; i < args.Count; i++) {
            var argument = args[i];
            switch (argument) {
                case "--":
                    harness.AddRange(args.Skip(i + 1));
                    i = args.Count;
                    break;
                case "--doc" when Kind == TestKind.Test: doc = true; break;
                case "--bless" when Kind == TestKind.Test: bless = true; break;
                case "-O0": optimise = false; break;
                case "--release": optimise = true; break;
                case "-g": debug = true; break;
                case "--exclusivity=checked": exclusivity = true; break;
                case "--exclusivity=unchecked": exclusivity = false; break;
                case "--harness-out":
                    if (i + 1 >= args.Count) {
                        return null;
                    }

                    output = Path.GetFullPath(args[++i]);
                    break;
                default:
                    if (HarnessOptions.TryGetValue(argument, out var takesValue)) {
                        harness.Add(argument);
                        if (takesValue) {
                            if (i + 1 >= args.Count) {
                                return null;
                            }

                            harness.Add(args[++i]);
                        }
                    } else if (argument.StartsWith('-')) {
                        return null;
                    } else if (argument.EndsWith(".sb", StringComparison.Ordinal)) {
                        files.Add(argument);
                    } else if (Directory.Exists(argument)) {
                        // A directory stands for every source file under it, as one program.
                        var under = Directory.GetFiles(argument, "*.sb", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
                        if (under.Count == 0) {
                            Console.Error.WriteLine($"sable: no .sb files under '{argument}'");
                            return null;
                        }

                        files.AddRange(under);
                    } else {
                        // Anything else selects tests by name.
                        harness.Add(argument);
                    }

                    break;
            }
        }

        return new(files, harness, doc, optimise, debug, exclusivity, bless) { Output = output };
    }

    public override int Run(IReadOnlyList<string> arguments) {
        if (Parse(arguments) is not { } options) {
            return BadUsage();
        }

        // A package's generator snapshots run first (docs/08 §8.8); a package
        // of generators alone has nothing else to test.
        var snapshots = Kind == TestKind.Test && options.Files.Count == 0 ? Snapshots(options.Bless) : null;
        if (snapshots is { HasSources: false } only) {
            return only.Outcome.Failed == 0 ? 0 : 1;
        }

        if (Program(options) is not { } program) {
            return 1;
        }

        // Built to be run here, and then gone; or, with '--harness-out',
        // written there and kept, for a debugger to start.
        var executable = options.Output ?? Path.Combine(Path.GetTempPath(), $"sable-{Name}-{Guid.NewGuid():N}{NativeTarget.Host.ExecutableSuffix}");
        ProgramDirectory = program.Directory;
        TargetDirectory = program.TargetDirectory;
        Bless = options.Bless;
        try {
            var code = Build(program.Trees, program.Directory, options, executable, program.Cache, program.Link, program.LinkInputs)
                ?? (options.Output is null ? Execute(executable, options.HarnessArguments) : 0);
            return snapshots is { Outcome.Failed: > 0 } ? 1 : code;
        } finally {
            if (options.Output is null) {
                File.Delete(executable);
            }
        }
    }

    /// <summary>
    ///     The generator snapshot cases of the package in this directory, run;
    ///     null when it has none (or there is no package here).
    /// </summary>
    static (SnapshotOutcome Outcome, bool HasSources)? Snapshots(bool bless) {
        try {
            var workspace = Workspace.Load(Directory.GetCurrentDirectory());
            var member = workspace.Current ?? workspace.Members[0];
            if (GeneratorSnapshots.Run(member.Name, member.Directory, bless, Console.Out) is not { } outcome) {
                return null;
            }

            var hasSources = member.LibrarySources().Count > 0
                || new[] { "tests", "benches" }.Any(d => Directory.Exists(Path.Combine(member.Directory, d)));
            return (outcome, hasSources);
        } catch (Exception e) when (e is PackageException or ManifestException or IOException) {
            return null;
        }
    }

    /// <summary>
    ///     What to build: the files named on the command line or, with none,
    ///     the package in this directory or the nearest one above it — its
    ///     library with its <c>tests/</c> and <c>benches/</c> files, and its
    ///     dependencies'. Null, having said why, when there is nothing to build.
    /// </summary>
    /// <remarks>
    ///     A package's harness is kept in <c>target/harness/</c> (<see cref="Build" />); files named on the command line have no cache.
    ///     <c>TargetDirectory</c> is the package's <c>target/</c>, or one beside the files.
    /// </remarks>
    protected static (IReadOnlyList<SyntaxTree> Trees, string Directory, string? Cache, IReadOnlyList<string> Link, string LinkInputs, string TargetDirectory)? Program(Options options) {
        if (options.Files.Count > 0) {
            foreach (var file in options.Files.Where(static f => !File.Exists(f))) {
                Console.Error.WriteLine($"sable: no such file '{file}'");
                return null;
            }

            var parseOptions = Pipeline.ParseOptionsFor(options.Optimise);
            var directory = Path.GetDirectoryName(Path.GetFullPath(options.Files[0]))!;
            // Each file by its name, from the first one's directory: those
            // under it or beside it are told apart.
            return ([..options.Files.Select(f => SyntaxTree.Parse(new SourceText(File.ReadAllText(f), Path.GetRelativePath(directory, Path.GetFullPath(f)).Replace('\\', '/')), parseOptions))],
                directory, null, [], "", Path.Combine(directory, "target"));
        }

        try {
            var workspace = Workspace.Load(Directory.GetCurrentDirectory());
            var environment = PackageEnvironment.FromProcess();
            var member = workspace.Current ?? workspace.Members[0];
            var graph = PackageBuilder.Resolve(workspace, environment, new BuildRequest(Package: member.Name));
            // The tests' generators too: those of the dev-dependencies (docs/08 §8.2).
            var generated = PackageGenerators.Run(graph, member.Name, dev: true, options.Optimise);
            foreach (var message in generated.Warnings.Concat(generated.Errors)) {
                Console.Error.WriteLine(message);
                Console.Error.WriteLine();
            }

            if (generated.Errors.Count > 0) {
                Console.Error.WriteLine($"error: could not compile {member.Name}");
                return null;
            }

            var trees = PackageBuilder.TestProgram(graph, member.Name, options.Optimise, generated);
            if (trees.Count == 0) {
                Console.Error.WriteLine($"sable: package '{member.Name}' has no source files");
                return null;
            }

            // What the package's build scripts found for the link: the harness links it too.
            return (trees, workspace.Root, Path.Combine(workspace.TargetDirectory, "harness", ResultCache.FileName(member.Name)),
                graph.LinkArguments(member.Name), graph.LinkInputs(member.Name), workspace.TargetDirectory);
        } catch (PackageException failure) {
            Console.Error.WriteLine($"error: {failure.Message}");
            return null;
        }
    }

    /// <summary>Builds the harness; null when built, else the exit code to fail with.</summary>
    /// <param name="cache">
    ///     Where the harness built last is kept, with the key of what it was
    ///     built from: run again on unchanged sources with the same options,
    ///     it is copied from there rather than compiled again. (Not with
    ///     debug information, which names the executable's own path.)
    /// </param>
    /// <param name="link">More for the link, from build scripts; <paramref name="linkInputs" /> hashes what it brings in.</param>
    protected int? Build(IReadOnlyList<SyntaxTree> trees, string directory, Options options, string executable, string? cache = null,
        IReadOnlyList<string>? link = null, string linkInputs = "") {
        link ??= [];
        string? kept = null, record = null, key = null;

        // A build measured for coverage is not kept: its counters are this build's.
        if (cache is not null && !options.Debug && !MeasureCoverage) {
            kept = Path.Combine(cache, Name);
            record = kept + ".json";
            key = ResultCache.Key(trees, $"{Name}|{Kind}|doc={options.DocExamples}|opt={options.Optimise}|exclusivity={options.Exclusivity}|link={string.Join(' ', link)}|{linkInputs}");
            if (File.Exists(kept) && ResultCache.Read(record, key) is { } hit) {
                foreach (var warning in hit.Warnings) {
                    Console.Error.WriteLine(warning);
                    Console.Error.WriteLine();
                }

                File.Copy(kept, executable, overwrite: true);
                return null;
            }
        }

        var result = TestHarness.Compile(
            trees,
            Kind,
            out _,
            options.DocExamples,
            options.Debug ? directory : null,
            options.Exclusivity ?? (options.Debug || !options.Optimise),
            MeasureCoverage && CoveredLibrary is null ? file => IsUnder(file, directory) : null,
            MeasureCoverage ? CoveredLibrary : null
        );
        CoverageCounters = result.Coverage;
        foreach (var warning in result.Warnings) {
            Console.Error.WriteLine(warning);
            Console.Error.WriteLine();
        }

        if (!result.Succeeded) {
            foreach (var error in result.Errors) {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine();
            }

            DiagnosticDocs.WriteHint(Console.Error, result.Errors);
            return 1;
        }

        if (!NativeBuild.IsAvailable()) {
            Console.Error.WriteLine($"sable: cannot run '{NativeBuild.CompilerPath}'; set CC to a clang that can link for this platform");
            return 1;
        }

        var build = NativeBuild.Build(result.LlvmIr!, executable, NativeOptions.Of(options.Optimise, options.Debug) with { LinkArguments = link });
        if (!build.Succeeded) {
            Console.Error.WriteLine("internal error: the native build failed:");
            Console.Error.Write(build.Output);
            return 1;
        }

        if (kept is not null) {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
                File.Copy(executable, kept, overwrite: true);
                ResultCache.Write(record!, key!, [], result.Warnings);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                // A cache that cannot be written is only slower next time.
            }
        }

        return null;
    }

    /// <summary>
    ///     Whether a file is the program's own, to measure: under its
    ///     directory but not its build's output, or a file named on the
    ///     command line (whose name has no directory).
    /// </summary>
    static bool IsUnder(string file, string directory) {
        if (!Path.IsPathRooted(file)) {
            return !file.StartsWith('<');
        }

        var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
        return !relative.StartsWith("../", StringComparison.Ordinal) && !relative.StartsWith("target/", StringComparison.Ordinal)
            && !relative.StartsWith("vendor/", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    /// <summary>Runs the built harness with the arguments meant for it; returns its exit code.</summary>
    protected virtual int Execute(string executable, IReadOnlyList<string> arguments) => Execute(executable, arguments, null);

    /// <param name="environment">Variables set for the harness's process only: this process's own are shared by every command it runs.</param>
    protected static int Execute(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment) {
        var start = new ProcessStartInfo(executable);
        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        process.WaitForExit();
        return process.ExitCode;
    }
}
