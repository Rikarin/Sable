using System.Text;
using System.Text.RegularExpressions;
using Sable.LanguageServer;
using Sable.SIL;
using Sable.Syntax;

namespace Sable.Fuzz;

/// <summary>
///     The language server's documents (<see cref="Workspace" />): a document
///     opened, then edited as an editor edits it — ranges by line and UTF-16
///     column, several to a message, now and then the whole text at once —
///     holds exactly the text the edits make, parsed by the incremental parser
///     into the very tree, and the very diagnostics, a full parse of that text
///     gives. And positions and offsets map back and forth.
/// </summary>
/// <remarks>
///     The input is the document, then its edits, each after a NUL: an edit's
///     text is its replacement, and a hash of it says where it goes, how much
///     it replaces, and whether it ends a message. The ParserTarget edits by
///     offset; this one by the protocol's positions, through
///     <see cref="LineIndex" />, which has its own ways to go wrong: CRLF and a
///     lone CR, a column past a line's end, a line past the last. The edits
///     past a budget of <see cref="Work" /> are not made.
/// </remarks>
public sealed class EditorTarget : Target {
    public override string Name => "editor";

    public override TimeSpan Limit => TimeSpan.FromSeconds(10);

    const string Uri = "file:///fuzz/editor.sb";

    /// <summary>
    ///     How much text an input's edits may have the editor and the check go
    ///     through: the sum, over the edits, of the document's length. An edit
    ///     costs in proportion to the document — the line index, the
    ///     incremental parse, and the full parse and the walk of both trees
    ///     that check it — while it costs the input as little as a NUL, so
    ///     the bytes libFuzzer caps do not cap the work: a 16 KB document and
    ///     16 K empty edits are a quarter of a billion characters. This is a
    ///     hundred-odd edits of a 16 KB document, thousands of a small one.
    /// </summary>
    const long Work = 2_000_000;

    public override void Run(ReadOnlySpan<byte> data) {
        var parts = Text(data).Split('\0');
        var workspace = new Workspace();
        workspace.Open(Uri, parts[0], 1);
        var expected = parts[0];

        var message = new List<(LspRange? Range, string Text)>();
        var version = 1;

        void Send() {
            if (message.Count == 0) {
                return;
            }

            workspace.Change(Uri, ++version, message.ToList());
            message.Clear();
            Check(workspace, expected, version);
        }

        var work = 0L;
        foreach (var replacement in parts.Skip(1)) {
            // The edits past the budget are left out, as libFuzzer's -max_len
            // leaves out the bytes past it.
            work += expected.Length + replacement.Length + 1;
            if (work > Work) {
                break;
            }

            var hash = Hash(replacement);
            if (hash % 17 == 0) {
                // The whole text, as a client that does not send ranges does.
                message.Add((null, replacement));
                expected = replacement;
            } else {
                var lines = LineIndex.Of(expected);
                var start = new LspPosition((int)(hash % (uint)(lines.LineCount + 1)), (int)(hash / 7 % 41));
                var end = hash / 13 % 3 == 0 ? start : new LspPosition(start.Line + (int)(hash / 17 % 3), (int)(hash / 19 % 41));
                message.Add((new LspRange(start, end), replacement));

                // What the protocol says the edit does: a range's ends, clamped
                // to the text, and an end before the start taken as the start.
                var from = lines.OffsetOf(start);
                var to = Math.Max(from, lines.OffsetOf(end));
                expected = expected[..from] + replacement + expected[to..];
            }

            if (hash / 23 % 3 == 0) {
                Send();
            }
        }

        Send();
        Check(workspace, expected, version);
    }

    static void Check(Workspace workspace, string expected, int version) {
        var document = workspace.Current.Find(Uri) ?? throw new BrokenPromise("the open document is gone");
        if (document.Version != version) {
            throw new BrokenPromise($"the document is at version {document.Version}, not {version}");
        }

        var tree = document.Tree;
        if (tree.ToFullString() != expected) {
            throw new BrokenPromise($"the document's tree gives back other text than the edits made\n---- expected ----\n{expected}\n---- the tree ----\n{tree.ToFullString()}");
        }

        var full = SyntaxTree.Parse(new SourceText(expected, tree.Text.FileName), tree.Options);
        if (!ParserTarget.SameTree(tree.Root, full.Root)) {
            throw new BrokenPromise($"the edited document's tree differs from a full parse\n---- full ----\n{ParserTarget.Dump(full.Root)}\n---- edited ----\n{ParserTarget.Dump(tree.Root)}");
        }

        if (!full.Diagnostics.Select(static d => (d.Id, d.Span)).SequenceEqual(tree.Diagnostics.Select(static d => (d.Id, d.Span)))) {
            var expectedDiagnostics = string.Join(" ", full.Diagnostics.Select(static d => $"{d.Id}@{d.Span}"));
            var actualDiagnostics = string.Join(" ", tree.Diagnostics.Select(static d => $"{d.Id}@{d.Span}"));
            throw new BrokenPromise($"the edited document reports {actualDiagnostics}, a full parse {expectedDiagnostics}");
        }

        // Every offset is a position that maps back to it, but for one
        // between a CR and its LF, which no position names.
        var lines = document.Lines;
        for (var offset = 0; offset <= expected.Length; offset++) {
            if (offset > 0 && offset < expected.Length && expected[offset - 1] == '\r' && expected[offset] == '\n') {
                continue;
            }

            var position = lines.PositionOf(offset);
            if (lines.OffsetOf(position) != offset) {
                throw new BrokenPromise($"offset {offset} is position {position}, which is offset {lines.OffsetOf(position)}");
            }
        }
    }

    static uint Hash(string text) {
        var hash = 2166136261u;
        foreach (var c in text) {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }

    public override IEnumerable<(string, byte[])> Seeds(string root) {
        // Each source with an edit or two: a character typed, a line deleted.
        foreach (var (name, data) in SourcesUnder(root, "tests/corpus", "tests/programs", "examples")) {
            yield return (name, data);
            yield return (name + "-typed", [..data, 0, (byte)'x']);
            yield return (name + "-edited", [..data, 0, .."\r\n"u8, 0, .."}"u8, 0]);
        }
    }

    public override IEnumerable<string> Dictionary() => SableTokens().Concat(["\0", "\r", "\r\n", "\n\n", "\u00e9", "\U0001F600"]);
}

/// <summary>
///     SIL as text (<see cref="SilParser" />): any text is read or refused with
///     the parser's own error, never another; what reads prints as text that
///     reads back as itself; the verifier answers anything that reads, with
///     problems or none; and what it finds nothing wrong with, the passes
///     keep so.
/// </summary>
public sealed partial class SilTarget : Target {
    public override string Name => "sil";

    public override int MaxLength => 16 * 1024;

    public override TimeSpan Limit => TimeSpan.FromSeconds(10);

    public override void Run(ReadOnlySpan<byte> data) {
        SilModule module;
        try {
            module = SilParser.Parse(Text(data));
        } catch (SilParseException) {
            return;
        }

        var printed = SilPrinter.Print(module);
        SilModule reparsed;
        try {
            reparsed = SilParser.Parse(printed);
        } catch (SilParseException failure) {
            throw new BrokenPromise($"SIL that reads prints as SIL that does not: {failure.Message}\n{printed}");
        }

        if (SilPrinter.Print(reparsed) != printed) {
            throw new BrokenPromise($"SIL that reads prints as SIL that reads back as other SIL\n---- printed ----\n{printed}\n---- read back and printed ----\n{SilPrinter.Print(reparsed)}");
        }

        if (SilVerifier.Verify(module).Count > 0) {
            return;
        }

        try {
            SilPassManager.Run(module, SilPassManager.Default);
        } catch (SilPassException) {
            // The pass manager's own verdict: SIL a pass cannot take, said so.
            return;
        }

        if (SilVerifier.Verify(module) is { Count: > 0 } problems) {
            throw new BrokenPromise($"the passes made SIL the verifier accepted into SIL it rejects:\n{string.Join("\n", problems)}\n---- before ----\n{printed}");
        }
    }

    public override IEnumerable<(string, byte[])> Seeds(string root) {
        var seeds = new List<(string, byte[])>();
        var files = Path.Combine(root, "tests", "sil");
        if (Directory.Exists(files)) {
            seeds.AddRange(Directory.GetFiles(files, "*.sil").Order(StringComparer.Ordinal)
                .Select(f => ("sil_" + Path.GetFileName(f), File.ReadAllBytes(f))));
        }

        // And the SIL the SIL tests write out.
        foreach (var file in Directory.GetFiles(Path.Combine(root, "tests", "Sable.SIL.Tests"), "*.cs").Order(StringComparer.Ordinal)) {
            var blocks = RawStrings().Matches(File.ReadAllText(file)).Select(static m => Unindent(m.Groups[1].Value))
                .Where(static b => b.Contains("sil @", StringComparison.Ordinal) || b.Contains("sil_class", StringComparison.Ordinal) || b.Contains("sil_struct", StringComparison.Ordinal));
            seeds.AddRange(blocks.Select((b, i) => ($"{Path.GetFileNameWithoutExtension(file)}-{i}", Encoding.UTF8.GetBytes(b))));
        }

        return seeds;
    }

    /// <summary>A raw string's text, without the indentation its closing quotes give it.</summary>
    static string Unindent(string block) {
        var lines = block.Split('\n');
        var indent = lines.Where(static l => l.Trim().Length > 0).Select(static l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()));
    }

    [GeneratedRegex("\"\"\"\\n(.*?)\"\"\"", RegexOptions.Singleline)]
    private static partial Regex RawStrings();

    public override IEnumerable<string> Dictionary() => [
        "sil @", "sil_struct ", "sil_class ", "sil_enum ", "sil_global @", "sil_extern @", "sil_export @", " : $", " = ", "(", ")", "{", "}", "[", "]",
        ", ", ": ", "->", "$", "$Int", "$Bool", "$F64", "$String", "$Char", "$Object", "$()", "$*Int", "$[Int]", "$[Int:]", "$@thick () -> ()",
        "bb0", "bb1", "bb2", "%0", "%1", "%2", "%3", "#x", "@f", "\n", "\n}\n", "  ", " async", " throws(", "@owned ", "@noncopy", "@inline", "@sil_weak ",
        "@sil_unowned ", "@inline_array(", "@relative(", " vtable ", " deinit @", " empty_from ", "\"", "\"\\u{41}\"", "-1", "0", "1.5", "9223372036854775807",
        "integer_literal", "float_literal", "string_literal", "builtin", "\"add_Int\"", "\"cmp_eq_Int\"", "alloc_stack", "struct", "struct_extract", "tuple", "tuple_extract",
        "enum", "switch_enum", "throw", "cond_fail", "try_apply", "dealloc_stack", "load", "partial_apply", "upcast", "class_method", "unchecked_ref_cast",
        "load_weak", "load_unowned", "store_weak", "load_borrow", "end_borrow", "copy_value", "destroy_value", "destroy_addr", "alloc_ref", "ref_element_addr",
        "store", " to ", "[init]", "[assign]", "[take]", "[copy]", "[trivial]", "global_addr", "function_ref", "apply", "br", "cond_br", "return", "unreachable",
        "case #Some: ", "default ", "normal ", "error ", "// c\n",
    ];
}
