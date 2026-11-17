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
        }
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
        NamedTypeSymbol type, NamedTypeSymbol protocol, DiagnosticBag diagnostics)
    {
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
            if (Matches(requirement, candidate, protocol, type)) return;

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
        if (type.Equals(protocol)) return conformer;
        if (protocol.SelfTypeParameter is { } self && ReferenceEquals(type, self))
            return conformer;

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
        DiagnosticBag diagnostics, ErrorCode code, NamedTypeSymbol type, string message)
    {
        var span = type.Location ?? default;
        diagnostics.Error(code, span, message);
    }
}
