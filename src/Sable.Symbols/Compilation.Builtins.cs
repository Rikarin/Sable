using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     <c>@builtin("name")</c>: how the standard library declares, in Sable, a
///     member whose body is one of SIL's builtins.
/// </summary>
/// <remarks>
///     <para>
///         The primitive types are the compiler's, but what they can do is
///         written in <c>lib/Sable.Core</c>. Most of it is Sable over Sable; at
///         the floor are the operations only the runtime can perform — a
///         string's scalars counted, a file read — and those are declared
///         without a body, naming the builtin that performs them:
///     </para>
///     <code>
///         extension String {
///             @builtin("string_trimmed")
///             public fn trimmed() -> String
///         }
///     </code>
///     <para>
///         A call is that builtin, applied in place to the receiver (if there
///         is one) and then the arguments, in order: no function is generated,
///         so nothing is called and nothing is added to a program's size. SIL
///         lowering checks the declaration against the builtin's signature in
///         <c>SilBuiltins</c>, as it is the layer that knows them; this layer
///         checks what can be told from the declaration alone.
///     </para>
///     <para>
///         Only the standard library may do this: <c>Sable.Core</c> and the
///         other modules named <c>Sable.*</c>, as <c>Sable.Math</c> declares
///         F64's <c>sin</c> to be libm's. A builtin assumes what its signature
///         says and nothing checks it at run time, so anywhere else it would
///         be a way round the type system.
///     </para>
/// </remarks>
public sealed partial class Compilation {
    const string BuiltinAttribute = "builtin";

    /// <summary>
    ///     The builtin a function declaration names, or null — reporting a
    ///     misuse, after which the declaration is an ordinary one.
    /// </summary>
    string? BuiltinOf(FunctionDeclarationSyntax declaration) {
        if (BuiltinAttributeOf(declaration.Attributes) is not { } attribute) {
            return null;
        }

        if (declaration.Body is not null || declaration.ExpressionBody is not null) {
            return Malformed(attribute, "a '@builtin' function has no body: the builtin is its body");
        }

        if (declaration.TypeParameters is not null) {
            return Malformed(attribute, "a '@builtin' function cannot be generic: a builtin works on the types its signature names");
        }

        // 'mutating' passes the receiver where it is, for the builtins that
        // change an array in place: SIL lowering checks the builtin takes an address.
        if (!declaration.AsyncKeyword.IsNone || declaration.Modifiers.Any(SyntaxKind.AsyncKeyword)) {
            return Malformed(attribute, "a '@builtin' function is not async: a builtin runs to completion where it is applied");
        }

        return NameOf(attribute);
    }

    /// <summary>
    ///     The builtin an operator declaration names, or null: as a function's,
    ///     an operator written <c>@builtin</c> has no body.
    /// </summary>
    string? BuiltinOf(OperatorDeclarationSyntax declaration) {
        if (BuiltinAttributeOf(declaration.Attributes) is not { } attribute) {
            return null;
        }

        if (declaration.Body is not null || declaration.ExpressionBody is not null) {
            return Malformed(attribute, "a '@builtin' operator has no body: the builtin is its body");
        }

        return NameOf(attribute);
    }

    /// <summary>
    ///     The segment of a builtin's name that stands for the conforming
    ///     type, in a family declared in a protocol's extension.
    /// </summary>
    public const string BuiltinFamilySelf = "Self";

    /// <summary>True for a builtin name with a <c>Self</c> segment: <c>count_leading_zeros_Self</c>.</summary>
    public static bool IsBuiltinFamily(string name) => name.Split('_').Contains(BuiltinFamilySelf);

    /// <summary>
    ///     A builtin named with a <c>Self</c> segment is a family, one builtin
    ///     for each type: written once, in an extension of a protocol, it is
    ///     the default the built-in types conforming to the protocol take
    ///     for a requirement — <c>count_leading_zeros_I8</c> for an I8,
    ///     <c>count_leading_zeros_U64</c> for a U64 — so that what every
    ///     integer can do is declared once rather than once per integer.
    ///     Such a name belongs in a protocol's extension, and a builtin in a
    ///     protocol's extension needs one, as nothing else says which type's
    ///     builtin it is. Null, reported, where either is broken.
    /// </summary>
    string? BuiltinFamilyChecked(SyntaxList<AttributeSyntax> attributes, string? name, Symbol container) {
        if (name is null) {
            return null;
        }

        var inProtocolExtension = container is ExtensionSymbol { ExtendedType.TypeKind: TypeKind.Protocol };
        if (IsBuiltinFamily(name) == inProtocolExtension) {
            return name;
        }

        var attribute = attributes.First(static a => a.Name.ToString().Trim() == BuiltinAttribute);
        return inProtocolExtension
            ? Malformed(attribute, $"a '@builtin' in a protocol's extension names a family, with a 'Self' segment the conforming type takes: '{name}_Self'")
            : Malformed(attribute, $"'{name}' has a 'Self' segment, which only a protocol's extension can fill: a family belongs in one");
    }

    /// <summary>The builtin a computed property declaration names, or null.</summary>
    string? BuiltinOf(FieldDeclarationSyntax declaration) {
        if (BuiltinAttributeOf(declaration.Attributes) is not { } attribute) {
            return null;
        }

        if (declaration.Accessors is not { Accessors: [{ Keyword.Kind: SyntaxKind.GetKeyword, Body: null, ExpressionBody: null }] }
            || declaration.Modifiers.Any(SyntaxKind.StaticKeyword)) {
            return Malformed(attribute, "a '@builtin' property is an instance property with a bodiless getter alone: 'var count: Int { get }'");
        }

        return NameOf(attribute);
    }

    /// <summary>The one <c>@builtin</c> attribute in a list, or null; reports one outside the standard library.</summary>
    AttributeSyntax? BuiltinAttributeOf(SyntaxList<AttributeSyntax> attributes) {
        AttributeSyntax? found = null;
        foreach (var attribute in attributes) {
            if (attribute.Name.ToString().Trim() != BuiltinAttribute) {
                continue;
            }

            if (found is not null) {
                Malformed(attribute, "a declaration names one builtin");
                continue;
            }

            found = attribute;
        }

        if (found is null) {
            return null;
        }

        if (!CoreLibrary.IsStandardLibrary(CurrentScope?.Module.Name)) {
            ReportDeclaration(
                ErrorCode.ERR_BuiltinOutsideCore,
                found.Span,
                "only the standard library (Sable.Core and the Sable.* modules) may declare a '@builtin': a builtin trusts its operands, so elsewhere it would sidestep the checks the language makes"
            );
            return null;
        }

        return found;
    }

    /// <summary>The name a well-formed <c>@builtin("name")</c> gives, or null.</summary>
    string? NameOf(AttributeSyntax attribute) =>
        attribute.ArgumentList is { Arguments: [{ NameToken.IsNone: true, Expression: LiteralExpressionSyntax { Token: { Kind: SyntaxKind.StringLiteralToken, Value: string name } } }] }
        && name.Length > 0
            ? name
            : Malformed(attribute, "'@builtin' takes the builtin's name as one string literal: '@builtin(\"string_trimmed\")'");

    string? Malformed(AttributeSyntax attribute, string message) {
        ReportDeclaration(ErrorCode.ERR_MalformedBuiltin, attribute.Span, message);
        return null;
    }
}
