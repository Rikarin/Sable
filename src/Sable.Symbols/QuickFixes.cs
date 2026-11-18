using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     The edits that fix some of the compiler's own errors, carried on the
///     diagnostic (<see cref="Diagnostic.Fixes" />) as an editor's quick
///     fixes (<c>sable fix</c> takes only a program that compiles): <c>try</c>
///     where a call can throw, the cases
///     a match leaves out, and a stub for each requirement a type is missing.
///     A stub's or a case's body is <c>unreachable()</c>, which compiles
///     whatever the type and traps if it runs: the fix makes the code
///     compile and leaves the writing to the programmer.
/// </summary>
internal static class QuickFixes {
    const string Placeholder = "unreachable()";

    /// <summary>The diagnostic, with a fix of edits in its own file.</summary>
    public static Diagnostic With(Diagnostic diagnostic, string title, params TextChange[] changes) =>
        diagnostic with { Fixes = diagnostic.Fixes.Add(new(title, [..changes])) };

    /// <summary><c>try</c> before an expression that can throw.</summary>
    public static Diagnostic MissingTry(Diagnostic diagnostic, TextSpan expression) =>
        With(diagnostic, "Add 'try'", new TextChange(new(expression.Start, 0), "try "));

    /// <summary>A case for each pattern a match does not cover — or <c>default</c>, for cases a union may gain — after its last arm.</summary>
    public static Diagnostic MissingArms(Diagnostic diagnostic, MatchExpressionSyntax match, IReadOnlyList<string> missing, bool laterCases) {
        if (match.CloseBraceToken.IsMissing || match.Arms.Any(static a => a is not MatchArmSyntax)) {
            return diagnostic;
        }

        string indent;
        int at;
        if (match.Arms.LastOrDefault() is { } last) {
            indent = IndentOf(last.GetFirstToken());
            at = last.Span.End;
        } else {
            indent = IndentOf(match.CloseBraceToken) + "    ";
            at = match.OpenBraceToken.Span.End;
        }

        var arms = missing.Select(static w => $"case {w} => {Placeholder}").ToList();
        if (laterCases) {
            arms.Add($"default => {Placeholder}");
        }

        if (arms.Count == 0) {
            return diagnostic;
        }

        var text = string.Concat(arms.Select(a => "\n" + indent + a));
        var title = arms.Count == 1 ? $"Add '{(laterCases ? "default" : "case " + missing[0])}'" : "Add the missing cases";
        return With(diagnostic, title, new TextChange(new(at, 0), text));
    }

    /// <summary>A stub of a protocol's requirement, at the end of the body of the type that lacks it.</summary>
    public static Diagnostic Stub(Diagnostic diagnostic, NamedTypeSymbol type, Symbol requirement) {
        if (type.DeclaringSyntax.IsEmpty || CloseBraceOf(type.DeclaringSyntax[0]) is not { IsMissing: false } close
            || requirement.DeclaringSyntax.IsEmpty) {
            return diagnostic;
        }

        var indent = IndentOf(close);
        string? stub = requirement.DeclaringSyntax[0] switch {
            FunctionDeclarationSyntax { Body: null, ExpressionBody: null } function =>
                $"{Signature(function)} {{\n{indent}        {Placeholder}\n{indent}    }}",
            _ when requirement is FieldSymbol field && !(field.IsComputed ? field.HasSetter : field.IsMutable) =>
                $"var {field.Name}: {field.Type.ToDisplayString()} => {Placeholder}",
            _ => null,
        };

        return stub is null
            ? diagnostic
            : With(diagnostic, $"Add a stub of '{requirement.Name}'", new TextChange(new(close.Span.Start, 0), $"    {stub}\n{indent}"));
    }

    /// <summary>A requirement's declaration as one line, without its attributes, doc comment or modifiers a witness would not repeat.</summary>
    static string Signature(FunctionDeclarationSyntax function) {
        var from = function.FnKeyword.Span.Start;
        foreach (var modifier in function.Modifiers) {
            if (modifier.Kind is SyntaxKind.StaticKeyword or SyntaxKind.MutatingKeyword) {
                from = Math.Min(from, modifier.Span.Start);
            }
        }

        var text = function.ToString()[(from - function.Span.Start)..];
        return string.Join(" ", text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(static l => l.Trim()));
    }

    static SyntaxToken? CloseBraceOf(SyntaxNode declaration) =>
        declaration switch {
            StructDeclarationSyntax s => s.CloseBraceToken,
            ClassDeclarationSyntax c => c.CloseBraceToken,
            ActorDeclarationSyntax a => a.CloseBraceToken,
            UnionDeclarationSyntax u => u.CloseBraceToken,
            EnumDeclarationSyntax e => e.CloseBraceToken,
            _ => null,
        };

    /// <summary>The whitespace that starts the line a token is on.</summary>
    static string IndentOf(SyntaxToken token) {
        if (token.Parent is not { } node) {
            return "";
        }

        while (node.Parent is { } parent) {
            node = parent;
        }

        var text = node.ToFullString();
        var start = token.Span.Start;
        while (start > 0 && text[start - 1] is not ('\n' or '\r')) {
            start--;
        }

        var end = start;
        while (end < text.Length && text[end] is ' ' or '\t') {
            end++;
        }

        return text[start..end];
    }
}
