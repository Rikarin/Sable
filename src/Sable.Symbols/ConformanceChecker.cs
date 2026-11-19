using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     Checks that a type does what its base list says it does.
/// </summary>
/// <remarks>
///     <para>
///         Conformance is nominal and explicit, per docs/03-type-system.md
///         §3.2: satisfying a protocol's members by accident is not enough, and a
///         declared conformance that is not met is an error naming what is
///         missing.
///     </para>
///     <para>
///         A protocol member with a body is a default implementation and is not
///         a requirement. A member with none is, and the conforming type — or
///         something it inherits from — has to supply it.
///     </para>
/// </remarks>
internal static class ConformanceChecker {
    public static void Check(NamedTypeSymbol type, DiagnosticBag diagnostics, CoreTypes core) {
        if (type.TypeKind == TypeKind.Protocol) {
            return;
        }

        var classBases = 0;
        var conformances = new HashSet<TypeSymbol>();

        foreach (var baseType in type.BaseTypes) {
            if (baseType.IsError) {
                continue;
            }

            // At most one conformance per (type, protocol) pair, globally
            // (docs/03 §3.2) — and so certainly not two in one place.
            if (baseType is NamedTypeSymbol { TypeKind: TypeKind.Protocol } && !conformances.Add(baseType)) {
                Report(
                    diagnostics,
                    ErrorCode.ERR_DuplicateConformance,
                    type,
                    $"'{type.Name}' already conforms to '{baseType.ToDisplayString()}'"
                );
                continue;
            }

            if (baseType is not NamedTypeSymbol named) {
                Report(
                    diagnostics,
                    ErrorCode.ERR_NotAProtocol,
                    type,
                    $"'{baseType.ToDisplayString()}' is not a type "
                    + $"'{type.Name}' can inherit from or conform to"
                );
                continue;
            }

            // An enum's base list names its underlying representation as well
            // as any protocols it conforms to.
            if (type.TypeKind == TypeKind.Enum && core.IsInteger(named)) {
                continue;
            }

            switch (named.TypeKind) {
                // '@inline_layout' is how a struct conforms: only for that
                // layout can the compiler write 'fromBytes' (Compilation.InlineLayout.cs).
                case TypeKind.Protocol when ReferenceEquals(named, core.InlineLayout) && !type.IsInlineLayout:
                    Report(
                        diagnostics,
                        ErrorCode.ERR_DoesNotConform,
                        type,
                        $"'{type.Name}' cannot conform to 'InlineLayout' by naming it: the compiler conforms "
                        + "every '@inline_layout' struct, writing 'fromBytes' for its layout; mark the struct '@inline_layout'"
                    );
                    break;

                case TypeKind.Protocol:
                    CheckProtocol(type, named, diagnostics, HasBuiltins(type, core));
                    break;

                case TypeKind.Class when type.TypeKind is TypeKind.Class or TypeKind.Actor:
                    classBases++;
                    break;

                default:
                    Report(
                        diagnostics,
                        ErrorCode.ERR_NotAProtocol,
                        type,
                        $"'{type.Name}' cannot inherit from "
                        + $"'{named.ToDisplayString()}'; only a protocol or a class"
                    );
                    break;
            }
        }

        // Sendable has no members to check; what it asks is what the type is
        // made of. Checked once, however many protocols bring it in.
        if (type.BaseTypes.Any(static b => !b.IsError && Sendability.RefinesSendable(b))
            && Sendability.WhyNotAsDeclared(type) is { } notSendable) {
            Report(diagnostics, ErrorCode.ERR_DeclaredSendableIsNot, type, notSendable);
        }

        if (classBases > 1) {
            Report(
                diagnostics,
                ErrorCode.ERR_MultipleBaseClasses,
                type,
                $"'{type.Name}' lists more than one base class; inheritance is single"
            );
        }

        if (InheritsItself(type)) {
            Report(
                diagnostics,
                ErrorCode.ERR_CircularInheritance,
                type,
                $"'{type.Name}' inherits from itself"
            );
            return;
        }

        CheckInheritance(type, diagnostics);
        CheckOverrides(type, diagnostics);
        CheckExplicitImplementations(type, diagnostics);
    }

    static bool SameProtocol(TypeSymbol a, TypeSymbol b) =>
        a is NamedTypeSymbol x && b is NamedTypeSymbol y && ReferenceEquals(x.OriginalDefinition, y.OriginalDefinition);

    /// <summary>
    ///     <c>fn Drawable.render()</c> (SYNTAX.md §7) names a protocol the type
    ///     conforms to, and implements one of its requirements.
    /// </summary>
    static void CheckExplicitImplementations(NamedTypeSymbol type, DiagnosticBag diagnostics) {
        foreach (var function in type.Members.OfType<FunctionSymbol>()) {
            if (function.ExplicitProtocol is not { IsError: false } named) {
                continue;
            }

            if (named is not NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol) {
                Report(
                    diagnostics,
                    ErrorCode.ERR_NotAProtocol,
                    function,
                    $"'{named.ToDisplayString()}.{function.Name}' implements a requirement of '{named.ToDisplayString()}', "
                    + "which is not a protocol"
                );
                continue;
            }

            if (ProtocolsOf(type).FirstOrDefault(p => SameProtocol(p, protocol)) is not { } conformed) {
                Report(
                    diagnostics,
                    ErrorCode.ERR_DoesNotConform,
                    function,
                    $"'{protocol.Name}.{function.Name}' implements a requirement of '{protocol.Name}', "
                    + $"but '{type.Name}' does not conform to '{protocol.Name}'"
                );
                continue;
            }

            // A generic protocol's requirements are read with its arguments.
            var arguments = TypeSubstitution.For(conformed);
            var requirements = conformed.OriginalDefinition.Members.OfType<FunctionSymbol>()
                .Where(r => r.IsAbstract && r.Name == function.Name);
            if (!requirements.Any(r => Matches(r, function, conformed.OriginalDefinition, arguments, type))) {
                Report(
                    diagnostics,
                    ErrorCode.ERR_RequirementSignatureMismatch,
                    function,
                    $"'{protocol.Name}.{function.Name}' matches no requirement of '{protocol.Name}'"
                );
            }
        }
    }

    /// <summary>Every protocol a type conforms to: those it names, those they refine, and its base classes'.</summary>
    static IEnumerable<NamedTypeSymbol> ProtocolsOf(NamedTypeSymbol type) {
        var seen = new HashSet<NamedTypeSymbol>();
        var queue = new Queue<NamedTypeSymbol>();
        queue.Enqueue(type);

        while (queue.Count > 0) {
            var current = queue.Dequeue();
            if (!seen.Add(current)) {
                continue;
            }

            if (current.TypeKind == TypeKind.Protocol) {
                yield return current;
            }

            foreach (var baseType in current.BaseTypes) {
                if (baseType is NamedTypeSymbol named) {
                    queue.Enqueue(named);
                }
            }
        }
    }

    /// <summary>What a base class allows, per spec/SYNTAX.md §2.</summary>
    static void CheckInheritance(NamedTypeSymbol type, DiagnosticBag diagnostics) {
        foreach (var baseType in type.BaseTypes) {
            if (baseType is not NamedTypeSymbol { TypeKind: TypeKind.Class } baseClass) {
                continue;
            }

            if (baseClass.IsSealed || baseClass.IsFinal) {
                var keyword = baseClass.IsFinal ? "final" : "sealed";
                Report(
                    diagnostics,
                    ErrorCode.ERR_NotOpen,
                    type,
                    $"'{baseClass.Name}' is {keyword}, so '{type.Name}' cannot inherit from it"
                );
                continue;
            }

            // 'open' is required to subclass across modules; inside one, a
            // class is open to its own module.
            if (!baseClass.IsOpen
                && !ReferenceEquals(baseClass.ContainingModule, type.ContainingModule)) {
                Report(
                    diagnostics,
                    ErrorCode.ERR_NotOpen,
                    type,
                    $"'{baseClass.Name}' is not 'open', so it cannot be subclassed "
                    + "outside the module that declares it"
                );
            }
        }
    }

    /// <summary>
    ///     <c>override</c> is required where a member replaces a base one, and
    ///     forbidden where it replaces nothing. What it replaces must allow it,
    ///     and the replacement must keep its signature: a call through the base
    ///     reaches the override, and reads what it returns as the base's type.
    /// </summary>
    static void CheckOverrides(NamedTypeSymbol type, DiagnosticBag diagnostics) {
        foreach (var member in type.Members) {
            if (member is FieldSymbol { IsOverride: true } property) {
                CheckPropertyOverride(type, property, diagnostics);
                continue;
            }

            if (member is not FunctionSymbol { FunctionKind: FunctionKind.Ordinary } function) {
                continue;
            }

            if (FindOverridden(type, function) is not var (overridden, seen)) {
                if (!function.IsOverride) {
                    continue;
                }

                Report(
                    diagnostics,
                    ErrorCode.ERR_NothingToOverride,
                    function,
                    $"'{type.Name}.{function.Name}' is marked 'override' but no base class "
                    + "declares it"
                );
                continue;
            }

            if (!function.IsOverride) {
                Report(
                    diagnostics,
                    ErrorCode.ERR_MissingOverride,
                    function,
                    $"'{type.Name}.{function.Name}' replaces a member of "
                    + $"'{overridden.ContainingSymbol?.Name}', so it needs 'override'",
                    overridden.DeclaredHere("the member it replaces")
                );
                continue;
            }

            if (overridden.IsFinal) {
                Report(
                    diagnostics,
                    ErrorCode.ERR_NotOpen,
                    function,
                    $"'{overridden.ContainingSymbol?.Name}.{overridden.Name}' is 'final', so it cannot be overridden",
                    overridden.DeclaredHere("declared 'final' here")
                );
                continue;
            }

            if (!overridden.IsOpen
                && !ReferenceEquals(overridden.ContainingModule, type.ContainingModule)) {
                Report(
                    diagnostics,
                    ErrorCode.ERR_NotOpen,
                    function,
                    $"'{overridden.ContainingSymbol?.Name}.{overridden.Name}' is not 'open', "
                    + "so it cannot be overridden outside the module that declares it"
                );
                continue;
            }

            if (OverrideMismatch(function, overridden, seen) is { } mismatch) {
                Report(
                    diagnostics,
                    ErrorCode.ERR_OverrideSignatureMismatch,
                    function,
                    $"'{type.Name}.{function.Name}' overrides '{overridden.ContainingSymbol?.Name}.{overridden.Name}', "
                    + mismatch,
                    overridden.DeclaredHere("the member it overrides")
                );
            }
        }
    }

    /// <summary>
    ///     How an override's signature differs from what it replaces, beyond the
    ///     parameter types that matched it: the base's callers read its result,
    ///     handle what it throws and await it as the base declares, so each has
    ///     to stay the same.
    /// </summary>
    static string? OverrideMismatch(FunctionSymbol function, FunctionSymbol overridden, TypeSubstitution seen) {
        var returned = seen.Apply(overridden.ReturnType);
        if (!function.ReturnType.Equals(returned)
            && !function.ReturnType.IsError && !returned.IsError) {
            return $"which returns '{returned.ToDisplayString()}', "
                + $"but returns '{function.ReturnType.ToDisplayString()}'";
        }

        if (function.IsAsync != overridden.IsAsync) {
            return overridden.IsAsync
                ? "which is 'async', but is not"
                : "which is not 'async', but is";
        }

        if (function.IsStatic != overridden.IsStatic) {
            return overridden.IsStatic
                ? "which is 'static', but is not"
                : "which is not 'static', but is";
        }

        var thrown = function.ThrownType;
        var allowed = overridden.ThrownType is { } declared ? seen.Apply(declared) : null;
        if (thrown?.IsError == true || allowed?.IsError == true) {
            return null;
        }

        // Callers through the base handle exactly what the base says it
        // throws, in the shape the base's slot has, so the effect is kept.
        if (thrown is not null && allowed is null) {
            return $"which throws nothing, but throws '{thrown.ToDisplayString()}'";
        }

        if (thrown is null && allowed is not null) {
            return $"which throws '{allowed.ToDisplayString()}', but throws nothing";
        }

        if (thrown is not null && !thrown.Equals(allowed)) {
            return $"which throws '{allowed!.ToDisplayString()}', but throws '{thrown.ToDisplayString()}'";
        }

        return null;
    }

    /// <summary>
    ///     <c>override var p</c>: there must be a base property <c>p</c> to
    ///     replace, of the same type, writable if the base's is, and not
    ///     <c>final</c>.
    /// </summary>
    static void CheckPropertyOverride(NamedTypeSymbol type, FieldSymbol property, DiagnosticBag diagnostics) {
        var found = FindOverriddenProperty(type, property.Name);

        if (found is not var (overridden, seen) || overridden.IsStatic != property.IsStatic) {
            Report(
                diagnostics,
                ErrorCode.ERR_NothingToOverride,
                property,
                $"'{type.Name}.{property.Name}' is marked 'override' but no base class declares it"
            );
            return;
        }

        if (overridden.IsFinal) {
            Report(
                diagnostics,
                ErrorCode.ERR_NotOpen,
                property,
                $"'{overridden.ContainingSymbol?.Name}.{overridden.Name}' is 'final', so it cannot be overridden"
            );
            return;
        }

        var expected = seen.Apply(overridden.Type);
        if (!property.Type.Equals(expected) && !property.Type.IsError && !expected.IsError) {
            Report(
                diagnostics,
                ErrorCode.ERR_OverrideSignatureMismatch,
                property,
                $"'{type.Name}.{property.Name}' overrides '{overridden.ContainingSymbol?.Name}.{overridden.Name}', "
                + $"which is '{expected.ToDisplayString()}', but is '{property.Type.ToDisplayString()}'"
            );
            return;
        }

        var baseWritable = overridden.IsComputed ? overridden.HasSetter : overridden.IsMutable;
        var writable = property.IsComputed ? property.HasSetter : property.IsMutable;
        if (baseWritable && !writable) {
            Report(
                diagnostics,
                ErrorCode.ERR_OverrideSignatureMismatch,
                property,
                $"'{type.Name}.{property.Name}' overrides '{overridden.ContainingSymbol?.Name}.{overridden.Name}', "
                + "which can be written, but cannot be"
            );
        }
    }

    /// <summary>The nearest base-class property of this name, if any, and the base's type arguments as this type sees them.</summary>
    static (FieldSymbol Property, TypeSubstitution Seen)? FindOverriddenProperty(
        NamedTypeSymbol type,
        string name,
        TypeSubstitution? outer = null
    ) {
        foreach (var baseType in type.BaseTypes) {
            if ((outer ?? TypeSubstitution.Empty).Apply(baseType) is not NamedTypeSymbol { TypeKind: TypeKind.Class } baseClass) {
                continue;
            }

            var seen = TypeSubstitution.For(baseClass);
            if (baseClass.GetMembers(name).OfType<FieldSymbol>().FirstOrDefault() is { } found) {
                return (found, seen);
            }

            if (FindOverriddenProperty(baseClass.OriginalDefinition, name, seen) is { } inherited) {
                return inherited;
            }
        }

        return null;
    }

    /// <summary>
    ///     The member a method of a class finally overrides: the one a
    ///     vtable gives its slot. Null for a method that overrides nothing.
    /// </summary>
    public static FunctionSymbol? RootOverridden(FunctionSymbol function) {
        if (!function.IsOverride || function.ContainingSymbol is not NamedTypeSymbol { TypeKind: TypeKind.Class } type) {
            return null;
        }

        FunctionSymbol? root = null;
        for (var current = FindOverridden(type, function)?.Method; current is not null;) {
            root = current;
            current = current.IsOverride && current.ContainingSymbol is NamedTypeSymbol owner ? FindOverridden(owner, current)?.Method : null;
        }

        return root;
    }

    /// <summary>
    ///     The base-class member a function replaces, if any, and what the
    ///     base's type parameters are where this type inherits it:
    ///     <c>class Ints: Box&lt;Int&gt;</c> sees <c>Box.get() -&gt; T</c> as returning Int.
    /// </summary>
    static (FunctionSymbol Method, TypeSubstitution Seen)? FindOverridden(
        NamedTypeSymbol type,
        FunctionSymbol function,
        TypeSubstitution? outer = null
    ) {
        foreach (var baseType in type.BaseTypes) {
            if ((outer ?? TypeSubstitution.Empty).Apply(baseType) is not NamedTypeSymbol { TypeKind: TypeKind.Class } baseClass) {
                continue;
            }

            var seen = TypeSubstitution.For(baseClass);

            foreach (var candidate in baseClass.GetMembers(function.Name)) {
                if (candidate is not FunctionSymbol other) {
                    continue;
                }

                if (other.Parameters.Length != function.Parameters.Length) {
                    continue;
                }

                var matches = true;
                for (var i = 0; i < other.Parameters.Length; i++) {
                    // In the base's terms as this class names it: 'GDer: GBase<Int>'
                    // overrides 'take(_ x: T)' with 'take(_ x: Int)'.
                    if (seen.Apply(other.Parameters[i].Type).Equals(function.Parameters[i].Type)) {
                        continue;
                    }

                    matches = false;
                    break;
                }

                if (matches) {
                    return (other, seen);
                }
            }

            if (FindOverridden(baseClass.OriginalDefinition, function, seen) is { } inherited) {
                return inherited;
            }
        }

        return null;
    }

    /// <summary>
    ///     What a type binds a protocol's associated types to.
    /// </summary>
    /// <remarks>
    ///     A conformer names them: <c>alias Element = Int</c>, or a nested type
    ///     called Element. Failing that its witnesses say: <c>fn get() -&gt; Int</c>
    ///     meeting <c>fn get() -&gt; Item</c> makes Item an Int. Failing that the
    ///     declaration's own default applies, and failing that the conformance
    ///     is not met.
    /// </remarks>
    public static TypeSymbol? ResolveAssociated(
        NamedTypeSymbol conformer,
        TypeParameterSymbol associated
    ) {
        foreach (var member in conformer.GetMembers(associated.Name)) {
            switch (member) {
                case AliasSymbol alias: return alias.AliasedType;
                case NamedTypeSymbol nested: return nested;
            }
        }

        foreach (var baseType in conformer.BaseTypes) {
            if (baseType is NamedTypeSymbol { TypeKind: not TypeKind.Protocol } baseClass
                && ResolveAssociated(baseClass, associated) is { } inherited) {
                return inherited;
            }
        }

        return InferAssociated(conformer, associated) ?? associated.DefaultType;
    }

    /// <summary>The (conformer, associated type) pairs being inferred, so inference never asks itself.</summary>
    [ThreadStatic]
    static HashSet<(NamedTypeSymbol, TypeParameterSymbol)>? s_inferring;

    /// <summary>
    ///     An associated type read off the conformer's witnesses: the first
    ///     requirement that mentions it, matched against the conformer's own
    ///     member of that name and shape.
    /// </summary>
    static TypeSymbol? InferAssociated(NamedTypeSymbol conformer, TypeParameterSymbol associated) {
        if (associated.ContainingSymbol is not NamedTypeSymbol { TypeKind: TypeKind.Protocol } protocol) {
            return null;
        }

        conformer = conformer.OriginalDefinition;
        s_inferring ??= [];
        if (!s_inferring.Add((conformer, associated))) {
            return null;
        }

        try {
            ImmutableArray<TypeParameterSymbol> inferrable = [associated];
            foreach (var requirement in protocol.Members) {
                foreach (var witness in FindMembers(conformer, requirement.Name)) {
                    // A protocol's default speaks of the associated type
                    // itself, so it says nothing about what it is.
                    if (witness.ContainingSymbol is NamedTypeSymbol { TypeKind: TypeKind.Protocol }
                        or ExtensionSymbol { ExtendedType.TypeKind: TypeKind.Protocol }) {
                        continue;
                    }

                    var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
                    var fits = (requirement, witness) switch {
                        (FunctionSymbol { IsAbstract: true } wanted, FunctionSymbol given) =>
                            wanted.IsStatic == given.IsStatic
                            && wanted.Parameters.Length == given.Parameters.Length
                            && wanted.Parameters.Zip(given.Parameters).All(p => TypeSubstitution.Unify(p.First.Type, p.Second.Type, inferrable, map))
                            && TypeSubstitution.Unify(wanted.ReturnType, given.ReturnType, inferrable, map),
                        (FieldSymbol { IsAbstract: true } wanted, FieldSymbol given) =>
                            TypeSubstitution.Unify(wanted.Type, given.Type, inferrable, map),
                        _ => false,
                    };

                    if (fits && map.TryGetValue(associated, out var inferred) && !inferred.IsError) {
                        return inferred;
                    }
                }
            }

            return null;
        } finally {
            s_inferring.Remove((conformer, associated));
        }
    }

    /// <summary>
    ///     True for the types SIL's builtins work on — the numbers, Bool,
    ///     Char and String — which alone take a protocol's builtin family
    ///     (Compilation.Builtins.cs) as a requirement's default: there is no
    ///     builtin for any other type, so it must meet the requirement itself.
    /// </summary>
    static bool HasBuiltins(NamedTypeSymbol type, CoreTypes core) =>
        core.IsNumeric(type) || type.Equals(core.Bool) || type.Equals(core.Char) || type.Equals(core.String);

    static void CheckProtocol(
        NamedTypeSymbol type,
        NamedTypeSymbol protocol,
        DiagnosticBag diagnostics,
        bool hasBuiltins,
        HashSet<NamedTypeSymbol>? seen = null
    ) {
        // A protocol that refines another requires everything the other does.
        seen ??= [];
        if (!seen.Add(protocol)) {
            return;
        }

        // 'struct Ones: Source<Int>' conforms to Source with T as Int: its
        // requirements are the definition's, read with the arguments.
        var arguments = TypeSubstitution.For(protocol);
        protocol = protocol.OriginalDefinition;

        foreach (var refined in protocol.BaseTypes) {
            if (arguments.Apply(refined) is NamedTypeSymbol { TypeKind: TypeKind.Protocol } parent) {
                CheckProtocol(type, parent, diagnostics, hasBuiltins, seen);
            }
        }

        foreach (var associated in protocol.AssociatedTypes) {
            if (ResolveAssociated(type, associated) is not null) {
                continue;
            }

            Report(
                diagnostics,
                ErrorCode.ERR_DoesNotConform,
                type,
                $"'{type.Name}' does not conform to '{protocol.Name}': "
                + $"it does not say what '{associated.Name}' is"
            );
        }

        foreach (var requirement in protocol.Members) {
            switch (requirement) {
                case FunctionSymbol { IsAbstract: true } function:
                    CheckFunction(type, protocol, arguments, function, diagnostics, hasBuiltins);
                    break;

                case FieldSymbol { IsAbstract: true } field:
                    CheckProperty(type, protocol, arguments, field, diagnostics, hasBuiltins);
                    break;
            }
        }
    }

    static void CheckFunction(
        NamedTypeSymbol type,
        NamedTypeSymbol protocol,
        TypeSubstitution arguments,
        FunctionSymbol requirement,
        DiagnosticBag diagnostics,
        bool hasBuiltins
    ) {
        // 'fn Printable.render()' implements Printable's requirement only.
        var candidates = FindMembers(type, requirement.Name, hasBuiltins).OfType<FunctionSymbol>()
            .Where(c => c.ExplicitProtocol is null || SameProtocol(c.ExplicitProtocol, protocol))
            .ToList();

        var required = requirement.DeclaredHere($"'{protocol.Name}' requires it here");
        if (candidates.Count == 0) {
            Report(
                diagnostics,
                ErrorCode.ERR_DoesNotConform,
                type,
                $"'{type.Name}' does not conform to '{protocol.Name}': "
                + $"it is missing '{Describe(requirement)}'",
                required,
                requirement
            );
            return;
        }

        foreach (var candidate in candidates) {
            if (!Matches(requirement, candidate, protocol, arguments, type)) {
                continue;
            }

            // A witness has to be visible wherever the conformance is, or the
            // protocol's promise is one the caller cannot collect on.
            if (candidate.DeclaredAccessibility < type.DeclaredAccessibility) {
                Report(
                    diagnostics,
                    ErrorCode.ERR_DoesNotConform,
                    candidate,
                    $"'{type.Name}.{candidate.Name}' is "
                    + $"{candidate.DeclaredAccessibility.ToString().ToLowerInvariant()}, but "
                    + $"'{type.Name}' conforms to '{protocol.Name}' "
                    + $"{type.DeclaredAccessibility.ToString().ToLowerInvariant()}ly"
                );
                return;
            }

            return;
        }

        // A static requirement is not met by an instance member, nor the
        // other way round.
        if (candidates.TrueForAll(c => c.IsStatic != requirement.IsStatic)) {
            Report(
                diagnostics,
                ErrorCode.ERR_RequirementSignatureMismatch,
                type,
                $"'{type.Name}.{requirement.Name}' is "
                + $"{(requirement.IsStatic ? "an instance" : "a static")} member, but "
                + $"'{protocol.Name}' requires "
                + $"{(requirement.IsStatic ? "a static" : "an instance")} one",
                required
            );
            return;
        }

        Report(
            diagnostics,
            ErrorCode.ERR_RequirementSignatureMismatch,
            type,
            $"'{type.Name}.{requirement.Name}' does not match the signature "
            + $"'{protocol.Name}' requires: '{Describe(requirement)}'",
            required
        );
    }

    static void CheckProperty(
        NamedTypeSymbol type,
        NamedTypeSymbol protocol,
        TypeSubstitution arguments,
        FieldSymbol requirement,
        DiagnosticBag diagnostics,
        bool hasBuiltins
    ) {
        var candidate = FindMembers(type, requirement.Name, hasBuiltins).OfType<FieldSymbol>().FirstOrDefault();
        var required = requirement.DeclaredHere($"'{protocol.Name}' requires it here");

        if (candidate is null) {
            Report(
                diagnostics,
                ErrorCode.ERR_DoesNotConform,
                type,
                $"'{type.Name}' does not conform to '{protocol.Name}': "
                + $"it is missing '{requirement.Name}: "
                + $"{requirement.Type.ToDisplayString()}'",
                required,
                requirement
            );
            return;
        }

        // A '{ get set }' requirement is not met by a read-only member.
        var requiresSetter = requirement.IsComputed ? requirement.HasSetter : requirement.IsMutable;
        var hasSetter = candidate.IsComputed ? candidate.HasSetter : candidate.IsMutable;

        if (requiresSetter && !hasSetter) {
            Report(
                diagnostics,
                ErrorCode.ERR_RequirementSignatureMismatch,
                type,
                $"'{type.Name}.{requirement.Name}' cannot be written, but "
                + $"'{protocol.Name}' requires '{requirement.Name}' to have a setter",
                required
            );
            return;
        }

        var expected = arguments.Apply(SubstituteSelf(requirement.Type, protocol, type));
        if (candidate.Type.Equals(expected) || candidate.Type.IsError || expected.IsError) {
            return;
        }

        Report(
            diagnostics,
            ErrorCode.ERR_RequirementSignatureMismatch,
            type,
            $"'{type.Name}.{requirement.Name}' is "
            + $"'{candidate.Type.ToDisplayString()}' but '{protocol.Name}' requires "
            + $"'{expected.ToDisplayString()}'",
            required
        );
    }

    static bool Matches(
        FunctionSymbol requirement,
        FunctionSymbol candidate,
        NamedTypeSymbol protocol,
        TypeSubstitution arguments,
        NamedTypeSymbol conformer
    ) {
        if (candidate.IsStatic != requirement.IsStatic) {
            return false;
        }

        if (candidate.IsAsync != requirement.IsAsync) {
            return false;
        }

        if (candidate.Parameters.Length != requirement.Parameters.Length) {
            return false;
        }

        if (candidate.TypeParameters.Length != requirement.TypeParameters.Length) {
            return false;
        }

        // A generic requirement is written with its own type parameters, and
        // the witness with its own. They stand for the same things, so the
        // requirement is read with the witness's names before comparing.
        var renamed = new Dictionary<TypeParameterSymbol, TypeSymbol>();
        for (var i = 0; i < requirement.TypeParameters.Length; i++) {
            renamed[requirement.TypeParameters[i]] = candidate.TypeParameters[i];
        }

        var rename = new TypeSubstitution(renamed);

        TypeSymbol Expected(TypeSymbol type) => rename.Apply(arguments.Apply(SubstituteSelf(type, protocol, conformer)));

        // A default from a protocol's extension writes Self for the
        // conformer too: 'static fn make(seed: Int) -> Self' meets the
        // requirement of that signature for whichever type conforms.
        TypeSymbol Actual(TypeSymbol type) =>
            candidate.ContainingSymbol is ExtensionSymbol { ExtendedType: { TypeKind: TypeKind.Protocol } owner }
                ? SubstituteSelf(type, owner, conformer)
                : type;

        for (var i = 0; i < requirement.Parameters.Length; i++) {
            var expected = Expected(requirement.Parameters[i].Type);
            var actual = Actual(candidate.Parameters[i].Type);
            if (!expected.Equals(actual) && !expected.IsError && !actual.IsError) {
                return false;
            }
        }

        // The witness may promise less than the requirement allows, never
        // more: a requirement that cannot throw is not met by one that can.
        if (candidate.ThrownType is { } thrown) {
            if (requirement.ThrownType is not { } allowed) {
                return false;
            }

            if (!Expected(allowed).Equals(Actual(thrown)) && !thrown.IsError) {
                return false;
            }
        }

        var expectedReturn = Expected(requirement.ReturnType);
        return expectedReturn.Equals(Actual(candidate.ReturnType))
            || expectedReturn.IsError
            || candidate.ReturnType.IsError;
    }

    /// <summary>
    ///     A protocol writes <c>Self</c> for the conforming type, and binding
    ///     resolved it to the protocol; swap it back.
    /// </summary>
    /// <summary>
    ///     A requirement's type as the conformer sees it: <c>Self</c> becomes the
    ///     conforming type and each associated type becomes what it bound.
    /// </summary>
    static TypeSymbol SubstituteSelf(
        TypeSymbol type,
        NamedTypeSymbol protocol,
        NamedTypeSymbol conformer
    ) {
        // 'Self' is the conforming type as it names itself, which for a
        // generic conformer means with its own parameters applied.
        var asItself = conformer.TypeParameters.IsEmpty
            ? conformer
            : conformer.Construct([..conformer.TypeParameters.Cast<TypeSymbol>()]);

        if (type.Equals(protocol)) {
            return asItself;
        }

        if (protocol.SelfTypeParameter is { } self && ReferenceEquals(type, self)) {
            return asItself;
        }

        if (type is TypeParameterSymbol parameter
            && protocol.AssociatedTypes.Contains(parameter)) {
            return ResolveAssociated(conformer, parameter) ?? type;
        }

        return type switch {
            OptionalTypeSymbol optional =>
                new OptionalTypeSymbol(SubstituteSelf(optional.UnderlyingType, protocol, conformer)),

            ArrayTypeSymbol array =>
                new ArrayTypeSymbol(SubstituteSelf(array.ElementType, protocol, conformer), array.ArrayKind),

            // 'Decoded<Self>': Self as a type argument, or deeper.
            NamedTypeSymbol { IsConstructed: true } constructed when protocol.SelfTypeParameter is { } selfParameter =>
                new TypeSubstitution(new() { [selfParameter] = asItself }).Apply(constructed),

            _ => type,
        };
    }

    /// <summary>
    ///     Members of a type or anything it inherits from that could implement a
    ///     requirement.
    /// </summary>
    /// <remarks>
    ///     A protocol's own requirement is skipped — it is the thing being asked
    ///     about, not an answer to it — while a protocol's default implementation
    ///     counts, because that is what a default is for — unless it is a
    ///     builtin family, which is only a default for a type with builtins.
    /// </remarks>
    static IEnumerable<Symbol> FindMembers(NamedTypeSymbol type, string name, bool hasBuiltins = true) {
        var seen = new HashSet<NamedTypeSymbol>();
        var queue = new Queue<NamedTypeSymbol>();
        queue.Enqueue(type);

        while (queue.Count > 0) {
            var current = queue.Dequeue();
            if (!seen.Add(current)) {
                continue;
            }

            foreach (var member in current.GetMembers(name)) {
                if (member is FunctionSymbol { IsAbstract: true }) {
                    continue;
                }

                if (member is FieldSymbol { IsAbstract: true }) {
                    continue;
                }

                if (!hasBuiltins && member is FunctionSymbol { Builtin: { } function } && Compilation.IsBuiltinFamily(function)
                    || !hasBuiltins && member is FieldSymbol { Builtin: { } field } && Compilation.IsBuiltinFamily(field)) {
                    continue;
                }

                yield return member;
            }

            foreach (var baseType in current.BaseTypes) {
                if (baseType is NamedTypeSymbol named) {
                    queue.Enqueue(named);
                }
            }
        }
    }

    static bool InheritsItself(NamedTypeSymbol type) {
        var seen = new HashSet<NamedTypeSymbol>();
        var queue = new Queue<NamedTypeSymbol>();

        foreach (var baseType in type.BaseTypes) {
            if (baseType is NamedTypeSymbol named) {
                queue.Enqueue(named);
            }
        }

        while (queue.Count > 0) {
            var current = queue.Dequeue();
            if (ReferenceEquals(current.OriginalDefinition, type.OriginalDefinition)) {
                return true;
            }

            if (!seen.Add(current)) {
                continue;
            }

            foreach (var baseType in current.BaseTypes) {
                if (baseType is NamedTypeSymbol named) {
                    queue.Enqueue(named);
                }
            }
        }

        return false;
    }

    static string Describe(FunctionSymbol function) {
        var parameters = string.Join(
            ", ",
            function.Parameters.Select(static p => $"{p.Name}: {p.Type.ToDisplayString()}")
        );

        return function.FunctionKind == FunctionKind.Initializer
            ? $"init({parameters})"
            : $"fn {function.Name}({parameters}) -> {function.ReturnType.ToDisplayString()}";
    }

    static void Report(
        DiagnosticBag diagnostics,
        ErrorCode code,
        Symbol at,
        string message,
        DiagnosticLocation? related = null,
        Symbol? missing = null
    ) {
        // A conformance failure belongs to the type, but an override mistake
        // belongs to the member that made it. A missing requirement can be
        // written in, as a stub.
        var diagnostic = Diagnostic.Error(code, at.Location ?? default, message).WithRelated(related);
        if (missing is not null && at is NamedTypeSymbol type) {
            diagnostic = QuickFixes.Stub(diagnostic, type, missing);
        }

        diagnostics.Add(diagnostic);
    }
}
