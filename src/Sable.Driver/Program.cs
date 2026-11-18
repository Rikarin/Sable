using Sable.CodeGen;
using Sable.Driver;
using Sable.Driver.Commands;

// The single toolchain binary of docs/05: `sable <command> …`, where each
// command is a class under Commands/.

if (GlobalOptions.Parse(args, out var badOption) is not { } parsed) {
    Console.Error.WriteLine($"sable: {badOption}");
    return 2;
}

var (options, rest) = parsed;

GlobalOptions.Current = options;

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
