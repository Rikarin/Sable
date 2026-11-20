using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     A const-generic predicate as a type holds it — the <c>N &gt; 0</c> of
///     <c>struct Buf&lt;T, N: Int&gt; where N &gt; 0</c> — bound from its
///     expression when the type is declared: whole numbers, <c>true</c>,
///     <c>false</c>, the type's value parameters by name, and operators. A
///     use of the type is checked by evaluating it with the numbers given,
///     which needs no syntax, so a type read from a library's interface
///     checks its uses as one bound from source does.
/// </summary>
/// <param name="Text">The predicate as written, trimmed, as a diagnostic quotes it.</param>
public sealed record ValuePredicate(string Text, ValuePredicate.Node Root) {
    /// <summary>A part of a predicate.</summary>
    public abstract record Node;

    /// <summary>A whole number.</summary>
    public sealed record Number(long Value) : Node;

    /// <summary><c>true</c> or <c>false</c>.</summary>
    public sealed record Truth(bool Value) : Node;

    /// <summary>A value parameter, <c>N</c>.</summary>
    public sealed record Parameter(string Name) : Node;

    /// <summary><c>-x</c> or <c>!x</c>, by its operator's text.</summary>
    public sealed record Unary(string Operator, Node Operand) : Node;

    /// <summary><c>a op b</c>, by its operator's text.</summary>
    public sealed record Binary(string Operator, Node Left, Node Right) : Node;

    /// <summary>The predicate an expression is, or null when it is not one (a part is not a number, a parameter or an operator).</summary>
    public static ValuePredicate? Bind(ExpressionSyntax expression) =>
        BindNode(expression) is { } root ? new(expression.ToString().Trim(), root) : null;

    static Node? BindNode(ExpressionSyntax expression) =>
        expression switch {
            LiteralExpressionSyntax { Token.Kind: SyntaxKind.TrueKeyword } => new Truth(true),
            LiteralExpressionSyntax { Token.Kind: SyntaxKind.FalseKeyword } => new Truth(false),
            LiteralExpressionSyntax literal => long.TryParse(literal.Token.Text.Replace("_", ""), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var number)
                ? new Number(number)
                : null,
            IdentifierNameSyntax name => new Parameter(name.Identifier.Text),
            ParenthesizedExpressionSyntax parenthesized => BindNode(parenthesized.Expression),
            PrefixUnaryExpressionSyntax unary => BindNode(unary.Operand) is { } operand ? new Unary(unary.OperatorToken.Text, operand) : null,
            BinaryExpressionSyntax binary => BindNode(binary.Left) is { } left && BindNode(binary.Right) is { } right
                ? new Binary(binary.OperatorToken.Text, left, right)
                : null,
            _ => null,
        };

    /// <summary>Whether it holds for the values given: null when it has none — it divides by zero, overflows, names a value not given, or is not true or false.</summary>
    public bool? Evaluate(IReadOnlyDictionary<string, long> values) => Evaluate(Root, values) as bool?;

    static object? Evaluate(Node node, IReadOnlyDictionary<string, long> values) {
        try {
            checked {
                return node switch {
                    Number number => number.Value,
                    Truth truth => truth.Value,
                    Parameter parameter => values.TryGetValue(parameter.Name, out var value) ? value : null,
                    Unary unary => (unary.Operator, Evaluate(unary.Operand, values)) switch {
                        ("-", long operand) => -operand,
                        ("!", bool operand) => !operand,
                        _ => null,
                    },
                    Binary binary => (binary.Operator, Evaluate(binary.Left, values), Evaluate(binary.Right, values)) switch {
                        ("+", long l, long r) => l + r,
                        ("-", long l, long r) => l - r,
                        ("*", long l, long r) => l * r,
                        ("/", long l, long r) when r != 0 => l / r,
                        ("%", long l, long r) when r != 0 => l % r,
                        ("==", long l, long r) => l == r,
                        ("==", bool l, bool r) => l == r,
                        ("!=", long l, long r) => l != r,
                        ("!=", bool l, bool r) => l != r,
                        ("<", long l, long r) => l < r,
                        ("<=", long l, long r) => l <= r,
                        (">", long l, long r) => l > r,
                        (">=", long l, long r) => l >= r,
                        ("&&", bool l, bool r) => l && r,
                        ("||", bool l, bool r) => l || r,
                        _ => null,
                    },
                    _ => null,
                };
            }
        } catch (OverflowException) {
            return null;
        }
    }
}
