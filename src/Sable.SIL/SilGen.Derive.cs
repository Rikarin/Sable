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
    void AddDerived(List<FunctionSource> functions) {
        foreach (var derivation in _compilation.Derivations) {
            if (IsLowerable(derivation.Function)) {
                functions.Add(new(derivation.Function, null, _compilation.GetSemanticModel(derivation.Tree)));
                if (derivation is { Member: DerivedMember.DescribeCallables, Type.IsGenericDefinition: false }
                    && _evaluation is null && _unit?.Owns(derivation.Tree) != false && !_compilation.IsFromInterface(derivation.Tree)) {
                    _callables.Add(derivation);
                }
            }
        }
    }

    /// <summary>The types of the unit that ask for their members to be callable (docs/06 §6.3).</summary>
    readonly List<Derivation> _callables = [];

    /// <summary>
    ///     For each, the function that gives its members' descriptors, which the runtime is handed as the image starts: what
    ///     finds a type's members by the type, and what makes a type one of
    ///     those a program lists by what they conform to.
    /// </summary>
    void LowerCallables() {
        var callables = _callables.ToList();
        _callables.Clear();
        foreach (var derivation in callables) {
            if (Core.Module.GetType("Callable")?.GetMembers("callableMembers").OfType<FunctionSymbol>().FirstOrDefault(static f => f.HasBody) is not { } members
                || Lower(derivation.Type) is not { } described || Lower(members.ReturnType) is not { } array) {
                continue;
            }

            // The embedded build (docs/06 §6.1) carries no reflection.
            if (_denyReflection) {
                Report(
                    ErrorCode.ERR_ReflectionDenied,
                    derivation.Attribute.Span,
                    $"'{derivation.Type.Name}' is '@reflectable(callable)', and this program is built with --deny-reflection"
                );
                continue;
            }

            var name = $"$callable${Sanitise(described.ToString())}";
            var symbol = FunctionSymbol.Synthesized(name, derivation.Type);
            var at = derivation.Attribute;
            var call = new BoundCall(at, new BoundTypeExpression(at, derivation.Type), members, [], members.ReturnType) {
                Substitution = new TypeSubstitution(new() { [Core.Module.GetType("Callable")!.SelfTypeParameter!] = derivation.Type }),
            };
            var function = new SilFunction(name, new FunctionType([], array));
            new FunctionLowering(this, symbol, function, derivation.Tree.Text, TypeSubstitution.Empty)
                .Lower(new BoundBlock(at, [], [new BoundReturn(at, call)]));
            function.RemoveUnreachableBlocks();
            _module.Add(function);
            _module.Callables[described.ToString()] = (described, name);
        }
    }

    /// <summary>
    ///     Each generator's <c>makeIterator()</c> and <c>next()</c>, which the
    ///     compilation made as it bound the generator's body: lowered where the
    ///     generator is declared, from its body.
    /// </summary>
    void AddGenerators(List<FunctionSource> functions) {
        foreach (var source in functions.ToList()) {
            // A library taken from its interface has none (LibrarySymbols).
            if (_compilation.IsFromInterface(source.Model.SyntaxTree)) {
                continue;
            }

            var (symbol, (syntax, model)) = (source.Symbol, source);
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
                functions.Add(new(makeIterator, syntax, model));
            }

            functions.Add(new(generator.Next, syntax, model));
        }
    }

    /// <summary>The initialisers a subclass inherits, which the compilation wrote: lowered where the class is declared.</summary>
    void AddInheritedInitializers(List<FunctionSource> functions) {
        foreach (var (init, inherited) in _compilation.InheritedInitializers) {
            if (IsLowerable(init)) {
                functions.Add(new(init, _compilation.IsFromInterface(inherited.Tree) ? null : inherited.Declaration, _compilation.GetSemanticModel(inherited.Tree)));
            }
        }
    }

    /// <summary>A lazy or observed property's getter and setter, which the compilation wrote: lowered where the property is declared.</summary>
    void AddLazy(List<FunctionSource> functions) {
        foreach (var (field, lazy) in _compilation.LazyProperties.Concat(_compilation.ObservedProperties)) {
            if (!IsLowerable(lazy.Getter) || field.DeclaringSyntax is not [var declaration, ..]) {
                continue;
            }

            var model = _compilation.GetSemanticModel(lazy.Tree);
            _getters[field] = lazy.Getter;
            _setters[field] = lazy.Setter;
            functions.Add(new(lazy.Getter, declaration, model));
            functions.Add(new(lazy.Setter, declaration, model));
        }
    }
}
