using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>What an attribute may be written on.</summary>
[Flags]
internal enum AttributeTargets {
    None = 0,
    Function = 1 << 0,
    Struct = 1 << 1,
    Class = 1 << 2,
    Actor = 1 << 3,
    Protocol = 1 << 4,
    Union = 1 << 5,
    Enum = 1 << 6,
    Extension = 1 << 7,
    Initializer = 1 << 8,
    Deinitializer = 1 << 9,
    Subscript = 1 << 10,
    Operator = 1 << 11,
    Field = 1 << 12,
    Accessor = 1 << 13,
    Const = 1 << 14,
    Alias = 1 << 15,
    AssociatedType = 1 << 16,
    UnionCase = 1 << 17,
    EnumCase = 1 << 18,
    Parameter = 1 << 19,
    GenericParameter = 1 << 20,
    Module = 1 << 21,

    Type = Struct | Class | Actor | Protocol | Union | Enum,
    Callable = Function | Initializer | Deinitializer | Subscript | Operator | Accessor,
    Any = ~None,
}

/// <summary>
///     The attributes spec/SYNTAX.md §22 lists, and the compiler's own: each
///     known by name, and allowed only where it means something. An attribute
///     nothing knows is an error, as one written in the wrong place is — an
///     attribute that is silently ignored is worse than none.
/// </summary>
public sealed partial class Compilation {
    /// <summary>Every attribute there is, with where it may go and how that place is described.</summary>
    static readonly Dictionary<string, (AttributeTargets Targets, string Where)> KnownAttributes = new(StringComparer.Ordinal) {
        ["inline"] = (AttributeTargets.Callable, "a function"),
        ["inline_layout"] = (AttributeTargets.Struct | AttributeTargets.Union, "a struct or a union"),
        ["noncopy"] = (AttributeTargets.Struct | AttributeTargets.Union, "a struct or a union"),
        ["reflectable"] = (AttributeTargets.Type, "a type"),
        ["immutable"] = (AttributeTargets.Class, "a class"),
        ["immortal"] = (AttributeTargets.Field, "a 'static let'"),
        ["flags"] = (AttributeTargets.Enum, "an enum"),
        ["escaping"] = (AttributeTargets.Parameter, "a closure parameter"),
        ["polymorphic"] = (AttributeTargets.Callable, "a generic function"),
        ["deprecated"] = (AttributeTargets.Any & ~AttributeTargets.Module, "a declaration"),
        ["available"] = (AttributeTargets.Any & ~AttributeTargets.Module, "a declaration"),
        ["extern"] = (AttributeTargets.Function, "a function"),
        ["export"] = (AttributeTargets.Function | AttributeTargets.Field, "a function or a variable"),
        ["packed"] = (AttributeTargets.Struct, "a struct"),
        ["align"] = (AttributeTargets.Struct, "a struct"),
        ["test"] = (AttributeTargets.Function, "a function"),
        ["bench"] = (AttributeTargets.Function, "a function"),
        ["main"] = (AttributeTargets.Function | AttributeTargets.Type, "a function or a type"),
        ["discardable"] = (AttributeTargets.Callable, "a function"),
        ["MainActor"] = (AttributeTargets.Any & ~AttributeTargets.Module, "a declaration"),
        ["frozen"] = (AttributeTargets.Struct | AttributeTargets.Union | AttributeTargets.Enum, "a struct, a union or an enum"),
        ["derive"] = (AttributeTargets.Type, "a type"),
        ["indirect"] = (AttributeTargets.Union | AttributeTargets.UnionCase, "a union or a union case"),

        // The compiler's own: lint levels, and Sable.Core's ties to the
        // runtime, which are checked where they are read.
        ["allow"] = (AttributeTargets.Any, "a declaration"),
        ["warn"] = (AttributeTargets.Any, "a declaration"),
        ["builtin"] = (AttributeTargets.Any, "a declaration"),
        ["intrinsic"] = (AttributeTargets.Any, "a declaration"),
    };

    /// <summary>What a declaration is, as an attribute's place: null for a node no attribute is written on.</summary>
    static (AttributeTargets Target, string Noun)? AttributeTargetOf(SyntaxNode? node) =>
        node switch {
            FunctionDeclarationSyntax => (AttributeTargets.Function, "a function"),
            StructDeclarationSyntax => (AttributeTargets.Struct, "a struct"),
            ClassDeclarationSyntax => (AttributeTargets.Class, "a class"),
            ActorDeclarationSyntax => (AttributeTargets.Actor, "an actor"),
            ProtocolDeclarationSyntax => (AttributeTargets.Protocol, "a protocol"),
            UnionDeclarationSyntax => (AttributeTargets.Union, "a union"),
            EnumDeclarationSyntax => (AttributeTargets.Enum, "an enum"),
            ExtensionDeclarationSyntax => (AttributeTargets.Extension, "an extension"),
            InitDeclarationSyntax => (AttributeTargets.Initializer, "an initialiser"),
            DeinitDeclarationSyntax => (AttributeTargets.Deinitializer, "a deinitialiser"),
            SubscriptDeclarationSyntax => (AttributeTargets.Subscript, "a subscript"),
            OperatorDeclarationSyntax => (AttributeTargets.Operator, "an operator"),
            FieldDeclarationSyntax => (AttributeTargets.Field, "a property"),
            AccessorSyntax => (AttributeTargets.Accessor, "an accessor"),
            ConstDeclarationSyntax => (AttributeTargets.Const, "a constant"),
            AliasDeclarationSyntax => (AttributeTargets.Alias, "an alias"),
            AssociatedTypeDeclarationSyntax => (AttributeTargets.AssociatedType, "an associated type"),
            UnionCaseDeclarationSyntax => (AttributeTargets.UnionCase, "a union case"),
            EnumCaseDeclarationSyntax => (AttributeTargets.EnumCase, "an enum case"),
            ParameterSyntax => (AttributeTargets.Parameter, "a parameter"),
            GenericParameterSyntax => (AttributeTargets.GenericParameter, "a type parameter"),
            ModuleDeclarationSyntax => (AttributeTargets.Module, "a module declaration"),
            _ => null,
        };

    /// <summary>An attribute's name as written: <c>inline</c> for <c>@inline(always)</c>.</summary>
    internal static string AttributeName(AttributeSyntax attribute) => attribute.Name.ToString().Trim();

    /// <summary>The attributes written on a declaration.</summary>
    internal static IEnumerable<AttributeSyntax> AttributesOf(SyntaxNode declaration) =>
        declaration.ChildNodes().OfType<AttributeSyntax>();

    /// <summary>Checks every attribute in a file: that it is one there is, written where it applies, and with the arguments it takes.</summary>
    void CheckAttributes(SyntaxTree tree) {
        foreach (var attribute in tree.Root.DescendantNodes().OfType<AttributeSyntax>()) {
            var name = AttributeName(attribute);
            if (name.Length == 0) {
                continue;
            }

            if (!KnownAttributes.TryGetValue(name, out var known)) {
                ReportDeclaration(
                    ErrorCode.ERR_UnknownAttribute,
                    attribute.Name.Span,
                    $"there is no attribute '@{name}'"
                );
                continue;
            }

            if (AttributeTargetOf(attribute.Parent) is not var (target, noun)) {
                continue;
            }

            // '@immortal' is for a 'static let' alone.
            var applies = (known.Targets & target) != 0
                && !(name == "immortal" && attribute.Parent is FieldDeclarationSyntax field
                    && (field.LetOrVarKeyword.Kind != SyntaxKind.LetKeyword || !field.Modifiers.Any(static m => m.Kind == SyntaxKind.StaticKeyword)));

            if (!applies) {
                ReportDeclaration(
                    ErrorCode.ERR_AttributeDoesNotApply,
                    attribute.Span,
                    $"'@{name}' applies to {known.Where}, not to {noun}"
                );
                continue;
            }

            CheckAttributeArguments(attribute, name);

            if (name == "immutable" && attribute.Parent is ClassDeclarationSyntax immutableClass) {
                TypeBinder.DeferLate(() => CheckImmutable(immutableClass));
            }
        }
    }

    /// <summary>
    ///     An <c>@immutable</c> class is deeply immutable: every stored
    ///     property a <c>let</c>, and of a type that cannot change either — a
    ///     value, or another <c>@immutable</c> class.
    /// </summary>
    void CheckImmutable(ClassDeclarationSyntax declaration) {
        if (GetDeclaredSymbol(declaration) is not NamedTypeSymbol type) {
            return;
        }

        foreach (var field in type.Members.OfType<FieldSymbol>()) {
            if (field.IsStatic || field.IsComputed || field.DeclaringSyntax is not [var written, ..]) {
                continue;
            }

            if (field.IsMutable) {
                ReportDeclaration(
                    ErrorCode.ERR_NotDeeplyImmutable,
                    written.Span,
                    $"'{type.Name}' is '@immutable', so '{field.Name}' cannot be a 'var'; declare it with 'let'"
                );
                continue;
            }

            // Through an optional or an array, to the objects it holds.
            var fieldType = field.Type;
            while (fieldType is OptionalTypeSymbol or ArrayTypeSymbol) {
                fieldType = fieldType is OptionalTypeSymbol optional ? optional.UnderlyingType : ((ArrayTypeSymbol)fieldType).ElementType;
            }

            if (fieldType is NamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Actor } held
                && !(held.OriginalDefinition.DeclaringSyntax is [var heldDeclaration, ..]
                    && AttributesOf(heldDeclaration).Any(static a => AttributeName(a) == "immutable"))) {
                ReportDeclaration(
                    ErrorCode.ERR_NotDeeplyImmutable,
                    written.Span,
                    $"'{type.Name}' is '@immutable', so '{field.Name}' cannot hold '{held.ToDisplayString()}', "
                    + "an object that can change; make that class '@immutable' too"
                );
            }
        }
    }

    /// <summary>The arguments of the attributes whose meaning depends on them, which are read later.</summary>
    void CheckAttributeArguments(AttributeSyntax attribute, string name) {
        var arguments = attribute.ArgumentList?.Arguments.ToList() ?? [];

        void Malformed(string message) =>
            ReportDeclaration(ErrorCode.ERR_MalformedAttribute, attribute.Span, message);

        static bool IsString(ExpressionSyntax e) =>
            e is LiteralExpressionSyntax { Token.Kind: SyntaxKind.StringLiteralToken or SyntaxKind.RawStringLiteralToken };

        switch (name) {
            case "deprecated" when arguments.Count > 1 || arguments is [var only] && (!IsString(only.Expression) || !only.NameToken.IsNone):
                Malformed("'@deprecated' takes at most a message, as a string: '@deprecated(\"use X\")'");
                return;

            case "available" when arguments.Count == 0
                || arguments.Any(static a => a.NameToken.IsNone ? a.Expression is not IdentifierNameSyntax : !IsString(a.Expression)):
                Malformed("'@available' names the platforms it is for, each alone or with a version: '@available(macos: \"14.0\", linux)'");
                return;

            case "inline" when arguments is not [{ Expression: IdentifierNameSyntax { Identifier.Text: "always" or "never" } }]:
                Malformed("'@inline' takes 'always' or 'never': '@inline(always)'");
                return;

            case "align" when arguments is not [{ Expression: LiteralExpressionSyntax { Token.Value: ulong alignment } }] || alignment == 0 || (alignment & (alignment - 1)) != 0:
                Malformed("'@align' takes a power of two: '@align(16)'");
                return;

            case "packed" or "discardable" or "frozen" or "immutable" or "immortal" or "polymorphic" or "noncopy" or "escaping" or "flags" or "MainActor"
                when attribute.ArgumentList is not null:
                Malformed($"'@{name}' takes no arguments");
                return;
        }
    }

    /// <summary>
    ///     A use of a declaration, as <c>@deprecated</c> and <c>@available</c>
    ///     have it: a deprecated one is warned of, unless the use is in it or
    ///     in something deprecated too; one available only on other platforms
    ///     than the one being built for is an error.
    /// </summary>
    internal void ReportUse(DiagnosticBag diagnostics, Symbol used, TextSpan span, Symbol? context, FileScope? scope) {
        var (deprecation, platforms) = AttributeFactsOf(used);
        if (deprecation is null && platforms is null) {
            return;
        }

        var definition = used is NamedTypeSymbol named ? named.OriginalDefinition : used;
        var inDeprecated = false;
        var availableHere = false;
        for (var here = context; here is not null; here = here.ContainingSymbol) {
            if (ReferenceEquals(here, definition)) {
                return;
            }

            // Code that is itself only for some of the platforms may use
            // what is available on all of those.
            var facts = AttributeFactsOf(here);
            inDeprecated |= facts.Deprecation is not null;
            availableHere |= platforms is { } needed && facts.Platforms is { } own && own.All(p => needed.Contains(p, StringComparer.Ordinal));
        }

        if (deprecation is not null && !inDeprecated) {
            diagnostics.Add(Diagnostic.Warning(
                ErrorCode.WRN_Deprecated,
                span,
                $"'{used.Name}' is deprecated{(deprecation.Length > 0 ? $": {deprecation}" : "")}"
            ));
        }

        var target = scope?.Tree.Options.TargetOs ?? Options.TargetOs;
        if (platforms is { } on && !on.Contains(target, StringComparer.Ordinal) && !availableHere) {
            diagnostics.Error(
                ErrorCode.ERR_NotAvailable,
                span,
                $"'{used.Name}' is available only on {string.Join(" and ", on)}, and this builds for {target}"
            );
        }
    }

    readonly Dictionary<Symbol, (string? Deprecation, ImmutableArray<string>? Platforms)> _attributeFacts = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    ///     What <c>@deprecated</c> and <c>@available</c> say of a declaration:
    ///     the message it is deprecated with ("" for none), and the platforms
    ///     it is available on — null for each it does not say.
    /// </summary>
    internal (string? Deprecation, ImmutableArray<string>? Platforms) AttributeFactsOf(Symbol symbol) {
        var definition = symbol is NamedTypeSymbol named ? named.OriginalDefinition : symbol;
        lock (_attributeFacts) {
            if (_attributeFacts.TryGetValue(definition, out var known)) {
                return known;
            }
        }

        string? deprecation = null;
        ImmutableArray<string>? platforms = null;
        if (definition.DeclaringSyntax is [var declaration, ..]) {
            foreach (var attribute in AttributesOf(declaration)) {
                var arguments = attribute.ArgumentList?.Arguments.ToList() ?? [];
                switch (AttributeName(attribute)) {
                    case "deprecated":
                        deprecation = arguments is [{ Expression: LiteralExpressionSyntax { Token.Value: string message } }] ? message : "";
                        break;

                    case "available":
                        platforms = [
                            ..arguments.Select(static a => a.NameToken.IsNone
                                ? a.Expression is IdentifierNameSyntax platform ? platform.Identifier.Text : ""
                                : a.NameToken.Text)
                        ];
                        break;
                }
            }
        }

        lock (_attributeFacts) {
            _attributeFacts[definition] = (deprecation, platforms);
        }

        return (deprecation, platforms);
    }
}
