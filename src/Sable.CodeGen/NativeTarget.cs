using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Sable.CodeGen;

/// <summary>
///     What a program is built for: an LLVM target triple, and what the
///     compiler needs to know about it — the architecture and operating
///     system <c>#if arch(...)</c> and <c>#if os(...)</c> test, the C calling
///     convention, how the platform links (docs/04 §2.3, docs/05 §5.1).
/// </summary>
/// <remarks>
///     <para>
///         The triple is the one clang is given with <c>--target=</c>; the
///         host's is the one clang builds for when it is given none. What
///         LLVM needs to lay data out for the target — its
///         <c>target datalayout</c>, and the CPU and features clang gives a
///         C function there — is asked of clang once per triple
///         (<see cref="Describe" />), so that the program's IR says exactly
///         what the runtime, which clang compiles, was compiled for.
///     </para>
///     <para>
///         Every target the triple parser knows can be compiled to an object
///         file. Linking an executable needs the runtime, whose platform
///         layer is POSIX for macOS and Linux and Win32 for Windows
///         (<see cref="HasRuntime" />).
///     </para>
/// </remarks>
public sealed record NativeTarget {
    NativeTarget(string triple, string arch, NativePlatform platform, bool isHost) {
        Triple = triple;
        Arch = arch;
        Platform = platform;
        IsHost = isHost;
    }

    /// <summary>The triple as given, or for the host the one clang reports.</summary>
    public string Triple { get; }

    /// <summary>The architecture, as <c>#if arch(...)</c> names it: <c>arm64</c>, <c>x86_64</c> or <c>wasm32</c>.</summary>
    public string Arch { get; }

    public NativePlatform Platform { get; }

    /// <summary>The machine running the compiler, for which clang needs no <c>--target</c>.</summary>
    public bool IsHost { get; }

    /// <summary>The operating system, as <c>#if os(...)</c> names it.</summary>
    public string Os => OsName(Platform);

    /// <summary>The bits in a pointer: what LLVM lays addresses out with.</summary>
    public int PointerBits => Arch == "wasm32" ? 32 : 64;

    /// <summary>
    ///     Whether the runtime can be built for it, and so an executable
    ///     linked: the runtime's platform layer (threads, a trap's backtrace,
    ///     files, processes) is POSIX on macOS and Linux and Win32 on
    ///     Windows. WebAssembly needs a layer of its own (docs/06), not
    ///     written yet.
    /// </summary>
    public bool HasRuntime => Platform is NativePlatform.MacOS or NativePlatform.Linux or NativePlatform.Windows;

    /// <summary>What an executable's file name ends with on the target.</summary>
    public string ExecutableSuffix => Platform switch {
        NativePlatform.Windows => ".exe",
        NativePlatform.Wasi => ".wasm",
        _ => "",
    };

    /// <summary>
    ///     What clang is told so that it builds for this target: its triple,
    ///     but for the host. On Windows, the C runtime is the DLL, as
    ///     Microsoft's compiler links it by default: the Universal C Runtime
    ///     is the system's, and a program carries none of it.
    /// </summary>
    public IReadOnlyList<string> ClangFlags => [
        ..IsHost ? [] : new[] { "--target=" + Triple },
        ..Platform == NativePlatform.Windows ? new[] { "-fms-runtime-lib=dll" } : [],
    ];

    public override string ToString() => Triple;

    /// <summary>The machine running the compiler.</summary>
    public static NativeTarget Host => HostTarget.Value;

    static readonly Lazy<NativeTarget> HostTarget = new(() => {
        var arch = RuntimeInformation.OSArchitecture switch {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x86_64",
            Architecture.Wasm => "wasm32",
            var other => other.ToString().ToLowerInvariant(),
        };
        var platform = OperatingSystem.IsMacOS() ? NativePlatform.MacOS
            : OperatingSystem.IsLinux() ? NativePlatform.Linux
            : OperatingSystem.IsWindows() ? NativePlatform.Windows
            : NativePlatform.Other;

        // The triple clang builds for by default, when it can be asked; a
        // plausible one otherwise, since without a compiler nothing is built.
        var triple = HostTriple() ?? (arch, platform) switch {
            (_, NativePlatform.MacOS) => $"{(arch == "arm64" ? "arm64" : arch)}-apple-macosx",
            (_, NativePlatform.Linux) => $"{(arch == "arm64" ? "aarch64" : arch)}-unknown-linux-gnu",
            (_, NativePlatform.Windows) => $"{(arch == "arm64" ? "aarch64" : arch)}-pc-windows-msvc",
            _ => $"{arch}-unknown-unknown",
        };
        return new(triple, arch, platform, isHost: true);
    });

    static string? HostTriple() {
        try {
            var (exitCode, output) = Run(NativeBuild.CompilerPath, ["-print-target-triple"]);
            return exitCode == 0 && output.Trim() is { Length: > 0 } triple && !triple.Contains(' ') ? triple : null;
        } catch (System.ComponentModel.Win32Exception) {
            return null;
        }
    }

    /// <summary>
    ///     The target a triple names: <c>&lt;arch&gt;-&lt;vendor&gt;-&lt;os&gt;[-&lt;env&gt;]</c>,
    ///     as clang takes it — <c>aarch64-unknown-linux-gnu</c>,
    ///     <c>x86_64-apple-darwin</c>, <c>x86_64-pc-windows-msvc</c>,
    ///     <c>wasm32-wasi</c>. Null, with why, for one the compiler does not
    ///     know how to build for.
    /// </summary>
    public static NativeTarget? Parse(string triple, out string? error) {
        error = null;
        var parts = triple.Trim().ToLowerInvariant().Split('-');
        if (parts.Length < 2 || parts.Any(static p => p.Length == 0)) {
            error = $"'{triple}' is not a target triple: it is <arch>-<vendor>-<os>[-<environment>], such as aarch64-unknown-linux-gnu";
            return null;
        }

        var arch = parts[0] switch {
            "aarch64" or "arm64" => "arm64",
            "x86_64" or "amd64" or "x86-64" => "x86_64",
            "wasm32" => "wasm32",
            _ => null,
        };
        if (arch is null) {
            error = $"the target '{triple}' has an architecture the compiler does not build for: it knows aarch64 (arm64), x86_64 and wasm32";
            return null;
        }

        var rest = parts.Skip(1).ToArray();
        NativePlatform? platform =
            rest.Any(static p => p.StartsWith("darwin", StringComparison.Ordinal) || p.StartsWith("macos", StringComparison.Ordinal)) ? NativePlatform.MacOS
            : rest.Contains("linux") ? NativePlatform.Linux
            : rest.Any(static p => p.StartsWith("windows", StringComparison.Ordinal) || p == "win32") ? NativePlatform.Windows
            : rest.Any(static p => p.StartsWith("wasi", StringComparison.Ordinal)) || arch == "wasm32" ? NativePlatform.Wasi
            : null;
        if (platform is null) {
            error = $"the target '{triple}' has an operating system the compiler does not build for: it knows darwin (macOS), linux, windows and wasi";
            return null;
        }

        if (arch == "wasm32" != (platform == NativePlatform.Wasi)) {
            error = $"the target '{triple}' pairs {arch} with {OsName(platform.Value)}: WebAssembly is built for WASI, and WASI only for WebAssembly";
            return null;
        }

        // The host's own triple is the host, built without '--target'.
        var host = Host;
        var isHost = string.Equals(triple.Trim(), host.Triple, StringComparison.OrdinalIgnoreCase);
        return new(triple.Trim(), arch, platform.Value, isHost);
    }

    /// <summary>The target a triple names, or the host for null.</summary>
    public static NativeTarget Of(string? triple) =>
        triple is null ? Host
        : Parse(triple, out var error) ?? throw new ArgumentException(error, nameof(triple));

    static string OsName(NativePlatform platform) => platform switch {
        NativePlatform.MacOS => "macos",
        NativePlatform.Linux => "linux",
        NativePlatform.Windows => "windows",
        NativePlatform.Wasi => "wasi",
        _ => "unknown",
    };

    // =======================================================================
    // What clang says about the target
    // =======================================================================

    /// <summary>What LLVM is told about a target in the program's IR, as clang tells it for C.</summary>
    /// <param name="Triple">The triple, as clang normalises it.</param>
    /// <param name="DataLayout">How the target lays data out: pointer and integer sizes and alignments.</param>
    /// <param name="FunctionAttributes">The <c>target-cpu</c> and <c>target-features</c> clang gives a C function there.</param>
    public sealed record Description(string Triple, string DataLayout, string? FunctionAttributes) {
        /// <summary>The module's header: its data layout and triple, which every unit of the program shares with the runtime.</summary>
        public string Header => $"target datalayout = \"{DataLayout}\"\ntarget triple = \"{Triple}\"\n";
    }

    /// <summary>
    ///     What clang says about this target, from the IR of a one-line C
    ///     function it compiles for it — needing no headers or libraries, so
    ///     it works for any target clang was built with. Null when clang
    ///     cannot be run or does not know the target. Asked once per process.
    /// </summary>
    public Description? Describe() => Descriptions.GetOrAdd(Triple, _ => new(() => Probe(this))).Value;

    static readonly ConcurrentDictionary<string, Lazy<Description?>> Descriptions = new(StringComparer.Ordinal);

    static Description? Probe(NativeTarget target) {
        DirectoryInfo? directory = null;
        try {
            directory = Directory.CreateTempSubdirectory("sable-probe-");
            var probe = Path.Combine(directory.FullName, "probe.c");
            var output = Path.Combine(directory.FullName, "probe.ll");
            File.WriteAllText(probe, "int sable_probe(int x) { return x + 1; }\n");
            if (Run(NativeBuild.CompilerPath, [..target.ClangFlags, "-O2", "-S", "-emit-llvm", probe, "-o", output]).ExitCode != 0) {
                return null;
            }

            var text = File.ReadAllText(output);
            var layout = Regex.Match(text, "^target datalayout = \"([^\"]*)\"", RegexOptions.Multiline);
            var triple = Regex.Match(text, "^target triple = \"([^\"]*)\"", RegexOptions.Multiline);
            if (!layout.Success || !triple.Success) {
                return null;
            }

            var cpu = Regex.Match(text, "\"target-cpu\"=\"[^\"]*\"");
            var features = Regex.Match(text, "\"target-features\"=\"[^\"]*\"");
            var attributes = cpu.Success ? cpu.Value + (features.Success ? " " + features.Value : "") : null;
            return new(triple.Groups[1].Value, layout.Groups[1].Value, attributes);
        } catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) {
            return null;
        } finally {
            directory?.Delete(true);
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
