using System.Collections.Immutable;
using Sable.Symbols;

namespace Sable.SIL;

/// <summary>
///     Structural unions (docs/03 §3.5, SYNTAX.md "Structural unions"):
///     <c>F64 | String</c> is lowered to a nominal tagged union — an enum with
///     a case per member type, carrying the value (<c>LowerUnion</c>, which
///     error conversion shares). A value goes in where the
///     union is wanted, <c>x is String</c> tests the case, and a read of a
///     binding a test has narrowed takes the payload out.
/// </summary>
public sealed partial class SilGen {
    sealed partial class FunctionLowering {
        /// <summary>
        ///     A read of a binding of union type that a test has narrowed to
        ///     one of its members, or to a union of some of them: the binding
        ///     as declared, and which members are left.
        /// </summary>
        (BoundExpression Whole, ImmutableArray<int> Cases)? NarrowedUnion(BoundExpression expression) {
            var (whole, declared) = expression switch {
                BoundLocalAccess { IsNarrowed: true } local =>
                    ((BoundExpression)new BoundLocalAccess(local.Syntax, local.Local), local.Local.Type),
                BoundParameterAccess { IsNarrowed: true } parameter =>
                    (new BoundParameterAccess(parameter.Syntax, parameter.Parameter), parameter.Parameter.Type),
                _ => (null, null),
            };

            if (whole is null || StructuralUnionTypeSymbol.Of(Sub(declared!)) is not { } union) {
                return null;
            }

            // 'if v is Int { return }' leaves an 'Int | String | Vec3' as a
            // 'String | Vec3': each of those is a case of the declared union.
            var narrowed = Sub(expression.Type);
            ImmutableArray<int> cases = narrowed is StructuralUnionTypeSymbol left
                ? [..left.Cases.Select(c => union.Cases.IndexOf(c))]
                : [union.Cases.IndexOf(narrowed)];
            return cases.Contains(-1) ? null : (whole, cases);
        }

        /// <summary>
        ///     The narrowed binding's value: its payload, which the test has
        ///     said is there — or, narrowed to several members, the value
        ///     moved to the smaller union's case for the member it holds.
        /// </summary>
        SilValue? NarrowedRead(BoundExpression expression, BoundExpression whole, ImmutableArray<int> cases) {
            if (Borrow(whole) is not { Type: EnumType union } value) {
                return Missing(expression, $"a read of '{expression.Type.ToDisplayString()}' narrowed from '{whole.Type.ToDisplayString()}'");
            }

            if (cases.Length > 1) {
                return NarrowedToUnion(expression, value, union, cases);
            }

            var index = cases[0];

            var present = function.AddBlock();
            var payload = Payload(present, union.Cases[index].Payload!);
            var absent = function.AddBlock();
            Jump(new SwitchEnumInst(value, [(index, present)], absent));

            _current = absent;
            Emit(new CondFailInst(Emit(new IntegerLiteralInst(SilType.Bool, 1)), "a narrowed union held another case"));
            Jump(new UnreachableInst());

            _current = present;
            var result = Owned(payload);
            End(value);
            return result;
        }

        SilValue? NarrowedToUnion(BoundExpression expression, SilValue value, EnumType union, ImmutableArray<int> cases) {
            if (LowerType(expression.Type) is not EnumType smaller) {
                End(value);
                return Missing(expression, $"the union '{expression.Type.ToDisplayString()}'");
            }

            var joined = function.AddBlock();
            var result = joined.AddArgument(smaller);
            var targets = new List<(int, SilBlock)>();
            var blocks = new List<SilBlock>();
            foreach (var index in cases) {
                var block = function.AddBlock();
                targets.Add((index, block));
                blocks.Add(block);
            }

            var absent = function.AddBlock();
            Jump(new SwitchEnumInst(value, targets, absent));

            for (var i = 0; i < cases.Length; i++) {
                _current = blocks[i];
                var payload = Payload(blocks[i], union.Cases[cases[i]].Payload!);
                Branch(joined, Emit(new EnumInst(smaller, i, Owned(payload))));
            }

            _current = absent;
            Emit(new CondFailInst(Emit(new IntegerLiteralInst(SilType.Bool, 1)), "a narrowed union held another case"));
            Jump(new UnreachableInst());

            _current = joined;
            End(value);
            return result;
        }

        /// <summary>A value where a union of its type is wanted: the case for its type, carrying it.</summary>
        SilValue? Inject(BoundConversion conversion) {
            if (StructuralUnionTypeSymbol.Of(Sub(conversion.Type)) is not { } union || LowerType(conversion.Type) is not EnumType enumType) {
                return Missing(conversion, $"a value of the union '{conversion.Type.ToDisplayString()}'");
            }

            var operandType = Sub(conversion.Operand.Type);
            var index = union.Cases.IndexOf(operandType);

            // A literal takes the first member it can be: '1' for 'F64 | String' is an F64.
            if (index < 0 && conversion.Operand is BoundLiteral) {
                index = union.Cases.IndexOf(union.Cases.FirstOrDefault(c => Core.IsNumeric(operandType) && Core.IsNumeric(c)) ?? operandType);
            }

            if (index < 0) {
                return Missing(conversion, $"'{operandType.ToDisplayString()}' as the union '{conversion.Type.ToDisplayString()}'");
            }

            var value = conversion.Operand is BoundLiteral literal
                ? Literal(literal, union.Cases[index])
                : Expression(conversion.Operand);
            return value is null ? null : Emit(new EnumInst(enumType, index, Owned(value)));
        }

        /// <summary><c>d is String</c> on a union: whether it holds that case.</summary>
        SilValue? UnionTypeTest(BoundTypeTest test, StructuralUnionTypeSymbol union) {
            var index = union.Cases.IndexOf(Sub(test.TestedType));
            if (index < 0 || Borrow(test.Operand) is not { Type: EnumType enumType } value) {
                return Missing(test, $"'is {test.TestedType.ToDisplayString()}' on '{test.Operand.Type.ToDisplayString()}'");
            }

            var yes = function.AddBlock();
            Payload(yes, enumType.Cases[index].Payload!);
            var no = function.AddBlock();
            var joined = function.AddBlock();
            var answer = joined.AddArgument(SilType.Bool);
            Jump(new SwitchEnumInst(value, [(index, yes)], no));
            _current = yes;
            Branch(joined, Emit(new IntegerLiteralInst(SilType.Bool, 1)));
            _current = no;
            Branch(joined, Emit(new IntegerLiteralInst(SilType.Bool, 0)));
            _current = joined;
            End(value);
            return answer;
        }
    }
}
