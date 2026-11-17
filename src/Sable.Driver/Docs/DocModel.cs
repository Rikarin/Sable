using System.Text;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Driver.Docs;

/// <summary>One documented declaration: a type, a member, a function, a value.</summary>
public sealed class DocEntry {
    /// <summary>
    ///     Unique among the set: the module, then each name below it after a
    ///     <c>/</c> (<c>Sable.Collections/Stack/push</c>), with <c>-2</c>,
    ///     <c>-3</c>… for overloads.
    /// </summary>
    public required string Id { get; init; }

    /// <summary><c>struct</c>, <c>class</c>, <c>fn</c>, <c>init</c>, <c>var</c>, <c>case</c>…</summary>
    public required string Kind { get; init; }

    public required string Name { get; init; }

    /// <summary>The module whose source declares it.</summary>
    public required string Module { get; init; }

    /// <summary>The declaration as a reader writes against it, rendered from its symbol.</summary>
    public required string Signature { get; init; }

    /// <summary>Its <c>///</c> comment, without the slashes.</summary>
    public string Doc { get; init; } = "";

    /// <summary>The type it is a member of, if any.</summary>
    public DocEntry? Parent { get; init; }

    public List<DocEntry> Members { get; } = [];

    /// <summary>For a type: the types it inherits from or conforms to, as written.</summary>
    public List<string> Supertypes { get; } = [];

    /// <summary>Names in the signature that are type parameters here, which link nowhere.</summary>
    public HashSet<string> TypeParameters { get; } = new(StringComparer.Ordinal);

    public required Symbol Symbol { get; init; }

    public bool IsType => Symbol is NamedTypeSymbol;

    /// <summary>The anchor within its type's page: its name, and a number for an overload.</summary>
    public string Anchor => Sanitise(Id[(Id.LastIndexOf('/') + 1)..]);

    /// <summary>A name as an HTML id that needs no escaping: an operator's characters spelled in hex.</summary>
    public static string Sanitise(string name) {
        var text = new StringBuilder();
        foreach (var c in name) {
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '-') {
                text.Append(c);
            } else {
                text.Append('_').Append(((int)c).ToString("x", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return text.ToString();
    }

    public override string ToString() => Id;
}

/// <summary>A module's documentation: its own comment, types, functions, values, and what it adds to other modules' types.</summary>
public sealed class DocModule {
    public required string Name { get; init; }
    public string Doc { get; set; } = "";
    public List<DocEntry> Types { get; } = [];
    public List<DocEntry> Functions { get; } = [];
    public List<DocEntry> Values { get; } = [];

    /// <summary>Members this module's extensions add to types of other modules, by type.</summary>
    public List<(NamedTypeSymbol Type, List<DocEntry> Members)> Extensions { get; } = [];
}

/// <summary>
///     The documentation of some modules (docs/05 §5.1, <c>sable doc</c>):
///     every public declaration in them, with its signature rendered from
///     its symbol and its <c>///</c> comment, gathered from a compilation.
/// </summary>
public sealed class DocSet {
    public List<DocModule> Modules { get; } = [];

    /// <summary>Every entry, by id.</summary>
    public Dictionary<string, DocEntry> Entries { get; } = new(StringComparer.Ordinal);

    /// <summary>Documented types by simple name, for linking the names in signatures and comments.</summary>
    public Dictionary<string, List<DocEntry>> TypesByName { get; } = new(StringComparer.Ordinal);

    /// <summary>The type a name in a signature refers to, preferring one in the given module.</summary>
    public DocEntry? ResolveType(string name, string module) =>
        TypesByName.TryGetValue(name, out var candidates)
            ? candidates.FirstOrDefault(c => c.Module == module) ?? candidates[0]
            : null;

    /// <summary>Documents the named modules of a compilation.</summary>
    public static DocSet Build(Compilation compilation, IEnumerable<string> moduleNames) {
        // Signatures bind lazily: constraints, and the conformances an
        // extension adds, exist once everything has been bound.
        _ = compilation.GetDiagnostics();

        var builder = new Builder(compilation);
        var set = new DocSet();
        foreach (var name in moduleNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) {
            if (compilation.GetModule(name) is { } module) {
                set.Modules.Add(builder.Module(module, set));
            }
        }

        // Extensions last: a member's own type may be in a module documented after its extender.
        foreach (var module in set.Modules) {
            builder.Extensions(module, set);
        }

        return set;
    }

    private sealed class Builder(Compilation compilation) {
        readonly Dictionary<SyntaxNode, string> _moduleOfRoot = compilation.SyntaxTrees
            .Where(t => t.Root is CompilationUnitSyntax { Module: not null })
            .ToDictionary(static t => t.Root, t => compilation.GetFileScope(t).Module.Name);

        /// <summary>The module whose source declares a symbol, or null for one with no source.</summary>
        public string? DeclaringModule(Symbol symbol) {
            if (symbol.DeclaringSyntax.IsEmpty) {
                return null;
            }

            var node = symbol.DeclaringSyntax[0];
            while (node.Parent is { } parent) {
                node = parent;
            }

            return _moduleOfRoot.GetValueOrDefault(node);
        }

        public DocModule Module(ModuleSymbol module, DocSet set) {
            var doc = new DocModule { Name = module.Name, Doc = ModuleDoc(module.Name) };
            foreach (var member in module.Members.Where(IsPublic)) {
                switch (member) {
                    case NamedTypeSymbol type when type.DeclaringSyntax.IsEmpty && !type.Members.Any(m => IsPublic(m) && DeclaringModule(m) == module.Name):
                        // A built-in type nothing here adds to.
                        break;
                    case NamedTypeSymbol type:
                        doc.Types.Add(Type(type, module.Name, null, set));
                        break;
                    case FunctionSymbol function:
                        doc.Functions.Add(Add(set, Function(function, module.Name, null, Id(module.Name, function.Name, set))));
                        break;
                    case FieldSymbol or AliasSymbol:
                        doc.Values.Add(Add(set, Member(member, module.Name, null, Id(module.Name, member.Name, set))!));
                        break;
                }
            }

            return doc;
        }

        public void Extensions(DocModule doc, DocSet set) {
            // Every type a member declared in this module belongs to, when that type is another module's.
            var byType = new Dictionary<NamedTypeSymbol, List<DocEntry>>();
            foreach (var type in compilation.Modules.SelectMany(static m => m.Members).OfType<NamedTypeSymbol>()) {
                if (type.ContainingModule?.Name == doc.Name) {
                    continue;
                }

                foreach (var member in type.Members.Where(m => IsPublic(m) && !m.IsSynthesized && DeclaringModule(m) == doc.Name)) {
                    if (!byType.TryGetValue(type, out var members)) {
                        byType[type] = members = [];
                        doc.Extensions.Add((type, members));
                    }

                    var id = Id($"{doc.Name}/{type.Name}", member.Name, set);
                    if (Member(member, doc.Name, null, id) is { } entry) {
                        members.Add(Add(set, entry));
                    }
                }
            }
        }

        static DocEntry Add(DocSet set, DocEntry entry) {
            set.Entries[entry.Id] = entry;
            return entry;
        }

        /// <summary>A fresh id under a parent: the name, numbered from 2 for an overload.</summary>
        static string Id(string parent, string name, DocSet set) {
            var id = $"{parent}/{name}";
            for (var n = 2; set.Entries.ContainsKey(id); n++) {
                id = $"{parent}/{name}-{n}";
            }

            return id;
        }

        DocEntry Type(NamedTypeSymbol type, string module, DocEntry? parent, DocSet set) {
            var entry = new DocEntry {
                Id = Id(parent?.Id ?? module, type.Name, set),
                Kind = KindOf(type),
                Name = type.Name,
                Module = module,
                Signature = TypeSignature(type),
                Doc = DocComment(type),
                Parent = parent,
                Symbol = type,
            };
            Add(set, entry);
            entry.TypeParameters.UnionWith(type.TypeParameters.Select(static p => p.Name));
            entry.TypeParameters.Add("Self");
            entry.Supertypes.AddRange(Supertypes(type));

            if (!set.TypesByName.TryGetValue(type.Name, out var named)) {
                set.TypesByName[type.Name] = named = [];
            }

            named.Add(entry);

            foreach (var associated in type.AssociatedTypes) {
                var constraints = associated.Constraints.IsEmpty ? "" : ": " + string.Join(" & ", associated.Constraints.Select(static c => c.ToDisplayString()));
                entry.Members.Add(Add(set, new DocEntry {
                    Id = Id(entry.Id, associated.Name, set),
                    Kind = "associatedtype",
                    Name = associated.Name,
                    Module = module,
                    Signature = $"associatedtype {associated.Name}{constraints}",
                    Doc = DocComment(associated),
                    Parent = entry,
                    Symbol = associated,
                }));
                entry.TypeParameters.Add(associated.Name);
            }

            var isProtocol = type.TypeKind == TypeKind.Protocol;
            foreach (var member in type.Members) {
                if (member.IsSynthesized || (!isProtocol && !IsPublic(member))) {
                    continue;
                }

                // Members another module's extension adds are that module's to document.
                if (DeclaringModule(member) is { } declaredIn && declaredIn != module) {
                    continue;
                }

                if (member is NamedTypeSymbol nested) {
                    entry.Members.Add(Type(nested, module, entry, set));
                } else if (Member(member, module, entry, Id(entry.Id, member.Name, set)) is { } child) {
                    entry.Members.Add(Add(set, child));
                }
            }

            return entry;
        }

        DocEntry? Member(Symbol member, string module, DocEntry? parent, string id) {
            return member switch {
                FunctionSymbol { FunctionKind: FunctionKind.Accessor or FunctionKind.Deinitializer } => null,
                FunctionSymbol function => Function(function, module, parent, id),
                FieldSymbol field => Entry(field, field.IsMutable ? "var" : "let", FieldSignature(field, InProtocol(parent))),
                AliasSymbol alias => Entry(alias, "alias", $"alias {alias.Name}{TypeParameterList(alias.TypeParameters)} = {alias.AliasedType.ToDisplayString()}"),
                UnionCaseSymbol unionCase => Entry(unionCase, "case", unionCase.HasPayload ? $"case {unionCase.Name}({string.Join(", ", unionCase.Payload.Select(Parameter))})" : $"case {unionCase.Name}"),
                EnumCaseSymbol enumCase => Entry(enumCase, "case", $"case {enumCase.Name}"),
                _ => null,
            };

            DocEntry Entry(Symbol symbol, string kind, string signature) => new() {
                Id = id,
                Kind = kind,
                Name = symbol.Name,
                Module = module,
                Signature = signature,
                Doc = DocComment(symbol),
                Parent = parent,
                Symbol = symbol,
            };
        }

        DocEntry Function(FunctionSymbol function, string module, DocEntry? parent, string id) {
            var entry = new DocEntry {
                Id = id,
                Kind = function.FunctionKind switch {
                    FunctionKind.Initializer => "init",
                    FunctionKind.Operator => "operator",
                    FunctionKind.Subscript => "subscript",
                    _ => "fn",
                },
                Name = function.Name,
                Module = module,
                Signature = FunctionSignature(function, InProtocol(parent)),
                Doc = DocComment(function),
                Parent = parent,
                Symbol = function,
            };
            entry.TypeParameters.UnionWith(function.TypeParameters.Select(static p => p.Name));
            if (parent is not null) {
                entry.TypeParameters.UnionWith(parent.TypeParameters);
            }

            return entry;
        }

        // ---- Signatures ----------------------------------------------------

        static string KindOf(NamedTypeSymbol type) =>
            type.TypeKind switch {
                TypeKind.Struct => "struct",
                TypeKind.Class => "class",
                TypeKind.Actor => "actor",
                TypeKind.Protocol => "protocol",
                TypeKind.Union => "union",
                TypeKind.Enum => "enum",
                _ => "type",
            };

        static string TypeSignature(NamedTypeSymbol type) {
            var text = new StringBuilder("public ");
            if (type.IsOpen) {
                text.Append("open ");
            }

            if (type.IsAbstract) {
                text.Append("abstract ");
            }

            text.Append(type.TypeKind == TypeKind.Builtin ? "struct" : KindOf(type)).Append(' ').Append(type.Name);
            if (type.IsVariableSize) {
                text.Append("..");
            }

            text.Append(TypeParameterList(type.TypeParameters));
            var supertypes = Supertypes(type);
            if (supertypes.Count > 0) {
                text.Append(": ").Append(string.Join(", ", supertypes));
            }

            return text.ToString();
        }

        static List<string> Supertypes(NamedTypeSymbol type) =>
            [..type.BaseTypes.Concat(type.DerivedConformances).Select(static t => t.ToDisplayString()).Distinct(StringComparer.Ordinal)];

        static string TypeParameterList(IReadOnlyList<TypeParameterSymbol> parameters) =>
            parameters.Count == 0
                ? ""
                : "<" + string.Join(", ", parameters.Select(static p =>
                    p.Constraints.IsEmpty ? p.Name : $"{p.Name}: {string.Join(" & ", p.Constraints.Select(static c => c.ToDisplayString()))}")) + ">";

        /// <summary>A protocol's requirements are as visible as it is, and say nothing about it.</summary>
        static bool InProtocol(DocEntry? parent) => parent?.Symbol is NamedTypeSymbol { TypeKind: TypeKind.Protocol };

        string FunctionSignature(FunctionSymbol function, bool requirement) {
            var text = new StringBuilder(requirement ? "" : "public ");
            if (function.IsStatic && function.FunctionKind != FunctionKind.Operator) {
                text.Append("static ");
            }

            if (function.IsMutating) {
                text.Append("mutating ");
            }

            switch (function.FunctionKind) {
                case FunctionKind.Initializer:
                    text.Append("init");
                    break;
                case FunctionKind.Subscript:
                    text.Append("subscript");
                    break;
                case FunctionKind.Operator:
                    if (function.Fixity != OperatorFixity.Infix) {
                        text.Append(function.Fixity.ToString().ToLowerInvariant()).Append(' ');
                    }

                    text.Append("operator ").Append(function.Name).Append(' ');
                    break;
                default:
                    text.Append("fn ").Append(function.Name);
                    break;
            }

            text.Append(TypeParameterList(function.TypeParameters));
            text.Append('(').Append(string.Join(", ", function.Parameters.Select(Parameter))).Append(')');
            if (function.IsAsync) {
                text.Append(" async");
            }

            if (function.IsRethrows) {
                text.Append(" rethrows");
            } else if (function.ThrownType is { } thrown) {
                text.Append(thrown == compilation.Core.AnyError ? " throws" : $" throws({thrown.ToDisplayString()})");
            }

            if (function.FunctionKind != FunctionKind.Initializer && function.ReturnType != compilation.Core.Void) {
                text.Append(" -> ").Append(function.ReturnType.ToDisplayString());
            }

            return text.ToString();
        }

        static string Parameter(ParameterSymbol parameter) {
            var text = new StringBuilder();
            if (parameter.Label is { } label) {
                text.Append(label).Append(' ');
            }

            text.Append(parameter.Name).Append(": ");
            if (parameter.Modifier != ParameterModifier.None) {
                text.Append(parameter.Modifier switch {
                    ParameterModifier.InOut => "inout",
                    _ => parameter.Modifier.ToString().ToLowerInvariant(),
                }).Append(' ');
            }

            text.Append(parameter.Type.ToDisplayString());
            if (parameter.IsVariadic) {
                text.Append("...");
            }

            if (parameter.HasDefaultValue && parameter.DeclaringSyntax is [ParameterSyntax { DefaultValue: { } value }]) {
                text.Append(" = ").Append(value.Value.ToString().Trim());
            }

            return text.ToString();
        }

        static string FieldSignature(FieldSymbol field, bool requirement) {
            var text = new StringBuilder(requirement ? "" : "public ");
            if (field.IsStatic) {
                text.Append("static ");
            }

            text.Append(field.IsMutable || field.IsComputed ? "var " : "let ").Append(field.Name).Append(": ").Append(field.Type.ToDisplayString());
            if (field.IsComputed) {
                text.Append(field.HasSetter ? " { get set }" : " { get }");
            }

            return text.ToString();
        }

        // ---- Comments ------------------------------------------------------

        static bool IsPublic(Symbol symbol) => symbol.DeclaredAccessibility == Accessibility.Public;

        /// <summary>
        ///     The <c>///</c> lines (or <c>/** */</c> block) right before a
        ///     declaration, attributes included, without their markers.
        /// </summary>
        public static string DocComment(Symbol symbol) {
            if (symbol.DeclaringSyntax.IsEmpty) {
                return "";
            }

            var node = symbol.DeclaringSyntax[0];
            while (node is not DeclarationSyntax && node.Parent is { } parent && parent is not CompilationUnitSyntax) {
                node = parent;
            }

            var lines = new List<string>();
            foreach (var trivia in node.GetFirstToken().LeadingTrivia) {
                switch (trivia.Kind) {
                    case SyntaxKind.DocCommentTrivia when trivia.Text.StartsWith("/**", StringComparison.Ordinal):
                        lines.Clear();
                        lines.AddRange(trivia.Text[3..^2].Split('\n').Select(static l => l.Trim().TrimStart('*').TrimStart()));
                        break;
                    case SyntaxKind.DocCommentTrivia:
                        lines.Add(StripMarker(trivia.Text, "///"));
                        break;
                    case SyntaxKind.LineCommentTrivia or SyntaxKind.BlockCommentTrivia:
                        lines.Clear();
                        break;
                }
            }

            return string.Join('\n', lines).Trim();
        }

        /// <summary>The <c>//!</c> lines of every file of a module, in order.</summary>
        string ModuleDoc(string module) {
            var lines = new List<string>();
            foreach (var tree in compilation.SyntaxTrees.Where(t => _moduleOfRoot.GetValueOrDefault(t.Root) == module)) {
                foreach (var token in tree.Root.DescendantTokens()) {
                    foreach (var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia)) {
                        if (trivia.Kind == SyntaxKind.ModuleDocCommentTrivia) {
                            lines.Add(StripMarker(trivia.Text, "//!"));
                        }
                    }
                }
            }

            return string.Join('\n', lines).Trim();
        }

        static string StripMarker(string text, string marker) {
            var line = text.TrimEnd()[marker.Length..];
            return line.StartsWith(' ') ? line[1..] : line;
        }
    }
}
