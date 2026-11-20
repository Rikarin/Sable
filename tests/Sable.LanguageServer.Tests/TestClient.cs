using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sable.Driver;

namespace Sable.LanguageServer.Tests;

/// <summary>
///     An editor, as far as the server can tell: it talks to a server running
///     in this process through its message loop, over a pair of in-process
///     pipes, framing and all.
/// </summary>
/// <remarks>
///     The pipes are in memory (System.IO.Pipelines), not the system's
///     anonymous pipes: on Windows those are synchronous, so every read and
///     write — the framing reads a header a byte at a time — held a thread
///     of the pool, and with the test classes running at once the pool was
///     starved until a message the server sent was not read within a test's
///     minute (EachLoadOfThePackagesIsDiagnosed, on the Windows runner).
/// </remarks>
internal sealed class TestClient : IAsyncDisposable {
    /// <summary>
    ///     How long an answer or a message is waited for. A report with the
    ///     lints and the SIL checks waits for the program to be lowered whole,
    ///     Sable.Core's bodies too: 2–3s for one server alone, but each test
    ///     class runs a server of its own at once, and under the suite they
    ///     took up to 33s on a ten-core Mac; on a four-core runner shared with
    ///     the other test projects, over the minute this once was.
    /// </summary>
    static readonly TimeSpan Patience = TimeSpan.FromMinutes(3);

    readonly Pipe _toServerPipe = new();
    readonly Pipe _toClientPipe = new();
    readonly Stream _toServer;
    readonly Stream _serverInput;
    readonly Stream _toClient;
    readonly Stream _clientInput;
    readonly JsonRpcChannel _channel;
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _waiting = new();
    readonly List<JsonElement> _notifications = [];
    readonly SemaphoreSlim _notified = new(0);
    readonly Task _reading;
    int _nextId;

    /// <summary>What the server logged, which a test that times out shows: why the message never came.</summary>
    readonly StringWriter _logged = new();
    readonly TextWriter _log;

    /// <summary>The server's log so far.</summary>
    public string Logged {
        get {
            // The synchronised writer locks itself while it writes.
            lock (_log) {
                return _logged.ToString();
            }
        }
    }

    public Task<int> Running { get; }

    /// <summary>The server, for what only a test asks of it.</summary>
    internal LanguageServer Server { get; }

    /// <param name="projects">How the server finds the packages under its folder; none, by default.</param>
    public TestClient(TimeSpan? diagnosticsDelay = null, Func<string, IReadOnlyList<LanguageProject>>? projects = null) {
        _log = TextWriter.Synchronized(_logged);
        _toServer = _toServerPipe.Writer.AsStream();
        _serverInput = _toServerPipe.Reader.AsStream();
        _toClient = _toClientPipe.Writer.AsStream();
        _clientInput = _toClientPipe.Reader.AsStream();
        var server = Server = new LanguageServer(_serverInput, _toClient, new LanguageServerOptions {
            LibraryFor = Library.For,
            LibraryModules = Library.ModuleNames,
            ProjectsFor = projects ?? (static _ => []),
            DiagnosticsDelay = diagnosticsDelay ?? TimeSpan.FromMilliseconds(30),
            LibraryCacheDirectory = Path.Combine(Path.GetTempPath(), "sable-lsp-tests", Guid.NewGuid().ToString("N")),
            ExplainLink = DiagnosticDocs.Link,
            Log = _log
        });
        Running = Task.Run(() => server.RunAsync());
        _channel = new(_clientInput, _toServer);
        _reading = Task.Run(ReadAsync);
    }

    async Task ReadAsync() {
        while (await _channel.ReadAsync(CancellationToken.None) is { } message) {
            if (message.Get("id") is { ValueKind: JsonValueKind.Number } id && message.GetString("method") is null) {
                if (_waiting.TryRemove(id.GetInt32(), out var waiting)) {
                    waiting.SetResult(message);
                }

                continue;
            }

            lock (_notifications) {
                _notifications.Add(message);
            }

            // A request of the server's — to make a progress token, to watch
            // files, to pull again — is agreed to, as an editor would.
            if (message.Get("id") is { } request && message.GetString("method") is not null) {
                await _channel.WriteAsync(new JsonObject {
                    ["jsonrpc"] = "2.0",
                    ["id"] = JsonNode.Parse(request.GetRawText()),
                    ["result"] = null
                });
            }

            _notified.Release();
        }
    }

    /// <summary>Sends a request; returns the whole response, result or error.</summary>
    public async Task<JsonElement> SendAsync(string method, object? parameters) {
        var id = Interlocked.Increment(ref _nextId);
        var waiting = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = waiting;
        await _channel.WriteAsync(new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = Json.ToNode(parameters)
        });
        return await waiting.Task.WaitAsync(Patience);
    }

    /// <summary>Sends a request and returns its result, failing on an error.</summary>
    public async Task<JsonElement> RequestAsync(string method, object? parameters) {
        var response = await SendAsync(method, parameters);
        if (response.Get("error") is { } error) {
            throw new InvalidOperationException($"{method} failed: {error}");
        }

        return response.TryGetProperty("result", out var result) ? result : default;
    }

    public Task NotifyAsync(string method, object? parameters) {
        var node = Json.ToNode(parameters);
        Track(method, node);
        return _channel.WriteAsync(new JsonObject {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = node
        });
    }

    /// <summary>The version of each document this client last opened or changed; a closed one is gone.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _versions = new(StringComparer.Ordinal);

    void Track(string method, JsonNode? parameters) {
        if (parameters?["textDocument"] is not JsonObject document || document["uri"]?.GetValue<string>() is not { } uri) {
            return;
        }

        if (method == "textDocument/didClose") {
            _versions.TryRemove(uri, out _);
        } else if (method is "textDocument/didOpen" or "textDocument/didChange" && document["version"] is { } version) {
            _versions[uri] = version.GetValue<int>();
        }
    }

    /// <summary>
    ///     Whether a report is of the document as this client last sent it.
    ///     The server reports a version in two rounds, the type errors then
    ///     the lints and checks, and an older version's second round may come
    ///     after an edit: a test waiting for the edit's report must not take
    ///     it. A report without a version is of a file not open.
    /// </summary>
    bool IsCurrent(string uri, JsonElement report) =>
        !_versions.TryGetValue(uri, out var sent) || report.GetInt("version") is not { } version || version >= sent;

    /// <param name="capabilities">What the editor says it can do; nothing, by default.</param>
    public Task<JsonElement> InitializeAsync(string? root = null, object? capabilities = null) =>
        RequestAsync("initialize", new {
            processId = (int?)null,
            rootUri = root is null ? null : Workspace.UriOf(root),
            capabilities = capabilities ?? new { }
        });

    /// <summary>Initialises with several workspace folders.</summary>
    public Task<JsonElement> InitializeAsync(IEnumerable<string> folders, object? capabilities = null) =>
        RequestAsync("initialize", new {
            processId = (int?)null,
            workspaceFolders = folders.Select(static f => new { uri = Workspace.UriOf(f), name = Path.GetFileName(f) }).ToArray(),
            capabilities = capabilities ?? new { }
        });

    public Task OpenAsync(string uri, string text, int version = 1) =>
        NotifyAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = "sable", version, text } });

    /// <summary>Replaces a range, given as offsets into <paramref name="before" />, with new text.</summary>
    public Task ChangeAsync(string uri, int version, string before, int start, int end, string text) {
        var lines = LineIndex.Of(before);
        return NotifyAsync("textDocument/didChange", new {
            textDocument = new { uri, version },
            contentChanges = new[] { new { range = Range(lines, start, end), text } }
        });
    }

    public static object Range(LineIndex lines, int start, int end) {
        var from = lines.PositionOf(start);
        var to = lines.PositionOf(end);
        return new { start = new { line = from.Line, character = from.Character }, end = new { line = to.Line, character = to.Character } };
    }

    public static object Position(string text, int offset) {
        var position = LineIndex.Of(text).PositionOf(offset);
        return new { line = position.Line, character = position.Character };
    }

    /// <summary>A text-document-position request at an offset.</summary>
    public Task<JsonElement> AtAsync(string method, string uri, string text, int offset, object? extra = null) {
        var parameters = new JsonObject {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["position"] = Json.ToNode(Position(text, offset))
        };
        if (extra is not null) {
            foreach (var (key, value) in Json.ToNode(extra)!.AsObject()) {
                parameters[key] = value?.DeepClone();
            }
        }

        return RequestAsync(method, parameters);
    }

    /// <summary>
    ///     How many notifications and requests the server has sent so far: a
    ///     wait from here takes only what comes after. A file not open has
    ///     no version to tell an old report of it from a new one.
    /// </summary>
    public int Received {
        get {
            lock (_notifications) {
                return _notifications.Count;
            }
        }
    }

    /// <summary>Waits for diagnostics for a document that satisfy a condition, and returns them.</summary>
    /// <param name="since">A <see cref="Received" /> count: only reports sent after it are taken.</param>
    public async Task<JsonElement> DiagnosticsAsync(string uri, Func<JsonElement, bool>? until = null, int since = 0) {
        var parameters = await MessageAsync("textDocument/publishDiagnostics",
            p => p.GetString("uri") == uri && IsCurrent(uri, p) && (until is null || until(p.Get("diagnostics")!.Value)), since);
        return parameters.Get("diagnostics")!.Value;
    }

    /// <summary>Waits for a notification or request from the server, of a method and satisfying a condition; returns its parameters.</summary>
    /// <param name="since">A <see cref="Received" /> count: only messages sent after it are taken.</param>
    public async Task<JsonElement> MessageAsync(string method, Func<JsonElement, bool>? until = null, int since = 0) {
        var deadline = DateTime.UtcNow + Patience;
        var seen = since;
        while (DateTime.UtcNow < deadline) {
            List<JsonElement> pending;
            lock (_notifications) {
                pending = _notifications.Skip(seen).ToList();
                seen = _notifications.Count;
            }

            foreach (var notification in pending.Where(n => n.GetString("method") == method)) {
                var parameters = notification.Get("params") ?? default;
                if (until is null || until(parameters)) {
                    return parameters;
                }
            }

            await _notified.WaitAsync(TimeSpan.FromMilliseconds(200));
        }

        throw new TimeoutException($"no {method} as expected; the server logged:\n{Logged}\nthread pool: {ThreadPool.ThreadCount} threads, {ThreadPool.PendingWorkItemCount} items queued");
    }

    /// <summary>Every message of a method the server has sent so far.</summary>
    public IReadOnlyList<JsonElement> Sent(string method) {
        lock (_notifications) {
            return [.._notifications.Where(n => n.GetString("method") == method)];
        }
    }

    /// <summary>Shuts the server down properly, and returns its exit code.</summary>
    public async Task<int> ShutdownAsync() {
        await RequestAsync("shutdown", null);
        await NotifyAsync("exit", null);
        return await Running.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public async ValueTask DisposeAsync() {
        if (!Running.IsCompleted) {
            try {
                await NotifyAsync("exit", null);
                await Running.WaitAsync(TimeSpan.FromSeconds(10));
            } catch (Exception e) when (e is IOException or TimeoutException or ObjectDisposedException) {
                // Already gone.
            }
        }

        await _toServer.DisposeAsync();
        await _toClient.DisposeAsync();
        try {
            await _reading.WaitAsync(TimeSpan.FromSeconds(5));
        } catch (Exception e) when (e is IOException or TimeoutException or ObjectDisposedException) {
            // The pipe closed under it.
        }

        await _serverInput.DisposeAsync();
        await _clientInput.DisposeAsync();
    }
}
