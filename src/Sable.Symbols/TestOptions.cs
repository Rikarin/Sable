using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>What <c>@test(…)</c> says of a test, or <c>@suite(…)</c> of a suite's.</summary>
internal sealed class TestOptions {
    public string? Trap { get; private set; }
    public List<string> Tags { get; } = [];
    public TimeSpan? Timeout { get; private set; }
    public List<ExpressionSyntax> Arguments { get; } = [];
    public int? Retries { get; private set; }
    public string? Collection { get; private set; }
    public bool IsSerialized { get; private set; }
    public bool IsExplicit { get; private set; }
    public TestSkip? Skip { get; private set; }

    /// <summary>Whether it is a <c>@suite</c>'s, which takes neither arguments nor a trap.</summary>
    public bool OnSuite { get; init; }

    /// <summary>What both attributes take, for a message.</summary>
    public const string Common =
        "'tags: [\"text\"]', 'timeout: .seconds(5)', 'retry: 3', 'skip: \"why\"' or 'skip(if: condition, \"why\")', 'collection: \"name\"', 'serialized' and 'explicit'";

    /// <summary>Reads the attribute's arguments; gives the one that is not one '@test' takes, or null.</summary>
    public SyntaxNode? Read(AttributeSyntax attribute) {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var inArguments = false;
        if (attribute.ArgumentList!.Arguments.Count == 0) {
            return attribute.ArgumentList;
        }

        foreach (var argument in attribute.ArgumentList.Arguments) {
            var name = argument.NameToken.IsNone ? "" : argument.NameToken.Text;
            if (name.Length == 0) {
                // A word alone, as a name; or another array of arguments.
                var word = argument.Expression.ToString().Trim();
                if (word is "traps" or "serialized" or "explicit" && !(word == "traps" && OnSuite) && seen.Add(word)) {
                    Trap = word == "traps" ? "" : Trap;
                    IsSerialized |= word == "serialized";
                    IsExplicit |= word == "explicit";
                    inArguments = false;
                } else if (SkipOf(argument.Expression) is { } skip && seen.Add("skip")) {
                    Skip = skip;
                    inArguments = false;
                } else if (inArguments) {
                    Arguments.Add(argument.Expression);
                } else {
                    return argument;
                }

                continue;
            }

            inArguments = name == "arguments";
            if (!seen.Add(name)) {
                return argument;
            }

            switch (name) {
                case "traps" when !OnSuite && Text(argument.Expression) is { } text:
                    Trap = text;
                    break;
                case "tags" when argument.Expression is ArrayExpressionSyntax array && array.Elements.All(static e => Text(e) is { } tag && IsTag(tag)):
                    Tags.AddRange(array.Elements.Select(static e => Text(e)!));
                    break;
                case "timeout" when Duration(argument.Expression) is { } timeout:
                    Timeout = timeout;
                    break;
                case "retry" when argument.Expression is LiteralExpressionSyntax { Token.Kind: SyntaxKind.IntegerLiteralToken } literal
                    && int.TryParse(literal.Token.Text, System.Globalization.CultureInfo.InvariantCulture, out var retries) && retries is > 0 and <= 100:
                    Retries = retries;
                    break;
                case "collection" when Text(argument.Expression) is { Length: > 0 } collection && !collection.Any(char.IsControl):
                    Collection = collection;
                    break;
                case "skip" when Text(argument.Expression) is { } reason:
                    Skip = new(null, reason, OnSuite);
                    break;
                case "arguments" when !OnSuite:
                    Arguments.Add(argument.Expression);
                    break;
                default:
                    return argument;
            }
        }

        return null;
    }

    /// <summary><c>skip(if: condition, "why")</c>; without the reason, the condition is it.</summary>
    TestSkip? SkipOf(ExpressionSyntax expression) =>
        expression is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "skip" }, ArgumentList.Arguments: var given, TrailingClosure: null }
        && given.Count is 1 or 2 && given[0] is { NameToken: { IsNone: false, Text: "if" }, Expression: var condition }
            ? given.Count == 1 ? new(condition, condition.ToString().Trim(), OnSuite)
            : given[1].NameToken.IsNone && Text(given[1].Expression) is { } reason ? new(condition, reason, OnSuite)
            : null
            : null;

    /// <summary>A plain string: no interpolation, nothing to evaluate.</summary>
    static string? Text(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { Token.Kind: SyntaxKind.StringLiteralToken } literal
        && literal.Token.Parts.All(static p => p.Kind == StringPartKind.Literal)
            ? literal.Token.Value as string
            : null;

    /// <summary>A tag a filter can be written with: no space, nor what a filter is put together with.</summary>
    static bool IsTag(string tag) => tag.Length > 0 && tag.All(static c => !char.IsWhiteSpace(c) && !char.IsControl(c) && !"&|!()*,\"".Contains(c));

    /// <summary><c>.seconds(5)</c>, <c>.milliseconds(200)</c> or <c>.minutes(2)</c>, as Sable.Time's Duration has them, of a literal.</summary>
    static TimeSpan? Duration(ExpressionSyntax expression) {
        var parts = expression.ToString().Trim().TrimEnd(')').Split('(');
        if (parts is not [var unit, var number] || !double.TryParse(number.Replace("_", "", StringComparison.Ordinal), System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= 0) {
            return null;
        }

        double? seconds = unit switch {
            ".seconds" => value,
            ".milliseconds" => value / 1000,
            ".minutes" => value * 60,
            _ => null,
        };
        return seconds is { } length and < 1e9 ? TimeSpan.FromSeconds(length) : null;
    }
}
