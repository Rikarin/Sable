using System.Diagnostics;
using Sable.CodeGen;
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
                    } else if (argument.EndsWith(".sab", StringComparison.Ordinal)) {
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

    /// <summary>
    ///     The source files to build: those named on the command line.
    ///     Null, having said why, when there are none to build.
    /// </summary>
    /// <remarks>
    ///     TODO(packages): with no file named, this is where a package's
    ///     sources plug in — find <c>sable.toml</c> from the current directory
    ///     up, and give every <c>.sab</c> file of the package (with its test
    ///     files, which an ordinary build leaves out). Everything after this
    ///     takes a list of files already.
    /// </remarks>
    protected virtual IReadOnlyList<string>? InputFiles(IReadOnlyList<string> named) {
        if (named.Count == 0) {
            Console.Error.WriteLine($"sable: '{Name}' needs a file to build until packages are supported: 'sable {Name} <file.sab>'");
            return null;
        }

        foreach (var file in named.Where(static f => !File.Exists(f))) {
            Console.Error.WriteLine($"sable: no such file '{file}'");
            return null;
        }

        return named;
    }

    public override int Run(IReadOnlyList<string> arguments) {
        if (Parse(arguments) is not { } options) {
            return BadUsage();
        }

        if (InputFiles(options.Files) is not { } files) {
            return 1;
        }

        var executable = Path.Combine(Path.GetTempPath(), $"sable-{Name}-{Guid.NewGuid():N}");
        try {
            return Build(files, options, executable) ?? Execute(executable, options.HarnessArguments);
        } finally {
            File.Delete(executable);
        }
    }

    /// <summary>Builds the harness; null when built, else the exit code to fail with.</summary>
    protected int? Build(IReadOnlyList<string> files, Options options, string executable) {
        var sources = files.Select(static f => new SourceText(File.ReadAllText(f), Path.GetFileName(f))).ToList();
        var result = TestHarness.Compile(
            sources,
            Kind,
            out _,
            options.DocExamples,
            options.Debug ? Path.GetDirectoryName(Path.GetFullPath(files[0])) : null,
            options.Exclusivity ?? (options.Debug || !options.Optimise)
        );
        if (!result.Succeeded) {
            foreach (var error in result.Errors) {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine();
            }

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
