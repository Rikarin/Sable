using Sable.Symbols;

namespace Sable.SIL;

/// <summary>
///     Slices, <c>T[:]</c> (SYNTAX.md §11, docs/02 §2.5): an array of any kind
///     viewed as one, with no copy of its elements.
/// </summary>
/// <remarks>
///     A slice is <c>[T:]</c> — the array's buffer, where its run starts and
///     how many elements it has. Viewing an array retains the buffer, so the
///     slice is a value like any other: it may be stored and returned, and
///     outlives the array it was taken from. The array copies its buffer on
///     its next write while a slice shares it, as it does for another copy of
///     itself; what a slice sees never changes under it. The reads — count,
///     element, a range, a loop — are the array operations, which take a
///     slice as they take an array, the index and the range checked against
///     the slice's own count.
/// </remarks>
public sealed partial class SilGen {
    sealed partial class FunctionLowering {
        /// <summary>
        ///     An array of another kind where a slice of it is wanted: the
        ///     binder records it as an identity conversion, as the types are
        ///     alike to it, but the slice is a value of its own here.
        /// </summary>
        bool IsArrayView(BoundExpression expression) =>
            expression is BoundConversion { ConversionKind: ConversionKind.Identity } conversion
            && Sub(conversion.Type) is ArrayTypeSymbol { ArrayKind: ArrayKind.Slice }
            && Sub(conversion.Operand.Type) is ArrayTypeSymbol { ArrayKind: not ArrayKind.Slice };

        /// <summary><c>sum(xs)</c> of a <c>sum(_: Int[:])</c>: the whole of the array, viewed.</summary>
        SilValue? ArrayView(BoundConversion view) {
            if (IsInlineArray(view.Operand)) {
                return InlineArrayAsSlice(view.Operand);
            }

            if (Borrow(view.Operand) is not { } array) {
                return null;
            }

            if (array.Type is not ArrayType arrayType) {
                End(array);
                return Missing(view, $"'{view.Operand.Type.ToDisplayString()}' as a slice");
            }

            var slice = Emit(new BuiltinInst("slice_of", new SliceType(arrayType.Element), array));
            End(array);
            return slice;
        }

        /// <summary>An owned array, given up for a slice of the whole of it.</summary>
        SilValue WholeSlice(SilValue array) {
            var slice = Emit(new BuiltinInst("slice_of", new SliceType(((ArrayType)array.Type).Element), array));
            Destroy(array);
            return slice;
        }

        /// <summary>
        ///     A receiver passed to a member of <c>Element[:]</c>, which every
        ///     kind of array has: the array, viewed, held with the call's
        ///     borrows; anything else as it is.
        /// </summary>
        SilValue AsSliceReceiver(SilValue receiver, SilType wanted, List<SilValue> borrows) {
            if (receiver.Type is not ArrayType arrayType || wanted is not SliceType) {
                return receiver;
            }

            var slice = Hold(Emit(new BuiltinInst("slice_of", new SliceType(arrayType.Element), receiver)));
            borrows.Add(slice);
            return slice;
        }

        /// <summary>
        ///     An array field of an <c>@inline_layout</c> struct as a slice: its
        ///     elements are bytes in the block, laid out for it rather than as
        ///     a buffer's, so they are copied out — each element read from the
        ///     view, as a loop over the field reads it — into a buffer the slice
        ///     is the whole of.
        /// </summary>
        SilValue? InlineArrayAsSlice(BoundExpression field) {
            var ends = new List<SilValue>();
            if (View(field, ends) is not { Type: InlinePointerType { Pointee: InlineArrayType inlineArray } } array) {
                EndAll(ends);
                return Missing(field, $"'{Describe(field.Syntax)}' as a slice");
            }

            var element = inlineArray.Element;
            var read = InlineOf(element) is not null ? "inline_copy" : "inline_load";
            var arrayType = new ArrayType(element);
            var elementPointer = new InlinePointerType(element);
            var copy = NewSlot(arrayType);
            Emit(new StoreInst(Builtin("array_empty", arrayType), copy, StoreKind.Init));

            var count = Builtin("inline_count", SilType.Int, array);
            var counter = NewSlot(SilType.Int);
            Emit(new StoreInst(Emit(new IntegerLiteralInst(SilType.Int, 0)), counter, StoreKind.Init));
            var cursor = NewSlot(elementPointer);
            Emit(new StoreInst(Builtin("inline_first", elementPointer, array), cursor, StoreKind.Init));

            var header = function.AddBlock();
            var body = function.AddBlock();
            var after = function.AddBlock();
            Branch(header);

            _current = header;
            Jump(new CondBranchInst(Builtin("cmp_lt_Int", SilType.Bool, Emit(new LoadInst(counter)), count), body, [], after, []));

            _current = body;
            var at = Emit(new LoadInst(cursor));
            Builtin("array_append", SilType.Unit, copy, Builtin(read, element, at));
            Emit(new StoreInst(Builtin("inline_next", elementPointer, array, Emit(new LoadInst(cursor))), cursor, StoreKind.Init));
            var one = Emit(new IntegerLiteralInst(SilType.Int, 1));
            Emit(new StoreInst(Builtin("add_Int", SilType.Int, Emit(new LoadInst(counter)), one), counter, StoreKind.Init));
            Branch(header);

            _current = after;
            EndAll(ends);
            return WholeSlice(Emit(new LoadInst(copy, LoadKind.Take)));
        }
    }
}
