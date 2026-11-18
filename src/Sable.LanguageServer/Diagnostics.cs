using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sable.Syntax;

namespace Sable.LanguageServer;

/// <summary>
///     What the server last worked out for one document: the diagnostics
///     as the protocol carries them, and an id that changes when they do.
/// </summary>
/// <param name="Tree">The text they are of.</param>
/// <param name="Complete">Whether they include the lints and the SIL checks, or are the type errors alone.</param>
internal sealed record DiagnosticReport(string Uri, int? Version, bool IsOpen, string ResultId, JsonArray Items, SyntaxTree Tree, bool Complete);

public sealed partial class LanguageServer {
    /// <summary>The documents' latest reports, by <see cref="Workspace.KeyOf" />: what was published, or what a workspace pull answers.</summary>
    readonly ConcurrentDictionary<string, DiagnosticReport> _reports = new(StringComparer.Ordinal);

    /// <summary>The round of diagnostics running now, which a change cancels: what it would publish is out of date.</summary>
    CancellationTokenSource? _round;

    int _requests;

    /// <summary>
    ///     The client asks for diagnostics (<c>textDocument/diagnostic</c>)
    ///     rather than being sent them; the rounds then only keep the
    ///     reports and tell it when they changed.
    /// </summary>
    bool _pull;

    /// <summary>The client takes <c>workspace/diagnostic/refresh</c>: a pull need not wait for the SIL checks.</summary>
    bool _refresh;

    /// <summary>Set when loading the files ends: the round it starts need not wait for the workspace to be quiet.</summary>
    int _loaded;

    /// <summary>The workspace changed: diagnostics are due once it is quiet, and a round under way is out of date.</summary>
    /// <remarks>
    ///     The round under way is cancelled before the next is asked for,
    ///     never after: released first, the loop could wake and start the
    ///     round for this very change — at once, after loading — and the
    ///     cancel would then stop that one, whose release was already taken,
    ///     so no round would follow and the change would go undiagnosed.
    /// </remarks>
    void Changed() {
        try {
            Volatile.Read(ref _round)?.Cancel();
        } catch (ObjectDisposedException) {
            // That round is over.
        }

        _changed.Release();
    }

    /// <summary>
    ///     Recomputes the workspace's diagnostics each time it has been quiet
    ///     for the configured delay, and publishes them — or, to a client
    ///     that pulls, says they changed.
    /// </summary>
    async Task DiagnosticsLoopAsync(CancellationToken cancellation) {
        while (!cancellation.IsCancellationRequested) {
            await _changed.WaitAsync(cancellation);

            // Debounce: wait until nothing has changed for a while — unless
            // the files were just loaded, which is no typing to wait out.
            do {
                while (_changed.CurrentCount > 0) {
                    await _changed.WaitAsync(cancellation);
                }
            } while (Interlocked.Exchange(ref _loaded, 0) == 0 && await _changed.WaitAsync(_options.DiagnosticsDelay, cancellation));

            using var round = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            Volatile.Write(ref _round, round);
            try {
                await _loading;
                await DiagnoseAsync(round.Token);

                // The library's names, for completion, once the first
                // diagnostics are out of the way.
                _ = _library.Ready;
            } catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) {
                // Superseded: the change that cancelled it starts the next round.
            } catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) {
                Log($"diagnostics failed: {e}");
            } finally {
                Interlocked.CompareExchange(ref _round, null, round);
            }
        }
    }

    /// <summary>
    ///     One round, over every document of the workspace — open ones first,
    ///     then those only on disk, so problems in a file nobody opened are
    ///     found too. The type errors go out first; then, per document, the
    ///     lints and what the checks on SIL say — use after move, overlapping
    ///     accesses, dangling references — which need each package lowered,
    ///     slower than binding. Documents are in package order, so each
    ///     package is lowered once.
    /// </summary>
    async Task DiagnoseAsync(CancellationToken cancellation) {
        var snapshot = _workspace.Current;
        var documents = snapshot.Documents.Values
            .Where(d => d.IsOpen || _diagnoseClosed)
            .OrderBy(static d => d.IsOpen ? 0 : 1)
            .ThenBy(snapshot.KeyOf, StringComparer.Ordinal)
            .ThenBy(static d => d.Uri, StringComparer.Ordinal)
            .ToList();

        var diagnosed = new List<(Document Document, Analysis Analysis)>();
        var changed = false;
        foreach (var document in documents) {
            cancellation.ThrowIfCancellationRequested();
            var analysis = await _analyses.GetAsync(snapshot, snapshot.KeyOf(document)).WaitAsync(cancellation);
            if (!IsCurrent(document)) {
                continue;
            }

            diagnosed.Add((document, analysis));

            // A file whose text is as it was keeps its whole report until the
            // whole one is ready: sending its type errors alone would take its
            // warnings away for a moment, each time any file changes.
            if (_reports.TryGetValue(Workspace.KeyOf(document.Uri), out var previous) && previous.Complete && ReferenceEquals(previous.Tree, document.Tree)) {
                continue;
            }

            changed |= await ReportAsync(document, analysis, analysis.DiagnosticsOf(document.Tree, cancellation), false);
            _analyses.Bound(analysis);
        }

        await ForgetRemovedAsync(snapshot);
        if (_pull && _refresh && changed) {
            await RefreshAsync();
        }

        changed = false;
        foreach (var (document, analysis) in diagnosed) {
            cancellation.ThrowIfCancellationRequested();
            var all = await AllDiagnosticsAsync(analysis, document, true, cancellation);
            if (IsCurrent(document)) {
                changed |= await ReportAsync(document, analysis, all, true);
            }
        }

        if (_pull && _refresh && changed) {
            await RefreshAsync();
        }
    }

    /// <summary>Whether a document is still as the workspace has it: one edited since is the next round's.</summary>
    bool IsCurrent(Document document) => ReferenceEquals(_workspace.Current.Find(document.Uri)?.Tree, document.Tree);

    /// <summary>
    ///     Everything wrong with a document: its type errors, then — once it
    ///     has none — its lints, and what the checks on SIL found in its
    ///     package. Those last are waited for, or taken only if they are done.
    /// </summary>
    async Task<IReadOnlyList<Diagnostic>> AllDiagnosticsAsync(Analysis analysis, Document document, bool waitForChecks, CancellationToken cancellation) {
        var found = new List<Diagnostic>(analysis.DiagnosticsOf(document.Tree, cancellation));
        if (_lints) {
            found.AddRange(analysis.LintsOf(document.Tree, cancellation));
        }
        var checks = waitForChecks ? await RootSummariesAsync(analysis).WaitAsync(cancellation) : RootSummariesIfDone(analysis);
        if (checks is not null) {
            found.AddRange(checks.ChecksOf(document.Tree));
        }

        return [..found.Distinct().OrderBy(static d => d.Span.Start)];
    }

    /// <summary>Keeps a document's report; publishes it if it changed and the client is sent diagnostics. True if it changed.</summary>
    async Task<bool> ReportAsync(Document document, Analysis analysis, IReadOnlyList<Diagnostic> diagnostics, bool complete) {
        var report = ReportOf(document, analysis, diagnostics, complete);
        var key = Workspace.KeyOf(document.Uri);
        var same = _reports.TryGetValue(key, out var previous)
                   && previous.ResultId == report.ResultId
                   && previous.Version == report.Version
                   && previous.IsOpen == report.IsOpen;
        _reports[key] = report;
        if (same) {
            return false;
        }

        if (!_pull) {
            await PublishAsync(document.Uri, document.Version, report.Items);
        }

        return true;
    }

    DiagnosticReport ReportOf(Document document, Analysis analysis, IReadOnlyList<Diagnostic> diagnostics, bool complete) {
        var items = new JsonArray([..diagnostics.Select(d => DiagnosticNode(document, analysis, d))]);
        return new(document.Uri, document.Version, document.IsOpen, ResultIdOf(items), items, document.Tree, complete);
    }

    /// <summary>What a report is known by: its content, so a pull of what the client has is answered "unchanged".</summary>
    static string ResultIdOf(JsonArray items) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(items.ToJsonString())), 0, 12);

    /// <summary>
    ///     A document gone from the workspace — closed, deleted, its folder
    ///     removed — has its diagnostics cleared; so has one nobody opened,
    ///     when those are not diagnosed.
    /// </summary>
    async Task ForgetRemovedAsync(WorkspaceSnapshot snapshot) {
        foreach (var (key, report) in _reports) {
            // Still there, and diagnosed: a file nobody opened is not, when
            // the workspace's files are not to be.
            var document = _workspace.Current.Documents.GetValueOrDefault(key) ?? snapshot.Documents.GetValueOrDefault(key);
            if (document is not null && (document.IsOpen || _diagnoseClosed)) {
                continue;
            }

            if (_reports.TryRemove(key, out _) && !_pull) {
                await PublishAsync(report.Uri, null, []);
            }
        }
    }

    JsonNode DiagnosticNode(Document document, Analysis analysis, Diagnostic diagnostic) {
        var node = new JsonObject {
            ["range"] = RangeNode(document.Lines.RangeOf(diagnostic.Span)),
            ["severity"] = diagnostic.Severity == DiagnosticSeverity.Error ? 1 : diagnostic.Severity == DiagnosticSeverity.Warning ? 2 : 3,
            ["code"] = diagnostic.Id,
            ["source"] = "sable",
            ["message"] = diagnostic.Message
        };

        // The long-form page, which 'sable explain' prints, as a link.
        if (_options.ExplainLink(diagnostic.Id) is { } link) {
            node["codeDescription"] = new JsonObject { ["href"] = link };
        }

        // The other places it is about, each in the file it is in.
        var related = new JsonArray();
        foreach (var location in diagnostic.Related) {
            if (FileOf(analysis, document, location.Anchor) is var (uri, lines)) {
                related.Add(new JsonObject {
                    ["location"] = new JsonObject { ["uri"] = uri, ["range"] = RangeNode(lines.RangeOf(location.Span)) },
                    ["message"] = location.Message
                });
            }
        }

        if (related.Count > 0) {
            node["relatedInformation"] = related;
        }

        // What nobody uses is shown faded, as editors show unused code.
        if (diagnostic.Code is ErrorCode.LNT_UnusedLocal or ErrorCode.LNT_UnusedParameter or ErrorCode.LNT_UnusedImport) {
            node["tags"] = new JsonArray(1);
        }

        return node;
    }

    Task PublishAsync(string uri, int? version, JsonArray diagnostics) {
        var parameters = new JsonObject { ["uri"] = uri, ["diagnostics"] = diagnostics.DeepClone() };
        if (version is { } v) {
            parameters["version"] = v;
        }

        return _channel.WriteAsync(new JsonObject {
            ["jsonrpc"] = "2.0",
            ["method"] = "textDocument/publishDiagnostics",
            ["params"] = parameters
        });
    }

    /// <summary>Asks a client that pulls to pull again; its answer is not waited for.</summary>
    Task RefreshAsync() {
        _ = SendRequestAsync("workspace/diagnostic/refresh", null);
        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Pulled diagnostics
    // -----------------------------------------------------------------------

    /// <summary>
    ///     <c>textDocument/diagnostic</c>: a document's diagnostics, as of the
    ///     request. The SIL checks are included once its package is lowered;
    ///     until then the round lowering it refreshes the client when done.
    /// </summary>
    async Task<JsonNode?> DocumentDiagnosticsAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        var uri = parameters.Get("textDocument")?.GetString("uri") ?? "";
        var document = snapshot.Find(uri) ?? throw new LspException(LspException.InvalidParams, $"'{uri}' is not open");
        var analysis = await AnalysisOfAsync(snapshot, document);
        var diagnostics = await AllDiagnosticsAsync(analysis, document, !_refresh, cancellation);
        var report = ReportOf(document, analysis, diagnostics, true);
        return parameters.GetString("previousResultId") == report.ResultId
            ? new JsonObject { ["kind"] = "unchanged", ["resultId"] = report.ResultId }
            : new JsonObject { ["kind"] = "full", ["resultId"] = report.ResultId, ["items"] = report.Items };
    }

    /// <summary>
    ///     <c>workspace/diagnostic</c>: the latest round's reports of the
    ///     documents not open — the client pulls the open ones itself.
    /// </summary>
    async Task<JsonNode?> WorkspaceDiagnosticsAsync(WorkspaceSnapshot snapshot, JsonElement parameters, CancellationToken cancellation) {
        await LoadedAsync(snapshot);
        var previous = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var known in parameters.Get("previousResultIds")?.EnumerateArray() ?? default) {
            if (known.GetString("uri") is { } uri && known.GetString("value") is { } value) {
                previous[Workspace.KeyOf(uri)] = value;
            }
        }

        var items = new JsonArray();
        foreach (var (key, report) in _reports.OrderBy(static r => r.Key, StringComparer.Ordinal)) {
            cancellation.ThrowIfCancellationRequested();
            if (report.IsOpen || _workspace.Current.Documents.GetValueOrDefault(key) is { IsOpen: true }) {
                continue;
            }

            items.Add(previous.GetValueOrDefault(key) == report.ResultId
                ? new JsonObject { ["uri"] = report.Uri, ["version"] = null, ["kind"] = "unchanged", ["resultId"] = report.ResultId }
                : new JsonObject { ["uri"] = report.Uri, ["version"] = null, ["kind"] = "full", ["resultId"] = report.ResultId, ["items"] = report.Items.DeepClone() });
        }

        return new JsonObject { ["items"] = items };
    }
}
