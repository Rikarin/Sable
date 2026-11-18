using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     The lint set of <c>sable check</c> (docs/05 §5.7), and the entry point
///     that runs it.
/// </summary>
public static class Lints {
    /// <summary>Every lint there is, by name.</summary>
    public static IReadOnlyList<Lint> All { get; } = [
        new UnusedLocalLint(),
        new UnusedParameterLint(),
        new UnusedImportLint(),
        new VarNeverMutatedLint(),
        new ShadowedParameterLint(),
        new UnusedResultLint(),
    ];

    public static Lint? Find(string name) => All.FirstOrDefault(l => l.Name == name);

    /// <summary>The attributes that set a lint's level for a declaration and everything inside it.</summary>
    static readonly Dictionary<string, LintLevel> LevelAttributes = new(StringComparer.Ordinal) {
        ["allow"] = LintLevel.Allow,
        ["warn"] = LintLevel.Warn,
    };

    /// <summary>
    ///     Runs every lint over the compilation's files — all but the core
    ///     library's, or just <paramref name="trees" /> — and returns what they
    ///     report at the level the configuration and the attributes give it.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Run(Compilation compilation, LintConfiguration configuration, IEnumerable<SyntaxTree>? trees = null) =>
        [..RunByFile(compilation, configuration, trees).Select(static d => d.Diagnostic)];

    /// <summary>The same, each diagnostic with the file it is in.</summary>
    public static IReadOnlyList<(Diagnostic Diagnostic, SyntaxTree Tree)> RunByFile(
        Compilation compilation,
        LintConfiguration configuration,
        IEnumerable<SyntaxTree>? trees = null
    ) {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(configuration);

        var found = new List<(Diagnostic, SyntaxTree)>();
        foreach (var tree in trees ?? compilation.SyntaxTrees.Where(static t => !CoreLibrary.Contains(t))) {
            if (tree.Root is not CompilationUnitSyntax) {
                continue;
            }

            foreach (var unknown in UnknownLintNames(tree)) {
                found.Add((unknown, tree));
            }

            var context = new LintContext(compilation, tree);
            foreach (var lint in All) {
                lint.Check(context);
                foreach (var (node, message) in context.TakeReports()) {
                    var level = LocalLevel(node, lint.Name) ?? configuration.LevelOf(lint);
                    if (level == LintLevel.Allow) {
                        continue;
                    }

                    var severity = level == LintLevel.Error ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;
                    found.Add((new Diagnostic(lint.Code, severity, node.Span, $"{message} [{lint.Name}]"), tree));
                }
            }
        }

        found.Sort(static (a, b) => a.Item1.Span.Start.CompareTo(b.Item1.Span.Start));
        return found;
    }

    /// <summary>The level the innermost <c>@allow</c> / <c>@warn</c> naming the lint around a node sets, if any.</summary>
    static LintLevel? LocalLevel(SyntaxNode node, string lint) {
        for (var at = node; at is not null; at = at.Parent) {
            if (at is not DeclarationSyntax) {
                continue;
            }

            foreach (var attribute in at.ChildNodesAndTokens().Where(static c => !c.IsToken).Select(static c => c.AsNode()).OfType<AttributeSyntax>()) {
                if (LevelAttributes.TryGetValue(attribute.Name.ToString().Trim(), out var level) && NamesIn(attribute).Any(n => n.Name == lint)) {
                    return level;
                }
            }
        }

        return null;
    }

    /// <summary>The names an attribute's arguments give: identifiers, or strings.</summary>
    static IEnumerable<(string Name, SyntaxNode Syntax)> NamesIn(AttributeSyntax attribute) {
        if (attribute.ArgumentList is not { } arguments) {
            yield break;
        }

        foreach (var argument in arguments.Arguments) {
            var text = argument.Expression.ToString().Trim();
            yield return (text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text, argument);
        }
    }

    /// <summary><c>@allow(nonsense)</c>: a name no lint has, which would otherwise silently do nothing.</summary>
    static IEnumerable<Diagnostic> UnknownLintNames(SyntaxTree tree) {
        foreach (var attribute in tree.Root.DescendantNodes().OfType<AttributeSyntax>()) {
            if (!LevelAttributes.ContainsKey(attribute.Name.ToString().Trim())) {
                continue;
            }

            var names = NamesIn(attribute).ToList();
            if (names.Count == 0) {
                yield return Diagnostic.Warning(ErrorCode.LNT_UnknownLint, attribute.Span, $"'@{attribute.Name.ToString().Trim()}' names no lint; write the lint's name, as '@allow(unused_local)'");
            }

            foreach (var (name, syntax) in names) {
                if (Find(name) is null) {
                    yield return Diagnostic.Warning(ErrorCode.LNT_UnknownLint, syntax.Span, $"no lint named '{name}'; the lints are {string.Join(", ", All.Select(static l => l.Name))}");
                }
            }
        }
    }
}
