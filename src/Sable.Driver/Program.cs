using Sable.Driver.Commands;

// The single toolchain binary of docs/05: `sable <command> …`, where each
// command is a class under Commands/.

if (args.Length == 0 || args[0] is "-h" or "--help" or "help") {
    var writer = args.Length == 0 ? Console.Error : Console.Out;
    writer.WriteLine("usage: sable <command> [arguments]");
    writer.WriteLine();
    foreach (var known in Command.All) {
        writer.WriteLine($"  {known.Name,-10} {known.Summary}");
    }

    writer.WriteLine();
    writer.WriteLine("'sable <command> --help' shows a command's arguments.");
    return args.Length == 0 ? 2 : 0;
}

if (Command.Find(args[0]) is not { } command) {
    Console.Error.WriteLine($"sable: no command '{args[0]}'; 'sable --help' lists them");
    return 2;
}

if (args.Length > 1 && args[1] is "-h" or "--help") {
    Console.WriteLine(command.Usage);
    return 0;
}

return command.Run(args[1..]);
