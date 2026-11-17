using Sable.Driver.Packages;
using Sable.Syntax;

namespace Sable.Driver.Commands;

/// <summary><c>sable check</c>: type-check only, no code generation — the fast path editors and hooks run.</summary>
public sealed class CheckCommand : PackageCommand {
    public override string Name => "check";
    public override string Summary => "type-check the package (or a file) without generating code";

    public override string Usage => """
        usage: sable check [--features <a,b>] [--no-default-features] [--all-features] [-p <package>] [--locked]
               sable check <file.sab>
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        if (arguments is [var file] && file.EndsWith(".sab", StringComparison.Ordinal)) {
            if (!File.Exists(file)) {
                Console.Error.WriteLine($"sable: no such file '{file}'");
                return 1;
            }

            var errors = Pipeline.Check([SyntaxTree.Parse(new SourceText(File.ReadAllText(file), Path.GetFileName(file)))]);
            foreach (var error in errors) {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine();
            }

            return errors.Count == 0 ? 0 : 1;
        }

        if (ParseBuildRequest(arguments) is not { } request) {
            return BadUsage();
        }

        return Guard(() => PackageBuilder.Check(LoadWorkspace(), Environment, request));
    }
}
