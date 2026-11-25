using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

public sealed partial class SilGen {
    sealed partial class FunctionLowering {
        /// <summary>
        ///     <c>e is IoError</c>, <c>e as? IoError</c> and <c>e as! IoError</c>
        ///     of an optional: asked of the value it holds, and of none, no. One
        ///     to another optional is of the optional itself.
        /// </summary>
        internal static bool IsOptionalCast(BoundConversion conversion) =>
            conversion is { ConversionKind: ConversionKind.Explicit, Operand.Type: OptionalTypeSymbol from, Syntax: CastExpressionSyntax { QuestionOrBangToken: { IsNone: false } mark } }
            && (mark.Kind == SyntaxKind.QuestionToken
                ? conversion.Type is OptionalTypeSymbol { UnderlyingType: not OptionalTypeSymbol }
                : conversion.Type is not OptionalTypeSymbol && !from.UnderlyingType.Equals(conversion.Type));

        SilValue? OptionalCast(BoundConversion conversion) {
            var held = ((OptionalTypeSymbol)conversion.Operand.Type).UnderlyingType;
            var at = conversion.Syntax;
            var local = LocalSymbol.Synthesised("$held", Core.Module, held);
            BoundExpression read = new BoundLocalAccess(at, local);
            if (conversion.Type is not OptionalTypeSymbol result) {
                // 'as!': a trap of none too, as of '!'.
                var none = new BoundLiteral(at, new OptionalTypeSymbol(conversion.Type), null);
                return Expression(HeldMatch(at, conversion.Operand, local, conversion.Type,
                    new BoundConversion(at, conversion.Type, read, ConversionKind.Explicit),
                    new BoundConversion(at, conversion.Type, none, ConversionKind.Explicit)));
            }

            var kind = held.Equals(result.UnderlyingType) ? ConversionKind.OptionalWrap : ConversionKind.Explicit;
            return Expression(HeldMatch(at, conversion.Operand, local, result, new BoundConversion(at, result, read, kind), new BoundLiteral(at, result, null)));
        }

        SilValue? OptionalTest(BoundTypeTest test) {
            var held = ((OptionalTypeSymbol)test.Operand.Type).UnderlyingType;
            var at = test.Syntax;
            var local = LocalSymbol.Synthesised("$held", Core.Module, held);
            return Expression(HeldMatch(at, test.Operand, local, Core.Bool,
                new BoundTypeTest(at, Core.Bool, new BoundLocalAccess(at, local), test.TestedType),
                new BoundLiteral(at, Core.Bool, false)));
        }

        /// <summary>'match operand { .Some(let held) => some, _ => none }'.</summary>
        static BoundMatch HeldMatch(SyntaxNode at, BoundExpression operand, LocalSymbol held, TypeSymbol type, BoundExpression some, BoundExpression none) {
            var optional = (OptionalTypeSymbol)operand.Type;
            return new BoundMatch(at, type, operand, [
                new BoundMatchArm(at, [new BoundSomePattern(at, optional, new BoundBindingPattern(at, held.Type, held))], null, some),
                new BoundMatchArm(at, [new BoundWildcardPattern(at, optional)], null, none),
            ]);
        }
    }
}
