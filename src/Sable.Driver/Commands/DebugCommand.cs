using System.Diagnostics;
using Sable.CodeGen;
using Sable.Driver.Packages;
using Sable.Syntax;

namespace Sable.Driver.Commands;

/// <summary>
///     <c>sable debug</c>: build with debug information and start lldb on the
///     program, with the Sable formatters loaded (docs/04 Phase 5), so that a
///     <c>String</c> shows its text, a list its elements, a union its case.
///     In a package it builds the package's binary with its dev profile, as
///     <c>sable run</c> would; given a file, just that file with <c>-g</c>.
/// </summary>
/// <remarks>
///     lldb is <c>$LLDB</c> when it is set, otherwise <c>lldb</c> on the path.
///     <c>--batch</c> and <c>-o</c> are handed to it, for scripts and tests:
///     <c>sable debug x.sb --batch -o "b x.sb:3" -o run -o "frame variable"</c>.
/// </remarks>
public sealed class DebugCommand : PackageCommand {
    public override string Name => "debug";
    public override string Summary => "build with debug information and run it under lldb, with Sable's formatters";

    public override string Usage => """
        usage: sable debug [-p <package>] [--bin <name>] [--features <a,b>] [--batch] [-o <lldb command>]... [-- <arguments>]
               sable debug <file.sb> [--batch] [-o <lldb command>]... [-- <arguments>]
               sable debug --write-plugin <path.py>

          (no file)          in a package: build its binary with the dev profile, and debug that
          -p, --bin, --features, --no-default-features, --all-features, --locked
                             choose the binary and its features, as 'sable run' does
          --batch            have lldb run the -o commands and exit, as 'lldb --batch' does
          -o <command>       an lldb command to run once the program is loaded; repeatable
          what follows '--' is passed to the program
          --write-plugin     write the lldb formatter plugin to <path.py> and exit, for an
                             editor's debugger to load with 'command script import'
        """;

    static string LldbPath => System.Environment.GetEnvironmentVariable("LLDB") is { Length: > 0 } lldb ? lldb : "lldb";

    public override int Run(IReadOnlyList<string> arguments) {
        if (arguments is ["--write-plugin", var pluginPath]) {
            if (Path.GetDirectoryName(Path.GetFullPath(pluginPath)) is { } parent) {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllText(pluginPath, NativeBuild.LldbPluginSource);
            return 0;
        }

        string? input = null;
        var batch = false;
        var commands = new List<string>();
        var programArguments = new List<string>();
        var packageOptions = new List<string>();
        for (var i = 0; i < arguments.Count; i++) {
            switch (arguments[i]) {
                case "--":
                    programArguments.AddRange(arguments.Skip(i + 1));
                    i = arguments.Count;
                    break;
                case "--batch": batch = true; break;
                case "-o" when i + 1 < arguments.Count: commands.Add(arguments[++i]); break;
                case "-p" or "--package" or "--bin" or "--features" or "-F" when i + 1 < arguments.Count:
                    packageOptions.Add(arguments[i]);
                    packageOptions.Add(arguments[++i]);
                    break;
                case "--no-default-features" or "--all-features" or "--locked":
                    packageOptions.Add(arguments[i]);
                    break;
                default:
                    if (input is not null || arguments[i].StartsWith('-')) {
                        return BadUsage();
                    }

                    input = arguments[i];
                    break;
            }
        }

        if (input is null) {
            if (ParseBuildRequest(packageOptions) is not { } request || Workspace.FindManifestDirectory(Directory.GetCurrentDirectory()) is null) {
                return BadUsage();
            }

            string? executable = null;
            var built = Guard(() => {
                executable = BuildPackage(request);
                return executable is null ? 1 : 0;
            });
            return built != 0 ? built : Launch(executable!, batch, commands, programArguments);
        }

        if (packageOptions.Count > 0) {
            return BadUsage();
        }

        if (!File.Exists(input)) {
            Console.Error.WriteLine($"sable: no such file '{input}'");
            return 1;
        }

        var source = new SourceText(File.ReadAllText(input), Path.GetFileName(input));
        var result = Pipeline.Compile([SyntaxTree.Parse(source, Pipeline.ParseOptionsFor(optimise: false))], Path.GetDirectoryName(Path.GetFullPath(input)));
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

        var directory = Directory.CreateTempSubdirectory("sable-debug-");
        try {
            var executable = Path.Combine(directory.FullName, Path.GetFileNameWithoutExtension(input));
            var build = NativeBuild.Build(result.LlvmIr!, executable, optimise: false, debug: true);
            if (!build.Succeeded) {
                Console.Error.WriteLine("internal error: the native build failed:");
                Console.Error.Write(build.Output);
                return 1;
            }

            return Launch(executable, batch, commands, programArguments);
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>
    ///     Builds the package's one binary (or the one asked for) with the dev
    ///     profile, which has debug information unless the manifest turned it
    ///     off; null when it did not build.
    /// </summary>
    static string? BuildPackage(BuildRequest request) {
        var workspace = LoadWorkspace();
        var candidates = PackageBuilder.Selected(workspace, request.Package)
            .SelectMany(static m => m.Bins.Select(b => (Member: m, Bin: b)))
            .Where(c => request.Bin is null || c.Bin.Name == request.Bin)
            .ToList();
        switch (candidates.Count) {
            case 0:
                throw new PackageException(request.Bin is null ? "there is no binary to debug: a package needs src/main.sb or a [[bin]]" : $"no binary called '{request.Bin}'");
            case > 1:
                throw new PackageException($"there are several binaries; say which with --bin: {string.Join(", ", candidates.Select(static c => c.Bin.Name))}");
        }

        var (member, bin) = candidates[0];
        if (!workspace.RootManifest.ProfileFor(false).Debug) {
            Console.Error.WriteLine("warning: the dev profile has 'debug = false', so lldb will see no source lines or variables");
        }

        return PackageBuilder.Build(workspace, Environment, request with { Release = false, Package = member.Name, Bin = bin.Name }) is [var binary] ? binary.Path : null;
    }

    /// <summary>Starts lldb on <paramref name="executable" /> with the formatters, and waits for it.</summary>
    static int Launch(string executable, bool batch, IReadOnlyList<string> commands, IReadOnlyList<string> programArguments) {
        var directory = Directory.CreateTempSubdirectory("sable-debug-plugin-");
        try {
            var plugin = Path.Combine(directory.FullName, "sable_lldb.py");
            File.WriteAllText(plugin, NativeBuild.LldbPluginSource);

            // The formatters are registered before the program is loaded. The
            // copy in a macOS .dSYM would be the same, so lldb is told not to
            // load it — and not to warn that it did not.
            var start = new ProcessStartInfo(LldbPath);
            if (batch) {
                start.ArgumentList.Add("--batch");
            }

            start.ArgumentList.Add("--one-line-before-file");
            start.ArgumentList.Add("settings set target.load-script-from-symbol-file false");
            start.ArgumentList.Add("--one-line-before-file");
            start.ArgumentList.Add($"command script import \"{plugin}\"");
            foreach (var command in commands) {
                start.ArgumentList.Add("-o");
                start.ArgumentList.Add(command);
            }

            start.ArgumentList.Add("--");
            start.ArgumentList.Add(executable);
            foreach (var argument in programArguments) {
                start.ArgumentList.Add(argument);
            }

            Process process;
            try {
                process = Process.Start(start) ?? throw new InvalidOperationException();
            } catch (System.ComponentModel.Win32Exception) {
                Console.Error.WriteLine($"sable: cannot run '{LldbPath}'; install lldb or set LLDB to it");
                return 1;
            }

            using (process) {
                process.WaitForExit();
                return process.ExitCode;
            }
        } finally {
            directory.Delete(true);
        }
    }
}
