using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     Checks declaration-site variance: an <c>out</c> parameter may not appear
///     in an input position, nor an <c>in</c> parameter in an output one.
/// </summary>
/// <remarks>
///     <para>
///         docs/03 §3.4: generic types are invariant, and only a protocol may
///         declare variance, because only a protocol existential can be used
///         variantly. Use-site variance is not supported.
///     </para>
///     <para>
///         The check is the usual one: walk each member's signature tracking
///         the position's polarity, flipping it under a function parameter and under
///         an <c>in</c> argument, and report a parameter used against its
///         declaration.
///     </para>
/// </remarks>
internal static class VarianceChecker {
    private enum Position {
        Output,
        Input,
        Invariant
    }

    public static void Check(NamedTypeSymbol type, DiagnosticBag diagnostics) {
        if (type.TypeKind != TypeKind.Protocol) {
            return;
        }

        if (!type.TypeParameters.Any(static p => p.Variance != VarianceKind.None)) {
            return;
        }

        foreach (var member in type.Members) {
            switch (member) {
                case FunctionSymbol function:
                    foreach (var parameter in function.Parameters) {
                        Walk(parameter.Type, Position.Input, type, member, diagnostics);
                    }

                    Walk(function.ReturnType, Position.Output, type, member, diagnostics);
                    break;

                case FieldSymbol field: {
                    // A property that can be written is both read and written;
                    // a computed one is writable only where it has a setter.
                    var writable = field.IsComputed ? field.HasSetter : field.IsMutable;
                    Walk(
                        field.Type,
                        writable ? Position.Invariant : Position.Output,
                        type,
                        member,
                        diagnostics
                    );
                    break;
                }
            }
        }
    }

    static void Walk(
        TypeSymbol type,
        Position position,
        NamedTypeSymbol owner,
        Symbol member,
        DiagnosticBag diagnostics
    ) {
        switch (type) {
            case TypeParameterSymbol parameter
                when owner.TypeParameters.Contains(parameter): {
                var allowed = parameter.Variance switch {
                    VarianceKind.Out => position is Position.Output,
                    VarianceKind.In => position is Position.Input,
                    _ => true,
                };

                if (allowed) {
                    return;
                }

                diagnostics.Error(
                    ErrorCode.ERR_VarianceViolation,
                    member.Location ?? owner.Location ?? default,
                    $"'{parameter.Name}' is declared "
                    + $"'{(parameter.Variance == VarianceKind.Out ? "out" : "in")}', so it may "
                    + $"not appear in {Describe(position)} position of "
                    + $"'{owner.Name}.{member.Name}'"
                );
                return;
            }

            case OptionalTypeSymbol optional:
                Walk(optional.UnderlyingType, position, owner, member, diagnostics);
                return;

            case ArrayTypeSymbol array:
                // An array is mutable, so its element is invariant.
                Walk(array.ElementType, Position.Invariant, owner, member, diagnostics);
                return;

            case ReferenceTypeSymbol reference:
                Walk(reference.ReferencedType, Position.Invariant, owner, member, diagnostics);
                return;

            case TupleTypeSymbol tuple:
                foreach (var element in tuple.ElementTypes) {
                    Walk(element, position, owner, member, diagnostics);
                }

                return;

            case FunctionTypeSymbol function:
                foreach (var parameter in function.ParameterTypes) {
                    Walk(parameter, Flip(position), owner, member, diagnostics);
                }

                Walk(function.ReturnType, position, owner, member, diagnostics);
                return;

            case NamedTypeSymbol named when !named.TypeArguments.IsEmpty: {
                for (var i = 0; i < named.TypeArguments.Length; i++) {
                    var declared = i < named.OriginalDefinition.TypeParameters.Length
                        ? named.OriginalDefinition.TypeParameters[i].Variance
                        : VarianceKind.None;

                    var inner = declared switch {
                        VarianceKind.Out => position,
                        VarianceKind.In => Flip(position),
                        _ => Position.Invariant,
                    };

                    Walk(named.TypeArguments[i], inner, owner, member, diagnostics);
                }

                return;
            }

            default:
                return;
        }
    }

    static Position Flip(Position position) =>
        position switch {
            Position.Output => Position.Input,
            Position.Input => Position.Output,
            _ => Position.Invariant,
        };

    static string Describe(Position position) =>
        position switch {
            Position.Output => "an output",
            Position.Input => "an input",
            _ => "an invariant",
        };
}
