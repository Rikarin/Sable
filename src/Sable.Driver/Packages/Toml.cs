using System.Globalization;
using System.Text;

namespace Sable.Driver.Packages;

/// <summary>Where something is in a TOML file: 1-based line and column.</summary>
public readonly record struct TomlPosition(int Line, int Column) {
    public override string ToString() => $"{Line}:{Column}";
}

/// <summary>A TOML file that does not parse, with the place it went wrong.</summary>
public sealed class TomlException(string file, TomlPosition position, string reason)
    : Exception($"{file}:{position.Line}:{position.Column}: {reason}") {
    public string File { get; } = file;
    public TomlPosition Position { get; } = position;
    public string Reason { get; } = reason;
}

/// <summary>A TOML value, with where it was written.</summary>
public abstract class TomlValue {
    public TomlPosition Position { get; init; }

    /// <summary>What kind of value this is, as an error message names it.</summary>
    public abstract string Kind { get; }
}

public sealed class TomlString(string value) : TomlValue {
    public string Value { get; } = value;
    public override string Kind => "a string";
}

public sealed class TomlInteger(long value) : TomlValue {
    public long Value { get; } = value;
    public override string Kind => "an integer";
}

public sealed class TomlFloat(double value) : TomlValue {
    public double Value { get; } = value;
    public override string Kind => "a float";
}

public sealed class TomlBoolean(bool value) : TomlValue {
    public bool Value { get; } = value;
    public override string Kind => "a boolean";
}

/// <summary>A date, time or date-time, kept as written: manifests do not need its parts.</summary>
public sealed class TomlDateTime(string text) : TomlValue {
    public string Text { get; } = text;
    public override string Kind => "a date-time";
}

public sealed class TomlArray : TomlValue {
    public List<TomlValue> Items { get; } = [];

    /// <summary>Made by <c>[[header]]</c> lines rather than written as a value.</summary>
    public bool IsArrayOfTables { get; init; }

    public override string Kind => IsArrayOfTables ? "an array of tables" : "an array";
}

/// <summary>A table: keys in the order written, each with where its key was.</summary>
public sealed class TomlTable : TomlValue {
    readonly Dictionary<string, TomlValue> _values = new(StringComparer.Ordinal);
    readonly List<string> _order = [];
    readonly Dictionary<string, TomlPosition> _keyPositions = new(StringComparer.Ordinal);

    public override string Kind => "a table";

    /// <summary>Written as <c>{ … }</c>: closed once written.</summary>
    public bool IsInline { get; init; }

    /// <summary>Created by a <c>[header]</c> line, so another may not create it again.</summary>
    internal bool DefinedByHeader { get; set; }

    /// <summary>Created by a dotted key's prefix, so a header may not open it later.</summary>
    internal bool DefinedByDottedKey { get; set; }

    public IEnumerable<string> Keys => _order;

    public int Count => _order.Count;

    public IEnumerable<KeyValuePair<string, TomlValue>> Entries => _order.Select(k => new KeyValuePair<string, TomlValue>(k, _values[k]));

    public TomlValue? this[string key] => _values.GetValueOrDefault(key);

    public bool TryGetValue(string key, out TomlValue value) => _values.TryGetValue(key, out value!);

    /// <summary>Where <paramref name="key" /> was written, or where its value is.</summary>
    public TomlPosition KeyPosition(string key) => _keyPositions.TryGetValue(key, out var p) ? p : _values.TryGetValue(key, out var v) ? v.Position : Position;

    internal void Add(string key, TomlValue value, TomlPosition keyPosition) {
        _values.Add(key, value);
        _order.Add(key);
        _keyPositions[key] = keyPosition;
    }
}

/// <summary>
///     A reader for TOML 1.0 — the parts a manifest and a lock file use, which
///     is nearly all of it: tables, arrays of tables, inline tables, dotted
///     and quoted keys, basic, literal and multi-line strings, integers in
///     every base, floats, booleans, arrays and comments. Dates and times are
///     read and kept as text. Every error names the file, line and column.
/// </summary>
public sealed class TomlReader {
    readonly string _text;
    readonly string _file;
    int _position;
    int _line = 1;
    int _lineStart;

    TomlReader(string text, string file) {
        _text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        _file = file;
    }

    /// <summary>Parses <paramref name="text" />, read from <paramref name="file" />.</summary>
    /// <exception cref="TomlException">The text is not TOML.</exception>
    public static TomlTable Parse(string text, string file = "<toml>") => new TomlReader(text, file).ParseDocument();

    TomlPosition Here => new(_line, _position - _lineStart + 1);

    TomlException Error(string reason, TomlPosition? at = null) => new(_file, at ?? Here, reason);

    bool AtEnd => _position >= _text.Length;

    char Current => AtEnd ? '\0' : _text[_position];

    char Peek(int offset) => _position + offset < _text.Length ? _text[_position + offset] : '\0';

    void Advance() {
        if (Current == '\n') {
            _line++;
            _lineStart = _position + 1;
        }

        _position++;
    }

    void SkipSpaces() {
        while (Current is ' ' or '\t') {
            Advance();
        }
    }

    void SkipComment() {
        if (Current != '#') {
            return;
        }

        while (!AtEnd && Current != '\n') {
            if (Current < 0x20 && Current != '\t') {
                throw Error("a control character is not allowed in a comment");
            }

            Advance();
        }
    }

    /// <summary>After a key-value pair or header: only a comment may follow on the line.</summary>
    void ExpectEndOfLine() {
        SkipSpaces();
        SkipComment();
        if (AtEnd) {
            return;
        }

        if (Current != '\n') {
            throw Error($"expected the end of the line, found '{Current}'");
        }

        Advance();
    }

    /// <summary>Spaces, newlines and comments, as inside an array.</summary>
    void SkipWhitespaceAndComments() {
        while (true) {
            SkipSpaces();
            SkipComment();
            if (Current == '\n') {
                Advance();
                continue;
            }

            return;
        }
    }

    TomlTable ParseDocument() {
        var root = new TomlTable { Position = new(1, 1) };
        var current = root;

        while (true) {
            SkipWhitespaceAndComments();
            if (AtEnd) {
                return root;
            }

            if (Current == '[') {
                current = ParseHeader(root);
            } else {
                ParseKeyValue(current);
            }

            ExpectEndOfLine();
        }
    }

    TomlTable ParseHeader(TomlTable root) {
        var start = Here;
        Advance();
        var arrayOfTables = Current == '[';
        if (arrayOfTables) {
            Advance();
        }

        SkipSpaces();
        var keys = ParseKey();
        SkipSpaces();
        if (Current != ']' || (arrayOfTables && Peek(1) != ']')) {
            throw Error(arrayOfTables ? "expected ']]' to close the table header" : "expected ']' to close the table header");
        }

        Advance();
        if (arrayOfTables) {
            Advance();
        }

        // Every key but the last names a table on the way, made if missing.
        var table = root;
        for (var i = 0; i < keys.Count - 1; i++) {
            var (key, position) = keys[i];
            if (!table.TryGetValue(key, out var next)) {
                var made = new TomlTable { Position = position };
                table.Add(key, made, position);
                table = made;
            } else {
                table = next switch {
                    TomlTable { IsInline: true } => throw Error($"'{Dotted(keys, i)}' is an inline table, closed once written", position),
                    TomlTable t => t,
                    TomlArray { IsArrayOfTables: true } array => (TomlTable)array.Items[^1],
                    _ => throw Error($"'{Dotted(keys, i)}' is already {next.Kind}, not a table", position),
                };
            }
        }

        var (last, lastPosition) = keys[^1];
        var name = Dotted(keys, keys.Count - 1);
        if (arrayOfTables) {
            var element = new TomlTable { Position = start, DefinedByHeader = true };
            if (!table.TryGetValue(last, out var existing)) {
                var array = new TomlArray { Position = start, IsArrayOfTables = true };
                array.Items.Add(element);
                table.Add(last, array, lastPosition);
            } else if (existing is TomlArray { IsArrayOfTables: true } array) {
                array.Items.Add(element);
            } else {
                throw Error($"'{name}' is already {existing.Kind}, so '[[{name}]]' cannot add to it", lastPosition);
            }

            return element;
        }

        if (table.TryGetValue(last, out var defined)) {
            if (defined is TomlTable { IsInline: false, DefinedByHeader: false, DefinedByDottedKey: false } implicitTable) {
                implicitTable.DefinedByHeader = true;
                return implicitTable;
            }

            throw Error(defined is TomlTable ? $"the table '{name}' is defined twice" : $"'{name}' is already {defined.Kind}, not a table", lastPosition);
        }

        var created = new TomlTable { Position = start, DefinedByHeader = true };
        table.Add(last, created, lastPosition);
        return created;
    }

    static string Dotted(List<(string Key, TomlPosition Position)> keys, int through) =>
        string.Join('.', keys.Take(through + 1).Select(static k => k.Key.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') && k.Key.Length > 0 ? k.Key : $"\"{k.Key}\""));

    void ParseKeyValue(TomlTable table) {
        var keys = ParseKey();
        SkipSpaces();
        if (Current != '=') {
            throw Error(AtEnd || Current == '\n' ? $"expected '=' after the key '{Dotted(keys, keys.Count - 1)}'" : $"expected '=' after the key, found '{Current}'");
        }

        Advance();
        SkipSpaces();
        var value = ParseValue();

        // A dotted key makes (or extends) the tables named on the way.
        for (var i = 0; i < keys.Count - 1; i++) {
            var (key, position) = keys[i];
            if (!table.TryGetValue(key, out var next)) {
                var made = new TomlTable { Position = position, DefinedByDottedKey = true };
                table.Add(key, made, position);
                table = made;
            } else if (next is TomlTable { IsInline: false, DefinedByHeader: false } t) {
                table = t;
            } else {
                throw Error($"'{Dotted(keys, i)}' is already {next.Kind}, so a dotted key cannot add to it", position);
            }
        }

        var (last, lastPosition) = keys[^1];
        if (table.TryGetValue(last, out _)) {
            throw Error($"the key '{Dotted(keys, keys.Count - 1)}' is defined twice", lastPosition);
        }

        table.Add(last, value, lastPosition);
    }

    List<(string Key, TomlPosition Position)> ParseKey() {
        var keys = new List<(string, TomlPosition)>();
        while (true) {
            SkipSpaces();
            var position = Here;
            string key;
            if (Current == '"') {
                key = ParseBasicString();
            } else if (Current == '\'') {
                key = ParseLiteralString();
            } else {
                var start = _position;
                while (char.IsAsciiLetterOrDigit(Current) || Current is '-' or '_') {
                    Advance();
                }

                if (_position == start) {
                    throw Error(AtEnd || Current == '\n' ? "expected a key" : $"'{Current}' cannot start a key");
                }

                key = _text[start.._position];
            }

            keys.Add((key, position));
            SkipSpaces();
            if (Current != '.') {
                return keys;
            }

            Advance();
        }
    }

    TomlValue ParseValue() {
        var position = Here;
        TomlValue value = Current switch {
            '"' when Peek(1) == '"' && Peek(2) == '"' => new TomlString(ParseMultilineBasicString()),
            '"' => new TomlString(ParseBasicString()),
            '\'' when Peek(1) == '\'' && Peek(2) == '\'' => new TomlString(ParseMultilineLiteralString()),
            '\'' => new TomlString(ParseLiteralString()),
            '[' => ParseArray(),
            '{' => ParseInlineTable(),
            't' or 'f' => ParseBoolean(),
            '\n' or '\0' or '#' => throw Error("expected a value after '='"),
            _ => ParseNumberOrDate(),
        };

        return value switch {
            TomlString s => new TomlString(s.Value) { Position = position },
            TomlBoolean b => new TomlBoolean(b.Value) { Position = position },
            _ => value,
        };
    }

    TomlBoolean ParseBoolean() {
        foreach (var (word, value) in new[] { ("true", true), ("false", false) }) {
            if (string.CompareOrdinal(_text, _position, word, 0, word.Length) == 0 && !IsBareChar(Peek(word.Length))) {
                for (var i = 0; i < word.Length; i++) {
                    Advance();
                }

                return new(value);
            }
        }

        throw Error($"'{Word()}' is not a value: a string needs quotes");
    }

    static bool IsBareChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';

    string Word() {
        var end = _position;
        while (end < _text.Length && !char.IsWhiteSpace(_text[end]) && _text[end] is not (',' or ']' or '}' or '#')) {
            end++;
        }

        return _text[_position..end];
    }

    TomlValue ParseNumberOrDate() {
        var position = Here;
        var word = Word();
        if (word.Length == 0) {
            throw Error($"'{Current}' cannot start a value");
        }

        // A date or time: digits with '-' at index 4, or ':' at index 2.
        if (word.Length >= 5 && char.IsAsciiDigit(word[0]) && (word[4] == '-' || word[2] == ':')) {
            var text = word;
            for (var i = 0; i < word.Length; i++) {
                Advance();
            }

            // A date and a time may be separated by a space.
            if (word.Length == 10 && Current == ' ' && char.IsAsciiDigit(Peek(1))) {
                Advance();
                var time = Word();
                for (var i = 0; i < time.Length; i++) {
                    Advance();
                }

                text += " " + time;
            }

            return new TomlDateTime(text) { Position = position };
        }

        for (var i = 0; i < word.Length; i++) {
            Advance();
        }

        if (ParseInteger(word) is { } integer) {
            return new TomlInteger(integer) { Position = position };
        }

        if (ParseFloat(word) is { } number) {
            return new TomlFloat(number) { Position = position };
        }

        throw Error($"'{word}' is not a value: a string needs quotes", position);
    }

    static long? ParseInteger(string word) {
        var (style, digits, sign) = word switch {
            ['0', 'x', .. var rest] => (16, rest, 1),
            ['0', 'o', .. var rest] => (8, rest, 1),
            ['0', 'b', .. var rest] => (2, rest, 1),
            ['+', .. var rest] => (10, rest, 1),
            ['-', .. var rest] => (10, rest, -1),
            _ => (10, word, 1),
        };

        if (digits.Length == 0 || digits[0] == '_' || digits[^1] == '_' || digits.Contains("__", StringComparison.Ordinal)) {
            return null;
        }

        // No leading zeros in a decimal integer.
        if (style == 10 && digits.Length > 1 && digits[0] == '0') {
            return null;
        }

        var clean = digits.Replace("_", "", StringComparison.Ordinal);
        try {
            return style switch {
                10 when clean.All(char.IsAsciiDigit) => sign * long.Parse(clean, CultureInfo.InvariantCulture),
                16 when clean.All(char.IsAsciiHexDigit) => Convert.ToInt64(clean, 16),
                8 when clean.All(static c => c is >= '0' and <= '7') => Convert.ToInt64(clean, 8),
                2 when clean.All(static c => c is '0' or '1') => Convert.ToInt64(clean, 2),
                _ => null,
            };
        } catch (OverflowException) {
            return null;
        }
    }

    static double? ParseFloat(string word) {
        switch (word) {
            case "inf" or "+inf": return double.PositiveInfinity;
            case "-inf": return double.NegativeInfinity;
            case "nan" or "+nan" or "-nan": return double.NaN;
        }

        if (word.Contains("__", StringComparison.Ordinal) || word.StartsWith('_') || word.EndsWith('_') || word.Contains("._", StringComparison.Ordinal) || word.Contains("_.", StringComparison.Ordinal)) {
            return null;
        }

        var clean = word.Replace("_", "", StringComparison.Ordinal);
        if (!clean.Any(static c => c is '.' or 'e' or 'E') || clean.EndsWith('.') || clean.StartsWith('.') || clean.Contains(".e", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    TomlArray ParseArray() {
        var array = new TomlArray { Position = Here };
        Advance();
        while (true) {
            SkipWhitespaceAndComments();
            if (Current == ']') {
                Advance();
                return array;
            }

            if (AtEnd) {
                throw Error("the array is not closed with ']'", array.Position);
            }

            array.Items.Add(ParseValue());
            SkipWhitespaceAndComments();
            if (Current == ',') {
                Advance();
            } else if (Current != ']') {
                throw Error(AtEnd ? "the array is not closed with ']'" : $"expected ',' or ']' in the array, found '{Current}'", AtEnd ? array.Position : null);
            }
        }
    }

    TomlTable ParseInlineTable() {
        var table = new TomlTable { Position = Here, IsInline = true };
        Advance();
        SkipSpaces();
        if (Current == '}') {
            Advance();
            return table;
        }

        while (true) {
            SkipSpaces();
            if (Current == '\n' || AtEnd) {
                throw Error("an inline table must be written on one line");
            }

            ParseKeyValue(table);
            SkipSpaces();
            if (Current == '}') {
                Advance();
                return table;
            }

            if (Current != ',') {
                throw Error(Current == '\n' || AtEnd ? "an inline table must be written on one line" : $"expected ',' or '}}' in the inline table, found '{Current}'");
            }

            Advance();
            SkipSpaces();
            if (Current == '}') {
                throw Error("a trailing comma is not allowed in an inline table");
            }
        }
    }

    string ParseLiteralString() {
        var start = Here;
        Advance();
        var builder = new StringBuilder();
        while (Current != '\'') {
            if (AtEnd || Current == '\n') {
                throw Error("the string is not closed on its line", start);
            }

            builder.Append(Current);
            Advance();
        }

        Advance();
        return builder.ToString();
    }

    string ParseMultilineLiteralString() {
        var start = Here;
        Advance();
        Advance();
        Advance();
        if (Current == '\n') {
            Advance();
        }

        var builder = new StringBuilder();
        while (true) {
            if (AtEnd) {
                throw Error("the multi-line string is not closed with '''", start);
            }

            if (Current == '\'' && Peek(1) == '\'' && Peek(2) == '\'') {
                // Up to two quotes may sit just before the closing three.
                var run = 0;
                while (Peek(run) == '\'') {
                    run++;
                }

                builder.Append('\'', Math.Min(run - 3, 2));
                for (var i = 0; i < Math.Min(run, 5); i++) {
                    Advance();
                }

                return builder.ToString();
            }

            builder.Append(Current);
            Advance();
        }
    }

    string ParseBasicString() {
        var start = Here;
        Advance();
        var builder = new StringBuilder();
        while (Current != '"') {
            if (AtEnd || Current == '\n') {
                throw Error("the string is not closed on its line", start);
            }

            if (Current == '\\') {
                builder.Append(ParseEscape());
                continue;
            }

            if (Current < 0x20 && Current != '\t') {
                throw Error("a control character must be escaped in a string");
            }

            builder.Append(Current);
            Advance();
        }

        Advance();
        return builder.ToString();
    }

    string ParseMultilineBasicString() {
        var start = Here;
        Advance();
        Advance();
        Advance();
        if (Current == '\n') {
            Advance();
        }

        var builder = new StringBuilder();
        while (true) {
            if (AtEnd) {
                throw Error("the multi-line string is not closed with \"\"\"", start);
            }

            if (Current == '"' && Peek(1) == '"' && Peek(2) == '"') {
                var run = 0;
                while (Peek(run) == '"') {
                    run++;
                }

                builder.Append('"', Math.Min(run - 3, 2));
                for (var i = 0; i < Math.Min(run, 5); i++) {
                    Advance();
                }

                return builder.ToString();
            }

            if (Current == '\\') {
                // A backslash at the end of a line trims the line break and
                // the whitespace after it.
                var look = 1;
                while (Peek(look) is ' ' or '\t') {
                    look++;
                }

                if (Peek(look) == '\n') {
                    Advance();
                    while (Current is ' ' or '\t' or '\n') {
                        Advance();
                    }

                    continue;
                }

                builder.Append(ParseEscape());
                continue;
            }

            builder.Append(Current);
            Advance();
        }
    }

    string ParseEscape() {
        var position = Here;
        Advance();
        var c = Current;
        Advance();
        switch (c) {
            case 'b': return "\b";
            case 't': return "\t";
            case 'n': return "\n";
            case 'f': return "\f";
            case 'r': return "\r";
            case 'e': return "\u001b";
            case '"': return "\"";
            case '\\': return "\\";
            case 'u' or 'U':
                var length = c == 'u' ? 4 : 8;
                var hex = _position + length <= _text.Length ? _text.Substring(_position, length) : "";
                if (hex.Length != length || !hex.All(char.IsAsciiHexDigit)) {
                    throw Error($"'\\{c}' needs {length} hexadecimal digits", position);
                }

                var code = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (code is > 0x10FFFF or (>= 0xD800 and <= 0xDFFF)) {
                    throw Error($"'\\{c}{hex}' is not a Unicode scalar value", position);
                }

                for (var i = 0; i < length; i++) {
                    Advance();
                }

                return char.ConvertFromUtf32(code);
            default:
                throw Error($"'\\{c}' is not an escape TOML knows", position);
        }
    }
}

/// <summary>Writes TOML strings and keys the way the reader reads them back.</summary>
public static class TomlText {
    public static string Quote(string value) {
        var builder = new StringBuilder("\"");
        foreach (var c in value) {
            builder.Append(c switch {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\t' => "\\t",
                '\r' => "\\r",
                < ' ' => $"\\u{(int)c:X4}",
                _ => c.ToString(),
            });
        }

        return builder.Append('"').ToString();
    }

    /// <summary>A key, bare when it can be.</summary>
    public static string Key(string key) => key.Length > 0 && key.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? key : Quote(key);

    public static string Array(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(Quote)) + "]";
}
