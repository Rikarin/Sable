using System.Text;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     What <c>@serialize(…)</c> asks of a derived <c>Serializable</c>
///     conformance (docs/06 §6.5): on the type, <c>renameAll:</c> a naming
///     policy and, on a union, how a case is tagged — externally by default
///     (<c>{"Circle": {…}}</c>, a case with no payload its bare name),
///     internally (<c>tag: "type"</c>, the tag a member beside the payload's
///     fields) or adjacently (<c>tag: "t", content: "c"</c>); on a field or a
///     case, <c>rename:</c> its key, and on a field <c>skip</c> (never
///     written, its initial value on reading) or <c>defaulted</c> (its initial
///     value when the key is missing).
/// </summary>
internal sealed class SerializeOptions {
    public string? RenameAll { get; private init; }
    public string? Tag { get; private init; }
    public string? Content { get; private init; }

    /// <summary>The key a field or a case is written under, and whether it is skipped or defaulted.</summary>
    public sealed record Member(string Key, bool Skip, bool Default);

    static readonly string[] Policies = ["CamelCase", "PascalCase", "SnakeCase", "ScreamingSnakeCase", "KebabCase"];

    static IEnumerable<AttributeSyntax> SerializeAttributes(Symbol symbol) =>
        symbol.DeclaringSyntax.SelectMany(Compilation.AttributesOf).Where(static a => a.Name.ToString().Trim() == "serialize");

    static IEnumerable<ArgumentSyntax> Arguments(AttributeSyntax attribute) =>
        attribute.ArgumentList?.Arguments ?? [];

    static string Label(ArgumentSyntax argument) => argument.NameToken.IsMissing ? "" : argument.NameToken.Text;

    static string? StringValue(ArgumentSyntax argument) {
        var text = argument.Expression.ToString().Trim();
        return text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : null;
    }

    /// <summary>The type's options.</summary>
    public static SerializeOptions Of(NamedTypeSymbol type) {
        string? renameAll = null, tag = null, content = null;
        foreach (var argument in SerializeAttributes(type).SelectMany(Arguments)) {
            switch (Label(argument)) {
                case "renameAll":
                    renameAll = argument.Expression.ToString().Trim().TrimStart('.');
                    break;
                case "tag":
                    tag = StringValue(argument);
                    break;
                case "content":
                    content = StringValue(argument);
                    break;
            }
        }

        return new SerializeOptions { RenameAll = renameAll, Tag = tag, Content = content };
    }

    /// <summary>A field's or a case's key, after its own rename or the type's policy.</summary>
    public Member MemberOf(Symbol member, string name) {
        string? rename = null;
        bool skip = false, defaulted = false;
        foreach (var argument in SerializeAttributes(member).SelectMany(Arguments)) {
            switch (Label(argument), argument.Expression.ToString().Trim()) {
                case ("rename", _):
                    rename = StringValue(argument);
                    break;
                case ("", "skip"):
                    skip = true;
                    break;
                case ("", "defaulted"):
                    defaulted = true;
                    break;
            }
        }

        return new Member(rename ?? Apply(RenameAll, name), skip, defaulted);
    }

    /// <summary>Checks the options on the type and its members, reporting what is wrong; false when anything was.</summary>
    public static bool Check(NamedTypeSymbol type, Action<TextSpan, string> report) {
        var ok = true;
        foreach (var attribute in SerializeAttributes(type)) {
            foreach (var argument in Arguments(attribute)) {
                var label = Label(argument);
                var value = argument.Expression.ToString().Trim();
                if (label == "renameAll" && !Policies.Contains(value.TrimStart('.'))) {
                    report(argument.Span, $"'renameAll' is one of .{string.Join(", .", Policies)}, not '{value}'");
                    ok = false;
                } else if (label is "tag" or "content" && type.TypeKind != TypeKind.Union) {
                    report(argument.Span, $"'{label}' says how a union's case is tagged; '{type.Name}' is not a union");
                    ok = false;
                } else if (label is "tag" or "content" && StringValue(argument) is null) {
                    report(argument.Span, $"'{label}' is a key, written as a string literal");
                    ok = false;
                } else if (label is not ("renameAll" or "tag" or "content")) {
                    report(argument.Span, $"'@serialize' on a type takes 'renameAll:', and on a union 'tag:' and 'content:'; not '{argument}'");
                    ok = false;
                }
            }
        }

        var options = Of(type);
        if (options.Content is not null && options.Tag is null) {
            report(SerializeAttributes(type).First().Span, "'content:' goes with 'tag:': the case's name, and beside it its payload");
            ok = false;
        }

        // An internally tagged case's payload is members beside the tag: each named.
        if (options.Tag is not null && options.Content is null) {
            foreach (var unionCase in type.Members.OfType<UnionCaseSymbol>().Where(static c => c.Payload.Any(static p => p.Label is null))) {
                report(
                    unionCase.DeclaringSyntax.FirstOrDefault()?.Span ?? SerializeAttributes(type).First().Span,
                    $"an internally tagged case's payload is written as members beside the tag, so each needs a label; '{unionCase.Name}' has one without"
                );
                ok = false;
            }
        }

        var members = type.TypeKind switch {
            TypeKind.Union => type.Members.OfType<UnionCaseSymbol>().Cast<Symbol>(),
            TypeKind.Enum => type.Members.OfType<EnumCaseSymbol>().Cast<Symbol>(),
            _ => DerivedConformance.StoredFields(type).Cast<Symbol>(),
        };
        foreach (var member in members) {
            foreach (var argument in SerializeAttributes(member).SelectMany(Arguments)) {
                var label = Label(argument);
                var value = argument.Expression.ToString().Trim();
                var isField = member is FieldSymbol;
                if (label == "rename" && StringValue(argument) is null) {
                    report(argument.Span, "'rename' is a key, written as a string literal");
                    ok = false;
                } else if (label.Length == 0 && value is "skip" or "defaulted" && !isField) {
                    report(argument.Span, $"'{value}' is for a field; a case is renamed, never skipped");
                    ok = false;
                } else if (!(label == "rename" || label.Length == 0 && value is "skip" or "defaulted")) {
                    report(argument.Span, $"'@serialize' on a {(isField ? "field" : "case")} takes 'rename:'{(isField ? ", 'skip' and 'defaulted'" : "")}; not '{argument}'");
                    ok = false;
                }
            }

            // A field skipped or defaulted is given its initial value, so it
            // needs one — an optional's is none.
            if (member is FieldSymbol field && !field.HasInitializer && field.Type is not OptionalTypeSymbol) {
                var asked = options.MemberOf(field, field.Name);
                if (asked.Skip || asked.Default) {
                    report(
                        field.DeclaringSyntax.FirstOrDefault()?.Span ?? SerializeAttributes(field).First().Span,
                        $"'{field.Name}' is {(asked.Skip ? "skipped" : "defaulted")}, so it is given its initial value, and it has none: write one, as in 'var {field.Name}: … = …'"
                    );
                    ok = false;
                }
            }
        }

        return ok;
    }

    /// <summary>A lowerCamelCase or UpperCamelCase name, as the policy writes it: Sable.Serialize's NamingPolicy.apply.</summary>
    static string Apply(string? policy, string name) {
        if (policy is null) {
            return name;
        }

        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in name) {
            if (char.IsAsciiLetterUpper(c) && current.Length > 0) {
                words.Add(current.ToString());
                current.Clear();
            }

            current.Append(c);
        }

        if (current.Length > 0) {
            words.Add(current.ToString());
        }

        var lower = words.Select(static w => w.ToLowerInvariant()).ToList();
        static string Capital(string w) => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..];
        return policy switch {
            "CamelCase" => string.Concat(lower.Select((w, i) => i == 0 ? w : Capital(w))),
            "PascalCase" => string.Concat(lower.Select(Capital)),
            "SnakeCase" => string.Join("_", lower),
            "ScreamingSnakeCase" => string.Join("_", lower).ToUpperInvariant(),
            "KebabCase" => string.Join("-", lower),
            _ => name,
        };
    }
}
