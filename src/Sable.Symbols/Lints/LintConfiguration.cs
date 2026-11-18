using System.Collections.Immutable;

namespace Sable.Symbols;

/// <summary>A plugin's lints, and the assembly they came from.</summary>
public sealed record LintPlugin(string Path, IReadOnlyList<Lint> Lints) {
    /// <summary>What changes when the assembly does — its path, size and time — for caching what the lints said.</summary>
    public string Fingerprint {
        get {
            var file = new FileInfo(Path);
            return file.Exists ? $"{Path}:{file.Length}:{file.LastWriteTimeUtc.Ticks}" : Path;
        }
    }
}

/// <summary>
///     Which lints there are and at which level, by name: the built-in ones
///     and those of the plugins it was given, at what a package's
///     <c>[lints]</c> table says, over each lint's default.
/// </summary>
public sealed class LintConfiguration {
    readonly ImmutableDictionary<string, LintLevel> _levels;

    LintConfiguration(ImmutableDictionary<string, LintLevel> levels, ImmutableArray<LintPlugin> plugins) {
        _levels = levels;
        Plugins = plugins;
    }

    /// <summary>Every built-in lint at its default level, and no plugins.</summary>
    public static LintConfiguration Default { get; } = new(ImmutableDictionary<string, LintLevel>.Empty.WithComparers(StringComparer.Ordinal), []);

    public ImmutableArray<LintPlugin> Plugins { get; }

    /// <summary>Every lint it runs: the built-in ones, then the plugins'.</summary>
    public IEnumerable<Lint> All => Lints.All.Concat(Plugins.SelectMany(static p => p.Lints));

    public Lint? Find(string name) => All.FirstOrDefault(l => l.Name == name);

    /// <summary>The same, with one lint at another level.</summary>
    /// <exception cref="ArgumentException">No lint has that name.</exception>
    public LintConfiguration With(string name, LintLevel level) =>
        Find(name) is null
            ? throw new ArgumentException($"no lint named '{name}'", nameof(name))
            : new(_levels.SetItem(name, level), Plugins);

    /// <summary>The same, with a plugin's lints too.</summary>
    /// <exception cref="ArgumentException">One of its lints has the name of one there already.</exception>
    public LintConfiguration WithPlugin(LintPlugin plugin) {
        ArgumentNullException.ThrowIfNull(plugin);
        foreach (var lint in plugin.Lints) {
            if (Find(lint.Name) is not null) {
                var other = Lints.Find(lint.Name) is not null ? "a built-in lint" : "another plugin's";
                throw new ArgumentException($"{System.IO.Path.GetFileName(plugin.Path)} has a lint named '{lint.Name}', and so has {other}", nameof(plugin));
            }
        }

        return new(_levels, Plugins.Add(plugin));
    }

    public LintLevel LevelOf(Lint lint) => _levels.TryGetValue(lint.Name, out var level) ? level : lint.DefaultLevel;

    /// <summary>
    ///     Everything apart from the sources that decides what the lints
    ///     report: each lint's level, and each plugin's assembly. Two runs
    ///     over the same sources with the same fingerprint report the same.
    /// </summary>
    public string Fingerprint =>
        string.Join(",", All.Select(l => $"{l.Name}={LevelOf(l)}").Concat(Plugins.Select(static p => p.Fingerprint)));

    /// <summary>
    ///     Reads a <c>[lints]</c> table: <c>name = "allow" | "warn" | "error"</c>,
    ///     over <paramref name="start" />, whose plugins' lints are among the
    ///     names it knows. What it cannot read is in <paramref name="errors" />,
    ///     and left at its default.
    /// </summary>
    public static LintConfiguration FromTable(
        IEnumerable<KeyValuePair<string, string>> table,
        out IReadOnlyList<string> errors,
        LintConfiguration? start = null
    ) {
        var problems = new List<string>();
        var configuration = start ?? Default;
        foreach (var (name, value) in table) {
            if (configuration.Find(name) is null) {
                problems.Add($"no lint named '{name}'; the lints are {string.Join(", ", configuration.All.Select(static l => l.Name))}");
            } else if (ParseLevel(value) is not { } level) {
                problems.Add($"lint '{name}': '{value}' is not a level; use \"allow\", \"warn\" or \"error\"");
            } else {
                configuration = configuration.With(name, level);
            }
        }

        errors = problems;
        return configuration;
    }

    public static LintLevel? ParseLevel(string text) =>
        text switch {
            "allow" => LintLevel.Allow,
            "warn" => LintLevel.Warn,
            "error" or "deny" => LintLevel.Error,
            _ => null,
        };
}
