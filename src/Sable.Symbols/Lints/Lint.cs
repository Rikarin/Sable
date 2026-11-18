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
///     Third-party lints implement this against the same API, in a .NET
///     assembly that <c>sable check</c> and <c>sable lint</c> load as a
///     plugin (<see cref="Lints.LoadPlugin" />): each public, concrete
///     subclass with a constructor that takes nothing is one lint.
/// </remarks>
public abstract class Lint {
    /// <summary>What configuration and attributes call it: <c>unused_local</c>. Lower-case letters, digits and '_'.</summary>
    public abstract string Name { get; }

    /// <summary>
    ///     Its diagnostic code, in S3300-S3399. A plugin's lints share
    ///     S3397, and are told apart by the name each report ends with.
    /// </summary>
    public virtual ErrorCode Code => ErrorCode.LNT_PluginLint;

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
    readonly List<(SyntaxNode Node, string Message, DiagnosticFix? Fix)> _reports = [];
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

    public void Report(SyntaxNode node, string message) => _reports.Add((node, message, null));

    /// <summary>Reports something with the edits that would fix it, which <c>sable fix</c> applies and an editor offers.</summary>
    public void Report(SyntaxNode node, string message, DiagnosticFix fix) => _reports.Add((node, message, fix));

    /// <summary>A fix made of some edits.</summary>
    public static DiagnosticFix Fix(string title, params TextChange[] changes) => new(title, [..changes]);

    /// <summary>
    ///     The whole lines a node is on, from the start of the first through
    ///     its line break: what deleting a statement or an import removes, so
    ///     no blank line is left behind. Null when something else shares them.
    /// </summary>
    public TextSpan? WholeLinesOf(SyntaxNode node) {
        var text = Tree.Text.Text;
        var start = node.Span.Start;
        while (start > 0 && text[start - 1] is ' ' or '\t') {
            start--;
        }

        var end = node.Span.End;
        while (end < text.Length && text[end] is ' ' or '\t' or '\r') {
            end++;
        }

        if ((start > 0 && text[start - 1] != '\n') || (end < text.Length && text[end] != '\n')) {
            return null;
        }

        return TextSpan.FromBounds(start, Math.Min(text.Length, end + 1));
    }

    internal List<(SyntaxNode Node, string Message, DiagnosticFix? Fix)> TakeReports() {
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

