using Sable.Driver.Repl;

namespace Sable.Driver.Commands;

/// <summary>
///     <c>sable repl</c>: an interactive session, run by the SIL interpreter.
///     The console loop only; <see cref="ReplSession" /> holds the logic.
/// </summary>
public sealed class ReplCommand : Command {
    public override string Name => "repl";
    public override string Summary => "an interactive session, run by the interpreter";

    public override string Usage => """
        usage: sable repl

          Enter declarations, statements and expressions; ':help' inside says more.
        """;

    public override int Run(IReadOnlyList<string> arguments) {
        if (arguments.Count != 0) {
            return BadUsage();
        }

        var interactive = !Console.IsInputRedirected;
        if (interactive) {
            Console.WriteLine("Sable repl. ':help' for help, ':quit' to leave.");
        }

        var session = new ReplSession();
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
}
