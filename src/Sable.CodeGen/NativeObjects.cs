using System.Diagnostics;

namespace Sable.CodeGen;

/// <summary>
///     Separate compilation's half of <see cref="NativeBuild" />: one unit's
///     LLVM IR to an object file, the runtime to one, and objects linked into
///     an executable — each a run of the same <c>clang</c>.
/// </summary>
public static class NativeObjects {
    /// <summary>
    ///     What <c>clang --version</c> says, asked once per process: part of what
    ///     a cached object is keyed by, since another compiler would make another.
    /// </summary>
    public static string CompilerIdentity => Identity.Value;

    /// <summary>Whether the C compiler runs: <see cref="NativeBuild.IsAvailable" />, from the same one question.</summary>
    public static bool CompilerAvailable => !Identity.Value.EndsWith(Unavailable, StringComparison.Ordinal);

    const string Unavailable = ": unavailable";

    static readonly Lazy<string> Identity = new(() => {
        try {
            var (exitCode, output) = Run(NativeBuild.CompilerPath, ["--version"]);
            return exitCode == 0 ? $"{NativeBuild.CompilerPath}\n{output}" : NativeBuild.CompilerPath + Unavailable;
        } catch (System.ComponentModel.Win32Exception) {
            return NativeBuild.CompilerPath + Unavailable;
        }
    });

    /// <summary>The flags a unit's IR is compiled with: <c>-g</c> at <c>-O0</c> for a debug build.</summary>
    public static IReadOnlyList<string> Flags(bool optimise, bool debug) => debug ? ["-g", "-O0"] : [optimise ? "-O2" : "-O0"];

    /// <summary>Compiles one unit's IR to an object file, written whole or not at all.</summary>
    public static NativeBuildResult CompileIr(string llvmIr, string objectPath, bool optimise, bool debug) {
        var temporary = objectPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        var irPath = temporary + ".ll";
        try {
            File.WriteAllText(irPath, NativeBuild.PlatformAttributes is { } platform ? NativeBuild.WithHostAttributes(llvmIr, platform) : llvmIr);
            var (exitCode, output) = Run(NativeBuild.CompilerPath, [..Flags(optimise, debug), "-Wno-override-module", "-c", "-x", "ir", irPath, "-o", temporary]);
            if (exitCode != 0) {
                return new(false, output);
            }

            File.Move(temporary, objectPath, overwrite: true);
            return new(true, output);
        } finally {
            File.Delete(irPath);
            File.Delete(temporary);
        }
    }

    /// <summary>Compiles the runtime's C source to an object file.</summary>
    public static NativeBuildResult CompileRuntime(string objectPath, bool optimise, bool debug) {
        var temporary = objectPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        var source = temporary + ".c";
        try {
            File.WriteAllText(source, NativeBuild.RuntimeSource);
            var (exitCode, output) = Run(NativeBuild.CompilerPath, [..Flags(optimise, debug), "-c", source, "-o", temporary]);
            if (exitCode != 0) {
                return new(false, output);
            }

            File.Move(temporary, objectPath, overwrite: true);
            return new(true, output);
        } finally {
            File.Delete(source);
            File.Delete(temporary);
        }
    }

    /// <summary>Links objects into an executable, which replaces the one there only once it is whole.</summary>
    public static NativeBuildResult Link(IReadOnlyList<string> objects, string outputPath) {
        var temporary = outputPath + $".{Environment.ProcessId}.tmp";
        try {
            List<string> arguments = [..objects, "-o", temporary];

            // As NativeBuild does: a trap's backtrace reads the dynamic symbol table on Linux.
            if (OperatingSystem.IsLinux()) {
                arguments.Add("-rdynamic");
            }

            var (exitCode, output) = Run(NativeBuild.CompilerPath, arguments);
            if (exitCode != 0) {
                return new(false, output);
            }

            File.Move(temporary, outputPath, overwrite: true);
            return new(true, output);
        } finally {
            File.Delete(temporary);
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
