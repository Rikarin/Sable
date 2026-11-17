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
internal sealed class WorkspaceSnapshot(ImmutableDictionary<string, Document> documents, long generation) {
    public static readonly WorkspaceSnapshot Empty = new(ImmutableDictionary.Create<string, Document>(StringComparer.Ordinal), 0);

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

    WorkspaceSnapshot Update(Func<ImmutableDictionary<string, Document>, ImmutableDictionary<string, Document>> change) {
        var current = Current;
        var next = new WorkspaceSnapshot(change(current.Documents), current.Generation + 1);
        Volatile.Write(ref _current, next);
        return next;
    }

    /// <summary>Adds files read from disk, leaving any that are open as the editor has them.</summary>
    public WorkspaceSnapshot AddFromDisk(IEnumerable<(string Path, SyntaxTree Tree)> files) =>
        Update(documents => {
            var builder = documents.ToBuilder();
            foreach (var (path, tree) in files) {
                var key = KeyOf(UriOf(path));
                if (builder.TryGetValue(key, out var existing) && existing.IsOpen) {
                    continue;
                }

                builder[key] = new(UriOf(path), tree, null, false);
            }

            return builder.ToImmutable();
        });

    public WorkspaceSnapshot Open(string uri, string text, int? version) {
        var tree = SyntaxTree.Parse(new SourceText(text, PathOf(uri) ?? uri));
        return Update(documents => documents.SetItem(KeyOf(uri), new(uri, tree, version, true)));
    }

    /// <summary>
    ///     Applies the editor's edits in order, each by the incremental
    ///     parser: tokens and whole members outside the edit are reused.
    /// </summary>
    public WorkspaceSnapshot Change(string uri, int? version, IReadOnlyList<(LspRange? Range, string Text)> changes) {
        var key = KeyOf(uri);
        if (!Current.Documents.TryGetValue(key, out var document)) {
            throw new LspException(LspException.InvalidParams, $"'{uri}' is not open");
        }

        var tree = document.Tree;
        foreach (var (range, text) in changes) {
            if (range is not { } r) {
                tree = SyntaxTree.Parse(new SourceText(text, tree.Text.FileName));
                continue;
            }

            var lines = LineIndex.Of(tree.Text);
            var start = lines.OffsetOf(r.Start);
            var end = Math.Max(start, lines.OffsetOf(r.End));
            tree = tree.WithChange(new TextChange(new TextSpan(start, end - start), text));
        }

        return Update(documents => documents.SetItem(key, document with { Tree = tree, Version = version ?? document.Version }));
    }

    /// <summary>
    ///     Closes a document: it goes back to what is on disk, if it is a
    ///     file of the workspace, and away otherwise.
    /// </summary>
    public WorkspaceSnapshot Close(string uri, string? root) {
        var key = KeyOf(uri);
        var path = PathOf(uri);
        if (path is not null && root is not null && File.Exists(path) && IsUnder(path, root)) {
            var tree = SyntaxTree.Parse(new SourceText(File.ReadAllText(path), path));
            return Update(documents => documents.SetItem(key, new(uri, tree, null, false)));
        }

        return Update(documents => documents.Remove(key));
    }

    /// <summary>A file changed on disk: read it again, unless the editor has it open.</summary>
    public WorkspaceSnapshot? Reload(string uri) {
        var key = KeyOf(uri);
        if (Current.Documents.TryGetValue(key, out var existing) && existing.IsOpen) {
            return null;
        }

        if (PathOf(uri) is not { } path || !File.Exists(path)) {
            return existing is null ? null : Update(documents => documents.Remove(key));
        }

        var tree = SyntaxTree.Parse(new SourceText(File.ReadAllText(path), path));
        return Update(documents => documents.SetItem(key, new(uri, tree, null, false)));
    }

    public static bool IsUnder(string path, string root) {
        var full = Path.GetFullPath(path);
        var folder = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(folder, StringComparison.Ordinal);
    }

    /// <summary>The source files under a folder, skipping build output, dependencies and hidden folders.</summary>
    public static IEnumerable<string> SourceFilesUnder(string root) {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0) {
            var folder = pending.Pop();
            IEnumerable<string> files, folders;
            try {
                files = Directory.EnumerateFiles(folder, "*.sab");
                folders = Directory.EnumerateDirectories(folder);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                continue;
            }

            foreach (var file in files) {
                yield return file;
            }

            foreach (var child in folders) {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || name is "bin" or "obj" or "target" or "node_modules") {
                    continue;
                }

                pending.Push(child);
            }
        }
    }
}
