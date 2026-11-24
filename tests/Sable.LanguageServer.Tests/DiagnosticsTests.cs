using System.Text.Json;
using Xunit;

namespace Sable.LanguageServer.Tests;

/// <summary>
///     Diagnostics beyond the type errors of open documents (#193): the
///     lints, files nobody opened, links to the long-form pages, and the
///     pull model.
/// </summary>
public sealed class DiagnosticsTests : IDisposable {
    readonly string _root = Path.Combine(Path.GetTempPath(), "sable-lsp-tests", Guid.NewGuid().ToString("N"));

    public DiagnosticsTests() => Directory.CreateDirectory(_root);

    public void Dispose() {
        try {
            Directory.Delete(_root, true);
        } catch (IOException) {
            // Left for the system to clean.
        }
    }

    string Write(string name, string text) {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return Workspace.UriOf(path);
    }

    const string Unused = "module App\n\nfn area(_ width: Int, _ height: Int) -> Int {\n    let perimeter = 2 * (width + height)\n    return width * height\n}\n";

    const string Moved = "module App\n\n@noncopy struct H { var v: Int }\n\nfn take(_ h: consuming H) -> Int { return h.v }\n\nfn f() -> Int {\n    let h = H(v: 1)\n    let a = take(h)\n    return a + take(h)\n}\n";

    static bool Has(JsonElement diagnostics, string code) => diagnostics.EnumerateArray().Any(d => d.GetString("code") == code);

    /// <summary>What a client that pulls diagnostics says it can do.</summary>
    static object Pulling(bool refresh) =>
        new { textDocument = new { diagnostic = new { dynamicRegistration = false } }, workspace = new { diagnostics = new { refreshSupport = refresh } } };

    [Fact]
    public async Task LintsArePublishedWithALinkAndFaded() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/lint.sb";
        await client.OpenAsync(uri, Unused);

        var diagnostics = await client.DiagnosticsAsync(uri, static d => Has(d, "S3300"));
        var unused = diagnostics.EnumerateArray().Single(static d => d.GetString("code") == "S3300");
        Assert.Equal(2, unused.GetInt("severity"));
        Assert.Equal(3, unused.Get("range")!.Value.Get("start")!.Value.GetInt("line"));
        Assert.EndsWith("docs/diagnostics/S3300.md", unused.Get("codeDescription")!.Value.GetString("href"), StringComparison.Ordinal);
        Assert.Equal([1], unused.Get("tags")!.Value.EnumerateArray().Select(static t => t.GetInt32()));
    }

    static (string Uri, int Line, string Message) Related(JsonElement diagnostic) {
        var related = diagnostic.Get("relatedInformation")!.Value.EnumerateArray().Single();
        var location = related.Get("location")!.Value;
        return (location.GetString("uri")!, location.Get("range")!.Value.Get("start")!.Value.GetInt("line")!.Value, related.GetString("message")!);
    }

    /// <summary>
    ///     A check run once every signature is bound reports in its own
    ///     file, not in another with a token at the same offset whose text
    ///     has the name its message quotes.
    /// </summary>
    [Fact]
    public async Task ALateCheckReportsInItsOwnFile() {
        const string Wrong = "module App\n\nfn hold(@escaping _ n: Int) -> Int {\n    return n\n}\n";
        var offset = Wrong.IndexOf("@escaping", StringComparison.Ordinal);
        const string Right = "module App\n\nfn sums(nnnnnnnnnnnn: Int) -> Int {\n    return nnnnnnnnnnnn\n}\n";
        Assert.Equal(offset, Right.IndexOf("nnnn", StringComparison.Ordinal));
        var wrongUri = Write("wrong.sb", Wrong);
        var rightUri = Write("right.sb", Right);
        await using var client = new TestClient();
        await client.InitializeAsync(_root);
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(wrongUri, Wrong);
        await client.OpenAsync(rightUri, Right);

        var reported = await client.DiagnosticsAsync(wrongUri, static d => Has(d, "S3093"));
        Assert.Single(reported.EnumerateArray());
        Assert.Empty((await client.DiagnosticsAsync(rightUri)).EnumerateArray());
    }

    [Fact]
    public async Task ADuplicatePointsAtTheDeclarationItClashesWith() {
        const string First = "module App\n\nstruct Point { var x: Int }\n";
        const string Second = "module App\n\nfn f() { }\n\nstruct Point { var y: Int }\n";
        var first = Write("first.sb", First);
        var second = Write("second.sb", Second);
        await using var client = new TestClient();
        await client.InitializeAsync(_root);
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(second, Second);

        var diagnostics = await client.DiagnosticsAsync(second, static d => Has(d, "S2000"));
        var duplicate = diagnostics.EnumerateArray().Single(static d => d.GetString("code") == "S2000");
        Assert.Equal((first, 2, "declared first here"), Related(duplicate));
    }

    [Fact]
    public async Task ALocalDeclaredTwicePointsAtTheFirst() {
        const string Text = "module App\n\nfn f() -> Int {\n    let a = 1\n    let a = 2\n    return a\n}\n";
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/twice.sb";
        await client.OpenAsync(uri, Text);

        var diagnostics = await client.DiagnosticsAsync(uri, static d => Has(d, "S2000"));
        var duplicate = diagnostics.EnumerateArray().Single(static d => d.GetString("code") == "S2000");
        Assert.Equal(4, duplicate.Get("range")!.Value.Get("start")!.Value.GetInt("line"));
        Assert.Equal((uri, 3, "declared first here"), Related(duplicate));
    }

    [Fact]
    public async Task AConformanceThatFailsPointsAtTheRequirement() {
        const string Text = "module App\n\nprotocol Shape {\n    fn area() -> Int\n}\n\nstruct Square: Shape {\n    var side: Int\n    fn area() -> String { return \"\" }\n}\n";
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/conformance.sb";
        await client.OpenAsync(uri, Text);

        var diagnostics = await client.DiagnosticsAsync(uri, static d => d.GetArrayLength() > 0);
        var mismatch = diagnostics.EnumerateArray().First(static d => d.Get("relatedInformation") is not null);
        Assert.Equal((uri, 3, "'Shape' requires it here"), Related(mismatch));
    }

    [Fact]
    public async Task AUseAfterAMovePointsAtTheMove() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/moved-related.sb";
        await client.OpenAsync(uri, Moved);

        var diagnostics = await client.DiagnosticsAsync(uri, static d => Has(d, "S3057"));
        var used = diagnostics.EnumerateArray().Single(static d => d.GetString("code") == "S3057");
        Assert.Equal(9, used.Get("range")!.Value.Get("start")!.Value.GetInt("line"));
        Assert.Equal((uri, 8, "'h' is moved here"), Related(used));
    }

    /// <summary>
    ///     The SIL checks of a small file leave the standard library alone
    ///     once it is known to check cleanly (#182): they bind none of its
    ///     bodies, lower only what the file reaches, and still find the
    ///     file's own problems. Until it is known, they check it whole, and
    ///     say so when nothing was found — what the next server reads.
    /// </summary>
    [Fact]
    public async Task TheChecksLeaveALibraryKnownToBeCleanAlone() {
        var passed = 0;
        var asked = new List<IReadOnlyCollection<Sable.Syntax.SyntaxTree>>();
        LibraryKnowledge? Unknown(Sable.Symbols.Compilation compilation, IReadOnlyCollection<Sable.Syntax.SyntaxTree> own) {
            lock (asked) {
                asked.Add(own);
            }

            return new(null, () => Interlocked.Increment(ref passed));
        }

        await using (var client = new TestClient(libraryCheck: Unknown)) {
            await client.InitializeAsync();
            const string uri = "file:///tmp/clean-library.sb";
            await client.OpenAsync(uri, "module App\n\nfn f() -> Int {\n    return [1, 2].count\n}\n");
            await client.DiagnosticsAsync(uri, static d => d.GetArrayLength() == 0);
            await WaitUntil(() => Volatile.Read(ref passed) == 1, client);

            // Asked with the program's own files: the document, not the library's.
            var own = Assert.Single(asked[0]);
            Assert.EndsWith("clean-library.sb", own.Text.FileName, StringComparison.Ordinal);
        }

        await using (var client = new TestClient(libraryCheck: (compilation, own) => new(t => !own.Contains(t), static () => throw new InvalidOperationException("nothing was checked whole")))) {
            await client.InitializeAsync();
            const string uri = "file:///tmp/moved-known.sb";
            await client.OpenAsync(uri, Moved);
            await client.DiagnosticsAsync(uri, static d => Has(d, "S3057"));
            Assert.Contains("the library known to check cleanly", client.Logged, StringComparison.Ordinal);
        }
    }

    static async Task WaitUntil(Func<bool> condition, TestClient client) {
        for (var i = 0; i < 600 && !condition(); i++) {
            await Task.Delay(50);
        }

        Assert.True(condition(), client.Logged);
    }

    [Fact]
    public async Task AnOverlappingAccessPointsAtBothArguments() {
        const string Text = "module App\n\nfn swap(_ a: inout Int, _ b: inout Int) { }\n\nfn f() {\n    var x = 1\n    swap(&x, &x)\n}\n";
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/overlap.sb";
        await client.OpenAsync(uri, Text);

        var diagnostics = await client.DiagnosticsAsync(uri, static d => Has(d, "S3062"));
        var overlap = diagnostics.EnumerateArray().Single(static d => d.GetString("code") == "S3062");
        var related = overlap.Get("relatedInformation")!.Value.EnumerateArray().ToList();
        Assert.Equal(2, related.Count);
        // Each is the variable an argument passes: 'x' in '    swap(&x, &x)'.
        Assert.Equal([10, 14], related.Select(static r => r.Get("location")!.Value.Get("range")!.Value.Get("start")!.Value.GetInt("character")!.Value));
        Assert.StartsWith("argument 1", related[0].GetString("message"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileWithErrorsIsNotLinted() {
        await using var client = new TestClient();
        await client.InitializeAsync();
        const string uri = "file:///tmp/lint-broken.sb";
        await client.OpenAsync(uri, Unused.Replace("return width * height", "return nowhere", StringComparison.Ordinal));

        // The lowering round comes after the type errors; wait for it too.
        var diagnostics = await client.DiagnosticsAsync(uri, static d => Has(d, "S2002") || Has(d, "S2000"));
        await Task.Delay(500);
        Assert.DoesNotContain(client.Sent("textDocument/publishDiagnostics"),
            static n => n.Get("params")!.Value.GetString("uri") == uri && Has(n.Get("params")!.Value.Get("diagnostics")!.Value, "S3300"));
        Assert.True(diagnostics.GetArrayLength() > 0);
    }

    [Fact]
    public async Task APackagesLintTableSetsTheLevels() {
        const string Scaled = "module App\n\nfn scale(_ factor: Int, _ ignored: Int) -> Int {\n    let spare = factor\n    return factor * 2\n}\n";
        var main = Write("app/src/main.sb", Scaled);
        var lints = Symbols.LintConfiguration.Default.With("unused_local", Symbols.LintLevel.Allow).With("unused_parameter", Symbols.LintLevel.Error);
        var app = new LanguageProject("app", Path.Combine(_root, "app"), [], []) { Lints = lints };
        await using var client = new TestClient(projects: _ => [app]);
        await client.InitializeAsync(_root);
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(main, Scaled);

        var diagnostics = await client.DiagnosticsAsync(main, static d => Has(d, "S3301"));
        Assert.False(Has(diagnostics, "S3300"));
        Assert.Equal(1, diagnostics.EnumerateArray().Single(static d => d.GetString("code") == "S3301").GetInt("severity"));
    }

    [Fact]
    public async Task ALateCheckIsReportedInItsOwnFileOnly() {
        // A variance error is found by a check run after every file is
        // declared. The other file has a token at the very same offset, with
        // the same name under the span: it used to be reported there too.
        var wrong = Write("wrong.sb", "module App\n\npublic protocol Producer<out T> {\n    fn accept(_ value: T)\n}\n");
        var right = Write("right.sb", "module App\n\npublic protocol Taker<T>        {\n    fn accept(_ value: T)\n}\n");
        await using var client = new TestClient();
        await client.InitializeAsync(_root);
        await client.NotifyAsync("initialized", new { });

        await client.DiagnosticsAsync(wrong, static d => Has(d, "S3047"));
        await client.DiagnosticsAsync(right, static d => d.GetArrayLength() == 0);
    }

    [Fact]
    public async Task AFileNobodyOpenedIsDiagnosed() {
        var closed = Write("closed.sb", "module App\n\nfn f() -> Int {\n    let s: String = 1\n    return 2\n}\n");
        var moved = Write("moved.sb", Moved);
        await using var client = new TestClient();
        await client.InitializeAsync(_root);
        await client.NotifyAsync("initialized", new { });

        var diagnostics = await client.DiagnosticsAsync(closed, static d => d.GetArrayLength() > 0);
        Assert.Equal(3, diagnostics[0].Get("range")!.Value.Get("start")!.Value.GetInt("line"));
        var published = await client.MessageAsync("textDocument/publishDiagnostics", p => p.GetString("uri") == closed);
        Assert.Null(published.Get("version"));

        // Fixed on disk, and the editor told so: cleared. With no type
        // errors left, the other file's checks on SIL run.
        File.WriteAllText(Workspace.PathOf(closed)!, "module App\n\nfn g() -> Int {\n    return 2\n}\n");
        var since = client.Received;
        await client.NotifyAsync("workspace/didChangeWatchedFiles", new { changes = new[] { new { uri = closed, type = 2 } } });
        await client.DiagnosticsAsync(closed, static d => d.GetArrayLength() == 0, since);
        await client.DiagnosticsAsync(moved, static d => Has(d, "S3057"), since);

        // And deleted: cleared too. Its report before the checks ran may
        // have been empty already; only one sent after the deletion counts.
        File.Delete(Workspace.PathOf(moved)!);
        since = client.Received;
        await client.NotifyAsync("workspace/didChangeWatchedFiles", new { changes = new[] { new { uri = moved, type = 3 } } });
        await client.DiagnosticsAsync(moved, static d => d.GetArrayLength() == 0, since);
    }

    [Fact]
    public async Task AChangeUnderAHiddenFolderIsNotTakenIn() {
        // The client watches every *.sb under the folder, so it reports the
        // files of a git worktree under .claude/ or a build's target/ too.
        // Taken in, each such change added a copy of the repository's files
        // to the workspace, one change at a time, and started a round over
        // all of them: the server compiled without end while agents worked.
        var closed = Write("closed.sb", "module App\n\nfn f() -> Int {\n    let s: String = 1\n    return 2\n}\n");
        const string Broken = "module Copy\n\nfn g() -> Int {\n    let s: String = 1\n    return 2\n}\n";
        var hidden = Write(".claude/worktrees/agent/lib/copy.sb", Broken);
        var output = Write("target/debug/out.sb", Broken);
        await using var client = new TestClient();
        await client.InitializeAsync(_root);
        await client.NotifyAsync("initialized", new { });
        await client.DiagnosticsAsync(closed, static d => d.GetArrayLength() > 0);
        var built = client.Server.AnalysesBuilt;

        await client.NotifyAsync("workspace/didChangeWatchedFiles", new { changes = new[] { new { uri = hidden, type = 2 }, new { uri = output, type = 1 } } });

        // A change to a file of the workspace after them: once its round is
        // over, the two before it were seen, and neither was diagnosed.
        File.WriteAllText(Workspace.PathOf(closed)!, "module App\n\nfn f() -> Int {\n    return 2\n}\n");
        var since = client.Received;
        await client.NotifyAsync("workspace/didChangeWatchedFiles", new { changes = new[] { new { uri = closed, type = 2 } } });
        await client.DiagnosticsAsync(closed, static d => d.GetArrayLength() == 0, since);
        Assert.DoesNotContain(client.Sent("textDocument/publishDiagnostics"), n => n.Get("params")?.GetString("uri") is { } uri && (uri == hidden || uri == output));
        Assert.Equal(built + 1, client.Server.AnalysesBuilt);
    }

    [Fact]
    public async Task AnIdleServerCompilesNothing() {
        // Once a round's diagnostics are out, nothing the server does on its
        // own may start another: no round, no compilation, no lowering. It
        // sat at 100-150% CPU for hours twice (#284), each time from work
        // that started itself again — the second, lowering a class that
        // inherits from itself.
        var moved = Write("moved.sb", Moved.Replace("module App", "module Moves", StringComparison.Ordinal));
        var unused = Write("unused.sb", Unused);
        var broken = Write("broken.sb", "module Broken\n\nfn g() -> Int {\n    let s: String = 1\n    return 2\n}\n\nclass Loop: Loop {}\n");
        await using var client = new TestClient();
        await client.InitializeAsync(_root);
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(unused, Unused);
        await client.DiagnosticsAsync(unused, static d => Has(d, "S3300"));
        await client.DiagnosticsAsync(moved);
        await client.DiagnosticsAsync(broken, static d => d.GetArrayLength() > 0);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (client.Server.Diagnosing && DateTime.UtcNow < deadline) {
            await Task.Delay(20);
        }

        Assert.False(client.Server.Diagnosing, "the first round never ended");
        var (rounds, built, lowered, sent) = (client.Server.RoundsRun, client.Server.AnalysesBuilt, client.Server.LoweringsStarted, client.Received);

        // Many times the debounce delay, with nothing changed.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Equal(rounds, client.Server.RoundsRun);
        Assert.Equal(built, client.Server.AnalysesBuilt);
        Assert.Equal(lowered, client.Server.LoweringsStarted);
        Assert.Equal(sent, client.Received);
        Assert.False(client.Server.Diagnosing);
    }

    [Fact]
    public async Task EachLoadOfThePackagesIsDiagnosed() {
        // A manifest changed: the packages are read again, and the round
        // after the reading starts at once, with no quiet to wait out. A
        // change cancelled that round as it started, now and then, and no
        // round came after it; each of these is a chance for that to happen.
        var closed = Write("closed.sb", "module App\n");
        var manifest = Workspace.UriOf(Path.Combine(_root, "sable.toml"));
        await using var client = new TestClient();
        await client.InitializeAsync(_root);
        await client.NotifyAsync("initialized", new { });
        await client.DiagnosticsAsync(closed);

        for (var i = 0; i < 40; i++) {
            // Each time wrong on another line, so no earlier report will do.
            var text = "module App\n" + new string('\n', i) + "fn f() -> Int {\n    let s: String = 1\n    return 2\n}\n";
            var line = text[..text.IndexOf("let s", StringComparison.Ordinal)].Count(static c => c == '\n');
            File.WriteAllText(Workspace.PathOf(closed)!, text);
            await client.NotifyAsync("workspace/didChangeWatchedFiles", new { changes = new[] { new { uri = closed, type = 2 }, new { uri = manifest, type = 2 } } });
            await client.DiagnosticsAsync(closed, d => d.EnumerateArray().Any(e => e.Get("range")!.Value.Get("start")!.Value.GetInt("line") == line));
        }
    }

    [Fact]
    public async Task ClosingAFileOfTheWorkspaceKeepsItsDiagnosticsFromDisk() {
        const string broken = "module App\n\nfn f() -> Int {\n    let s: String = 1\n    return 2\n}\n";
        var uri = Write("kept.sb", broken);
        await using var client = new TestClient();
        await client.InitializeAsync(_root);
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(uri, "module App\n\nfn f() -> Int {\n    return 2\n}\n");
        await client.DiagnosticsAsync(uri, static d => d.GetArrayLength() == 0);

        // Closed without saving: what is on disk is what is wrong.
        await client.NotifyAsync("textDocument/didClose", new { textDocument = new { uri } });
        await client.DiagnosticsAsync(uri, static d => d.GetArrayLength() > 0);

        // A file outside the workspace is forgotten on closing.
        const string loose = "file:///tmp/loose-closed.sb";
        await client.OpenAsync(loose, broken);
        await client.DiagnosticsAsync(loose, static d => d.GetArrayLength() > 0);
        await client.NotifyAsync("textDocument/didClose", new { textDocument = new { uri = loose } });
        await client.DiagnosticsAsync(loose, static d => d.GetArrayLength() == 0);
    }

    [Fact]
    public async Task AClientThatPullsIsAnsweredAndNotPushed() {
        await using var client = new TestClient();
        var capabilities = (await client.InitializeAsync((string?)null, Pulling(false))).Get("capabilities")!.Value;
        var provider = capabilities.Get("diagnosticProvider")!.Value;
        Assert.True(provider.GetBool("interFileDependencies"));
        Assert.True(provider.GetBool("workspaceDiagnostics"));

        const string uri = "file:///tmp/pulled.sb";
        await client.OpenAsync(uri, Moved);

        // Without refresh, a pull waits for the SIL checks.
        var report = await client.RequestAsync("textDocument/diagnostic", new { textDocument = new { uri } });
        Assert.Equal("full", report.GetString("kind"));
        Assert.True(Has(report.Get("items")!.Value, "S3057"), report.ToString());

        var again = await client.RequestAsync("textDocument/diagnostic", new { textDocument = new { uri }, previousResultId = report.GetString("resultId") });
        Assert.Equal("unchanged", again.GetString("kind"));
        Assert.Equal(report.GetString("resultId"), again.GetString("resultId"));

        await Task.Delay(500);
        Assert.Empty(client.Sent("textDocument/publishDiagnostics"));
    }

    [Fact]
    public async Task AClientThatPullsIsToldWhenTheChecksAreIn() {
        var closed = Write("closed.sb", Unused.Replace("module App", "module Other", StringComparison.Ordinal));
        var open = Write("open.sb", Moved);
        await using var client = new TestClient();
        await client.InitializeAsync(_root, Pulling(true));
        await client.NotifyAsync("initialized", new { });
        await client.OpenAsync(open, Moved);

        // The type errors are refreshed first, the checks on SIL after.
        JsonElement report;
        for (var refreshes = 1;; refreshes++) {
            var count = refreshes;
            await client.MessageAsync("workspace/diagnostic/refresh", _ => client.Sent("workspace/diagnostic/refresh").Count >= count);
            report = await client.RequestAsync("textDocument/diagnostic", new { textDocument = new { uri = open } });
            if (Has(report.Get("items")!.Value, "S3057")) {
                break;
            }
        }

        // The files nobody opened are in the workspace's report; the open one is not.
        var workspace = await client.RequestAsync("workspace/diagnostic", new { previousResultIds = Array.Empty<object>() });
        var items = workspace.Get("items")!.Value.EnumerateArray().ToList();
        Assert.DoesNotContain(items, static i => i.GetString("uri")!.EndsWith("open.sb", StringComparison.Ordinal));
        var item = items.Single(i => i.GetString("uri") == closed);
        Assert.Equal("full", item.GetString("kind"));
        Assert.True(item.Get("items")!.Value.GetArrayLength() > 0);

        var unchanged = await client.RequestAsync("workspace/diagnostic", new { previousResultIds = new[] { new { uri = closed, value = item.GetString("resultId") } } });
        Assert.Equal("unchanged", unchanged.Get("items")!.Value.EnumerateArray().Single(i => i.GetString("uri") == closed).GetString("kind"));
    }
}
