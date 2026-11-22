using Sable.Syntax;

namespace Sable.Symbols;

// A hole's format spec, '\(x:08x)': read, checked against the value's type,
// and made the FormatSpec value the lowering writes the hole with.
public sealed partial class Binder {
    /// <summary>
    ///     The hole's format spec, checked: a spec is read the same for every
    ///     type, but a kind, a precision or a number's options that the
    ///     value's type would ignore is a mistake the compiler can see. Null
    ///     for a spec that is not one, which is reported, and the hole then
    ///     written as if it had none.
    /// </summary>
    BoundFormatSpec? BindFormatSpec(InterpolationSyntax hole, BoundExpression part) {
        var token = hole.FormatToken;
        var text = token.Value as string ?? "";
        if (FormatSpecifier.Parse(text, out var errorAt, out var error) is not { } spec) {
            // The scalar it stops at, after the colon.
            var offset = 1 + text.EnumerateRunes().Take(errorAt).Sum(static r => r.Utf16SequenceLength);
            var length = errorAt < text.EnumerateRunes().Count() ? text.EnumerateRunes().ElementAt(errorAt).Utf16SequenceLength : 0;
            _diagnostics.Error(
                ErrorCode.ERR_FormatSpecMalformed,
                new TextSpan(token.Span.Start + offset, length),
                $"'{text}' is not a format spec: {error}"
            );
            return null;
        }

        if (!part.Type.IsError) {
            CheckFormatSpec(spec, part.Type, token.Span);
        }

        return spec.Bind(Core, hole) is { } value ? new BoundFormatSpec(hole, value, spec) : null;
    }

    /// <summary>Reports what of the spec the type would ignore.</summary>
    void CheckFormatSpec(FormatSpecifier spec, TypeSymbol type, TextSpan at) {
        var name = type.ToDisplayString();
        var letter = spec.KindLetter is { } written ? $"'{written}'" : "";

        void Report(ErrorCode code, string message) => _diagnostics.Error(code, at, message);

        if (Core.IsInteger(type) || Core.Floats.Any(f => f.Equals(type))) {
            var integer = Core.IsInteger(type);
            if (!(integer ? FormatSpecifier.IntegerKinds : FormatSpecifier.FloatKinds).Contains(spec.Kind)) {
                Report(
                    ErrorCode.ERR_FormatKindNotForType,
                    integer
                        ? $"{letter} writes a float, and '{name}' is an integer, written with d, x, X, o, b or ?; convert it, F64(value), to write it as a float"
                        : $"{letter} writes an integer, and '{name}' is a float, written with e, E, f, g, G, % or ?"
                );
                return;
            }

            if (spec.Kind == "Debug" && spec.HasNumberOptions) {
                Report(ErrorCode.ERR_FormatOptionNotForType, "'?' writes the value as Debug describes it: a sign, '#', '0' or a separator means nothing to it");
            } else if (integer && spec.Precision is not null) {
                Report(ErrorCode.ERR_FormatOptionNotForType, $"a precision means nothing to '{name}', an integer: it has no digits after a point");
            } else if (integer && spec.Alternate && !spec.IsPrettyDebug && spec.Kind is not ("Hex" or "UpperHex" or "Octal" or "Binary")) {
                Report(ErrorCode.ERR_FormatOptionNotForType, "'#' writes a radix's prefix, '0x', '0o' or '0b', so it goes with x, X, o or b");
            } else if (!integer && spec.Alternate && !spec.IsPrettyDebug && spec.Kind is not ("General" or "UpperGeneral")) {
                Report(ErrorCode.ERR_FormatOptionNotForType, "'#' keeps the trailing zeros 'g' and 'G' drop, so it goes with g or G");
            }

            return;
        }

        if (type.Equals(Core.String) || type.Equals(Core.Char) || type.Equals(Core.Bool)) {
            if (spec.Kind is not ("Automatic" or "Debug")) {
                Report(ErrorCode.ERR_FormatKindNotForType, $"{letter} writes a number, and '{name}' is written as it is, or with ? as a literal is");
            } else if (spec.HasNumberOptions) {
                Report(ErrorCode.ERR_FormatOptionNotForType, $"a sign, '#', '0' or a separator is for a number, not a '{name}'");
            } else if (spec.Precision is not null && !type.Equals(Core.String)) {
                Report(ErrorCode.ERR_FormatOptionNotForType, $"a precision means nothing to a '{name}': only a string's keeps that many of its scalars");
            }

            return;
        }

        // A type of the program's own decides what its spec means, if it
        // writes itself; one that does not is written as it always is, and
        // can only be padded.
        if (Core.Formattable is { } formattable && type.Satisfies(formattable)) {
            return;
        }

        if (spec.Kind is not ("Automatic" or "Debug") || spec.Precision is not null || spec.HasNumberOptions) {
            Report(
                ErrorCode.ERR_FormatSpecNeedsFormattable,
                $"'{name}' is not Formattable, so a fill, an alignment, a width and ? are all of the spec that apply to it"
            );
        }
    }
}
