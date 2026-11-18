using System.Collections.Concurrent;
using Sable.Syntax;

namespace Sable.LanguageServer;

/// <summary>
///     A package as the server compiles it, which the driver works out from
///     its manifest as <c>sable check</c> does: the files under
///     <paramref name="Directory" /> parsed with <paramref name="Features" />
///     on, and its dependencies' library files, each parsed with its own.
/// </summary>
/// <param name="Name">The package's name, for the log.</param>
/// <param name="Directory">Its root: a file under it is its, unless a package nested deeper has it.</param>
/// <param name="Features">The features it is built with by default.</param>
/// <param name="Dependencies">Every file of every package it depends on, transitively, that a build compiles with it.</param>
public sealed record LanguageProject(string Name, string Directory, IReadOnlyList<string> Features, IReadOnlyList<LanguageProjectFile> Dependencies);

/// <summary>A dependency's file, and the features its package is built with.</summary>
public sealed record LanguageProjectFile(string Path, IReadOnlyList<string> Features);

/// <summary>
///     The packages of the workspace folder, and which one a file is in:
///     the one with the deepest directory holding it. A file in no package
///     is compiled with the others like it, as a loose program.
/// </summary>
internal sealed class ProjectLayout {
    public static readonly ProjectLayout None = new([]);

    /// <summary>The key of the loose files' compilation.</summary>
    public const string Loose = "";

    readonly IReadOnlyList<(string Prefix, LanguageProject Project, ParseOptions Options)> _projects;

    public ProjectLayout(IReadOnlyList<LanguageProject> projects) {
        Projects = projects;
        _projects = [
            ..projects
                .Select(static p => (Path.GetFullPath(p.Directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, p, OptionsWith(p.Features)))
                .OrderByDescending(static p => p.Item1.Length),
        ];
    }

    public IReadOnlyList<LanguageProject> Projects { get; }

    public LanguageProject? ProjectOf(string? path) {
        if (path is null || _projects.Count == 0) {
            return null;
        }

        var full = Path.GetFullPath(path);
        foreach (var (prefix, project, _) in _projects) {
            if (full.StartsWith(prefix, StringComparison.Ordinal)) {
                return project;
            }
        }

        return null;
    }

    /// <summary>Which compilation a file is in: its package's directory, or <see cref="Loose" />.</summary>
    public string KeyOf(string? path) => ProjectOf(path)?.Directory ?? Loose;

    /// <summary>What a file is parsed with: its package's features.</summary>
    public ParseOptions OptionsFor(string? path) {
        if (path is null || _projects.Count == 0) {
            return ParseOptions.Default;
        }

        var full = Path.GetFullPath(path);
        foreach (var (prefix, _, options) in _projects) {
            if (full.StartsWith(prefix, StringComparison.Ordinal)) {
                return options;
            }
        }

        return ParseOptions.Default;
    }

    public static ParseOptions OptionsWith(IReadOnlyCollection<string> features) =>
        features.Count == 0 ? ParseOptions.Default : ParseOptions.Default with { Features = [..features] };

    /// <summary>Whether two parses of a file would give the same tree.</summary>
    public static bool SameFeatures(ParseOptions a, ParseOptions b) => a.Features.SetEquals(b.Features);
}

/// <summary>
///     The dependencies' files, parsed with their packages' features, kept
///     while their text stays the same: a dependency is read from disk, or
///     from the editor when it has the file open.
/// </summary>
internal sealed class DependencyTrees {
    readonly ConcurrentDictionary<(string Path, string Features), SyntaxTree> _trees = new();

    /// <summary>
    ///     The trees of a project's dependencies, as of the snapshot. A file
    ///     the snapshot has, parsed with the same features, is that very
    ///     tree, so its declarations are the document's; any other is the
    ///     file's text parsed here.
    /// </summary>
    public IReadOnlyList<SyntaxTree> For(LanguageProject project, WorkspaceSnapshot snapshot, Action<string> log) {
        var trees = new List<SyntaxTree>();
        foreach (var file in project.Dependencies) {
            var options = ProjectLayout.OptionsWith(file.Features);
            var document = snapshot.Documents.GetValueOrDefault(Path.GetFullPath(file.Path));
            if (document is not null && ProjectLayout.SameFeatures(document.Tree.Options, options)) {
                trees.Add(document.Tree);
                continue;
            }

            string text;
            try {
                text = document?.Tree.Text.Text ?? File.ReadAllText(file.Path);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                log($"cannot read {file.Path}: {e.Message}");
                continue;
            }

            var key = (file.Path, string.Join(",", file.Features.Order(StringComparer.Ordinal)));
            if (!_trees.TryGetValue(key, out var tree) || tree.Text.Text != text) {
                _trees[key] = tree = SyntaxTree.Parse(new SourceText(text, file.Path), options);
            }

            trees.Add(tree);
        }

        return trees;
    }
}
