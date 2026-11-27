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

/// <summary>
///     What a library is built as (docs/05 §5.2): an archive of objects that
///     another program's link takes in whole, or a shared library it loads.
/// </summary>
public enum LibraryKind {
    Static,
    Dynamic,
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

    /// <summary>The local symbols (#300): every one of the program's own, which nothing looks up by name; the debug information stays, and names its functions to a backtrace and the debugger.</summary>
    Local,
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
    public string Key => $"{OptLevel}|{DebugInfo}|{Lto}|{Strip}|{Rpath}|{Target?.Triple}|{NativeBuild.SysrootFor(this)}|{string.Join(' ', LinkArguments)}|{LinkInputs}|{PositionIndependent}";

    /// <summary>
    ///     Compile the program and the runtime as position-independent code,
    ///     which a shared library — or a static one linked into one — must be.
    ///     Only ELF needs asking: Mach-O and COFF code is so already.
    /// </summary>
    public bool PositionIndependent { get; init; }

    /// <summary>What the program is built for: null for the machine building it.</summary>
    public NativeTarget? Target { get; init; }

    /// <summary>
    ///     Where the target's C library and headers are, for a target that is
    ///     not this machine: clang's <c>--sysroot</c>. Null to look for one
    ///     (<see cref="NativeTarget.FindSysroot" />), or else leave it to clang.
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
/// <param name="Setup">
///     The build failed for what this machine lacks — the target's C library,
///     or the linker for it — not for anything the compiler did: the output
///     says what is missing and where it is looked for.
/// </param>
public sealed record NativeBuildResult(bool Succeeded, string Output, bool Setup = false) {
    /// <summary>The failure as the command line reports it.</summary>
    public string Report => Setup ? Output : $"internal error: the native build failed:\n{Output}";
}

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
    /// <summary>
    ///     The runtime's C source, carried inside this assembly: each file of
    ///     <c>runtime/</c> by its name. <c>sable_runtime.c</c> is the one
    ///     compiled; it includes the rest.
    /// </summary>
    public static IReadOnlyList<(string Name, string Text)> RuntimeFiles => RuntimeFileList.Value;

    static readonly Lazy<IReadOnlyList<(string Name, string Text)>> RuntimeFileList = new(static () => {
        const string prefix = "Sable.CodeGen.runtime/";
        var assembly = Assembly.GetExecutingAssembly();
        var files = assembly.GetManifestResourceNames()
            .Where(static name => name.StartsWith(prefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name => {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
                return (name[prefix.Length..], reader.ReadToEnd());
            })
            .ToList();
        return files.Any(static f => f.Item1 == RuntimeMain) ? files : throw new InvalidOperationException("the runtime source is missing from Sable.CodeGen");
    });

    /// <summary>The runtime's file that is compiled, which includes the others.</summary>
    public const string RuntimeMain = "sable_runtime.c";

    /// <summary>
    ///     The whole of the runtime's source, every file named and in order:
    ///     what a compiled runtime is keyed by.
    /// </summary>
    public static string RuntimeSource => string.Concat(RuntimeFiles.Select(static f => $"--- {f.Name}\n{f.Text}\n"));

    /// <summary>
    ///     Writes the runtime's files into <paramref name="directory" />, and
    ///     gives the path of the one to compile.
    /// </summary>
    public static string WriteRuntime(string directory) {
        Directory.CreateDirectory(directory);
        foreach (var (name, text) in RuntimeFiles) {
            File.WriteAllText(Path.Combine(directory, name), text);
        }

        return Path.Combine(directory, RuntimeMain);
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
    /// <param name="cache">
    ///     The build cache the compiled runtime is kept in between builds
    ///     (<see cref="Runtime" />); the user's when not given.
    /// </param>
    public static NativeBuildResult Build(string llvmIr, string outputPath, NativeOptions options, BuildCache? cache = null) =>
        Build(llvmIr, outputPath, options, cache, []);

    /// <param name="linkKind">What the link is asked to make other than an executable: <c>-shared</c>, with its name.</param>
    static NativeBuildResult Build(string llvmIr, string outputPath, NativeOptions options, BuildCache? cache, IReadOnlyList<string> linkKind) {
        var target = options.TargetOrHost;
        if (!target.HasRuntime) {
            return new(false, NoRuntime(target));
        }

        // The runtime is the same for every program built with these
        // options: compiled once and kept, it no longer costs most of a
        // small program's build.
        var lto = LtoFlags(options, target.Platform, LinksBitcodeFor(target));
        var runtime = Runtime(options, lto, cache ?? BuildCache.Default);
        if (!runtime.Result.Succeeded) {
            return Explained(runtime.Result, options);
        }

        var directory = Directory.CreateTempSubdirectory("sable-build-");
        try {
            var irPath = Path.Combine(directory.FullName, "program.ll");
            File.WriteAllText(irPath, ForTarget(llvmIr, target, lto.Count > 0, namesFrames: !options.Optimise));

            List<string> arguments = [..target.ClangFlags, ..SysrootFlags(options), ..CompileFlags(options), ..PositionIndependentFlags(options), ..lto, ..linkKind];

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

            arguments.AddRange([..target.IrFlags, irPath, runtime.Archive, "-o", outputPath, ..LinkFlags(options, target.Platform), ..CrossLinkFlags(target)]);

            var (exitCode, output) = Run(CompilerPath, arguments);
            if (exitCode == 0 && options.DebugInfo) {
                ShipLldbPlugin(outputPath);
            }

            return Explained(new(exitCode == 0, output), options);
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>
    ///     The file a library called <paramref name="name" /> is, as the
    ///     target's linkers look for it: <c>libname.a</c> and <c>libname.so</c>
    ///     (<c>.dylib</c> on macOS) for <c>-lname</c>; <c>name.lib</c> and
    ///     <c>name.dll</c> on Windows.
    /// </summary>
    public static string LibraryFileName(string name, LibraryKind kind, NativeTarget target) => (kind, target.Platform) switch {
        (LibraryKind.Static, NativePlatform.Windows) => name + ".lib",
        (LibraryKind.Static, _) => $"lib{name}.a",
        (LibraryKind.Dynamic, NativePlatform.Windows) => name + ".dll",
        (LibraryKind.Dynamic, NativePlatform.MacOS) => $"lib{name}.dylib",
        _ => $"lib{name}.so",
    };

    /// <summary>
    ///     Builds a library: the program's IR — compiled with no entry point
    ///     (<c>Pipeline.Compile(library: true)</c>), its <c>@export</c>
    ///     functions what C calls — with the runtime, so that the library is
    ///     all a C program needs beside its own objects.
    /// </summary>
    /// <remarks>
    ///     A static library is an archive of two native objects, the
    ///     program's and the runtime's: not LLVM bitcode, which only a linker
    ///     doing link-time optimisation reads. A shared library is linked as
    ///     an executable is, with link-time optimisation where the profile
    ///     asks for it; on macOS it is found through the rpath, and on Linux
    ///     it is named by its file.
    /// </remarks>
    public static NativeBuildResult BuildLibrary(string llvmIr, string outputPath, LibraryKind kind, NativeOptions options, BuildCache? cache = null) {
        var target = options.TargetOrHost;
        if (!target.HasRuntime) {
            return new(false, NoRuntime(target));
        }

        options = options with { PositionIndependent = true };
        if (kind == LibraryKind.Dynamic) {
            return Build(llvmIr, outputPath, options, cache, SharedFlags(Path.GetFileName(outputPath), target));
        }

        options = options with { Lto = LtoMode.Off };
        var runtime = Runtime(options, [], cache ?? BuildCache.Default);
        if (!runtime.Result.Succeeded) {
            return Explained(runtime.Result, options);
        }

        var directory = Directory.CreateTempSubdirectory("sable-library-");
        try {
            var program = Path.Combine(directory.FullName, "program" + (target.Platform == NativePlatform.Windows ? ".obj" : ".o"));
            var built = NativeObjects.CompileIr(llvmIr, program, options);
            if (!built.Succeeded) {
                return built;
            }

            // The runtime's object, not its archive: an archive in an
            // archive is no member a linker reads.
            return Archive([program, runtime.Object], outputPath, target);
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>What links a shared library called <paramref name="name" />: <c>-shared</c>, and the name it is found by.</summary>
    public static IReadOnlyList<string> SharedFlags(string name, NativeTarget target) => target.Platform switch {
        NativePlatform.MacOS => ["-shared", "-Wl,-install_name,@rpath/" + name],
        NativePlatform.Linux => ["-shared", "-Wl,-soname," + name],
        _ => ["-shared"],
    };

    /// <summary>Archives <paramref name="objects" /> as the static library <paramref name="outputPath" />, written whole or not at all.</summary>
    public static NativeBuildResult Archive(IReadOnlyList<string> objects, string outputPath, NativeTarget target) {
        // The archive is made beside the library and moved over it: an
        // archiver adds to an archive that is there.
        var directory = Directory.CreateTempSubdirectory("sable-archive-");
        try {
            var temporary = Path.Combine(directory.FullName, Path.GetFileName(outputPath));
            var (archiver, arguments) = Archiver(target, temporary, objects);
            var (exitCode, output) = Run(archiver, arguments);
            if (exitCode != 0) {
                return new(false, output);
            }

            File.Move(temporary, outputPath, overwrite: true);
            return new(true, output);
        } catch (System.ComponentModel.Win32Exception failure) {
            return new(false, $"could not run the archiver: {failure.Message}; set AR to one that can write archives for {target.Triple}");
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>
    ///     Compiles the program's IR as an optimised build does, to an object
    ///     that is thrown away, for what LLVM says about it: the output of
    ///     clang run with <paramref name="flags" />, such as the
    ///     <c>-Rpass</c> remarks <see cref="VectorizationReport" /> reads.
    /// </summary>
    public static NativeBuildResult Remarks(string llvmIr, NativeOptions options, IReadOnlyList<string> flags) {
        var target = options.TargetOrHost;
        var directory = Directory.CreateTempSubdirectory("sable-remarks-");
        try {
            var irPath = Path.Combine(directory.FullName, "program.ll");
            File.WriteAllText(irPath, ForTarget(llvmIr, target));
            var (exitCode, output) = Run(
                CompilerPath,
                [..target.ClangFlags, ..SysrootFlags(options), $"-O{options.OptLevel}", "-fno-color-diagnostics", ..flags, ..target.IrFlags, "-c", irPath, "-o", Path.Combine(directory.FullName, "program.o")]
            );
            return new(exitCode == 0, output);
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>The program's IR as assembly, optimised as the options say; in what is given back where it succeeded.</summary>
    public static NativeBuildResult CompileToAssembly(string llvmIr, NativeOptions options) {
        var target = options.TargetOrHost;
        var directory = Directory.CreateTempSubdirectory("sable-assembly-");
        try {
            var irPath = Path.Combine(directory.FullName, "program.ll");
            var output = Path.Combine(directory.FullName, "program.s");
            File.WriteAllText(irPath, ForTarget(llvmIr, target));
            var (exitCode, said) = Run(CompilerPath, [..target.ClangFlags, ..SysrootFlags(options), $"-O{options.OptLevel}", ..target.IrFlags, "-S", irPath, "-o", output]);
            return exitCode == 0 ? new(true, File.ReadAllText(output)) : new(false, said);
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>The functions of an assembly file, as clang writes one: each symbol, without the platform's underscore and read back as its SIL name (SymbolNames), and its lines.</summary>
    public static IEnumerable<(string Symbol, string Text)> FunctionsOf(string assembly) {
        string? symbol = null;
        var text = new System.Text.StringBuilder();
        foreach (var line in assembly.Split('\n')) {
            // A label in the first column, quoted where its name needs it, starts one.
            if (symbol is null && line.Length > 0 && line[0] is not (' ' or '\t' or '.' or '#' or ';' or '/') && line.LastIndexOf(':') is var colon and > 0) {
                var name = line[..colon].Trim('"');
                symbol = name.StartsWith("_sable.", StringComparison.Ordinal) ? name[1..] : name;
                symbol = symbol.StartsWith(LlvmEmitter.SymbolPrefix, StringComparison.Ordinal) ? LlvmEmitter.SymbolPrefix + SymbolNames.Demangle(symbol[LlvmEmitter.SymbolPrefix.Length..]) : symbol;
            }

            if (symbol is null) {
                continue;
            }

            text.Append(line).Append('\n');
            if (line.Contains(".cfi_endproc", StringComparison.Ordinal) || line.Contains(".seh_endproc", StringComparison.Ordinal) || line.Contains("-- End function", StringComparison.Ordinal)) {
                yield return (symbol, text.ToString());
                symbol = null;
                text.Clear();
            }
        }
    }

    /// <summary>
    ///     What makes a static library of <paramref name="objects" />: <c>$AR</c>
    ///     when it is set; for Windows, LLVM's <c>llvm-lib</c>, which writes the
    ///     archives Microsoft's linker reads; elsewhere the system's <c>ar</c>.
    /// </summary>
    /// <remarks>
    ///     On Windows the archive takes in compiler-rt's builtins too, as
    ///     <c>llvm-lib</c> merges a library it is given: the program and the
    ///     runtime call them (F16, 128-bit division), and neither Microsoft's
    ///     C runtime nor its linker has them, where on macOS and Linux every C
    ///     compiler links them in. So the library is still all a C program
    ///     needs beside its own objects.
    /// </remarks>
    static (string Program, IReadOnlyList<string> Arguments) Archiver(NativeTarget target, string output, IReadOnlyList<string> objects) {
        if (Environment.GetEnvironmentVariable("AR") is { Length: > 0 } ar) {
            return (ar, ["rcs", output, ..objects]);
        }

        if (target.Platform != NativePlatform.Windows) {
            return ("ar", ["rcs", output, ..objects]);
        }

        var builtins = NativeTarget.Host.Platform == NativePlatform.Windows && CompilerRtBuiltins.Value is { } found ? [found] : Array.Empty<string>();
        return ("llvm-lib", ["/out:" + output, ..objects, ..builtins]);
    }

    /// <summary>Why an executable cannot be linked for <paramref name="target" />.</summary>
    public static string NoRuntime(NativeTarget target) =>
        $"the runtime has no platform layer for {target.Triple} yet: it is built for macOS, Linux, Windows and WASI only (docs/06); "
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
    /// <param name="namesFrames">
    ///     An unoptimised build, whose trap's backtrace names every frame.
    ///     A whole program's functions are internal, which the symbol table
    ///     a backtrace reads on Linux (the dynamic one) and Windows (a PDB's
    ///     public symbols) leaves out: there they are kept external. Nothing
    ///     else links with the program, so no name can clash.
    /// </param>
    /// <param name="extraAttributes">More attributes for every function.</param>
    public static string ForTarget(string llvmIr, NativeTarget target, bool inlinesRuntime = false, bool namesFrames = false, string? extraAttributes = null) {
        if (namesFrames && target.Platform is NativePlatform.Linux or NativePlatform.Windows) {
            llvmIr = llvmIr.Replace("\ndefine internal ", "\ndefine ", StringComparison.Ordinal);
        }

        var attributes = new List<string>();
        if (inlinesRuntime && target.Describe()?.FunctionAttributes is { } features) {
            attributes.Add(features);
        }

        if (PlatformAttributesFor(target) is { } platform) {
            attributes.Add(platform);
        }

        if (extraAttributes is not null) {
            attributes.Add(extraAttributes);
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

    /// <summary>The sysroot a build with these options uses: theirs, or one found for the target; null for clang's own.</summary>
    public static string? SysrootFor(NativeOptions options) => options.Sysroot ?? options.TargetOrHost.FindSysroot();

    /// <summary>clang's <c>--sysroot</c>, when the options give one or one is found (<see cref="SysrootFor" />).</summary>
    public static IReadOnlyList<string> SysrootFlags(NativeOptions options) =>
        SysrootFor(options) is { } sysroot ? ["--sysroot=" + sysroot] : [];

    /// <summary><c>$SABLE_HOME</c>, by default <c>~/.sable</c>: where the compiled runtime and the targets' sysroots are kept.</summary>
    public static string SableHome =>
        Environment.GetEnvironmentVariable("SABLE_HOME") is { Length: > 0 } home ? home : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sable");

    // What clang says when the target's C library is not where it looks —
    // a header, a library or the start-up object — and when it has no
    // linker for the target.
    static readonly System.Text.RegularExpressions.Regex MissingLibrary = new(
        @"fatal error: '[^']+' file not found|unable to find library -l|cannot find -l|library not found for -l|cannot (open|find) [^\s:]*crt[^\s:]*\.o|could not open '[^']*crt");

    static readonly System.Text.RegularExpressions.Regex MissingLinker = new(
        @"invalid linker name in argument|Executable ""[^""]*(lld|wasm-ld|lld-link)[^""]*"" doesn't exist|unable to execute command: [^\n]*(lld|wasm-ld)");

    /// <summary>
    ///     A failed build for another machine, or with a sysroot given, said
    ///     as what this machine lacks when clang's output shows that is why:
    ///     the target's C library (<see cref="NoSysroot" />), or its linker.
    ///     Any other failure is left as it is.
    /// </summary>
    public static NativeBuildResult Explained(NativeBuildResult result, NativeOptions options) {
        var target = options.TargetOrHost;
        if (result.Succeeded || result.Setup || (target.IsHost && options.Sysroot is null)) {
            return result;
        }

        var why = MissingLinker.IsMatch(result.Output) ? NoLinker(target)
            : MissingLibrary.IsMatch(result.Output) ? NoSysroot(target, SysrootFor(options))
            : null;
        return why is null ? result : new(false, $"{why}\n\nclang said:\n{result.Output}", Setup: true);
    }

    /// <summary>Why a build for <paramref name="target" /> found no C library, and where one is looked for.</summary>
    public static string NoSysroot(NativeTarget target, string? sysroot) {
        var where = sysroot is null ? "on this machine" : $"in the sysroot {sysroot}";
        var kind = target.Platform switch {
            NativePlatform.Wasi => " For WASI it is wasi-libc's sysroot, such as wasi-sdk's share/wasi-sysroot, which is also found through WASI_SYSROOT or WASI_SDK_PATH.",
            NativePlatform.Linux => " For Linux it is a root of the target system with its /usr/include and /usr/lib, such as one debootstrap makes, or a cross toolchain's.",
            NativePlatform.MacOS => " For macOS it is an SDK, such as Xcode's MacOSX.sdk.",
            NativePlatform.Windows => " For Windows, clang finds the Windows SDK and Microsoft's C runtime by itself only on Windows.",
            _ => "",
        };
        return $"the C library for {target.Triple} is not {where}: the runtime is compiled with its headers, and the program linked with it. "
            + $"Give its root with --sysroot <dir>, or put it at {target.SysrootHome}, where it is found by itself.{kind}";
    }

    /// <summary>Why a build for <paramref name="target" /> could not be linked: clang has no linker for it.</summary>
    public static string NoLinker(NativeTarget target) {
        var linker = target.Platform switch {
            NativePlatform.Wasi => "wasm-ld",
            NativePlatform.Windows => "lld-link",
            NativePlatform.MacOS => "ld64.lld",
            _ => "ld.lld",
        };
        return $"linking for {target.Triple} needs LLVM's linker, {linker}, which clang cannot find: install lld beside it";
    }

    /// <summary>
    ///     What linking for another machine's Linux needs: lld, since the
    ///     system's own linker links only for the system (Apple's takes no
    ///     ELF, GNU ld one architecture).
    /// </summary>
    public static IReadOnlyList<string> CrossLinkFlags(NativeTarget target) =>
        !target.IsHost && target.Platform == NativePlatform.Linux ? ["-fuse-ld=lld"] : [];

    /// <summary>Whether the link for <paramref name="target" /> takes LLVM bitcode: this machine's answer, or lld's for another's Linux.</summary>
    public static bool LinksBitcodeFor(NativeTarget target) =>
        target.IsHost ? LinksBitcode : target.Platform is NativePlatform.MacOS or NativePlatform.Linux or NativePlatform.Wasi;

    // =======================================================================
    // The runtime, compiled once
    // =======================================================================

    /// <summary>
    ///     The runtime compiled as <paramref name="options" /> ask, from the
    ///     build cache when it was compiled before: its object, and the archive
    ///     of it programs link against (<c>libsable_runtime.a</c>,
    ///     <c>sable_runtime.lib</c> on Windows). The entry's key is all that
    ///     makes them — the compiler's identity, the runtime's source, the C
    ///     compiler, the flags and the target — so a cached one is only ever
    ///     the one this build would make.
    /// </summary>
    /// <param name="lto">
    ///     Link-time optimisation's flags: with them the runtime is LLVM
    ///     bitcode, which the link optimises with the program, inlining the
    ///     runtime's small functions into it.
    /// </param>
    /// <param name="profile">The profile the build is for, which the entry records.</param>
    public static RuntimeBuild Runtime(NativeOptions options, IReadOnlyList<string> lto, BuildCache cache, string profile = "") {
        var target = options.TargetOrHost;
        var flags = RuntimeFlags(options, lto);
        var key = cache.Key("runtime", "sable-runtime", CompilerIdentity.Version, CacheKey.Checksum(RuntimeSource), target.Triple, [
            "flags=" + string.Join(' ', flags),
            "cc=" + CacheKey.Checksum(NativeObjects.CompilerIdentity),
        ]);
        var objectName = "sable_runtime" + (target.Platform == NativePlatform.Windows ? ".obj" : ".o");
        var archiveName = LibraryFileName("sable_runtime", LibraryKind.Static, target);
        var (directory, built) = cache.GetOrCreate(key, profile, temporary => {
            var sources = Path.Combine(temporary, "src");
            var objectPath = Path.Combine(temporary, objectName);
            var (exitCode, output) = Run(CompilerPath, [..flags, WriteRuntime(sources), "-o", objectPath]);
            Directory.Delete(sources, true);
            if (exitCode != 0) {
                return new(false, output);
            }

            var archived = Archive([objectPath], Path.Combine(temporary, archiveName), target);
            return archived.Succeeded ? new(true, output) : archived;
        });
        return new(Path.Combine(directory, objectName), Path.Combine(directory, archiveName), built);
    }

    /// <summary>
    ///     What the runtime is compiled with for <paramref name="options" />:
    ///     without debug information as the program is; with it, optimised
    ///     and without debug information — it is not the program's code, and
    ///     stepping goes over it as over a system library. Its functions are
    ///     hidden, as the program's are (#300): only the program calls them,
    ///     and C reaches a library through the functions it exports.
    /// </summary>
    public static IReadOnlyList<string> RuntimeFlags(NativeOptions options, IReadOnlyList<string> lto) {
        var level = options.DebugInfo && !options.Optimise ? "-O2" : $"-O{options.OptLevel}";
        return [
            ..options.TargetOrHost.ClangFlags, ..SysrootFlags(options), "-c", level, "-fvisibility=hidden", ..SectionFlags(options.TargetOrHost), ..PositionIndependentFlags(options),
            ..lto.Where(static f => f.StartsWith("-flto", StringComparison.Ordinal)),
        ];
    }

    /// <summary>
    ///     <c>-fPIC</c>, for a library on a target whose code is not
    ///     position-independent unless asked: ELF's (<see cref="NativeOptions.PositionIndependent" />).
    /// </summary>
    public static IReadOnlyList<string> PositionIndependentFlags(NativeOptions options) =>
        options.PositionIndependent && options.TargetOrHost.Platform is NativePlatform.Linux ? ["-fPIC"] : [];

    /// <summary>
    ///     The IR marked position-independent in itself, as clang marks a C
    ///     file compiled <c>-fPIC</c>. <c>-fPIC</c> on IR reaches only the code
    ///     clang generates from it; IR compiled to bitcode for link-time
    ///     optimisation is generated as it links, and a relocatable link
    ///     (<c>-r</c>, a static library's) takes the model from the module's
    ///     'PIC Level' — without it, absolute addresses, which no PIE program
    ///     can link. (A second <c>!llvm.module.flags</c> adds to the first;
    ///     its node's number is past any the emitter gives.)
    /// </summary>
    public static string WithPicLevel(string llvmIr) =>
        llvmIr + "\n!llvm.module.flags = !{!4000000000}\n!4000000000 = !{i32 8, !\"PIC Level\", i32 2}\n";

    /// <summary>
    ///     The flags the program's IR is compiled with: its optimisation
    ///     level, <c>-g</c> for debug information, and a section per function
    ///     and datum where the target's linker needs that to leave any out
    ///     (<see cref="SectionFlags" />).
    /// </summary>
    public static IReadOnlyList<string> CompileFlags(NativeOptions options) =>
        [..options.DebugInfo ? ["-g", $"-O{options.OptLevel}"] : new[] { $"-O{options.OptLevel}" }, ..SectionFlags(options.TargetOrHost)];

    /// <summary>
    ///     A section per function and per datum, on ELF and COFF: their
    ///     linkers leave out only whole sections that nothing reaches
    ///     (<c>--gc-sections</c>, <c>/OPT:REF</c>: <see cref="PlatformLinkFlags" />),
    ///     so an object whose code is all one <c>.text</c> — the standard
    ///     library's, which holds every generic function of it compiled once
    ///     (#297 stage 7) — is otherwise taken whole. Apple's linker strips
    ///     by symbol already, and LLVM gives WebAssembly a section per
    ///     function and datum always. (Link-time optimisation's code lld
    ///     generates, a section each as well.)
    /// </summary>
    public static IReadOnlyList<string> SectionFlags(NativeTarget target) =>
        target.Platform is NativePlatform.Linux or NativePlatform.Windows ? ["-ffunction-sections", "-fdata-sections"] : [];

    /// <summary>
    ///     Link-time optimisation's flags, which let LLVM inline the runtime's
    ///     small functions into the program: in an optimised build that asks
    ///     for it, where the linker takes LLVM bitcode — Apple's does; on Linux
    ///     and Windows it is lld, as the GNU linkers need a plugin few
    ///     installations have, and Microsoft's takes none.
    /// </summary>
    public static IReadOnlyList<string> LtoFlags(NativeOptions options, NativePlatform platform, bool linksBitcode) =>
        !options.Optimise || options.Lto == LtoMode.Off || !linksBitcode ? []
        : [options.Lto == LtoMode.Thin ? "-flto=thin" : "-flto", ..platform is NativePlatform.Linux or NativePlatform.Windows ? new[] { "-fuse-ld=lld" } : []];

    /// <summary>
    ///     What the link takes after the objects: what the platform needs, then
    ///     what the profile strips, and where it looks for shared libraries.
    /// </summary>
    public static IReadOnlyList<string> LinkFlags(NativeOptions options, NativePlatform platform) {
        List<string> flags = [..PlatformLinkFlags(platform)];

        // GNU ld leaves the debug information of a function it drops, its
        // addresses from 0: lldb takes whatever of the program lies there,
        // from just past 0, for that function. A debug build keeps them all.
        if (options.DebugInfo && platform == NativePlatform.Linux) {
            flags.Add("-Wl,--no-gc-sections");
        }

        // GNU ld's and lld's -S and -s; Apple's linker has -S for the debug
        // map, and -x for local symbols — a program's exported ones it keeps.
        flags.AddRange((options.Strip, platform) switch {
            (StripMode.DebugInfo, NativePlatform.Linux) => ["-Wl,--strip-debug"],
            (StripMode.Local, NativePlatform.Linux) => ["-Wl,--discard-all"],
            (StripMode.Symbols, NativePlatform.Linux) => ["-Wl,--strip-all"],
            (StripMode.DebugInfo, NativePlatform.MacOS) => ["-Wl,-S"],
            (StripMode.Local, NativePlatform.MacOS) => ["-Wl,-x"],
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
    ///     Whether this machine's link takes LLVM bitcode: always on macOS, and
    ///     on Windows, where the link is always lld's; on Linux when clang can
    ///     link through lld, which is asked once per process.
    /// </summary>
    public static bool LinksBitcode => Host switch {
        NativePlatform.MacOS or NativePlatform.Windows => true,
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
    public static IReadOnlyList<string> PlatformLinkFlags(NativePlatform platform) => platform switch {
        // The sections nothing reaches left out: the standard library's
        // archive is one object, which holds every generic function of the
        // library compiled once (#297 stage 7), each in a section of its
        // own (SectionFlags). What a constructor reaches stays (.init_array
        // is a root): the witness tables, describers and error erasers a
        // unit registers. So does what llvm.used marks, SHF_GNU_RETAIN: the
        // debugger's table of classes. Not -rdynamic, whose every exported
        // symbol would be a root too: a trap's backtrace names the
        // program's frames from its .symtab where dladdr cannot. And 'frem'
        // becomes a call to fmod, which glibc keeps in libm, not libc. The
        // executor's pool is threads: pthread, which older glibc keeps apart.
        NativePlatform.Linux => ["-Wl,--gc-sections", "-lm", "-ldl", "-pthread"],

        // lld, which takes LLVM bitcode as Microsoft's linker does not; the
        // main thread's stack the size a POSIX one's is, 8 MiB, not 1; a
        // PDB, with the program's symbols at least, from which a trap's
        // backtrace names frames; and compiler-rt's builtins, which LLVM
        // calls for what the machine has no instruction for (F16, 128-bit
        // division) and which Microsoft's C runtime does not have. And the
        // sections nothing reaches left out, as on Linux, which /DEBUG
        // keeps unless asked (/OPT:REF): a constructor's .CRT$XCU entry is
        // in no COMDAT, so it and what it registers stay. Not /OPT:ICF,
        // which would fold identical read-only data — two witness tables, or
        // two types' metadata, whose address is the type's identity — and
        // identical functions, which a backtrace would name as one.
        NativePlatform.Windows => ["-fuse-ld=lld", "-Wl,/STACK:8388608", "-Wl,/DEBUG", "-Wl,/OPT:REF", ..CompilerRtBuiltins.Value is { } builtins ? [builtins] : Array.Empty<string>()],

        // wasm-ld gives the stack 64 KiB unless asked: 8 MiB, as POSIX's
        // main thread has. And the stack first in linear memory, below the
        // data, so that running off its end is an access below address 0,
        // which the engine traps, not a write over the program's globals.
        // (What nothing reaches wasm-ld leaves out unless asked not to.)
        NativePlatform.Wasi => ["-Wl,-z,stack-size=8388608", "-Wl,--stack-first"],

        // Apple's linker takes an archive's object whole: the standard
        // library's is one object, which holds every generic function of the
        // library compiled once (#297 stage 7). What nothing reaches — a
        // constructor, an export or the entry point — it leaves out.
        NativePlatform.MacOS => ["-Wl,-dead_strip"],
        _ => [],
    };

    /// <summary>compiler-rt's builtins library, as clang names it; null when it has none.</summary>
    static readonly Lazy<string?> CompilerRtBuiltins = new(static () => {
        try {
            var (exitCode, output) = Run(CompilerPath, ["--rtlib=compiler-rt", "-print-libgcc-file-name"]);
            return exitCode == 0 && output.Trim() is { Length: > 0 } path && File.Exists(path) ? path : null;
        } catch (System.ComponentModel.Win32Exception) {
            return null;
        }
    });

    /// <summary>A Linux executable's local symbols left out (<c>strip = "local"</c>); its DWARF stays.</summary>
    public static void DiscardLocalSymbols(string executable, NativeTarget target) {
        if (target.Platform != NativePlatform.Linux) {
            return;
        }

        // The program's symbols are hidden in its objects and made local by the
        // link (#300), which GNU ld's --discard-all leaves be — it discards the
        // symbols local in what it is given, and lld those it writes — so they
        // are discarded from the executable, by LLVM's objcopy or binutils'.
        foreach (var tool in (string[])[Path.Combine(Path.GetDirectoryName(CompilerPath) ?? "", "llvm-objcopy"), "llvm-objcopy", "objcopy"]) {
            try {
                if (Run(tool, ["--discard-all", executable]).ExitCode == 0) {
                    return;
                }
            } catch (System.ComponentModel.Win32Exception) {
                // Not here: the next.
            }
        }
    }

    /// <summary>
    ///     Gathers a macOS executable's debug information, which its link left
    ///     in the objects it was made from, into its <c>.dSYM</c>, as clang does
    ///     for a program it compiles and links in one step: for objects that
    ///     are about to be deleted. Nothing to do for any other target, whose
    ///     executable holds it.
    /// </summary>
    public static void GatherDebugInfo(string executable, NativeTarget target) {
        if (target.Platform != NativePlatform.MacOS) {
            return;
        }

        try {
            Run("dsymutil", [executable]);
        } catch (System.ComponentModel.Win32Exception) {
            // Built elsewhere, without the Xcode tools: no .dSYM, as clang would leave none.
            return;
        }

        ShipLldbPlugin(executable);
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
    ///     What every function needs on the target's platform, whatever the
    ///     build, for a trap's backtrace (and the system's crash reports) to
    ///     see past it. macOS's backtrace() walks the frame-pointer chain:
    ///     keep it in every function that calls, as clang does for C there.
    ///     glibc's, and Windows' CaptureStackBackTrace, walk the unwind
    ///     tables, which LLVM writes for a function that cannot throw — all
    ///     of Sable's — only when asked to, as clang asks for C there.
    ///     (Without them an unoptimised build's trap showed the innermost
    ///     frame only.)
    /// </summary>
    public static string? PlatformAttributesFor(NativeTarget target) => target.Platform switch {
        NativePlatform.MacOS => "\"frame-pointer\"=\"non-leaf\"",
        NativePlatform.Linux or NativePlatform.Windows => "uwtable",
        _ => null,
    };

    /// <summary>Every function the IR defines, given the host's attributes through one attribute group.</summary>
    public static string WithHostAttributes(string llvmIr, string attributes) {
        const string group = "#9000";
        const string wideGroup = "#9001";
        var wide = false;
        var lines = llvmIr.Split('\n');
        for (var i = 0; i < lines.Length; i++) {
            if (!lines[i].StartsWith("define ", StringComparison.Ordinal) || ParametersEnd(lines[i]) is not (> 0 and var end)) {
                continue;
            }

            // A function for wider vectors has features of its own, added to
            // the target's: one 'target-features' would be in place of the other.
            if (lines[i].Contains(LlvmEmitter.WideAttributes, StringComparison.Ordinal)) {
                lines[i] = lines[i][..(end + 1)] + " " + wideGroup + lines[i][(end + 1)..].Replace(LlvmEmitter.WideAttributes, "noinline", StringComparison.Ordinal);
                wide = true;
                continue;
            }

            lines[i] = lines[i][..(end + 1)] + " " + group + lines[i][(end + 1)..];
        }

        var text = string.Join('\n', lines) + $"\nattributes {group} = {{ {attributes} }}\n";
        const string features = "\"target-features\"=\"";
        return !wide ? text
            : text + $"attributes {wideGroup} = {{ " + (attributes.Contains(features, StringComparison.Ordinal)
                ? attributes.Replace(features, features + LlvmEmitter.WideFeatures + ",", StringComparison.Ordinal)
                : $"{attributes} {features}{LlvmEmitter.WideFeatures}\"") + " }\n";

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

    internal static (int ExitCode, string Output) Run(string program, IEnumerable<string> arguments) {
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
