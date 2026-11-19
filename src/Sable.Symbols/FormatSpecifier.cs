using System.Collections.Immutable;
using System.Text;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     The format spec after an interpolation's colon, <c>\(x:&gt;8.2)</c>,
///     read: the compiler's twin of <c>FormatSpec.parse</c> in
///     <c>lib/Sable.Core/FormatSpec.sb</c>, whose form it reads (SYNTAX.md
///     §16). The binder checks it against the value's type, and it becomes
///     the <c>FormatSpec</c> value the hole is written with.
/// </summary>
public sealed record FormatSpecifier {
    /// <summary>What a spec that says nothing is: what <c>"\(x)"</c> writes with.</summary>
    public static FormatSpecifier Default { get; } = new();

    public Rune Fill { get; init; } = new(' ');

    /// <summary>The <c>FormatAlignment</c> case: <c>Automatic</c>, <c>Left</c>, <c>Center</c> or <c>Right</c>.</summary>
    public string Alignment { get; init; } = "Automatic";

    /// <summary>The <c>FormatSign</c> case: <c>Negative</c>, <c>Always</c> or <c>Space</c>.</summary>
    public string Sign { get; init; } = "Negative";

    public bool Alternate { get; init; }
    public bool ZeroPadded { get; init; }
    public long Width { get; init; }
    public Rune? Separator { get; init; }
    public long? Precision { get; init; }

    /// <summary>The <c>FormatKind</c> case, <c>Automatic</c> when no letter ends the spec.</summary>
    public string Kind { get; init; } = "Automatic";

    /// <summary>The letter that named the kind, as written; none for <c>Automatic</c>.</summary>
    public char? KindLetter { get; init; }

    /// <summary>True when the spec writes a sign, <c>#</c>, <c>0</c> or a separator: what only a number has.</summary>
    public bool HasNumberOptions => Sign != "Negative" || Alternate || ZeroPadded || Separator is not null;

    static readonly Dictionary<char, string> Kinds = new() {
        ['d'] = "Decimal", ['x'] = "Hex", ['X'] = "UpperHex", ['o'] = "Octal", ['b'] = "Binary",
        ['e'] = "Exponent", ['E'] = "UpperExponent", ['f'] = "Fixed", ['g'] = "General",
        ['G'] = "UpperGeneral", ['%'] = "Percent", ['?'] = "Debug",
    };

    /// <summary>The kinds that write an integer, and those that write a float.</summary>
    public static readonly ImmutableHashSet<string> IntegerKinds = ["Automatic", "Decimal", "Hex", "UpperHex", "Octal", "Binary", "Debug"];

    /// <inheritdoc cref="IntegerKinds" />
    public static readonly ImmutableHashSet<string> FloatKinds = ["Automatic", "Exponent", "UpperExponent", "Fixed", "General", "UpperGeneral", "Percent", "Debug"];

    /// <summary>
    ///     The spec the text writes:
    ///     <c>[[fill] align] [sign] [#] [0] [width] [, or _] [.precision] [kind]</c>.
    ///     Null when it is not one, with the scalar where it stops being one
    ///     and why.
    /// </summary>
    public static FormatSpecifier? Parse(string text, out int errorAt, out string error) {
        errorAt = 0;
        error = "";
        var scalars = text.EnumerateRunes().ToArray();
        var spec = new FormatSpecifier();
        var i = 0;

        static string? AlignmentOf(Rune r) =>
            r.Value switch { '<' => "Left", '^' => "Center", '>' => "Right", _ => null };

        if (scalars.Length >= 2 && AlignmentOf(scalars[1]) is { } aligned) {
            spec = spec with { Fill = scalars[0], Alignment = aligned };
            i = 2;
        } else if (scalars.Length >= 1 && AlignmentOf(scalars[0]) is { } alone) {
            spec = spec with { Alignment = alone };
            i = 1;
        }

        bool At(char c) => i < scalars.Length && scalars[i].Value == c;

        if (At('+') || At('-') || At(' ')) {
            spec = spec with { Sign = scalars[i].Value switch { '+' => "Always", ' ' => "Space", _ => "Negative" } };
            i++;
        }

        if (At('#')) {
            spec = spec with { Alternate = true };
            i++;
        }

        if (At('0')) {
            spec = spec with { ZeroPadded = true };
            i++;
        }

        if (Number(scalars, ref i, out var width, ref errorAt, ref error) is false) {
            return null;
        }

        spec = spec with { Width = width ?? 0 };
        if (At(',') || At('_')) {
            spec = spec with { Separator = scalars[i] };
            i++;
        }

        if (At('.')) {
            i++;
            if (Number(scalars, ref i, out var precision, ref errorAt, ref error) is false) {
                return null;
            }

            if (precision is null) {
                (errorAt, error) = (i, "a '.' is followed by the precision, a number");
                return null;
            }

            spec = spec with { Precision = precision };
        }

        if (i < scalars.Length && scalars[i].IsBmp && Kinds.TryGetValue((char)scalars[i].Value, out var kind)) {
            spec = spec with { Kind = kind, KindLetter = (char)scalars[i].Value };
            i++;
        }

        if (i < scalars.Length) {
            (errorAt, error) = (i, $"'{scalars[i]}' has no meaning there; a spec is [[fill] align] [sign] [#] [0] [width] [, or _] [.precision] [kind]");
            return null;
        }

        return spec;
    }

    /// <summary>A width or a precision: decimal digits, none, or too many for one.</summary>
    static bool Number(Rune[] scalars, ref int i, out long? value, ref int errorAt, ref string error) {
        var start = i;
        long total = 0;
        while (i < scalars.Length && scalars[i].Value is >= '0' and <= '9') {
            total = total * 10 + (scalars[i].Value - '0');
            // A field wider than a string can be is a mistake, and a number
            // past it would not fit the Int it becomes.
            if (total > int.MaxValue) {
                (errorAt, error) = (start, "a width or a precision is at most 2147483647");
                value = null;
                return false;
            }

            i++;
        }

        value = i == start ? null : total;
        return true;
    }

    /// <summary>
    ///     The <c>FormatSpec</c> value this spec is: a record literal of the
    ///     fields that are not what the type starts them as. Null when
    ///     Sable.Core has no <c>FormatSpec</c> to make.
    /// </summary>
    public BoundExpression? Bind(CoreTypes core, SyntaxNode syntax) {
        if (core.FormatSpec is not { } type) {
            return null;
        }

        var fields = ImmutableArray.CreateBuilder<(FieldSymbol, BoundExpression)>();

        void Set(string name, Func<TypeSymbol, BoundExpression> value) {
            if (type.GetMembers(name).OfType<FieldSymbol>().FirstOrDefault() is { } field) {
                fields.Add((field, value(field.Type)));
            }
        }

        BoundExpression Literal(TypeSymbol fieldType, object value) => new BoundLiteral(syntax, fieldType, value);

        BoundExpression Some(TypeSymbol fieldType, TypeSymbol underlying, object value) =>
            new BoundConversion(syntax, fieldType, new BoundLiteral(syntax, underlying, value), ConversionKind.OptionalWrap);

        if (Fill != Default.Fill) {
            Set("fill", t => Literal(t, Fill));
        }

        if (Alignment != Default.Alignment) {
            Set("alignment", t => Literal(t, Alignment));
        }

        if (Sign != Default.Sign) {
            Set("sign", t => Literal(t, Sign));
        }

        if (Alternate) {
            Set("alternate", t => Literal(t, true));
        }

        if (ZeroPadded) {
            Set("zeroPadded", t => Literal(t, true));
        }

        if (Width != 0) {
            Set("width", t => Literal(t, Width));
        }

        if (Separator is { } separator) {
            Set("separator", t => Some(t, core.Char, separator));
        }

        if (Precision is { } precision) {
            Set("precision", t => Some(t, core.Int, precision));
        }

        if (Kind != Default.Kind) {
            Set("kind", t => Literal(t, Kind));
        }

        return new BoundRecordExpression(syntax, type, fields.ToImmutable());
    }
}
