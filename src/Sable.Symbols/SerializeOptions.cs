using System.Text;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     What <c>@serialize(…)</c> asks of a derived <c>Serializable</c>
///     conformance (docs/06 §6.5): on the type, <c>renameAll:</c> a naming
///     policy and, on a union, how a case is tagged — externally by default
///     (<c>{"Circle": {…}}</c>, a case with no payload its bare name),
///     internally (<c>tag: "type"</c>, the tag a member beside the payload's
///     fields) or adjacently (<c>tag: "t", content: "c"</c>) or not at all
///     (<c>untagged</c>: the first case whose payload decodes); on a field or a
///     case, <c>rename:</c> its key, and on a field <c>skip</c> (never
///     written, its initial value on reading) or <c>defaulted</c> (its initial
///     value when the key is missing) or <c>flatten</c> (its members are the
///     enclosing struct's).
/// </summary>
internal sealed class SerializeOptions {
    public string? RenameAll { get; private init; }
    public string? Tag { get; private init; }
    public string? Content { get; private init; }
    public bool Untagged { get; private init; }

    /// <summary>The key a field or a case is written under, and whether it is skipped, defaulted or flattened.</summary>
    public sealed record Member(string Key, bool Skip, bool Default, bool Flatten);

    static readonly string[] Policies = ["CamelCase", "PascalCase", "SnakeCase", "ScreamingSnakeCase", "KebabCase"];

    static IEnumerable<AttributeSyntax> SerializeAttributes(Symbol symbol) =>
        symbol.DeclaringSyntax.SelectMany(Compilation.AttributesOf).Where(static a => a.Name.ToString().Trim() == "serialize");

    static IEnumerable<ArgumentSyntax> Arguments(AttributeSyntax attribute) =>
        attribute.ArgumentList?.Arguments ?? [];

    static string Label(ArgumentSyntax argument) => argument.NameToken.IsMissing ? "" : argument.NameToken.Text;

    static string Value(ArgumentSyntax argument) => argument.Expression.ToString().Trim();

    static string? StringValue(ArgumentSyntax argument) {
        var text = argument.Expression.ToString().Trim();
        return text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : null;
    }

    /// <summary>The type's options.</summary>
    public static SerializeOptions Of(NamedTypeSymbol type) {
        string? renameAll = null, tag = null, content = null;
        var untagged = false;
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
                case "" when Value(argument) == "untagged":
                    untagged = true;
                    break;
            }
        }

        return new SerializeOptions { RenameAll = renameAll, Tag = tag, Content = content, Untagged = untagged };
    }

    /// <summary>A field's or a case's key, after its own rename or the type's policy.</summary>
    public Member MemberOf(Symbol member, string name) {
        string? rename = null;
        bool skip = false, defaulted = false, flatten = false;
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
                case ("", "flatten"):
                    flatten = true;
                    break;
            }
        }

        return new Member(rename ?? Apply(RenameAll, name), skip, defaulted, flatten);
    }

    /// <summary>
    ///     The keys a struct's fields are written under, leaving out
    ///     <paramref name="but" />: a flattened field's own members' where its
    ///     type is a struct that derives them, which is when they are known.
    /// </summary>
    public static IEnumerable<string> Keys(NamedTypeSymbol type, TypeSubstitution substitution, FieldSymbol? but = null) {
        var options = Of(type.OriginalDefinition);
        foreach (var field in DerivedConformance.StoredFields(type.OriginalDefinition)) {
            var member = options.MemberOf(field, field.Name);
            if (member.Skip || ReferenceEquals(field, but)) {
                continue;
            }

            if (!member.Flatten) {
                yield return member.Key;
            } else if (substitution.Apply(field.Type) is NamedTypeSymbol { TypeKind: TypeKind.Struct } inner && Derives(inner)) {
                foreach (var key in Keys(inner, TypeSubstitution.For(inner))) {
                    yield return key;
                }
            }
        }
    }

    static bool Derives(NamedTypeSymbol type) =>
        type.OriginalDefinition.DeclaringSyntax.SelectMany(Compilation.AttributesOf)
            .Any(static a => a.Name.ToString().Trim() == "derive" && Arguments(a).Any(static d => Value(d) == "Serializable"));

    /// <summary>Why a field's type can never be flattened, or null: it is written as no struct or map.</summary>
    static string? Unflattenable(TypeSymbol type) =>
        type switch {
            ArrayTypeSymbol => "an array",
            OptionalTypeSymbol => "an optional, whose none would be no members at all",
            TupleTypeSymbol => "a tuple",
            NamedTypeSymbol { TypeKind: TypeKind.Enum } => "an enum, written as its case's name",
            NamedTypeSymbol { TypeKind: TypeKind.Union } union when Derives(union) && Of(union.OriginalDefinition) is { Tag: null } =>
                "a union without a 'tag:', which may be written as anything but a struct",
            NamedTypeSymbol named when named.OriginalDefinition.ContainingSymbol?.Name == "Sable.Core" && named.Name != "Dictionary" => $"'{named.Name}', a single value",
            _ => null,
        };

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
                } else if (label.Length == 0 && value == "untagged") {
                    if (type.TypeKind != TypeKind.Union) {
                        report(argument.Span, $"'untagged' leaves a union's case without a tag; '{type.Name}' is not a union");
                        ok = false;
                    }
                } else if (label is not ("renameAll" or "tag" or "content")) {
                    report(argument.Span, $"'@serialize' on a type takes 'renameAll:', and on a union 'tag:', 'content:' and 'untagged'; not '{argument}'");
                    ok = false;
                }
            }
        }

        var options = Of(type);
        if (options.Content is not null && options.Tag is null) {
            report(SerializeAttributes(type).First().Span, "'content:' goes with 'tag:': the case's name, and beside it its payload");
            ok = false;
        }

        if (options.Untagged && (options.Tag is not null || options.Content is not null)) {
            report(SerializeAttributes(type).First().Span, "'untagged' leaves the tag out, so there is none for 'tag:' or 'content:' to name");
            ok = false;
        }

        // An untagged case that carries nothing is null, which the first such takes.
        if (options.Untagged) {
            foreach (var second in type.Members.OfType<UnionCaseSymbol>().Where(static c => !c.HasPayload).Skip(1)) {
                report(
                    second.DeclaringSyntax.FirstOrDefault()?.Span ?? SerializeAttributes(type).First().Span,
                    $"an untagged case that carries nothing is written as null, and an earlier one of '{type.Name}' already is, so '{second.Name}' would never be read back"
                );
                ok = false;
            }
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
                } else if (label == "rename" && !isField && options.Untagged) {
                    report(argument.Span, $"an untagged case's name is never written, so '{member.Name}' has nothing to rename");
                    ok = false;
                } else if (label.Length == 0 && value is "flatten" && !isField) {
                    report(argument.Span, "'flatten' is for a field, whose members it writes into the struct's; a case has no members of its own");
                    ok = false;
                } else if (label.Length == 0 && value is "skip" or "defaulted" && !isField) {
                    report(argument.Span, $"'{value}' is for a field; a case is renamed, never skipped");
                    ok = false;
                } else if (!(label == "rename" || label.Length == 0 && value is "skip" or "defaulted" or "flatten")) {
                    report(argument.Span, $"'@serialize' on a {(isField ? "field" : "case")} takes 'rename:'{(isField ? ", 'skip', 'defaulted' and 'flatten'" : "")}; not '{argument}'");
                    ok = false;
                }
            }

            if (member is FieldSymbol flattened && options.MemberOf(flattened, flattened.Name) is { Flatten: true } asks) {
                var at = SerializeAttributes(flattened).First().Span;
                if (asks.Skip || asks.Default || SerializeAttributes(flattened).SelectMany(Arguments).Any(static a => Label(a) == "rename")) {
                    report(at, $"'{flattened.Name}' is flattened, so it has no key of its own: 'flatten' does not go with 'rename:', 'skip' or 'defaulted'");
                    ok = false;
                } else if (Unflattenable(flattened.Type) is { } why) {
                    report(at, $"'{flattened.Name}' cannot be flattened: only what is written as a struct or a map has members to write into '{type.Name}', and it is {why}");
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

        // A flattened field's members are beside the struct's own: no key twice.
        if (ok && type.TypeKind == TypeKind.Struct) {
            var seen = new HashSet<string>();
            var flattens = DerivedConformance.StoredFields(type).FirstOrDefault(f => options.MemberOf(f, f.Name).Flatten);
            if (flattens is not null && Keys(type, TypeSubstitution.Empty).FirstOrDefault(key => !seen.Add(key)) is { } twice) {
                report(SerializeAttributes(flattens).First().Span, $"flattened, '{type.Name}' has the key \"{twice}\" twice; rename one of them");
                ok = false;
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
