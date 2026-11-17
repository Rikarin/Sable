using System.Diagnostics;
using System.Reflection;

namespace Sable.CodeGen;

/// <summary>The outcome of turning LLVM IR into an executable.</summary>
public sealed record NativeBuildResult(bool Succeeded, string Output);

/// <summary>
///     Compiles LLVM IR and links it with the runtime into an executable, by
///     running <c>clang</c> — the tool that already knows the platform's
///     linker, its C library and its startup code.
/// </summary>
/// <remarks>
///     The compiler is <c>$CC</c> when it is set, otherwise <c>clang</c> on the
///     path. Optimisation is <c>-O2</c>: the IR is written for clarity, every
///     slot an <c>alloca</c>, and LLVM's own passes make it fast.
/// </remarks>
public static class NativeBuild {
    /// <summary>The runtime's C source, carried inside this assembly.</summary>
    public static string RuntimeSource {
        get {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Sable.CodeGen.sable_runtime.c")
                ?? throw new InvalidOperationException("the runtime source is missing from Sable.CodeGen");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    public static string CompilerPath => Environment.GetEnvironmentVariable("CC") is { Length: > 0 } cc ? cc : "clang";

    /// <summary>True when a C compiler can be run here.</summary>
    public static bool IsAvailable() {
        try {
            return Run(CompilerPath, ["--version"]).ExitCode == 0;
        } catch (System.ComponentModel.Win32Exception) {
            return false;
        }
    }

    /// <param name="debug">
    ///     Build for a debugger: <c>-g</c> at <c>-O0</c>, so that every variable
    ///     stays where the debug information says it is. On macOS clang then
    ///     writes a <c>.dSYM</c> beside the executable.
    /// </param>
    public static NativeBuildResult Build(string llvmIr, string outputPath, bool optimise = true, bool debug = false) {
        var directory = Directory.CreateTempSubdirectory("sable-build-");
        try {
            var irPath = Path.Combine(directory.FullName, "program.ll");
            var runtimePath = Path.Combine(directory.FullName, "sable_runtime.c");
            File.WriteAllText(irPath, llvmIr);
            File.WriteAllText(runtimePath, RuntimeSource);

            List<string> arguments = debug
                ? ["-g", "-O0"]
                : [optimise ? "-O2" : "-O0"];

            // Link-time optimisation lets LLVM inline the runtime's small
            // functions into the program — where the platform's linker takes
            // LLVM bitcode, as Apple's does; elsewhere it needs a plugin.
            if (optimise && !debug && OperatingSystem.IsMacOS()) {
                arguments.Add("-flto");
            }
            arguments.AddRange(["-Wno-override-module", irPath, runtimePath, "-o", outputPath]);

            var (exitCode, output) = Run(CompilerPath, arguments);

            return new(exitCode == 0, output);
        } finally {
            directory.Delete(true);
        }
    }

    static (int ExitCode, string Output) Run(string program, IEnumerable<string> arguments) {
        var start = new ProcessStartInfo(program) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {program}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr);
    }
}
