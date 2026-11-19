using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.LanguageServer;

/// <summary>A file the server knows: open in the editor, or read from the workspace folder.</summary>
/// <param name="Uri">The URI the client calls it by, which is what every reply uses.</param>
/// <param name="Version">The editor's version, for an open document.</param>
internal sealed record Document(string Uri, SyntaxTree Tree, int? Version, bool IsOpen) {
    public LineIndex Lines => LineIndex.Of(Tree.Text);
}

/// <summary>
///     The workspace at one moment: every document, immutable. A request
///     works on the snapshot current when it arrived, whatever arrives after.
/// </summary>
internal sealed class WorkspaceSnapshot(ImmutableDictionary<string, Document> documents, long generation, ProjectLayout layout) {
    public static readonly WorkspaceSnapshot Empty = new(ImmutableDictionary.Create<string, Document>(StringComparer.Ordinal), 0, ProjectLayout.None);

    /// <summary>The packages the documents are in, which each was parsed for.</summary>
    public ProjectLayout Layout { get; } = layout;

    /// <summary>Which compilation a document is in (<see cref="ProjectLayout.KeyOf" />).</summary>
    public string KeyOf(Document document) => Layout.KeyOf(Workspace.PathOf(document.Uri));

    /// <summary>Documents by key (see <see cref="Workspace.KeyOf" />).</summary>
    public ImmutableDictionary<string, Document> Documents { get; } = documents;

    /// <summary>Counts changes: a later snapshot has a larger one.</summary>
    public long Generation { get; } = generation;

    public Document? Find(string uri) => Documents.GetValueOrDefault(Workspace.KeyOf(uri));
}

/// <summary>
///     The documents the server knows, changed only by the message loop —
///     one change at a time, in the order the client sent them.
/// </summary>
internal sealed class Workspace {
    WorkspaceSnapshot _current = WorkspaceSnapshot.Empty;
    readonly object _updating = new();

    public WorkspaceSnapshot Current => Volatile.Read(ref _current);

    /// <summary>
    ///     What a URI is known by: a file URI by its full path, so that two
    ///     spellings of one file (an escaped colon, say) are one document.
    /// </summary>
    public static string KeyOf(string uri) {
        if (System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile) {
            return Path.GetFullPath(parsed.LocalPath);
        }

        return uri;
    }

    public static string UriOf(string path) => new System.Uri(Path.GetFullPath(path)).AbsoluteUri;

    /// <summary>The file a URI names, when it names one.</summary>
    public static string? PathOf(string uri) =>
        System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : null;

    /// <remarks>
    ///     The message loop and the task loading the folder both change the
    ///     workspace, so a change is made under a lock: neither loses the
    ///     other's.
    /// </remarks>
    WorkspaceSnapshot Update(Func<WorkspaceSnapshot, ImmutableDictionary<string, Document>> change, ProjectLayout? layout = null) {
        lock (_updating) {
            var current = Current;
            var next = new WorkspaceSnapshot(change(current), current.Generation + 1, layout ?? current.Layout);
            Volatile.Write(ref _current, next);
            return next;
        }
    }

    /// <summary>A file's text parsed as its package builds it: with its features.</summary>
    public SyntaxTree Parse(string path, string text) => SyntaxTree.Parse(new SourceText(text, path), Current.Layout.OptionsFor(path));

    /// <summary>
    ///     The packages are now these: every document is parsed again whose
    ///     package's features differ from what it was parsed with.
    /// </summary>
    public WorkspaceSnapshot SetLayout(ProjectLayout layout) =>
        Update(current => {
            var builder = current.Documents.ToBuilder();
            foreach (var (key, document) in current.Documents) {
                var options = layout.OptionsFor(PathOf(document.Uri));
                if (!ProjectLayout.SameFeatures(options, document.Tree.Options)) {
                    builder[key] = document with { Tree = SyntaxTree.Parse(new SourceText(document.Tree.Text.Text, document.Tree.Text.FileName), options) };
                }
            }

            return builder.ToImmutable();
        }, layout);

    /// <summary>Adds files read from disk, leaving any that are open as the editor has them.</summary>
    public WorkspaceSnapshot AddFromDisk(IEnumerable<(string Path, SyntaxTree Tree)> files) =>
        Update(current => {
            var builder = current.Documents.ToBuilder();
            foreach (var (path, tree) in files) {
                var key = KeyOf(UriOf(path));
                if (builder.TryGetValue(key, out var existing) && existing.IsOpen) {
                    continue;
                }

                // Parsed for an older layout, while the packages were read again.
                var parsed = ProjectLayout.SameFeatures(tree.Options, current.Layout.OptionsFor(path))
                    ? tree
                    : SyntaxTree.Parse(new SourceText(tree.Text.Text, path), current.Layout.OptionsFor(path));
                builder[key] = new(UriOf(path), parsed, null, false);
            }

            return builder.ToImmutable();
        });

    public WorkspaceSnapshot Open(string uri, string text, int? version) =>
        Update(current => {
            var tree = SyntaxTree.Parse(new SourceText(text, PathOf(uri) ?? uri), current.Layout.OptionsFor(PathOf(uri)));
            return current.Documents.SetItem(KeyOf(uri), new(uri, tree, version, true));
        });

    /// <summary>
    ///     Applies the editor's edits in order, each by the incremental
    ///     parser: tokens and whole members outside the edit are reused.
    /// </summary>
    public WorkspaceSnapshot Change(string uri, int? version, IReadOnlyList<(LspRange? Range, string Text)> changes) {
        var key = KeyOf(uri);
        return Update(current => {
            // Under the lock: a document parsed again for new packages
            // meanwhile is the one the edit applies to.
            if (!current.Documents.TryGetValue(key, out var document)) {
                throw new LspException(LspException.InvalidParams, $"'{uri}' is not open");
            }

            var tree = document.Tree;
            foreach (var (range, text) in changes) {
                if (range is not { } r) {
                    tree = SyntaxTree.Parse(new SourceText(text, tree.Text.FileName), tree.Options);
                    continue;
                }

                var lines = LineIndex.Of(tree.Text);
                var start = lines.OffsetOf(r.Start);
                var end = Math.Max(start, lines.OffsetOf(r.End));
                tree = tree.WithChange(new TextChange(new TextSpan(start, end - start), text));
            }

            return current.Documents.SetItem(key, document with { Tree = tree, Version = version ?? document.Version });
        });
    }

    /// <summary>
    ///     Closes a document: it goes back to what is on disk, if it is a
    ///     file of the workspace, and away otherwise.
    /// </summary>
    public WorkspaceSnapshot Close(string uri, IReadOnlyList<string> folders) {
        var key = KeyOf(uri);
        var path = PathOf(uri);
        if (path is not null && File.Exists(path) && IsSource(path, folders)) {
            var text = File.ReadAllText(path);
            return Update(current => current.Documents.SetItem(key, new(uri, SyntaxTree.Parse(new SourceText(text, path), current.Layout.OptionsFor(path)), null, false)));
        }

        return Update(current => current.Documents.Remove(key));
    }

    /// <summary>
    ///     A folder was removed from the workspace: its files go, but for
    ///     those the editor has open and those under a folder still there.
    /// </summary>
    public WorkspaceSnapshot Forget(string folder, IReadOnlyList<string> remaining) =>
        Update(current => current.Documents.RemoveRange(
            current.Documents
                .Where(d => !d.Value.IsOpen
                            && PathOf(d.Value.Uri) is { } path
                            && IsUnder(path, folder)
                            && !remaining.Any(f => IsUnder(path, f)))
                .Select(static d => d.Key)));

    /// <summary>
    ///     A file changed on disk: read it again, unless the editor has it
    ///     open. A file that is not one of the workspace's sources — outside
    ///     every folder, or under one <see cref="SourceFilesUnder" /> skips,
    ///     such as a hidden folder holding another checkout — is not taken
    ///     in: the client watches every <c>*.sb</c>, and a git worktree under
    ///     <c>.claude/</c> or a build's <c>target/</c> would otherwise add a
    ///     copy of the whole repository, one change at a time. Null when
    ///     nothing the workspace holds changed.
    /// </summary>
    public WorkspaceSnapshot? Reload(string uri, IReadOnlyList<string> folders) {
        var key = KeyOf(uri);
        Current.Documents.TryGetValue(key, out var existing);
        if (existing is { IsOpen: true }) {
            return null;
        }

        if (PathOf(uri) is not { } path || !File.Exists(path) || !IsSource(path, folders)) {
            return existing is null ? null : Update(current => current.Documents.Remove(key));
        }

        var text = File.ReadAllText(path);
        return Update(current => current.Documents.SetItem(key, new(uri, SyntaxTree.Parse(new SourceText(text, path), current.Layout.OptionsFor(path)), null, false)));
    }

    /// <summary>Whether a file is one <see cref="SourceFilesUnder" /> finds under one of the folders.</summary>
    public static bool IsSource(string path, IReadOnlyList<string> folders) {
        foreach (var folder in folders) {
            if (!IsUnder(path, folder)) {
                continue;
            }

            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            var skipped = false;
            while (directory is not null && directory.Length > root.Length) {
                if (IsSkipped(directory)) {
                    skipped = true;
                    break;
                }

                directory = Path.GetDirectoryName(directory);
            }

            if (!skipped) {
                return true;
            }
        }

        return false;
    }

    public static bool IsUnder(string path, string root) {
        var full = Path.GetFullPath(path);
        var folder = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(folder, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A generator snapshot case's <c>expected/</c> (docs/08 §8.8): what a
    ///     generator should write, kept to compare with, not compiled.
    /// </summary>
    static bool IsGeneratorSnapshot(string directory) {
        var testCase = Path.GetDirectoryName(directory);
        var tests = Path.GetDirectoryName(testCase);
        return Path.GetFileName(directory) == "expected"
            && Path.GetFileName(tests) == "tests"
            && Path.GetFileName(Path.GetDirectoryName(tests)) == "generators";
    }

    /// <summary>A folder whose files are not the workspace's: hidden, build output, or dependencies.</summary>
    static bool IsSkipped(string directory) {
        var name = Path.GetFileName(directory);
        // vendor/ holds copies of dependencies, which their packages compile.
        return name.StartsWith('.') || name is "bin" or "obj" or "target" or "vendor" or "node_modules" || IsGeneratorSnapshot(directory);
    }

    /// <summary>The source files under a folder, skipping build output, dependencies and hidden folders.</summary>
    public static IEnumerable<string> SourceFilesUnder(string root) {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0) {
            var folder = pending.Pop();
            IEnumerable<string> files, folders;
            try {
                files = Directory.EnumerateFiles(folder, "*.sb");
                folders = Directory.EnumerateDirectories(folder);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                continue;
            }

            foreach (var file in files) {
                yield return file;
            }

            foreach (var child in folders) {
                if (IsSkipped(child)) {
                    continue;
                }

                pending.Push(child);
            }
        }
    }
}
