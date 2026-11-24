using Sable.Symbols;
using Sable.Syntax;

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
        ///     The place of an array passed to a <c>mutating</c> member of
        ///     <c>Element[:]</c> — <c>xs.swapAt(0, 1)</c>, <c>xs.sort()</c> —
        ///     which changes the array where it is. The array is moved out of
        ///     its place and viewed whole, in a slot of the call's own; once
        ///     the call's borrows end, the slice is the array again and is
        ///     moved back. The view is the only reference to the buffer (the
        ///     array gave its own up), so a write through it changes the
        ///     buffer in place rather than copying it, and the buffer comes
        ///     back as it went, with nothing copied; a buffer the array shared
        ///     is copied by the first write, as the array's own write would.
        ///     A slice cannot change its count, so the array keeps its own.
        /// </summary>
        SilValue? AsMutableSliceReceiver(BoundCall call, SilValue place, SilType wanted, List<SilValue> borrows) {
            if (wanted is not AddressType { Pointee: SliceType sliceType }
                || place.Type is AddressType { Pointee: SliceType }) {
                return place;
            }

            if (place.Type is not AddressType { Pointee: ArrayType arrayType }) {
                return Missing(call, $"'{Describe(call.Syntax)}', a mutating member of a slice on an inline array");
            }

            // Taken once every argument is evaluated, as an inout access
            // begins then: 'xs.swapAt(0, xs.count - 1)' reads the array whole.
            // The take comes before the access to the array begins and the
            // array is put back after it ends, so that the access covers the
            // call alone, as it would the array passed inout.
            // A member compiled once for every element type (#297) is given
            // the slot of the array's own slice, seen as its own.
            // The array is made the only reference to its buffer first, once,
            // and moved into the view and back, the buffer neither retained
            // nor released: a write through the view changes the buffer in
            // place with no check of its own once nothing else can share it
            // (HoistUniqueness, 'bytes_store_unique_').
            var view = NewSlot(new SliceType(arrayType.Element));
            borrows.Add(new DeferredStep(() => {
                Emit(new BuiltinInst("array_make_unique", SilType.Unit, place));
                Emit(new StoreInst(Builtin("slice_of_take", new SliceType(arrayType.Element), Emit(new LoadInst(place, LoadKind.Take))), view, StoreKind.Init));
            }));
            borrows.Add(new WriteBack(() =>
                Emit(new StoreInst(Builtin("array_of_slice_take", arrayType, Emit(new LoadInst(view, LoadKind.Take))), place, StoreKind.Init))));
            BeginAccess(place, modify: true, borrows);
            return view.Type == wanted ? view : Emit(new BuiltinInst("opaque_cast", wanted, view));
        }

        /// <summary>
        ///     Where an inout argument is, its access begun among the call's:
        ///     its own place, or, for an array where a slice of it is wanted,
        ///     the array viewed whole.
        /// </summary>
        SilValue? InoutArgument(BoundExpression argument, List<SilValue> borrows) {
            if (argument is BoundConversion view && IsArrayView(argument) && !IsInlineArray(view.Operand)) {
                return WholeArrayInout(view, borrows);
            }

            if (Address(argument, borrows) is not { } address) {
                return null;
            }

            BeginAccess(address, modify: true, borrows);
            return address;
        }

        /// <summary>
        ///     The whole of an array passed as an inout slice, <c>sortAll(&amp;xs)</c>
        ///     of a <c>sortAll(_: inout Int[:])</c>: changed where it is, as by a
        ///     mutating member of a slice (<see cref="AsMutableSliceReceiver" />).
        ///     What a range passed inout is held to, it is held to: the slice
        ///     that comes back has the array's count, or the call traps.
        /// </summary>
        SilValue? WholeArrayInout(BoundConversion view, List<SilValue> borrows) {
            if (Address(view.Operand, borrows) is not { Type: AddressType { Pointee: ArrayType arrayType } } place) {
                return Missing(view, $"'{Describe(view.Syntax)}' as a slice to change");
            }

            var sliceType = new SliceType(arrayType.Element);
            var slot = NewSlot(sliceType, "$slice");
            SilValue? before = null;
            borrows.Add(new DeferredStep(() => {
                Emit(new BuiltinInst("array_make_unique", SilType.Unit, place));
                Emit(new StoreInst(Builtin("slice_of_take", sliceType, Emit(new LoadInst(place, LoadKind.Take))), slot, StoreKind.Init));
                before = Count(slot);
            }));
            borrows.Add(new WriteBack(() => {
                var same = Builtin("cmp_eq_Int", SilType.Bool, Count(slot), before!);
                Emit(new CondFailInst(Builtin("not_Bool", SilType.Bool, same), "a slice of another count given back for the whole of an array"));
                Emit(new StoreInst(Builtin("array_of_slice_take", arrayType, Emit(new LoadInst(slot, LoadKind.Take))), place, StoreKind.Init));
            }));
            BeginAccess(place, modify: true, borrows);
            return slot;

            SilValue Count(SilValue at) {
                var slice = Emit(new LoadBorrowInst(at));
                var count = Builtin("array_count", SilType.Int, slice);
                Emit(new EndBorrowInst(slice));
                return count;
            }
        }

        /// <summary>
        ///     The range of an array's own slicing, <c>xs[1..&lt;3]</c> or, as a
        ///     place, <c>xs[1..&lt;^1]</c>: a range of integers of any shape, or
        ///     one counted from the end. Null for any other indexing.
        /// </summary>
        BoundExpression? SlicingRange(BoundIndexAccess index) =>
            index is { Subscript: null, Indices: [var range] }
            && Sub(index.Receiver.Type) is ArrayTypeSymbol
            && Sub(range.Type) is NamedTypeSymbol rangeType
            && (Core.IsAnyRange(rangeType) || Core.RelativeRange is { } relative && rangeType.Equals(relative))
                ? range
                : null;

        /// <summary>
        ///     The positions a range of an array names, from the first to the
        ///     one past the last; <paramref name="count" /> gives the array's
        ///     count, asked for only when a bound is left out or counted from
        ///     the end. Whether they are within the array is the slicing's to
        ///     check (<c>array_slice</c>, <c>array_store_slice</c>), except a
        ///     range counted from the end, which its own <c>offsets(in:)</c>
        ///     checks, saying so.
        /// </summary>
        (SilValue From, SilValue To)? RangeBounds(BoundExpression rangeIndex, Func<SilValue> count) {
            if (Core.RelativeRange is { } relative && Sub(rangeIndex.Type).Equals(relative)) {
                if (relative.GetMembers("offsets").OfType<FunctionSymbol>().FirstOrDefault() is not { } offsets) {
                    Missing(rangeIndex, "a range counted from the end, with no 'RelativeRange.offsets(in:)'");
                    return null;
                }

                if (Expression(rangeIndex) is not { } range) {
                    return null;
                }

                var held = Temporary(range, rangeIndex.Syntax, rangeIndex.Type, "$range");
                var counted = Temporary(count(), rangeIndex.Syntax, Core.Int, "$count");
                if (Expression(new BoundCall(rangeIndex.Syntax, held, offsets, [counted])) is not { } pair) {
                    return null;
                }

                return (Emit(new TupleExtractInst(pair, 0)), Emit(new TupleExtractInst(pair, 1)));
            }

            SilValue? from, to;
            bool inclusive;
            if (rangeIndex is BoundRange written) {
                from = written.Lower is { } lower ? Expression(lower) : Emit(new IntegerLiteralInst(SilType.Int, 0));
                to = written.Upper is { } upper ? Expression(upper) : count();
                inclusive = written.IsInclusive && written.Upper is not null;
            } else {
                if (Expression(rangeIndex) is not { Type: StructType rangeType } range) {
                    return null;
                }

                // A one-sided range held in a variable has only one of the
                // fields: the start, or the array's count, stands in for the other.
                from = rangeType.FieldIndex("lowerBound") is var lowerIndex and >= 0
                    ? Emit(new StructExtractInst(range, lowerIndex))
                    : Emit(new IntegerLiteralInst(SilType.Int, 0));
                var upperIndex = rangeType.FieldIndex("upperBound");
                to = upperIndex >= 0 ? Emit(new StructExtractInst(range, upperIndex)) : count();
                var definition = ((NamedTypeSymbol)Sub(rangeIndex.Type)).OriginalDefinition;
                inclusive = upperIndex >= 0
                    && (ReferenceEquals(definition, Core.ClosedRange) || ReferenceEquals(definition, Core.PartialRangeThrough));
            }

            if (from is null || to is null) {
                return null;
            }

            if (inclusive) {
                to = Emit(new BuiltinInst("add_Int", SilType.Int, to, Emit(new IntegerLiteralInst(SilType.Int, 1))));
            }

            return (from, to);
        }

        /// <summary>A value made already, kept in a slot of its own and read as a local is: an operand of a call built here.</summary>
        BoundLocalAccess Temporary(SilValue value, SyntaxNode syntax, TypeSymbol type, string name) {
            var slot = NewSlot(value.Type, name);
            Emit(new StoreInst(Owned(value), slot, StoreKind.Init));
            var local = new LocalSymbol(name, Core.Module, null, false);
            _slots[local] = slot;
            return new BoundLocalAccess(syntax, local, type);
        }

        /// <summary>The positions a range names in the array or slice at <paramref name="place" />.</summary>
        (SilValue From, SilValue To)? PlaceBounds(SilValue place, BoundExpression rangeIndex) =>
            RangeBounds(rangeIndex, () => {
                var array = Emit(new LoadInst(place));
                var count = Emit(new BuiltinInst("array_count", SilType.Int, array));
                Destroy(array);
                return count;
            });

        /// <summary>
        ///     A range of an array's elements as a place — <c>f(&amp;xs[1..&lt;3])</c>,
        ///     <c>xs[2...].sort()</c>, <c>xs[..&lt;2][0] = v</c> (SYNTAX.md §5, "A range of an array as a place"):
        ///     there is no slice in the array to change where it is, so the
        ///     range is read out as one, a view sharing the array's buffer,
        ///     into a slot of its own, which is changed instead; once the
        ///     caller ends <paramref name="borrows" />, the slice is stored
        ///     back over the range (<c>array_store_slice</c>). A write through
        ///     the view copies its run first, as the array still holds the
        ///     buffer, and storing it back writes the array's own buffer in
        ///     place; a slice nothing wrote is still the view, and is not
        ///     stored. The slice must come back with the range's count, or
        ///     the store traps: the callee was given a slice, which never
        ///     changes its count, and only replacing it whole can. The array
        ///     is accessed for the call, as it is for an element passed inout.
        /// </summary>
        SilValue? SliceForWriteBack(BoundIndexAccess sliced, BoundExpression rangeIndex, List<SilValue> borrows) {
            if (Address(sliced.Receiver, borrows) is not { Type: AddressType { Pointee: var pointee } } place) {
                return null;
            }

            if (ArrayType.ElementOf(pointee) is not { } element) {
                return Missing(sliced, $"'{Describe(sliced.Syntax)}' as a place");
            }

            if (PlaceBounds(place, rangeIndex) is not var (from, to)) {
                return null;
            }

            var array = Emit(new LoadInst(place));
            var view = Emit(new BuiltinInst("array_slice", new SliceType(element), array, from, to));
            Destroy(array);
            var slot = NewSlot(view.Type, "$slice");
            Emit(new StoreInst(view, slot, StoreKind.Init));

            // Written back after the access ends, as a mutating member of a
            // slice puts the array back (AsMutableSliceReceiver).
            borrows.Add(new WriteBack(() => {
                var changed = Emit(new LoadInst(slot, LoadKind.Take));
                Emit(new BuiltinInst("array_store_slice", SilType.Unit, place, from, to, changed));
                Destroy(changed);
            }));
            BeginAccess(place, modify: true, borrows);
            return slot;
        }

        /// <summary>
        ///     <c>xs[1..&lt;3] = v</c>, of an array that does not replace a range
        ///     (a growable one does, through its RangeReplaceableCollection
        ///     subscript), or of a slice: the elements stored over the range's,
        ///     the value as many as the range has, or a trap.
        /// </summary>
        SilValue? RangeStore(BoundIndexAccess target, BoundExpression rangeIndex, SilValue value) {
            var ends = new List<SilValue>();
            if (Address(target.Receiver, ends) is not { Type: AddressType { Pointee: var pointee } } place) {
                EndAll(ends);
                return null;
            }

            if (ArrayType.ElementOf(pointee) is null || PlaceBounds(place, rangeIndex) is not var (from, to)) {
                EndAll(ends);
                return ArrayType.ElementOf(pointee) is null ? Missing(target, $"storing to '{Describe(target.Syntax)}'") : null;
            }

            Unhold(value);
            var stored = value.Type is ArrayType ? WholeSlice(value) : value;
            Emit(new BuiltinInst("array_store_slice", SilType.Unit, place, from, to, stored));
            Destroy(stored);
            EndAll(ends);
            return UnitValue.Instance;
        }

        /// <summary>
        ///     Among a call's borrows: code to emit just before the call, once
        ///     every argument is evaluated, when its accesses begin.
        /// </summary>
        sealed class DeferredStep(Action run) : SilValue {
            public bool Done { get; set; }
            public void Run() => run();
            public override SilType Type => SilType.Unit;
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
