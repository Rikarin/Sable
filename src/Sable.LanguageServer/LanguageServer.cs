using Sable.Symbols;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sable.Syntax;

namespace Sable.LanguageServer;

/// <summary>
///     Whether a program's standard library is known to check cleanly with
///     it — learnt by a build, by an earlier server, or from the library's
///     <c>.sbi</c> in the build cache, and kept in
///     <c>$SABLE_HOME/cache/library-checked</c> (docs/05 §5.4). When it is,
///     the SIL checks neither bind its bodies nor lower more of it than the
///     program reaches, so a small file does not check all of Sable.Core;
///     its declarations are still bound from its source, which
///     go-to-definition and hover read.
/// </summary>
/// <param name="Clean">The library's files, whose bodies are left alone; null when it is not known yet.</param>
/// <param name="Passed">Says the library was checked whole with the program, and nothing was found.</param>
public sealed record LibraryKnowledge(Func<SyntaxTree, bool>? Clean, Action Passed);

/// <summary>What the server is told about the world it runs in.</summary>
public sealed class LanguageServerOptions {
    /// <summary>
    ///     The library trees a program needs, transitively — the driver's
    ///     <c>Library.For</c>. Sable.Core comes with every compilation and is
    ///     not among them.
    /// </summary>
    public Func<SyntaxTree, IReadOnlyList<SyntaxTree>> LibraryFor { get; init; } = static _ => [];

    /// <summary>What the library's own generators write for files that are in no package (docs/08 §8.1).</summary>
    public Func<IReadOnlyList<SyntaxTree>, GeneratedTrees>? GenerateLoose { get; init; }

    /// <summary>
    ///     What is known of a compilation's library files — the standard
    ///     library's, and its dependencies' files read from disk, known by
    ///     their text — given the package's own files (the driver's
    ///     <c>Library.CheckFor</c>); null for a program that could change how
    ///     they bind or lower.
    /// </summary>
    public Func<Symbols.Compilation, IReadOnlyCollection<SyntaxTree>, LibraryKnowledge?> LibraryCheck { get; init; } = static (_, _) => null;

    /// <summary>The library modules there are, offered after <c>import</c>.</summary>
    public IReadOnlyCollection<string> LibraryModules { get; init; } = [];

    /// <summary>
    ///     The packages under a folder, read from their manifests — the
    ///     driver's <c>LanguageProjects.For</c>. Each is compiled on its own,
    ///     with its features and its dependencies; with none, every file is
    ///     one program.
    /// </summary>
    public Func<string, IReadOnlyList<LanguageProject>> ProjectsFor { get; init; } = static _ => [];

    /// <summary>
    ///     Where a diagnostic code's long-form page is, for the editor to link
    ///     to (the driver's <c>DiagnosticDocs.Link</c>); null for a code that
    ///     has none.
    /// </summary>
    public Func<string, string?> ExplainLink { get; init; } = static _ => null;

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

    Task _loading = Task.CompletedTask;
    bool _initialized;
    bool _shutdown;

    public LanguageServer(Stream input, Stream output, LanguageServerOptions? options = null) {
        _channel = new(input, output);
        _options = options ?? new();
        _analyses = new(BuildAnalysis);
        _library = new(_options.LibraryModules, _options.LibraryFor, Log);
    }

    /// <summary>Every library module's public names, for what no file imports yet.</summary>
    readonly LibraryIndex _library;

    /// <summary>How many analyses have been built, for tests of what waits for one.</summary>
    internal int AnalysesBuilt => Volatile.Read(ref _analysesBuilt);

    int _analysesBuilt;

    /// <summary>How many rounds of diagnostics have started, for tests of an idle server.</summary>
    internal int RoundsRun => Volatile.Read(ref _roundsRun);

    int _roundsRun;

    /// <summary>How many lowerings for the SIL checks have been started, for tests of an idle server.</summary>
    internal int LoweringsStarted => Volatile.Read(ref _loweringsStarted);

    int _loweringsStarted;

    /// <summary>Whether a round of diagnostics is under way, or one is due.</summary>
    internal bool Diagnosing => Volatile.Read(ref _round) is not null || _changed.CurrentCount > 0;

    Analysis BuildAnalysis(WorkspaceSnapshot snapshot, string key) {
        Interlocked.Increment(ref _analysesBuilt);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var name = key.Length == 0 ? "the loose files" : key;
        try {
            var analysis = BuildAnalysisNow(snapshot, key);
            _failuresShown.TryRemove($"build:{key}", out _);
            ShowFailures(analysis);
            return analysis;
        } catch (Exception e) when (e is not OutOfMemoryException) {
            Log($"compiling {name} failed: {e}");
            ShowOnce($"build:{key}", e.Message, $"Sable could not compile {name}: {e.Message}");
            throw;
        } finally {
            Log($"compiled {name} in {watch.ElapsedMilliseconds} ms");
        }
    }

    /// <summary>What has been shown the user of each failure, by what failed: each is shown once, until it is fixed.</summary>
    readonly ConcurrentDictionary<string, string> _failuresShown = new(StringComparer.Ordinal);

    /// <summary>
    ///     Says, once, that a library or dependency file the package is
    ///     compiled with does not compile (#284): those files are no
    ///     document's, so no diagnostic would show it, and every file of the
    ///     package may fail because of it. A file that compiles again is
    ///     forgotten, and said again if it breaks again.
    /// </summary>
    void ShowFailures(Analysis analysis) {
        var failing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (tree, error) in analysis.Failures) {
            var file = tree.Text.FileName;
            failing.Add(file);
            var where = tree.Text.GetLinePosition(error.Span.Start);
            var what = analysis.IsLibrary(tree) ? "The standard library" : "A dependency";
            ShowOnce($"file:{file}", $"{error.Id} {error.Message}",
                $"{what} does not compile: {file}:{where}: error {error.Id}: {error.Message}. If this sable is older than the code, rebuild it.");
        }

        foreach (var tree in analysis.Compilation.SyntaxTrees) {
            if (!failing.Contains(tree.Text.FileName)) {
                _failuresShown.TryRemove($"file:{tree.Text.FileName}", out _);
            }
        }
    }

    /// <summary>Shows a warning unless the same one about the same thing was shown last.</summary>
    void ShowOnce(string about, string fingerprint, string message) {
        if (_failuresShown.TryGetValue(about, out var shown) && shown == fingerprint) {
            return;
        }

        _failuresShown[about] = fingerprint;
        Log(message);
        _ = _channel.WriteAsync(new JsonObject {
            ["jsonrpc"] = "2.0",
            ["method"] = "window/showMessage",
            ["params"] = new JsonObject { ["type"] = 2, ["message"] = message }
        });
    }

    Analysis BuildAnalysisNow(WorkspaceSnapshot snapshot, string key) {
        var project = snapshot.Layout.Projects.FirstOrDefault(p => p.Directory == key);
        var dependencies = project is null ? [] : _dependencies.For(project, snapshot, Log);
        dependencies = [..dependencies, ..WorkspaceLibrary(snapshot, key).Except(dependencies)];
        return Analysis.Build(
            snapshot, key, dependencies, _options.LibraryFor, project?.Generate is { } generate ? (trees, others) => Generated(generate, trees, others)
            : project is null && _options.GenerateLoose is { } loose ? (trees, others) => Generated((own, _) => loose(own), trees, others)
            : null, project?.Lints);
    }

    /// <summary>
    ///     The standard library's files, when the workspace holds its package
    ///     (the compiler's repository, whose <c>lib/</c> has a manifest): the
    ///     other packages and the loose files are compiled with those, as they
    ///     are in the editor, in place of the embedded ones.
    /// </summary>
    static IReadOnlyList<SyntaxTree> WorkspaceLibrary(WorkspaceSnapshot snapshot, string key) {
        if (snapshot.Layout.Projects.FirstOrDefault(static p => p.Name == LibraryPackage) is not { } library || library.Directory == key) {
            return [];
        }

        return [
            ..snapshot.Documents.Values
                .Where(d => snapshot.KeyOf(d) == library.Directory && CoreLibrary.IsStandardLibrary(CoreLibrary.ModuleOf(d.Tree)))
                .Select(static d => d.Tree)
                .OrderBy(static t => t.Text.FileName, StringComparer.Ordinal),
        ];
    }

    /// <summary>The name <c>lib/package.sb</c> gives the standard library.</summary>
    const string LibraryPackage = "sable";

    /// <summary>The generators' output; a generator program that fails outright is logged, and leaves the package without it.</summary>
    GeneratedTrees Generated(Func<IReadOnlyList<SyntaxTree>, IReadOnlyList<SyntaxTree>, GeneratedTrees> generate, IReadOnlyList<SyntaxTree> trees, IReadOnlyList<SyntaxTree> others) {
        try {
            return generate(trees, others);
        } catch (Exception e) when (e is not OutOfMemoryException) {
            Log($"the source generators failed: {e.Message}");
            return GeneratedTrees.None;
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
            // A response to a request of ours.
            Answered(id, message);
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
            "completionItem/resolve" => CompletionResolveAsync,
            "textDocument/hover" => HoverAsync,
            "textDocument/definition" or "textDocument/declaration" => DefinitionAsync,
            "textDocument/typeDefinition" => TypeDefinitionAsync,
            "textDocument/implementation" => ImplementationAsync,
            "textDocument/references" => ReferencesAsync,
            "textDocument/documentHighlight" => DocumentHighlightAsync,
            "workspace/symbol" => WorkspaceSymbolsAsync,
            "textDocument/foldingRange" => FoldingRangesAsync,
            "textDocument/selectionRange" => SelectionRangesAsync,
            "textDocument/semanticTokens/full" => SemanticTokensFullAsync,
            "textDocument/semanticTokens/range" => SemanticTokensRangeAsync,
            "textDocument/codeAction" => CodeActionsAsync,
            "textDocument/codeLens" => CodeLensesAsync,
            "codeLens/resolve" => CodeLensResolveAsync,
            "textDocument/documentLink" => DocumentLinksAsync,
            "workspace/executeCommand" => ExecuteCommandAsync,
            "textDocument/prepareCallHierarchy" => PrepareCallHierarchyAsync,
            "callHierarchy/incomingCalls" => IncomingCallsAsync,
            "callHierarchy/outgoingCalls" => OutgoingCallsAsync,
            "textDocument/prepareTypeHierarchy" => PrepareTypeHierarchyAsync,
            "typeHierarchy/supertypes" => SupertypesAsync,
            "typeHierarchy/subtypes" => SubtypesAsync,
            "textDocument/prepareRename" => PrepareRenameAsync,
            "textDocument/rename" => RenameAsync,
            "textDocument/documentSymbol" => DocumentSymbolsAsync,
            "textDocument/signatureHelp" => SignatureHelpAsync,
            "textDocument/inlayHint" => InlayHintsAsync,
            "textDocument/formatting" => FormattingAsync,
            "textDocument/rangeFormatting" => RangeFormattingAsync,
            "textDocument/onTypeFormatting" => OnTypeFormattingAsync,
            "textDocument/diagnostic" => DocumentDiagnosticsAsync,
            "workspace/diagnostic" => WorkspaceDiagnosticsAsync,
            _ => null,
        };

    void HandleNotification(string method, JsonElement parameters) {
        switch (method) {
            case "initialized":
                ClientInitialized();
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
                // A file of the workspace goes back to what is on disk, and
                // keeps being diagnosed; any other is gone, and the next round
                // clears its diagnostics.
                _workspace.Close(uri, _folders);
                Changed();
                return;
            }

            case "workspace/didChangeWatchedFiles": {
                var packages = false;
                var changed = false;
                foreach (var change in parameters.Get("changes")?.EnumerateArray() ?? default) {
                    if (change.GetString("uri") is not { } uri) {
                        continue;
                    }

                    if (uri.EndsWith(".sb", StringComparison.Ordinal)) {
                        changed |= _workspace.Reload(uri, _folders) is not null;
                    }

                    // Only a package's own files count: not a copy in a
                    // hidden folder, a dependency's, or a build's output.
                    packages |= IsPackageFile(uri) && Workspace.PathOf(uri) is { } path && Workspace.IsSource(path, _folders);
                }

                // A manifest or the lock changed: the packages may have too.
                if (packages) {
                    Load([]);
                }

                // A change to nothing the workspace holds — a file in another
                // checkout under a hidden folder, say — starts no round.
                if (changed || packages) {
                    Changed();
                }

                return;
            }

            case "textDocument/didSave": {
                // The editor has the text already; but a client that watches
                // no files says this much about a manifest saved.
                if (parameters.Get("textDocument")?.GetString("uri") is { } uri && IsPackageFile(uri)) {
                    Load([]);
                }

                return;
            }

            case "workspace/didChangeWorkspaceFolders":
                ChangeFolders(parameters);
                return;

            case "workspace/didChangeConfiguration":
                if (parameters.Get("settings")?.Get("sable") is { } settings) {
                    ReadSettings(settings);
                    Changed();
                }

                return;

            default:
                return;
        }
    }

    JsonNode Initialize(JsonElement parameters) {
        _initialized = true;
        ReadClient(parameters);
        _folders = [..FoldersOf(parameters).Distinct(StringComparer.Ordinal)];
        if (_folders.Length > 0) {
            Load(_folders);
        }

        var capabilities = new JsonObject {
            ["positionEncoding"] = "utf-16",
            ["textDocumentSync"] = new JsonObject {
                ["openClose"] = true,
                ["change"] = 2,
                ["save"] = new JsonObject { ["includeText"] = false }
            },
            ["workspace"] = new JsonObject {
                ["workspaceFolders"] = new JsonObject { ["supported"] = true, ["changeNotifications"] = true }
            },
            ["completionProvider"] = new JsonObject {
                ["triggerCharacters"] = new JsonArray("."),
                ["resolveProvider"] = true
            },
            ["hoverProvider"] = true,
            ["definitionProvider"] = true,
            ["declarationProvider"] = true,
            ["typeDefinitionProvider"] = true,
            ["implementationProvider"] = true,
            ["documentHighlightProvider"] = true,
            ["workspaceSymbolProvider"] = true,
            ["foldingRangeProvider"] = true,
            ["selectionRangeProvider"] = true,
            ["semanticTokensProvider"] = new JsonObject {
                ["legend"] = SemanticTokensLegend(),
                ["full"] = true,
                ["range"] = true
            },
            ["codeActionProvider"] = new JsonObject { ["codeActionKinds"] = new JsonArray("quickfix", "source.fixAll") },
            ["codeLensProvider"] = new JsonObject { ["resolveProvider"] = true },
            ["documentLinkProvider"] = new JsonObject { ["resolveProvider"] = false },
            ["executeCommandProvider"] = new JsonObject { ["commands"] = new JsonArray(FixAllCommand) },
            ["callHierarchyProvider"] = true,
            ["typeHierarchyProvider"] = true,
            ["referencesProvider"] = true,
            ["renameProvider"] = new JsonObject { ["prepareProvider"] = true },
            ["documentSymbolProvider"] = true,
            ["signatureHelpProvider"] = new JsonObject {
                ["triggerCharacters"] = new JsonArray("(", ","),
                ["retriggerCharacters"] = new JsonArray(",")
            },
            ["inlayHintProvider"] = true,
            ["documentFormattingProvider"] = true,
            ["documentRangeFormattingProvider"] = true,
            ["documentOnTypeFormattingProvider"] = new JsonObject { ["firstTriggerCharacter"] = "}" }
        };

        // A client that can pull diagnostics is not also sent them: it would
        // show each twice. A change in one file can change another's, and
        // the server lists the files nobody opened.
        if (_pull) {
            capabilities["diagnosticProvider"] = new JsonObject {
                ["identifier"] = "sable",
                ["interFileDependencies"] = true,
                ["workspaceDiagnostics"] = true
            };
        }

        return new JsonObject {
            ["capabilities"] = capabilities,
            ["serverInfo"] = new JsonObject { ["name"] = "sable-lsp", ["version"] = "0.1" }
        };
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
