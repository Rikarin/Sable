using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

public enum SymbolKind {
    Module,
    NamedType,
    TypeParameter,
    Function,
    Field,
    Property,
    Parameter,
    Local,
    Alias,
    UnionCase,
    EnumCase,
    ArrayType,
    TupleType,
    FunctionType,
    OptionalType,
    ReferenceType,
    StructuralUnionType,
    ExistentialType,
    OpaqueType,
    ErrorType,
    Extension,
    DependentMemberType,
    MetatypeType,

    /// <summary><c>4</c> in <c>Buf&lt;Int, 4&gt;</c>.</summary>
    ValueTypeArgument
}

/// <summary>Visibility, per spec/SYNTAX.md §2.</summary>
public enum Accessibility {
    Private,
    Internal,
    Package,
    Public
}

/// <summary>What kind of type a named type is.</summary>
public enum TypeKind {
    Struct,
    Class,
    Actor,
    Protocol,
    Union,
    Enum,
    Error,
    Builtin
}

/// <summary>
///     Anything a name can refer to: a module, a type, a member, a local.
/// </summary>
/// <remarks>
///     Symbols are created on demand from syntax and cached on the
///     <see cref="Compilation" /> that owns them. A symbol knows where it was
///     declared, which is what lets a diagnostic point at the source and the
///     language server answer "go to definition".
/// </remarks>
public abstract class Symbol(SyntaxNode? declaration = null) {
    public abstract SymbolKind Kind { get; }
    public abstract string Name { get; }

    /// <summary>The symbol this one is declared inside, or null for a module.</summary>
    public virtual Symbol? ContainingSymbol => null;

    ImmutableArray<SyntaxNode> _declaringSyntax = declaration is null ? [] : [declaration];

    /// <summary>
    ///     The syntax that declared this symbol; empty for synthesised ones. A
    ///     symbol read from a library's interface finds it in the library's
    ///     source (<see cref="Source" />), which is parsed when it is first
    ///     asked for: what only needs to know whether there is any asks
    ///     <see cref="IsDeclared" />.
    /// </summary>
    public virtual ImmutableArray<SyntaxNode> DeclaringSyntax {
        get {
            if (_declaringSyntax.IsEmpty && Source is { } source) {
                _declaringSyntax = [source.Find()];
            }

            return _declaringSyntax;
        }
    }

    /// <summary>For a symbol read from a library's interface, where the library's source declares it; null for any other.</summary>
    internal SymbolSource? Source { get; set; }

    /// <summary>Whether any source declares the symbol: whether it has <see cref="DeclaringSyntax" />, without parsing it.</summary>
    public virtual bool IsDeclared => Source is not null || !_declaringSyntax.IsEmpty;

    public virtual Accessibility DeclaredAccessibility => Accessibility.Internal;

    bool? _synthesized;

    /// <summary>
    ///     True for symbols the compiler created rather than the source: an
    ///     implicit initialiser, a flags type's <c>contains</c>. A symbol read
    ///     from a library's interface (<c>.sbi</c>) has no syntax and says so
    ///     itself; one bound from source is synthesised when nothing declares it.
    /// </summary>
    public bool IsSynthesized {
        get => _synthesized ?? !IsDeclared;
        internal set => _synthesized = value;
    }

    ImmutableArray<SymbolAttribute> _attributes;

    /// <summary>
    ///     The attributes written on the declaration — and on the extension
    ///     it is declared in — in source order: <c>@inline(always)</c>,
    ///     <c>@main</c>, <c>@MainActor</c>. What lowering and binding read of
    ///     another unit's declarations is read here rather than from its
    ///     syntax, so that a symbol read from an interface answers the same.
    /// </summary>
    public ImmutableArray<SymbolAttribute> Attributes {
        get {
            if (_attributes.IsDefault) {
                ImmutableInterlocked.InterlockedInitialize(ref _attributes, SymbolAttribute.Of(DeclaringSyntax));
            }

            return _attributes;
        }
        internal set => _attributes = value;
    }

    bool? _nonisolated;

    /// <summary>
    ///     Declared <c>nonisolated</c> — a function or a property, or an
    ///     accessor of one: reached on an actor from anywhere, without a hop.
    /// </summary>
    public bool IsNonisolated {
        get => _nonisolated ?? DeclaredNonisolated(DeclaringSyntax);
        internal set => _nonisolated = value;
    }

    static bool DeclaredNonisolated(ImmutableArray<SyntaxNode> declarations) {
        foreach (var syntax in declarations) {
            for (var node = syntax; node is not null; node = node.Parent) {
                if (node is FunctionDeclarationSyntax function) {
                    if (function.Modifiers.Any(static m => m.Kind == SyntaxKind.NonisolatedKeyword)) {
                        return true;
                    }

                    break;
                }

                if (node is FieldDeclarationSyntax field) {
                    if (field.Modifiers.Any(static m => m.Kind == SyntaxKind.NonisolatedKeyword)) {
                        return true;
                    }

                    break;
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Declared <c>@MainActor</c>, or inside a declaration that is — a
    ///     member of such a type or extension, a closure in such a function.
    /// </summary>
    public bool IsOnMainActor {
        get {
            for (var at = this; at is not null and not ModuleSymbol; at = at.ContainingSymbol) {
                if (at.HasAttribute("MainActor")) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    ///     The program's entry point — a function of a module marked
    ///     <c>@main</c> or named <c>main</c> — or code inside it.
    /// </summary>
    public bool IsInEntryPoint {
        get {
            for (var at = this; at is not null and not ModuleSymbol; at = at.ContainingSymbol) {
                if (at is FunctionSymbol { ContainingSymbol: ModuleSymbol } function && (function.Name == "main" || function.HasAttribute("main"))) {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Whether the declaration has the attribute of this name, as <see cref="Attributes" /> has it.</summary>
    public bool HasAttribute(string name) {
        foreach (var attribute in Attributes) {
            if (attribute.Name == name) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The first attribute of this name, if the declaration has one.</summary>
    public SymbolAttribute? GetAttribute(string name) {
        foreach (var attribute in Attributes) {
            if (attribute.Name == name) {
                return attribute;
            }
        }

        return null;
    }

    /// <summary>The module this symbol ultimately belongs to.</summary>
    public ModuleSymbol? ContainingModule {
        get {
            for (var symbol = this; symbol is not null; symbol = symbol.ContainingSymbol) {
                if (symbol is ModuleSymbol module) {
                    return module;
                }
            }

            return null;
        }
    }

    /// <summary>The dotted path from the module down to this symbol.</summary>
    public virtual string QualifiedName {
        get {
            if (ContainingSymbol is null or ModuleSymbol { Name.Length: 0 }) {
                return Name;
            }

            var parent = ContainingSymbol.QualifiedName;
            return parent.Length == 0 ? Name : parent + "." + Name;
        }
    }

    /// <summary>The first location this symbol was declared at, if any.</summary>
    public TextSpan? Location => DeclaringSyntax.IsEmpty ? null : DeclaringSyntax[0].Span;

    /// <summary>Its name where it is first declared, as a place a diagnostic about something else points at too.</summary>
    public DiagnosticLocation? DeclaredHere(string message) {
        if (DeclaringSyntax.IsEmpty) {
            return null;
        }

        var name = SemanticModel.NameTokenOf(this, DeclaringSyntax[0]);
        return name.IsNone ? DiagnosticLocation.At(DeclaringSyntax[0], message) : DiagnosticLocation.At(name, message);
    }

    public override string ToString() => QualifiedName;
}

/// <summary>
///     An attribute as a symbol holds it: its name, <c>inline</c> for
///     <c>@inline(always)</c>, and its argument list as written —
///     <c>(always)</c>, or <c>("c", "abs")</c>, null when there is none —
///     with each argument's text, trimmed. Arguments are kept as written
///     rather than evaluated, since what they mean is the attribute's
///     business; a label stays on its argument (<c>since: 2</c>).
/// </summary>
/// <param name="FromExtension">
///     Written on the extension the declaration is in rather than on the
///     declaration: <c>@MainActor extension Model { … }</c> isolates its
///     members, but a type's reflected attributes are its own.
/// </param>
public sealed record SymbolAttribute(string Name, string? ArgumentList, bool FromExtension = false) {
    /// <summary>Each argument's text, trimmed, in order; empty when there is no argument list.</summary>
    public ImmutableArray<string> Arguments => ArgumentList is null ? [] : SplitArguments(ArgumentList);

    /// <summary>The attributes of declarations, in order, with those of the extension each is in after its own.</summary>
    internal static ImmutableArray<SymbolAttribute> Of(ImmutableArray<SyntaxNode> declarations) {
        if (declarations.IsEmpty) {
            return [];
        }

        var found = ImmutableArray.CreateBuilder<SymbolAttribute>();
        foreach (var declaration in declarations) {
            Add(declaration, found, false);
            for (var parent = declaration.Parent; parent is not null; parent = parent.Parent) {
                if (parent is ExtensionDeclarationSyntax extension) {
                    Add(extension, found, true);
                    break;
                }

                if (parent is DeclarationSyntax) {
                    break;
                }
            }
        }

        return found.ToImmutable();
    }

    static void Add(SyntaxNode declaration, ImmutableArray<SymbolAttribute>.Builder found, bool fromExtension) {
        foreach (var attribute in declaration.ChildNodes().OfType<AttributeSyntax>()) {
            found.Add(From(attribute) with { FromExtension = fromExtension });
        }
    }

    /// <summary>The attribute an <c>@name(arguments)</c> is.</summary>
    public static SymbolAttribute From(AttributeSyntax attribute) =>
        new(attribute.Name.ToString().Trim(), attribute.ArgumentList?.ToString().Trim());

    /// <summary>The top-level arguments of <c>(a, (b, c), "d,e")</c>: split at commas outside brackets and strings.</summary>
    static ImmutableArray<string> SplitArguments(string list) {
        var inner = list.Length >= 2 && list[0] == '(' && list[^1] == ')' ? list[1..^1] : list;
        var arguments = ImmutableArray.CreateBuilder<string>();
        var depth = 0;
        var start = 0;
        var quoted = false;
        for (var i = 0; i < inner.Length; i++) {
            var c = inner[i];
            if (quoted) {
                if (c == '\\') {
                    i++;
                } else if (c == '"') {
                    quoted = false;
                }

                continue;
            }

            switch (c) {
                case '"':
                    quoted = true;
                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    arguments.Add(inner[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }

        if (inner[start..].Trim() is { Length: > 0 } last) {
            arguments.Add(last);
        }

        return arguments.ToImmutable();
    }

    /// <summary>The attribute as written: <c>inline(always)</c>, without the <c>@</c>.</summary>
    public string Written => Name + ArgumentList;

    public override string ToString() => "@" + Written;
}
