using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     The body <c>@derive(Validatable)</c> makes (docs/06 §6.5): for each
///     field with attributes that are rules, what
///     <c>r.field(\.email).notEmpty().email()</c> would bind to.
/// </summary>
/// <remarks>
///     An attribute's arguments are bound where they are written, against the
///     method of Sable.Validation's <c>FieldRules</c> the attribute names — an
///     extension's among them — so what is wrong with them is reported there.
/// </remarks>
public sealed partial class Binder {
    internal BoundBlock BindValidationRules(Derivation derivation, NamedTypeSymbol self, IReadOnlyList<(FieldSymbol Field, IReadOnlyList<AttributeSyntax> Rules)> fields) {
        PushScope();
        var rules = derivation.Function.Parameters[0];
        var statements = new List<BoundStatement>();
        foreach (var (field, attributes) in fields) {
            var type = TypeSubstitution.For(self).Apply(field.Type);
            BoundExpression chain = new BoundParameterAccess(attributes[0], rules);
            chain = BindRule(attributes[0], chain, "field", [FieldKeyPath(attributes[0], self, field, type)], [null], [], takesOptional: type is OptionalTypeSymbol);
            foreach (var attribute in attributes) {
                if (chain.HasError) {
                    break;
                }

                var syntaxes = ImmutableArray.CreateBuilder<ExpressionSyntax>();
                var labels = ImmutableArray.CreateBuilder<string?>();
                foreach (var argument in attribute.ArgumentList?.Arguments ?? default) {
                    syntaxes.Add(argument.Expression);
                    labels.Add(argument.NameToken.IsNone ? null : argument.NameToken.Text);
                }

                var arguments = syntaxes.Select(e => IsClosure(e) ? new BoundUnboundClosure(e) : BindExpression(e)).ToImmutableArray();
                chain = BindRule(attribute, chain, Compilation.AttributeName(attribute), arguments, labels.ToImmutable(), syntaxes.ToImmutable(), null);
            }

            statements.Add(new BoundExpressionStatement(attributes[0], chain));
        }

        PopScope();
        return new BoundBlock(derivation.Attribute, [], [..statements]);
    }

    /// <summary>A call of a rules method by name: of its <c>field</c> overloads, the one for an optional field or for any other.</summary>
    BoundExpression BindRule(
        AttributeSyntax at,
        BoundExpression receiver,
        string name,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<string?> labels,
        ImmutableArray<ExpressionSyntax> syntaxes,
        bool? takesOptional
    ) {
        _unmetRequirement = null;
        var candidates = ImmutableArray<FunctionSymbol>.Empty;
        var substitution = TypeSubstitution.Empty;
        if (LookupMemberOfKind(receiver.Type, name, instance: true) is { } found) {
            substitution = found.Substitution;
            candidates = [
                ..found.Members.OfType<FunctionSymbol>()
                    .Where(f => !f.IsStatic && IsAccessible(f))
                    .Where(f => takesOptional is not { } optional || KeyPathArguments(f.Parameters[0].Type)?.Value is OptionalTypeSymbol == optional),
            ];
        }

        if (candidates.IsEmpty) {
            _diagnostics.Error(
                ErrorCode.ERR_UnknownAttribute,
                at.Name.Span,
                _unmetRequirement is { } unmet
                    ? $"'@{name}' is a rule only where {unmet}"
                    : $"there is no attribute '@{name}', nor a rule of that name in '{receiver.Type.ToDisplayString()}'"
            );
            return new BoundError(at);
        }

        return BindCallToGroup(at, new BoundFunctionGroup(at, receiver, candidates) { Substitution = substitution }, arguments, labels, syntaxes);
    }

    /// <summary><c>\.email</c>, of a field the compiler names.</summary>
    BoundExpression FieldKeyPath(SyntaxNode at, NamedTypeSymbol self, FieldSymbol field, TypeSymbol type) {
        var symbol = new FunctionSymbol(string.Empty, _containingSymbol, at, Accessibility.Private, FunctionKind.Ordinary);
        var root = new ParameterSymbol(KeyPathRootName, null, symbol, null, 0) { TypeResolver = () => self };
        symbol.Parameters = [root];
        symbol.ReturnTypeResolver = () => type;
        var body = new BoundBlock(at, [], [new BoundReturn(at, new BoundFieldAccess(at, new BoundParameterAccess(at, root), field, type))]);
        return MakeKeyPath(at, self, type, field.Name, new BoundClosure(at, new FunctionTypeSymbol([self], type, false, null), symbol, [], body));
    }
}
