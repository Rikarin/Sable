using Sable.Syntax;

namespace Sable.Symbols;

public sealed partial record TestFunction {
    /// <summary>
    ///     A fuzz target that takes the fuzzer's bytes as they are, one
    ///     <c>U8[:]</c>; another is called with arbitrary values made of them.
    /// </summary>
    public bool TakesBytes => Kind == TestKind.Fuzz && Compilation.IsFuzzInput(Symbol);

    /// <summary>
    ///     <c>@fuzz(mutator: mutated)</c>: the function of the target's module
    ///     that makes another input of one, in place of the fuzzer's own
    ///     changes to its bytes; null for those.
    /// </summary>
    public string? Mutator { get; init; }
}

/// <summary>What a <c>@fuzz</c> function may take (S3104), and its mutator.</summary>
public sealed partial class Compilation {
    const string MutatorExpected =
        "'@fuzz' takes nothing, or 'mutator: f' for a function of the module, 'f(_ input: U8[:], _ seed: U64) -> U8[>..]', that makes another input of one";

    /// <summary>How many values a fuzz target may take: what Sable.Testing's <c>fuzzCall</c> is written for.</summary>
    const int MostFuzzArguments = 4;

    /// <summary>Whether the function takes the fuzzer's input itself: one <c>U8[:]</c>.</summary>
    internal static bool IsFuzzInput(FunctionSymbol function) =>
        function.Parameters is [{ Type: ArrayTypeSymbol { ArrayKind: ArrayKind.Slice, ElementType: NamedTypeSymbol { Name: "U8", ContainingModule.Name: "Sable.Core" } } }];

    /// <summary>
    ///     A fuzz target takes one <c>U8[:]</c>, the bytes the fuzzer chose; or
    ///     one value or several, plainly, of types that are <c>Arbitrary</c>,
    ///     which the harness makes of those bytes. Whether they are is known
    ///     only where the program has Sable.Testing, as every fuzz build does.
    /// </summary>
    void CheckFuzzTarget(FunctionSymbol function, FunctionDeclarationSyntax declaration, Action<ErrorCode, TextSpan, string> report) {
        if (function.IsAsync) {
            report(ErrorCode.ERR_FuzzSignature, declaration.Identifier.Span, "a fuzz target is not async: C calls it once an input, and waits for nothing");
        }

        if (IsFuzzInput(function)) {
            return;
        }

        const string wanted = "a fuzz target takes one 'U8[:]', the bytes the fuzzer chose, or values made of them, of types that are 'Arbitrary'";
        if (function.Parameters.Length is 0 or > MostFuzzArguments) {
            report(ErrorCode.ERR_FuzzSignature, declaration.Parameters.Span, function.Parameters.IsEmpty ? wanted : $"{wanted}: {MostFuzzArguments} at most");
            return;
        }

        foreach (var (parameter, syntax) in function.Parameters.Zip(declaration.Parameters.Parameters)) {
            if (parameter.Modifier is not ParameterModifier.None || parameter.IsVariadic || parameter.IsAutoclosure || parameter.IsByReference) {
                report(ErrorCode.ERR_FuzzSignature, syntax.Span, "a fuzz target's parameters are plain ones, each given a value made of the fuzzer's bytes");
            } else if (parameter.Type is ArrayTypeSymbol { ArrayKind: ArrayKind.Slice }) {
                report(ErrorCode.ERR_FuzzSignature, syntax.Span, $"{wanted}; a slice is not, beside other parameters or of another element: take the array");
            } else if (TestingType("Arbitrary") is { } arbitrary && !Conversions.Satisfies(parameter.Type, arbitrary)) {
                report(ErrorCode.ERR_FuzzSignature, syntax.Span, $"{wanted}; '{parameter.Type.ToDisplayString()}' is not ('@derive(Arbitrary)' makes a type of one's own so)");
            }
        }
    }

    /// <summary>The name after <c>mutator:</c>, when it is of a function that can be one; else null.</summary>
    string? FuzzMutator(AttributeSyntax attribute, SyntaxTree tree) {
        if (attribute.ArgumentList!.Arguments is not [{ NameToken: { IsNone: false, Text: "mutator" } } argument]) {
            return null;
        }

        var name = argument.Expression.ToString().Trim();
        return GetFileScope(tree).Module.GetMembers(name).OfType<FunctionSymbol>().Any(f =>
            f is { IsAsync: false, ThrownType: null, Parameters: [{ Label: "_" }, { Label: "_", Type: var seed }] }
            && f.TypeParameters.IsEmpty
            && IsBytes(f.Parameters[0].Type, ArrayKind.Slice) && ReferenceEquals(seed, Core.U64) && IsBytes(f.ReturnType, ArrayKind.Grow))
            ? name
            : null;

        bool IsBytes(TypeSymbol type, ArrayKind kind) => type is ArrayTypeSymbol array && array.ArrayKind == kind && ReferenceEquals(array.ElementType, Core.U8);
    }
}
