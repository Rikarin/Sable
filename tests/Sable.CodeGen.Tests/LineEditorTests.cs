using Sable.Driver.Repl;

namespace Sable.CodeGen.Tests;

/// <summary><c>sable repl</c>'s line editor: editing, history and completion, from keys.</summary>
public sealed class LineEditorTests {
    static ConsoleKeyInfo Key(ConsoleKey key, bool control = false) => new('\0', key, shift: false, alt: false, control);

    static IEnumerable<ConsoleKeyInfo> Text(string text) =>
        text.Select(static c => new ConsoleKeyInfo(c, char.IsLetter(c) ? ConsoleKey.A + (char.ToUpperInvariant(c) - 'A') : ConsoleKey.Oem1, false, false, false));

    static LineEditor Editor(IEnumerable<ConsoleKeyInfo> keys, Func<string, IReadOnlyList<string>>? complete = null, StringWriter? output = null) {
        var queue = new Queue<ConsoleKeyInfo>(keys);
        return new LineEditor(() => queue.Dequeue(), output ?? new StringWriter(), complete ?? (static _ => []));
    }

    static string? Read(LineEditor editor) => editor.ReadLine("> ", out _);

    [Fact]
    public void TypesMovesAndDeletesWithinTheLine() {
        // "lt x" → Home, Right, insert 'e' → "let x"; End, Backspace → "let ".
        var keys = Text("lt x").Concat([Key(ConsoleKey.Home), Key(ConsoleKey.RightArrow)]).Concat(Text("e"))
            .Concat([Key(ConsoleKey.End), Key(ConsoleKey.Backspace)]).Concat(Text("y")).Append(Key(ConsoleKey.Enter));
        Assert.Equal("let y", Read(Editor(keys)));
    }

    [Fact]
    public void CutsToEitherEnd() {
        // "abcd|ef" cut to the end → "abcd"; "abc|d" cut to the start → "d".
        var keys = Text("abcdef").Concat([Key(ConsoleKey.LeftArrow), Key(ConsoleKey.LeftArrow), Key(ConsoleKey.K, control: true)])
            .Concat([Key(ConsoleKey.LeftArrow), Key(ConsoleKey.U, control: true), Key(ConsoleKey.Enter)]);
        Assert.Equal("d", Read(Editor(keys)));
        Assert.Equal("b", Read(Editor(Text("ab").Concat([Key(ConsoleKey.A, control: true), Key(ConsoleKey.Delete), Key(ConsoleKey.Enter)]))));
    }

    [Fact]
    public void RecallsEarlierLinesAndTheOneBeingWritten() {
        var keys = Text("first").Append(Key(ConsoleKey.Enter))
            .Concat(Text("second")).Append(Key(ConsoleKey.Enter))
            .Concat(Text("draft")).Concat([Key(ConsoleKey.UpArrow), Key(ConsoleKey.UpArrow), Key(ConsoleKey.Enter)])
            .Concat([Key(ConsoleKey.UpArrow), Key(ConsoleKey.DownArrow)]).Concat(Text("new")).Append(Key(ConsoleKey.Enter));
        var editor = Editor(keys);
        Assert.Equal("first", Read(editor));
        Assert.Equal("second", Read(editor));
        Assert.Equal("first", Read(editor));
        Assert.Equal("new", Read(editor));
        Assert.Equal(["first", "second", "first", "new"], editor.History);
    }

    [Fact]
    public void TabCompletesAName() {
        string[] names = ["total", "twice", "print"];
        IReadOnlyList<string> Complete(string prefix) => [..names.Where(n => n.StartsWith(prefix, StringComparison.Ordinal))];

        Assert.Equal("print(", Read(Editor(Text("pr").Append(Key(ConsoleKey.Tab)).Concat([Key(ConsoleKey.Backspace)]).Concat(Text("(")).Append(Key(ConsoleKey.Enter)), Complete)));

        // Several fit: as far as they agree, then the list.
        var output = new StringWriter();
        Assert.Equal("t", Read(Editor(Text("t").Concat([Key(ConsoleKey.Tab), Key(ConsoleKey.Enter)]), Complete, output)));
        Assert.Contains("total  twice", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CtrlCDropsTheLineAndCtrlDEndsInput() {
        var editor = Editor(Text("oops").Append(Key(ConsoleKey.C, control: true)).Append(Key(ConsoleKey.D, control: true)));
        Assert.Equal("", editor.ReadLine("> ", out var cancelled));
        Assert.True(cancelled);
        Assert.Null(editor.ReadLine("> ", out cancelled));
        Assert.False(cancelled);
    }

    [Fact]
    public void TheSessionCompletesWhatItKnows() {
        var session = new ReplSession();
        session.Submit("var total = 0");
        session.Submit("fn twice(_ n: Int) -> Int { return n * 2 }");
        session.Submit("1");
        Assert.Contains("total", session.Completions("to"));
        Assert.Contains("twice", session.Completions("tw"));
        Assert.Contains("res0", session.Completions("re"));
        Assert.Contains("return", session.Completions("re"));
        Assert.Equal([":type"], session.Completions(":t").Where(static c => c.StartsWith(":t", StringComparison.Ordinal)));
    }
}
