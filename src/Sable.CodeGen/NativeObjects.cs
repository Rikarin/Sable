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

    /// <summary>
    ///     The flags a unit's IR, and the runtime beside it, are compiled with:
    ///     <c>-g</c> at <c>-O0</c> for an unoptimised debug build; otherwise
    ///     the profile's level and, where the link takes bitcode, link-time
    ///     optimisation's — so each unit is LLVM bitcode that the link
    ///     optimises across units (ThinLTO, or the whole program at once).
    /// </summary>
    public static IReadOnlyList<string> Flags(NativeOptions options) =>
        options.DebugInfo && !options.Optimise ? ["-g", "-O0"]
        : [..NativeBuild.CompileFlags(options), ..Bitcode(options)];

    /// <summary>Link-time optimisation's flags for the options' target, without the linker's: what makes an object bitcode.</summary>
    public static IReadOnlyList<string> Bitcode(NativeOptions options) => [
        ..NativeBuild.LtoFlags(options, options.TargetOrHost.Platform, NativeBuild.LinksBitcodeFor(options.TargetOrHost))
            .Where(static f => f.StartsWith("-flto", StringComparison.Ordinal)),
    ];

    /// <summary>One unit's IR to a native object file for this machine: <c>-O2</c>, or <c>-g -O0</c> for a debug build.</summary>
    public static NativeBuildResult CompileIr(string llvmIr, string objectPath, bool optimise, bool debug) =>
        CompileIr(llvmIr, objectPath, NativeOptions.Of(optimise, debug) with { Lto = LtoMode.Off });

    /// <summary>The runtime to a native object file for this machine, as <see cref="CompileIr(string, string, bool, bool)" /> compiles a unit.</summary>
    public static NativeBuildResult CompileRuntime(string objectPath, bool optimise, bool debug) =>
        CompileRuntime(objectPath, NativeOptions.Of(optimise, debug) with { Lto = LtoMode.Off });

    /// <summary>
    ///     Compiles one unit's IR to an object file for the options' target,
    ///     written whole or not at all.
    /// </summary>
    public static NativeBuildResult CompileIr(string llvmIr, string objectPath, NativeOptions options) {
        var target = options.TargetOrHost;
        var temporary = objectPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        var irPath = temporary + ".ll";
        try {
            File.WriteAllText(irPath, NativeBuild.ForTarget(llvmIr, target, Bitcode(options).Count > 0));
            var (exitCode, output) = Run(NativeBuild.CompilerPath, [..target.ClangFlags, ..Flags(options), "-Wno-override-module", "-c", "-x", "ir", irPath, "-o", temporary]);
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

    /// <summary>Compiles the runtime's C source to an object file for the options' target.</summary>
    public static NativeBuildResult CompileRuntime(string objectPath, NativeOptions options) {
        var target = options.TargetOrHost;
        if (!target.HasRuntime) {
            return new(false, NativeBuild.NoRuntime(target));
        }

        var temporary = objectPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        var source = temporary + ".c";
        try {
            File.WriteAllText(source, NativeBuild.RuntimeSource);
            var (exitCode, output) = Run(NativeBuild.CompilerPath, [..target.ClangFlags, ..NativeBuild.SysrootFlags(options), ..Flags(options), "-c", source, "-o", temporary]);
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

    /// <summary>
    ///     Links objects into an executable for the options' target, which
    ///     replaces the one there only once it is whole. Objects that are
    ///     bitcode (<see cref="Flags" />) are optimised together as they link.
    /// </summary>
    /// <param name="options">What the profile strips, and whether it looks beside the executable for shared libraries.</param>
    /// <param name="thinLtoCache">
    ///     Where ThinLTO keeps what it optimised for each unit, so that a link
    ///     after one unit changed optimises that unit again and not the rest.
    /// </param>
    public static NativeBuildResult Link(IReadOnlyList<string> objects, string outputPath, NativeOptions? options = null, string? thinLtoCache = null) {
        options ??= NativeOptions.Of(false, true);
        var target = options.TargetOrHost;
        var temporary = outputPath + $".{Environment.ProcessId}.tmp";
        try {
            var lto = NativeBuild.LtoFlags(options, target.Platform, NativeBuild.LinksBitcodeFor(target));
            List<string> cache = thinLtoCache is null || options.Lto != LtoMode.Thin || lto.Count == 0 ? []
                : target.Platform == NativePlatform.MacOS ? ["-Wl,-cache_path_lto," + thinLtoCache]
                : target.Platform == NativePlatform.Windows ? ["-Wl,/lldltocache:" + thinLtoCache]
                : ["-Wl,--thinlto-cache-dir=" + thinLtoCache];
            List<string> arguments = [
                ..target.ClangFlags, ..NativeBuild.SysrootFlags(options), ..lto.Count > 0 ? NativeBuild.CompileFlags(options) : [], ..lto, ..cache,
                ..LinkArguments(objects, temporary, target.Platform, options), ..NativeBuild.CrossLinkFlags(target),
            ];
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

    /// <summary>
    ///     The arguments that link <paramref name="objects" /> into
    ///     <paramref name="outputPath" /> for <paramref name="platform" />: the
    ///     objects, then what the platform needs, as the whole program's build
    ///     links them.
    /// </summary>
    public static IReadOnlyList<string> LinkArguments(IReadOnlyList<string> objects, string outputPath, NativePlatform platform, NativeOptions? options = null) =>
        [..objects, "-o", outputPath, ..NativeBuild.LinkFlags(options ?? NativeOptions.Of(false, true), platform)];

    static (int ExitCode, string Output) Run(string program, IEnumerable<string> arguments) {
        var start = new ProcessStartInfo(program) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        NativeBuild.LogCommand(program, [..start.ArgumentList]);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {program}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr);
    }
}
