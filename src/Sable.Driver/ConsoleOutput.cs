using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sable.Driver;

/// <summary>
///     A writer that hands each whole line it is given to a callback: what the
///     colouring and the JSON writers below are built on. Every command writes
///     its diagnostics and status lines to standard error a line at a time, so
///     the line is the unit they work on.
/// </summary>
public abstract class LineWriter : TextWriter {
    readonly StringBuilder _pending = new();
    readonly Lock _gate = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value) {
        lock (_gate) {
            if (value == '\n') {
                var line = _pending.ToString().TrimEnd('\r');
                _pending.Clear();
                WriteLineCore(line);
            } else {
                _pending.Append(value);
            }
        }
    }

    public override void Write(string? value) {
        foreach (var c in value ?? "") {
            Write(c);
        }
    }

    public override void Flush() {
        lock (_gate) {
            if (_pending.Length > 0) {
                var line = _pending.ToString();
                _pending.Clear();
                WriteLineCore(line);
            }

            FlushCore();
        }
    }

    protected abstract void WriteLineCore(string line);

    protected virtual void FlushCore() {
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            Flush();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
///     Colours standard error as Cargo does: <c>error</c> red, <c>warning</c>
///     yellow, a location's arrow blue, and the right-aligned verb of a status
///     line (<c>   Compiling</c>, <c>    Finished</c>) green. The text is the
///     same as without colour; only escape codes are added.
/// </summary>
public sealed partial class ColorWriter(TextWriter inner) : LineWriter {
    const string Reset = "\e[0m";
    const string Red = "\e[1;31m";
    const string Yellow = "\e[1;33m";
    const string Green = "\e[1;32m";
    const string Blue = "\e[1;34m";

    [GeneratedRegex(@"^(error|warning)(\[S\d{4}\])?(:)")]
    private static partial Regex Level();

    [GeneratedRegex(@"^(\s*)(-->|\|)")]
    private static partial Regex Gutter();

    /// <summary>Colours one line of standard error.</summary>
    public static string Colour(string line) {
        if (Level().Match(line) is { Success: true } level) {
            var colour = level.Groups[1].Value == "error" ? Red : Yellow;
            return colour + level.Value + Reset + line[level.Length..];
        }

        // The status verb is right-aligned to twelve columns: '   Compiling app'.
        if (line.Length > 12 && line[12] == ' ' && line[..12].TrimStart() is { Length: > 1 } verb && line[..12].StartsWith(' ')
            && char.IsAsciiLetterUpper(verb[0]) && verb.Skip(1).All(char.IsAsciiLetterLower)) {
            return Green + line[..12] + Reset + line[12..];
        }

        return Gutter().Match(line) is { Success: true } gutter
            ? gutter.Groups[1].Value + Blue + gutter.Groups[2].Value + Reset + line[gutter.Length..]
            : line;
    }

    protected override void WriteLineCore(string line) => inner.WriteLine(Colour(line));

    protected override void FlushCore() => inner.Flush();
}

/// <summary>
///     <c>--message-format=json</c>: what a command writes to standard error,
///     turned into JSON objects, one a line, on standard output — what an
///     editor or CI reads instead of matching text with a regular expression.
/// </summary>
/// <remarks>
///     <para>
///         A diagnostic becomes <c>{"type":"diagnostic","severity","code",
///         "message","file","line","column","rendered"}</c>, where
///         <c>rendered</c> is the text a person would have seen; a manifest's
///         error (<c>error: file:line: key: message</c>) the same without a
///         code or column; any other line, such as a status line,
///         <c>{"type":"message","text"}</c>. The command's own output on
///         standard output (a program's, <c>sable tree</c>'s) is left as it is,
///         but for the diagnostics among it (<c>sable lint</c> prints its
///         findings there): <paramref name="passThrough" /> is that writer.
///     </para>
///     <para>
///         It reads the layout <see cref="Sable.Syntax.Diagnostic.Format" />
///         writes — a header line, then the <c>--></c> location and the
///         excerpt — and nothing else, so the two are tested together.
///     </para>
/// </remarks>
public sealed partial class JsonMessageWriter(TextWriter output, bool passThrough = false) : LineWriter {
    [GeneratedRegex(@"^(error|warning|info|note)\[(S\d{4})\]: (.*)$")]
    private static partial Regex Header();

    [GeneratedRegex(@"^\s*--> (.+):(\d+):(\d+)$")]
    private static partial Regex Location();

    [GeneratedRegex(@"^\s*(\d+ )?\|")]
    private static partial Regex Excerpt();

    [GeneratedRegex(@"^(error|warning): (?:(.+?):(\d+): )?(.*)$")]
    private static partial Regex Plain();

    sealed class Pending {
        public required string Severity;
        public required string Code;
        public required string Message;
        public string? File;
        public int? Line;
        public int? Column;
        public readonly List<string> Rendered = [];
    }

    Pending? _diagnostic;

    protected override void WriteLineCore(string line) {
        if (_diagnostic is { } open) {
            if (Location().Match(line) is { Success: true } location && open.File is null) {
                open.File = location.Groups[1].Value;
                open.Line = int.Parse(location.Groups[2].Value, CultureInfo.InvariantCulture);
                open.Column = int.Parse(location.Groups[3].Value, CultureInfo.InvariantCulture);
                open.Rendered.Add(line);
                return;
            }

            if (Excerpt().IsMatch(line) || line.StartsWith("   = ", StringComparison.Ordinal)) {
                open.Rendered.Add(line);
                return;
            }

            Emit(open);
            _diagnostic = null;
        }

        if (Header().Match(line) is { Success: true } header) {
            _diagnostic = new() { Severity = header.Groups[1].Value, Code = header.Groups[2].Value, Message = header.Groups[3].Value, Rendered = { line } };
            return;
        }

        if (passThrough) {
            output.WriteLine(line);
            return;
        }

        if (line.Trim().Length == 0) {
            return;
        }

        if (Plain().Match(line) is { Success: true } plain) {
            WriteObject(writer => {
                writer.WriteString("type", "diagnostic");
                writer.WriteString("severity", plain.Groups[1].Value);
                writer.WriteNull("code");
                writer.WriteString("message", plain.Groups[4].Value);
                if (plain.Groups[2].Success) {
                    writer.WriteString("file", plain.Groups[2].Value);
                    writer.WriteNumber("line", int.Parse(plain.Groups[3].Value, CultureInfo.InvariantCulture));
                }

                writer.WriteString("rendered", line);
            });
            return;
        }

        WriteObject(writer => {
            writer.WriteString("type", "message");
            writer.WriteString("text", line);
        });
    }

    void Emit(Pending diagnostic) =>
        WriteObject(writer => {
            writer.WriteString("type", "diagnostic");
            writer.WriteString("severity", diagnostic.Severity);
            writer.WriteString("code", diagnostic.Code);
            writer.WriteString("message", diagnostic.Message);
            if (diagnostic.File is not null) {
                writer.WriteString("file", diagnostic.File);
                writer.WriteNumber("line", diagnostic.Line!.Value);
                writer.WriteNumber("column", diagnostic.Column!.Value);
            }

            writer.WriteString("rendered", string.Join('\n', diagnostic.Rendered));
        });

    void WriteObject(Action<Utf8JsonWriter> body) {
        using var buffer = new MemoryStream();
        // Relaxed: a message's quotes and arrows stay readable; each object is one line all the same.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        output.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    protected override void FlushCore() {
        if (_diagnostic is { } open) {
            Emit(open);
            _diagnostic = null;
        }

        output.Flush();
    }
}
