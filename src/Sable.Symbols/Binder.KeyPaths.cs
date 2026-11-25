using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     Key paths (SYNTAX.md §16): <c>\.address.city</c> is Sable.Core's
///     <c>KeyPath&lt;Customer, String&gt;</c> where that is wanted, made of the
///     path as text and a closure reading it, <c>{ $0.address.city }</c>; where a
///     function is wanted, it is the closure alone.
/// </summary>
/// <remarks>
///     The root's type is written, <c>\Customer.name</c>, or the context's. As
///     a closure's, a key path argument waits for the overload it is passed
///     to, and what it leads to settles type parameters.
/// </remarks>
public sealed partial class Binder {
    const string KeyPathRootName = "$0";

    BoundExpression BindKeyPath(KeyPathExpressionSyntax syntax, TypeSymbol? expectedType) {
        if (Core.KeyPath is not { } keyPath) {
            return Unsupported(syntax);
        }

        var wanted = Expand(expectedType is OptionalTypeSymbol optional ? optional.UnderlyingType : expectedType);
        var function = wanted as FunctionTypeSymbol;
        TypeSymbol? root = null;
        TypeSymbol? value = null;
        if (KeyPathArguments(wanted) is { } arguments) {
            (root, value) = arguments;
        } else if (function is { ParameterTypes: [var only] }) {
            (root, value) = (only, function.ReturnType);
        }

        if (RootOf(syntax).Type is { } written) {
            root = Sub(_compilation.TypeBinder.BindType(written, _containingSymbol, _scope));
        }

        if (root is null || MentionsOpen(root)) {
            _diagnostics.Error(
                ErrorCode.ERR_CannotInferType,
                syntax.Span,
                "what this key path starts from cannot be inferred here, so write its type, as in '\\Customer.name'"
            );
            return new BoundError(syntax);
        }

        if (root.IsError) {
            return new BoundError(syntax);
        }

        // What it leads to is its own to say, unless the context knows.
        var savedOpen = _openTypeParameters;
        if (value is null || MentionsOpen(value)) {
            value = keyPath.TypeParameters[1];
            _openTypeParameters = [.._openTypeParameters, keyPath.TypeParameters[1]];
        }

        var closure = BindClosure(
            syntax,
            null,
            default,
            null,
            null,
            syntax.Body,
            function ?? new FunctionTypeSymbol([root], value, false, null),
            false
        );
        _openTypeParameters = savedOpen;
        if (function is not null || closure is not BoundClosure { Type: FunctionTypeSymbol read }) {
            return closure;
        }

        if (read.ReturnType.IsError) {
            return new BoundError(syntax);
        }

        return MakeKeyPath(syntax, root, read.ReturnType, KeyPathText(syntax.Body), closure);
    }

    /// <summary><c>KeyPath(path: …, read: …)</c>, as the compiler writes it.</summary>
    BoundExpression MakeKeyPath(SyntaxNode syntax, TypeSymbol root, TypeSymbol value, string path, BoundExpression read) {
        var type = Core.KeyPath!.Construct([root, value]);
        var initializers = Core.KeyPath.GetMembers("init").OfType<FunctionSymbol>().Where(static i => !i.IsSynthesized);
        var group = new BoundFunctionGroup(syntax, null, [..initializers]) { Substitution = TypeSubstitution.For(type) };
        return BindCallToGroup(syntax, group, [new BoundLiteral(syntax, Core.String, path), read], ["path", "read"], [], type);
    }

    /// <summary>What a <c>KeyPath&lt;Root, Value&gt;</c>, or an optional one, is from and to; null for any other type.</summary>
    (TypeSymbol Root, TypeSymbol Value)? KeyPathArguments(TypeSymbol? type) =>
        Expand(type is OptionalTypeSymbol optional ? optional.UnderlyingType : type) is NamedTypeSymbol { TypeArguments: [var root, var value] } named
        && ReferenceEquals(named.OriginalDefinition, Core.KeyPath)
            ? (root, value)
            : null;

    /// <summary>
    ///     The type of what a key path leads to from a root, as a question:
    ///     nothing is reported. Null where it leads nowhere, or where the
    ///     root is the call's to infer.
    /// </summary>
    TypeSymbol? KeyPathValue(KeyPathExpressionSyntax syntax, TypeSymbol root, FunctionSymbol candidate) {
        if (Core.KeyPath is not { } keyPath || RootOf(syntax).Type is null && ContainsUninferred(root, candidate)) {
            return null;
        }

        var saved = _openTypeParameters;
        _openTypeParameters = [.._openTypeParameters, keyPath.TypeParameters[1]];
        var bound = Speculate(syntax, keyPath.Construct([root, keyPath.TypeParameters[1]]));
        _openTypeParameters = saved;
        return KeyPathArguments(bound.Type)?.Value;
    }

    BoundExpression BindKeyPathRoot(KeyPathRootExpressionSyntax syntax) =>
        LookupLocal(KeyPathRootName) is ParameterSymbol parameter ? new BoundParameterAccess(syntax, parameter) : new BoundError(syntax);

    static KeyPathRootExpressionSyntax RootOf(KeyPathExpressionSyntax syntax) {
        var expression = syntax.Body;
        while (expression is MemberAccessExpressionSyntax access) {
            expression = access.Expression;
        }

        return (KeyPathRootExpressionSyntax)expression;
    }

    /// <summary><c>address.city</c> for <c>\.address?.city</c>: the members' names, from the root on.</summary>
    static string KeyPathText(ExpressionSyntax body) {
        var names = new Stack<string>();
        while (body is MemberAccessExpressionSyntax access) {
            names.Push(access.Name is IdentifierNameSyntax { Identifier: var name } ? name.Value as string ?? name.Text : access.Name.ToString().Trim());
            body = access.Expression;
        }

        return string.Join('.', names);
    }
}
