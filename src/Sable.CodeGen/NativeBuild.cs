using System.Diagnostics;
using System.Reflection;

namespace Sable.CodeGen;

/// <summary>The platform a program is built for, which decides some of clang's flags: today always the host.</summary>
public enum NativePlatform {
    MacOS,
    Linux,
    Other,
}

/// <summary>Link-time optimisation: none, ThinLTO, or the whole program as one module.</summary>
public enum LtoMode {
    Off,
    Thin,
    Fat,
}

/// <summary>What the link leaves out of the executable.</summary>
public enum StripMode {
    None,
    DebugInfo,
    Symbols,
}

/// <summary>
///     What clang is asked for: the settings of a build profile that reach the
///     native build (docs/05 §5.2).
/// </summary>
public sealed record NativeOptions {
    /// <summary>clang's optimisation level: <c>0</c> to <c>3</c>, <c>s</c> or <c>z</c>.</summary>
    public string OptLevel { get; init; } = "2";

    /// <summary>
    ///     Compile with <c>-g</c>. At <c>-O0</c> every variable stays where the
    ///     debug information says it is; optimised, the debugger sees what is left.
    /// </summary>
    public bool DebugInfo { get; init; }

    /// <summary>Link-time optimisation, in an optimised build where the platform's linker takes LLVM bitcode.</summary>
    public LtoMode Lto { get; init; } = LtoMode.Fat;

    public StripMode Strip { get; init; } = StripMode.None;

    /// <summary>Look for shared libraries beside the executable.</summary>
    public bool Rpath { get; init; }

    public bool Optimise => OptLevel != "0";

    /// <summary>
    ///     The two builds a single file's <c>sable build</c> knows: <c>-O2</c>
    ///     or <c>-O0</c>, and a debug build, which is <c>-g</c> at <c>-O0</c>.
    /// </summary>
    public static NativeOptions Of(bool optimise, bool debug) => new() { OptLevel = optimise && !debug ? "2" : "0", DebugInfo = debug };
}

/// <summary>The outcome of turning LLVM IR into an executable.</summary>
public sealed record NativeBuildResult(bool Succeeded, string Output);

/// <summary>
///     Compiles LLVM IR and links it with the runtime into an executable, by
///     running <c>clang</c> — the tool that already knows the platform's
///     linker, its C library and its startup code.
/// </summary>
/// <remarks>
///     The compiler is <c>$CC</c> when it is set, otherwise <c>clang</c> on the
///     path. Optimisation is <c>-O2</c> unless a profile says otherwise: the
///     IR is written for clarity, every slot an <c>alloca</c>, and LLVM's own
///     passes make it fast.
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

    /// <summary>The platform this process runs on, which is the one built for.</summary>
    public static NativePlatform Host =>
        OperatingSystem.IsMacOS() ? NativePlatform.MacOS : OperatingSystem.IsLinux() ? NativePlatform.Linux : NativePlatform.Other;

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
    public static NativeBuildResult Build(string llvmIr, string outputPath, bool optimise = true, bool debug = false) =>
        Build(llvmIr, outputPath, NativeOptions.Of(optimise, debug));

    /// <summary>
    ///     Builds with a profile's settings: any optimisation level, with or
    ///     without debug information, link-time optimisation where the
    ///     platform's linker can do it, and what the link strips.
    /// </summary>
    public static NativeBuildResult Build(string llvmIr, string outputPath, NativeOptions options) {
        var directory = Directory.CreateTempSubdirectory("sable-build-");
        try {
            var irPath = Path.Combine(directory.FullName, "program.ll");
            var runtimePath = Path.Combine(directory.FullName, "sable_runtime.c");
            File.WriteAllText(irPath, llvmIr);
            File.WriteAllText(runtimePath, RuntimeSource);

            var lto = LtoFlags(options, Host, LinksBitcode);
            List<string> arguments = [..CompileFlags(options), ..lto];

            // LLVM inlines the runtime's small functions into the program only
            // into functions built for at least the runtime's CPU features.
            var attributes = new List<string>();
            if (lto.Count > 0 && HostAttributes.Value is { } host) {
                attributes.Add(host);
            }

            if (PlatformAttributes is { } platform) {
                attributes.Add(platform);
            }

            if (attributes.Count > 0) {
                File.WriteAllText(irPath, WithHostAttributes(llvmIr, string.Join(' ', attributes)));
            }

            // With debug information the runtime is compiled apart, without
            // it — and, optimised, as bitcode, so link-time optimisation still
            // inlines it.
            var runtime = runtimePath;
            if (options.DebugInfo) {
                runtime = Path.Combine(directory.FullName, "sable_runtime.o");
                var bitcode = lto.Where(static f => f.StartsWith("-flto", StringComparison.Ordinal));
                var (runtimeExit, runtimeOutput) = Run(CompilerPath, ["-c", options.Optimise ? $"-O{options.OptLevel}" : "-O2", ..bitcode, runtimePath, "-o", runtime]);
                if (runtimeExit != 0) {
                    return new(false, runtimeOutput);
                }

                // Apple's linker deletes the object link-time optimisation
                // makes, and dsymutil, which clang runs next, reads the debug
                // information from it: keep it until the build is done.
                if (lto.Count > 0 && Host == NativePlatform.MacOS) {
                    var kept = Path.Combine(directory.FullName, "lto");
                    if (options.Lto == LtoMode.Thin) {
                        Directory.CreateDirectory(kept);
                    }

                    arguments.Add("-Wl,-object_path_lto," + kept);
                }
            }

            arguments.AddRange(["-Wno-override-module", irPath, runtime, "-o", outputPath, ..LinkFlags(options, Host)]);

            var (exitCode, output) = Run(CompilerPath, arguments);
            if (exitCode == 0 && options.DebugInfo) {
                ShipLldbPlugin(outputPath);
            }

            return new(exitCode == 0, output);
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>The flags the program's IR is compiled with: its optimisation level, and <c>-g</c> for debug information.</summary>
    public static IReadOnlyList<string> CompileFlags(NativeOptions options) =>
        options.DebugInfo ? ["-g", $"-O{options.OptLevel}"] : [$"-O{options.OptLevel}"];

    /// <summary>
    ///     Link-time optimisation's flags, which let LLVM inline the runtime's
    ///     small functions into the program: in an optimised build that asks
    ///     for it, where the linker takes LLVM bitcode — Apple's does; on Linux
    ///     it is lld, as the GNU linkers need a plugin few installations have.
    /// </summary>
    public static IReadOnlyList<string> LtoFlags(NativeOptions options, NativePlatform platform, bool linksBitcode) =>
        !options.Optimise || options.Lto == LtoMode.Off || !linksBitcode ? []
        : [options.Lto == LtoMode.Thin ? "-flto=thin" : "-flto", ..platform == NativePlatform.Linux ? new[] { "-fuse-ld=lld" } : []];

    /// <summary>
    ///     What the link takes after the objects: what the platform needs, then
    ///     what the profile strips, and where it looks for shared libraries.
    /// </summary>
    public static IReadOnlyList<string> LinkFlags(NativeOptions options, NativePlatform platform) {
        List<string> flags = [..PlatformLinkFlags(platform)];

        // GNU ld's and lld's -S and -s; Apple's linker has -S for the debug
        // map, and -x for local symbols — a program's exported ones it keeps.
        flags.AddRange((options.Strip, platform) switch {
            (StripMode.DebugInfo, NativePlatform.Linux) => ["-Wl,--strip-debug"],
            (StripMode.Symbols, NativePlatform.Linux) => ["-Wl,--strip-all"],
            (StripMode.DebugInfo, NativePlatform.MacOS) => ["-Wl,-S"],
            (StripMode.Symbols, NativePlatform.MacOS) => ["-Wl,-S", "-Wl,-x"],
            _ => [],
        });

        if (options.Rpath) {
            flags.AddRange(platform switch {
                NativePlatform.Linux => ["-Wl,-rpath,$ORIGIN"],
                NativePlatform.MacOS => ["-Wl,-rpath,@executable_path"],
                _ => [],
            });
        }

        return flags;
    }

    /// <summary>
    ///     Whether this machine's link takes LLVM bitcode: always on macOS; on
    ///     Linux when clang can link through lld, which is asked once per process.
    /// </summary>
    public static bool LinksBitcode => Host switch {
        NativePlatform.MacOS => true,
        NativePlatform.Linux => LldLinks.Value,
        _ => false,
    };

    static readonly Lazy<bool> LldLinks = new(() => {
        var directory = Directory.CreateTempSubdirectory("sable-probe-");
        try {
            var probe = Path.Combine(directory.FullName, "probe.c");
            File.WriteAllText(probe, "int main(void) { return 0; }\n");
            return Run(CompilerPath, ["-O2", "-flto", "-fuse-ld=lld", probe, "-o", Path.Combine(directory.FullName, "probe")]).ExitCode == 0;
        } catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) {
            return false;
        } finally {
            directory.Delete(true);
        }
    });

    /// <summary>
    ///     What linking a Sable program needs on <paramref name="platform" />,
    ///     after its objects. The whole program's build and separate
    ///     compilation's link both take them from here, so that the two cannot
    ///     drift apart.
    /// </summary>
    public static IReadOnlyList<string> PlatformLinkFlags(NativePlatform platform) =>
        platform == NativePlatform.Linux
            // A trap's backtrace names frames through dladdr, which on Linux
            // sees only the dynamic symbol table: the program's functions go
            // there too. And 'frem' becomes a call to fmod, which glibc keeps
            // in libm, not libc.
            ? ["-rdynamic", "-lm"]
            : [];

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

    /// <summary>
    ///     What every function needs on this platform, whatever the build.
    ///     macOS's backtrace(), which a trap's backtrace and the system's crash
    ///     reports use, walks the frame-pointer chain: keep it in every
    ///     function that calls, as clang does for C there. (Without it an
    ///     unoptimised build's trap showed the innermost frame only.)
    /// </summary>
    public static string? PlatformAttributes => OperatingSystem.IsMacOS() ? "\"frame-pointer\"=\"non-leaf\"" : null;

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
