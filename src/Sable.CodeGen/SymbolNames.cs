using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Sable.CodeGen;

/// <summary>
///     A function's or a record's symbol, from its SIL name (#300): the name
///     behind <c>sable.</c>, shortened by substitutions as Swift's mangling
///     shortens its own (docs/05 §5.4, "Symbols").
/// </summary>
/// <remarks>
///     <para>
///         A SIL name is made of <em>chains</em>, qualified names such as
///         <c>Sable.Core.Collection</c>, between the punctuation that says
///         what the thing is (<c>$generic$witness$…</c>, <c>$of$</c>,
///         <c>witnesses.T.P</c>). Read left to right, each chain is written
///         as its parts, where a run of them is, the longest first,
///     </para>
///     <list type="bullet">
///         <item>
///             a back-reference, <c>~</c>, a number and <c>_</c>, to the
///             start of a chain the name has had before: each chain's every
///             prefix of four characters or more that is not one of the
///             below is numbered from 0 as the chain ends, as Swift numbers
///             its substitutions; a reference is used only where it is
///             shorter than what it stands for;
///         </item>
///         <item>
///             or a module of the standard library, <c>~</c> and a lower-case
///             letter (<c>Sable.Core</c> is <c>~a</c>, <c>Sable</c> alone
///             <c>~s</c>), or a word its names are full of, <c>~</c> and a
///             capital (<c>witnesses</c> is <c>~A</c>): <see cref="Modules" />
///             and <see cref="Words" />, as Swift's known substitutions are.
///         </item>
///     </list>
///     <para>
///         A <c>~</c> of the name itself is <c>~~</c>. A name with nothing to
///         substitute is its symbol as it is (<c>sable.Main.main</c>), as
///         most of a program's own are. The symbol depends on the name alone,
///         so that every unit gives a function or a record the same one,
///         which <c>linkonce_odr</c> needs. A backtrace names a frame by its
///         SIL name again: the runtime reads the symbol back with the same
///         tables (<c>runtime/symbols.c</c>, which a test holds to these), as
///         <see cref="Demangle" /> does.
///     </para>
/// </remarks>
public static partial class SymbolNames {
    /// <summary>The standard library's modules a symbol names in two characters, by the letter after <c>~</c> (<c>s</c> is <c>Sable</c>).</summary>
    public static readonly IReadOnlyList<(char Code, string Text)> Modules = [
        ('a', "Sable.Core"), ('b', "Sable.Collections"), ('c', "Sable.Concurrency"), ('d', "Sable.Text"), ('e', "Sable.IO"),
        ('f', "Sable.Time"), ('g', "Sable.Math"), ('h', "Sable.Net"), ('i', "Sable.Process"), ('j', "Sable.Serialize"),
        ('k', "Sable.Testing"), ('l', "Sable.Crypto"), ('m', "Sable.Globalization"), ('n', "Sable.Diagnostics"), ('o', "Sable.Hosting"),
        ('p', "Sable.Interop"), ('q', "Sable.Validation"), ('r', "Sable.Build"), ('s', "Sable"), ('t', "Sable.DependencyInjection"),
        ('u', "Sable.Generators"),
    ];

    /// <summary>The words symbols are full of, by the capital after <c>~</c>.</summary>
    public static readonly IReadOnlyList<(char Code, string Text)> Words = [
        ('A', "witnesses"), ('B', "generic"), ('C', "witness"), ('D', "opaque"), ('E', "template"), ('F', "conformances"),
        ('G', "associated"), ('H', "associated_conformances"), ('I', "cache"), ('J', "shape"), ('K', "Element"), ('L', "String"),
        ('M', "Int"), ('N', "debugDescription"), ('O', "Bool"), ('P', "thick"), ('Q', "throws"), ('R', "Error"),
        ('S', "destroy"), ('T', "visit"), ('U', "reabstract"), ('V', "convert"), ('W', "describe"), ('X', "closure"),
        ('Y', "Self"), ('Z', "accessors"),
    ];

    static readonly Dictionary<string, string> Fixed = Modules.Concat(Words).ToDictionary(static e => e.Text, static e => "~" + e.Code, StringComparer.Ordinal);
    static readonly Dictionary<char, string> FixedByCode = Modules.Concat(Words).ToDictionary(static e => e.Code, static e => e.Text);
    static readonly ConcurrentDictionary<string, string> Mangled = new(StringComparer.Ordinal);

    static bool IsStart(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_';

    static bool IsPart(char c) => IsStart(c) || c is >= '0' and <= '9';

    /// <summary>The prefixes a name has numbered so far, both ways.</summary>
    sealed class Substitutions {
        readonly Dictionary<string, int> _indices = new(StringComparer.Ordinal);
        readonly List<string> _keys = [];

        public bool TryGetIndex(string key, out int index) => _indices.TryGetValue(key, out index);

        public string? this[int index] => index < _keys.Count ? _keys[index] : null;

        /// <summary>Numbers a chain's prefixes, as both directions do once it ends.</summary>
        public void Remember(IReadOnlyList<string> parts) {
            var key = new StringBuilder();
            for (var end = 0; end < parts.Count; end++) {
                key.Append(end > 0 ? "." : "").Append(parts[end]);
                var text = key.ToString();
                if (text.Length >= 4 && !Fixed.ContainsKey(text) && _indices.TryAdd(text, _keys.Count)) {
                    _keys.Add(text);
                }
            }
        }
    }

    /// <summary>The symbol of the SIL name <paramref name="name" />, without <c>sable.</c>.</summary>
    public static string Mangle(string name) {
        if (Mangled.TryGetValue(name, out var known)) {
            return known;
        }

        var table = new Substitutions();
        var output = new StringBuilder(name.Length);
        var parts = new List<string>();
        var i = 0;
        while (i < name.Length) {
            var c = name[i];
            if (!IsStart(c)) {
                output.Append(c == '~' ? "~~" : c);
                i++;
                continue;
            }

            // A chain: its parts, each a letter or '_' and what follows, joined by dots.
            parts.Clear();
            while (true) {
                var start = i;
                while (i < name.Length && IsPart(name[i])) {
                    i++;
                }

                parts.Add(name[start..i]);
                if (i + 1 < name.Length && name[i] == '.' && IsStart(name[i + 1])) {
                    i++;
                    continue;
                }

                break;
            }

            for (var at = 0; at < parts.Count;) {
                if (at > 0) {
                    output.Append('.');
                }

                var taken = 1;
                var text = parts[at];
                for (var end = parts.Count; end > at; end--) {
                    var key = string.Join('.', parts.GetRange(at, end - at));
                    if (table.TryGetIndex(key, out var index) && Reference(index) is var reference && reference.Length < key.Length) {
                        (taken, text) = (end - at, reference);
                        break;
                    }

                    if (Fixed.TryGetValue(key, out var code)) {
                        (taken, text) = (end - at, code);
                        break;
                    }
                }

                output.Append(text);
                at += taken;
            }

            table.Remember(parts);
        }

        var symbol = output.ToString();
        Mangled.TryAdd(name, symbol);
        return symbol;
    }

    /// <summary>The SIL name a symbol (without <c>sable.</c>) is of; the text as it is where it is not one <see cref="Mangle" /> makes.</summary>
    public static string Demangle(string symbol) {
        if (!symbol.Contains('~', StringComparison.Ordinal)) {
            return symbol;
        }

        var table = new Substitutions();
        var output = new StringBuilder(symbol.Length * 2);
        var chain = new StringBuilder();
        var i = 0;
        while (i < symbol.Length) {
            if (symbol[i] == '~' && i + 1 < symbol.Length && symbol[i + 1] == '~') {
                output.Append('~');
                i += 2;
                continue;
            }

            if (Component(symbol, i, table) is not var (first, next)) {
                output.Append(symbol[i]);
                i++;
                continue;
            }

            chain.Clear().Append(first);
            i = next;
            while (i + 1 < symbol.Length && symbol[i] == '.' && Component(symbol, i + 1, table) is var (more, after)) {
                chain.Append('.').Append(more);
                i = after;
            }

            var text = chain.ToString();
            output.Append(text);
            table.Remember(text.Split('.'));
        }

        return output.ToString();
    }

    /// <summary>A chain's part at <paramref name="at" />, read back: what it stands for and where it ends; null where none starts there.</summary>
    static (string Text, int End)? Component(string symbol, int at, Substitutions table) {
        if (at >= symbol.Length) {
            return null;
        }

        if (IsStart(symbol[at])) {
            var end = at;
            while (end < symbol.Length && IsPart(symbol[end])) {
                end++;
            }

            return (symbol[at..end], end);
        }

        if (symbol[at] != '~' || at + 1 >= symbol.Length) {
            return null;
        }

        if (FixedByCode.TryGetValue(symbol[at + 1], out var text)) {
            return (text, at + 2);
        }

        var digits = at + 1;
        while (digits < symbol.Length && symbol[digits] is >= '0' and <= '9') {
            digits++;
        }

        return digits > at + 1 && digits < symbol.Length && symbol[digits] == '_'
               && int.TryParse(symbol.AsSpan(at + 1, digits - at - 1), CultureInfo.InvariantCulture, out var index)
               && table[index] is { } referenced
            ? (referenced, digits + 1)
            : null;
    }

    static string Reference(int index) => "~" + index.ToString(CultureInfo.InvariantCulture) + "_";

    /// <summary>
    ///     The module's LLVM IR with every Sable symbol made its mangled form:
    ///     each <c>@"sable.…"</c> and COMDAT <c>$"sable.…"</c>, whatever wrote it.
    /// </summary>
    public static string MangleIr(string ir) =>
        QuotedSymbol().Replace(ir, static m => Mangle(m.Groups[2].Value) is var symbol && symbol != m.Groups[2].Value
            ? m.Groups[1].Value + "\"" + LlvmEmitter.SymbolPrefix + symbol + "\""
            : m.Value);

    /// <summary>The module's LLVM IR with every Sable symbol read back as its SIL name: what a reader of the IR wants to see.</summary>
    public static string DemangleIr(string ir) =>
        QuotedSymbol().Replace(ir, static m => m.Groups[1].Value + "\"" + LlvmEmitter.SymbolPrefix + Demangle(m.Groups[2].Value) + "\"");

    [System.Text.RegularExpressions.GeneratedRegex("([@$])\"sable\\.([^\"]*)\"")]
    private static partial System.Text.RegularExpressions.Regex QuotedSymbol();
}
