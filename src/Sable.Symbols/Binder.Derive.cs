using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     Uses of what <c>@derive</c> made on a generic type: the conformance is
///     conditional, so a construction whose fields do not conform has no
///     <c>==</c> or <c>hash()</c> to call, and is told which field is why.
/// </summary>
public sealed partial class Binder {
    /// <summary>A derived member, called on a construction its conformance does not hold for.</summary>
    void ReportUnmetDerivation(FunctionSymbol function, TypeSymbol? receiver, SyntaxNode syntax) {
        if (_compilation.GetDerivation(function) is { } derivation && receiver is NamedTypeSymbol { IsConstructed: true } named) {
            ReportUnmet(named, derivation.Protocol, syntax);
        }
    }

    /// <summary>
    ///     An operator a generic type derives, on a construction that does not
    ///     conform: reported, and true, so that the caller looks no further.
    /// </summary>
    bool ReportUnmetDerivedOperator(string basis, TypeSymbol operand, SyntaxNode syntax) {
        if (operand is not NamedTypeSymbol { IsConstructed: true } named) {
            return false;
        }

        foreach (var member in named.OriginalDefinition.GetMembers(basis)) {
            if (member is FunctionSymbol function && _compilation.GetDerivation(function) is { } derivation) {
                return ReportUnmet(named, derivation.Protocol, syntax);
            }
        }

        return false;
    }

    bool ReportUnmet(NamedTypeSymbol named, NamedTypeSymbol protocol, SyntaxNode syntax) {
        if (Conversions.Satisfies(named, protocol)) {
            return false;
        }

        var substitution = TypeSubstitution.For(named);
        var (field, unionCase, type) = DerivedConformance.Fields(named.OriginalDefinition)
            .Select(f => (f.Name, f.Case, Type: substitution.Apply(f.Type)))
            .FirstOrDefault(f => !Conversions.Satisfies(f.Type, protocol));
        var why = type is null
            ? ""
            : $", and {(unionCase is null ? $"its field '{field}'" : $"the payload '{field}' of '{unionCase}'")} is '{type.ToDisplayString()}', which is not";
        _diagnostics.Error(
            ErrorCode.ERR_DoesNotConform,
            syntax.Span,
            $"'{named.ToDisplayString()}' is not '{protocol.Name}': '@derive({protocol.Name})' makes "
            + $"'{named.Name}' {protocol.Name} only where its fields are{why}"
        );
        return true;
    }
}
