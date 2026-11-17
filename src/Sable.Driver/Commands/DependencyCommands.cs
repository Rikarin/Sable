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

/// <summary><c>sable remove &lt;pkg&gt;</c>.</summary>
public sealed class RemoveCommand : PackageCommand {
    public override string Name => "remove";
    public override string Summary => "remove a dependency";
    public override string Usage => "usage: sable remove <package> [-p <member>]";

    public override int Run(IReadOnlyList<string> arguments) {
        string? name = null;
        string? member = null;
        for (var i = 0; i < arguments.Count; i++) {
            switch (arguments[i]) {
                case "-p" or "--package" when i + 1 < arguments.Count: member = arguments[++i]; break;
                default:
                    if (name is not null || arguments[i].StartsWith('-')) {
                        return BadUsage();
                    }

                    name = arguments[i];
                    break;
            }
        }

        if (name is null) {
            return BadUsage();
        }

        return Guard(() => {
            var workspace = LoadWorkspace();
            PackageOperations.Remove(Environment, Member(workspace, member), name);
            return 0;
        });
    }
}

/// <summary><c>sable update [pkg…]</c>.</summary>
public sealed class UpdateCommand : PackageCommand {
    public override string Name => "update";
    public override string Summary => "re-resolve dependencies within their bounds, updating the lock file";
    public override string Usage => "usage: sable update [<package>...]";

    public override int Run(IReadOnlyList<string> arguments) {
        if (arguments.Any(static a => a.StartsWith('-'))) {
            return BadUsage();
        }

        return Guard(() => {
            if (PackageOperations.Update(LoadWorkspace(), Environment, arguments).Count == 0) {
                Console.Error.WriteLine("    Nothing to update");
            }

            return 0;
        });
    }
}

/// <summary><c>sable tree</c>.</summary>
public sealed class TreeCommand : PackageCommand {
    public override string Name => "tree";
    public override string Summary => "print the resolved dependency graph";
    public override string Usage => "usage: sable tree [-p <member>]";

    public override int Run(IReadOnlyList<string> arguments) {
        string? member = null;
        switch (arguments) {
            case []: break;
            case ["-p" or "--package", var name]: member = name; break;
            default: return BadUsage();
        }

        return Guard(() => {
            Console.Out.Write(PackageOperations.Tree(LoadWorkspace(), Environment, member));
            return 0;
        });
    }
}
