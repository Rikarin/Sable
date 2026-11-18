using Sable.Driver.Packages;

namespace Sable.Driver.Commands;

/// <summary><c>sable yank &lt;pkg&gt;@&lt;version&gt; [--undo]</c>.</summary>
public sealed class YankCommand : PackageCommand {
    public override string Name => "yank";
    public override string Summary => "mark a published version unresolvable for new lock files";
    public override string Usage => "usage: sable yank <package>@<version> [--undo] [--registry <url or directory>]";

    public override int Run(IReadOnlyList<string> arguments) {
        var rest = arguments.ToList();
        if (!TakeRegistry(rest, out var location)) {
            return BadUsage();
        }

        var undo = rest.Remove("--undo");
        if (rest is not [var specification] || specification.IndexOf('@', 1) is not (> 0 and var at)
            || !SemVersion.TryParse(specification[(at + 1)..], out var version, out _)) {
            return BadUsage();
        }

        return Guard(() => {
            var directory = Workspace.FindManifestDirectory(Directory.GetCurrentDirectory());
            PackageOperations.Yank(directory is null ? null : LoadWorkspace(), Environment, specification[..at], version, undo,
                location is null ? null : Environment.Open(location));
            return 0;
        });
    }
}
