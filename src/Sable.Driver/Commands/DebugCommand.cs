using System.Diagnostics;
using Sable.CodeGen;
using Sable.Syntax;

namespace Sable.Driver.Commands;

/// <summary>
///     <c>sable debug &lt;file.sb&gt;</c>: build with debug information and
///     start lldb on the program, with the Sable formatters loaded
///     (docs/04 Phase 5), so that a <c>String</c> shows its text, a list its
///     elements, a union its case.
/// </summary>
/// <remarks>
///     lldb is <c>$LLDB</c> when it is set, otherwise <c>lldb</c> on the path.
///     <c>--batch</c> and <c>-o</c> are handed to it, for scripts and tests:
///     <c>sable debug x.sb --batch -o "b x.sb:3" -o run -o "frame variable"</c>.
/// </remarks>
public sealed class DebugCommand : Command {
    public override string Name => "debug";
    public override string Summary => "build with debug information and run it under lldb, with Sable's formatters";

    public override string Usage => """
        usage: sable debug <file.sb> [--batch] [-o <lldb command>]... [-- <arguments>]
               sable debug --write-plugin <path.py>

          --batch            have lldb run the -o commands and exit, as 'lldb --batch' does
          -o <command>       an lldb command to run once the program is loaded; repeatable
          what follows '--' is passed to the program
          --write-plugin     write the lldb formatter plugin to <path.py> and exit, for an
                             editor's debugger to load with 'command script import'
        """;

    static string LldbPath => Environment.GetEnvironmentVariable("LLDB") is { Length: > 0 } lldb ? lldb : "lldb";

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
        for (var i = 0; i < arguments.Count; i++) {
            switch (arguments[i]) {
                case "--":
                    programArguments.AddRange(arguments.Skip(i + 1));
                    i = arguments.Count;
                    break;
                case "--batch": batch = true; break;
                case "-o" when i + 1 < arguments.Count: commands.Add(arguments[++i]); break;
                default:
                    if (input is not null || arguments[i].StartsWith('-')) {
                        return BadUsage();
                    }

                    input = arguments[i];
                    break;
            }
        }

        if (input is null) {
            return BadUsage();
        }

        if (!File.Exists(input)) {
            Console.Error.WriteLine($"sable: no such file '{input}'");
            return 1;
        }

        var source = new SourceText(File.ReadAllText(input), Path.GetFileName(input));
        var result = Pipeline.Compile(source, Path.GetDirectoryName(Path.GetFullPath(input)));
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

        var directory = Directory.CreateTempSubdirectory("sable-debug-");
        try {
            var executable = Path.Combine(directory.FullName, Path.GetFileNameWithoutExtension(input));
            var build = NativeBuild.Build(result.LlvmIr!, executable, optimise: false, debug: true);
            if (!build.Succeeded) {
                Console.Error.WriteLine("internal error: the native build failed:");
                Console.Error.Write(build.Output);
                return 1;
            }

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
