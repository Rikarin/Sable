using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     The members <c>@derive</c> made (docs/06 §6.5): functions like any
///     other, whose bodies the compilation builds from the type's fields
///     rather than binds from source. They are lowered where the attribute
///     is, and, on a generic type, once per construction that uses them.
/// </summary>
public sealed partial class SilGen {
    void AddDerived(List<(FunctionSymbol Symbol, SyntaxNode Syntax, SemanticModel Model)> functions) {
        foreach (var derivation in _compilation.Derivations) {
            if (IsLowerable(derivation.Function)) {
                functions.Add((derivation.Function, derivation.Attribute, _compilation.GetSemanticModel(derivation.Tree)));
            }
        }
    }

    /// <summary>A lazy or observed property's getter and setter, which the compilation wrote: lowered where the property is declared.</summary>
    void AddLazy(List<(FunctionSymbol Symbol, SyntaxNode Syntax, SemanticModel Model)> functions) {
        foreach (var (field, lazy) in _compilation.LazyProperties.Concat(_compilation.ObservedProperties)) {
            if (!IsLowerable(lazy.Getter) || field.DeclaringSyntax is not [var declaration, ..]) {
                continue;
            }

            var model = _compilation.GetSemanticModel(lazy.Tree);
            _getters[field] = lazy.Getter;
            _setters[field] = lazy.Setter;
            functions.Add((lazy.Getter, declaration, model));
            functions.Add((lazy.Setter, declaration, model));
        }
    }
}
