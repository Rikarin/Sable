using Sable.Driver.Packages;

namespace Sable.Driver.Commands;

/// <summary><c>sable new &lt;name&gt; [--lib|--bin]</c>: a new package in a new directory.</summary>
public sealed class NewCommand : PackageCommand {
    public override string Name => "new";
    public override string Summary => "make a new package in a new directory";

    public override string Usage => """
        usage: sable new <name> [--lib|--bin]

          --bin    a program, src/main.sab (the default)
          --lib    a library, src/lib.sab
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        string? name = null;
        var library = false;
        foreach (var argument in arguments) {
            switch (argument) {
                case "--lib": library = true; break;
                case "--bin": library = false; break;
                default:
                    if (name is not null || argument.StartsWith('-')) {
                        return BadUsage();
                    }

                    name = argument;
                    break;
            }
        }

        if (name is null) {
            return BadUsage();
        }

        return Guard(() => {
            Scaffold.New(Directory.GetCurrentDirectory(), name, library, Environment);
            return 0;
        });
    }
}
