using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Sable.Syntax.Generator;

/// <summary>
///     Turns <c>Syntax.xml</c> into the green and red node classes.
/// </summary>
/// <remarks>
///     Node classes are generated rather than hand-written for the reason given in
///     docs/04-implementation-plan.md §0.3: there will eventually be around 180 of
///     them, and hand-writing them guarantees drift between the green tree, the red
///     tree and the visitor.
/// </remarks>
public static partial class SyntaxGenerator {
    public const string GreenFileName = "Syntax.Green.g.cs";
    public const string RedFileName = "Syntax.g.cs";

    private sealed record Field(string Name, string Type, bool Optional) {
        public bool IsToken => Type == "Token";
        public bool IsTokenList => Type == "TokenList";
        public bool IsList => Type.StartsWith("List:", StringComparison.Ordinal);
        public bool IsSeparatedList => Type.StartsWith("SeparatedList:", StringComparison.Ordinal);
        public bool IsNode => !IsToken && !IsTokenList && !IsList && !IsSeparatedList;
        public string ItemType => Type[(Type.IndexOf(':', StringComparison.Ordinal) + 1)..];
        public string ParameterName => char.ToLowerInvariant(Name[0]) + Name[1..];
        public string FieldName => "_" + ParameterName;
    }

    private sealed record Node(string Name, string Base, string Kind, string? Doc, List<Field> Fields);

    private sealed record Abstract(string Name, string Base, string? Doc);

    /// <summary>Generates both output files, keyed by file name.</summary>
    /// <param name="schemaXml">The contents of Syntax.xml.</param>
    /// <param name="syntaxKindSource">
    ///     The contents of SyntaxKind.cs, used to verify that every Kind named in
    ///     the schema actually exists.
    /// </param>
    public static IReadOnlyDictionary<string, string> Generate(
        string schemaXml,
        string? syntaxKindSource = null
    ) {
        var document = XDocument.Parse(schemaXml);
        var root = document.Root
            ?? throw new InvalidOperationException("Syntax.xml has no root element");

        var abstracts = root.Elements("Abstract")
            .Select(static e => new Abstract(Attr(e, "Name"), Attr(e, "Base"), e.Attribute("Doc")?.Value))
            .ToList();

        var nodes = root.Elements("Node").Select(ParseNode).ToList();

        if (syntaxKindSource is not null) {
            VerifyKinds(nodes, syntaxKindSource);
        }

        VerifyBases(nodes, abstracts);

        return new Dictionary<string, string>(StringComparer.Ordinal) {
            [GreenFileName] = GenerateGreen(nodes), [RedFileName] = GenerateRed(nodes, abstracts)
        };
    }

    static Node ParseNode(XElement element) =>
        new(
            Attr(element, "Name"),
            Attr(element, "Base"),
            Attr(element, "Kind"),
            element.Attribute("Doc")?.Value,
            [
                ..element.Elements("Field")
                    .Select(static f => new Field(
                            Attr(f, "Name"),
                            Attr(f, "Type"),
                            string.Equals(f.Attribute("Optional")?.Value, "true", StringComparison.OrdinalIgnoreCase)
                        )
                    )
            ]
        );

    static string Attr(XElement element, string name) =>
        element.Attribute(name)?.Value
        ?? throw new InvalidOperationException($"<{element.Name}> is missing the required '{name}' attribute");

    static void VerifyKinds(List<Node> nodes, string syntaxKindSource) {
        var declared = KindMemberRegex()
            .Matches(syntaxKindSource)
            .Select(static m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var missing = nodes.Where(n => !declared.Contains(n.Kind)).Select(static n => n.Kind).ToList();
        if (missing.Count > 0) {
            throw new InvalidOperationException(
                "Syntax.xml names kinds that do not exist in SyntaxKind.cs: "
                + string.Join(", ", missing)
            );
        }
    }

    static void VerifyBases(List<Node> nodes, List<Abstract> abstracts) {
        var known = abstracts.Select(static a => a.Name)
            .Concat(nodes.Select(static n => n.Name))
            .Append("SyntaxNode")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var node in nodes) {
            if (!known.Contains(node.Base)) {
                throw new InvalidOperationException($"{node.Name} has unknown base '{node.Base}'");
            }

            foreach (var field in node.Fields) {
                if (field.IsToken || field.IsTokenList) {
                    continue;
                }

                var type = field.IsList || field.IsSeparatedList ? field.ItemType : field.Type;
                if (!known.Contains(type)) {
                    throw new InvalidOperationException($"{node.Name}.{field.Name} has unknown type '{type}'");
                }
            }
        }
    }

    // The comma is optional: a formatter may drop it after the last member.
    [GeneratedRegex(@"^\s{4}(\w+) = \d+,?", RegexOptions.Multiline)]
    private static partial Regex KindMemberRegex();

    // -----------------------------------------------------------------------
    // Green nodes
    // -----------------------------------------------------------------------

    static string GenerateGreen(List<Node> nodes) {
        var sb = new StringBuilder();
        WriteHeader(sb);
        sb.AppendLine("namespace Sable.Syntax.Green;");
        sb.AppendLine();

        foreach (var node in nodes) {
            var green = GreenName(node.Name);
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"/// <summary>Green node for <see cref=\"{node.Name}\"/>.</summary>"
            );
            sb.AppendLine(CultureInfo.InvariantCulture, $"internal sealed class {green} : GreenNode");
            sb.AppendLine("{");

            foreach (var field in node.Fields) {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"    private readonly {GreenFieldType(field)} {field.FieldName};"
                );
            }

            if (node.Fields.Count > 0) {
                sb.AppendLine();
            }

            var parameters = string.Join(
                ", ",
                node.Fields.Select(static f => $"{GreenFieldType(f)} {f.ParameterName}")
            );
            sb.AppendLine(CultureInfo.InvariantCulture, $"    public {green}({parameters})");
            sb.AppendLine(CultureInfo.InvariantCulture, $"        : base(SyntaxKind.{node.Kind}, 0)");
            sb.AppendLine("    {");
            foreach (var field in node.Fields) {
                sb.AppendLine(CultureInfo.InvariantCulture, $"        {field.FieldName} = {field.ParameterName};");
            }

            if (node.Fields.Count > 0) {
                var width = string.Join(
                    "\n                    + ",
                    node.Fields.Select(static f => $"({f.ParameterName}?.FullWidth ?? 0)")
                );
                sb.AppendLine(CultureInfo.InvariantCulture, $"        FullWidth = {width};");
            }

            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine(CultureInfo.InvariantCulture, $"    public override int SlotCount => {node.Fields.Count};");
            sb.AppendLine();
            sb.AppendLine("    public override GreenNode? GetSlot(int index) => index switch");
            sb.AppendLine("    {");
            for (var i = 0; i < node.Fields.Count; i++) {
                sb.AppendLine(CultureInfo.InvariantCulture, $"        {i} => {node.Fields[i].FieldName},");
            }

            sb.AppendLine("        _ => null,");
            sb.AppendLine("    };");
            sb.AppendLine();
            sb.AppendLine("    public override SyntaxNode CreateRed(SyntaxNode? parent, int position) =>");
            sb.AppendLine(CultureInfo.InvariantCulture, $"        new {node.Name}(this, parent, position);");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    static string GreenFieldType(Field field) =>
        field.IsToken ? "GreenToken?"
        : field.IsTokenList || field.IsList || field.IsSeparatedList ? "GreenList?"
        : "GreenNode?";

    static string GreenName(string nodeName) =>
        nodeName.EndsWith("Syntax", StringComparison.Ordinal)
            ? nodeName[..^"Syntax".Length] + "Green"
            : nodeName + "Green";

    // -----------------------------------------------------------------------
    // Red nodes
    // -----------------------------------------------------------------------

    static string GenerateRed(List<Node> nodes, List<Abstract> abstracts) {
        var sb = new StringBuilder();
        WriteHeader(sb);
        sb.AppendLine("using Sable.Syntax.Green;");
        sb.AppendLine();
        sb.AppendLine("namespace Sable.Syntax;");
        sb.AppendLine();

        foreach (var node in abstracts) {
            if (node.Doc is not null) {
                sb.AppendLine(CultureInfo.InvariantCulture, $"/// <summary>{Escape(node.Doc)}</summary>");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"public abstract class {node.Name} : {node.Base}");
            sb.AppendLine("{");
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"    private protected {node.Name}(GreenNode green, SyntaxNode? parent, int position)"
            );
            sb.AppendLine("        : base(green, parent, position) { }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        foreach (var node in nodes) {
            if (node.Doc is not null) {
                sb.AppendLine(CultureInfo.InvariantCulture, $"/// <summary>{Escape(node.Doc)}</summary>");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"public sealed class {node.Name} : {node.Base}");
            sb.AppendLine("{");

            var cached = node.Fields
                .Select(static (f, i) => (Field: f, Index: i))
                .Where(static x => x.Field.IsNode)
                .ToList();

            foreach (var (field, _) in cached) {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    private {field.Type}? {field.FieldName};");
            }

            if (cached.Count > 0) {
                sb.AppendLine();
            }

            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"    internal {node.Name}(GreenNode green, SyntaxNode? parent, int position)"
            );
            sb.AppendLine("        : base(green, parent, position) { }");
            sb.AppendLine();

            for (var i = 0; i < node.Fields.Count; i++) {
                var field = node.Fields[i];
                if (field.IsToken) {
                    sb.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"    public SyntaxToken {field.Name} => GetToken({i});"
                    );
                } else if (field.IsTokenList) {
                    sb.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"    public SyntaxTokenList {field.Name} => GetTokenList({i});"
                    );
                } else if (field.IsSeparatedList) {
                    sb.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"    public SeparatedSyntaxList<{field.ItemType}> {field.Name} => GetSeparatedList<{field.ItemType}>({i});"
                    );
                } else if (field.IsList) {
                    sb.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"    public SyntaxList<{field.ItemType}> {field.Name} => GetList<{field.ItemType}>({i});"
                    );
                } else if (field.Optional) {
                    sb.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"    public {field.Type}? {field.Name} => GetRedOptional(ref {field.FieldName}, {i});"
                    );
                } else {
                    sb.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"    public {field.Type} {field.Name} => GetRed(ref {field.FieldName}, {i});"
                    );
                }
            }

            sb.AppendLine();

            sb.AppendLine("    internal override SyntaxNode? GetNodeSlot(int index) => index switch");
            sb.AppendLine("    {");
            foreach (var (field, index) in cached) {
                sb.AppendLine(CultureInfo.InvariantCulture, $"        {index} => {field.Name},");
            }

            sb.AppendLine("        _ => null,");
            sb.AppendLine("    };");
            sb.AppendLine();
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"    public override void Accept(SyntaxVisitor visitor) => visitor.Visit{VisitName(node.Name)}(this);"
            );
            sb.AppendLine("}");
            sb.AppendLine();
        }

        // Visitor
        sb.AppendLine("/// <summary>A depth-first visitor over the syntax tree.</summary>");
        sb.AppendLine("public abstract class SyntaxVisitor");
        sb.AppendLine("{");
        sb.AppendLine("    public virtual void Visit(SyntaxNode? node) => node?.Accept(this);");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Visits every child node. Called by any Visit method not overridden.</summary>");
        sb.AppendLine("    public virtual void DefaultVisit(SyntaxNode node)");
        sb.AppendLine("    {");
        sb.AppendLine("        foreach (var child in node.ChildNodes()) Visit(child);");
        sb.AppendLine("    }");
        sb.AppendLine();
        foreach (var node in nodes) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"    public virtual void Visit{VisitName(node.Name)}({node.Name} node) => DefaultVisit(node);"
            );
        }

        sb.AppendLine("}");
        sb.AppendLine();

        return sb.ToString();
    }

    static string VisitName(string nodeName) =>
        nodeName.EndsWith("Syntax", StringComparison.Ordinal)
            ? nodeName[..^"Syntax".Length]
            : nodeName;

    /// <summary>Re-escapes decoded attribute text for an XML doc comment.</summary>
    static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    static void WriteHeader(StringBuilder sb) {
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("//");
        sb.AppendLine("// Generated from src/Sable.Syntax/Syntax.xml by tools/Sable.Syntax.Generator.");
        sb.AppendLine("// Do not edit by hand: change the schema and run");
        sb.AppendLine("//");
        sb.AppendLine("//     dotnet run --project tools/Sable.Syntax.Generator");
        sb.AppendLine("//");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
    }
}
