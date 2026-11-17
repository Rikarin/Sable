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
}
