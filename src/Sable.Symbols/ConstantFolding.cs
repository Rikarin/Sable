using System.Numerics;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     Integer arithmetic in a <c>const</c>'s initialiser, worked out as the
///     compiler reads it, so that one that overflows its type — <c>const
///     Big: Int = 9223372036854775807 + 1</c> — or divides by zero is an
///     error here rather than a trap when the program starts.
/// </summary>
/// <remarks>
///     Only what is plainly integer arithmetic folds: literals, the built-in
///     unary and binary operators over them, and the conversions that give a
///     literal its type. Anything else — a call, another constant — is left
///     to run, and so is everything around it.
/// </remarks>
internal static class ConstantFolding {
    /// <summary>The value of an integer expression, or null when it does not fold; reports the first overflow.</summary>
    public static BigInteger? Fold(BoundExpression expression, CoreTypes core, DiagnosticBag diagnostics) {
        var reported = false;

        BigInteger? Checked(BigInteger value, BoundExpression at) {
            if (core.IntegerRange(at.Type) is not var (low, high)) {
                return null;
            }

            if (value >= low && value <= high) {
                return value;
            }

            if (!reported) {
                reported = true;
                diagnostics.Error(
                    ErrorCode.ERR_ConstantOverflow,
                    at.Syntax.Span,
                    $"the constant '{at.Syntax.ToString().Trim()}' is {value}, which does not fit in '{at.Type.ToDisplayString()}' ({low} through {high})"
                );
            }

            return null;
        }

        BigInteger? Literal(object value, TypeSymbol type) {
            var number = (BigInteger)System.Convert.ToDecimal(value);
            return core.IntegerRange(type) is var (low, high) && number >= low && number <= high ? number : null;
        }

        BigInteger? Of(BoundExpression node) {
            switch (node) {
                case BoundLiteral { Value: long or int or ulong } literal:
                    return Literal(literal.Value, literal.Type);

                // A literal given its type: its range was checked where it was bound.
                case BoundConversion { ConversionKind: ConversionKind.Identity or ConversionKind.LiteralTyping, Operand: BoundLiteral { Value: long or int or ulong } literal } conversion:
                    return Literal(literal.Value, conversion.Type);

                case BoundConversion { ConversionKind: ConversionKind.Identity or ConversionKind.LiteralTyping } conversion
                    when core.IsInteger(conversion.Type):
                    return Of(conversion.Operand) is { } inner ? Checked(inner, conversion) : null;

                case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Negate } negate:
                    return Of(negate.Operand) is { } operand ? Checked(-operand, negate) : null;

                case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Plus } plus:
                    return Of(plus.Operand);

                case BoundBinaryOperator { Method: null } binary when core.IsInteger(binary.Type):
                    if (Of(binary.Left) is not { } left || Of(binary.Right) is not { } right) {
                        return null;
                    }

                    if (binary.OperatorKind is BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder && right.IsZero) {
                        if (!reported) {
                            reported = true;
                            diagnostics.Error(
                                ErrorCode.ERR_ConstantOverflow,
                                binary.Syntax.Span,
                                $"the constant '{binary.Syntax.ToString().Trim()}' divides by zero"
                            );
                        }

                        return null;
                    }

                    return binary.OperatorKind switch {
                        BinaryOperatorKind.Add => Checked(left + right, binary),
                        BinaryOperatorKind.Subtract => Checked(left - right, binary),
                        BinaryOperatorKind.Multiply => Checked(left * right, binary),
                        // Division truncates toward zero, as the machine's does.
                        BinaryOperatorKind.Divide => Checked(BigInteger.Divide(left, right), binary),
                        BinaryOperatorKind.Remainder => Checked(BigInteger.Remainder(left, right), binary),
                        _ => null,
                    };

                default:
                    return null;
            }
        }

        return Of(expression);
    }
}
