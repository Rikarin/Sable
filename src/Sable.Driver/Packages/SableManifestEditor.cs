using System.Text;
using Sable.Syntax;

namespace Sable.Driver.Packages;

/// <summary>
///     <c>sable add</c> and <c>sable remove</c> on a <c>package.sb</c>
///     (docs/05 §5.2.7): edits made through the syntax tree, at the spans of
///     the nodes they change, so every comment and every line not touched
///     stays as it was.
/// </summary>
/// <remarks>
///     It edits the <c>Package { ... }</c> record that <c>let manifest</c> is
///     initialised with, and the array literals of its dependency fields.
///     Where a list is computed instead — by a function, by <c>+</c> — it does
///     not guess, and says what to add and where.
/// </remarks>
public static class SableManifestEditor {
    /// <summary>The field of a <c>Package</c> for each kind of dependency.</summary>
    public static string FieldFor(bool dev) => dev ? "devDependencies" : "dependencies";

    static readonly string[] DependencyFields = ["dependencies", "devDependencies", "buildDependencies"];

    /// <summary>A dependency as <c>Sable.Build</c> writes it.</summary>
    public static string Element(string name, string? version, string? path) =>
        path is null
            ? $".registry({Quote(name)}, {Quote(version!)})"
            : version is null
                ? $".path({Quote(name)}, {Quote(path)})"
                : $".path({Quote(name)}, {Quote(path)}, version: {Quote(version)})";

    /// <summary>Sets the dependency in <paramref name="field" />: replaced where it is, else added at the end of the list.</summary>
    /// <exception cref="PackageException">The manifest is not written in a shape that can be edited safely.</exception>
    public static string Set(string text, string file, string field, string name, string element) {
        var (tree, record) = Record(text, file, element);
        var fieldSyntax = Field(record, field);
        if (fieldSyntax is null) {
            return AddField(text, record, field, element);
        }

        var array = Array(tree, fieldSyntax, field, element);
        if (array.Elements.Count == 0) {
            return Replace(text, array.Span, $"[{element}]");
        }

        for (var i = 0; i < array.Elements.Count; i++) {
            if (NameOf(array.Elements[i]) == name) {
                return Replace(text, array.Elements[i].Span, element);
            }
        }

        return Append(text, array.Elements[^1].Span, array.Elements.SeparatorCount == array.Elements.Count, LinesOf(array), element);
    }

    /// <summary>Removes the dependency from whichever dependency list has it; null when none does.</summary>
    public static string? Remove(string text, string file, string name) {
        var (tree, record) = Record(text, file, $"(removing {name})");
        foreach (var field in DependencyFields) {
            if (Field(record, field) is not { Expression: ArrayExpressionSyntax array }) {
                continue;
            }

            for (var i = 0; i < array.Elements.Count; i++) {
                if (NameOf(array.Elements[i]) != name) {
                    continue;
                }

                var element = array.Elements[i].Span;
                var hasSeparator = i < array.Elements.SeparatorCount;
                var end = hasSeparator ? array.Elements.GetSeparator(i).Span.End : element.End;
                if (LinesOf(array)) {
                    // The element's whole line, its indentation and line break with it.
                    var start = text.LastIndexOf('\n', element.Start - 1) + 1;
                    var lineEnd = text.IndexOf('\n', end);
                    return text[..start] + text[(lineEnd < 0 ? text.Length : lineEnd + 1)..];
                }

                // On one line: the element and the separator beside it.
                if (hasSeparator) {
                    var after = end;
                    while (after < text.Length && text[after] == ' ') {
                        after++;
                    }

                    return text[..element.Start] + text[after..];
                }

                if (i > 0) {
                    return text[..array.Elements.GetSeparator(i - 1).Span.Start] + text[element.End..];
                }

                return text[..element.Start] + text[element.End..];
            }
        }

        return null;
    }

    /// <summary>
    ///     Adds a member directory to the <c>members</c> list of a
    ///     <c>workspace.sb</c>, at the end, laid out as the list is.
    /// </summary>
    /// <exception cref="PackageException">The list is computed, or the file is not written in a shape that can be edited safely.</exception>
    public static string AddWorkspaceMember(string text, string file, string member) {
        var element = Quote(member);
        var (tree, record) = Record(text, file, element, "workspace", "Workspace");
        if (Field(record, "members") is not { } field) {
            return AddField(text, record, "members", element);
        }

        var array = Array(tree, field, "members", element);
        return array.Elements.Count == 0
            ? Replace(text, array.Span, $"[{element}]")
            : Append(text, array.Elements[^1].Span, array.Elements.SeparatorCount == array.Elements.Count, LinesOf(array), element);
    }

    static (SyntaxTree Tree, RecordExpressionSyntax Record) Record(string text, string file, string element, string value = "manifest", string type = "Package") {
        var tree = SyntaxTree.Parse(new SourceText(text, file));
        if (tree.Diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error)) {
            throw new PackageException($"{file} does not parse, so it cannot be edited; fix it, or add {element} by hand");
        }

        var unit = (CompilationUnitSyntax)tree.Root;
        var declaration = unit.Members.OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(f => f.Declarator.Pattern is BindingPatternSyntax binding && binding.Identifier.Text == value);
        if (declaration?.Declarator.Initializer?.Value is not RecordExpressionSyntax record) {
            throw new PackageException($"{file}: `let {value}` is not written as `{type} {{ ... }}`, so it cannot be edited safely; add {element} by hand");
        }

        return (tree, record);
    }

    static FieldInitializerSyntax? Field(RecordExpressionSyntax record, string name) {
        for (var i = 0; i < record.Fields.Count; i++) {
            if (record.Fields[i].NameToken.Text == name) {
                return record.Fields[i];
            }
        }

        return null;
    }

    static ArrayExpressionSyntax Array(SyntaxTree tree, FieldInitializerSyntax field, string name, string element) =>
        field.Expression as ArrayExpressionSyntax
        ?? throw new PackageException(
            $"{tree.Text.FileName}:{tree.Text.GetLinePosition(field.Span.Start).Line}: '{name}' is computed, not a list, so it cannot be edited safely; add {element} to it there");

    /// <summary>What a dependency element names: the first argument of <c>.registry("name", ...)</c> and its kin.</summary>
    static string? NameOf(ExpressionSyntax element) =>
        element is InvocationExpressionSyntax { ArgumentList: { Arguments.Count: > 0 } arguments }
        && arguments.Arguments[0].Expression is LiteralExpressionSyntax { Token.Value: string name }
            ? name
            : null;

    /// <summary>Whether a list is written one element to a line.</summary>
    static bool LinesOf(ArrayExpressionSyntax array) => array.ToString().Contains('\n', StringComparison.Ordinal);

    /// <summary>A new last element after <paramref name="last" />, laid out as the list is.</summary>
    static string Append(string text, TextSpan last, bool trailingComma, bool lines, string element) {
        if (!lines) {
            return trailingComma
                ? text[..CommaEnd(text, last.End)] + $" {element}," + text[CommaEnd(text, last.End)..]
                : text[..last.End] + $", {element}" + text[last.End..];
        }

        var indent = IndentationAt(text, last.Start);
        // After the line's end, so a comment after the last element stays with it.
        var after = CommaEnd(text, last.End);
        var lineEnd = text.IndexOf('\n', after);
        if (lineEnd >= 0 && text[after..lineEnd].Trim() is var rest && (rest.Length == 0 || rest.StartsWith("//", StringComparison.Ordinal))) {
            after = lineEnd;
        }

        return trailingComma
            ? text[..after] + $"\n{indent}{element}," + text[after..]
            : text[..last.End] + $",\n{indent}{element}" + text[last.End..];
    }

    /// <summary>A new field at the end of the record, holding a list of the one element.</summary>
    static string AddField(string text, RecordExpressionSyntax record, string field, string element) {
        if (record.Fields.Count == 0) {
            return Replace(text, record.CloseBraceToken.Span, $"{field}: [{element}] }}");
        }

        var last = record.Fields[^1].Span;
        var indent = IndentationAt(text, last.Start);
        var entry = $"{field}: [\n{indent}    {element},\n{indent}]";
        var trailingComma = record.Fields.SeparatorCount == record.Fields.Count;
        return trailingComma
            ? text[..CommaEnd(text, last.End)] + $"\n{indent}{entry}," + text[CommaEnd(text, last.End)..]
            : text[..last.End] + $",\n{indent}{entry}" + text[last.End..];
    }

    static int CommaEnd(string text, int position) {
        var comma = text.IndexOf(',', position);
        return comma < 0 ? position : comma + 1;
    }

    static string IndentationAt(string text, int position) {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;
        var indent = new StringBuilder();
        for (var i = lineStart; i < text.Length && text[i] is ' ' or '\t'; i++) {
            indent.Append(text[i]);
        }

        return indent.ToString();
    }

    static string Replace(string text, TextSpan span, string replacement) => text[..span.Start] + replacement + text[span.End..];

    /// <summary>A Sable string literal.</summary>
    static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
