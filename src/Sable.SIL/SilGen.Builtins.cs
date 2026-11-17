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

    /// <summary>Checks every <c>@builtin</c> declaration against the builtin it names.</summary>
    void CheckBuiltins() {
        foreach (var tree in _compilation.SyntaxTrees) {
            foreach (var declaration in Declarations(tree)) {
                switch (declaration) {
                    case FunctionDeclarationSyntax function
                        when _compilation.GetDeclaredSymbol(function) is FunctionSymbol { Builtin: { } name } symbol:
                        CheckBuiltin(symbol, name, function.Attributes, Operands(symbol), symbol.ReturnType, symbol.ThrownType is not null);
                        break;

                    case FieldDeclarationSyntax field
                        when _compilation.GetDeclaredSymbol(field) is FieldSymbol { Builtin: { } name } symbol:
                        CheckBuiltin(symbol, name, field.Attributes, OwnerOfField(symbol) is { } owner ? [owner] : [], symbol.Type, false);
                        break;
                }
            }
        }
    }

    /// <summary>What a builtin function is applied to: its receiver, if it has one, and its parameters.</summary>
    static List<TypeSymbol> Operands(FunctionSymbol function) {
        var operands = new List<TypeSymbol>();
        if (HasSelf(function) && Owner(function) is { } owner) {
            operands.Add(owner);
        }

        operands.AddRange(function.Parameters.Select(static p => p.Type));
        return operands;
    }

    static NamedTypeSymbol? OwnerOfField(FieldSymbol field) =>
        field.ContainingSymbol switch {
            NamedTypeSymbol type => type,
            ExtensionSymbol extension => extension.ExtendedType,
            _ => null,
        };

    void CheckBuiltin(Symbol symbol, string name, SyntaxList<AttributeSyntax> attributes, List<TypeSymbol> operands, TypeSymbol result, bool throws) {
        var at = attributes.First(static a => a.Name.ToString().Trim() == "builtin").Span;
        if (SilBuiltins.Find(name) is not { } signature) {
            Report(ErrorCode.ERR_UnknownBuiltin, at,
                $"there is no builtin '{name}' with a fixed signature: '@builtin' names one of SilBuiltins' table");
            return;
        }

        var lowered = operands.Select(Lower).ToList();
        var loweredResult = Lower(result);
        var modified = symbol is FunctionSymbol { Parameters: var parameters } && parameters.Any(static p => p.Modifier != ParameterModifier.None || p.IsVariadic);
        if (lowered.Any(static o => o is null) || loweredResult is null || modified || throws
            || !lowered.SequenceEqual(signature.Operands) || loweredResult != signature.Result) {
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
            if (!generator._soundBuiltins.Contains(callee)) {
                _failed = true;
                return null;
            }

            var operands = new List<BoundExpression>();
            if (HasSelf(callee)) {
                operands.Add(call.Receiver ?? new BoundSelfAccess(call.Syntax, Owner(callee)!));
            }

            operands.AddRange(call.Arguments);
            return ApplyBuiltin(callee.Builtin!, operands);
        }

        /// <summary>A read of a <c>@builtin</c> property: the builtin, on the borrowed receiver.</summary>
        SilValue? BuiltinProperty(BoundFieldAccess access) {
            if (!generator._soundBuiltins.Contains(access.Field)) {
                _failed = true;
                return null;
            }

            var receiver = access.Receiver ?? new BoundSelfAccess(access.Syntax, OwnerOfField(access.Field)!);
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

            var result = SilBuiltins.Find(name)!.Result;
            var applied = Emit(new BuiltinInst(name, result, [..borrowed]));
            EndAll(borrowed);
            return result == SilType.Unit ? UnitValue.Instance : applied;
        }
    }
}
