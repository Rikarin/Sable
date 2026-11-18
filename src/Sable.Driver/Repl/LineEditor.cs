using System.Text;

namespace Sable.Driver.Repl;

/// <summary>
///     The REPL's line input on a terminal: editing within the line, the
///     history of earlier lines, and completion of names with Tab.
/// </summary>
/// <remarks>
///     Keys come from <see cref="Console.ReadKey(bool)" /> and the line is
///     redrawn with ANSI escapes, which every terminal sable runs in reads;
///     tests give it keys and read what it drew. The keys are those of
///     readline: ←/→ and Ctrl-B/F move, Home/End and Ctrl-A/E go to an end,
///     Backspace and Delete (Ctrl-D) delete, Ctrl-K and Ctrl-U cut to an end,
///     ↑/↓ and Ctrl-P/N walk the history, Tab completes, Enter submits,
///     Ctrl-C drops the line and Ctrl-D on an empty one ends input.
/// </remarks>
public sealed class LineEditor(Func<ConsoleKeyInfo> readKey, TextWriter output, Func<string, IReadOnlyList<string>> complete) {
    readonly List<string> _history = [];

    /// <summary>The lines entered so far, oldest first.</summary>
    public IReadOnlyList<string> History => _history;

    /// <summary>Remembers a line, as if it had been entered: one read from a history file.</summary>
    public void AddHistory(string line) {
        if (line.Length > 0 && (_history.Count == 0 || _history[^1] != line)) {
            _history.Add(line);
        }
    }

    /// <summary>
    ///     Reads a line after the prompt; null at the end of input, and the
    ///     empty string with <paramref name="cancelled" /> when Ctrl-C dropped it.
    /// </summary>
    public string? ReadLine(string prompt, out bool cancelled) {
        cancelled = false;
        var line = new StringBuilder();
        var cursor = 0;

        // Where in the history the line shown is from; the history's length
        // for the line being written, which is kept while walking away.
        var recalled = _history.Count;
        var draft = "";
        output.Write(prompt);

        while (true) {
            var key = readKey();
            var control = (key.Modifiers & ConsoleModifiers.Control) != 0;
            switch (key.Key) {
                case ConsoleKey.Enter:
                    output.Write("\r\n");
                    AddHistory(line.ToString());
                    return line.ToString();

                case ConsoleKey.C when control:
                    output.Write("^C\r\n");
                    cancelled = true;
                    return "";

                case ConsoleKey.D when control && line.Length == 0:
                    output.Write("\r\n");
                    return null;

                case ConsoleKey.D when control:
                case ConsoleKey.Delete:
                    if (cursor < line.Length) {
                        line.Remove(cursor, 1);
                    }

                    break;

                case ConsoleKey.Backspace:
                    if (cursor > 0) {
                        line.Remove(--cursor, 1);
                    }

                    break;

                case ConsoleKey.LeftArrow:
                case ConsoleKey.B when control:
                    cursor = Math.Max(0, cursor - 1);
                    break;

                case ConsoleKey.RightArrow:
                case ConsoleKey.F when control:
                    cursor = Math.Min(line.Length, cursor + 1);
                    break;

                case ConsoleKey.Home:
                case ConsoleKey.A when control:
                    cursor = 0;
                    break;

                case ConsoleKey.End:
                case ConsoleKey.E when control:
                    cursor = line.Length;
                    break;

                case ConsoleKey.K when control:
                    line.Remove(cursor, line.Length - cursor);
                    break;

                case ConsoleKey.U when control:
                    line.Remove(0, cursor);
                    cursor = 0;
                    break;

                case ConsoleKey.UpArrow:
                case ConsoleKey.P when control:
                case ConsoleKey.DownArrow:
                case ConsoleKey.N when control: {
                    var older = key.Key is ConsoleKey.UpArrow or ConsoleKey.P;
                    var next = recalled + (older ? -1 : 1);
                    if (next < 0 || next > _history.Count) {
                        break;
                    }

                    if (recalled == _history.Count) {
                        draft = line.ToString();
                    }

                    recalled = next;
                    line.Clear().Append(recalled == _history.Count ? draft : _history[recalled]);
                    cursor = line.Length;
                    break;
                }

                case ConsoleKey.Tab:
                    cursor = Complete(prompt, line, cursor);
                    break;

                default:
                    if (key.KeyChar >= ' ' && !control) {
                        line.Insert(cursor++, key.KeyChar);
                    }

                    break;
            }

            Redraw(prompt, line, cursor);
        }
    }

    /// <summary>
    ///     Completes the name before the cursor: wholly when one name fits,
    ///     as far as they agree when several do — and when that is no
    ///     further, lists them below the line.
    /// </summary>
    int Complete(string prompt, StringBuilder line, int cursor) {
        var start = cursor;
        while (start > 0 && (char.IsLetterOrDigit(line[start - 1]) || line[start - 1] is '_' or ':')) {
            start--;
        }

        var prefix = line.ToString(start, cursor - start);
        var candidates = complete(prefix).Where(c => c.StartsWith(prefix, StringComparison.Ordinal)).Distinct().Order(StringComparer.Ordinal).ToList();
        if (candidates.Count == 0) {
            return cursor;
        }

        var common = candidates.Aggregate(static (a, b) => a[..a.Zip(b).TakeWhile(static p => p.First == p.Second).Count()]);
        if (candidates.Count == 1) {
            common += " ";
        }

        if (common.Length > prefix.Length) {
            line.Insert(cursor, common[prefix.Length..]);
            return cursor + common.Length - prefix.Length;
        }

        output.Write("\r\n" + string.Join("  ", candidates) + "\r\n");
        output.Write(prompt);
        return cursor;
    }

    /// <summary>Draws the line again: back to its start, the prompt and text, the rest of the row cleared, the cursor put back.</summary>
    void Redraw(string prompt, StringBuilder line, int cursor) {
        output.Write("\r" + prompt + line + "\u001b[K");
        if (line.Length > cursor) {
            output.Write($"\u001b[{line.Length - cursor}D");
        }
    }
}
