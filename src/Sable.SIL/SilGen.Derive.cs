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
}
