using System.Collections.Immutable;
using Sable.Syntax;

namespace Sable.Symbols;

/// <summary>
///     <c>#expression</c> (SYNTAX.md §18): a parameter whose default it is is
///     given, by a call that leaves it off, what the call's first argument
///     is written as and the values its parts had as it was worked out. That
///     is how <c>expect(a.count == b)</c> says what <c>a.count</c> and
///     <c>b</c> were.
/// </summary>
/// <remarks>
///     <para>
///         The parameter's type is a class that makes itself with a static
///         <c>begin()</c>, and takes each value with <c>note(text, value)</c>,
///         which gives the value back. The call <c>expect(a.count == b)</c> is
///         bound as
///     </para>
///     <code>
///         match Captured.begin() {
///             case let e => expect(e.note("a.count == b", e.note("a.count", …) == e.note("b", b)), e)
///         }
///     </code>
///     <para>
///         so that the parts are worked out once, where and in the order they
///         would have been, and the argument may throw, wait and be narrowed
///         as any other is. A part that is a literal, a move-only value, a
///         slice or an object that does not describe itself is not noted.
///     </para>
/// </remarks>
public sealed partial class Binder {
    /// <summary><c>#expression</c> itself, which is a parameter's default value and nothing else.</summary>
    BoundExpression BindExpressionBuiltin(BuiltinExpressionSyntax syntax, TypeSymbol? expectedType) {
        if (syntax.Parent is not EqualsValueClauseSyntax { Parent: ParameterSyntax parameter } || parameter.Parent is not ParameterListSyntax list || list.Parameters[0] == parameter) {
            _diagnostics.Error(
                ErrorCode.ERR_InvalidExpressionCapture,
                syntax.Span,
                "'#expression' is the default value of a parameter that is not the first: what the call's first argument is written as"
            );
            return new BoundError(syntax);
        }

        if (expectedType is null || expectedType.IsError) {
            return new BoundError(syntax);
        }

        if (CaptureMembers(expectedType) is null) {
            _diagnostics.Error(
                ErrorCode.ERR_InvalidExpressionCapture,
                syntax.Span,
                $"a parameter whose default is '#expression' is a class with 'static fn begin()', which makes one, and 'fn note<T>(_ text: String, _ value: T) -> T', "
                + $"as Sable.Testing's 'CapturedExpression' is; '{expectedType.ToDisplayString()}' is not"
            );
            return new BoundError(syntax);
        }

        return Record(syntax, new BoundBuiltin(syntax, expectedType, "expression"));
    }

    /// <summary>What takes the values of a capture's type, or null when it is no such type.</summary>
    static (FunctionSymbol Begin, FunctionSymbol Note)? CaptureMembers(TypeSymbol type) {
        if (type is not NamedTypeSymbol { TypeKind: TypeKind.Class, TypeParameters.IsEmpty: true } named) {
            return null;
        }

        FunctionSymbol? Member(string name, bool isStatic, int typeParameters, int parameters) =>
            named.GetMembers(name).OfType<FunctionSymbol>().FirstOrDefault(f =>
                f.IsStatic == isStatic && f.TypeParameters.Length == typeParameters && f.Parameters.Length == parameters);

        return Member("begin", true, 0, 0) is { } begin && begin.ReturnType.Equals(named)
               && Member("note", false, 1, 2) is { } note
            ? (begin, note)
            : null;
    }

    /// <summary>A call given <c>#expression</c>, bound around the values of its first argument; null for any other.</summary>
    BoundExpression? CapturingExpression(BoundCall call) {
        var at = -1;
        for (var i = 0; i < call.Arguments.Length; i++) {
            if (call.Arguments[i] is BoundBuiltin { Name: "expression" } builtin && builtin.Syntax == call.Syntax) {
                at = i;
                break;
            }
        }

        if (at < 1 || CaptureMembers(call.Arguments[at].Type) is not { } members) {
            return null;
        }

        var type = (NamedTypeSymbol)call.Arguments[at].Type;
        var local = LocalSymbol.Synthesised("$expression", _containingSymbol, type);
        var capture = new ExpressionCapture(local, members.Note, _compilation.CoreProtocol("Debug"));
        var arguments = call.Arguments
            .SetItem(0, capture.Whole(call.Arguments[0]))
            .SetItem(at, new BoundLocalAccess(call.Syntax, local));
        var made = new BoundCall(call.Syntax, call.Receiver, call.Function, arguments, call.Type) { Substitution = call.Substitution };
        var begin = new BoundCall(call.Syntax, new BoundTypeExpression(call.Syntax, type), members.Begin, [], type);
        return new BoundMatch(call.Syntax, call.Type, begin, [new BoundMatchArm(call.Syntax, [new BoundBindingPattern(call.Syntax, type, local)], null, made)]) {
            CapturedCall = made,
        };
    }

    /// <summary>Puts a note of its value around each part of an expression that has one worth telling.</summary>
    sealed class ExpressionCapture(LocalSymbol local, FunctionSymbol note, NamedTypeSymbol? debug) {
        /// <summary>The whole argument, always noted, for what it is written as.</summary>
        public BoundExpression Whole(BoundExpression expression) => Noted(Parts(expression, whole: true), expression, whole: true);

        /// <summary>A part: noted, if its value is worth telling, with its own parts.</summary>
        BoundExpression Part(BoundExpression expression) =>
            expression switch {
                // 'user?.name.count' is one step, which lowering takes whole.
                _ when IsChain(expression) => Noted(expression, expression),

                // A conversion the compiler put in is written as its operand.
                BoundConversion { ConversionKind: ConversionKind.Explicit } => Noted(Parts(expression), expression),
                BoundConversion { ConversionKind: ConversionKind.LiteralTyping } => expression,
                BoundConversion conversion => Converted(conversion, Part(conversion.Operand)),
                _ => Noted(Parts(expression), expression),
            };

        /// <summary>An optional chain, or what may be one: '?' in what it is written as, through a member, a call or an index.</summary>
        static bool IsChain(BoundExpression expression) =>
            expression is BoundCall or BoundIndirectCall or BoundFieldAccess or BoundIndexAccess or BoundConversion { ConversionKind: ConversionKind.OptionalWrap }
            && expression.Syntax.ToString().Contains('?', StringComparison.Ordinal);

        /// <summary>The expression with its parts noted, and not itself.</summary>
        BoundExpression Parts(BoundExpression expression, bool whole = false) {
            switch (expression) {
                case var chain when IsChain(chain):
                    return chain;

                case BoundBinaryOperator binary: {
                    // What the whole compares is told even when it is a literal.
                    var left = Operand(binary.Left, whole);
                    var right = Operand(binary.Right, whole);
                    return new BoundBinaryOperator(binary.Syntax, binary.Type, binary.OperatorKind, left, right, binary.Method) { Structural = binary.Structural };
                }

                case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Not or UnaryOperatorKind.Negate or UnaryOperatorKind.Plus or UnaryOperatorKind.Complement or UnaryOperatorKind.UserDefined } unary:
                    return new BoundUnaryOperator(unary.Syntax, unary.Type, unary.OperatorKind, Part(unary.Operand), unary.Method);

                case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.Await } unary:
                    return new BoundUnaryOperator(unary.Syntax, unary.Type, unary.OperatorKind, Parts(unary.Operand, whole), unary.Method);

                case BoundTry { TryKind: TryKind.Propagate or TryKind.Optional or TryKind.Force, Fallback: null } attempt:
                    return new BoundTry(attempt.Syntax, attempt.Type, Parts(attempt.Expression, whole), attempt.TryKind, null);

                case BoundConversion conversion:
                    return Converted(conversion, whole ? Parts(conversion.Operand, whole) : Part(conversion.Operand));

                case BoundConditional conditional:
                    return new BoundConditional(conditional.Syntax, conditional.Type, Part(conditional.Condition), Part(conditional.WhenTrue), Part(conditional.WhenFalse));

                case BoundCall call:
                    return Called(call);

                case BoundFieldAccess { Receiver: { } receiver } field when Read(receiver) is { } read:
                    return WithReceiver(field, read);

                case BoundIndexAccess { Subscript: null } index:
                    return new BoundIndexAccess(index.Syntax, index.Type, Read(index.Receiver) ?? index.Receiver, [..index.Indices.Select(Part)]) {
                        Substitution = index.Substitution,
                    };

                default:
                    return expression;
            }
        }

        BoundExpression Operand(BoundExpression operand, bool compared) =>
            compared && Unconverted(operand) is BoundLiteral { Value: not null } ? Noted(operand, operand, whole: true) : Part(operand);

        static BoundExpression Unconverted(BoundExpression expression) =>
            expression is BoundConversion { ConversionKind: not ConversionKind.Explicit } conversion ? Unconverted(conversion.Operand) : expression;

        static BoundExpression Converted(BoundConversion conversion, BoundExpression operand) =>
            new BoundConversion(conversion.Syntax, conversion.Type, operand, conversion.ConversionKind) { IsUnchecked = conversion.IsUnchecked };

        static BoundExpression WithReceiver(BoundFieldAccess field, BoundExpression receiver) =>
            new BoundFieldAccess(field.Syntax, receiver, field.Field, field.Type) { Substitution = field.Substitution, FlattensChain = field.FlattensChain };

        BoundExpression Called(BoundCall call) {
            // An argument passed on as it is: storage the call writes to or
            // refers to, a closure, or what stands for where the call is.
            var function = call.Function;
            var arguments = call.Arguments;
            if (arguments.Length == function.Parameters.Length) {
                arguments = [
                    ..arguments.Select((argument, i) => function.Parameters[i] is { Modifier: ParameterModifier.None, IsAutoclosure: false, IsByReference: false, IsVariadic: false }
                            && argument.Syntax != call.Syntax
                        ? Part(argument)
                        : argument),
                ];
            }

            var receiver = call.Receiver is { } written && !function.IsMutating && !function.IsStatic ? Read(written) ?? written : call.Receiver;
            return new BoundCall(call.Syntax, receiver, function, arguments, call.Type) { Substitution = call.Substitution };
        }

        /// <summary>What a member is read from, noted if its value says something; null to leave it as it is.</summary>
        BoundExpression? Read(BoundExpression receiver) =>
            receiver is BoundTypeExpression or BoundMetatype or BoundSuperAccess or BoundSelfAccess || receiver.Type is ReferenceTypeSymbol
                ? null
                : Part(receiver);

        BoundExpression Noted(BoundExpression inner, BoundExpression written, bool whole = false) {
            var type = inner.Type;
            if (!whole && Unconverted(written) is BoundLiteral or BoundTypeExpression or BoundMetatype or BoundFunctionReference or BoundClosure
                    or BoundArray or BoundDictionary or BoundTuple or BoundFunctionGroup or BoundBuiltin or BoundUnionCaseConstruction or BoundSelfAccess or BoundSuperAccess
                || !Notable(type)) {
                return inner;
            }

            // An object is told only if it says how: all it reaches would say little.
            if (!whole && type.IsReferenceType && (debug is null || !Conversions.Satisfies(type, debug))) {
                return inner;
            }

            var text = new BoundLiteral(written.Syntax, note.Parameters[0].Type, Text(written.Syntax));
            return new BoundCall(written.Syntax, new BoundLocalAccess(written.Syntax, local), note, [text, inner], type) {
                Substitution = new TypeSubstitution(new() { [note.TypeParameters[0]] = type }),
            };
        }

        /// <summary>A value that can be kept beside the one that is used: a copy that nothing else owns the storage of.</summary>
        static bool Notable(TypeSymbol type) =>
            !type.IsError && !Copyability.IsNoncopyable(type)
            && type is not (FunctionTypeSymbol or MetatypeTypeSymbol or ReferenceTypeSymbol or OpaqueTypeSymbol or ArrayTypeSymbol { ArrayKind: ArrayKind.Slice })
            && type is not NamedTypeSymbol { Name: "Void" or "Never", ContainingSymbol.Name: "Sable.Core" };

        /// <summary>What it is written as, on one line.</summary>
        static string Text(SyntaxNode syntax) =>
            string.Join(' ', syntax.ToString().Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
