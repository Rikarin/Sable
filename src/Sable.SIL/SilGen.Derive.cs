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

    /// <summary>
    ///     Each generator's <c>makeIterator()</c> and <c>next()</c>, which the
    ///     compilation made as it bound the generator's body: lowered where the
    ///     generator is declared, from its body.
    /// </summary>
    void AddGenerators(List<(FunctionSymbol Symbol, SyntaxNode Syntax, SemanticModel Model)> functions) {
        foreach (var (symbol, syntax, model) in functions.ToList()) {
            // The text first: walking every function's syntax for a 'yield'
            // was a noticeable part of lowering a small unit.
            var text = model.SyntaxTree.Text.Text;
            if (text.IndexOf("yield", syntax.Span.Start, syntax.Span.Length, StringComparison.Ordinal) < 0
                || !syntax.DescendantNodes().OfType<YieldStatementSyntax>().Any()) {
                continue;
            }

            // Binding the body is what makes the generator.
            _ = BodyModel(model, syntax).GetBoundNode(syntax);
            if (_compilation.GetGenerator(symbol) is not { } generator || !ReferenceEquals(generator.Function, symbol)) {
                continue;
            }

            if (generator.MakeIterator is { } makeIterator) {
                functions.Add((makeIterator, syntax, model));
            }

            functions.Add((generator.Next, syntax, model));
        }
    }

    /// <summary>The initialisers a subclass inherits, which the compilation wrote: lowered where the class is declared.</summary>
    void AddInheritedInitializers(List<(FunctionSymbol Symbol, SyntaxNode Syntax, SemanticModel Model)> functions) {
        foreach (var (init, inherited) in _compilation.InheritedInitializers) {
            if (IsLowerable(init)) {
                functions.Add((init, inherited.Declaration, _compilation.GetSemanticModel(inherited.Tree)));
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
