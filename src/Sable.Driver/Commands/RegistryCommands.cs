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

/// <summary><c>sable yank &lt;pkg&gt;@&lt;version&gt; [--undo]</c>.</summary>
public sealed class YankCommand : PackageCommand {
    public override string Name => "yank";
    public override string Summary => "mark a published version unresolvable for new lock files";
    public override string Usage => "usage: sable yank <package>@<version> [--undo]";

    public override int Run(IReadOnlyList<string> arguments) {
        var undo = arguments.Contains("--undo");
        var rest = arguments.Where(static a => a != "--undo").ToList();
        if (rest is not [var specification] || specification.IndexOf('@', 1) is not (> 0 and var at)
            || !SemVersion.TryParse(specification[(at + 1)..], out var version, out _)) {
            return BadUsage();
        }

        return Guard(() => {
            var directory = Workspace.FindManifestDirectory(Directory.GetCurrentDirectory());
            PackageOperations.Yank(directory is null ? null : LoadWorkspace(), Environment, specification[..at], version, undo);
            return 0;
        });
    }
}

/// <summary><c>sable vendor</c>.</summary>
public sealed class VendorCommand : PackageCommand {
    public override string Name => "vendor";
    public override string Summary => "copy every registry dependency into vendor/, for offline builds";
    public override string Usage => "usage: sable vendor";

    public override int Run(IReadOnlyList<string> arguments) {
        if (arguments.Count > 0) {
            return BadUsage();
        }

        return Guard(() => {
            var vendored = PackageOperations.Vendor(LoadWorkspace(), Environment);
            Console.Error.WriteLine($"    Vendored {vendored.Count} package(s); builds now read them from vendor/");
            return 0;
        });
    }
}
