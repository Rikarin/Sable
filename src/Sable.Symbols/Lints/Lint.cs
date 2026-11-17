using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>A lint's tier (docs/05 §5.7): fatal, reported, or off unless asked for.</summary>
public enum LintLevel {
    Allow,
    Warn,
    Error
}

/// <summary>
///     A check over a file's <see cref="SemanticModel" /> for something legal
///     but likely wrong (docs/05 §5.7). A lint finds; whether what it finds is
///     an error, a warning or nothing is the configuration's business and
///     that of <c>@allow(name)</c> / <c>@warn(name)</c> on the declarations
///     around it.
/// </summary>
/// <remarks>
///     Third-party lints are meant to implement this against the same API,
///     loaded by <c>sable check</c> as plugins; that loading is not built yet.
/// </remarks>
public abstract class Lint {
    /// <summary>What configuration and attributes call it: <c>unused_local</c>.</summary>
    public abstract string Name { get; }

    /// <summary>Its diagnostic code, in S3300-S3399.</summary>
    public abstract ErrorCode Code { get; }

    public abstract LintLevel DefaultLevel { get; }

    /// <summary>One line on what it reports.</summary>
    public abstract string Description { get; }

    /// <summary>Reports what it finds in one file through <see cref="LintContext.Report" />.</summary>
    public abstract void Check(LintContext context);
}

/// <summary>A body a lint can look into: the declaration, its function, and the bound body.</summary>
public sealed record LintBody(SyntaxNode Declaration, FunctionSymbol? Function, BoundBlock Body);

/// <summary>One file, as a lint sees it.</summary>
public sealed class LintContext {
    readonly List<(SyntaxNode Node, string Message)> _reports = [];
    readonly Lazy<IReadOnlyList<LintBody>> _bodies;

    internal LintContext(Compilation compilation, SyntaxTree tree) {
        Compilation = compilation;
        Tree = tree;
        Model = compilation.GetSemanticModel(tree);
        _bodies = new(FindBodies);
    }

    public Compilation Compilation { get; }
    public SyntaxTree Tree { get; }
    public SemanticModel Model { get; }

    /// <summary>
    ///     Every bound body in the file: functions, initialisers, operators,
    ///     subscripts and accessors, the methods of local types among them. A
    ///     local function's body is part of the body around it.
    /// </summary>
    public IReadOnlyList<LintBody> Bodies => _bodies.Value;

    public void Report(SyntaxNode node, string message) => _reports.Add((node, message));

    internal List<(SyntaxNode Node, string Message)> TakeReports() {
        var reports = _reports.ToList();
        _reports.Clear();
        return reports;
    }

    IReadOnlyList<LintBody> FindBodies() {
        var bodies = new List<LintBody>();
        foreach (var node in Tree.Root.DescendantNodes()) {
            if (node is FunctionDeclarationSyntax { Parent: DeclarationStatementSyntax }) {
                continue;
            }

            if (node is not (FunctionDeclarationSyntax or InitDeclarationSyntax or DeinitDeclarationSyntax
                or OperatorDeclarationSyntax or SubscriptDeclarationSyntax or AccessorSyntax)) {
                continue;
            }

            if (Model.GetBoundNode(node) is not BoundBlock body) {
                continue;
            }

            var function = node is AccessorSyntax accessor
                ? Model.GetAccessorSymbol(accessor)
                : Compilation.GetDeclaredSymbol(node) as FunctionSymbol;
            bodies.Add(new(node, function, body));
        }

        return bodies;
    }
}

/// <summary>
///     Which lints are at which level, by name: what a package's
///     <c>[lints]</c> table says, over each lint's default.
/// </summary>
public sealed class LintConfiguration {
    readonly ImmutableDictionary<string, LintLevel> _levels;

    LintConfiguration(ImmutableDictionary<string, LintLevel> levels) => _levels = levels;

    /// <summary>Every lint at its default level.</summary>
    public static LintConfiguration Default { get; } = new(ImmutableDictionary<string, LintLevel>.Empty.WithComparers(StringComparer.Ordinal));

    /// <summary>The same, with one lint at another level.</summary>
    /// <exception cref="ArgumentException">No lint has that name.</exception>
    public LintConfiguration With(string name, LintLevel level) =>
        Lints.Find(name) is null
            ? throw new ArgumentException($"no lint named '{name}'", nameof(name))
            : new(_levels.SetItem(name, level));

    public LintLevel LevelOf(Lint lint) => _levels.TryGetValue(lint.Name, out var level) ? level : lint.DefaultLevel;

    /// <summary>
    ///     Reads a <c>[lints]</c> table: <c>name = "allow" | "warn" | "error"</c>.
    ///     What it cannot read is in <paramref name="errors" />, and left at its default.
    /// </summary>
    public static LintConfiguration FromTable(IEnumerable<KeyValuePair<string, string>> table, out IReadOnlyList<string> errors) {
        var problems = new List<string>();
        var configuration = Default;
        foreach (var (name, value) in table) {
            if (Lints.Find(name) is null) {
                problems.Add($"no lint named '{name}'; the lints are {string.Join(", ", Lints.All.Select(static l => l.Name))}");
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
