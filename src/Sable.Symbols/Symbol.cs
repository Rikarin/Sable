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
    DependentMemberType
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
public abstract class Symbol {
    public abstract SymbolKind Kind { get; }
    public abstract string Name { get; }

    /// <summary>The symbol this one is declared inside, or null for a module.</summary>
    public virtual Symbol? ContainingSymbol => null;

    /// <summary>The syntax that declared this symbol; empty for synthesised ones.</summary>
    public virtual ImmutableArray<SyntaxNode> DeclaringSyntax => [];

    public virtual Accessibility DeclaredAccessibility => Accessibility.Internal;

    /// <summary>True for symbols the compiler created rather than the source.</summary>
    public virtual bool IsSynthesized => DeclaringSyntax.IsEmpty;

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
