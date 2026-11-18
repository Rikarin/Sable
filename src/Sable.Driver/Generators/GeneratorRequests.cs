using System.Globalization;
using System.Text;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Driver.Generators;

/// <summary>A declaration that carries a generator's attribute, and the places a message about it can point at.</summary>
/// <param name="Spans">What a location's second part indexes: the declaration's name first, then each field, case and parameter.</param>
sealed record GeneratorItem(SyntaxTree Tree, CompilationUnitSyntax Unit, SyntaxNode Node, AttributeSyntax Attribute, string Name, IReadOnlyList<TextSpan> Spans);

/// <summary>
///     The declarations of a package's files that generators answer to,
///     described as <c>Sable.Generators</c>' reader reads them (docs/08 §8.3),
///     with their types resolved against the package's declarations.
/// </summary>
sealed class GeneratorRequests {
    const char FieldSeparator = '\u001f';
    const char RecordStart = '\u001e';
    const char LineBreak = '\u001d';

    readonly IReadOnlyList<SyntaxTree> _files;
    readonly IReadOnlyList<SyntaxTree> _references;
    Compilation? _compilation;
    bool _compilationFailed;
    readonly Dictionary<TypeSymbol, IReadOnlyList<string>> _conformances = new();
    List<NamedTypeSymbol>? _protocols;

    public GeneratorRequests(IReadOnlyList<SyntaxTree> files, IReadOnlyList<SyntaxTree> references) {
        _files = files;
        _references = references;
    }

    // ---- Finding them -------------------------------------------------------

    /// <summary>
    ///     Every declaration in the file that carries one of the names: types at
    ///     the top level or nested in a type, functions at the top level, and
    ///     methods of types and extensions (docs/08 §8.2). Each comes once for
    ///     each such attribute it has.
    /// </summary>
    public static IEnumerable<(string Name, SyntaxNode Node, AttributeSyntax Attribute, string Container)> Find(CompilationUnitSyntax unit, ISet<string> names) {
        var found = new List<(string, SyntaxNode, AttributeSyntax, string)>();

        void Walk(SyntaxList<SyntaxNode> members, string container) {
            foreach (var member in members) {
                if (TypeParts(member) is { } parts) {
                    Add(member, parts.Attributes, container);
                    Walk(parts.Members, Qualified(container, parts.Identifier.Text));
                } else if (member is FunctionDeclarationSyntax function) {
                    Add(member, function.Attributes, container);
                } else if (member is ExtensionDeclarationSyntax extension) {
                    Walk(extension.Members, extension.Type.ToString().Trim());
                }
            }
        }

        void Add(SyntaxNode node, SyntaxList<AttributeSyntax> attributes, string container) {
            foreach (var attribute in attributes) {
                var name = attribute.Name.ToString().Trim();
                if (names.Contains(name)) {
                    found.Add((name, node, attribute, container));
                }
            }
        }

        Walk(unit.Members, "");
        return found;
    }

    static string Qualified(string container, string name) => container.Length == 0 ? name : $"{container}.{name}";

    /// <summary>What every kind of type declaration has.</summary>
    internal sealed record TypeDeclarationParts(
        string Keyword,
        SyntaxToken Identifier,
        SyntaxList<AttributeSyntax> Attributes,
        SyntaxTokenList Modifiers,
        GenericParameterListSyntax? TypeParameters,
        BaseTypeListSyntax? BaseTypes,
        SyntaxList<SyntaxNode> Members);

    internal static TypeDeclarationParts? TypeParts(SyntaxNode? member) => member switch {
        StructDeclarationSyntax s => new("struct", s.Identifier, s.Attributes, s.Modifiers, s.TypeParameters, s.BaseTypes, s.Members),
        ClassDeclarationSyntax c => new("class", c.Identifier, c.Attributes, c.Modifiers, c.TypeParameters, c.BaseTypes, c.Members),
        ActorDeclarationSyntax a => new("actor", a.Identifier, a.Attributes, a.Modifiers, a.TypeParameters, a.BaseTypes, a.Members),
        UnionDeclarationSyntax u => new("union", u.Identifier, u.Attributes, u.Modifiers, u.TypeParameters, u.BaseTypes, u.Members),
        EnumDeclarationSyntax e => new("enum", e.Identifier, e.Attributes, e.Modifiers, null, e.BaseTypes, e.Members),
        _ => null,
    };

    /// <summary>A declaration's name, for messages.</summary>
    internal static string NameOf(SyntaxNode node) =>
        TypeParts(node)?.Identifier.Text ?? (node as FunctionDeclarationSyntax)?.Identifier.Text ?? "?";

    // ---- Describing them ----------------------------------------------------

    /// <summary>
    ///     A declaration as the reader reads it, as the <paramref name="index" />th
    ///     of its request: its lines, through <c>E</c>, and the places its
    ///     locations name.
    /// </summary>
    public (string Lines, GeneratorItem Item) Describe(int index, SyntaxTree tree, CompilationUnitSyntax unit, SyntaxNode node, AttributeSyntax attribute, string container) {
        var lines = new StringBuilder();
        var spans = new List<TextSpan>();
        var types = new TypeTable(this, lines);

        void Line(params string[] fields) => lines.Append(string.Join(FieldSeparator, fields.Select(Encoded))).Append('\n');

        string Location(TextSpan span) {
            spans.Add(span);
            return $"{index.ToString(CultureInfo.InvariantCulture)}:{(spans.Count - 1).ToString(CultureInfo.InvariantCulture)}";
        }

        void Attributes(SyntaxList<AttributeSyntax> attributes, string target) {
            foreach (var a in attributes) {
                Line("A", target, a.Name.ToString().Trim());
                foreach (var argument in a.ArgumentList?.Arguments ?? default) {
                    var text = argument.Expression.ToString().Trim();
                    var value = GeneratorSet.StringValue(argument.Expression);
                    Line("G", argument.NameToken.IsNone ? "" : argument.NameToken.Text, text, Flag(value is not null), value ?? "");
                }
            }
        }

        var module = unit.Module is { } m ? m.Name.ToString().Trim() : "";
        var symbol = SymbolOf(tree, node);
        if (TypeParts(node) is { } parts) {
            var location = Location(parts.Identifier.Span);
            Line("D", parts.Keyword, parts.Identifier.Text, container, module, Flag(IsPublic(parts.Modifiers)), Flag(IsStatic(parts.Modifiers)), tree.Text.FileName, location);
            if (symbol is NamedTypeSymbol type) {
                foreach (var protocol in ConformancesOf(type)) {
                    Line("X", protocol);
                }

                if (type.TypeKind == TypeKind.Class && Guarded(() => type.BaseTypes.OfType<NamedTypeSymbol>().FirstOrDefault(static b => b.TypeKind == TypeKind.Class)) is { } superclass) {
                    Line("S", superclass.Name);
                }
            }

            Common(parts.TypeParameters, parts.BaseTypes);
            Attributes(parts.Attributes, "d");
            foreach (var member in parts.Members) {
                switch (member) {
                    case FieldDeclarationSyntax field when IsStored(field): {
                        var name = ((BindingPatternSyntax)field.Declarator.Pattern).Identifier.Text;
                        var resolved = symbol is NamedTypeSymbol owner
                            ? Guarded(() => owner.Members.OfType<FieldSymbol>().FirstOrDefault(f => f.Name == name)?.Type)
                            : null;
                        var id = types.Add(resolved);
                        Line("F", name, field.Declarator.TypeAnnotation?.Type.ToString().Trim() ?? resolved?.ToDisplayString() ?? "", Flag(field.LetOrVarKeyword.Kind == SyntaxKind.VarKeyword),
                            Flag(field.Declarator.Initializer is not null), Flag(IsPublic(field.Modifiers)), Location(((BindingPatternSyntax)field.Declarator.Pattern).Identifier.Span), Id(id));
                        Attributes(field.Attributes, "f");
                        break;
                    }
                    case UnionCaseDeclarationSyntax unionCase: {
                        var caseSymbol = symbol is NamedTypeSymbol owner
                            ? Guarded(() => owner.Members.OfType<UnionCaseSymbol>().FirstOrDefault(c => c.Name == unionCase.Identifier.Text))
                            : null;
                        var payloadIds = new List<int>();
                        var payload = unionCase.Payload?.Fields.ToList() ?? [];
                        for (var i = 0; i < payload.Count; i++) {
                            var at = i;
                            payloadIds.Add(types.Add(Guarded(() => caseSymbol is { } c && at < c.Payload.Length ? c.Payload[at].Type : null)));
                        }

                        Line("K", unionCase.Identifier.Text, "0", "", Location(unionCase.Identifier.Span));
                        Attributes(unionCase.Attributes, "c");
                        for (var i = 0; i < payload.Count; i++) {
                            Line("P", payload[i].NameToken.IsNone ? "" : payload[i].NameToken.Text, payload[i].Type.ToString().Trim(), Id(payloadIds[i]));
                        }

                        break;
                    }
                    case EnumCaseDeclarationSyntax enumCase:
                        Line("K", enumCase.Identifier.Text, Flag(enumCase.Value is not null), enumCase.Value?.ToString().Trim() ?? "", Location(enumCase.Identifier.Span));
                        Attributes(enumCase.Attributes, "c");
                        break;
                }
            }
        } else if (node is FunctionDeclarationSyntax function) {
            var location = Location(function.Identifier.Span);
            Line("D", "fn", function.Identifier.Text, container, module, Flag(IsPublic(function.Modifiers)), Flag(IsStatic(function.Modifiers)), tree.Text.FileName, location);
            var functionSymbol = symbol as FunctionSymbol;
            var returned = functionSymbol is { } f ? Guarded(() => f.ReturnType) : null;
            var returnId = function.ReturnClause is null || returned is null || returned.Name == "Void" ? -1 : types.Add(returned);
            Line("V", Flag(function.AsyncKeyword.Kind != SyntaxKind.None && !function.AsyncKeyword.IsNone), Flag(function.ThrowsSpec is not null),
                function.ReturnClause?.Type.ToString().Trim() ?? "", Id(returnId));
            Common(function.TypeParameters, null);
            Attributes(function.Attributes, "d");
            var parameters = function.Parameters.Parameters.ToList();
            for (var i = 0; i < parameters.Count; i++) {
                var parameter = parameters[i];
                var at = i;
                var id = types.Add(Guarded(() => functionSymbol is { } fs && at < fs.Parameters.Length ? fs.Parameters[at].Type : null));
                var label = parameter.Label.IsNone ? parameter.Identifier.Text : parameter.Label.Text == "_" ? "" : parameter.Label.Text;
                Line("Q", label, parameter.Identifier.Text, parameter.Type.ToString().Trim(), Flag(parameter.DefaultValue is not null), Location(parameter.Identifier.Span), Id(id));
                Attributes(parameter.Attributes, "q");
            }
        }

        Line("E");
        return (lines.ToString(), new(tree, unit, node, attribute, NameOf(node), spans));

        void Common(GenericParameterListSyntax? typeParameters, BaseTypeListSyntax? baseTypes) {
            foreach (var import in unit.Imports) {
                Line("I", import.Name.ToString().Trim());
            }

            foreach (var parameter in typeParameters?.Parameters ?? default) {
                Line("T", parameter.Identifier.Text);
            }

            foreach (var conformance in baseTypes?.Types ?? default) {
                Line("C", conformance.ToString().Trim());
            }
        }
    }

    static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>A field with storage: not static, and with no accessors of its own.</summary>
    static bool IsStored(FieldDeclarationSyntax field) =>
        field.Accessors is null
        && field.Declarator.Pattern is BindingPatternSyntax
        && !IsStatic(field.Modifiers);

    static bool IsPublic(SyntaxTokenList modifiers) => modifiers.Any(static m => m.Kind == SyntaxKind.PublicKeyword);

    static bool IsStatic(SyntaxTokenList modifiers) => modifiers.Any(static m => m.Kind == SyntaxKind.StaticKeyword);

    static string Flag(bool value) => value ? "1" : "0";

    /// <summary>A value as one field of one line: line breaks as U+001D, and the separators the protocol uses taken out.</summary>
    internal static string Encoded(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace(FieldSeparator, ' ')
            .Replace(RecordStart, ' ')
            .Replace('\n', LineBreak);

    // ---- Resolving them -----------------------------------------------------

    /// <summary>
    ///     The package's declarations bound, without their bodies: what resolves
    ///     a declaration's types. Null when it cannot be built; the types are
    ///     then unresolved, and the build reports why.
    /// </summary>
    Compilation? Semantic {
        get {
            if (_compilation is null && !_compilationFailed) {
                try {
                    IReadOnlyList<SyntaxTree> program = [.._files, .._references];
                    _compilation = Compilation.Create([..program, ..Library.For(program)]);
                    // Every declaration bound — an extension's conformances
                    // among them — but no body.
                    _ = _compilation.GetDiagnostics();
                } catch (Exception e) when (e is not OutOfMemoryException) {
                    _compilationFailed = true;
                }
            }

            return _compilation;
        }
    }

    Symbol? SymbolOf(SyntaxTree tree, SyntaxNode node) =>
        Semantic is { } compilation ? Guarded(() => compilation.GetSemanticModel(tree).GetSymbolOf(node)) : null;

    /// <summary>What the compiler can say; nothing, when asking it fails on a program that does not compile yet.</summary>
    static T? Guarded<T>(Func<T?> ask) where T : class {
        try {
            return ask();
        } catch (Exception e) when (e is not OutOfMemoryException) {
            return null;
        }
    }

    /// <summary>Every protocol the type conforms to, by name, in a fixed order.</summary>
    internal IReadOnlyList<string> ConformancesOf(TypeSymbol type) {
        if (_conformances.TryGetValue(type, out var known)) {
            return known;
        }

        if (type is not NamedTypeSymbol { TypeKind: not TypeKind.Protocol and not TypeKind.Error } || Semantic is not { } compilation) {
            return _conformances[type] = [];
        }

        _protocols ??= [
            ..compilation.Modules.SelectMany(static m => m.Members.OfType<NamedTypeSymbol>())
                .Where(static t => t.TypeKind == TypeKind.Protocol && t.TypeParameters.IsEmpty)
                .OrderBy(static t => t.Name, StringComparer.Ordinal),
        ];
        var names = new List<string>();
        foreach (var protocol in _protocols) {
            try {
                if (type.Satisfies(protocol)) {
                    names.Add(protocol.Name);
                }
            } catch (Exception e) when (e is not OutOfMemoryException) {
                // A protocol the compiler cannot decide for this type is left out.
            }
        }

        return _conformances[type] = [..names.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    ///     The resolved types of one declaration, each written once as <c>Y</c>
    ///     lines before what uses it, its arguments before it, so the reader
    ///     can build each from those it has.
    /// </summary>
    sealed class TypeTable(GeneratorRequests owner, StringBuilder lines) {
        readonly Dictionary<TypeSymbol, int> _ids = new();
        int _next;

        /// <summary>The type's id, writing it and what it is made of first; -1 for none.</summary>
        public int Add(TypeSymbol? type, int depth = 0) {
            if (type is null) {
                return -1;
            }

            if (_ids.TryGetValue(type, out var known)) {
                return known;
            }

            var (kind, name, arguments) = Shape(type);
            var argumentIds = depth < 8 ? arguments.Select(a => Add(a, depth + 1)).Where(static i => i >= 0).ToList() : [];
            var id = _next++;
            _ids[type] = id;
            Line("Y", Id(id), kind, name, ModuleOf(type), Guarded(type.ToDisplayString) ?? name);
            foreach (var protocol in owner.ConformancesOf(type)) {
                Line("YC", protocol);
            }

            if (type is NamedTypeSymbol { DeclaringSyntax: [var declaration, ..] }) {
                foreach (var attribute in declaration.ChildNodes().OfType<AttributeSyntax>()) {
                    Line("YA", attribute.Name.ToString().Trim());
                }
            }

            foreach (var argument in argumentIds) {
                Line("YG", Id(argument));
            }

            return id;
        }

        void Line(params string[] fields) => lines.Append(string.Join(FieldSeparator, fields.Select(Encoded))).Append('\n');

        static (string Kind, string Name, IReadOnlyList<TypeSymbol> Arguments) Shape(TypeSymbol type) => type switch {
            OptionalTypeSymbol optional => ("optional", "Optional", [optional.UnderlyingType]),
            ArrayTypeSymbol array => ("array", "Array", [array.ElementType]),
            TupleTypeSymbol tuple => ("tuple", "", [..tuple.ElementTypes]),
            FunctionTypeSymbol => ("function", "", []),
            TypeParameterSymbol parameter => ("parameter", parameter.Name, []),
            ExistentialTypeSymbol existential => ("protocol", existential.Constraint.Name, []),
            NamedTypeSymbol { Name: "Dictionary" } named when ModuleOf(named) == CoreLibrary.ModuleName => ("dictionary", "Dictionary", [..named.TypeArguments]),
            NamedTypeSymbol named => (named.TypeKind switch {
                TypeKind.Struct or TypeKind.Builtin => "struct",
                TypeKind.Class => "class",
                TypeKind.Actor => "actor",
                TypeKind.Protocol => "protocol",
                TypeKind.Union => "union",
                TypeKind.Enum => "enum",
                _ => "unresolved",
            }, named.Name, [..named.TypeArguments]),
            _ => ("unresolved", type.Name, []),
        };

        static string ModuleOf(TypeSymbol type) {
            for (var current = (Symbol?)type; current is not null; current = current.ContainingSymbol) {
                if (current is ModuleSymbol module) {
                    return module.Name;
                }
            }

            return "";
        }
    }
}
