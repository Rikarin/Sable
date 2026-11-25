using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     A derived <c>Injectable</c> conformance's two members
///     (Sable.DependencyInjection, #81), as bound trees: <c>create(from:)</c>,
///     which calls the type's initialiser with what the scope gives for each
///     parameter, and <c>dependencies()</c>, which says what that is.
/// </summary>
internal sealed partial class DerivedBodies {
    NamedTypeSymbol Scope => compilation.InjectionType("ServiceScope")!;

    BoundExpression ScopeParameter() => new BoundParameterAccess(_at, _function.Parameters[0], Scope);

    /// <summary>A call of the scope's method of this name, for the service.</summary>
    BoundExpression FromScope(string name, TypeSymbol service, TypeSymbol? result, string? key, params BoundExpression[] more) {
        var function = Scope.GetMembers(name).OfType<FunctionSymbol>().First(f => f.Parameters.Length == 2 + more.Length && f.TypeParameters.Length == 1);
        var substitution = new TypeSubstitution(new() { [function.TypeParameters[0]] = service });
        var call = new BoundCall(_at, ScopeParameter(), function, [new BoundMetatype(_at, service), Key(key), ..more], substitution.Apply(function.ReturnType)) { Substitution = substitution };
        BoundExpression value = function.ThrownType is null ? call : Try(call);
        return result is null || value.Type.Equals(result) ? value : new BoundConversion(_at, result, value, ConversionKind.Identity);
    }

    BoundExpression Key(string? key) {
        var optional = new OptionalTypeSymbol(core.String);
        return key is null ? new BoundLiteral(_at, optional, null) : new BoundConversion(_at, optional, Literal(key), ConversionKind.OptionalWrap);
    }

    List<BoundStatement>? Create(NamedTypeSymbol type, NamedTypeSymbol self) {
        if (InjectedInitializer.Of(type) is not { } init) {
            return null;
        }

        var substitution = TypeSubstitution.For(self);
        var arguments = new List<BoundExpression>();
        foreach (var parameter in init.Parameters) {
            var wanted = substitution.Apply(parameter.Type);
            var (kind, service) = InjectedInitializer.Classify(compilation, wanted);
            var key = InjectedInitializer.KeyOf(type, parameter);
            if (kind == InjectedKind.Service && parameter.HasDefaultValue) {
                if (compilation.BindParameterDefault(_at, parameter, substitution) is not { } fallback) {
                    return null;
                }

                arguments.Add(FromScope("resolve", service, null, key, Deferred(fallback)));
                continue;
            }

            arguments.Add(kind switch {
                InjectedKind.Scope => ScopeParameter(),
                InjectedKind.Optional => FromScope("find", service, null, key),
                InjectedKind.All => FromScope("resolveAll", service, wanted, key),
                InjectedKind.Factory => FromScope("factory", service, null, key),
                InjectedKind.Lazy => FromScope("lazily", service, null, key),
                _ => FromScope("resolve", service, null, key),
            });
        }

        BoundExpression made = new BoundCall(_at, null, init, [..arguments], self) { Substitution = substitution };
        return [new BoundReturn(_at, init.ThrownType is null ? made : Try(made))];
    }

    /// <summary>An expression as a closure that gives it: a default, worked out only where it is wanted.</summary>
    BoundExpression Deferred(BoundExpression value) {
        var symbol = new FunctionSymbol(string.Empty, _function, _at, Accessibility.Private, FunctionKind.Ordinary) { Parameters = [] };
        var type = value.Type;
        symbol.ThrownTypeResolver = static () => null;
        symbol.ReturnTypeResolver = () => type;
        return new BoundClosure(_at, new FunctionTypeSymbol([], type, false, null), symbol, [], new BoundBlock(_at, [], [new BoundReturn(_at, value)]));
    }

    List<BoundStatement>? Dependencies(NamedTypeSymbol type, NamedTypeSymbol self) {
        if (InjectedInitializer.Of(type) is not { } init || compilation.InjectionType("ServiceDependency") is not { } dependency
            || compilation.InjectionType("DependencyKind") is not { } kinds
            || dependency.GetMembers("init").OfType<FunctionSymbol>().FirstOrDefault(static f => f.Parameters.Length == 3) is not { } make) {
            return null;
        }

        var substitution = TypeSubstitution.For(self);
        var elements = new List<BoundExpression>();
        foreach (var parameter in init.Parameters) {
            var (kind, service) = InjectedInitializer.Classify(compilation, substitution.Apply(parameter.Type));
            if (kind == InjectedKind.Scope) {
                continue;
            }

            var name = kind switch {
                InjectedKind.Optional => "Optional",
                InjectedKind.All => "All",
                InjectedKind.Factory or InjectedKind.Lazy => "Deferred",
                _ => parameter.HasDefaultValue ? "Optional" : "Required",
            };
            var described = new BoundConversion(_at, core.Type, new BoundMetatype(_at, service), ConversionKind.Identity);
            elements.Add(new BoundCall(_at, null, make, [described, Key(InjectedInitializer.KeyOf(type, parameter)), new BoundLiteral(_at, kinds, name)], dependency));
        }

        return [new BoundReturn(_at, new BoundArray(_at, new ArrayTypeSymbol(dependency, ArrayKind.Grow), [..elements]))];
    }
}

/// <summary>What an injected parameter is given.</summary>
internal enum InjectedKind {
    /// <summary>The service.</summary>
    Service,

    /// <summary>The service, or none.</summary>
    Optional,

    /// <summary>Every registration of it.</summary>
    All,

    /// <summary>A closure that gives it.</summary>
    Factory,

    /// <summary>A <c>Lazy</c> of it.</summary>
    Lazy,

    /// <summary>The scope itself.</summary>
    Scope,
}

/// <summary>The initialiser a derived <c>Injectable</c> conformance calls, and what its parameters are given.</summary>
internal static class InjectedInitializer {
    /// <summary>The type's one initialiser, or of several the one marked <c>@Inject</c>; null when that is none.</summary>
    public static FunctionSymbol? Of(NamedTypeSymbol type) {
        var all = type.GetMembers("init").OfType<FunctionSymbol>().ToList();
        return all.Count == 1 ? all[0] : all.Where(static f => Attribute(f, "Inject") is not null).ToList() is [var marked] ? marked : null;
    }

    static AttributeSyntax? Attribute(Symbol symbol, string name) =>
        symbol.DeclaringSyntax.SelectMany(Compilation.AttributesOf).FirstOrDefault(a => a.Name.ToString().Trim().Split('.')[^1] == name);

    static AttributeSyntax? KeyedAttribute(NamedTypeSymbol type, ParameterSymbol parameter) =>
        Attribute(parameter, "Keyed")
        ?? (parameter.DeclaringSyntax.IsEmpty && type.GetMembers(parameter.Name).OfType<FieldSymbol>().FirstOrDefault() is { } field ? Attribute(field, "Keyed") : null);

    /// <summary>The key a parameter's — or, of a memberwise initialiser's, its field's — <c>@Keyed("k")</c> says.</summary>
    public static string? KeyOf(NamedTypeSymbol type, ParameterSymbol parameter) =>
        KeyedAttribute(type, parameter) is { ArgumentList.Arguments: [var argument] } ? (argument.Expression as LiteralExpressionSyntax)?.Token.Value as string : null;

    /// <summary>What a parameter of the type is given, and of which service.</summary>
    public static (InjectedKind Kind, TypeSymbol Service) Classify(Compilation compilation, TypeSymbol type) =>
        type switch {
            OptionalTypeSymbol optional => (InjectedKind.Optional, optional.UnderlyingType),
            ArrayTypeSymbol { ArrayKind: ArrayKind.Fixed or ArrayKind.Grow or ArrayKind.GrowShrink, Capacity: null } array => (InjectedKind.All, array.ElementType),
            FunctionTypeSymbol { ParameterTypes.IsEmpty: true, IsAsync: false, ThrownType: null } function => (InjectedKind.Factory, function.ReturnType),
            NamedTypeSymbol { TypeArguments: [var held] } named when ReferenceEquals(named.OriginalDefinition, compilation.GetModule("Sable.Concurrency")?.GetType("Lazy")) => (InjectedKind.Lazy, held),
            NamedTypeSymbol named when ReferenceEquals(named, compilation.InjectionType("ServiceScope")) => (InjectedKind.Scope, named),
            _ => (InjectedKind.Service, type),
        };

    /// <summary>Why the type cannot derive Injectable, and where; null when it can.</summary>
    public static (TextSpan? At, string Message)? WhyNot(Compilation compilation, NamedTypeSymbol type) {
        if (type.IsNoncopyable) {
            return (null, $"'{type.Name}' is '@noncopy', and a service is shared by whoever asks for it");
        }

        var all = type.GetMembers("init").OfType<FunctionSymbol>().ToList();
        if (Of(type) is not { } init) {
            return (null, all.Count == 0
                ? $"'{type.Name}' has no initialiser to make it with"
                : $"'{type.Name}' has {all.Count} initialisers; mark the one a container is to use '@Inject'");
        }

        var at = init.DeclaringSyntax.FirstOrDefault()?.Span;
        if (init.IsAsync) {
            return (at, $"'{type.Name}' has an async initialiser, which a container cannot wait for; register a closure that gives one already made");
        }

        if (!init.TypeParameters.IsEmpty) {
            return (at, $"'{type.Name}' has an initialiser generic over type parameters of its own, which nothing says");
        }

        foreach (var parameter in init.Parameters) {
            var here = parameter.DeclaringSyntax.FirstOrDefault()?.Span ?? at;
            var what = $"'{type.Name}' cannot derive 'Injectable': its parameter '{parameter.Name}'";
            if (parameter.IsCallersStorage || parameter.IsVariadic || parameter.IsAutoclosure) {
                return (here, $"{what} takes what no container gives: {(parameter.IsVariadic ? "any number of values" : parameter.IsAutoclosure ? "an expression" : "a place to change")}");
            }

            if (parameter.Type is FunctionTypeSymbol && Classify(compilation, parameter.Type).Kind != InjectedKind.Factory) {
                return (here, $"{what} is a '{parameter.Type.ToDisplayString()}'; a container gives a function that takes nothing, and neither throws nor waits");
            }

            if (parameter.Type is ReferenceTypeSymbol or ArrayTypeSymbol { ArrayKind: ArrayKind.Slice or ArrayKind.Inline }) {
                return (here, $"{what} is a '{parameter.Type.ToDisplayString()}', which refers into what somebody else holds, or is of a size of its own");
            }

            if (KeyedAttribute(type, parameter) is { } keyed && KeyOf(type, parameter) is null) {
                return (keyed.Span, "'@Keyed' takes the key as a string literal");
            }
        }

        return null;
    }
}
