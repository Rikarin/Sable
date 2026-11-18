using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sable.LanguageServer;

/// <summary>
///     The workspace folders: every one the editor has open, read from disk
///     with their packages, kept as the editor adds and removes them; and
///     the client's side of it — its settings, the files it watches, the
///     progress it shows.
/// </summary>
public sealed partial class LanguageServer {
    /// <summary>The folders, as full paths; changed only by the message loop.</summary>
    ImmutableArray<string> _folders = [];

    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _answers = new(StringComparer.Ordinal);
    readonly TaskCompletionSource _clientReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The client shows progress the server reports (<c>window.workDoneProgress</c>).</summary>
    bool _progress;

    /// <summary>The client lets the server say which files it wants to hear about.</summary>
    bool _registerWatchers;

    /// <summary>
    ///     Whether the files nobody opened are diagnosed: the setting
    ///     <c>sable.diagnostics.workspace</c>, on unless turned off — for a
    ///     workspace too large to bind whole after each edit.
    /// </summary>
    volatile bool _diagnoseClosed = true;

    /// <summary>The folders the client opened with: every workspace folder, or else the root.</summary>
    static IEnumerable<string> FoldersOf(JsonElement parameters) {
        if (parameters.Get("workspaceFolders") is { ValueKind: JsonValueKind.Array } folders && folders.GetArrayLength() > 0) {
            foreach (var folder in folders.EnumerateArray()) {
                if (folder.GetString("uri") is { } uri && Workspace.PathOf(uri) is { } path) {
                    yield return Path.GetFullPath(path);
                }
            }

            yield break;
        }

        var root = parameters.GetString("rootUri") is { } rootUri ? Workspace.PathOf(rootUri) : parameters.GetString("rootPath");
        if (root is not null) {
            yield return Path.GetFullPath(root);
        }
    }

    /// <summary>Reads what the client can do and is set to, from <c>initialize</c>.</summary>
    void ReadClient(JsonElement parameters) {
        var client = parameters.Get("capabilities");
        _pull = client?.Get("textDocument")?.Get("diagnostic") is { ValueKind: JsonValueKind.Object };
        _refresh = client?.Get("workspace")?.Get("diagnostics")?.GetBool("refreshSupport") ?? false;
        _progress = client?.Get("window")?.GetBool("workDoneProgress") ?? false;
        _registerWatchers = client?.Get("workspace")?.Get("didChangeWatchedFiles")?.GetBool("dynamicRegistration") ?? false;
        if (parameters.Get("initializationOptions") is { } options) {
            ReadSettings(options);
        }
    }

    /// <summary>
    ///     The server's settings, as <c>initializationOptions</c> or the
    ///     <c>sable</c> section of <c>workspace/didChangeConfiguration</c>
    ///     give them; a setting not given keeps its value.
    /// </summary>
    void ReadSettings(JsonElement settings) {
        if (settings.Get("diagnostics")?.Get("workspace") is { ValueKind: JsonValueKind.True or JsonValueKind.False } workspace) {
            _diagnoseClosed = workspace.ValueKind == JsonValueKind.True;
        }
    }

    /// <summary>
    ///     <c>initialized</c>: the client is ready for the server's requests.
    ///     It is asked to watch the files that change what is compiled, when
    ///     it lets the server say so — a client need not watch anything of
    ///     its own accord.
    /// </summary>
    void ClientInitialized() {
        _clientReady.TrySetResult();
        if (_registerWatchers) {
            _ = SendRequestAsync("client/registerCapability", new JsonObject {
                ["registrations"] = new JsonArray(new JsonObject {
                    ["id"] = "sable-watched-files",
                    ["method"] = "workspace/didChangeWatchedFiles",
                    ["registerOptions"] = new JsonObject {
                        ["watchers"] = new JsonArray(
                            new JsonObject { ["globPattern"] = "**/*.sb" },
                            new JsonObject { ["globPattern"] = "**/sable.toml" },
                            new JsonObject { ["globPattern"] = "**/sable.lock" })
                    }
                })
            });
        }
    }

    /// <summary>
    ///     The packages under every folder, as the driver reads them. A
    ///     folder inside another is read with it, so a package is counted
    ///     once. A failure is logged, and leaves that folder's files loose.
    /// </summary>
    ProjectLayout ReadProjects(IReadOnlyList<string> folders) {
        var projects = new List<LanguageProject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in folders.Where(Directory.Exists)) {
            try {
                foreach (var project in _options.ProjectsFor(folder)) {
                    if (!seen.Add(Path.GetFullPath(project.Directory))) {
                        continue;
                    }

                    Log($"package {project.Name} in {project.Directory}: features [{string.Join(", ", project.Features)}], {project.Dependencies.Count} dependency file(s)");
                    projects.Add(project);
                }
            } catch (Exception e) when (e is not OutOfMemoryException) {
                Log($"cannot read the packages under {folder}: {e.Message}");
            }
        }

        return projects.Count == 0 ? ProjectLayout.None : new(projects);
    }

    /// <summary>
    ///     Reads the packages of every folder again, then the source files
    ///     of <paramref name="added" />, after whatever loading is under way:
    ///     the packages first, since each file is parsed with its package's
    ///     features. Reported as progress where the client shows it.
    /// </summary>
    void Load(IReadOnlyList<string> added) {
        var previous = _loading;
        var folders = _folders;
        _loading = Task.Run(async () => {
            await previous;
            var work = Task.Run(() => {
                _workspace.SetLayout(ReadProjects(folders));
                var files = added
                    .Where(Directory.Exists)
                    .SelectMany(Workspace.SourceFilesUnder)
                    .Select(Path.GetFullPath)
                    .Distinct(StringComparer.Ordinal)
                    .AsParallel()
                    .Select(path => (path, _workspace.Parse(path, File.ReadAllText(path))))
                    .ToList();
                _workspace.AddFromDisk(files);
                return files.Count;
            });

            if (_progress) {
                _ = ReportProgressAsync(added.Count == 0 ? "Reading the packages" : "Loading the workspace", work);
            }

            try {
                var count = await work;
                Log($"loaded {count} file(s) from {string.Join(", ", added)}");
            } catch (Exception e) when (e is not OutOfMemoryException) {
                Log($"loading failed: {e}");
            }

            Changed();
        });
    }

    /// <summary>
    ///     <c>workspace/didChangeWorkspaceFolders</c>: the files of a folder
    ///     removed are forgotten — unless open, or in a folder still there —
    ///     and those of a folder added are read; the packages of all of them
    ///     are read again.
    /// </summary>
    void ChangeFolders(JsonElement parameters) {
        var change = parameters.Get("event");
        var removed = PathsOf(change?.Get("removed")).ToList();
        var added = PathsOf(change?.Get("added")).Where(f => !_folders.Contains(f)).ToList();
        _folders = [.._folders.Where(f => !removed.Contains(f)), ..added];
        foreach (var folder in removed) {
            _workspace.Forget(folder, _folders);
        }

        Load(added);
        Changed();

        static IEnumerable<string> PathsOf(JsonElement? folders) {
            foreach (var folder in folders?.EnumerateArray() ?? default) {
                if (folder.GetString("uri") is { } uri && Workspace.PathOf(uri) is { } path) {
                    yield return Path.GetFullPath(path);
                }
            }
        }
    }

    /// <summary>
    ///     Work done progress for <paramref name="work" />, once the client is
    ///     ready for requests and has made the token; nothing, if the work is
    ///     over first. The work never waits for this.
    /// </summary>
    async Task ReportProgressAsync(string title, Task<int> work) {
        try {
            if (await Task.WhenAny(_clientReady.Task, work) == work) {
                return;
            }

            var token = $"sable-progress-{Interlocked.Increment(ref _requests)}";
            var created = await SendRequestAsync("window/workDoneProgress/create", new JsonObject { ["token"] = token })
                .WaitAsync(TimeSpan.FromSeconds(10), _stopping.Token);
            if (created.Get("error") is not null) {
                return;
            }

            await NotifyAsync("$/progress", new JsonObject {
                ["token"] = token,
                ["value"] = new JsonObject { ["kind"] = "begin", ["title"] = title, ["cancellable"] = false }
            });
            string message;
            try {
                message = $"{await work} file(s)";
            } catch (Exception e) when (e is not OutOfMemoryException) {
                message = "failed";
            }

            await NotifyAsync("$/progress", new JsonObject {
                ["token"] = token,
                ["value"] = new JsonObject { ["kind"] = "end", ["message"] = message }
            });
        } catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException) {
            // No progress shown; the loading goes on regardless.
        }
    }

    /// <summary>Sends a request to the client; the task completes with its response, error or result.</summary>
    Task<JsonElement> SendRequestAsync(string method, JsonNode? parameters) {
        var id = $"sable-{Interlocked.Increment(ref _requests)}";
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _answers[id] = answer;
        _ = _channel.WriteAsync(new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters
        });
        return answer.Task;
    }

    /// <summary>A response from the client, to a request of ours.</summary>
    void Answered(JsonElement? id, JsonElement message) {
        if (id is { ValueKind: JsonValueKind.String } known && _answers.TryRemove(known.GetString()!, out var answer)) {
            answer.TrySetResult(message);
        }
    }

    Task NotifyAsync(string method, JsonNode? parameters) =>
        _channel.WriteAsync(new JsonObject {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters
        });
}
