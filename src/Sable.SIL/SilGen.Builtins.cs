using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     <c>@builtin("name")</c> (Compilation.Builtins.cs): a standard library
///     member whose body is a SIL builtin, lowered where it is used.
/// </summary>
/// <remarks>
///     <para>
///         A call is the builtin, in place: the receiver borrowed first, then
///         each argument in order, the borrows ended once it has run. Nothing
///         is called, so a member written this way costs what the C it once
///         was in <c>CoreTypes</c> cost, and a program that never uses it
///         carries none of it.
///     </para>
///     <para>
///         Each declaration is checked against <see cref="SilBuiltins" />
///         before anything is lowered — its parameters, its receiver and its
///         result must lower to the builtin's operands and result exactly —
///         so that one which is wrong is reported where it is written, used or
///         not, rather than as a verifier failure wherever it happens to be
///         called.
///     </para>
/// </remarks>
public sealed partial class SilGen {
    /// <summary>The <c>@builtin</c> declarations that agree with their builtin; a call to any other fails quietly, its error already given.</summary>
    readonly HashSet<Symbol> _soundBuiltins = [];

    /// <summary>The <c>@builtin</c> members of inline arrays, which only an inline layout lowers.</summary>
    readonly HashSet<Symbol> _inlineArrayBuiltins = [];

    /// <summary>
    ///     Each builtin family (Compilation.Builtins.cs) and the types its
    ///     builtin was found for, agreeing with the declaration: the built-in
    ///     types that conform to the family's protocol.
    /// </summary>
    readonly Dictionary<Symbol, HashSet<SilType>> _builtinFamilies = [];

    /// <summary>Checks every <c>@builtin</c> declaration against the builtin it names.</summary>
    void CheckBuiltins() {
        foreach (var tree in _compilation.SyntaxTrees) {
            foreach (var declaration in Declarations(tree)) {
                switch (declaration) {
                    case FunctionDeclarationSyntax function
                        when _compilation.GetDeclaredSymbol(function) is FunctionSymbol { Builtin: { } name } symbol && Compilation.IsBuiltinFamily(name):
                        CheckFamily(symbol, name, function.Attributes, Operands(symbol), symbol.ReturnType, symbol.ThrownType is not null, symbol.IsMutating);
                        break;

                    case OperatorDeclarationSyntax operatorDeclaration
                        when _compilation.GetDeclaredSymbol(operatorDeclaration) is FunctionSymbol { Builtin: { } name } symbol && Compilation.IsBuiltinFamily(name):
                        CheckFamily(symbol, name, operatorDeclaration.Attributes, Operands(symbol), symbol.ReturnType, false, false);
                        break;

                    case FieldDeclarationSyntax field
                        when _compilation.GetDeclaredSymbol(field) is FieldSymbol { Builtin: { } name } symbol && Compilation.IsBuiltinFamily(name):
                        CheckFamily(symbol, name, field.Attributes, OwnerOfField(symbol) is { } protocol ? [SelfOf(protocol)] : [], symbol.Type, false, false);
                        break;

                    case FunctionDeclarationSyntax function
                        when _compilation.GetDeclaredSymbol(function) is FunctionSymbol { Builtin: { } name } symbol:
                        CheckBuiltin(symbol, name, function.Attributes, Operands(symbol), symbol.ReturnType, symbol.ThrownType is not null,
                            Representative(Owner(symbol)), symbol.IsMutating);
                        break;

                    case OperatorDeclarationSyntax operatorDeclaration
                        when _compilation.GetDeclaredSymbol(operatorDeclaration) is FunctionSymbol { Builtin: { } name } symbol:
                        CheckBuiltin(symbol, name, operatorDeclaration.Attributes, Operands(symbol), symbol.ReturnType, false, TypeSubstitution.Empty, false);
                        break;

                    case FieldDeclarationSyntax field
                        when _compilation.GetDeclaredSymbol(field) is FieldSymbol { Builtin: { } name } symbol:
                        CheckBuiltin(symbol, name, field.Attributes, OwnerOfField(symbol) is { } owner ? [SelfOf(owner)] : [], symbol.Type, false,
                            Representative(OwnerOfField(symbol)), false);
                        break;
                }
            }
        }
    }

    /// <summary>What a builtin function is applied to: its receiver, if it has one, and its parameters.</summary>
    static List<TypeSymbol> Operands(FunctionSymbol function) {
        var operands = new List<TypeSymbol>();
        if (HasSelf(function) && Owner(function) is { } owner) {
            operands.Add(SelfOf(owner));
        }

        operands.AddRange(function.Parameters.Select(static p => p.Type));
        return operands;
    }

    /// <summary>The receiver of a member of the type: the type, or the array an array extension extends.</summary>
    static TypeSymbol SelfOf(NamedTypeSymbol owner) => owner.ExtendedArray ?? owner.ExtendedOptional ?? (TypeSymbol)owner;

    /// <summary>
    ///     An array extension's parameters, each as Int: the array builtins
    ///     work for any element, so a declaration of one is checked for one
    ///     element type, and agrees for every other.
    /// </summary>
    TypeSubstitution Representative(NamedTypeSymbol? owner) =>
        owner is { ExtendedArray: not null, TypeParameters.IsEmpty: false }
            ? new(owner.TypeParameters.ToDictionary(static p => p, TypeSymbol (_) => Core.Int))
            : TypeSubstitution.Empty;

    /// <summary>The type, its parameters replaced: a generic definition, which is what <c>self</c> is in its own extension, constructed with them.</summary>
    static TypeSymbol Represent(TypeSubstitution representative, TypeSymbol type) =>
        type is NamedTypeSymbol { IsGenericDefinition: true } definition && definition.TypeParameters.All(p => representative.Lookup(p) is not null)
            ? definition.Construct([..definition.TypeParameters.Select(p => representative.Lookup(p)!)])
            : representative.Apply(type);

    static NamedTypeSymbol? OwnerOfField(FieldSymbol field) =>
        field.ContainingSymbol switch {
            NamedTypeSymbol type => type,
            ExtensionSymbol extension => extension.ExtendedType,
            _ => null,
        };

    void CheckBuiltin(Symbol symbol, string name, SyntaxList<AttributeSyntax> attributes, List<TypeSymbol> operands, TypeSymbol result, bool throws,
        TypeSubstitution representative, bool mutating) {
        var at = attributes.First(static a => a.Name.ToString().Trim() == "builtin").Span;

        // A SIMD builtin's signature follows the vectors it is given, which
        // are known only where it is used: checked there (ApplyBuiltin).
        if (SimdBuiltins.Is(name)) {
            _soundBuiltins.Add(symbol);
            return;
        }

        var lowered = operands.Select(o => Lower(Represent(representative, o))).ToList();
        var loweredResult = Lower(Represent(representative, result));

        // An inline array's members are lowered where its layout is
        // (SilGen.Inline.cs), never as the builtin; it has no SIL type of its own to check.
        if (operands is [ArrayTypeSymbol { ArrayKind: ArrayKind.Inline }, ..] && lowered[0] is null) {
            _inlineArrayBuiltins.Add(symbol);
            return;
        }

        // A mutating member passes its receiver where it is.
        if (mutating && lowered.Count > 0 && lowered[0] is { } receiver) {
            lowered[0] = SilType.Address(receiver);
        }

        // The array operations' signatures follow the element; the rest are fixed.
        var signature = SilBuiltins.Find(name);
        if (signature is null && SilBuiltins.ArrayOperations.Contains(name) && name is not ("array_empty" or "region_alloc")) {
            if (lowered.All(static o => o is not null) && loweredResult is not null && lowered.Count > 0) {
                // When it does not apply, the receiver passed the other way
                // says what it would be, for the message.
                var other = lowered[0] is AddressType { Pointee: var pointee } ? pointee : SilType.Address(lowered[0]!);
                signature = SilBuiltins.Resolve(name, lowered!, loweredResult)
                    ?? SilBuiltins.Resolve(name, [other, ..lowered.Skip(1)!], loweredResult);
            }

            signature ??= new([], SilType.Unit);
        }

        if (signature is null) {
            Report(ErrorCode.ERR_UnknownBuiltin, at,
                $"there is no builtin '{name}' with a fixed signature: '@builtin' names one of SilBuiltins' table");
            return;
        }

        var modified = symbol is FunctionSymbol { Parameters: var parameters } && parameters.Any(static p => p.Modifier != ParameterModifier.None || p.IsVariadic);
        if (lowered.Any(static o => o is null) || loweredResult is null || modified || throws
            || !lowered.SequenceEqual(signature.Operands) || loweredResult != signature.Result
            || mutating != (signature.Operands is [AddressType, ..])) {
            var declared = $"({string.Join(", ", operands.Select(static o => o.ToDisplayString()))}) -> {result.ToDisplayString()}";
            var expected = $"({string.Join(", ", signature.Operands)}) -> {signature.Result}";
            Report(ErrorCode.ERR_BuiltinSignatureMismatch, at,
                $"'{name}' is {expected}, receiver first, but this declaration is {declared}"
                + (modified ? ", with a parameter passed other than by value" : "")
                + (throws ? ", and throws" : ""));
            return;
        }

        _soundBuiltins.Add(symbol);
    }

    /// <summary>
    ///     A builtin family, checked for each built-in type that conforms to
    ///     its protocol: Self — and the protocol, which is what <c>self</c>
    ///     is in its extension — made that type, the declaration must agree
    ///     with that type's builtin, as any <c>@builtin</c> must with its one.
    ///     A family is a requirement's default, so it must meet one.
    /// </summary>
    void CheckFamily(Symbol symbol, string name, SyntaxList<AttributeSyntax> attributes, List<TypeSymbol> operands, TypeSymbol result, bool throws, bool mutating) {
        var at = attributes.First(static a => a.Name.ToString().Trim() == "builtin").Span;
        var protocol = symbol switch {
            FunctionSymbol function => Owner(function),
            FieldSymbol field => OwnerOfField(field),
            _ => null,
        };

        if (protocol is not { TypeKind: TypeKind.Protocol, SelfTypeParameter: { } self } || IsExtensionOnly(symbol)) {
            Report(ErrorCode.ERR_MalformedBuiltin, at,
                $"a builtin family is the built-in types' default for one of the protocol's requirements, and '{symbol.Name}' meets none: declare the requirement in the protocol");
            return;
        }

        var verified = new HashSet<SilType>();
        _builtinFamilies[symbol] = verified;
        foreach (var type in BuiltinTypes()) {
            if (!type.Satisfies(protocol)) {
                continue;
            }

            var lowered = Lower(type)!;
            var concrete = new TypeSubstitution(new() { [self] = type });
            TypeSymbol AsType(TypeSymbol t) => ReferenceEquals((t as NamedTypeSymbol)?.OriginalDefinition, protocol.OriginalDefinition) ? type : concrete.Apply(t);
            var before = _diagnostics.Count;
            CheckBuiltin(symbol, FamilyMember(name, lowered), attributes, [..operands.Select(AsType)], AsType(result), throws, TypeSubstitution.Empty, mutating);
            if (_soundBuiltins.Remove(symbol)) {
                verified.Add(lowered);
            }

            // One type's disagreement says what is wrong with the declaration; the rest would repeat it.
            if (_diagnostics.Count > before) {
                return;
            }
        }
    }

    /// <summary>The types SIL's builtins work on, which a builtin family is for.</summary>
    IEnumerable<TypeSymbol> BuiltinTypes() => [..Core.Numerics, Core.Bool, Core.Char, Core.String];

    /// <summary>The builtin of a family for one type: its <c>Self</c> segment made the type's name, as <c>count_leading_zeros_I8</c>.</summary>
    static string FamilyMember(string family, SilType type) =>
        string.Join('_', family.Split('_').Select(segment => segment == Compilation.BuiltinFamilySelf ? type.ToString() : segment));

    sealed partial class FunctionLowering {
        /// <summary>
        ///     Whether a SIMD builtin, which is checked where it is used, gives
        ///     what its declaration says — reported where not. Every other
        ///     builtin was checked where it was declared.
        /// </summary>
        bool Agrees(string name, SilBuiltins.Signature? signature, SilType? declared, BoundNode at) {
            if (!SimdBuiltins.Is(name) || signature is not null && (declared is null || signature.Result == declared || declared == SilType.Unit && signature.Result == SilType.Unit)) {
                return true;
            }

            _failed = true;
            generator.Report(ErrorCode.ERR_BuiltinSignatureMismatch, at.Syntax.Span,
                signature is null
                    ? $"the SIMD builtin '{name}' does not apply to what it is given here"
                    : $"the SIMD builtin '{name}' gives {signature.Result} here, where its declaration says {declared}");
            return false;
        }

        /// <summary>
        ///     The builtin a family's member is for the type Self stands for at
        ///     this use, or null — reported — when there is none: the type is
        ///     not one of those the family was checked for.
        /// </summary>
        string? FamilyBuiltin(Symbol member, string family, TypeSymbol? self, BoundNode at) {
            if (self is not null && generator.Lower(Sub(self)) is { } lowered
                && generator._builtinFamilies.TryGetValue(member, out var verified) && verified.Contains(lowered)) {
                return FamilyMember(family, lowered);
            }

            _failed = true;
            generator.Report(ErrorCode.ERR_UnknownBuiltin, at.Syntax.Span,
                $"'{member.Name}' is a builtin for the built-in types alone, and '{(self is null ? "?" : Sub(self).ToDisplayString())}' is not one of them");
            return null;
        }

        /// <summary>
        ///     What Self is at a call to a family's member: the receiver's type,
        ///     the type a static member is reached through, or the type of
        ///     what is passed for a parameter of type Self — an operator's operands.
        /// </summary>
        TypeSymbol? FamilySelf(BoundCall call, FunctionSymbol callee) {
            if (OwnerOf(callee)?.OriginalDefinition is not { SelfTypeParameter: { } self }) {
                return null;
            }

            if (HasSelf(callee)) {
                return call.Receiver?.Type ?? self;
            }

            if (call.Receiver is BoundTypeExpression on) {
                return on.ReferencedType;
            }

            // 'I8.truncating(…)': the binder says what Self is.
            if (call.Substitution.Lookup(self) is { } named) {
                return named;
            }

            for (var i = 0; i < callee.Parameters.Length && i < call.Arguments.Length; i++) {
                if (ReferenceEquals(callee.Parameters[i].Type, self)) {
                    return call.Arguments[i].Type;
                }
            }

            return self;
        }

        /// <summary>A call to a <c>@builtin</c> function: the builtin, on the borrowed receiver and arguments.</summary>
        SilValue? BuiltinCall(BoundCall call, FunctionSymbol callee) {
            if (generator._inlineArrayBuiltins.Contains(callee)) {
                return Missing(call, $"the member '{callee.Name}' of '{call.Receiver?.Type.ToDisplayString()}' outside an inline layout");
            }

            var name = callee.Builtin!;
            if (Compilation.IsBuiltinFamily(name)) {
                if (FamilyBuiltin(callee, name, FamilySelf(call, callee), call) is not { } member) {
                    return null;
                }

                name = member;
            } else if (!generator._soundBuiltins.Contains(callee)) {
                _failed = true;
                return null;
            }

            var operands = new List<BoundExpression>();
            if (HasSelf(callee)) {
                operands.Add(call.Receiver ?? new BoundSelfAccess(call.Syntax, Owner(callee)!.SelfTypeParameter ?? SelfOf(Owner(callee)!)));
            }

            operands.AddRange(call.Arguments);
            var result = LowerType(call.Type);
            return callee.IsMutating ? ApplyMutatingBuiltin(name, operands, result, call) : ApplyBuiltin(name, operands, result, call);
        }

        /// <summary>A read of a <c>@builtin</c> property: the builtin, on the borrowed receiver.</summary>
        SilValue? BuiltinProperty(BoundFieldAccess access) => BuiltinProperty(access, access.Field);

        /// <summary>A read of a property whose getter is a builtin, a family's member or not: the builtin, on the borrowed receiver.</summary>
        SilValue? BuiltinProperty(BoundFieldAccess access, FieldSymbol property) {
            var owner = OwnerOfField(property)!;
            var receiver = access.Receiver ?? new BoundSelfAccess(access.Syntax, owner.SelfTypeParameter ?? SelfOf(owner));
            var name = property.Builtin!;
            if (Compilation.IsBuiltinFamily(name)) {
                if (FamilyBuiltin(property, name, receiver.Type, access) is not { } member) {
                    return null;
                }

                name = member;
            } else if (!generator._soundBuiltins.Contains(property)) {
                _failed = true;
                return null;
            }

            return ApplyBuiltin(name, [receiver], LowerType(access.Type), access);
        }

        SilValue? ApplyBuiltin(string name, List<BoundExpression> operands, SilType? declared, BoundNode at) {
            var borrowed = new List<SilValue>();
            foreach (var operand in operands) {
                if (Borrow(operand) is not { } value) {
                    EndAll(borrowed);
                    return null;
                }

                borrowed.Add(value);
            }

            // A program that reads a type's fields from its metadata has every type's metadata carry them.
            if (name.StartsWith("metadata_field_", StringComparison.Ordinal)) {
                generator._module.FieldDescriptors = true;
            }

            // Only a SIMD builtin takes its type from the declaration; the rest are told by their operands.
            var simd = SimdBuiltins.Is(name);
            var resolved = SilBuiltins.Find(name) ?? SilBuiltins.Resolve(name, [..borrowed.Select(static b => b.Type)], simd ? declared : null);
            if (!Agrees(name, resolved, declared, at) || resolved is not { } signature) {
                _failed = true;
                EndAll(borrowed);
                return null;
            }

            var result = signature.Result;
            var applied = Emit(new BuiltinInst(name, result, [..borrowed]));
            EndAll(borrowed);
            return result == SilType.Unit ? UnitValue.Instance : applied;
        }

        /// <summary>
        ///     A <c>mutating</c> builtin — an array appended to, emptied, its last
        ///     element taken — on the receiver where it is, and the rest of its
        ///     operands borrowed, or owned where it keeps them.
        /// </summary>
        SilValue? ApplyMutatingBuiltin(string name, List<BoundExpression> operands, SilType? declared, BoundNode at) {
            var borrows = new List<SilValue>();
            if (Address(operands[0], borrows) is not { } address) {
                EndAll(borrows);
                return null;
            }

            // What the builtin keeps is evaluated owned; it can only be known
            // from the operands, so the consumed positions are the array
            // operations' own: an appended element.
            var values = new List<SilValue> { address };
            var held = new List<SilValue>();
            for (var i = 1; i < operands.Count; i++) {
                var consumed = name == "array_append" && i == 1;
                if ((consumed ? Expression(operands[i]) : Borrow(operands[i])) is not { } value) {
                    EndAll(held);
                    EndAll(borrows);
                    return null;
                }

                values.Add(consumed ? Owned(value) : value);
                if (!consumed) {
                    held.Add(value);
                }
            }

            var resolved = SilBuiltins.Resolve(name, [..values.Select(static v => v.Type)], SimdBuiltins.Is(name) ? declared : null);
            if (!Agrees(name, resolved, declared, at) || resolved is not { } signature) {
                _failed = true;
                EndAll(held);
                EndAll(borrows);
                return null;
            }

            // 'T[..k]' grows within its bound, and no further.
            if (name == "array_append" && Sub(operands[0].Type) is ArrayTypeSymbol { ArrayKind: ArrayKind.Inline, Capacity: { } capacity }) {
                var bounded = Emit(new LoadBorrowInst(address));
                var count = Emit(new BuiltinInst("array_count", SilType.Int, bounded));
                Emit(new EndBorrowInst(bounded));
                var full = Emit(new BuiltinInst("cmp_ge_Int", SilType.Bool, count, Emit(new IntegerLiteralInst(SilType.Int, capacity))));
                Emit(new CondFailInst(full, "inline array is full"));
            }

            var applied = Emit(new BuiltinInst(name, signature.Result, [..values]));
            EndAll(held);
            EndAll(borrows);
            return signature.Result == SilType.Unit ? UnitValue.Instance : applied;
        }
    }
}
