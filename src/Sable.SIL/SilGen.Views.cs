using Sable.Symbols;

namespace Sable.SIL;

/// <summary>
///     A slice's elements seen as another plain type's, as a place
///     (SYNTAX.md "Views of a slice's bytes", docs/02 §2.5):
///     <c>xs.reinterpreted(as: U8.self)[0] = 1</c>,
///     <c>fill(&amp;xs.reinterpreted(as: F32.self))</c>,
///     <c>xs.reinterpreted(as: F32.self).sort()</c>.
/// </summary>
/// <remarks>
///     The receiver lends itself whole, as to an inout slice — an array made
///     the only reference to its buffer and moved into the view, an inline
///     array lent where it is — and the slice it lends is seen as the other
///     type's in a slot of the access's own. Nothing is copied: the view is
///     the only reference to the buffer, so a write through it changes the
///     receiver's elements in place. Once the access ends the view is seen as
///     the receiver's type again and given back, which costs nothing while it
///     is still the buffer it was; a view given back with another count of
///     bytes traps, as a slice of another count given back for an array does.
/// </remarks>
public sealed partial class SilGen {
    sealed partial class FunctionLowering {
        SilValue? ReinterpretedPlace(BoundCall call, List<SilValue> borrows) {
            var receiver = call.Receiver!;
            if (Sub(receiver.Type) is not ArrayTypeSymbol { ElementType: var element } viewed
                || Sub(call.Type) is not ArrayTypeSymbol { ElementType: var target }
                || LowerType(call.Type) is not SliceType viewType) {
                return Missing(call, $"'{Describe(call.Syntax)}' as a place");
            }

            // The receiver as a slice to change, its access among the borrows.
            var lent = receiver switch {
                BoundConversion { ConversionKind: ConversionKind.Identity } converted when IsArrayView(converted) => receiver,
                BoundConversion { ConversionKind: ConversionKind.Identity, Operand: var same } when Sub(same.Type) is ArrayTypeSymbol { ArrayKind: ArrayKind.Slice } => same,
                _ when viewed.ArrayKind == ArrayKind.Slice => receiver,
                _ => new BoundConversion(receiver.Syntax, new ArrayTypeSymbol(element, ArrayKind.Slice), receiver, ConversionKind.Identity),
            };
            if (InoutArgument(lent, borrows) is not { Type: AddressType { Pointee: SliceType sliceType } } whole) {
                return Missing(call, $"'{Describe(receiver.Syntax)}' lent to be seen as another type's elements");
            }

            var view = NewSlot(viewType, "$view");
            SilValue? fromStride = null, toStride = null, before = null;
            borrows.Add(new DeferredStep(() => {
                fromStride = StrideOf(element);
                toStride = StrideOf(target);
                var slice = Emit(new LoadInst(whole, LoadKind.Take));
                before = Builtin("array_count", SilType.Int, slice);
                var seen = Builtin("slice_reinterpret", viewType, slice, fromStride, toStride);
                Destroy(slice);
                Emit(new StoreInst(seen, view, StoreKind.Init));
            }));
            borrows.Add(new WriteBack(() => {
                var changed = Emit(new LoadInst(view, LoadKind.Take));
                var back = Builtin("slice_reinterpret", sliceType, changed, toStride!, fromStride!);
                Destroy(changed);
                var same = Builtin("cmp_eq_Int", SilType.Bool, Builtin("array_count", SilType.Int, back), before!);
                Emit(new CondFailInst(Builtin("not_Bool", SilType.Bool, same), "a view of another count of bytes given back for the elements it was a view of"));
                Emit(new StoreInst(back, whole, StoreKind.Init));
            }));
            return view;
        }

        /// <summary>
        ///     <c>&amp;xs[i]</c>, and <c>&amp;p.items[i].v</c>, of a place that
        ///     may be written: an element of an array where it is, through
        ///     stored fields and other elements alone — not a property with
        ///     accessors or a subscript, whose value is read out and written
        ///     back, nor a view, which is given back as the statement ends.
        /// </summary>
        bool IsElementPlace(BoundExpression target) {
            var inElement = false;
            for (var at = target; ;) {
                switch (at) {
                    case BoundIndexAccess { Subscript: null, Indices: [var index], Receiver: var array }
                        when Sub(array.Type) is ArrayTypeSymbol && SlicingRange((BoundIndexAccess)at) is null && !IsInlineArray(array):
                        // In a buffer, which may move; an inline array's element is where the array is.
                        inElement |= LowerType(array.Type) is ArrayType or SliceType;
                        at = array;
                        break;
                    case BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } }
                        when !IsInlineType(receiver.Type) && Sub(receiver.Type).TypeKind is not (TypeKind.Class or TypeKind.Actor):
                        at = receiver;
                        break;
                    case BoundTupleElementAccess element:
                        at = element.Receiver;
                        break;
                    case BoundLocalAccess or BoundParameterAccess or BoundSelfAccess or BoundConversion { ConversionKind: ConversionKind.Dereference }:
                        return inElement;
                    case BoundFieldAccess { Receiver: { } objectField, Field: { IsComputed: false, IsStatic: false } }:
                        return inElement && Sub(objectField.Type).TypeKind is TypeKind.Class or TypeKind.Actor;
                    default:
                        return false;
                }
            }
        }

        /// <summary>
        ///     An element's reference, where it is: the array made the only
        ///     reference to its buffer, the element's address in it, which is
        ///     good until the array next changes — so the reference holds an
        ///     access to the variable it is rooted in until its last use.
        /// </summary>
        SilValue? ElementReference(BoundExpression target) {
            var borrows = new List<SilValue>();
            if (Address(target, borrows) is not { } address) {
                EndAll(borrows);
                return null;
            }

            BeginDeferred(borrows);
            EndAll(borrows);
            function.ExclusiveReferences.Add(address);
            return address;
        }

        /// <summary>A plain type's stride: a constant, or its metadata's where only that knows it (#297).</summary>
        SilValue StrideOf(TypeSymbol type) {
            var lowered = LowerType(type);
            return lowered is not null && !SilType.IsIndirect(lowered) && !SilType.MentionsOpaque(lowered)
                ? Emit(new IntegerLiteralInst(SilType.Int, TypeLayout.Stride(lowered)))
                : Builtin("metadata_stride", SilType.Int, MetadataOf(type)!);
        }

        /// <summary>
        ///     Runs what a place's lowering put off until its access begins —
        ///     the receiver of a view taken, lent — where no call begins it:
        ///     an assignment's target, a reference made of a place.
        /// </summary>
        void BeginDeferred(List<SilValue> borrows) {
            foreach (var borrow in borrows) {
                if (borrow is DeferredStep { Done: false } step) {
                    step.Done = true;
                    step.Run();
                }
            }
        }
    }
}
