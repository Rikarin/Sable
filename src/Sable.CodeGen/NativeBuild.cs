using System.Diagnostics;
using System.Reflection;

namespace Sable.CodeGen;

/// <summary>The operating system a program is built for (<see cref="NativeTarget" />), which decides some of clang's flags.</summary>
public enum NativePlatform {
    MacOS,
    Linux,
    Other,
    Windows,
    Wasi,
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

    /// <summary>
    ///     More for the link, after everything else: the native libraries a
    ///     package's build script found (<c>-lfoo</c>, <c>-L dir</c>).
    /// </summary>
    public IReadOnlyList<string> LinkArguments { get; init; } = [];

    /// <summary>
    ///     A hash of what <see cref="LinkArguments" /> bring in: a library
    ///     rebuilt under the same name is a different link.
    /// </summary>
    public string LinkInputs { get; init; } = "";

    /// <summary>The settings as text, every one of them: what a cache of builds made with them is keyed by.</summary>
    public string Key => $"{OptLevel}|{DebugInfo}|{Lto}|{Strip}|{Rpath}|{Target?.Triple}|{Sysroot}|{string.Join(' ', LinkArguments)}|{LinkInputs}";
    /// <summary>What the program is built for: null for the machine building it.</summary>
    public NativeTarget? Target { get; init; }

    /// <summary>
    ///     Where the target's C library and headers are, for a target that is
    ///     not this machine: clang's <c>--sysroot</c>. Null for clang's own idea.
    /// </summary>
    public string? Sysroot { get; init; }

    public bool Optimise => OptLevel != "0";

    /// <summary>The target, the host when none is given.</summary>
    public NativeTarget TargetOrHost => Target ?? NativeTarget.Host;

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

    /// <summary>The platform this process runs on, which is the one built for unless a target says otherwise.</summary>
    public static NativePlatform Host => NativeTarget.Host.Platform;

    public static string CompilerPath => Environment.GetEnvironmentVariable("CC") is { Length: > 0 } cc ? cc : "clang";

    /// <summary>
    ///     The C compiler's LLVM major version, and whether it is Apple's
    ///     (whose numbers are its own); null when there is none, or it does
    ///     not say. Debug information leans on it where LLVMs lay out a line
    ///     table differently.
    /// </summary>
    public static (int Major, bool Apple)? CompilerVersion => Version.Value;

    static readonly Lazy<(int Major, bool Apple)?> Version = new(static () => {
        try {
            var (exitCode, output) = Run(CompilerPath, ["--version"]);
            return exitCode == 0 ? ParseVersion(output) : null;
        } catch (System.ComponentModel.Win32Exception) {
            return null;
        }
    });

    /// <summary>From <c>clang --version</c>: "Ubuntu clang version 18.1.3 (1ubuntu1)", "Apple clang version 16.0.0 (…)".</summary>
    public static (int Major, bool Apple)? ParseVersion(string output) {
        var match = System.Text.RegularExpressions.Regex.Match(output, @"(Apple )?clang version (\d+)\.");
        return match.Success
            ? (int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), match.Groups[1].Success)
            : null;
    }

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
    ///     platform's linker can do it, and what the link strips — for the
    ///     machine building it, or the target the options name.
    /// </summary>
    /// <param name="runtimeCache">
    ///     Where the compiled runtime is kept between builds
    ///     (<see cref="RuntimeObject" />); the user's cache when not given.
    /// </param>
    public static NativeBuildResult Build(string llvmIr, string outputPath, NativeOptions options, string? runtimeCache = null) {
        var target = options.TargetOrHost;
        if (!target.HasRuntime) {
            return new(false, NoRuntime(target));
        }

        // The runtime is the same for every program built with these
        // options: compiled once and kept, it no longer costs most of a
        // small program's build.
        var lto = LtoFlags(options, target.Platform, LinksBitcodeFor(target));
        var (runtime, compiled) = RuntimeObject(options, lto, runtimeCache ?? DefaultRuntimeCache);
        if (!compiled.Succeeded) {
            return compiled;
        }

        var directory = Directory.CreateTempSubdirectory("sable-build-");
        try {
            var irPath = Path.Combine(directory.FullName, "program.ll");
            File.WriteAllText(irPath, ForTarget(llvmIr, target, lto.Count > 0));

            List<string> arguments = [..target.ClangFlags, ..SysrootFlags(options), ..CompileFlags(options), ..lto];

            // Apple's linker deletes the object link-time optimisation makes,
            // and dsymutil, which clang runs next, reads the debug information
            // from it: keep it until the build is done.
            if (options.DebugInfo && lto.Count > 0 && target.Platform == NativePlatform.MacOS) {
                var kept = Path.Combine(directory.FullName, "lto");
                if (options.Lto == LtoMode.Thin) {
                    Directory.CreateDirectory(kept);
                }

                arguments.Add("-Wl,-object_path_lto," + kept);
            }

            arguments.AddRange(["-Wno-override-module", irPath, runtime, "-o", outputPath, ..LinkFlags(options, target.Platform), ..CrossLinkFlags(target)]);

            var (exitCode, output) = Run(CompilerPath, arguments);
            if (exitCode == 0 && options.DebugInfo) {
                ShipLldbPlugin(outputPath);
            }

            return new(exitCode == 0, output);
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>Why an executable cannot be linked for <paramref name="target" />.</summary>
    public static string NoRuntime(NativeTarget target) =>
        $"the runtime has no platform layer for {target.Triple} yet: it is built for macOS and Linux only (docs/06); "
        + "the program can still be compiled to an object file (--emit obj) or to LLVM IR (--emit llvm-ir)";

    /// <summary>
    ///     The program's IR as clang is given it for <paramref name="target" />:
    ///     with the target's data layout and triple — the ones clang compiles
    ///     the runtime with, so that both agree on every size and offset — and
    ///     the attributes every function needs there.
    /// </summary>
    /// <param name="inlinesRuntime">
    ///     Link-time optimisation is on: LLVM inlines the runtime's small
    ///     functions into the program only into functions built for at least
    ///     the runtime's CPU features, so the program's get them too. (The
    ///     IR, written by hand, said nothing, so under LTO the runtime's
    ///     retain and release were never inlined into it.)
    /// </param>
    public static string ForTarget(string llvmIr, NativeTarget target, bool inlinesRuntime = false) {
        var attributes = new List<string>();
        if (inlinesRuntime && target.Describe()?.FunctionAttributes is { } features) {
            attributes.Add(features);
        }

        if (PlatformAttributesFor(target) is { } platform) {
            attributes.Add(platform);
        }

        var ir = attributes.Count > 0 ? WithHostAttributes(llvmIr, string.Join(' ', attributes)) : llvmIr;
        return WithTargetHeader(ir, target);
    }

    /// <summary>
    ///     The IR with <c>target datalayout</c> and <c>target triple</c> at its
    ///     top, as clang describes <paramref name="target" />; unchanged when
    ///     it has them already, or clang cannot say.
    /// </summary>
    public static string WithTargetHeader(string llvmIr, NativeTarget target) =>
        llvmIr.StartsWith("target ", StringComparison.Ordinal) || llvmIr.Contains("\ntarget triple = ", StringComparison.Ordinal) || target.Describe() is not { } description
            ? llvmIr
            : description.Header + llvmIr;

    /// <summary>clang's <c>--sysroot</c>, when the options give one.</summary>
    public static IReadOnlyList<string> SysrootFlags(NativeOptions options) =>
        options.Sysroot is { } sysroot ? ["--sysroot=" + sysroot] : [];

    /// <summary>
    ///     What linking for another machine's Linux needs: lld, since the
    ///     system's own linker links only for the system (Apple's takes no
    ///     ELF, GNU ld one architecture).
    /// </summary>
    public static IReadOnlyList<string> CrossLinkFlags(NativeTarget target) =>
        !target.IsHost && target.Platform == NativePlatform.Linux ? ["-fuse-ld=lld"] : [];

    /// <summary>Whether the link for <paramref name="target" /> takes LLVM bitcode: this machine's answer, or lld's for another's Linux.</summary>
    public static bool LinksBitcodeFor(NativeTarget target) =>
        target.IsHost ? LinksBitcode : target.Platform is NativePlatform.MacOS or NativePlatform.Linux;

    // =======================================================================
    // The runtime, compiled once
    // =======================================================================

    /// <summary>
    ///     Where a compiled runtime is kept when the build names no cache:
    ///     <c>$SABLE_HOME/cache/runtime</c>, by default under <c>~/.sable</c>.
    ///     Where it is kept never changes what is built: the object is keyed
    ///     by everything that goes into it.
    /// </summary>
    public static string DefaultRuntimeCache =>
        Path.Combine(
            Environment.GetEnvironmentVariable("SABLE_HOME") is { Length: > 0 } home ? home : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sable"),
            "cache",
            "runtime");

    /// <summary>
    ///     The runtime compiled as <paramref name="options" /> ask, from
    ///     <paramref name="cacheDirectory" /> when it was compiled before. The
    ///     object's name is a hash of all that makes it — the compiler, the
    ///     runtime's source, the flags and the target — so a cached one is
    ///     only ever the one this build would make. Written whole or not at all.
    /// </summary>
    /// <param name="lto">
    ///     Link-time optimisation's flags: with them the runtime is LLVM
    ///     bitcode, which the link optimises with the program, inlining the
    ///     runtime's small functions into it.
    /// </param>
    public static (string Path, NativeBuildResult Result) RuntimeObject(NativeOptions options, IReadOnlyList<string> lto, string cacheDirectory) {
        var target = options.TargetOrHost;
        var bitcode = lto.Where(static f => f.StartsWith("-flto", StringComparison.Ordinal));

        // Without debug information the runtime is compiled as the program
        // is; with it, optimised and without debug information — it is not
        // the program's code, and stepping goes over it as over a system
        // library.
        var level = options.DebugInfo && !options.Optimise ? "-O2" : $"-O{options.OptLevel}";
        List<string> flags = [..target.ClangFlags, ..SysrootFlags(options), "-c", level, ..bitcode];

        var key = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"sable-runtime-1\n{NativeObjects.CompilerIdentity}\n{string.Join(' ', flags)}\n{target.Triple}\n{RuntimeSource}")));
        var path = Path.Combine(cacheDirectory, $"runtime-{key[..32]}.o");
        if (File.Exists(path)) {
            return (path, new(true, ""));
        }

        Directory.CreateDirectory(cacheDirectory);
        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        var source = temporary + ".c";
        try {
            File.WriteAllText(source, RuntimeSource);
            var (exitCode, output) = Run(CompilerPath, [..flags, source, "-o", temporary]);
            if (exitCode != 0) {
                return (path, new(false, output));
            }

            File.Move(temporary, path, overwrite: true);
            return (path, new(true, output));
        } finally {
            File.Delete(source);
            File.Delete(temporary);
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

        flags.AddRange(options.LinkArguments);
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
    ///     What every function needs on the target's platform, whatever the
    ///     build. macOS's backtrace(), which a trap's backtrace and the
    ///     system's crash reports use, walks the frame-pointer chain: keep it
    ///     in every function that calls, as clang does for C there. (Without
    ///     it an unoptimised build's trap showed the innermost frame only.)
    /// </summary>
    public static string? PlatformAttributesFor(NativeTarget target) =>
        target.Platform == NativePlatform.MacOS ? "\"frame-pointer\"=\"non-leaf\"" : null;

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

    /// <summary>
    ///     Where each command run to build is written before it runs, for
    ///     <c>sable -v</c>; null to write nothing.
    /// </summary>
    public static TextWriter? CommandLog { get; set; }

    /// <summary>Writes a command to <see cref="CommandLog" />, quoted as a shell would need it.</summary>
    public static void LogCommand(string program, IReadOnlyList<string> arguments) {
        if (CommandLog is { } log) {
            static string Quote(string text) =>
                text.Length > 0 && text.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or '=' or ',' or ':' or '+')
                    ? text
                    : "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
            log.WriteLine($"     Running `{string.Join(' ', new[] { program }.Concat(arguments).Select(Quote))}`");
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

        LogCommand(program, [..start.ArgumentList]);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {program}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr);
    }
}
