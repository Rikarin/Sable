using Sable.Driver.Packages;

namespace Sable.Driver.Commands;

/// <summary><c>sable clean</c>: removes <c>target/</c>.</summary>
public sealed class CleanCommand : PackageCommand {
    public override string Name => "clean";
    public override string Summary => "remove the target directory";
    public override string Usage => "usage: sable clean";

    public override int Run(IReadOnlyList<string> arguments) {
        if (arguments.Count > 0) {
            return BadUsage();
        }

        return Guard(() => {
            PackageBuilder.Clean(LoadWorkspace(), Environment);
            return 0;
        });
    }
}
