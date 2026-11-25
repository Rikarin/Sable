namespace Sable.Symbols;

/// <summary>
///     What <c>@reflectable(callable)</c> makes (docs/06 §6.3): Sable.Core's
///     <c>Callable</c>, whose <c>describeCallables()</c> gives each public
///     initialiser, method and operator the type declares, and whose
///     <c>callMember</c> and <c>callMemberAsync</c> call the one of an index:
///     a match on it, each arm taking the arguments out of their boxes as the
///     types the member has and calling it as code written out would.
/// </summary>
internal sealed partial class DerivedBodies {
    /// <summary>A member, whether a call of it waits, and why it cannot be called, if it cannot.</summary>
    readonly record struct CallableMember(FunctionSymbol Function, bool Waits, string? Refusal);

    NamedTypeSymbol CallableHelpers => compilation.GetModule("Sable.Core")!.GetType("CallableMember")!;

    List<BoundStatement> Callable(DerivedMember member, NamedTypeSymbol type, NamedTypeSymbol self) {
        var members = CallableMembers(type, self);
        if (member == DerivedMember.DescribeCallables) {
            return [new BoundReturn(_at, new BoundArray(_at, _function.ReturnType, [..members.Select(m => Describe(m, self))]))];
        }

        var waits = member == DerivedMember.CallMemberAsync;
        var arms = new List<BoundMatchArm>();
        for (var i = 0; i < members.Count; i++) {
            if (members[i] is { Refusal: null } each && each.Waits == waits) {
                arms.Add(new BoundMatchArm(_at, [new BoundLiteralPattern(_at, core.Int, (long)i, false)], null, CallArm(each, self)));
            }
        }

        var missing = new BoundReturn(_at, Try(Helper("missing", [], [])));
        if (arms.Count == 0) {
            return [missing];
        }

        arms.Add(new BoundMatchArm(_at, [], null, new BoundBlock(_at, [], [missing])));
        return [Statement(new BoundMatch(_at, core.Void, new BoundParameterAccess(_at, _function.Parameters[0]), [..arms]))];
    }

    /// <summary>The public initialisers, methods and operators a type and its extensions declare, in the order declared.</summary>
    List<CallableMember> CallableMembers(NamedTypeSymbol type, NamedTypeSymbol self) => [
        ..type.Members.OfType<FunctionSymbol>()
            .Where(f => f.FunctionKind is FunctionKind.Ordinary or FunctionKind.Initializer or FunctionKind.Operator
                && f.DeclaredAccessibility == Accessibility.Public
                && compilation.GetDerivation(f) is not { Member: DerivedMember.DescribeCallables or DerivedMember.CallMember or DerivedMember.CallMemberAsync })
            .Select(f => new CallableMember(f, f.IsAsync || Isolated(f, type), Refusal(f, type, self))),
    ];

    /// <summary>Whether a call from outside its actor hops: what is isolated to an actor, or to the main one.</summary>
    static bool Isolated(FunctionSymbol function, NamedTypeSymbol type) =>
        function is { IsStatic: false, FunctionKind: FunctionKind.Ordinary, IsNonisolated: false }
        && (type.TypeKind == TypeKind.Actor || function.IsOnMainActor);

    /// <summary>Why a member cannot be called with boxed arguments, if it cannot.</summary>
    string? Refusal(FunctionSymbol function, NamedTypeSymbol type, NamedTypeSymbol self) {
        var members = TypeSubstitution.For(self);
        if (!function.TypeParameters.IsEmpty) {
            return "it is generic over type parameters of its own";
        }

        if (function.ContainingSymbol is ExtensionSymbol { IsConstrained: true }) {
            return "its extension holds only of some instances of the type";
        }

        if (function.FunctionKind == FunctionKind.Initializer && type.IsAbstract) {
            return "its class is abstract";
        }

        if (function.FunctionKind != FunctionKind.Initializer && Unboxable(members.Apply(function.ReturnType))) {
            return $"its result is of type '{function.ReturnType.ToDisplayString()}', which 'any Any' does not hold";
        }

        foreach (var parameter in function.Parameters) {
            if (parameter.Modifier is ParameterModifier.InOut or ParameterModifier.Out) {
                return $"it changes its argument '{parameter.Name}'";
            }

            if (parameter.IsVariadic) {
                return $"its parameter '{parameter.Name}' is variadic";
            }

            if (Unboxable(members.Apply(parameter.Type))) {
                return $"its parameter '{parameter.Name}' is of type '{parameter.Type.ToDisplayString()}', which 'any Any' does not hold";
            }
        }

        // What crosses into an actor, and back out: only what its type says may.
        if (Isolated(function, type)) {
            foreach (var (crossing, what) in function.Parameters.Select(static p => (p.Type, $"its parameter '{p.Name}'")).Append((function.ReturnType, "its result"))) {
                if (!crossing.Equals(core.Void) && !Sendability.IsSendable(members.Apply(crossing))) {
                    return $"{what} is of type '{crossing.ToDisplayString()}', which is not Sendable, and it is isolated to {(function.IsOnMainActor ? "the main actor" : "its actor")}";
                }
            }
        }

        return null;
    }

    /// <summary>What a box does not hold: a value that is moved, not copied, a view, a reference, a function, a hidden type.</summary>
    static bool Unboxable(TypeSymbol type) =>
        Copyability.IsNoncopyable(type)
        || type is OpaqueTypeSymbol or ReferenceTypeSymbol or FunctionTypeSymbol or MetatypeTypeSymbol or ErrorTypeSymbol or ArrayTypeSymbol { ArrayKind: ArrayKind.Slice }
        || type is OptionalTypeSymbol { UnderlyingType: var wrapped } && Unboxable(wrapped);

    /// <summary><c>CallableMember.described("move", "mutating fn move(by d: Int)", ["d"], [Int.self], Void.self, flags, "")</c>.</summary>
    BoundExpression Describe(CallableMember member, NamedTypeSymbol self) {
        var function = member.Function;
        var members = TypeSubstitution.For(self);
        BoundExpression TypeValue(TypeSymbol type) => new BoundConversion(_at, core.Type, new BoundMetatype(_at, members.Apply(type)), ConversionKind.Identity);

        var isInitializer = function.FunctionKind == FunctionKind.Initializer;
        var flags = (function.IsStatic || function.FunctionKind == FunctionKind.Operator ? 1 : 0)
            | (function.IsMutating ? 2 : 0)
            | (member.Waits ? 4 : 0)
            | (function.ThrownType is null ? 0 : 8)
            | (isInitializer ? 16 : 0)
            | (function.FunctionKind == FunctionKind.Operator ? 32 : 0);
        var callable = member.Refusal is null;
        return Helper("described", [], [
            Literal(isInitializer ? "init" : function.Name),
            Literal(CallableSignature(function, members)),
            new BoundArray(_at, new ArrayTypeSymbol(core.String, ArrayKind.Grow), [..function.Parameters.Select(p => Literal(p.Name))]),
            new BoundArray(_at, new ArrayTypeSymbol(core.Type, ArrayKind.Grow), [..callable ? function.Parameters.Select(p => TypeValue(p.Type)) : []]),
            TypeValue(isInitializer ? self : callable ? function.ReturnType : core.Void),
            Literal((long)flags),
            Literal(member.Refusal ?? ""),
        ]);
    }

    /// <summary>A member as it would be declared: 'mutating fn move(by dx: Int) throws'.</summary>
    static string CallableSignature(FunctionSymbol function, TypeSubstitution members) {
        var parameters = string.Join(", ", function.Parameters.Select(p => {
            var label = p.Label is { } l && l != p.Name ? l + " " : "";
            var type = members.Apply(p.Type);
            var how = p.Modifier switch { ParameterModifier.InOut => "inout ", ParameterModifier.Out => "out ", ParameterModifier.Consuming => "consuming ", _ => "" };
            return $"{label}{p.Name}: {how}{(p.IsVariadic && type is ArrayTypeSymbol { ElementType: var element } ? element.ToDisplayString() + "..." : type.ToDisplayString())}";
        }));
        var head = function.FunctionKind switch {
            FunctionKind.Initializer => "init",
            FunctionKind.Operator => $"operator {function.Name} ",
            _ => $"fn {function.Name}",
        };
        var result = function.FunctionKind == FunctionKind.Initializer || function.ReturnType.ToDisplayString() == "Void"
            ? ""
            : $" -> {members.Apply(function.ReturnType).ToDisplayString()}";
        return (function is { IsStatic: true, FunctionKind: FunctionKind.Ordinary } ? "static " : "") + (function.IsMutating ? "mutating " : "")
            + $"{head}({parameters}){(function.IsAsync ? " async" : "")}{(function.ThrownType is null ? "" : " throws")}{result}";
    }

    /// <summary>
    ///     <c>return CallableMember.boxed(try CallableMember.receiver(receiver, S.self).move(try CallableMember.argument(arguments, 0, Int.self)))</c>;
    ///     a mutating one's receiver is a local of its own, put back.
    /// </summary>
    BoundBlock CallArm(CallableMember member, NamedTypeSymbol self) {
        var function = member.Function;
        var members = TypeSubstitution.For(self);
        var boxed = _function.Parameters[1];
        var statements = new List<BoundStatement>();
        var locals = new List<LocalSymbol>();

        LocalSymbol Keep(string name, TypeSymbol of, BoundExpression value, bool isMutable = false) {
            var local = new LocalSymbol(name, _function, null, isMutable) { TypeResolver = () => of };
            locals.Add(local);
            statements.Add(new BoundLocalDeclaration(_at, local, value));
            return local;
        }

        LocalSymbol? receiver = null;
        if (function is { IsStatic: false, FunctionKind: FunctionKind.Ordinary }) {
            receiver = Keep("receiver", self, Try(Helper("receiver", [self], [new BoundParameterAccess(_at, boxed), new BoundMetatype(_at, self)])), function.IsMutating);
        }

        var arguments = function.Parameters
            .Select((p, i) => {
                var of = members.Apply(p.Type);
                return Keep($"argument{i}", of, Try(Helper("argument", [of], [new BoundParameterAccess(_at, _function.Parameters[2]), Literal((long)i), new BoundMetatype(_at, of)])));
            })
            .ToList();

        var result = function.FunctionKind == FunctionKind.Initializer ? self : members.Apply(function.ReturnType);
        BoundExpression target = receiver is null ? new BoundTypeExpression(_at, self) : Local(receiver);
        BoundExpression call = new BoundCall(_at, target, function, [..arguments.Select(Local)], result) { Substitution = members };
        if (member.Waits) {
            call = new BoundUnaryOperator(_at, result, UnaryOperatorKind.Await, call);
        }

        if (function.ThrownType is not null) {
            call = Try(call);
        }

        BoundExpression value;
        if (result.Equals(core.Void)) {
            statements.Add(Statement(call));
            value = Helper("nothing", [], []);
        } else {
            value = Helper("boxed", [result], [Local(Keep("result", result, call))]);
        }

        if (receiver is not null && function.IsMutating) {
            statements.Add(Statement(new BoundAssignment(_at, new BoundParameterAccess(_at, boxed), Helper("boxed", [self], [Local(receiver)]))));
        }

        statements.Add(new BoundReturn(_at, value));
        return new BoundBlock(_at, [..locals], [..statements]);
    }

    /// <summary>A call to one of CallableMember's static functions.</summary>
    BoundExpression Helper(string name, TypeSymbol[] typeArguments, BoundExpression[] arguments) {
        var function = MemberOf(CallableHelpers, name);
        var substitution = new TypeSubstitution(function.TypeParameters.Zip(typeArguments).ToDictionary(static p => p.First, static p => p.Second));
        return new BoundCall(_at, new BoundTypeExpression(_at, CallableHelpers), function, [..arguments], substitution.Apply(function.ReturnType)) { Substitution = substitution };
    }
}
