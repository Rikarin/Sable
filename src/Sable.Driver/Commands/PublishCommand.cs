using Sable.Driver.Packages;

namespace Sable.Driver.Commands;

/// <summary><c>sable publish [--dry-run]</c>.</summary>
public sealed class PublishCommand : PackageCommand {
    public override string Name => "publish";
    public override string Summary => "publish the package to the registry";

    public override string Usage => """
        usage: sable publish [--dry-run] [--allow-dirty] [-p <member>]

          --dry-run       show exactly what would be uploaded, and upload nothing
          --allow-dirty   publish even with uncommitted changes

        The registry is $SABLE_REGISTRY, else ~/.sable/registry, else [registry] path in sable.toml.
        The package is signed with ~/.sable/key, made on the first publish.
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        var dryRun = false;
        var allowDirty = false;
        string? member = null;
        for (var i = 0; i < arguments.Count; i++) {
            switch (arguments[i]) {
                case "--dry-run": dryRun = true; break;
                case "--allow-dirty": allowDirty = true; break;
                case "-p" or "--package" when i + 1 < arguments.Count: member = arguments[++i]; break;
                default: return BadUsage();
            }
        }

        return Guard(() => {
            var workspace = LoadWorkspace();
            PackageOperations.Publish(workspace, Environment, Member(workspace, member), dryRun, allowDirty);
            return 0;
        });
    }
}
