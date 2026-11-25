using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>A member <c>@derive</c> makes.</summary>
public enum DerivedMember {
    /// <summary><c>==</c>, field by field: <c>Equatable</c>.</summary>
    Equals,

    /// <summary><c>&lt;</c>, lexicographic in declaration order: <c>Comparable</c>.</summary>
    LessThan,

    /// <summary><c>hash(into:)</c>, each field fed to the hasher: <c>Hashable</c>.</summary>
    Hash,

    /// <summary><c>debugDescription()</c>, as source would write the value: <c>Debug</c>.</summary>
    Debug,

    /// <summary><c>encode(to:)</c>, field by field to an Encoder: Sable.Serialize's <c>Serializable</c>.</summary>
    Encode,

    /// <summary><c>static decode(from:)</c>, field by field from a Decoder: <c>Serializable</c>.</summary>
    Decode,

    /// <summary><c>static create(from:)</c>, the initialiser called with what a scope gives: Sable.DependencyInjection's <c>Injectable</c>.</summary>
    Create,

    /// <summary><c>static dependencies()</c>, what that is: <c>Injectable</c>.</summary>
    Dependencies,
}

/// <summary>One member <c>@derive</c> made, and where it was asked for.</summary>
public sealed record Derivation(
    FunctionSymbol Function,
    NamedTypeSymbol Type,
    NamedTypeSymbol Protocol,
    DerivedMember Member,
    SyntaxTree Tree
) {
    /// <summary>The <c>@derive</c> that asked for it.</summary>
    public AttributeSyntax Attribute => (AttributeSyntax)Function.DeclaringSyntax[0];
}

/// <summary>
///     <c>@derive(Equatable, Hashable, Comparable, Debug)</c> (docs/06 §6.5).
/// </summary>
/// <remarks>
///     <para>
///         Derivation is conformance synthesis the compiler knows how to do, not
///         source generation: the members are symbols on the type, made once every
///         type is declared, and their bodies are bound trees built straight from
///         the type's fields (<see cref="DerivedBodies" />), which SIL lowering
///         takes as it takes any other body. Nothing is parsed, so nothing a
///         derived member does can be reported against text the program never
///         wrote.
///     </para>
///     <para>
///         On a generic type the conformance is conditional: <c>Pair&lt;A, B&gt;</c>
///         is <c>Equatable</c> where its fields' types are, decided for each
///         construction (<see cref="DerivedConformance" />). A field that can never
///         conform — one whose type mentions none of the parameters — is an error
///         at the declaration, naming the field.
///     </para>
///     <para>
///         Only structs and unions derive. A class has identity: two objects with
///         equal fields are still two objects, and deriving equality from the
///         fields would make <c>==</c> say otherwise. A class that wants value
///         equality writes it.
///     </para>
/// </remarks>
public sealed partial class Compilation {
    /// <summary>The protocols <c>@derive</c> knows, in the order their members are made.</summary>
    static readonly (string Protocol, DerivedMember Member)[] Derivable = [
        ("Equatable", DerivedMember.Equals),
        ("Comparable", DerivedMember.LessThan),
        ("Hashable", DerivedMember.Hash),
        ("Debug", DerivedMember.Debug),
        ("Serializable", DerivedMember.Encode),
        ("Serializable", DerivedMember.Decode),
        ("Injectable", DerivedMember.Create),
        ("Injectable", DerivedMember.Dependencies),
    ];

    readonly List<(NamedTypeSymbol Type, AttributeSyntax Attribute, FileScope? Scope)> _derivations = [];
    int _derivationsDeclared;
    readonly List<Derivation> _derived = [];
    readonly Dictionary<FunctionSymbol, Derivation> _derivedByFunction = [];
    readonly Dictionary<FunctionSymbol, BoundBlock?> _derivedBodies = [];
    DerivedBodies? _derivedBodyBuilder;

    /// <summary>Every member <c>@derive</c> made, in the order the program asked for them.</summary>
    public IReadOnlyList<Derivation> Derivations => _derived;

    /// <summary>The derivation that made a function, if one did.</summary>
    public Derivation? GetDerivation(FunctionSymbol function) =>
        _derivedByFunction.GetValueOrDefault(function);

    /// <summary>A derived member's body, built on first asking; null for any other function.</summary>
    public BoundBlock? GetDerivedBody(FunctionSymbol function) {
        if (!_derivedByFunction.TryGetValue(function, out var derivation)) {
            return null;
        }

        if (!_derivedBodies.TryGetValue(function, out var body)) {
            _derivedBodyBuilder ??= new(Core, ProtocolRequirement, this);
            _derivedBodies[function] = body = _derivedBodyBuilder.Build(derivation);
        }

        return body;
    }

    /// <summary>A core protocol's requirement, by protocol and member name.</summary>
    FunctionSymbol? ProtocolRequirement(string protocol, string member) =>
        CoreProtocol(protocol)?.GetMembers(member).OfType<FunctionSymbol>().FirstOrDefault();

    NamedTypeSymbol? CoreProtocol(string name) =>
        GetModule("Sable.Core")?.GetType(name) is { TypeKind: TypeKind.Protocol } protocol ? protocol : null;

    /// <summary>A protocol <c>@derive</c> knows: Sable.Core's, Sable.Serialize's <c>Serializable</c>, or Sable.DependencyInjection's <c>Injectable</c>.</summary>
    NamedTypeSymbol? DerivableProtocol(string name) =>
        name == "Injectable"
            ? InjectionType(name) is { TypeKind: TypeKind.Protocol } injectable ? injectable : null
        : name == "Serializable"
            ? GetModule("Sable.Serialize")?.GetType(name) is { TypeKind: TypeKind.Protocol } serializable ? serializable : null
            : CoreProtocol(name);

    /// <summary>A type of Sable.Serialize's, which a derived Serializable conformance uses.</summary>
    internal NamedTypeSymbol? SerializeType(string name) => GetModule("Sable.Serialize")?.GetType(name);

    /// <summary>A type of Sable.DependencyInjection's, which a derived Injectable conformance uses.</summary>
    internal NamedTypeSymbol? InjectionType(string name) => GetModule("Sable.DependencyInjection")?.GetType(name);

    /// <summary>A parameter's default value, as a call at <paramref name="at" /> that leaves it off has it.</summary>
    internal BoundExpression? BindParameterDefault(SyntaxNode at, ParameterSymbol parameter, TypeSubstitution substitution) =>
        FileScopeOf(at) is { } scope ? new Binder(this, scope, parameter.ContainingSymbol!).DefaultArgument(at, parameter, substitution) : null;

    /// <summary>
    ///     A field's initial value, bound as a memberwise initialiser would put
    ///     it in: what a derived decoding uses for a field that is skipped, or
    ///     defaulted and missing.
    /// </summary>
    internal BoundExpression? BindFieldDefault(FieldSymbol field, TypeSymbol type) {
        if (field.DeclaringSyntax is not [FieldDeclarationSyntax declaration]
            || FileScopeOf(declaration) is not { } scope
            || declaration.Declarator.Initializer is not { } initializer) {
            return null;
        }

        return new Binder(this, scope, field).BindInitializer(initializer.Value, type);
    }

    /// <summary>Makes the members of every <c>@derive</c> declared since the last call.</summary>
    void DeclareDerivations() {
        while (_derivationsDeclared < _derivations.Count) {
            var (type, attribute, scope) = _derivations[_derivationsDeclared++];
            if (scope is not null) {
                Derive(type, attribute, scope.Tree);
            }
        }
    }

    void Derive(NamedTypeSymbol type, AttributeSyntax attribute, SyntaxTree tree) {
        // A container makes a class, an actor or a struct.
        var injects = type.TypeKind is TypeKind.Class or TypeKind.Actor or TypeKind.Struct
                      && attribute.ArgumentList is { Arguments.Count: > 0 } named && named.Arguments.All(static a => a.Expression.ToString().Trim() == "Injectable");
        if (!injects && type.TypeKind is not (TypeKind.Struct or TypeKind.Union or TypeKind.Enum) || type.IsFlags) {
            ReportDeclaration(
                ErrorCode.ERR_CannotDerive,
                attribute.Span,
                type.TypeKind is TypeKind.Class or TypeKind.Actor
                    ? $"'@derive' is for structs, unions and enums; '{type.Name}' is a {(type.TypeKind == TypeKind.Class ? "class" : "actor")}, "
                      + "whose objects have identity, so equality and hashing by its fields would be wrong — write '==' and 'hash(into:)' for it"
                    : type.IsFlags
                        ? $"'{type.Name}' is a '@flags' enum, compared as the integer it is; '@derive' is for structs, unions and other enums"
                        : $"'@derive' is for structs, unions and enums, not '{type.Name}'"
            );
            return;
        }

        if (attribute.ArgumentList is not { Arguments.Count: > 0 } arguments) {
            ReportDeclaration(
                ErrorCode.ERR_UnknownDerivation,
                attribute.Span,
                "'@derive' names what to derive, as in '@derive(Equatable, Hashable)'"
            );
            return;
        }

        // What was asked for, by name; Hashable and Comparable both refine
        // Equatable, so either brings '==' unless the type writes its own.
        var asked = new Dictionary<string, ArgumentSyntax?>(StringComparer.Ordinal);
        foreach (var argument in arguments.Arguments) {
            var name = argument.Expression.ToString().Trim();

            // A struct or union is copied unless it is '@noncopy', so 'Copy'
            // makes nothing: it says the type is copied, and that is checked.
            if (name == "Copy") {
                if (type.IsNoncopyable) {
                    ReportDeclaration(
                        ErrorCode.ERR_CannotDerive,
                        argument.Span,
                        $"'{type.Name}' is '@noncopy', so it is moved, never copied: '@derive(Copy)' cannot hold of it"
                    );
                }

                continue;
            }

            if (!Derivable.Any(d => d.Protocol == name)) {
                ReportDeclaration(
                    ErrorCode.ERR_UnknownDerivation,
                    argument.Span,
                    $"'{name}' cannot be derived; '@derive' knows Equatable, Hashable, Comparable, Debug, Serializable, Injectable and Copy"
                );
                continue;
            }

            asked.TryAdd(name, argument);
        }

        if (asked.ContainsKey("Hashable") || asked.ContainsKey("Comparable")) {
            asked.TryAdd("Equatable", null);
        }

        var self = type.IsGenericDefinition ? type.Construct([..type.TypeParameters]) : type;
        foreach (var (protocolName, member) in Derivable) {
            if (!asked.TryGetValue(protocolName, out var argument)) {
                continue;
            }

            var isExplicit = argument is not null;
            var at = argument?.Span ?? attribute.Span;
            if (DerivableProtocol(protocolName) is not { } protocol) {
                ReportDeclaration(
                    ErrorCode.ERR_CannotDerive,
                    at,
                    protocolName == "Serializable"
                        ? "'@derive(Serializable)' needs Sable.Serialize: 'import Sable.Serialize'"
                        : protocolName == "Injectable" ? "'@derive(Injectable)' needs Sable.DependencyInjection: 'import Sable.DependencyInjection'"
                        : $"'@derive({protocolName})' needs Sable.Core's '{protocolName}', which is not here"
                );
                continue;
            }

            var serialize = member is DerivedMember.Encode or DerivedMember.Decode;
            if (serialize && (SerializeType("Encoder") is null || SerializeType("Decoder") is null || SerializeType("SerializeError") is null)) {
                continue;
            }

            // The options '@serialize' gives are checked once, as the first
            // of the two members is made.
            if (member == DerivedMember.Encode && !SerializeOptions.Check(type, (span, message) => ReportDeclaration(ErrorCode.ERR_CannotDerive, span, message))) {
                break;
            }

            var memberName = NameOf(member);
            var hasher = member == DerivedMember.Hash ? GetModule("Sable.Core")?.GetType("Hasher") : null;
            if (member == DerivedMember.Hash && hasher is null) {
                ReportDeclaration(ErrorCode.ERR_CannotDerive, at, "'@derive(Hashable)' needs Sable.Core's 'Hasher', which is not here");
                continue;
            }

            if (member is DerivedMember.Create && type.TypeKind is not (TypeKind.Class or TypeKind.Actor or TypeKind.Struct)) {
                ReportDeclaration(ErrorCode.ERR_CannotDerive, at, $"'@derive(Injectable)' is for classes, actors and structs, which an initialiser makes; '{type.Name}' is not one");
                break;
            }

            if (type.GetMembers(memberName).Any()) {
                // An implied '==' the type already has is the one to use.
                if (isExplicit) {
                    ReportDeclaration(
                        ErrorCode.ERR_CannotDerive,
                        at,
                        $"'{type.Name}' declares its own '{memberName}', so there is nothing for '@derive({protocolName})' to make; drop one or the other"
                    );
                }

                continue;
            }

            var function = DeclareDerivedMember(type, self, member, attribute, hasher);
            if (protocolName == "Serializable" && member == DerivedMember.Decode) {
                isExplicit = false;
            }
            var derivation = new Derivation(function, type, protocol, member, tree);
            _derived.Add(derivation);
            _derivedByFunction[function] = derivation;

            if (member is DerivedMember.Create or DerivedMember.Dependencies) {
                isExplicit = false;
                if (member == DerivedMember.Create) {
                    TypeBinder.DeferLate(() => {
                            if (InjectedInitializer.WhyNot(this, type) is var (where, why)) {
                                ReportDeclaration(ErrorCode.ERR_CannotDerive, where ?? at, why);
                            }
                        }
                    );
                }
            } else if (type.IsGenericDefinition && !type.DerivedConformances.Contains(protocol)) {
                type.DerivedConformances = type.DerivedConformances.Add(protocol);
            }

            // After the type's own base list is bound, which replaces the list.
            TypeBinder.Defer(() => {
                    if (!type.BaseTypes.Contains(protocol)) {
                        type.BaseTypes = type.BaseTypes.Add(protocol);
                    }
                }
            );

            // Implied, it is checked by what implied it: a field that is not
            // Hashable is reported once, as not Hashable.
            if (isExplicit) {
                TypeBinder.DeferLate(() => CheckDerivedFields(type, protocol, at));
            }
        }
    }

    static string NameOf(DerivedMember member) =>
        member switch {
            DerivedMember.Equals => "==",
            DerivedMember.LessThan => "<",
            DerivedMember.Hash => "hash",
            DerivedMember.Encode => "encode",
            DerivedMember.Decode => "decode",
            DerivedMember.Create => "create",
            DerivedMember.Dependencies => "dependencies",
            _ => "debugDescription",
        };

    /// <summary>
    ///     The member's symbol on the type: <c>==</c> and <c>&lt;</c> static
    ///     operators of two, <c>hash(into hasher: inout Hasher)</c> and
    ///     <c>debugDescription()</c> methods.
    /// </summary>
    FunctionSymbol DeclareDerivedMember(NamedTypeSymbol type, NamedTypeSymbol self, DerivedMember member, AttributeSyntax attribute, NamedTypeSymbol? hasher) {
        var isOperator = member is DerivedMember.Equals or DerivedMember.LessThan;
        var function = new FunctionSymbol(
            NameOf(member),
            type,
            attribute,
            type.DeclaredAccessibility,
            isOperator ? FunctionKind.Operator : FunctionKind.Ordinary
        ) {
            IsStatic = isOperator || member is DerivedMember.Decode or DerivedMember.Create or DerivedMember.Dependencies,
        };

        if (member is DerivedMember.Create) {
            function.Parameters = [new ParameterSymbol("services", "from", function, null, 0) { TypeResolver = () => InjectionType("ServiceScope") ?? (TypeSymbol)ErrorTypeSymbol.Instance }];
            function.ThrownTypeResolver = () => Core.AnyError;
            function.ReturnTypeResolver = () => self;
            type.AddMember(function);
            return function;
        }

        if (member is DerivedMember.Dependencies) {
            function.ReturnTypeResolver = () => new ArrayTypeSymbol(InjectionType("ServiceDependency") ?? (TypeSymbol)ErrorTypeSymbol.Instance, ArrayKind.Grow);
            type.AddMember(function);
            return function;
        }

        if (member is DerivedMember.Encode or DerivedMember.Decode) {
            var error = SerializeType("SerializeError")!;
            // 'encode<E: Encoder>(to encoder: E)', 'decode<D: Decoder>(from decoder: D)'.
            var format = new TypeParameterSymbol(member == DerivedMember.Encode ? "E" : "D", function, null, 0) {
                Constraints = [SerializeType(member == DerivedMember.Encode ? "Encoder" : "Decoder")!],
            };
            function.TypeParameters = [format];
            function.Parameters = member == DerivedMember.Encode
                ? [new ParameterSymbol("encoder", "to", function, null, 0) { TypeResolver = () => format }]
                : [new ParameterSymbol("decoder", "from", function, null, 0) { TypeResolver = () => format }];
            function.ThrownTypeResolver = () => error;
            function.ReturnTypeResolver = member == DerivedMember.Encode ? () => Core.Void : () => self;
            type.AddMember(function);
            return function;
        }

        function.Parameters = isOperator
            ? [
                new ParameterSymbol("lhs", null, function, null, 0) { TypeResolver = () => self },
                new ParameterSymbol("rhs", null, function, null, 1) { TypeResolver = () => self },
            ]
            : member == DerivedMember.Hash
            ? [new ParameterSymbol("hasher", "into", function, null, 0) { TypeResolver = () => hasher!, Modifier = ParameterModifier.InOut }]
            : [];

        var result = member switch {
            DerivedMember.Hash => Core.Void,
            DerivedMember.Debug => Core.String,
            _ => Core.Bool,
        };
        function.ReturnTypeResolver = () => result;
        type.AddMember(function);
        return function;
    }

    /// <summary>
    ///     Every field's type has to conform for the derived member to be
    ///     written: one that never can is an error, naming it.
    /// </summary>
    void CheckDerivedFields(NamedTypeSymbol type, NamedTypeSymbol protocol, TextSpan at) {
        foreach (var (field, owner, fieldType) in DerivedConformance.Fields(type)) {
            // On a generic type, a field that mentions a parameter conforms
            // or not for each construction: the conformance is conditional.
            if (fieldType.IsError || (type.IsGenericDefinition && DerivedConformance.MentionsParameters(type, fieldType))) {
                continue;
            }

            if (DerivedConformance.FieldConforms(fieldType, protocol)) {
                continue;
            }

            var what = owner is null
                ? $"its field '{field}'"
                : $"the payload '{field}' of its case '{owner}'";
            ReportDeclaration(
                ErrorCode.ERR_DerivedFieldDoesNotConform,
                at,
                $"'{type.Name}' cannot derive '{protocol.Name}': {what} is of type "
                + $"'{fieldType.ToDisplayString()}', which is not '{protocol.Name}'"
            );
        }
    }
}

/// <summary>When a conformance <c>@derive</c> gave a generic type holds.</summary>
internal static class DerivedConformance {
    /// <summary>
    ///     The pairs being decided, so that a recursive type — a tree of
    ///     itself — is assumed to conform while its own fields are asked about.
    /// </summary>
    [ThreadStatic] static HashSet<(TypeSymbol, NamedTypeSymbol)>? _assumed;

    /// <summary>
    ///     True when every stored field of this construction, with its type
    ///     arguments substituted, conforms to the protocol.
    /// </summary>
    public static bool Holds(NamedTypeSymbol type, NamedTypeSymbol protocol, TypeEquivalence? context) {
        _assumed ??= [];
        if (!_assumed.Add((type, protocol))) {
            return true;
        }

        try {
            var substitution = TypeSubstitution.For(type);
            return Fields(type.OriginalDefinition).All(f => FieldConforms(substitution.Apply(f.Type), protocol, context));
        } finally {
            _assumed.Remove((type, protocol));
        }
    }

    /// <summary>
    ///     True when a field of the type can be compared, hashed, ordered or
    ///     described as the protocol asks. Beyond what conforms, an optional
    ///     or an array compares where what it holds does: two are equal when
    ///     both are none or hold equal values, or are as long as each other
    ///     and equal element by element — '==' on them is the language's own.
    /// </summary>
    public static bool FieldConforms(TypeSymbol type, NamedTypeSymbol protocol, TypeEquivalence? context = null) =>
        Conversions.Satisfies(type, protocol, context)
        || protocol.Name == "Equatable" && type switch {
            OptionalTypeSymbol optional => FieldConforms(optional.UnderlyingType, protocol, context),
            ArrayTypeSymbol array => FieldConforms(array.ElementType, protocol, context),
            _ => false,
        };

    /// <summary>
    ///     What a derived member reads: a struct's stored fields, or every
    ///     case's payload, with the case it belongs to.
    /// </summary>
    public static IEnumerable<(string Name, string? Case, TypeSymbol Type)> Fields(NamedTypeSymbol type) =>
        type.TypeKind == TypeKind.Union
            ? type.Members.OfType<UnionCaseSymbol>().SelectMany(c => c.Payload.Select(p => (p.Label ?? $"{p.Ordinal}", (string?)c.Name, p.Type)))
            : StoredFields(type).Select(static f => (f.DisplayName, (string?)null, f.Type));

    public static IEnumerable<FieldSymbol> StoredFields(NamedTypeSymbol type) =>
        type.Members.OfType<FieldSymbol>().Where(static f => f is { IsStatic: false, IsComputed: false });

    /// <summary>True when a type is written in terms of the generic type's parameters.</summary>
    public static bool MentionsParameters(NamedTypeSymbol type, TypeSymbol written) {
        var away = new TypeSubstitution(type.TypeParameters.ToDictionary(static p => p, TypeSymbol (_) => new TypeParameterSymbol("$away", type, null, -1)));
        return !away.Apply(written).Equals(written);
    }
}
