using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     <c>@builtin("name")</c>: how <c>Sable.Core</c> declares, in Sable, a
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
///         Only <c>Sable.Core</c> may do this. A builtin assumes what its
///         signature says and nothing checks it at run time, so anywhere else
///         it would be a way round the type system.
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

    /// <summary>The one <c>@builtin</c> attribute in a list, or null; reports one outside Sable.Core.</summary>
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

        if (CurrentScope?.Module.Name != CoreLibrary.ModuleName) {
            ReportDeclaration(
                ErrorCode.ERR_BuiltinOutsideCore,
                found.Span,
                "only Sable.Core may declare a '@builtin': a builtin trusts its operands, so elsewhere it would sidestep the checks the language makes"
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
