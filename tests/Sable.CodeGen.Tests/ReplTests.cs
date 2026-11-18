using Sable.Driver.Repl;

namespace Sable.CodeGen.Tests;

/// <summary><c>sable repl</c>'s session: state across entries, values and types, errors, commands.</summary>
public sealed class ReplTests {
    static string Out(ReplSession session, string entry) {
        var result = session.Submit(entry);
        Assert.False(result.Failed, result.Output);
        return result.Output;
    }

    [Fact]
    public void AnExpressionShowsItsValueAndTypeAndIsKept() {
        var session = new ReplSession();
        Assert.Equal("res0: Int = 3", Out(session, "1 + 2"));
        Assert.Equal("res1: String = \"ab\"", Out(session, "\"a\" + \"b\""));
        Assert.Equal("res2: Int = 6", Out(session, "res0 * 2"));
    }

    [Fact]
    public void BindingsAndDeclarationsPersist() {
        var session = new ReplSession();
        Assert.Equal("", Out(session, "var total = 0"));
        Out(session, "for i in 0..<5 {\n    total += i\n}");
        Assert.Equal("defined twice", Out(session, "fn twice(_ n: Int) -> Int { return n * 2 }"));
        Assert.Equal("res0: Int = 20", Out(session, "twice(total)"));

        Assert.Equal("defined Point", Out(session, "struct Point {\n    var x: Int\n    var y: Int\n}"));
        Assert.Equal("res1: Point = Point(x: 1, y: 2)", Out(session, "Point { x: 1, y: 2 }"));
    }

    [Fact]
    public void ADeclarationCanBeReplaced() {
        var session = new ReplSession();
        Out(session, "fn f() -> Int { return 1 }");
        Out(session, "fn f() -> Int { return 2 }");
        Assert.Equal("res0: Int = 2", Out(session, "f()"));
    }

    [Fact]
    public void EarlierOutputIsNotRepeated() {
        var session = new ReplSession();
        Assert.Equal("once", Out(session, "print(\"once\")"));
        Assert.Equal("twice", Out(session, "print(\"twice\")"));
        Assert.Equal("res0: Int = 1", Out(session, "1"));
    }

    [Fact]
    public void AnErrorIsShownAndForgotten() {
        var session = new ReplSession();
        var result = session.Submit("let x: Int = \"text\"");
        Assert.True(result.Failed);
        Assert.Contains("S3004", result.Output, StringComparison.Ordinal);
        Assert.Equal(0, session.StatementCount);

        result = session.Submit("missing + 1");
        Assert.True(result.Failed);
        Assert.Contains("S2002", result.Output, StringComparison.Ordinal);

        Assert.Equal("res0: Int = 1", Out(session, "1"));
    }

    [Fact]
    public void ATrapIsReportedAndTheStatementDropped() {
        var session = new ReplSession();
        Out(session, "let xs = [1, 2, 3]");
        var result = session.Submit("xs[7]");
        Assert.True(result.Failed);
        Assert.Contains("trap", result.Output, StringComparison.Ordinal);
        Assert.Equal("res0: Int = 3", Out(session, "xs.count"));
    }

    [Fact]
    public void TypeShowsAnExpressionsTypeWithoutRunningIt() {
        var session = new ReplSession();
        Out(session, "fn f(_ n: Int) -> Bool { return n > 0 }");
        Assert.Equal("(Int) -> Bool", Out(session, ":type f"));
        Assert.Equal("F64", Out(session, ":type 1.5 * 2.0"));
        Assert.Equal("res0: Int = 1", Out(session, "1"));
    }

    [Fact]
    public void ImportsALibraryModule() {
        var session = new ReplSession();
        Assert.True(session.Submit("let s = Stack<Int>()").Failed);
        Assert.Equal("imported Sable.Collections", Out(session, "import Sable.Collections"));
        Out(session, "let s = Stack<Int>()");
        Out(session, "s.push(4)");
        Assert.Equal("res0: Int? = 4", Out(session, "s.peek()"));
        Assert.True(session.Submit("import Sable.Nonsense").Failed);
    }

    [Fact]
    public void CommandsHelpResetAndQuit() {
        var session = new ReplSession();
        Assert.Contains(":type", Out(session, ":help"), StringComparison.Ordinal);
        Out(session, "let x = 1");
        Out(session, ":reset");
        Assert.True(session.Submit("x").Failed);
        Assert.True(session.Submit(":quit").Quit);
        Assert.True(session.Submit(":nonsense").Failed);
    }

    [Theory]
    [InlineData("1 + 2", true)]
    [InlineData("fn f() {", false)]
    [InlineData("fn f() {\n}", true)]
    [InlineData("let s = \"{\"", true)]
    [InlineData("foo(1,", false)]
    public void KnowsWhenAnEntryIsComplete(string text, bool complete) => Assert.Equal(complete, ReplSession.IsComplete(text));

    /// <summary>An earlier statement's effects are replayed from the journal, not carried out again.</summary>
    [Fact]
    public void AStatementsEffectsHappenOnce() {
        var path = Path.Combine(Directory.CreateTempSubdirectory("sable-repl-").FullName, "log.txt");
        try {
            var session = new ReplSession();
            Out(session, $"let wrote = Runtime.writeFile(path: \"{path}\", text: \"x\", append: true)");
            Out(session, "1");
            Out(session, "2");
            Assert.Equal("x", File.ReadAllText(path));

            // The clock reads as it did, though it has moved on.
            Out(session, "let then = Runtime.monotonicNanoseconds()");
            var first = Out(session, "then");
            Out(session, "Runtime.sleep(nanoseconds: 1000000)");
            Assert.Equal(first.Replace("res2", "res3", StringComparison.Ordinal), Out(session, "then"));
        } finally {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    /// <summary>A failed entry's effects happened, but are not replayed into the next one's.</summary>
    [Fact]
    public void AFailedEntrysEffectsAreNotReplayed() {
        var path = Path.Combine(Directory.CreateTempSubdirectory("sable-repl-").FullName, "log.txt");
        try {
            var session = new ReplSession();
            var result = session.Submit($"Runtime.writeFile(path: \"{path}\", text: \"a\", append: true)\nlet list = [1]\nprint(list[5])");
            Assert.True(result.Failed, result.Output);
            Out(session, $"let wrote = Runtime.writeFile(path: \"{path}\", text: \"b\", append: true)");
            Out(session, "1");
            Assert.Equal("ab", File.ReadAllText(path));
        } finally {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    /// <summary>What runs natively runs here: deep calls and long loops, past the interpreter's usual limits.</summary>
    [Fact]
    public void DeepRecursionAndLongLoopsRun() {
        var session = new ReplSession();
        Out(session, "fn down(_ n: Int) -> Int {\n    if n == 0 { return 0 }\n    return down(n - 1) + 1\n}");
        Assert.Equal("res0: Int = 5000", Out(session, "down(5000)"));
        // Over ten million steps, the interpreter's usual limit.
        Assert.Equal("2000000", Out(session, "var i = 0\nwhile i < 2_000_000 { i += 1 }\nprint(i)"));
    }

    /// <summary>An entry that never ends is stopped by Ctrl-C, and the session goes on.</summary>
    [Fact]
    public async Task ARunawayEntryCanBeInterrupted() {
        var session = new ReplSession();
        Assert.False(session.Interrupt());
        var running = Task.Run(() => session.Submit("var n = 0\nwhile true { n += 1 }"));
        while (!session.Interrupt()) {
            await Task.Delay(10);
        }

        var result = await running.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(result.Failed);
        Assert.Contains("trap: interrupted", result.Output, StringComparison.Ordinal);
        Assert.Equal("res0: Int = 1", Out(session, "1"));
    }
}
