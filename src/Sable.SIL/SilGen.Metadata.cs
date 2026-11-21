using Sable.Symbols;

namespace Sable.SIL;

/// <summary>
///     <c>TypeMetadata</c>'s <c>@intrinsic</c> members (lib/Sable.Core/TypeMetadata.sb):
///     a type's metadata, <c>metadata $T</c>, and its value witnesses applied
///     to a value, as code that knows a type only by its metadata applies
///     them (#297).
/// </summary>
public sealed partial class SilGen {
    sealed partial class FunctionLowering {
        SilValue? MetadataIntrinsic(BoundCall call) {
            // 'TypeMetadata.of(T.self)': T's metadata.
            if (call.Function.Name == "of") {
                if (call.Arguments is not [var argument]
                    || Sub(argument.Type) is not MetatypeTypeSymbol { InstanceType: var instance }
                    || LowerType(instance) is not { } described) {
                    return Missing(call, "the metadata of a type");
                }

                Discard(argument);
                return Emit(new MetadataInst(described));
            }

            if (call.Receiver is not { } receiver) {
                return Missing(call, $"'TypeMetadata.{call.Function.Name}' without its metadata");
            }

            if (call.Function.Name == "assign") {
                return Assign(call, receiver);
            }

            if (call.Arguments is not [var operand] || call.Function.Name is not ("copy" or "move" or "destroyCopy")) {
                return Missing(call, $"the intrinsic 'TypeMetadata.{call.Function.Name}'");
            }

            if (Borrow(receiver) is not { } metadata) {
                return null;
            }

            if (Borrow(operand) is not { } value) {
                End(metadata);
                return null;
            }

            CheckDescribes(metadata, value.Type);
            var copy = Emit(new BuiltinInst("value_witness_copy", value.Type, metadata, value));
            End(value);
            SilValue result = copy;
            switch (call.Function.Name) {
                case "move":
                    result = Emit(new BuiltinInst("value_witness_take", copy.Type, metadata, copy));
                    break;
                case "destroyCopy":
                    Emit(new BuiltinInst("value_witness_destroy", SilType.Unit, metadata, copy));
                    result = UnitValue.Instance;
                    break;
            }

            End(metadata);
            return result;
        }

        /// <summary><c>m.assign(value, to: &amp;target)</c>: the value copied over the target by the metadata's assignWithCopy.</summary>
        SilValue? Assign(BoundCall call, BoundExpression receiver) {
            if (call.Arguments is not [var operand, var target]) {
                return Missing(call, "'TypeMetadata.assign' without a value and a target");
            }

            if (Borrow(receiver) is not { } metadata) {
                return null;
            }

            if (Borrow(operand) is not { } value) {
                End(metadata);
                return null;
            }

            var borrows = new List<SilValue>();
            if (Address(target, borrows) is not { } address) {
                End(value);
                End(metadata);
                EndAll(borrows);
                return null;
            }

            CheckDescribes(metadata, value.Type);
            Emit(new BuiltinInst("value_witness_assign", SilType.Unit, metadata, address, value));
            EndAll(borrows);
            End(value);
            End(metadata);
            return UnitValue.Instance;
        }

        /// <summary>Traps unless the metadata is the type's: a value witness given another type's value would misread it.</summary>
        void CheckDescribes(SilValue metadata, SilType type) {
            var same = Emit(new BuiltinInst("metadata_equal", SilType.Bool, metadata, Emit(new MetadataInst(type))));
            var other = Emit(new BuiltinInst("xor_Bool", SilType.Bool, same, Emit(new IntegerLiteralInst(SilType.Bool, 1))));
            Emit(new CondFailInst(other, "the metadata is not of the value's type"));
        }
    }
}
