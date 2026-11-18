using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sable.Syntax;

namespace Sable.LanguageServer;

/// <summary>What the server is told about the world it runs in.</summary>
public sealed class LanguageServerOptions {
    /// <summary>
    ///     The library trees a program needs, transitively — the driver's
    ///     <c>Library.For</c>. Sable.Core comes with every compilation and is
    ///     not among them.
    /// </summary>
    public Func<SyntaxTree, IReadOnlyList<SyntaxTree>> LibraryFor { get; init; } = static _ => [];

    /// <summary>The library modules there are, offered after <c>import</c>.</summary>
    public IReadOnlyCollection<string> LibraryModules { get; init; } = [];

    /// <summary>
    ///     The packages under a folder, read from their manifests — the
    ///     driver's <c>LanguageProjects.For</c>. Each is compiled on its own,
    ///     with its features and its dependencies; with none, every file is
    ///     one program.
    /// </summary>
    public Func<string, IReadOnlyList<LanguageProject>> ProjectsFor { get; init; } = static _ => [];

    /// <summary>How long the workspace must be quiet before diagnostics are recomputed.</summary>
    public TimeSpan DiagnosticsDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Where a library file is written when go-to-definition lands in it.</summary>
    public string LibraryCacheDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "sable-lsp", "lib");

    /// <summary>Where the server says what went wrong inside it; the protocol owns standard output.</summary>
    public TextWriter? Log { get; init; }
}

/// <summary>
///     The Sable language server (docs/04 Phase 5, docs/05 §5.1:
///     <c>sable lsp</c>): the Language Server Protocol over a pair of
///     streams, answered from <see cref="Symbols.SemanticModel" /> directly.
/// </summary>
/// <remarks>
///     <para>
///         <b>Concurrency.</b> One loop reads messages in order. Document
///         changes are applied there, so they are serialised and every later
///         request sees them; each request then runs on the thread pool
///         against the workspace snapshot current when it arrived, and can be
///         cancelled by <c>$/cancelRequest</c>. A compilation is not
///         thread-safe, so whatever binds holds its analysis's gate, one
///         member at a time.
///     </para>
///     <para>
///         <b>Latency.</b> A compilation over the workspace is built when
///         something needs one and the workspace has been quiet for
///         <see cref="LanguageServerOptions.DiagnosticsDelay" />; with its
///         signatures bound, it stays the base for typing until the next.
///         A completion or a signature help does not wait for it: it binds the
///         one member being edited, as it is now, against the newest
///         compilation there is (a speculative member model).
///     </para>
/// </remarks>
public sealed partial class LanguageServer {
    readonly JsonRpcChannel _channel;
    readonly LanguageServerOptions _options;
    readonly Workspace _workspace = new();
    readonly AnalysisCache _analyses;
    readonly DependencyTrees _dependencies = new();
    readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.Ordinal);
    readonly CancellationTokenSource _stopping = new();
    readonly SemaphoreSlim _changed = new(0);
    readonly ConcurrentDictionary<string, bool> _published = new(StringComparer.Ordinal);

    Task _loading = Task.CompletedTask;
    string? _root;
    bool _initialized;
    bool _shutdown;

    public LanguageServer(Stream input, Stream output, LanguageServerOptions? options = null) {
        _channel = new(input, output);
        _options = options ?? new();
        _analyses = new(BuildAnalysis);
    }

    Analysis BuildAnalysis(WorkspaceSnapshot snapshot, string key) {
        var project = snapshot.Layout.Projects.FirstOrDefault(p => p.Directory == key);
        var dependencies = project is null ? [] : _dependencies.For(project, snapshot, Log);
        return Analysis.Build(snapshot, key, dependencies, _options.LibraryFor);
    }

    /// <summary>
    ///     The packages under the folder, as the driver reads them. A failure
    ///     is logged, and leaves every file loose.
    /// </summary>
    ProjectLayout ReadProjects(string folder) {
        try {
            var projects = _options.ProjectsFor(folder);
            foreach (var project in projects) {
                Log($"package {project.Name} in {project.Directory}: features [{string.Join(", ", project.Features)}], {project.Dependencies.Count} dependency file(s)");
            }

            return new(projects);
        } catch (Exception e) when (e is not OutOfMemoryException) {
            Log($"cannot read the packages under {folder}: {e.Message}");
            return ProjectLayout.None;
        }
    }

    /// <summary>A file whose change can change the packages: a manifest, or the lock file.</summary>
    static bool IsPackageFile(string uri) =>
        Path.GetFileName(Workspace.PathOf(uri) ?? uri) is "package.sb" or "workspace.sb" or "sable.toml" or "sable.lock";

    /// <summary>
    ///     Serves until <c>exit</c> or the end of the input; returns the exit
    ///     code the protocol asks for: 0 after <c>shutdown</c>, 1 otherwise.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellation = default) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stopping.Token);
        var diagnostics = Task.Run(() => DiagnosticsLoopAsync(_stopping.Token), CancellationToken.None);
        try {
            while (!linked.IsCancellationRequested) {
                JsonElement? message;
                try {
                    message = await _channel.ReadAsync(linked.Token);
                } catch (OperationCanceledException) {
                    break;
                } catch (JsonException e) {
                    Log($"unreadable message: {e.Message}");
                    await SendErrorAsync(null, LspException.ParseError, e.Message);
                    continue;
                }

                if (message is not { } m) {
                    break;
                }

                if (!await HandleAsync(m)) {
                    break;
                }
            }
        } finally {
            await _stopping.CancelAsync();
            try {
                await diagnostics;
            } catch (OperationCanceledException) {
                // Stopping.
            }
        }

        return _shutdown ? 0 : 1;
    }

    /// <summary>Handles one message; false once it was <c>exit</c>.</summary>
    async Task<bool> HandleAsync(JsonElement message) {
        var method = message.GetString("method");
        var id = message.Get("id");
        var parameters = message.Get("params") ?? default;

        if (method is null) {
            // A response to a request of ours; the server sends none it waits on.
            return true;
        }

        if (id is not { } requestId) {
            if (method == "exit") {
                return false;
            }

            try {
                HandleNotification(method, parameters);
            } catch (Exception e) when (e is not OutOfMemoryException) {
                Log($"{method} failed: {e}");
            }

            return true;
        }

        switch (method) {
            case "initialize":
                await RespondAsync(requestId, Initialize(parameters));
                return true;

            case "shutdown":
                _shutdown = true;
                await RespondAsync(requestId, null);
                return true;
        }

        if (!_initialized) {
            await SendErrorAsync(requestId, LspException.ServerNotInitialized, "the server has not been initialised");
            return true;
        }

        if (_shutdown) {
            await SendErrorAsync(requestId, LspException.InvalidRequest, "the server is shutting down");
            return true;
        }

        if (Handler(method) is not { } handler) {
            await SendErrorAsync(requestId, LspException.MethodNotFound, $"no method '{method}'");
            return true;
        }

        // The snapshot is taken here, in arrival order: a request sees every
        // change sent before it and none sent after.
        var snapshot = _workspace.Current;
        var key = requestId.ToString();
        var source = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        _pending[key] = source;
        _ = Task.Run(async () => {
            try {
                var result = await handler(snapshot, parameters, source.Token);
                source.Token.ThrowIfCancellationRequested();
                await RespondAsync(requestId, result);
            } catch (OperationCanceledException) {
                await SendErrorAsync(requestId, LspException.RequestCancelled, "cancelled");
            } catch (LspException e) {
                await SendErrorAsync(requestId, e.Code, e.Message);
            } catch (Exception e) when (e is not OutOfMemoryException) {
                Log($"{method} failed: {e}");
                await SendErrorAsync(requestId, LspException.InternalError, e.Message);
            } finally {
                _pending.TryRemove(key, out _);
                source.Dispose();
            }
        }, CancellationToken.None);

        return true;
    }

    delegate Task<JsonNode?> RequestHandler(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation);

    RequestHandler? Handler(string method) =>
        method switch {
            "textDocument/completion" => CompletionAsync,
            "textDocument/hover" => HoverAsync,
            "textDocument/definition" => DefinitionAsync,
            "textDocument/references" => ReferencesAsync,
            "textDocument/prepareRename" => PrepareRenameAsync,
            "textDocument/rename" => RenameAsync,
            "textDocument/documentSymbol" => DocumentSymbolsAsync,
            "textDocument/signatureHelp" => SignatureHelpAsync,
            "textDocument/inlayHint" => InlayHintsAsync,
            "textDocument/formatting" => FormattingAsync,
            _ => null,
        };

    void HandleNotification(string method, JsonElement parameters) {
        switch (method) {
            case "initialized":
                return;

            case "$/cancelRequest":
                if (parameters.Get("id") is { } id && _pending.TryGetValue(id.ToString(), out var source)) {
                    try {
                        source.Cancel();
                    } catch (ObjectDisposedException) {
                        // It finished meanwhile.
                    }
                }

                return;

            case "textDocument/didOpen": {
                var document = parameters.Get("textDocument")!.Value;
                _workspace.Open(document.GetString("uri")!, document.GetString("text") ?? "", document.GetInt("version"));
                Changed();
                return;
            }

            case "textDocument/didChange": {
                var document = parameters.Get("textDocument")!.Value;
                var changes = new List<(LspRange?, string)>();
                foreach (var change in parameters.Get("contentChanges")!.Value.EnumerateArray()) {
                    changes.Add((change.Get("range") is { } range ? ReadRange(range) : null, change.GetString("text") ?? ""));
                }

                _workspace.Change(document.GetString("uri")!, document.GetInt("version"), changes);
                Changed();
                return;
            }

            case "textDocument/didClose": {
                var uri = parameters.Get("textDocument")!.Value.GetString("uri")!;
                _workspace.Close(uri, _root);
                if (_published.TryRemove(uri, out _)) {
                    _ = PublishAsync(uri, null, []);
                }

                Changed();
                return;
            }

            case "workspace/didChangeWatchedFiles": {
                var packages = false;
                foreach (var change in parameters.Get("changes")?.EnumerateArray() ?? default) {
                    if (change.GetString("uri") is not { } uri) {
                        continue;
                    }

                    if (uri.EndsWith(".sb", StringComparison.Ordinal)) {
                        _workspace.Reload(uri);
                    }

                    packages |= IsPackageFile(uri);
                }

                // A manifest or the lock changed: the packages may have too.
                if (packages && _root is { } folder && Directory.Exists(folder)) {
                    var previous = _loading;
                    _loading = Task.Run(async () => {
                        await previous;
                        _workspace.SetLayout(ReadProjects(folder));
                        Changed();
                    });
                }

                Changed();
                return;
            }

            default:
                return;
        }
    }

    /// <summary>The workspace changed: diagnostics are due once it is quiet.</summary>
    void Changed() => _changed.Release();

    JsonNode Initialize(JsonElement parameters) {
        _initialized = true;

        var root = parameters.Get("workspaceFolders") is { ValueKind: JsonValueKind.Array } folders && folders.GetArrayLength() > 0
            ? folders[0].GetString("uri")
            : parameters.GetString("rootUri");
        _root = root is null ? parameters.GetString("rootPath") : Workspace.PathOf(root);

        if (_root is { } folder && Directory.Exists(folder)) {
            _loading = Task.Run(() => {
                // The packages first: each file is parsed with its package's features.
                _workspace.SetLayout(ReadProjects(folder));
                var files = Workspace.SourceFilesUnder(folder)
                    .AsParallel()
                    .Select(path => (path, _workspace.Parse(path, File.ReadAllText(path))))
                    .ToList();
                _workspace.AddFromDisk(files);
                Changed();
            });
        }

        return new JsonObject {
            ["capabilities"] = new JsonObject {
                ["positionEncoding"] = "utf-16",
                ["textDocumentSync"] = new JsonObject {
                    ["openClose"] = true,
                    ["change"] = 2
                },
                ["completionProvider"] = new JsonObject {
                    ["triggerCharacters"] = new JsonArray("."),
                    ["resolveProvider"] = false
                },
                ["hoverProvider"] = true,
                ["definitionProvider"] = true,
                ["referencesProvider"] = true,
                ["renameProvider"] = new JsonObject { ["prepareProvider"] = true },
                ["documentSymbolProvider"] = true,
                ["signatureHelpProvider"] = new JsonObject {
                    ["triggerCharacters"] = new JsonArray("(", ","),
                    ["retriggerCharacters"] = new JsonArray(",")
                },
                ["inlayHintProvider"] = true,
                ["documentFormattingProvider"] = true
            },
            ["serverInfo"] = new JsonObject { ["name"] = "sable-lsp", ["version"] = "0.1" }
        };
    }

    // -----------------------------------------------------------------------
    // Diagnostics
    // -----------------------------------------------------------------------

    /// <summary>
    ///     Recomputes and publishes the open documents' diagnostics each time
    ///     the workspace has been quiet for the configured delay.
    /// </summary>
    async Task DiagnosticsLoopAsync(CancellationToken cancellation) {
        while (!cancellation.IsCancellationRequested) {
            await _changed.WaitAsync(cancellation);

            // Debounce: wait until nothing has changed for a while.
            do {
                while (_changed.CurrentCount > 0) {
                    await _changed.WaitAsync(cancellation);
                }
            } while (await _changed.WaitAsync(_options.DiagnosticsDelay, cancellation));

            try {
                await _loading;
                await PublishDiagnosticsAsync(cancellation);
            } catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) {
                // Superseded.
            } catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) {
                Log($"diagnostics failed: {e}");
            }
        }
    }

    async Task PublishDiagnosticsAsync(CancellationToken cancellation) {
        var snapshot = _workspace.Current;
        var published = new List<(Document Document, Analysis Analysis)>();
        foreach (var document in snapshot.Documents.Values.Where(static d => d.IsOpen).OrderBy(snapshot.KeyOf, StringComparer.Ordinal)) {
            cancellation.ThrowIfCancellationRequested();
            var analysis = await _analyses.GetAsync(snapshot, snapshot.KeyOf(document));

            // A document edited since is the next round's.
            if (!ReferenceEquals(_workspace.Current.Find(document.Uri)?.Tree, document.Tree)) {
                continue;
            }

            var diagnostics = analysis.DiagnosticsOf(document.Tree, cancellation);
            _published[document.Uri] = true;
            published.Add((document, analysis));
            await PublishAsync(document.Uri, document.Version, [..diagnostics.Select(d => DiagnosticNode(document, d))]);
        }

        // Use after move, overlapping accesses and dangling references are
        // found on SIL, by lowering each package's compilation — slower than
        // binding, so the type errors above go out first, and these follow,
        // with them. Documents are in package order, so each is lowered once.
        foreach (var (document, analysis) in published) {
            cancellation.ThrowIfCancellationRequested();
            var checks = await RootSummariesAsync(analysis);
            var found = checks.ChecksOf(document.Tree);
            if (found.Count == 0 || !ReferenceEquals(_workspace.Current.Find(document.Uri)?.Tree, document.Tree)) {
                continue;
            }

            var diagnostics = analysis.DiagnosticsOf(document.Tree, cancellation).Concat(found).OrderBy(static d => d.Span.Start);
            await PublishAsync(document.Uri, document.Version, [..diagnostics.Select(d => DiagnosticNode(document, d))]);
        }
    }

    static JsonNode DiagnosticNode(Document document, Diagnostic diagnostic) =>
        new JsonObject {
            ["range"] = RangeNode(document.Lines.RangeOf(diagnostic.Span)),
            ["severity"] = diagnostic.Severity == DiagnosticSeverity.Error ? 1 : diagnostic.Severity == DiagnosticSeverity.Warning ? 2 : 3,
            ["code"] = diagnostic.Id,
            ["source"] = "sable",
            ["message"] = diagnostic.Message
        };

    Task PublishAsync(string uri, int? version, JsonNode?[] diagnostics) {
        var parameters = new JsonObject { ["uri"] = uri, ["diagnostics"] = new JsonArray(diagnostics) };
        if (version is { } v) {
            parameters["version"] = v;
        }

        return _channel.WriteAsync(new JsonObject {
            ["jsonrpc"] = "2.0",
            ["method"] = "textDocument/publishDiagnostics",
            ["params"] = parameters
        });
    }

    // -----------------------------------------------------------------------
    // Messages
    // -----------------------------------------------------------------------

    Task RespondAsync(JsonElement id, JsonNode? result) =>
        _channel.WriteAsync(new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["result"] = result
        });

    Task SendErrorAsync(JsonElement? id, int code, string message) =>
        _channel.WriteAsync(new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"] = id is { } known ? JsonNode.Parse(known.GetRawText()) : null,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        });

    void Log(string message) => _options.Log?.WriteLine($"sable-lsp: {message}");

    // -----------------------------------------------------------------------
    // Positions
    // -----------------------------------------------------------------------

    static LspPosition ReadPosition(JsonElement position) =>
        new(position.GetInt("line") ?? 0, position.GetInt("character") ?? 0);

    static LspRange ReadRange(JsonElement range) =>
        new(ReadPosition(range.Get("start")!.Value), ReadPosition(range.Get("end")!.Value));

    static JsonNode PositionNode(LspPosition position) =>
        new JsonObject { ["line"] = position.Line, ["character"] = position.Character };

    static JsonNode RangeNode(LspRange range) =>
        new JsonObject { ["start"] = PositionNode(range.Start), ["end"] = PositionNode(range.End) };

    /// <summary>The document and offset a text-document-position request is about.</summary>
    static (Document Document, int Offset) Locate(WorkspaceSnapshot snapshot, JsonElement parameters) {
        var uri = parameters.Get("textDocument")?.GetString("uri")
                  ?? throw new LspException(LspException.InvalidParams, "no textDocument");
        var document = snapshot.Find(uri) ?? throw new LspException(LspException.InvalidParams, $"'{uri}' is not open");
        var position = parameters.Get("position") is { } p ? ReadPosition(p) : default;
        return (document, document.Lines.OffsetOf(position));
    }
}
