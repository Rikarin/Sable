using Sable.Driver.Packages;

namespace Sable.Driver.Commands;

/// <summary><c>sable toolchain &lt;install|list|pin|uninstall&gt;</c> (docs/05 §5.1): see <see cref="Toolchains" />.</summary>
public sealed class ToolchainCommand : PackageCommand {
    public override string Name => "toolchain";
    public override string Summary => "manage toolchain versions: install, list, pin";

    public override string Usage => """
        usage: sable toolchain list
               sable toolchain install <version> --from <build>
               sable toolchain uninstall <version>
               sable toolchain pin <version>

          list                       the installed toolchains, this one, and the pin in effect here
          install <version> --from   install a toolchain from a build of it: a directory with sable
                                     (or sable.dll) in it, or a .tar or .tar.gz of one
          uninstall <version>        remove an installed toolchain
          pin <version>              build this workspace with that toolchain: writes sable-toolchain
                                     at its root, which 'sable' then hands every command to

        Toolchains live in $SABLE_HOME/toolchains (default ~/.sable/toolchains).
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        var home = PackageEnvironment.HomeFromProcess;
        return arguments switch {
            ["list"] => List(home),
            ["install", var version, "--from", var source] => Guard(() => {
                var installed = Toolchains.Install(home, version, source);
                Console.Error.WriteLine($"  Installed toolchain {version} in {installed}");
                return 0;
            }),
            ["install", var version] => Fail($"there are no released toolchains to download yet: install {version} from a build of it with 'sable toolchain install {version} --from <directory or archive>'"),
            ["uninstall", var version] => Guard(() => {
                Toolchains.Uninstall(home, version);
                Console.Error.WriteLine($"    Removed toolchain {version}");
                return 0;
            }),
            ["pin", var version] => Guard(() => {
                var file = Toolchains.WritePin(Directory.GetCurrentDirectory(), version);
                Console.Error.WriteLine($"      Pinned {Path.GetDirectoryName(file)} to toolchain {version}");
                if (version != Toolchains.RunningVersion && Toolchains.Installed(home).All(t => t.Version != version)) {
                    Console.Error.WriteLine($"warning: toolchain {version} is not installed: 'sable toolchain install {version} --from <build>' installs it");
                }

                return 0;
            }),
            _ => BadUsage(),
        };
    }

    static int List(string home) {
        var pin = Toolchains.Pin(Directory.GetCurrentDirectory());
        Console.WriteLine($"{Toolchains.RunningVersion} (this one){(pin?.Version == Toolchains.RunningVersion ? ", pinned here" : "")}");
        foreach (var toolchain in Toolchains.Installed(home).Where(static t => t.Version != Toolchains.RunningVersion)) {
            Console.WriteLine($"{toolchain.Version}{(pin?.Version == toolchain.Version ? " (pinned here)" : "")}  {toolchain.Directory}");
        }

        if (pin is var (version, file) && version != Toolchains.RunningVersion && Toolchains.Installed(home).All(t => t.Version != version)) {
            Console.WriteLine($"{version} (pinned by {file}, not installed)");
        }

        return 0;
    }

    static int Fail(string message) {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }
}
