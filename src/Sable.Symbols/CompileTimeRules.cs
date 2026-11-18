using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     What code that runs in the compiler may do (SYNTAX.md §21): a
///     <c>const</c>'s value, a <c>const fn</c>'s body, a <c>comptime</c>
///     block.
/// </summary>
/// <remarks>
///     <para>
///         Such code is run by SIL lowering, in the interpreter, while the
///         program is compiled; so it must mean the same on every machine that
///         compiles it, and must not do anything a build should not. It may
///         allocate, loop, branch and call; what it calls must be a
///         <c>const fn</c>, something the compiler made (a memberwise
///         initialiser, a derived <c>==</c>), or a member of
///         <c>Sable.Core</c> that neither reads, writes nor waits. It may not
///         use <c>unsafe</c>, nor read a global that is set at run time.
///     </para>
///     <para>
///         A <c>comptime</c> block in a function body runs before the
///         function ever does, so it cannot see the function's parameters,
///         locals or <c>self</c>; only what it declares itself.
///     </para>
///     <para>
///         These are checked here, so that <c>sable check</c> and the editor
///         report them; whether the value can then be computed — it might
///         divide by zero — is lowering's to find out, by computing it.
///     </para>
/// </remarks>
public static class CompileTimeRules {
    /// <summary>Checks a constant's bound value.</summary>
    public static void CheckConstant(BoundExpression value, FieldSymbol constant, List<Diagnostic> diagnostics) {
        new Walker(diagnostics, $"the constant '{constant.Name}'", null).Visit(value);

        // A literal is not computed — it is its own value — so whether it
        // fits is told here: 'const D: U8 = 300' is not a U8.
        if (IntegerLiteral(value) is { } number && Range(constant.Type) is var (low, high) && (number < low || number > high)) {
            diagnostics.Add(Diagnostic.Error(
                ErrorCode.ERR_ConstantEvaluationFailed,
                value.Syntax.Span,
                $"the constant '{constant.Name}' is {number}, which does not fit in {constant.Type.ToDisplayString()} ({low} to {high})"
            ));
        }
    }

    static System.Numerics.BigInteger? IntegerLiteral(BoundExpression value) =>
        value switch {
            BoundConversion conversion => IntegerLiteral(conversion.Operand),
            BoundLiteral { Value: long or int or ulong } literal => new System.Numerics.BigInteger(System.Convert.ToDecimal(literal.Value)),
            BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Negate, Method: null } negated => -IntegerLiteral(negated.Operand),
            _ => null,
        };

    static (System.Numerics.BigInteger Low, System.Numerics.BigInteger High)? Range(TypeSymbol type) {
        static (System.Numerics.BigInteger, System.Numerics.BigInteger) Signed(int bits) =>
            (-(System.Numerics.BigInteger.One << (bits - 1)), (System.Numerics.BigInteger.One << (bits - 1)) - 1);

        static (System.Numerics.BigInteger, System.Numerics.BigInteger) Unsigned(int bits) =>
            (System.Numerics.BigInteger.Zero, (System.Numerics.BigInteger.One << bits) - 1);

        return type is not NamedTypeSymbol { TypeKind: TypeKind.Builtin } primitive ? null
            : primitive.Name switch {
                "I8" => Signed(8),
                "I16" => Signed(16),
                "I32" => Signed(32),
                "I64" or "Int" => Signed(64),
                "U8" => Unsigned(8),
                "U16" => Unsigned(16),
                "U32" => Unsigned(32),
                "U64" or "UInt" => Unsigned(64),
                _ => null,
            };
    }

    /// <summary>
    ///     Checks a function's bound body: all of it, for a <c>const fn</c>;
    ///     otherwise each <c>comptime</c> block and local <c>const fn</c> in
    ///     it.
    /// </summary>
    public static void CheckBody(BoundNode body, FunctionSymbol function, List<Diagnostic> diagnostics) {
        if (function.IsConst) {
            new Walker(diagnostics, $"the const fn '{function.Name}'", null).Visit(body);
            return;
        }

        FindNested(body, diagnostics);
    }

    /// <summary>Whether a body's text could hold anything <see cref="CheckBody" /> looks at: most do not, and need no walk.</summary>
    /// <remarks>
    ///     A search of the text, which is quicker than a walk of the tokens;
    ///     a body of a tree being edited, whose text is not the file's, is
    ///     walked.
    /// </remarks>
    public static bool MightNeedChecking(SyntaxTree tree, SyntaxNode body) {
        var root = body;
        while (root.Parent is { } parent) {
            root = parent;
        }

        if (!ReferenceEquals(root, tree.Root)) {
            return body.DescendantTokens().Any(static t => t.Kind is SyntaxKind.ConstKeyword or SyntaxKind.ComptimeKeyword);
        }

        var text = tree.Text.Text.AsSpan(body.Span.Start, body.Span.Length);
        return text.Contains("const", StringComparison.Ordinal) || text.Contains("comptime", StringComparison.Ordinal);
    }

    /// <summary>Checks a top-level <c>comptime</c> block.</summary>
    public static void CheckComptime(BoundBlock block, List<Diagnostic> diagnostics) =>
        new Walker(diagnostics, "a 'comptime' block", block.Syntax.Span).Visit(block);

    static void FindNested(BoundNode node, List<Diagnostic> diagnostics) {
        switch (node) {
            case BoundScopedBlock { ScopeKind: ScopeKind.Comptime } comptime:
                new Walker(diagnostics, "a 'comptime' block", comptime.Syntax.Span).Visit(comptime.Body);
                return;
            case BoundLocalFunction { Symbol.IsConst: true, Body: { } body } local:
                new Walker(diagnostics, $"the const fn '{local.Symbol.Name}'", null).Visit(body);
                return;
        }

        foreach (var child in node.Children) {
            FindNested(child, diagnostics);
        }
    }

    /// <summary>Whether code run in the compiler may call the function.</summary>
    public static bool MayCall(FunctionSymbol function) {
        if (function.IsConst) {
            return true;
        }

        // A protocol's requirement, called on a type parameter: which
        // function that is, is known only once the code runs.
        if (function.ContainingSymbol is NamedTypeSymbol { TypeKind: TypeKind.Protocol }) {
            return true;
        }

        // What the compiler wrote: a memberwise initialiser, a derived member.
        if (function.DeclaringSyntax is not [FunctionDeclarationSyntax or InitDeclarationSyntax or OperatorDeclarationSyntax or AccessorSyntax
            or SubscriptDeclarationSyntax or DeinitDeclarationSyntax]) {
            return true;
        }

        return InCore(function) && Effect(function) is null;
    }

    /// <summary>
    ///     Whether <c>Sable.Core</c> declares the symbol: told by the file it
    ///     is written in, since an extension anywhere can add to a core type.
    /// </summary>
    static bool InCore(Symbol symbol) {
        for (var current = symbol; current is not null; current = current.ContainingSymbol) {
            if (current is ModuleSymbol module) {
                return module.Name == CoreLibrary.ModuleName;
            }

            if (current.DeclaringSyntax is [var syntax, ..]) {
                var node = syntax;
                while (node.Parent is { } parent) {
                    node = parent;
                }

                return node is CompilationUnitSyntax { Module: { } declared } && declared.Name.ToString().Trim() == CoreLibrary.ModuleName;
            }
        }

        return false;
    }

    /// <summary>The part of <c>Sable.Core</c> that reads, writes or waits, which the symbol is part of; or null.</summary>
    static string? Effect(Symbol symbol) {
        for (var current = symbol; current is not null; current = current.ContainingSymbol) {
            switch (current) {
                case NamedTypeSymbol { Name: "Runtime" or "Task" or "Region" } type when InCore(type):
                    return type.Name;
                case FunctionSymbol { Name: "print" } function when InCore(function):
                    return "print";
            }
        }

        return null;
    }

    static bool IsConstant(FieldSymbol field) => field.DeclaringSyntax is [ConstDeclarationSyntax];

    sealed class Walker(List<Diagnostic> diagnostics, string what, TextSpan? block) {
        readonly HashSet<int> _reported = [];

        void Report(SyntaxNode at, string message) {
            if (_reported.Add(at.Span.Start)) {
                diagnostics.Add(Diagnostic.Error(ErrorCode.ERR_NotConstant, at.Span, message));
            }
        }

        bool Outside(SyntaxNode? declaration) =>
            block is { } span && (declaration is null || declaration.Span.Start < span.Start || declaration.Span.End > span.End);

        public void Visit(BoundNode node) {
            switch (node) {
                case BoundCall call when !MayCall(call.Function):
                    Report(call.Syntax, Effect(call.Function) is { } effect
                        ? $"{what} runs at compile time, so it cannot use '{effect}', which reads, writes or waits"
                        : $"{what} runs at compile time, so it can call only a 'const fn'; '{call.Function.Name}' is not one");
                    break;

                case BoundCall { Function: { Name: "compileError" } function, Arguments: [var message] } when InCore(function)
                    && message is not BoundLiteral { Value: string }:
                    Report(message.Syntax, "'compileError' takes its message as a string literal");
                    break;

                case BoundFunctionReference reference when !MayCall(reference.Function):
                    Report(reference.Syntax, $"{what} runs at compile time, so it can use only a 'const fn'; '{reference.Function.Name}' is not one");
                    break;

                case BoundBinaryOperator { Method: { } method } binary when !MayCall(method):
                    Report(binary.Syntax, $"{what} runs at compile time, so it can use only a 'const' operator; this '{method.Name}' is not one");
                    break;

                case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Await } awaited:
                    Report(awaited.Syntax, $"{what} runs at compile time, so it cannot wait");
                    break;

                case BoundUnaryOperator { Method: { } method } unary when !MayCall(method):
                    Report(unary.Syntax, $"{what} runs at compile time, so it can use only a 'const' operator; this '{method.Name}' is not one");
                    break;

                case BoundScopedBlock { ScopeKind: ScopeKind.Unsafe } unsafeBlock:
                    Report(unsafeBlock.Syntax, $"{what} runs at compile time, so it cannot use 'unsafe'");
                    return;

                // A global set at run time: 'static let', a module-level 'var'.
                case BoundFieldAccess { Field: var field } access
                    when (field.IsStatic || field.ContainingSymbol is ModuleSymbol) && !IsConstant(field)
                    && !(field.IsComputed && InCore(field) && Effect(field) is null):
                    Report(access.Syntax, $"{what} runs at compile time, so it can read only constants; '{field.Name}' is set when the program runs");
                    break;

                // An instance property computed by code nobody marked const.
                case BoundFieldAccess { Field: { IsComputed: true, Builtin: null } field } access
                    when !(InCore(field) && Effect(field) is null):
                    Report(access.Syntax, $"{what} runs at compile time, so it cannot read the computed property '{field.Name}'");
                    break;

                case BoundLocalAccess local when Outside(local.Local.DeclaringSyntax.FirstOrDefault()):
                    Report(local.Syntax, $"{what} runs before the function does, so it cannot see '{local.Local.Name}'");
                    return;

                case BoundParameterAccess parameter when Outside(parameter.Parameter.DeclaringSyntax.FirstOrDefault()):
                    Report(parameter.Syntax, $"{what} runs before the function does, so it cannot see '{parameter.Parameter.Name}'");
                    return;

                case BoundSelfAccess self when block is not null:
                    Report(self.Syntax, $"{what} runs before the function does, so it cannot see 'self'");
                    return;
            }

            foreach (var child in node.Children) {
                Visit(child);
            }
        }
    }
}
