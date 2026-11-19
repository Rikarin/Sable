using System.Text;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.LanguageServer;

/// <summary>How a symbol reads in a hover, a completion item's detail, or a signature help.</summary>
internal static class SymbolDisplay {
    /// <summary>The declaration as it would be written, without its body.</summary>
    public static string Signature(Symbol symbol) {
        switch (symbol) {
            case FunctionSymbol function:
                return FunctionSignature(function);

            case FieldSymbol field:
                return $"{(field.IsStatic ? "static " : "")}{(field.IsMutable ? "var" : "let")} {field.Name}: {Type(field.Type)}";

            case LocalSymbol local:
                return $"{(local.IsMutable ? "var" : "let")} {local.Name}: {Type(local.Type)}";

            case ParameterSymbol parameter:
                return $"{Modifier(parameter.Modifier)}{parameter.Name}: {Type(parameter.Type)}";

            case NamedTypeSymbol type: {
                var builder = new StringBuilder();
                builder.Append(Keyword(type.TypeKind)).Append(' ').Append(type.Name);
                if (!type.TypeParameters.IsEmpty) {
                    builder.Append('<').AppendJoin(", ", type.TypeParameters.Select(static p => p.Name)).Append('>');
                }

                if (!type.BaseTypes.IsEmpty) {
                    builder.Append(": ").AppendJoin(", ", type.BaseTypes.Select(Type));
                }

                return builder.ToString();
            }

            case UnionCaseSymbol unionCase:
                return unionCase.Payload.IsEmpty
                    ? $"case {unionCase.Name}"
                    : $"case {unionCase.Name}({string.Join(", ", unionCase.Payload.Select(static p => $"{p.Name}: {Type(p.Type)}"))})";

            case EnumCaseSymbol enumCase:
                return $"case {enumCase.Name}";

            case TypeParameterSymbol parameter:
                return parameter.Constraints.IsEmpty
                    ? parameter.Name
                    : $"{parameter.Name}: {string.Join(" & ", parameter.Constraints.Select(Type))}";

            case AliasSymbol alias:
                return $"alias {alias.Name}";

            case ModuleSymbol module:
                return $"module {module.Name}";

            case TypeSymbol type:
                return Type(type);

            default:
                return symbol.Name;
        }
    }

    public static string FunctionSignature(FunctionSymbol function, bool withKeyword = true) {
        var builder = new StringBuilder();
        if (withKeyword) {
            // What a caller must vouch for is the first thing hover says.
            if (function.IsUnsafe) {
                builder.Append("unsafe ");
            }

            if (function.IsStatic) {
                builder.Append("static ");
            }

            if (function.IsMutating) {
                builder.Append("mutating ");
            }
        }

        switch (function.FunctionKind) {
            case FunctionKind.Initializer:
                builder.Append("init");
                break;
            case FunctionKind.Deinitializer:
                return "deinit";
            case FunctionKind.Operator:
                builder.Append("operator ").Append(function.Name).Append(' ');
                break;
            case FunctionKind.Subscript:
                builder.Append("subscript");
                break;
            default:
                if (withKeyword) {
                    builder.Append("fn ");
                }

                builder.Append(function.Name);
                break;
        }

        if (!function.TypeParameters.IsEmpty) {
            builder.Append('<').AppendJoin(", ", function.TypeParameters.Select(static p => p.Name)).Append('>');
        }

        builder.Append('(').AppendJoin(", ", function.Parameters.Select(Parameter)).Append(')');

        if (function.IsAsync) {
            builder.Append(" async");
        }

        if (function.IsRethrows) {
            builder.Append(" rethrows");
        } else if (function.ThrownType is { } thrown) {
            builder.Append(" throws(").Append(Type(thrown)).Append(')');
        }

        if (function.FunctionKind is not (FunctionKind.Initializer or FunctionKind.Deinitializer)
            && function.ReturnType is { } result
            && !result.IsError
            && result.ToDisplayString() != "Void") {
            builder.Append(" -> ").Append(Type(result));
        }

        return builder.ToString();
    }

    public static string Parameter(ParameterSymbol parameter) {
        var label = parameter.Label is { } written && written != parameter.Name ? written + " " : "";
        var ellipsis = parameter.IsVariadic ? "..." : "";
        var defaulted = parameter.HasDefaultValue ? " = …" : "";
        return $"{label}{Modifier(parameter.Modifier)}{parameter.Name}: {Type(parameter.Type)}{ellipsis}{defaulted}";
    }

    static string Modifier(ParameterModifier modifier) =>
        modifier switch {
            ParameterModifier.InOut => "inout ",
            ParameterModifier.Out => "out ",
            ParameterModifier.Consuming => "consuming ",
            ParameterModifier.Borrowing => "borrowing ",
            ParameterModifier.Ref => "ref ",
            ParameterModifier.In => "in ",
            _ => "",
        };

    public static string Type(TypeSymbol type) => type.ToDisplayString();

    static string Keyword(TypeKind kind) =>
        kind switch {
            TypeKind.Class => "class",
            TypeKind.Actor => "actor",
            TypeKind.Protocol => "protocol",
            TypeKind.Union => "union",
            TypeKind.Enum => "enum",
            _ => "struct",
        };

    /// <summary>The documentation comment above a declaration: its <c>///</c> lines, markers stripped.</summary>
    public static string? Documentation(Symbol symbol) {
        if (symbol.DeclaringSyntax.IsEmpty) {
            return null;
        }

        var declaration = symbol.DeclaringSyntax[0];

        // A field's comment sits before 'var', a local's before 'let'.
        if (declaration is VariableDeclaratorSyntax { Parent: { } owner }) {
            declaration = owner;
        }

        var lines = new List<string>();
        foreach (var trivia in declaration.GetFirstToken().LeadingTrivia) {
            if (trivia.Kind is SyntaxKind.DocCommentTrivia) {
                foreach (var raw in trivia.Text.Split('\n')) {
                    var line = raw.TrimEnd('\r').TrimStart();
                    line = line.StartsWith("///", StringComparison.Ordinal) ? line[3..] : line;
                    lines.Add(line.StartsWith(' ') ? line[1..] : line);
                }
            } else if (trivia.Kind is SyntaxKind.LineCommentTrivia or SyntaxKind.BlockCommentTrivia) {
                // An ordinary comment between the doc comment and the declaration ends it.
                lines.Clear();
            }
        }

        while (lines.Count > 0 && lines[^1].Length == 0) {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>The protocol's <c>CompletionItemKind</c> for a symbol.</summary>
    public static int CompletionKind(Symbol symbol) =>
        symbol switch {
            FunctionSymbol { FunctionKind: FunctionKind.Initializer } => 4,
            FunctionSymbol { ContainingSymbol: NamedTypeSymbol or ExtensionSymbol } => 2,
            FunctionSymbol => 3,
            FieldSymbol { IsComputed: true } => 10,
            FieldSymbol => 5,
            LocalSymbol or ParameterSymbol => 6,
            NamedTypeSymbol { TypeKind: TypeKind.Protocol } => 8,
            NamedTypeSymbol { TypeKind: TypeKind.Enum or TypeKind.Union } => 13,
            NamedTypeSymbol { TypeKind: TypeKind.Struct } => 22,
            NamedTypeSymbol => 7,
            UnionCaseSymbol or EnumCaseSymbol => 20,
            TypeParameterSymbol => 25,
            ModuleSymbol => 9,
            AliasSymbol => 7,
            _ => 1,
        };

    /// <summary>The protocol's <c>SymbolKind</c> for a declaration's symbol.</summary>
    public static int SymbolKindOf(SyntaxNode declaration) =>
        declaration switch {
            StructDeclarationSyntax => 23,
            ClassDeclarationSyntax or ActorDeclarationSyntax => 5,
            ProtocolDeclarationSyntax => 11,
            UnionDeclarationSyntax or EnumDeclarationSyntax => 10,
            ExtensionDeclarationSyntax => 19,
            FunctionDeclarationSyntax { Parent: CompilationUnitSyntax } => 12,
            FunctionDeclarationSyntax => 6,
            InitDeclarationSyntax or DeinitDeclarationSyntax => 9,
            OperatorDeclarationSyntax => 25,
            SubscriptDeclarationSyntax => 6,
            FieldDeclarationSyntax { Parent: CompilationUnitSyntax } => 13,
            FieldDeclarationSyntax => 8,
            ConstDeclarationSyntax => 14,
            UnionCaseDeclarationSyntax or EnumCaseDeclarationSyntax => 22,
            AliasDeclarationSyntax => 26,
            _ => 13,
        };
}
