using System.Diagnostics;
using Sable.CodeGen;
using Sable.SIL;
using Sable.Syntax;

namespace Sable.Driver.Commands;

/// <summary>
///     <c>sable build &lt;file.sb&gt;</c>: one file to a native executable.
///     <see cref="RunCommand" /> shares it, and runs what it built.
/// </summary>
public class BuildCommand : Command {
    public override string Name => "build";
    public override string Summary => "compile to a native executable";

    public override string Usage => """
        usage: sable build [--release] [--features <a,b>] [-p <package>] [--bin <name>]
               sable build <file.sb> [-o <output>] [--emit sil|llvm-ir] [-O0] [-g] [--exclusivity=checked|unchecked]

        Without a file, builds the package in this directory or the nearest above it (sable.toml).

        """ + PackageCommand.BuildOptions + """

        With a file, builds just that file:

          -o <output>        the executable's name (default: the file's)
          --emit sil         print the verified SIL instead of building
          --emit llvm-ir     print the LLVM IR instead of building
          -O0                build without LLVM's optimiser
          -g                 build with debug information, for lldb
          --exclusivity=...  keep the dynamic exclusivity checks, or drop them; by default
                             kept in a debug or -O0 build, dropped in an optimised one
        """;

    /// <summary>What the command line asked for.</summary>
    protected sealed record Options(string Input, string? Output, string? Emit, bool Optimise, bool Debug, bool? Exclusivity, IReadOnlyList<string> ProgramArguments);

    protected virtual bool TakesProgramArguments => false;

    protected Options? Parse(IReadOnlyList<string> args) {
        string? input = null;
        string? output = null;
        string? emit = null;
        var optimise = true;
        var debug = false;
        bool? exclusivity = null;
        var arguments = new List<string>();

        for (var i = 0; i < args.Count; i++) {
            switch (args[i]) {
                case "--" when TakesProgramArguments:
                    // Everything after '--' is the program's.
                    arguments.AddRange(args.Skip(i + 1));
                    i = args.Count;
                    break;
                case "-o" when i + 1 < args.Count && !TakesProgramArguments: output = args[++i]; break;
                case "--emit" when i + 1 < args.Count && !TakesProgramArguments: emit = args[++i]; break;
                case "-O0": optimise = false; break;
                case "-g": debug = true; break;
                case "--exclusivity=checked": exclusivity = true; break;
                case "--exclusivity=unchecked": exclusivity = false; break;
                default:
                    if (input is not null || args[i].StartsWith('-')) {
                        return null;
                    }

                    input = args[i];
                    break;
            }
        }

        return input is null || emit is not (null or "sil" or "llvm-ir")
            ? null
            : new(input, output, emit, optimise, debug, exclusivity, arguments);
    }

    public override int Run(IReadOnlyList<string> arguments) {
        if (PackageCommand.IsPackageMode(arguments)) {
            return PackageCommand.BuildPackage(arguments, Usage);
        }

        if (Parse(arguments) is not { } options) {
            return BadUsage();
        }

        return Build(options, options.Output ?? Path.GetFileNameWithoutExtension(options.Input)) is { } failure ? Math.Max(failure, 0) : 0;
    }

    /// <summary>Builds <paramref name="executable" />; null when built, -1 when --emit printed a stage instead, else the exit code to fail with.</summary>
    protected static int? Build(Options options, string executable) {
        if (!File.Exists(options.Input)) {
            Console.Error.WriteLine($"sable: no such file '{options.Input}'");
            return 1;
        }

        var source = new SourceText(File.ReadAllText(options.Input), Path.GetFileName(options.Input));
        // Dynamic exclusivity checks: on in a debug build, off in a release one,
        // unless asked for (docs/02 §2.3).
        var result = Pipeline.Compile(
            [SyntaxTree.Parse(source, Pipeline.ParseOptionsFor(options.Optimise))],
            options.Debug ? Path.GetDirectoryName(Path.GetFullPath(options.Input)) : null,
            options.Exclusivity ?? (options.Debug || !options.Optimise)
        );
        Report(result.Warnings);

        // --emit shows a stage's output, even when a later stage could not run.
        if (options.Emit == "sil" && result.Sil is { } sil) {
            Console.Write(SilPrinter.Print(sil));
            Report(result.Errors);
            return result.Errors.Count == 0 ? -1 : 1;
        }

        if (!result.Succeeded) {
            Report(result.Errors);
            return 1;
        }

        if (options.Emit == "llvm-ir") {
            Console.Write(result.LlvmIr);
            return -1;
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

    protected static void Report(IReadOnlyList<string> diagnostics) {
        foreach (var diagnostic in diagnostics) {
            Console.Error.WriteLine(diagnostic);
            Console.Error.WriteLine();
        }
    }

    /// <summary>Runs <paramref name="executable" /> and returns its exit code.</summary>
    protected static int Execute(string executable, IReadOnlyList<string> arguments) {
        var start = new ProcessStartInfo(executable);
        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        process.WaitForExit();
        return process.ExitCode;
    }
}

/// <summary><c>sable run &lt;file.sb&gt;</c>: build to a temporary executable, run it, and exit with its code.</summary>
public sealed class RunCommand : BuildCommand {
    public override string Name => "run";
    public override string Summary => "compile and run, exiting with the program's exit code";

    public override string Usage => """
        usage: sable run [--release] [--features <a,b>] [-p <package>] [--bin <name>] [-- <arguments>]
               sable run <file.sb> [-O0] [-g] [--exclusivity=checked|unchecked] [-- <arguments>]

        Without a file, builds and runs the package's binary (see 'sable build --help').

          what follows '--' is passed to the program
        """;

    protected override bool TakesProgramArguments => true;

    public override int Run(IReadOnlyList<string> arguments) {
        if (PackageCommand.IsPackageMode(arguments)) {
            return PackageCommand.RunPackage(arguments, Usage);
        }

        if (Parse(arguments) is not { } options) {
            return BadUsage();
        }

        var executable = Path.Combine(Path.GetTempPath(), $"sable-run-{Guid.NewGuid():N}");
        try {
            return Build(options, executable) is { } failure ? Math.Max(failure, 0) : Execute(executable, options.ProgramArguments);
        } finally {
            File.Delete(executable);
        }
    }
}
