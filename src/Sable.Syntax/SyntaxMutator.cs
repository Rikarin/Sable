using System.Text;

namespace Sable.Syntax;

/// <summary>
///     A grammar-aware mutator for fuzzing what reads Sable source: it parses
///     the input and edits whole nodes of its tree — one replaced by another
///     that can stand where it does, deleted, copied, traded with another, put
///     inside another, or given up for one inside it — so that most mutants
///     still parse, and get to the binder and past it, where an edit of bytes
///     or tokens rarely leaves anything but a syntax error.
/// </summary>
/// <remarks>
///     <para>
///         It is a function of bytes and a seed, and no more: the shape of
///         libFuzzer's custom mutator and crossover. So it is the same thing
///         wherever it is called from — the mutation fuzzer of the SIL tests
///         and the coverage-guided loop of tools/fuzz/Sable.Fuzz now; and, the
///         compiler written in Sable, a <c>@fuzz(mutator:)</c> function of it.
///         All it asks of the tree is each node's kind, its children and
///         where it is: what any syntax tree of the language has. What needs
///         that compiler is only to call it from a native fuzzer.
///     </para>
///     <para>
///         Its fragments come from programs given once, the fuzzer's seeds: a
///         mutator called by libFuzzer is shown one input and not the corpus.
///     </para>
/// </remarks>
public sealed class SyntaxMutator {
    /// <summary>Where a node can stand: two of one category can be traded.</summary>
    enum Category { None, Expression, Statement, Type, Pattern, Declaration, Other }

    /// <summary>A node: what it is, what it is in, and where it is, its trivia left out.</summary>
    readonly record struct Piece(Category Category, SyntaxKind Kind, SyntaxKind Parent, int Start, int Length, int Depth) {
        public int End => Start + Length;
        public (Category, SyntaxKind) Place => (Category, Category == Category.Other ? Kind : SyntaxKind.None);
    }

    readonly Dictionary<(Category, SyntaxKind), List<string>> _fragments = [];

    /// <summary>The longest fragment kept: longer ones are programs.</summary>
    const int LongestFragment = 600;

    /// <param name="programs">What fragments are taken from; none to use the input's own alone.</param>
    public SyntaxMutator(IEnumerable<string> programs) {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var program in programs) {
            foreach (var piece in Pieces(program)) {
                var text = program.Substring(piece.Start, piece.Length);
                if (piece.Length <= LongestFragment && seen.Add($"{piece.Place}{text}")) {
                    if (!_fragments.TryGetValue(piece.Place, out var list)) {
                        _fragments[piece.Place] = list = [];
                    }

                    list.Add(text);
                }
            }
        }
    }

    /// <summary>Another input made of this one, by one edit or a few: always the same of the same seed.</summary>
    /// <returns>At most <paramref name="longest" /> bytes; the input itself when nothing could be done to it.</returns>
    public byte[] Mutate(ReadOnlySpan<byte> input, ulong seed, int longest) => Bytes(Mutate(Encoding.UTF8.GetString(input), seed), input, longest);

    /// <summary>Another input made of two: a node of the first replaced by, or followed by, one of the second that can stand there.</summary>
    public byte[] CrossOver(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, ulong seed, int longest) {
        var random = new Random(unchecked((int)(seed ^ (seed >> 32))));
        var (text, other) = (Encoding.UTF8.GetString(first), Encoding.UTF8.GetString(second));
        var pieces = Pieces(text);
        var donors = Pieces(other).GroupBy(static p => p.Place).ToDictionary(static g => g.Key, static g => g.ToList());
        var places = pieces.Where(p => donors.ContainsKey(p.Place)).ToList();
        if (places.Count == 0) {
            return first.ToArray();
        }

        var place = places[random.Next(places.Count)];
        var donor = donors[place.Place][random.Next(donors[place.Place].Count)];
        var given = other.Substring(donor.Start, donor.Length);
        var made = Repeats(place) && random.Next(2) == 0 ? Insert(text, place, given) : Replace(text, place, given);
        return Bytes(made, first, longest);
    }

    /// <summary>The same of text.</summary>
    public string Mutate(string source, ulong seed) {
        var random = new Random(unchecked((int)(seed ^ (seed >> 32))));
        var text = source;
        for (var edits = random.Next(4) == 0 ? random.Next(2, 5) : 1; edits > 0; edits--) {
            text = Edit(text, random);
        }

        return text;
    }

    static byte[] Bytes(string made, ReadOnlySpan<byte> input, int longest) {
        var bytes = Encoding.UTF8.GetBytes(made);
        return bytes.Length <= longest ? bytes : input.ToArray();
    }

    string Edit(string text, Random random) {
        var pieces = Pieces(text);
        if (pieces.Count == 0) {
            // Nothing here: a declaration to begin with.
            return Fragment((Category.Declaration, SyntaxKind.None), random) is { } first ? text + "\n" + first + "\n" : text;
        }

        // A few tries at an edit that can be made here; then nothing is done.
        for (var tries = 0; tries < 8; tries++) {
            var piece = pieces[random.Next(pieces.Count)];
            var made = random.Next(12) switch {
                0 or 1 or 2 => Fragment(piece.Place, random) is { } fragment ? Replace(text, piece, fragment) : null,
                3 or 4 => Another(pieces, piece, random) is { } own ? Replace(text, piece, text.Substring(own.Start, own.Length)) : null,
                5 => Delete(text, piece),
                6 => Repeats(piece) ? Insert(text, piece, text.Substring(piece.Start, piece.Length)) : null,
                7 => Repeats(piece) && Fragment(piece.Place, random) is { } spliced ? Insert(text, piece, spliced) : null,
                8 => Another(pieces, piece, random) is { } other ? Swap(text, piece, other) : null,
                9 => Inner(pieces, piece, random) is { } inner ? Replace(text, piece, text.Substring(inner.Start, inner.Length)) : null,
                10 => Fragment(piece.Place, random) is { } around ? Wrap(text, piece, around, random) : null,
                _ => TradeToken(text, piece, random),
            };
            if (made is not null && made != text) {
                return made;
            }
        }

        return text;
    }

    string? Fragment((Category, SyntaxKind) place, Random random) =>
        _fragments.TryGetValue(place, out var list) ? list[random.Next(list.Count)] : null;

    /// <summary>Another piece that can stand where this one does, neither in the other.</summary>
    static Piece? Another(List<Piece> pieces, Piece piece, Random random) {
        var others = pieces.Where(p => p.Place == piece.Place && (p.Start >= piece.End || p.End <= piece.Start)).ToList();
        return others.Count == 0 ? null : others[random.Next(others.Count)];
    }

    /// <summary>One inside this that can stand where it does.</summary>
    static Piece? Inner(List<Piece> pieces, Piece piece, Random random) {
        var inside = pieces.Where(p => p.Place == piece.Place && p.Start >= piece.Start && p.End <= piece.End && p.Length < piece.Length).ToList();
        return inside.Count == 0 ? null : inside[random.Next(inside.Count)];
    }

    static string Replace(string text, Piece piece, string with) => text[..piece.Start] + with + text[piece.End..];

    /// <summary>The fragment with one of its own nodes made this one, in its place: <c>f(x)</c> around <c>a + b</c>, <c>f(a + b)</c>.</summary>
    static string? Wrap(string text, Piece piece, string around, Random random) {
        var inside = Pieces(around, piece.Category).Where(p => p.Place == piece.Place && p.Length < around.Length).ToList();
        if (inside.Count == 0) {
            return null;
        }

        return Replace(text, piece, Replace(around, inside[random.Next(inside.Count)], text.Substring(piece.Start, piece.Length)));
    }

    static string Swap(string text, Piece one, Piece other) {
        var (first, second) = one.Start < other.Start ? (one, other) : (other, one);
        return text[..first.Start] + text.Substring(second.Start, second.Length) + text[first.End..second.Start]
            + text.Substring(first.Start, first.Length) + text[second.End..];
    }

    /// <summary>Whether another of its kind can come after it: a statement, a declaration, or an element of a list.</summary>
    static bool Repeats(Piece piece) =>
        piece.Category is Category.Statement or Category.Declaration
        || piece.Kind is SyntaxKind.Argument or SyntaxKind.Parameter or SyntaxKind.GenericParameter or SyntaxKind.MatchArm or SyntaxKind.Attribute;

    static string Insert(string text, Piece after, string inserted) {
        var between = after.Kind is SyntaxKind.Argument or SyntaxKind.Parameter or SyntaxKind.GenericParameter ? ", "
            : after.Kind == SyntaxKind.Attribute ? " "
            : "\n";
        return text[..after.End] + between + inserted + text[after.End..];
    }

    /// <summary>The text without the node, and without the comma after it or, after the last, before it.</summary>
    static string Delete(string text, Piece piece) {
        var (start, end) = (piece.Start, piece.End);
        var after = end;
        while (after < text.Length && text[after] is ' ' or '\t') {
            after++;
        }

        if (after < text.Length && text[after] == ',') {
            return text[..start] + text[(after + 1)..].TrimStart(' ');
        }

        var before = start;
        while (before > 0 && text[before - 1] is ' ' or '\t') {
            before--;
        }

        return (before > 0 && text[before - 1] == ',' ? text[..(before - 1)] : text[..start]) + text[end..];
    }

    /// <summary>One of the node's tokens traded for another of its kind: a name for a name, a literal for a literal, an operator for any.</summary>
    string? TradeToken(string text, Piece piece, Random random) {
        var tokens = SyntaxTree.Parse(text).Root.DescendantTokens().Where(t => t.Span.Length > 0).ToList();
        var inside = tokens.Where(t => t.Span.Start >= piece.Start && t.Span.End <= piece.End).ToList();
        if (inside.Count == 0) {
            return null;
        }

        var token = inside[random.Next(inside.Count)];
        var kin = tokens.Where(t => t.Kind == token.Kind && t.Text != token.Text).Select(static t => t.Text).ToList();
        var with = kin.Count > 0 ? kin[random.Next(kin.Count)]
            : SyntaxFacts.GetText(token.Kind) is not null ? TokenDictionary.Fixed[random.Next(TokenDictionary.Fixed.Count)]
            : null;
        return with is null ? null : text[..token.Span.Start] + with + text[token.Span.End..];
    }

    /// <summary>The nodes of a source that were written: those with text, but the file itself.</summary>
    static List<Piece> Pieces(string source, Category parsedAs = Category.Declaration) {
        var root = parsedAs switch {
            Category.Expression => SyntaxTree.ParseExpression(source).Root,
            Category.Statement => SyntaxTree.ParseStatement(source).Root,
            Category.Type => SyntaxTree.ParseType(source).Root,
            _ => SyntaxTree.Parse(source).Root,
        };
        var pieces = new List<Piece>();
        Add(root, 0);
        return pieces;

        void Add(SyntaxNode node, int depth) {
            // As deep as any program is: past that, what matters has been seen.
            if (depth > 200) {
                return;
            }

            foreach (var child in node.ChildNodes()) {
                var span = child.Span;
                if (span.Length > 0 && span.End <= source.Length) {
                    pieces.Add(new(CategoryOf(child), child.Kind, node.Kind, span.Start, span.Length, depth));
                    Add(child, depth + 1);
                }
            }
        }
    }

    static Category CategoryOf(SyntaxNode node) => node switch {
        ExpressionSyntax => Category.Expression,
        StatementSyntax => Category.Statement,
        TypeSyntax => Category.Type,
        PatternSyntax => Category.Pattern,
        DeclarationSyntax => Category.Declaration,
        _ => Category.Other,
    };
}
