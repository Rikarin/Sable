using Sable.Driver.Packages;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Driver.Commands;

/// <summary>
///     <c>sable fix</c>: applies the fixes the lints carry (docs/05 §5.7) —
///     <c>var</c> to <c>let</c>, an unused import removed, <c>x = x</c>
///     deleted — to the package, or to the loose files named.
/// </summary>
public sealed class FixCommand : PackageCommand {
    public override string Name => "fix";
    public override string Summary => "apply the fixes the lints suggest, to the package or to source files";

    public override string Usage => """
        usage: sable fix [--check] [-p <package>] [--features <a,b>]
               sable fix <paths...> [--check] [--allow <lint>] [--warn <lint>] [--error <lint>] [--plugin <dll>]

          In a package, its own files are fixed, at the levels of its [lints] table.
          With paths (.sb files, or directories of them), those files are, as 'sable lint' sees them.
          --check   change nothing: say what would be fixed, and fail if anything would
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        var check = arguments.Contains("--check");
        var rest = arguments.Where(static a => a != "--check").ToList();
        string[] packageOptions = ["-p", "--package", "--features", "-F", "--no-default-features", "--all-features", "--locked"];
        if (rest.Exists(a => packageOptions.Contains(a))) {
            return ParseBuildRequest(rest) is { } selected ? Guard(() => PackageBuilder.Fix(LoadWorkspace(), Environment, selected, check)) : BadUsage();
        }

        if (LintCommand.ReadOptions(rest, "fix", out var paths, out var configuration) is { } failure) {
            return failure;
        }

        // Paths, or lint options only loose files take, or no package here: loose files.
        if (paths.Count > 0 || rest.Count > 0 || Workspace.FindManifestDirectory(Directory.GetCurrentDirectory()) is null) {
            var files = FmtCommand.SourceFiles(paths.Count == 0 ? ["."] : paths).Select(Path.GetFullPath).ToList();
            if (files.Count == 0) {
                Console.Error.WriteLine("sable fix: no .sb files");
                return 1;
            }

            // Named as the user would write them; written back by their full path.
            var byName = files.ToDictionary(static f => Path.GetRelativePath(Directory.GetCurrentDirectory(), f), static f => f, StringComparer.Ordinal);
            return LintFixer.Run(
                () => [..byName.Select(static f => SyntaxTree.Parse(new SourceText(File.ReadAllText(f.Value), f.Key)))],
                static program => program,
                tree => byName[tree.Text.FileName],
                configuration,
                check,
                Console.Out,
                Console.Error);
        }

        return Guard(() => PackageBuilder.Fix(LoadWorkspace(), Environment, new(), check));
    }
}
