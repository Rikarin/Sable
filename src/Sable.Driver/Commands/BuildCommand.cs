using System.Diagnostics;
using Sable.CodeGen;
using Sable.SIL;
using Sable.Syntax;

namespace Sable.Driver.Commands;

/// <summary>
///     <c>sable build &lt;file.sb&gt;</c>: one file to a native executable.
///     <see cref="RunCommand" /> shares it, and runs what it built.
/// </summary>
public class BuildCommand : Command {
    public override string Name => "build";
    public override string Summary => "compile to a native executable";

    public override string Usage => """
        usage: sable build [--release] [--features <a,b>] [-p <package>] [--bin <name>]
               sable build <file.sb> [-o <output>] [--emit sil|llvm-ir|obj|staticlib|dylib] [-O0|-O1|-O2|-O3|-Os|-Oz] [-g] [--exclusivity=checked|unchecked]
                                     [--target <triple>] [--sysroot <dir>] [--deny-reflection] [--deny-unsafe] [--vectorize-report]

        Without a file, builds the package in this directory or the nearest above it (sable.toml).

        """ + PackageCommand.BuildOptions + """

        With a file, builds just that file:

          -o <output>        the executable's name (default: the file's)
          --emit sil         print the verified SIL instead of building
          --emit llvm-ir     print the LLVM IR instead of building
          --emit obj         write the program's object file (<output>.o), without the runtime or a link
          --emit staticlib   build a static library C links (libname.a, name.lib on Windows): no entry
                             point; C calls the @export functions, and the globals are set up as it loads
          --emit dylib       the same as a shared library (libname.dylib, libname.so, name.dll)
          --vectorize-report say, for each loop by its place in the source, whether LLVM's loop
                             vectoriser vectorised it in an optimised build, and if not, why not
          -O0 ... -O3        the optimisation level, clang's and the SIL passes' (default -O2);
          -Os, -Oz           -Os and -Oz optimise for size
          -g                 build with debug information, for lldb: at -O0 unless a level is given,
                             so that every variable is where the debugger looks
          --exclusivity=...  keep the dynamic exclusivity checks, or drop them; by default
                             kept in a debug or -O0 build, dropped in an optimised one
          --target <triple>  build for another machine: aarch64-unknown-linux-gnu, x86_64-apple-darwin,
                             x86_64-pc-windows-msvc, wasm32-wasi, ... (default: this one)
          --sysroot <dir>    where the target's C library is, for clang's link
          --deny-reflection  carry no reflection: Mirror and TypeInfo are errors (S3450), and the
                             runtime's field descriptors have no names
          --deny-unsafe      fail on any 'unsafe' block or 'unsafe fn' in the file (S3472)
        """;

    /// <summary>What the command line asked for.</summary>
    protected sealed record Options(string Input, string? Output, string? Emit, string OptLevel, bool Debug, bool? Exclusivity, IReadOnlyList<string> ProgramArguments) {
        public bool Optimise => OptLevel != "0";

        /// <summary>What to build for: null for this machine.</summary>
        public NativeTarget? Target { get; init; }

        public string? Sysroot { get; init; }

        /// <summary><c>--deny-reflection</c> (docs/06 §6.1).</summary>
        public bool DenyReflection { get; init; }

        /// <summary><c>--deny-unsafe</c> (SYNTAX.md §23).</summary>
        public bool DenyUnsafe { get; init; }

        /// <summary>What clang is asked for.</summary>
        public NativeOptions Native => new() { OptLevel = OptLevel, DebugInfo = Debug, Target = Target, Sysroot = Sysroot };
    }

    protected virtual bool TakesProgramArguments => false;

    protected Options? Parse(IReadOnlyList<string> args) {
        string? input = null;
        string? output = null;
        string? emit = null;
        string? level = null;
        var debug = false;
        bool? exclusivity = null;
        string? triple = null;
        string? sysroot = null;
        var denyReflection = false;
        var denyUnsafe = false;
        var arguments = new List<string>();

        for (var i = 0; i < args.Count; i++) {
            switch (args[i]) {
                case "--" when TakesProgramArguments:
                    // Everything after '--' is the program's.
                    arguments.AddRange(args.Skip(i + 1));
                    i = args.Count;
                    break;
                case "-o" when i + 1 < args.Count && !TakesProgramArguments: output = args[++i]; break;
                case "--emit" when i + 1 < args.Count && !TakesProgramArguments: emit = args[++i]; break;
                case "--target" when i + 1 < args.Count: triple = args[++i]; break;
                case "--sysroot" when i + 1 < args.Count: sysroot = args[++i]; break;
                case "--deny-reflection": denyReflection = true; break;
                case "--deny-unsafe": denyUnsafe = true; break;
                case "-O0" or "-O1" or "-O2" or "-O3" or "-Os" or "-Oz": level = args[i][2..]; break;
                case "-g": debug = true; break;
                case "--vectorize-report" when !TakesProgramArguments: emit = "vectorize-report"; break;
                case "--exclusivity=checked": exclusivity = true; break;
                case "--exclusivity=unchecked": exclusivity = false; break;
                default:
                    if (input is not null || args[i].StartsWith('-')) {
                        return null;
                    }

                    input = args[i];
                    break;
            }
        }

        if (input is null || emit is not (null or "sil" or "llvm-ir" or "obj" or "vectorize-report" or "staticlib" or "dylib")) {
            return null;
        }

        NativeTarget? target = null;
        if (triple is not null && (target = NativeTarget.Parse(triple, out var error)) is null) {
            Console.Error.WriteLine($"sable: {error}");
            return null;
        }

        // A debug build is unoptimised unless it asks for a level: then the
        // passes run, and the debugger sees what they leave. A vectorisation
        // report is of the optimised build.
        level ??= debug && emit != "vectorize-report" ? "0" : "2";
        return new(input, output, emit, level, debug, exclusivity, arguments) { Target = target, Sysroot = sysroot, DenyReflection = denyReflection, DenyUnsafe = denyUnsafe };
    }

    public override int Run(IReadOnlyList<string> arguments) {
        if (PackageCommand.IsPackageMode(arguments)) {
            return PackageCommand.BuildPackage(arguments, Usage);
        }

        if (Parse(arguments) is not { } options) {
            return BadUsage();
        }

        var target = options.Target ?? NativeTarget.Host;
        var name = Path.GetFileNameWithoutExtension(options.Input);
        var output = options.Output ?? (LibraryKindOf(options.Emit) is { } kind
            ? NativeBuild.LibraryFileName(name, kind, target)
            : name + (options.Emit == "obj" ? ".o" : target.ExecutableSuffix));
        return Build(options, output) is { } failure ? Math.Max(failure, 0) : 0;
    }

    /// <summary>The library <c>--emit</c> asks for, if it asks for one.</summary>
    static LibraryKind? LibraryKindOf(string? emit) => emit switch {
        "staticlib" => LibraryKind.Static,
        "dylib" => LibraryKind.Dynamic,
        _ => null,
    };

    /// <summary>Builds <paramref name="executable" />; null when built, -1 when --emit printed a stage instead, else the exit code to fail with.</summary>
    protected static int? Build(Options options, string executable) {
        if (!File.Exists(options.Input)) {
            Console.Error.WriteLine($"sable: no such file '{options.Input}'");
            return 1;
        }

        var source = new SourceText(File.ReadAllText(options.Input), Path.GetFileName(options.Input));
        var target = options.Target ?? NativeTarget.Host;
        // Dynamic exclusivity checks: on in a debug build, off in a release one,
        // unless asked for (docs/02 §2.3).
        // A vectorisation report is of the optimised build, with the debug
        // information that gives LLVM's remarks their places in the source.
        var report = options.Emit == "vectorize-report";
        var tree = SyntaxTree.Parse(source, Pipeline.ParseOptionsFor(options.Optimise, target));
        if (options.DenyUnsafe && Pipeline.UnsafeDenied([tree]) is { Count: > 0 } denied) {
            Report(denied);
            return 1;
        }

        var result = Pipeline.Compile(
            [tree],
            options.Debug || report ? Path.GetDirectoryName(Path.GetFullPath(options.Input)) : null,
            options.Exclusivity ?? ((options.Debug && !report) || !options.Optimise),
            target: target,
            optimised: options.Optimise,
            optLevel: options.OptLevel,
            library: LibraryKindOf(options.Emit) is not null,
            denyReflection: options.DenyReflection
        );
        Report(result.Warnings);

        // --emit shows a stage's output, even when a later stage could not run.
        if (options.Emit == "sil" && result.Sil is { } sil) {
            Console.Write(SilPrinter.Print(sil));
            Report(result.Errors);
            return result.Errors.Count == 0 ? -1 : 1;
        }

        if (!result.Succeeded) {
            Report(result.Errors);
            return 1;
        }

        if (options.Emit == "llvm-ir") {
            // As clang is given it: with the target's data layout and triple.
            Console.Write(NativeBuild.WithTargetHeader(result.LlvmIr!, target));
            return -1;
        }

        if (!NativeBuild.IsAvailable()) {
            Console.Error.WriteLine($"sable: cannot run '{NativeBuild.CompilerPath}'; set CC to a clang that can link for this platform");
            return 1;
        }

        if (report) {
            var remarks = NativeBuild.Remarks(result.LlvmIr!, options.Native with { DebugInfo = false }, VectorizationReport.Flags);
            if (!remarks.Succeeded) {
                Console.Error.WriteLine($"internal error: compiling for {target.Triple} failed:");
                Console.Error.Write(remarks.Output);
                return 1;
            }

            Console.Write(VectorizationReport.Format(VectorizationReport.Parse(remarks.Output)));
            return -1;
        }

        // An object file needs no runtime: it is what another machine's link,
        // or a target the runtime is not built for yet, starts from.
        if (options.Emit == "obj") {
            var compiled = NativeObjects.CompileIr(result.LlvmIr!, Path.GetFullPath(executable), options.Native with { Lto = LtoMode.Off });
            if (!compiled.Succeeded) {
                Console.Error.WriteLine($"internal error: compiling for {target.Triple} failed:");
                Console.Error.Write(compiled.Output);
                return 1;
            }

            return null;
        }

        if (!target.HasRuntime) {
            Console.Error.WriteLine($"sable: {NativeBuild.NoRuntime(target)}");
            return 1;
        }

        var build = LibraryKindOf(options.Emit) is { } library
            ? NativeBuild.BuildLibrary(result.LlvmIr!, executable, library, options.Native)
            : NativeBuild.Build(result.LlvmIr!, executable, options.Native);
        if (!build.Succeeded) {
            Console.Error.WriteLine("internal error: the native build failed:");
            Console.Error.Write(build.Output);
            return 1;
        }

        return null;
    }

    protected static void Report(IReadOnlyList<string> diagnostics) {
        foreach (var diagnostic in diagnostics) {
            Console.Error.WriteLine(diagnostic);
            Console.Error.WriteLine();
        }

        DiagnosticDocs.WriteHint(Console.Error, diagnostics);
    }

    /// <summary>Runs <paramref name="executable" /> and returns its exit code.</summary>
    protected static int Execute(string executable, IReadOnlyList<string> arguments) {
        var start = new ProcessStartInfo(executable);
        foreach (var argument in arguments) {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        process.WaitForExit();
        return process.ExitCode;
    }
}

/// <summary><c>sable run &lt;file.sb&gt;</c>: build to a temporary executable, run it, and exit with its code.</summary>
public sealed class RunCommand : BuildCommand {
    public override string Name => "run";
    public override string Summary => "compile and run, exiting with the program's exit code";

    public override string Usage => """
        usage: sable run [--release] [--features <a,b>] [-p <package>] [--bin <name>] [-- <arguments>]
               sable run <file.sb> [-O0|-O1|-O2|-O3|-Os|-Oz] [-g] [--exclusivity=checked|unchecked] [-- <arguments>]

        Without a file, builds and runs the package's binary (see 'sable build --help').

          what follows '--' is passed to the program
        """;

    protected override bool TakesProgramArguments => true;

    public override int Run(IReadOnlyList<string> arguments) {
        if (PackageCommand.IsPackageMode(arguments)) {
            return PackageCommand.RunPackage(arguments, Usage);
        }

        if (Parse(arguments) is not { } options) {
            return BadUsage();
        }

        var executable = Path.Combine(Path.GetTempPath(), $"sable-run-{Guid.NewGuid():N}{NativeTarget.Host.ExecutableSuffix}");
        try {
            return Build(options, executable) is { } failure ? Math.Max(failure, 0) : Execute(executable, options.ProgramArguments);
        } finally {
            File.Delete(executable);
        }
    }
}
