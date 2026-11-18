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

        // Ctrl-C stops the entry that is running, not the session; at the
        // prompt the line editor reads it as a key, and drops the line.
        Console.CancelKeyPress += (_, e) => e.Cancel = session.Interrupt();

        var editor = interactive && !Console.IsOutputRedirected ? Editor(session) : null;
        var pending = "";
        while (true) {
            var prompt = pending.Length == 0 ? "> " : ". ";
            string? line;
            if (editor is not null) {
                Console.TreatControlCAsInput = true;
                try {
                    line = editor.ReadLine(prompt, out var cancelled);
                    if (cancelled) {
                        pending = "";
                        continue;
                    }
                } finally {
                    Console.TreatControlCAsInput = false;
                }

                if (line is { Length: > 0 }) {
                    SaveHistory(line);
                }
            } else {
                if (interactive) {
                    Console.Write(prompt);
                }

                line = Console.ReadLine();
            }

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

    /// <summary>The line editor on the console, with the lines of earlier sessions to recall.</summary>
    static LineEditor Editor(ReplSession session) {
        var editor = new LineEditor(static () => Console.ReadKey(intercept: true), Console.Out, session.Completions);
        try {
            if (File.Exists(HistoryPath)) {
                foreach (var line in File.ReadLines(HistoryPath).TakeLast(HistoryLength)) {
                    editor.AddHistory(line);
                }
            }
        } catch (IOException) {
            // No history, then.
        }

        return editor;
    }

    const int HistoryLength = 1000;

    /// <summary>Where the lines entered are kept between sessions: beside the package cache, under the Sable home.</summary>
    static string HistoryPath =>
        Path.Combine(
            Environment.GetEnvironmentVariable("SABLE_HOME") is { Length: > 0 } home ? home : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sable"),
            "repl_history"
        );

    static void SaveHistory(string line) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            File.AppendAllText(HistoryPath, line.ReplaceLineEndings(" ") + "\n");
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // The session goes on without it.
        }
    }
}
