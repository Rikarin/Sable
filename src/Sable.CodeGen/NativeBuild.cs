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

    /// <summary>
    ///     The lldb formatter plugin (<c>tools/lldb/sable_lldb.py</c>), carried
    ///     inside this assembly so that a debug build can ship it beside the
    ///     program and <c>sable debug</c> can load it.
    /// </summary>
    public static string LldbPluginSource {
        get {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Sable.CodeGen.sable_lldb.py")
                ?? throw new InvalidOperationException("the lldb plugin is missing from Sable.CodeGen");
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
    ///     stays where the debug information says it is. The runtime is
    ///     compiled apart, optimised and without debug information — it is
    ///     not the program's code, and stepping goes over it as over a
    ///     system library. On macOS clang then writes a <c>.dSYM</c> beside
    ///     the executable, and the lldb formatter plugin goes into it, where
    ///     lldb finds it once <c>target.load-script-from-symbol-file</c> is on.
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
            var attributes = new List<string>();
            if (optimise && !debug && OperatingSystem.IsMacOS()) {
                arguments.Add("-flto");
                if (HostAttributes.Value is { } host) {
                    attributes.Add(host);
                }
            }

            // macOS's backtrace(), which a trap's backtrace and the system's
            // crash reports use, walks the frame-pointer chain: keep it in
            // every function that calls, as clang does for C there. (Without
            // it an unoptimised build's trap showed the innermost frame only.)
            if (OperatingSystem.IsMacOS()) {
                attributes.Add("\"frame-pointer\"=\"non-leaf\"");
            }

            if (attributes.Count > 0) {
                File.WriteAllText(irPath, WithHostAttributes(llvmIr, string.Join(' ', attributes)));
            }

            // A trap's backtrace names frames through dladdr, which on Linux
            // sees only the dynamic symbol table: the program's functions go there too.
            if (OperatingSystem.IsLinux()) {
                arguments.Add("-rdynamic");
            }

            var runtime = runtimePath;
            if (debug) {
                runtime = Path.Combine(directory.FullName, "sable_runtime.o");
                var (runtimeExit, runtimeOutput) = Run(CompilerPath, ["-c", "-O2", runtimePath, "-o", runtime]);
                if (runtimeExit != 0) {
                    return new(false, runtimeOutput);
                }
            }

            arguments.AddRange(["-Wno-override-module", irPath, runtime, "-o", outputPath]);

            var (exitCode, output) = Run(CompilerPath, arguments);
            if (exitCode == 0 && debug) {
                ShipLldbPlugin(outputPath);
            }

            return new(exitCode == 0, output);
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>
    ///     Puts the formatter plugin where lldb looks for a program's scripts:
    ///     in its <c>.dSYM</c>, as <c>Contents/Resources/Python/&lt;name&gt;.py</c>,
    ///     the name the executable's with what Python cannot import made <c>_</c>.
    ///     There is no such place on Linux — lldb does not read a
    ///     <c>.debug_gdb_scripts</c> section — so there it is loaded by hand.
    /// </summary>
    static void ShipLldbPlugin(string executable) {
        var dsym = executable + ".dSYM";
        if (!Directory.Exists(dsym)) {
            return;
        }

        var python = Path.Combine(dsym, "Contents", "Resources", "Python");
        Directory.CreateDirectory(python);
        var name = new string([..Path.GetFileName(executable).Select(static c => char.IsAsciiLetterOrDigit(c) ? c : '_')]);
        if (name.Length == 0 || char.IsAsciiDigit(name[0])) {
            name = "_" + name;
        }

        File.WriteAllText(Path.Combine(python, name + ".py"), LldbPluginSource);
    }

    /// <summary>
    ///     The <c>target-cpu</c> and <c>target-features</c> clang gives a C
    ///     function on this machine, or null when they cannot be had.
    /// </summary>
    /// <remarks>
    ///     LLVM inlines a function only into one built for at least its CPU
    ///     features. The runtime is compiled by clang, which says what they
    ///     are; the program's IR, written by hand, said nothing, so under LTO
    ///     the runtime's retain and release were never inlined into it. Asked
    ///     once per process, from a function compiled the way the runtime is.
    /// </remarks>
    static readonly Lazy<string?> HostAttributes = new(() => {
        var directory = Directory.CreateTempSubdirectory("sable-probe-");
        try {
            var probe = Path.Combine(directory.FullName, "probe.c");
            var output = Path.Combine(directory.FullName, "probe.ll");
            File.WriteAllText(probe, "int sable_probe(int x) { return x + 1; }\n");
            if (Run(CompilerPath, ["-O2", "-S", "-emit-llvm", probe, "-o", output]).ExitCode != 0) {
                return null;
            }

            var text = File.ReadAllText(output);
            var cpu = System.Text.RegularExpressions.Regex.Match(text, "\"target-cpu\"=\"[^\"]*\"");
            var features = System.Text.RegularExpressions.Regex.Match(text, "\"target-features\"=\"[^\"]*\"");
            return cpu.Success ? cpu.Value + (features.Success ? " " + features.Value : "") : null;
        } catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) {
            return null;
        } finally {
            directory.Delete(true);
        }
    });

    /// <summary>Every function the IR defines, given the host's attributes through one attribute group.</summary>
    public static string WithHostAttributes(string llvmIr, string attributes) {
        const string group = "#9000";
        var lines = llvmIr.Split('\n');
        for (var i = 0; i < lines.Length; i++) {
            if (!lines[i].StartsWith("define ", StringComparison.Ordinal) || ParametersEnd(lines[i]) is not (> 0 and var end)) {
                continue;
            }

            lines[i] = lines[i][..(end + 1)] + " " + group + lines[i][(end + 1)..];
        }

        return string.Join('\n', lines) + $"\nattributes {group} = {{ {attributes} }}\n";

        // The ')' closing the parameter list: after the name, which may be quoted.
        static int ParametersEnd(string line) {
            var at = line.IndexOf('@');
            if (at < 0) {
                return -1;
            }

            var i = at + 1;
            if (i < line.Length && line[i] == '"') {
                i = line.IndexOf('"', i + 1) + 1;
                if (i <= 0) {
                    return -1;
                }
            }

            i = line.IndexOf('(', i);
            if (i < 0) {
                return -1;
            }

            for (var depth = 0; i < line.Length; i++) {
                if (line[i] == '(') {
                    depth++;
                } else if (line[i] == ')' && --depth == 0) {
                    return i;
                }
            }

            return -1;
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
