namespace Sable.Syntax;

/// <summary>
///     Evaluates the restricted expression after <c>#if</c> and <c>#elseif</c>.
/// </summary>
/// <remarks>
///     <para>
///         The grammar, per spec/grammar.ebnf: identifiers, <c>true</c>,
///         <c>false</c>, the predicates <c>os(...)</c>, <c>arch(...)</c> and
///         <c>feature(...)</c>, the operators <c>!</c>, <c>&amp;&amp;</c> and
///         <c>||</c>, and parentheses. Nothing else, because a directive condition is
///         evaluated before any semantic analysis exists.
///     </para>
///     <para>
///         A malformed condition evaluates to <see langword="false" /> and
///         reports a diagnostic, so the region is simply disabled rather than
///         derailing the parse.
///     </para>
/// </remarks>
internal sealed class DirectiveCondition {
    readonly string _text;
    readonly ParseOptions _options;
    int _position;
    bool _failed;

    DirectiveCondition(string text, ParseOptions options) {
        _text = text;
        _options = options;
    }

    /// <summary>Evaluates a condition, reporting whether it was well formed.</summary>
    public static bool Evaluate(string text, ParseOptions options, out string? error) {
        var evaluator = new DirectiveCondition(text, options);
        var value = evaluator.ParseOr();
        evaluator.SkipWhitespace();

        if (!evaluator._failed && evaluator._position < evaluator._text.Length) {
            error = $"unexpected '{evaluator._text[evaluator._position..].Trim()}' "
                + "in a conditional-compilation condition";
            return false;
        }

        error = evaluator._failed
            ? "a conditional-compilation condition may only use identifiers, "
            + "os(), arch(), feature(), '!', '&&', '||' and parentheses"
            : null;

        return error is null && value;
    }

    void SkipWhitespace() {
        while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) {
            _position++;
        }
    }

    bool AtEnd {
        get {
            SkipWhitespace();
            return _position >= _text.Length;
        }
    }

    bool Matches(string token) {
        SkipWhitespace();
        if (_position + token.Length > _text.Length) {
            return false;
        }

        if (string.CompareOrdinal(_text, _position, token, 0, token.Length) != 0) {
            return false;
        }

        _position += token.Length;
        return true;
    }

    bool ParseOr() {
        var value = ParseAnd();
        while (Matches("||")) {
            value = ParseAnd() || value;
        }

        return value;
    }

    bool ParseAnd() {
        var value = ParseUnary();
        while (Matches("&&")) {
            value = ParseUnary() && value;
        }

        return value;
    }

    bool ParseUnary() => Matches("!") ? !ParseUnary() : ParsePrimary();

    bool ParsePrimary() {
        if (AtEnd) {
            _failed = true;
            return false;
        }

        if (Matches("(")) {
            var value = ParseOr();
            if (!Matches(")")) {
                _failed = true;
            }

            return value;
        }

        var name = ReadIdentifier();
        if (name.Length == 0) {
            _failed = true;
            return false;
        }

        switch (name) {
            case "true": return true;
            case "false": return false;

            case "os":
                return ReadPredicateArgument() is { } os
                    && string.Equals(os, _options.TargetOs, StringComparison.OrdinalIgnoreCase);

            case "arch":
                return ReadPredicateArgument() is { } arch
                    && string.Equals(arch, _options.TargetArch, StringComparison.OrdinalIgnoreCase);

            case "feature":
                return ReadPredicateArgument() is { } feature
                    && _options.Features.Contains(feature);

            default:
                return _options.IsDefined(name);
        }
    }

    string ReadIdentifier() {
        SkipWhitespace();
        var start = _position;
        while (_position < _text.Length && SyntaxFacts.IsIdentifierContinue(_text[_position])) {
            _position++;
        }

        return _text[start.._position];
    }

    /// <summary>Reads <c>(name)</c> after a predicate, or fails.</summary>
    string? ReadPredicateArgument() {
        if (!Matches("(")) {
            _failed = true;
            return null;
        }

        var argument = ReadIdentifier();
        if (argument.Length == 0 || !Matches(")")) {
            _failed = true;
            return null;
        }

        return argument;
    }
}
