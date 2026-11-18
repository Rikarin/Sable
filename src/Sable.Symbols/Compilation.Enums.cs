using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     An enum's cases as the integers they name (SYNTAX.md §10): each case's
///     value, written or implied, worked out as the case is declared; and a
///     <c>@flags</c> enum, whose values are sets of its cases.
/// </summary>
public sealed partial class Compilation {
    /// <summary>
    ///     The integer type an enum is: the one its base list names,
    ///     <c>enum Status: U8</c>, or <c>Int</c>.
    /// </summary>
    public NamedTypeSymbol EnumRawType(NamedTypeSymbol enumType) =>
        enumType.OriginalDefinition.BaseTypes.OfType<NamedTypeSymbol>().FirstOrDefault(Core.IsInteger) ?? Core.Int;

    /// <summary>
    ///     A case's value: the one it writes, or else the one after the
    ///     previous case's — in a <c>@flags</c> enum, the next bit up, so that
    ///     each case left to itself is a flag of its own.
    /// </summary>
    long EnumCaseValue(EnumCaseDeclarationSyntax declaration, NamedTypeSymbol enumType) {
        var previous = enumType.Members.OfType<EnumCaseSymbol>().LastOrDefault();
        if (declaration.Value is { } written) {
            return EvaluateCaseValue(written, enumType) ?? previous?.RawValue + 1 ?? 0;
        }

        if (!enumType.IsFlags) {
            return previous is null ? 0 : previous.RawValue + 1;
        }

        return previous is null || previous.RawValue <= 0 ? 1 : (long)System.Numerics.BitOperations.RoundUpToPowerOf2((ulong)previous.RawValue + 1);
    }

    /// <summary>
    ///     A constant integer: a literal, an earlier case of the same enum —
    ///     <c>Read</c> or <c>.Read</c> — or those combined with the integer
    ///     operators. Anything else is reported, and has no value.
    /// </summary>
    long? EvaluateCaseValue(ExpressionSyntax expression, NamedTypeSymbol enumType) {
        switch (expression) {
            case LiteralExpressionSyntax { Token.Value: long or int or ulong } literal:
                return unchecked(System.Convert.ToInt64(literal.Token.Value is ulong big ? (long)big : literal.Token.Value));

            case ParenthesizedExpressionSyntax parenthesized:
                return EvaluateCaseValue(parenthesized.Expression, enumType);

            case IdentifierNameSyntax or ImplicitMemberAccessExpressionSyntax: {
                var name = expression is IdentifierNameSyntax identifier ? identifier.Identifier.Text : ((ImplicitMemberAccessExpressionSyntax)expression).Name.ToString().Trim();
                if (enumType.GetMembers(name).OfType<EnumCaseSymbol>().FirstOrDefault() is { } earlier) {
                    return earlier.RawValue;
                }

                break;
            }

            case PrefixUnaryExpressionSyntax prefix when EvaluateCaseValue(prefix.Operand, enumType) is { } operand:
                switch (prefix.OperatorToken.Kind) {
                    case SyntaxKind.MinusToken: return -operand;
                    case SyntaxKind.TildeToken: return ~operand;
                    case SyntaxKind.PlusToken: return operand;
                }

                break;

            case BinaryExpressionSyntax binary
                when EvaluateCaseValue(binary.Left, enumType) is { } left && EvaluateCaseValue(binary.Right, enumType) is { } right:
                long? result = unchecked(binary.OperatorToken.Kind switch {
                    SyntaxKind.PipeToken => left | right,
                    SyntaxKind.AmpersandToken => left & right,
                    SyntaxKind.CaretToken => left ^ right,
                    SyntaxKind.PlusToken => left + right,
                    SyntaxKind.MinusToken => left - right,
                    SyntaxKind.StarToken => left * right,
                    SyntaxKind.LessLessToken => left << (int)right,
                    SyntaxKind.GreaterGreaterToken => left >> (int)right,
                    _ => (long?)null,
                });
                if (result is not null) {
                    return result;
                }

                break;

            case PrefixUnaryExpressionSyntax or BinaryExpressionSyntax:
                // An operand that has no value was reported where it is.
                return null;
        }

        ReportDeclaration(
            ErrorCode.ERR_EnumCaseValue,
            expression.Span,
            "a case's value is a constant integer: a literal, an earlier case, or those combined with | & ^ + - * << >> ~"
        );
        return null;
    }

    /// <summary>
    ///     In an enum that is not <c>@flags</c>, each case is a different
    ///     integer, so that going from an integer to a case has one answer.
    /// </summary>
    void CheckDistinctValue(EnumCaseDeclarationSyntax declaration, NamedTypeSymbol enumType, long value) {
        if (enumType.IsFlags || enumType.Members.OfType<EnumCaseSymbol>().FirstOrDefault(c => c.RawValue == value) is not { } same) {
            return;
        }

        ReportDeclaration(
            ErrorCode.ERR_EnumCaseValue,
            declaration.Identifier.Span,
            $"'{declaration.Identifier.Text}' is {value}, which '{same.Name}' already is; only a '@flags' enum's cases may share a value"
        );
    }

    /// <summary>
    ///     A <c>@flags</c> enum's own members: <c>contains(_:)</c>, true when
    ///     every flag of the argument is set. Lowering writes its body.
    /// </summary>
    void DeclareFlagsMembers(NamedTypeSymbol type) {
        var contains = new FunctionSymbol("contains", type, null, type.DeclaredAccessibility, FunctionKind.Ordinary);
        contains.Parameters = [new ParameterSymbol("member", null, contains, null, 0) { TypeResolver = () => type }];
        contains.ReturnTypeResolver = () => Core.Bool;
        type.AddMember(contains);
    }
}
