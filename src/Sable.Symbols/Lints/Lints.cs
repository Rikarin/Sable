using System.Reflection;
using System.Runtime.Loader;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     The lint set of <c>sable check</c> (docs/05 §5.7), the entry point
///     that runs it, and the loading of plugins that add to it.
/// </summary>
public static class Lints {
    /// <summary>Every built-in lint, by name.</summary>
    public static IReadOnlyList<Lint> All { get; } = [
        new UnusedLocalLint(),
        new UnusedParameterLint(),
        new UnusedImportLint(),
        new VarNeverMutatedLint(),
        new ShadowedParameterLint(),
        new UnusedResultLint(),
        new LetDiscardLint(),
        new ShadowedLocalLint(),
        new SelfAssignmentLint(),
        new UnusedPrivateLint(),
        new ConstantArithmeticLint(),
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

        var lints = configuration.All.ToList();
        var found = new List<(Diagnostic, SyntaxTree)>();
        foreach (var tree in trees ?? compilation.SyntaxTrees.Where(static t => !CoreLibrary.Contains(t))) {
            if (tree.Root is not CompilationUnitSyntax unit) {
                continue;
            }

            foreach (var unknown in UnknownLintNames(tree, configuration)) {
                found.Add((unknown, tree));
            }

            var context = new LintContext(compilation, tree);
            foreach (var lint in lints) {
                try {
                    lint.Check(context);
                } catch (Exception e) when (e is not OutOfMemoryException && Find(lint.Name) is null) {
                    // A plugin's lint that fails is the plugin's bug, not a
                    // reason to stop checking: it is said, and the rest run.
                    context.TakeReports();
                    found.Add((Diagnostic.Warning(ErrorCode.LNT_PluginFailed, new TextSpan(unit.Span.Start, 0),
                        $"the lint '{lint.Name}' failed on this file: {e.GetType().Name}: {e.Message}"), tree));
                    continue;
                }

                foreach (var (node, message, fix) in context.TakeReports()) {
                    var level = LocalLevel(node, lint.Name) ?? configuration.LevelOf(lint);
                    if (level == LintLevel.Allow) {
                        continue;
                    }

                    var severity = level == LintLevel.Error ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;
                    found.Add((new Diagnostic(lint.Code, severity, node.Span, $"{message} [{lint.Name}]") { Fixes = fix is null ? [] : [fix] }, tree));
                }
            }
        }

        found.Sort(static (a, b) => a.Item1.Span.Start.CompareTo(b.Item1.Span.Start));
        return found;
    }

    /// <summary>
    ///     Loads a plugin: a .NET assembly with lints in it, each a public,
    ///     concrete <see cref="Lint" /> with a constructor that takes nothing.
    ///     It is loaded beside the compiler, so it sees the same
    ///     <see cref="SemanticModel" /> types the built-in lints do.
    /// </summary>
    /// <exception cref="LintPluginException">It cannot be loaded, or has no lints, or a lint it has is not one sable can run.</exception>
    public static LintPlugin LoadPlugin(string path) {
        ArgumentNullException.ThrowIfNull(path);
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) {
            throw new LintPluginException($"no lint plugin at '{path}'");
        }

        Assembly assembly;
        Type[] types;
        try {
            assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(full);
            types = assembly.GetExportedTypes();
        } catch (Exception e) when (e is BadImageFormatException or FileLoadException or ReflectionTypeLoadException or TypeLoadException) {
            throw new LintPluginException($"cannot load the lint plugin '{path}': {e.Message}");
        }

        var lints = new List<Lint>();
        foreach (var type in types.Where(static t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(Lint))).OrderBy(static t => t.FullName, StringComparer.Ordinal)) {
            if (type.GetConstructor(Type.EmptyTypes) is null) {
                throw new LintPluginException($"the lint plugin '{path}': {type.FullName} needs a public constructor that takes nothing");
            }

            Lint lint;
            try {
                lint = (Lint)Activator.CreateInstance(type)!;
            } catch (TargetInvocationException e) {
                throw new LintPluginException($"the lint plugin '{path}': making {type.FullName} failed: {e.InnerException?.Message ?? e.Message}");
            }

            if (string.IsNullOrEmpty(lint.Name) || !lint.Name.All(static c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_') || !char.IsAsciiLetterLower(lint.Name[0])) {
                throw new LintPluginException($"the lint plugin '{path}': {type.FullName} is named '{lint.Name}'; a lint's name is lower-case letters, digits and '_', as '@allow(name)' writes it");
            }

            if ((int)lint.Code is < 3300 or > 3399) {
                throw new LintPluginException($"the lint plugin '{path}': '{lint.Name}' reports S{(int)lint.Code:D4}, which is not a lint's code; leave 'Code' as it is (S3397)");
            }

            if (lints.Exists(l => l.Name == lint.Name)) {
                throw new LintPluginException($"the lint plugin '{path}' has two lints named '{lint.Name}'");
            }

            lints.Add(lint);
        }

        if (lints.Count == 0) {
            throw new LintPluginException($"the lint plugin '{path}' has no lints: no public class in it derives from Sable.Symbols.Lint");
        }

        return new(full, lints);
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
    static IEnumerable<Diagnostic> UnknownLintNames(SyntaxTree tree, LintConfiguration configuration) {
        foreach (var attribute in tree.Root.DescendantNodes().OfType<AttributeSyntax>()) {
            if (!LevelAttributes.ContainsKey(attribute.Name.ToString().Trim())) {
                continue;
            }

            var names = NamesIn(attribute).ToList();
            if (names.Count == 0) {
                yield return Diagnostic.Warning(ErrorCode.LNT_UnknownLint, attribute.Span, $"'@{attribute.Name.ToString().Trim()}' names no lint; write the lint's name, as '@allow(unused_local)'");
            }

            foreach (var (name, syntax) in names) {
                if (configuration.Find(name) is null) {
                    yield return Diagnostic.Warning(ErrorCode.LNT_UnknownLint, syntax.Span, $"no lint named '{name}'; the lints are {string.Join(", ", configuration.All.Select(static l => l.Name))}");
                }
            }
        }
    }
}

/// <summary>A lint plugin that cannot be used, and why.</summary>
public sealed class LintPluginException(string message) : Exception(message);
