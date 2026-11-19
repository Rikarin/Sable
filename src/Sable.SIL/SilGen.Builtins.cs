using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     <c>@builtin("name")</c> (Compilation.Builtins.cs): a <c>Sable.Core</c>
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

    /// <summary>Checks every <c>@builtin</c> declaration against the builtin it names.</summary>
    void CheckBuiltins() {
        foreach (var tree in _compilation.SyntaxTrees) {
            foreach (var declaration in Declarations(tree)) {
                switch (declaration) {
                    case FunctionDeclarationSyntax function
                        when _compilation.GetDeclaredSymbol(function) is FunctionSymbol { Builtin: { } name } symbol:
                        CheckBuiltin(symbol, name, function.Attributes, Operands(symbol), symbol.ReturnType, symbol.ThrownType is not null,
                            Representative(Owner(symbol)), symbol.IsMutating);
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

    sealed partial class FunctionLowering {
        /// <summary>A call to a <c>@builtin</c> function: the builtin, on the borrowed receiver and arguments.</summary>
        SilValue? BuiltinCall(BoundCall call, FunctionSymbol callee) {
            if (generator._inlineArrayBuiltins.Contains(callee)) {
                return Missing(call, $"the member '{callee.Name}' of '{call.Receiver?.Type.ToDisplayString()}' outside an inline layout");
            }

            if (!generator._soundBuiltins.Contains(callee)) {
                _failed = true;
                return null;
            }

            var operands = new List<BoundExpression>();
            if (HasSelf(callee)) {
                operands.Add(call.Receiver ?? new BoundSelfAccess(call.Syntax, SelfOf(Owner(callee)!)));
            }

            operands.AddRange(call.Arguments);
            return callee.IsMutating ? ApplyMutatingBuiltin(callee.Builtin!, operands) : ApplyBuiltin(callee.Builtin!, operands);
        }

        /// <summary>A read of a <c>@builtin</c> property: the builtin, on the borrowed receiver.</summary>
        SilValue? BuiltinProperty(BoundFieldAccess access) {
            if (!generator._soundBuiltins.Contains(access.Field)) {
                _failed = true;
                return null;
            }

            var receiver = access.Receiver ?? new BoundSelfAccess(access.Syntax, SelfOf(OwnerOfField(access.Field)!));
            return ApplyBuiltin(access.Field.Builtin!, [receiver]);
        }

        SilValue? ApplyBuiltin(string name, List<BoundExpression> operands) {
            var borrowed = new List<SilValue>();
            foreach (var operand in operands) {
                if (Borrow(operand) is not { } value) {
                    EndAll(borrowed);
                    return null;
                }

                borrowed.Add(value);
            }

            var result = (SilBuiltins.Find(name) ?? SilBuiltins.Resolve(name, [..borrowed.Select(static b => b.Type)]))!.Result;
            var applied = Emit(new BuiltinInst(name, result, [..borrowed]));
            EndAll(borrowed);
            return result == SilType.Unit ? UnitValue.Instance : applied;
        }

        /// <summary>
        ///     A <c>mutating</c> builtin — an array appended to, emptied, its last
        ///     element taken — on the receiver where it is, and the rest of its
        ///     operands borrowed, or owned where it keeps them.
        /// </summary>
        SilValue? ApplyMutatingBuiltin(string name, List<BoundExpression> operands) {
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

            if (SilBuiltins.Resolve(name, [..values.Select(static v => v.Type)]) is not { } signature) {
                _failed = true;
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
