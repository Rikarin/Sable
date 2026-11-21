using Sable.Driver.Packages;
using Sable.LanguageServer;

namespace Sable.Driver.Commands;

/// <summary>
///     <c>sable lsp</c>: the language server, over standard input and output
///     (docs/05 §5.1). The editor starts it; nothing else talks to it.
/// </summary>
public sealed class LspCommand : Command {
    public override string Name => "lsp";
    public override string Summary => "run the language server over stdio";

    public override string Usage => """
        usage: sable lsp [--stdio]

          Speaks the Language Server Protocol on standard input and output, for an
          editor to start. Problems inside the server go to standard error.
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        if (arguments.Any(static a => a != "--stdio")) {
            return BadUsage();
        }

        var server = new LanguageServer.LanguageServer(
            Console.OpenStandardInput(),
            Console.OpenStandardOutput(),
            new LanguageServerOptions {
                LibraryFor = Library.For,
                LibraryModules = Library.ModuleNames,
                LibraryCheck = static (compilation, own) => Library.CheckFor(compilation, own) is { } check ? new(check.Skipped, () => check.Passed()) : null,
                // Standard output is the protocol's: whatever resolving prints
                // (a download, say) goes to standard error with the rest.
                ProjectsFor = root => LanguageProjects.For(root, PackageEnvironment.FromProcess() with { Out = Console.Error }, Console.Error),
                ExplainLink = DiagnosticDocs.Link,
                Log = Console.Error
            }
        );
        return server.RunAsync().GetAwaiter().GetResult();
    }
}
