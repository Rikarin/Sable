using Sable.Driver.Packages;

namespace Sable.Driver.Commands;

/// <summary><c>sable add &lt;pkg&gt;[@&lt;req&gt;] [--dev] [--path &lt;dir&gt;]</c>.</summary>
public sealed class AddCommand : PackageCommand {
    public override string Name => "add";
    public override string Summary => "add a dependency and update the lock file";

    public override string Usage => """
        usage: sable add <package>[@<requirement>] [--dev] [--path <dir>] [-p <member>]

          @<requirement>   e.g. @1.2, @^0.4.1, @=2.0.0 (default: the newest version)
          --dev            a dev-dependency
          --path <dir>     a package on disk rather than in the registry
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        string? specification = null;
        string? path = null;
        string? member = null;
        var dev = false;
        for (var i = 0; i < arguments.Count; i++) {
            switch (arguments[i]) {
                case "--dev": dev = true; break;
                case "--path" when i + 1 < arguments.Count: path = arguments[++i]; break;
                case "-p" or "--package" when i + 1 < arguments.Count: member = arguments[++i]; break;
                default:
                    if (specification is not null || arguments[i].StartsWith('-')) {
                        return BadUsage();
                    }

                    specification = arguments[i];
                    break;
            }
        }

        if (specification is null) {
            return BadUsage();
        }

        return Guard(() => {
            var workspace = LoadWorkspace();
            PackageOperations.Add(workspace, Environment, Member(workspace, member), specification, dev, path);
            return 0;
        });
    }
}
