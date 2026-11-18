using System.Diagnostics;
using Sable.CodeGen;
using Sable.Driver.Packages;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Driver.Commands;

/// <summary>
///     What <c>sable test</c> and <c>sable bench</c> share: build the
///     files with the harness the compiler writes (<see cref="TestHarness" />),
///     run it with the filters and options given, and exit as it does.
/// </summary>
public abstract class HarnessCommand : Command {
    protected abstract TestKind Kind { get; }

    /// <summary>Optimised unless asked otherwise: a benchmark is, a test is not.</summary>
    protected abstract bool OptimiseByDefault { get; }

    /// <summary>The harness's options this command passes on as they are, and whether each takes a value.</summary>
    protected abstract IReadOnlyDictionary<string, bool> HarnessOptions { get; }

    /// <summary>What the command line asked for.</summary>
    protected sealed record Options(
        IReadOnlyList<string> Files,
        IReadOnlyList<string> HarnessArguments,
        bool DocExamples,
        bool Optimise,
        bool Debug,
        bool? Exclusivity);

    protected Options? Parse(IReadOnlyList<string> args) {
        var files = new List<string>();
        var harness = new List<string>();
        var doc = false;
        var optimise = OptimiseByDefault;
        var debug = false;
        bool? exclusivity = null;
        for (var i = 0; i < args.Count; i++) {
            var argument = args[i];
            switch (argument) {
                case "--":
                    harness.AddRange(args.Skip(i + 1));
                    i = args.Count;
                    break;
                case "--doc" when Kind == TestKind.Test: doc = true; break;
                case "-O0": optimise = false; break;
                case "--release": optimise = true; break;
                case "-g": debug = true; break;
                case "--exclusivity=checked": exclusivity = true; break;
                case "--exclusivity=unchecked": exclusivity = false; break;
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
                    } else {
                        // Anything else selects tests by name.
                        harness.Add(argument);
                    }

                    break;
            }
        }

        return new(files, harness, doc, optimise, debug, exclusivity);
    }

    public override int Run(IReadOnlyList<string> arguments) {
        if (Parse(arguments) is not { } options) {
            return BadUsage();
        }

        if (Program(options) is not { } program) {
            return 1;
        }

        var executable = Path.Combine(Path.GetTempPath(), $"sable-{Name}-{Guid.NewGuid():N}");
        try {
            return Build(program.Trees, program.Directory, options, executable) ?? Execute(executable, options.HarnessArguments);
        } finally {
            File.Delete(executable);
        }
    }

    /// <summary>
    ///     What to build: the files named on the command line or, with none,
    ///     the package in this directory or the nearest one above it — its
    ///     library with its <c>tests/</c> and <c>benches/</c> files, and its
    ///     dependencies'. Null, having said why, when there is nothing to build.
    /// </summary>
    static (IReadOnlyList<SyntaxTree> Trees, string Directory)? Program(Options options) {
        if (options.Files.Count > 0) {
            foreach (var file in options.Files.Where(static f => !File.Exists(f))) {
                Console.Error.WriteLine($"sable: no such file '{file}'");
                return null;
            }

            var parseOptions = Pipeline.ParseOptionsFor(options.Optimise);
            return ([..options.Files.Select(f => SyntaxTree.Parse(new SourceText(File.ReadAllText(f), Path.GetFileName(f)), parseOptions))],
                Path.GetDirectoryName(Path.GetFullPath(options.Files[0]))!);
        }

        try {
            var workspace = Workspace.Load(Directory.GetCurrentDirectory());
            var environment = PackageEnvironment.FromProcess();
            var member = workspace.Current ?? workspace.Members[0];
            var graph = PackageBuilder.Resolve(workspace, environment, new BuildRequest(Package: member.Name));
            var trees = PackageBuilder.TestProgram(graph, member.Name, options.Optimise);
            if (trees.Count == 0) {
                Console.Error.WriteLine($"sable: package '{member.Name}' has no source files");
                return null;
            }

            return (trees, workspace.Root);
        } catch (PackageException failure) {
            Console.Error.WriteLine($"error: {failure.Message}");
            return null;
        }
    }

    /// <summary>Builds the harness; null when built, else the exit code to fail with.</summary>
    protected int? Build(IReadOnlyList<SyntaxTree> trees, string directory, Options options, string executable) {
        var result = TestHarness.Compile(
            trees,
            Kind,
            out _,
            options.DocExamples,
            options.Debug ? directory : null,
            options.Exclusivity ?? (options.Debug || !options.Optimise)
        );
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

        var build = NativeBuild.Build(result.LlvmIr!, executable, options.Optimise, options.Debug);
        if (!build.Succeeded) {
            Console.Error.WriteLine("internal error: the native build failed:");
            Console.Error.Write(build.Output);
            return 1;
        }

        return null;
    }

    static int Execute(string executable, IReadOnlyList<string> arguments) {
        var start = new ProcessStartInfo(executable);
        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        process.WaitForExit();
        return process.ExitCode;
    }
}
