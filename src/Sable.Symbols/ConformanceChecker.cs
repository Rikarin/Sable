using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
/// Checks that a type does what its base list says it does.
/// </summary>
/// <remarks>
/// <para>Conformance is nominal and explicit, per docs/03-type-system.md
/// §3.2: satisfying a protocol's members by accident is not enough, and a
/// declared conformance that is not met is an error naming what is
/// missing.</para>
/// <para>A protocol member with a body is a default implementation and is not
/// a requirement. A member with none is, and the conforming type — or
/// something it inherits from — has to supply it.</para>
/// </remarks>
internal static class ConformanceChecker
{
    public static void Check(NamedTypeSymbol type, DiagnosticBag diagnostics, CoreTypes core)
    {
        if (type.TypeKind == TypeKind.Protocol) return;

        var classBases = 0;

        foreach (var baseType in type.BaseTypes)
        {
            if (baseType.IsError) continue;

            if (baseType is not NamedTypeSymbol named)
            {
                Report(diagnostics, ErrorCode.ERR_NotAProtocol, type,
                    $"'{baseType.ToDisplayString()}' is not a type "
                    + $"'{type.Name}' can inherit from or conform to");
                continue;
            }

            // An enum's base list names its underlying representation as well
            // as any protocols it conforms to.
            if (type.TypeKind == TypeKind.Enum && core.IsInteger(named)) continue;

            switch (named.TypeKind)
            {
                case TypeKind.Protocol:
                    CheckProtocol(type, named, diagnostics);
                    break;

                case TypeKind.Class when type.TypeKind is TypeKind.Class or TypeKind.Actor:
                    classBases++;
                    break;

                default:
                    Report(diagnostics, ErrorCode.ERR_NotAProtocol, type,
                        $"'{type.Name}' cannot inherit from "
                        + $"'{named.ToDisplayString()}'; only a protocol or a class");
                    break;
            }
        }

        if (classBases > 1)
        {
            Report(diagnostics, ErrorCode.ERR_MultipleBaseClasses, type,
                $"'{type.Name}' lists more than one base class; inheritance is single");
        }

        if (InheritsItself(type))
        {
            Report(diagnostics, ErrorCode.ERR_CircularInheritance, type,
                $"'{type.Name}' inherits from itself");
            return;
        }

        CheckInheritance(type, diagnostics);
        CheckOverrides(type, diagnostics);
    }

    /// <summary>What a base class allows, per spec/SYNTAX.md §2.</summary>
    private static void CheckInheritance(NamedTypeSymbol type, DiagnosticBag diagnostics)
    {
        foreach (var baseType in type.BaseTypes)
        {
            if (baseType is not NamedTypeSymbol { TypeKind: TypeKind.Class } baseClass) continue;

            if (baseClass.IsSealed)
            {
                Report(diagnostics, ErrorCode.ERR_NotOpen, type,
                    $"'{baseClass.Name}' is sealed, so '{type.Name}' cannot inherit from it");
                continue;
            }

            // 'open' is required to subclass across modules; inside one, a
            // class is open to its own module.
            if (!baseClass.IsOpen
                && !ReferenceEquals(baseClass.ContainingModule, type.ContainingModule))
            {
                Report(diagnostics, ErrorCode.ERR_NotOpen, type,
                    $"'{baseClass.Name}' is not 'open', so it cannot be subclassed "
                    + "outside the module that declares it");
            }
        }
    }

    /// <summary>
    /// <c>override</c> is required where a member replaces a base one, and
    /// forbidden where it replaces nothing.
    /// </summary>
    private static void CheckOverrides(NamedTypeSymbol type, DiagnosticBag diagnostics)
    {
        foreach (var member in type.Members)
        {
            if (member is not FunctionSymbol
                { FunctionKind: FunctionKind.Ordinary } function)
            {
                continue;
            }

            var overridden = FindOverridden(type, function);

            if (overridden is null)
            {
                if (!function.IsOverride) continue;

                Report(diagnostics, ErrorCode.ERR_NothingToOverride, function,
                    $"'{type.Name}.{function.Name}' is marked 'override' but no base class "
                    + "declares it");
                continue;
            }

            if (!function.IsOverride)
            {
                Report(diagnostics, ErrorCode.ERR_MissingOverride, function,
                    $"'{type.Name}.{function.Name}' replaces a member of "
                    + $"'{overridden.ContainingSymbol?.Name}', so it needs 'override'");
                continue;
            }

            if (!overridden.IsOpen
                && !ReferenceEquals(overridden.ContainingModule, type.ContainingModule))
            {
                Report(diagnostics, ErrorCode.ERR_NotOpen, function,
                    $"'{overridden.ContainingSymbol?.Name}.{overridden.Name}' is not 'open', "
                    + "so it cannot be overridden outside the module that declares it");
            }
        }
    }

    /// <summary>The base-class member a function replaces, if any.</summary>
    private static FunctionSymbol? FindOverridden(
        NamedTypeSymbol type, FunctionSymbol function)
    {
        foreach (var baseType in type.BaseTypes)
        {
            if (baseType is not NamedTypeSymbol { TypeKind: TypeKind.Class } baseClass) continue;

            foreach (var candidate in baseClass.GetMembers(function.Name))
            {
                if (candidate is not FunctionSymbol other) continue;
                if (other.Parameters.Length != function.Parameters.Length) continue;

                var matches = true;
                for (var i = 0; i < other.Parameters.Length; i++)
                {
                    if (other.Parameters[i].Type.Equals(function.Parameters[i].Type)) continue;
                    matches = false;
                    break;
                }

                if (matches) return other;
            }

            if (FindOverridden(baseClass, function) is { } inherited) return inherited;
        }

        return null;
    }

    /// <summary>
    /// What a type binds a protocol's associated types to.
    /// </summary>
    /// <remarks>
    /// A conformer names them: <c>alias Element = Int</c>, or a nested type
    /// called Element. Failing that the declaration's own default applies, and
    /// failing that the conformance is not met.
    /// </remarks>
    public static TypeSymbol? ResolveAssociated(
        NamedTypeSymbol conformer, TypeParameterSymbol associated)
    {
        foreach (var member in conformer.GetMembers(associated.Name))
        {
            switch (member)
            {
                case AliasSymbol alias: return alias.AliasedType;
                case NamedTypeSymbol nested: return nested;
            }
        }

        foreach (var baseType in conformer.BaseTypes)
        {
            if (baseType is NamedTypeSymbol { TypeKind: not TypeKind.Protocol } baseClass
                && ResolveAssociated(baseClass, associated) is { } inherited)
            {
                return inherited;
            }
        }

        return associated.DefaultType;
    }

    private static void CheckProtocol(
        NamedTypeSymbol type,
        NamedTypeSymbol protocol,
        DiagnosticBag diagnostics,
        HashSet<NamedTypeSymbol>? seen = null)
    {
        // A protocol that refines another requires everything the other does.
        seen ??= [];
        if (!seen.Add(protocol)) return;

        foreach (var refined in protocol.BaseTypes)
        {
            if (refined is NamedTypeSymbol { TypeKind: TypeKind.Protocol } parent)
                CheckProtocol(type, parent, diagnostics, seen);
        }

        foreach (var associated in protocol.AssociatedTypes)
        {
            if (ResolveAssociated(type, associated) is not null) continue;

            Report(diagnostics, ErrorCode.ERR_DoesNotConform, type,
                $"'{type.Name}' does not conform to '{protocol.Name}': "
                + $"it does not say what '{associated.Name}' is");
        }

        foreach (var requirement in protocol.Members)
        {
            switch (requirement)
            {
                case FunctionSymbol { IsAbstract: true } function:
                    CheckFunction(type, protocol, function, diagnostics);
                    break;

                case FieldSymbol { IsAbstract: true } field:
                    CheckProperty(type, protocol, field, diagnostics);
                    break;
            }
        }
    }

    private static void CheckFunction(
        NamedTypeSymbol type,
        NamedTypeSymbol protocol,
        FunctionSymbol requirement,
        DiagnosticBag diagnostics)
    {
        var candidates = FindMembers(type, requirement.Name).OfType<FunctionSymbol>().ToList();

        if (candidates.Count == 0)
        {
            Report(diagnostics, ErrorCode.ERR_DoesNotConform, type,
                $"'{type.Name}' does not conform to '{protocol.Name}': "
                + $"it is missing '{Describe(requirement)}'");
            return;
        }

        foreach (var candidate in candidates)
        {
            if (!Matches(requirement, candidate, protocol, type)) continue;

            // A witness has to be visible wherever the conformance is, or the
            // protocol's promise is one the caller cannot collect on.
            if (candidate.DeclaredAccessibility < type.DeclaredAccessibility)
            {
                Report(diagnostics, ErrorCode.ERR_DoesNotConform, candidate,
                    $"'{type.Name}.{candidate.Name}' is "
                    + $"{candidate.DeclaredAccessibility.ToString().ToLowerInvariant()}, but "
                    + $"'{type.Name}' conforms to '{protocol.Name}' "
                    + $"{type.DeclaredAccessibility.ToString().ToLowerInvariant()}ly");
                return;
            }

            return;
        }

        // A static requirement is not met by an instance member, nor the
        // other way round.
        if (candidates.TrueForAll(c => c.IsStatic != requirement.IsStatic))
        {
            Report(diagnostics, ErrorCode.ERR_RequirementSignatureMismatch, type,
                $"'{type.Name}.{requirement.Name}' is "
                + $"{(requirement.IsStatic ? "an instance" : "a static")} member, but "
                + $"'{protocol.Name}' requires "
                + $"{(requirement.IsStatic ? "a static" : "an instance")} one");
            return;
        }

        Report(diagnostics, ErrorCode.ERR_RequirementSignatureMismatch, type,
            $"'{type.Name}.{requirement.Name}' does not match the signature "
            + $"'{protocol.Name}' requires: '{Describe(requirement)}'");
    }

    private static void CheckProperty(
        NamedTypeSymbol type,
        NamedTypeSymbol protocol,
        FieldSymbol requirement,
        DiagnosticBag diagnostics)
    {
        var candidate = FindMembers(type, requirement.Name).OfType<FieldSymbol>().FirstOrDefault();

        if (candidate is null)
        {
            Report(diagnostics, ErrorCode.ERR_DoesNotConform, type,
                $"'{type.Name}' does not conform to '{protocol.Name}': "
                + $"it is missing '{requirement.Name}: "
                + $"{requirement.Type.ToDisplayString()}'");
            return;
        }

        // A '{ get set }' requirement is not met by a read-only member.
        var requiresSetter = requirement.IsComputed ? requirement.HasSetter : requirement.IsMutable;
        var hasSetter = candidate.IsComputed ? candidate.HasSetter : candidate.IsMutable;

        if (requiresSetter && !hasSetter)
        {
            Report(diagnostics, ErrorCode.ERR_RequirementSignatureMismatch, type,
                $"'{type.Name}.{requirement.Name}' cannot be written, but "
                + $"'{protocol.Name}' requires '{requirement.Name}' to have a setter");
            return;
        }

        var expected = SubstituteSelf(requirement.Type, protocol, type);
        if (candidate.Type.Equals(expected) || candidate.Type.IsError || expected.IsError) return;

        Report(diagnostics, ErrorCode.ERR_RequirementSignatureMismatch, type,
            $"'{type.Name}.{requirement.Name}' is "
            + $"'{candidate.Type.ToDisplayString()}' but '{protocol.Name}' requires "
            + $"'{expected.ToDisplayString()}'");
    }

    private static bool Matches(
        FunctionSymbol requirement,
        FunctionSymbol candidate,
        NamedTypeSymbol protocol,
        NamedTypeSymbol conformer)
    {
        if (candidate.IsStatic != requirement.IsStatic) return false;
        if (candidate.Parameters.Length != requirement.Parameters.Length) return false;

        for (var i = 0; i < requirement.Parameters.Length; i++)
        {
            var expected = SubstituteSelf(requirement.Parameters[i].Type, protocol, conformer);
            var actual = candidate.Parameters[i].Type;
            if (!expected.Equals(actual) && !expected.IsError && !actual.IsError) return false;
        }

        var expectedReturn = SubstituteSelf(requirement.ReturnType, protocol, conformer);
        return expectedReturn.Equals(candidate.ReturnType)
               || expectedReturn.IsError
               || candidate.ReturnType.IsError;
    }

    /// <summary>
    /// A protocol writes <c>Self</c> for the conforming type, and binding
    /// resolved it to the protocol; swap it back.
    /// </summary>
    /// <summary>
    /// A requirement's type as the conformer sees it: <c>Self</c> becomes the
    /// conforming type and each associated type becomes what it bound.
    /// </summary>
    private static TypeSymbol SubstituteSelf(
        TypeSymbol type, NamedTypeSymbol protocol, NamedTypeSymbol conformer)
    {
        // 'Self' is the conforming type as it names itself, which for a
        // generic conformer means with its own parameters applied.
        var asItself = conformer.TypeParameters.IsEmpty
            ? conformer
            : conformer.Construct([.. conformer.TypeParameters.Cast<TypeSymbol>()]);

        if (type.Equals(protocol)) return asItself;
        if (protocol.SelfTypeParameter is { } self && ReferenceEquals(type, self))
            return asItself;

        if (type is TypeParameterSymbol parameter
            && protocol.AssociatedTypes.Contains(parameter))
        {
            return ResolveAssociated(conformer, parameter) ?? type;
        }

        return type switch
        {
            OptionalTypeSymbol optional =>
                new OptionalTypeSymbol(
                    SubstituteSelf(optional.UnderlyingType, protocol, conformer)),

            ArrayTypeSymbol array =>
                new ArrayTypeSymbol(
                    SubstituteSelf(array.ElementType, protocol, conformer), array.ArrayKind),

            _ => type,
        };
    }

    /// <summary>
    /// Members of a type or anything it inherits from that could implement a
    /// requirement.
    /// </summary>
    /// <remarks>
    /// A protocol's own requirement is skipped — it is the thing being asked
    /// about, not an answer to it — while a protocol's default implementation
    /// counts, because that is what a default is for.
    /// </remarks>
    private static IEnumerable<Symbol> FindMembers(NamedTypeSymbol type, string name)
    {
        var seen = new HashSet<NamedTypeSymbol>();
        var queue = new Queue<NamedTypeSymbol>();
        queue.Enqueue(type);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current)) continue;

            foreach (var member in current.GetMembers(name))
            {
                if (member is FunctionSymbol { IsAbstract: true }) continue;
                if (member is FieldSymbol { IsAbstract: true }) continue;
                yield return member;
            }

            foreach (var baseType in current.BaseTypes)
                if (baseType is NamedTypeSymbol named) queue.Enqueue(named);
        }
    }

    private static bool InheritsItself(NamedTypeSymbol type)
    {
        var seen = new HashSet<NamedTypeSymbol>();
        var queue = new Queue<NamedTypeSymbol>();

        foreach (var baseType in type.BaseTypes)
            if (baseType is NamedTypeSymbol named) queue.Enqueue(named);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (ReferenceEquals(current.OriginalDefinition, type.OriginalDefinition)) return true;
            if (!seen.Add(current)) continue;

            foreach (var baseType in current.BaseTypes)
                if (baseType is NamedTypeSymbol named) queue.Enqueue(named);
        }

        return false;
    }

    private static string Describe(FunctionSymbol function)
    {
        var parameters = string.Join(", ",
            function.Parameters.Select(p => $"{p.Name}: {p.Type.ToDisplayString()}"));

        return $"fn {function.Name}({parameters}) -> {function.ReturnType.ToDisplayString()}";
    }

    private static void Report(
        DiagnosticBag diagnostics, ErrorCode code, Symbol at, string message)
    {
        // A conformance failure belongs to the type, but an override mistake
        // belongs to the member that made it.
        diagnostics.Error(code, at.Location ?? default, message);
    }
}
