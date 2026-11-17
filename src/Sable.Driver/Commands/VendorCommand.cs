using Sable.Driver.Packages;

namespace Sable.Driver.Commands;

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
