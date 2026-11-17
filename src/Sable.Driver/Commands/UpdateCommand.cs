using Sable.Driver.Packages;

namespace Sable.Driver.Commands;

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
