using Sable.Driver.Packages;

namespace Sable.Driver.Commands;

/// <summary><c>sable publish [--dry-run]</c>.</summary>
public sealed class PublishCommand : PackageCommand {
    public override string Name => "publish";
    public override string Summary => "publish the package to the registry";

    public override string Usage => """
        usage: sable publish [--dry-run] [--allow-dirty] [--allow-untagged] [--no-verify] [-p <member>] [--registry <url or directory>]

          --dry-run          show exactly what would be uploaded, and upload nothing
          --allow-dirty      publish even with uncommitted changes
          --allow-untagged   publish a commit without a tag for the version (v1.2.0, 1.2.0, name-v1.2.0)
          --no-verify        publish without running the package's tests first

        The registry is --registry's, else [registry] path in the manifest, else $SABLE_REGISTRY,
        else ~/.sable/registry: a directory, or a network registry's URL ('sable login' first).
        The package is signed with ~/.sable/key, made on the first publish.
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        var rest = arguments.ToList();
        if (!TakeRegistry(rest, out var location)) {
            return BadUsage();
        }

        var dryRun = false;
        var allowDirty = false;
        var allowUntagged = false;
        var verify = true;
        string? member = null;
        for (var i = 0; i < rest.Count; i++) {
            switch (rest[i]) {
                case "--dry-run": dryRun = true; break;
                case "--allow-dirty": allowDirty = true; break;
                case "--allow-untagged": allowUntagged = true; break;
                case "--no-verify": verify = false; break;
                case "-p" or "--package" when i + 1 < rest.Count: member = rest[++i]; break;
                default: return BadUsage();
            }
        }

        return Guard(() => {
            var workspace = LoadWorkspace();
            PackageOperations.Publish(workspace, Environment, Member(workspace, member), dryRun, allowDirty, verify, allowUntagged,
                location is null ? null : Environment.Open(location));
            return 0;
        });
    }
}
