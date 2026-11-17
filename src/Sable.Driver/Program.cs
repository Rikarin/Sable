using System.Diagnostics;
using Sable.CodeGen;
using Sable.Driver;
using Sable.SIL;
using Sable.Syntax;

// sable build <file.sab> [-o <output>] [--emit sil|llvm-ir] [-O0] [-g]
// sable run   <file.sab> [-O0] [-g]
//
// The single toolchain binary of docs/05, as far as Phase 2 takes it:
// compile one file to a native executable, or compile and run it.

if (args.Length == 0 || args[0] is "-h" or "--help") {
    return Usage(args.Length == 0 ? 2 : 0);
}

var command = args[0];
string? input = null;
string? output = null;
string? emit = null;
var optimise = true;
var debug = false;

for (var i = 1; i < args.Length; i++) {
    switch (args[i]) {
        case "-o" when i + 1 < args.Length: output = args[++i]; break;
        case "--emit" when i + 1 < args.Length: emit = args[++i]; break;
        case "-O0": optimise = false; break;
        case "-g": debug = true; break;
        default:
            if (input is not null || args[i].StartsWith('-')) {
                return Usage(2);
            }

            input = args[i];
            break;
    }
}

if (command is not ("build" or "run") || input is null || emit is not (null or "sil" or "llvm-ir")) {
    return Usage(2);
}

if (!File.Exists(input)) {
    Console.Error.WriteLine($"sable: no such file '{input}'");
    return 1;
}

var source = new SourceText(File.ReadAllText(input), Path.GetFileName(input));
var result = Pipeline.Compile(source, debug ? Path.GetDirectoryName(Path.GetFullPath(input)) : null);

// --emit shows a stage's output, even when a later stage could not run.
if (emit == "sil" && result.Sil is { } sil) {
    Console.Write(SilPrinter.Print(sil));
    Report(result.Errors);
    return result.Errors.Count == 0 ? 0 : 1;
}

if (!result.Succeeded) {
    Report(result.Errors);
    return 1;
}

if (emit == "llvm-ir") {
    Console.Write(result.LlvmIr);
    return 0;
}

if (!NativeBuild.IsAvailable()) {
    Console.Error.WriteLine($"sable: cannot run '{NativeBuild.CompilerPath}'; set CC to a clang that can link for this platform");
    return 1;
}

var executable = command == "run"
    ? Path.Combine(Path.GetTempPath(), $"sable-run-{Guid.NewGuid():N}")
    : output ?? Path.GetFileNameWithoutExtension(input);

var build = NativeBuild.Build(result.LlvmIr!, executable, optimise, debug);
if (!build.Succeeded) {
    Console.Error.WriteLine("internal error: the native build failed:");
    Console.Error.Write(build.Output);
    return 1;
}

if (command == "build") {
    return 0;
}

try {
    using var process = Process.Start(new ProcessStartInfo(executable)) ?? throw new InvalidOperationException();
    process.WaitForExit();
    return process.ExitCode;
} finally {
    File.Delete(executable);
}

static void Report(IReadOnlyList<string> errors) {
    foreach (var error in errors) {
        Console.Error.WriteLine(error);
        Console.Error.WriteLine();
    }
}

static int Usage(int code) {
    var writer = code == 0 ? Console.Out : Console.Error;
    writer.WriteLine("usage: sable build <file.sab> [-o <output>] [--emit sil|llvm-ir] [-O0] [-g]");
    writer.WriteLine("       sable run   <file.sab> [-O0] [-g]");
    writer.WriteLine();
    writer.WriteLine("  build              compile to a native executable (default name: the file's)");
    writer.WriteLine("  run                compile, run, and exit with the program's exit code");
    writer.WriteLine("  --emit sil         print the verified SIL instead of building");
    writer.WriteLine("  --emit llvm-ir     print the LLVM IR instead of building");
    writer.WriteLine("  -O0                build without LLVM's optimiser");
    writer.WriteLine("  -g                 build with debug information, for lldb");
    return code;
}
