using Sable.Driver.Packages;

namespace Sable.Driver.Commands;

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
