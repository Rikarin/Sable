using Sable.Driver.Packages;

namespace Sable.Driver.Commands;

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
