using Sable.Driver.Packages;
using Sable.Driver.Repl;
using Sable.Syntax;

namespace Sable.Driver.Commands;

/// <summary>
///     <c>sable repl</c>: an interactive session, run by the SIL interpreter.
///     The console loop only; <see cref="ReplSession" /> holds the logic. In a
///     package the session has the package's library and its dependencies, so
///     their modules can be imported.
/// </summary>
public sealed class ReplCommand : PackageCommand {
    public override string Name => "repl";
    public override string Summary => "an interactive session, run by the interpreter";

    public override string Usage => """
        usage: sable repl [-p <package>] [--features <a,b>] [--no-package]

          Enter declarations, statements and expressions; ':help' inside says more.

          In a package, its library's modules and its dependencies' can be imported,
          with its default features (or those --features names).
          -p <name>       the workspace member (default: the one you are in)
          --no-package    a session without the package, as outside one
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        var noPackage = arguments.Contains("--no-package");
        var options = arguments.Where(static a => a != "--no-package").ToList();
        if (ParseBuildRequest(options) is not { } request || request.Bin is not null || request.Release || noPackage && options.Count > 0) {
            return BadUsage();
        }

        IReadOnlyList<SyntaxTree>? package = null;
        string? member = null;
        if (!noPackage && Workspace.FindManifestDirectory(Directory.GetCurrentDirectory()) is not null) {
            var loaded = Guard(() => {
                (package, member) = Package(request);
                return package is null ? 1 : 0;
            });
            if (loaded != 0) {
                return loaded;
            }
        } else if (options.Count > 0) {
            Console.Error.WriteLine("sable repl: -p and --features name a package, and there is no package here");
            return 2;
        }

        var interactive = !Console.IsInputRedirected;
        if (interactive) {
            Console.WriteLine("Sable repl. ':help' for help, ':quit' to leave.");
            if (member is not null) {
                var modules = package!.Select(static t => t.Root is CompilationUnitSyntax { Module: { } m } ? m.Name.ToString().Trim() : null).OfType<string>().Distinct().Order(StringComparer.Ordinal);
                Console.WriteLine($"In {member}: 'import' any of {string.Join(", ", modules)}.");
            }
        }

        var session = new ReplSession(package);
        var pending = "";
        while (true) {
            if (interactive) {
                Console.Write(pending.Length == 0 ? "> " : ". ");
            }

            var line = Console.ReadLine();
            if (line is null) {
                break;
            }

            pending = pending.Length == 0 ? line : pending + "\n" + line;
            if (!ReplSession.IsComplete(pending)) {
                continue;
            }

            var result = session.Submit(pending);
            pending = "";
            if (result.Quit) {
                break;
            }

            if (result.Output.Length > 0) {
                (result.Failed ? Console.Error : Console.Out).WriteLine(result.Output);
            }
        }

        return 0;
    }

    /// <summary>
    ///     The package's library and its dependencies' as a debug build parses
    ///     them; null, having printed why, when they do not compile.
    /// </summary>
    static (IReadOnlyList<SyntaxTree>? Trees, string Member) Package(BuildRequest request) {
        var workspace = LoadWorkspace();
        var member = Member(workspace, request.Package);
        var graph = PackageBuilder.Resolve(workspace, Environment, request with { Package = member.Name });
        var trees = PackageBuilder.Program(graph, member.Name, null);
        var (errors, _) = Pipeline.Diagnose(trees);
        if (errors.Count > 0) {
            foreach (var error in errors) {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine();
            }

            Console.Error.WriteLine($"error: {member.Name} does not compile, so the session cannot have it; fix it, or start one with --no-package");
            return (null, member.Name);
        }

        return (trees, member.Name);
    }
}
