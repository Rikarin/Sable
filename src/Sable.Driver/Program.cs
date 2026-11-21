using System.Text;
using Sable.CodeGen;
using Sable.Driver;
using Sable.Driver.Commands;

// The single toolchain binary of docs/05: `sable <command> …`, where each
// command is a class under Commands/.

// Text in and out is UTF-8, as source files are and as a Sable program
// writes. Windows would read and write a pipe in the console's code page
// instead, so an 'é' a program printed, or piped to 'sable fmt -', would
// come through as other characters; a console is set to UTF-8, as the
// runtime sets it for a program.
if (OperatingSystem.IsWindows()) {
    var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    if (Console.IsInputRedirected) {
        Console.SetIn(new StreamReader(Console.OpenStandardInput(), utf8));
    }

    if (!Console.IsOutputRedirected || !Console.IsErrorRedirected) {
        try {
            Console.OutputEncoding = utf8;
        } catch (IOException) {
            // No console to set: the redirected streams below are all there is.
        }
    }

    if (Console.IsOutputRedirected) {
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
    }

    if (Console.IsErrorRedirected) {
        Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
    }
}

if (GlobalOptions.Parse(args, out var badOption) is not { } parsed) {
    Console.Error.WriteLine($"sable: {badOption}");
    return 2;
}

var (options, rest) = parsed;

GlobalOptions.Current = options;

// '-generics=unspecialised|specialised' (#297), from anywhere before '--':
// how generic functions are compiled. It changes what a build produces, so
// it is not one of the global options; it is the process's, and what the
// process runs — a test harness's build among them — inherits it.
for (var i = 0; i < rest.Length && rest[i] != "--"; i++) {
    if (rest[i].StartsWith("-generics=", StringComparison.Ordinal) || rest[i].StartsWith("--generics=", StringComparison.Ordinal)) {
        var mode = rest[i][(rest[i].IndexOf('=') + 1)..];
        if (Sable.SIL.SilGen.ParseGenerics(mode) is null) {
            Console.Error.WriteLine($"sable: -generics takes unspecialised or specialised, not '{mode}'");
            return 2;
        }

        Environment.SetEnvironmentVariable("SABLE_GENERICS", mode);
        rest = [..rest[..i], ..rest[(i + 1)..]];
        i--;
    }
}

if (rest is ["-V" or "--version", ..]) {
    Console.WriteLine($"sable {Toolchains.RunningVersion} ({Toolchains.RunningBuild})");
    return 0;
}

if (rest.Length == 0 || rest[0] is "-h" or "--help" || rest is ["help"]) {
    var writer = rest.Length == 0 ? Console.Error : Console.Out;
    writer.WriteLine("usage: sable [options] <command> [arguments]");
    writer.WriteLine();
    foreach (var known in Command.All) {
        writer.WriteLine($"  {known.Name,-12} {known.Summary}");
    }

    writer.WriteLine();
    writer.WriteLine(GlobalOptions.Help);
    writer.WriteLine("'sable help <command>' or 'sable <command> --help' shows a command's arguments.");
    return rest.Length == 0 ? 2 : 0;
}

// 'sable help build' is 'sable build --help'.
if (rest is ["help", var topic]) {
    if (Command.Find(topic) is not { } helped) {
        Console.Error.WriteLine($"sable: no command '{topic}'; 'sable --help' lists them");
        return 2;
    }

    Console.WriteLine(helped.Usage);
    return 0;
}

if (Command.Find(rest[0]) is not { } command) {
    Console.Error.WriteLine($"sable: no command '{rest[0]}'; 'sable --help' lists them");
    return 2;
}

if (rest.Length > 1 && rest[1] is "-h" or "--help") {
    Console.WriteLine(command.Usage);
    return 0;
}

// A workspace that pins another toolchain is built by that one (docs/05 §5.1:
// the toolchain version is part of what determines the artifact).
if (command is not ToolchainCommand && Toolchains.Redirect(args) is { } exitCode) {
    return exitCode;
}

// What the options change about the output: where it goes and how it looks.
var standardError = Console.Error;
var standardOutput = Console.Out;
LineWriter? errors = null;
LineWriter? output = null;
if (options.MessageFormat == MessageFormat.Json) {
    errors = new JsonMessageWriter(standardOutput);
    output = new JsonMessageWriter(standardOutput, passThrough: true);
    Console.SetOut(TextWriter.Synchronized(output));
} else if (options.ColorsErrors) {
    errors = new ColorWriter(standardError);
}

if (errors is not null) {
    Console.SetError(TextWriter.Synchronized(errors));
}

if (options.Verbosity == Verbosity.Verbose) {
    NativeBuild.CommandLog = Console.Error;
}

try {
    return command.Run(rest[1..]);
} finally {
    output?.Flush();
    errors?.Flush();
    Console.SetOut(standardOutput);
    Console.SetError(standardError);
    standardOutput.Flush();
    standardError.Flush();
}
