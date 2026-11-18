using Sable.Driver.Packages;

namespace Sable.Driver.Commands;

/// <summary><c>sable init [--lib|--bin] [--name &lt;name&gt;]</c>: a new package in the current directory.</summary>
public sealed class InitCommand : PackageCommand {
    public override string Name => "init";
    public override string Summary => "make a new package in the current directory";

    public override string Usage => """
        usage: sable init [--lib|--bin] [--name <name>]

          --bin           a program, src/main.sb (the default)
          --lib           a library, src/lib.sb
          --name <name>   the package's name (default: the directory's)
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        string? name = null;
        var library = false;
        for (var i = 0; i < arguments.Count; i++) {
            switch (arguments[i]) {
                case "--lib": library = true; break;
                case "--bin": library = false; break;
                case "--name" when i + 1 < arguments.Count: name = arguments[++i]; break;
                default: return BadUsage();
            }
        }

        return Guard(() => {
            Scaffold.Init(Directory.GetCurrentDirectory(), name, library, Environment);
            return 0;
        });
    }
}
