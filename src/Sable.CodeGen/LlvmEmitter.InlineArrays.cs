using System.Globalization;
using Sable.SIL;

namespace Sable.CodeGen;

/// <summary>
///     Inline arrays, <c>T[k]</c> and <c>T[..k]</c> (#304, docs/02 §2.5): the
///     array builtins on a value of one — the address of its storage, as every
///     value held in memory is (<see cref="Held" />) — or on its place. A fixed
///     one is <c>[k x T]</c>, its count k; a bounded one <c>{ i64, [k x T] }</c>,
///     its count its first word. Elements are read and written where they
///     are, with no call; what moves runs of them, or copies them out into a
///     buffer of their own, is the runtime's (arrays.c's "Inline arrays").
/// </summary>
/// <remarks>
///     A slice of one is lent where <see cref="LendInlineArrays" /> proved the
///     slice goes nowhere: a header in the frame (<c>sable_lent_array_metadata</c>,
///     count one), whose elements pointer is the array's storage, so that the
///     slice code, which reads a buffer's elements through its header, sees
///     the array where it is and changes it there. Everywhere else, a slice
///     of one is its elements copied out.
/// </remarks>
public sealed partial class LlvmEmitter {
    sealed partial class FunctionEmitter {
        /// <summary>Where an inline array's elements start: its storage, or past a bounded one's count.</summary>
        string FixedElements(string array, FixedArrayType type) {
            if (!type.IsBounded) {
                return array;
            }

            var elements = Fresh();
            Line($"{elements} = getelementptr inbounds {Type(type)}, ptr {array}, i32 0, i32 1");
            return elements;
        }

        /// <summary>How many elements an inline array has: a fixed one's count, a bounded one's first word.</summary>
        string FixedCount(string array, FixedArrayType type) {
            if (!type.IsBounded) {
                return type.Count.ToString(CultureInfo.InvariantCulture);
            }

            var count = Fresh();
            Line($"{count} = load i64, ptr {array}, align 8");
            return count;
        }

        /// <summary>An element's address, its index checked against the count unless proved in range.</summary>
        string FixedElement(string array, FixedArrayType type, string index, bool check) {
            if (check) {
                IndexTraps(index, FixedCount(array, type), 1);
            }

            var element = Fresh();
            Line($"{element} = getelementptr inbounds {PartType(type.Element)}, ptr {FixedElements(array, type)}, i64 {index}");
            return element;
        }

        /// <summary>The object metadata the runtime's array code is given for elements of the type.</summary>
        string ElementsMetadata(SilType element) => ArrayMetadataOf(new ArrayType(element));

        /// <summary>
        ///     A header in the frame that lends a run of elements as a buffer
        ///     (<c>sable_lent_array_metadata</c>): count one, its elements the
        ///     run itself. A slice of it is a view of the run where it is.
        /// </summary>
        string LendHeader(string elements, string count, bool change = false) {
            var header = Scratch("%SableArrayHeader", HeapAlign);
            var typeField = Fresh();
            var strongField = Fresh();
            var flagsField = Fresh();
            var countField = Fresh();
            var capacityField = Fresh();
            var elementsField = Fresh();
            Line($"{typeField} = getelementptr inbounds %SableObjectHeader, ptr {header}, i32 0, i32 0");
            Line($"store ptr @sable_lent_array_metadata, ptr {typeField}");
            Line($"{strongField} = getelementptr inbounds %SableObjectHeader, ptr {header}, i32 0, i32 1");
            Line($"store i32 {(change ? 1 : 2)}, ptr {strongField}");
            // SABLE_STACK: never freed.
            Line($"{flagsField} = getelementptr inbounds %SableObjectHeader, ptr {header}, i32 0, i32 2");
            Line($"store i32 8, ptr {flagsField}");
            Line($"{countField} = getelementptr inbounds %SableArrayHeader, ptr {header}, i32 0, i32 1");
            Line($"store i64 {count}, ptr {countField}, !tbaa {TbaaHeader}");
            Line($"{capacityField} = getelementptr inbounds %SableArrayHeader, ptr {header}, i32 0, i32 2");
            Line($"store i64 {count}, ptr {capacityField}, !tbaa {TbaaHeader}");
            Line($"{elementsField} = getelementptr inbounds %SableArrayHeader, ptr {header}, i32 0, i32 3");
            Line($"store ptr {elements}, ptr {elementsField}, !tbaa {TbaaHeader}");
            return header;
        }

        /// <summary>The range [from, to) checked against a count, as a slice's is.</summary>
        void RangeTraps(string from, string to, string count) {
            var negative = Fresh();
            var past = Fresh();
            var backwards = Fresh();
            var either = Fresh();
            var outside = Fresh();
            Line($"{negative} = icmp slt i64 {from}, 0");
            Line($"{past} = icmp sgt i64 {to}, {count}");
            Line($"{backwards} = icmp sgt i64 {from}, {to}");
            Line($"{either} = or i1 {negative}, {past}");
            Line($"{outside} = or i1 {either}, {backwards}");
            TrapIf(outside, "array slice out of range");
        }

        /// <summary>A value read from an element's place: copied (retained) into its own, or, borrowed, the element where it is.</summary>
        void ReadElement(BuiltinInst builtin, string element, bool borrowed, Func<string> result) {
            var type = builtin.Type;
            if (Held(type)) {
                if (borrowed) {
                    _names[builtin] = element;
                } else {
                    var buffer = OpaqueBuffer(type);
                    _names[builtin] = buffer;
                    OpaqueWitness(type, CopyWitness, buffer, element);
                }

                return;
            }

            if (type == SilType.Unit) {
                _names[builtin] = "zeroinitializer";
                return;
            }

            var value = result();
            Line($"{value} = load {Type(type)}, ptr {element}, !tbaa {TbaaOf(Type(type))}");
            if (!borrowed) {
                Own(true, type, value);
            }
        }

        /// <summary>A value moved out of an element's place, which is left as storage to fill.</summary>
        void TakeElement(BuiltinInst builtin, string element, Func<string> result) {
            var type = builtin.Type;
            if (Held(type)) {
                var buffer = OpaqueBuffer(type);
                _names[builtin] = buffer;
                OpaqueMove(type, buffer, element);
                return;
            }

            if (type == SilType.Unit) {
                _names[builtin] = "zeroinitializer";
                return;
            }

            Line($"{result()} = load {Type(type)}, ptr {element}, !tbaa {TbaaOf(Type(type))}");
        }

        /// <summary>A value stored into an element's place, what was there destroyed after.</summary>
        void AssignElement(SilType type, string element, string value) {
            if (Held(type)) {
                OpaqueWitness(type, AssignWithTakeWitness, element, value);
                return;
            }

            if (type == SilType.Unit) {
                return;
            }

            string? old = null;
            if (!type.IsTrivial) {
                old = Fresh();
                Line($"{old} = load {Type(type)}, ptr {element}");
            }

            Line($"store {Type(type)} {value}, ptr {element}, !tbaa {TbaaOf(Type(type))}");
            if (old is not null) {
                Own(false, type, old);
            }
        }

        /// <summary>
        ///     The array builtins on an inline array, or the inline array's
        ///     own (<c>fixed_*</c>); false for any other builtin, or one on any
        ///     other array.
        /// </summary>
        bool FixedBuiltin(BuiltinInst builtin, string[] operands, Func<string> result) {
            if (builtin.Type is FixedArrayType made) {
                switch (builtin.Name) {
                    // The elements stored in storage of its own; a bounded one's count, how many.
                    case "array_literal" or "array_empty": {
                        var buffer = OpaqueBuffer(made);
                        _names[builtin] = buffer;
                        if (made.IsBounded) {
                            Line($"store i64 {operands.Length}, ptr {buffer}, align 8");
                        }

                        var elements = FixedElements(buffer, made);
                        for (var i = 0; i < operands.Length; i++) {
                            var at = Fresh();
                            Line($"{at} = getelementptr inbounds {PartType(made.Element)}, ptr {elements}, i64 {i}");
                            StoreElement(made.Element, operands[i], at);
                        }

                        return true;
                    }

                    // So many copies of one element: a loop, each copied as an assignment copies it.
                    case "fixed_repeating": {
                        var buffer = OpaqueBuffer(made);
                        _names[builtin] = buffer;
                        var entry = _currentLabel;
                        var loop = NewLabel("repeat");
                        var done = NewLabel("repeated");
                        Line($"br label %{loop}");
                        Continue(loop);
                        var index = Fresh();
                        var next = Fresh();
                        var more = Fresh();
                        Line($"{index} = phi i64 [ 0, %{entry} ], [ {next}, %{loop} ]");
                        var at = Fresh();
                        Line($"{at} = getelementptr inbounds {PartType(made.Element)}, ptr {buffer}, i64 {index}");
                        if (Held(made.Element)) {
                            OpaqueWitness(made.Element, CopyWitness, at, operands[0]);
                        } else if (made.Element != SilType.Unit) {
                            Line($"store {Type(made.Element)} {operands[0]}, ptr {at}");
                            Own(true, made.Element, operands[0]);
                        }

                        Line($"{next} = add nuw nsw i64 {index}, 1");
                        Line($"{more} = icmp slt i64 {next}, {made.Count}");
                        Line($"br i1 {more}, label %{loop}, label %{done}");
                        Continue(done);
                        return true;
                    }
                }
            }

            if (builtin.Name is "fixed_lend_mut" or "fixed_lend_mut_copy" or "fixed_restore"
                && builtin.Operands[0].Type is AddressType { Pointee: FixedArrayType lentPlace }) {
                var elements = FixedElements(operands[0], lentPlace);
                var count = FixedCount(operands[0], lentPlace);
                switch (builtin.Name) {
                    case "fixed_lend_mut":
                        _names[builtin] = MakeSlice((SliceType)builtin.Type, LendHeader(elements, count, change: true), "0", count);
                        return true;

                    case "fixed_lend_mut_copy": {
                        var copy = Fresh();
                        Line($"{copy} = call ptr @sable_array_from_elements(ptr {elements}, i64 {count}, ptr {ElementsMetadata(lentPlace.Element)})");
                        _names[builtin] = MakeSlice((SliceType)builtin.Type, copy, "0", count);
                        return true;
                    }

                    default: {
                        var back = (SliceType)builtin.Operands[1].Type;
                        SiteForRuntime();
                        Line($"call void @sable_inline_restore(ptr {elements}, i64 {count}, ptr {ElementsMetadata(lentPlace.Element)}, ptr {SlicePart(operands[1], back, 0)}, "
                             + $"i64 {SlicePart(operands[1], back, 1)}, i64 {SlicePart(operands[1], back, 2)})");
                        return true;
                    }
                }
            }

            // The rest take the array first: its value, or its place.
            if (builtin.Operands.Count == 0) {
                return false;
            }

            var first = builtin.Operands[0].Type;
            var place = first is AddressType { Pointee: FixedArrayType };
            if ((place ? ((AddressType)first).Pointee : first) is not FixedArrayType type) {
                // An array literal and an empty array were made above; a
                // mismatch or a share between an array and an inline one
                // has the inline one second.
                return false;
            }

            var array = operands[0];
            switch (builtin.Name) {
                case "array_count":
                    _names[builtin] = FixedCount(array, type);
                    return true;

                case "array_capacity":
                    _names[builtin] = type.Count.ToString(CultureInfo.InvariantCulture);
                    return true;

                case "array_get" or "array_get_borrowed":
                    ReadElement(builtin, FixedElement(array, type, operands[1], !builtin.InBounds), builtin.Name == "array_get_borrowed", result);
                    return true;

                case "array_set" or "array_set_unique":
                    AssignElement(type.Element, FixedElement(array, type, operands[1], !builtin.InBounds), operands[2]);
                    return true;

                case "array_element_addr" or "array_element_addr_unique":
                    _names[builtin] = FixedElement(array, type, operands[1], !builtin.InBounds);
                    return true;

                // In place already: nothing shares an inline array's elements.
                case "array_make_unique":
                    return true;

                // Within its bound, and no further.
                case "array_append": {
                    var count = FixedCount(array, type);
                    var full = Fresh();
                    Line($"{full} = icmp sge i64 {count}, {type.Count}");
                    TrapIf(full, "inline array is full");
                    var at = Fresh();
                    Line($"{at} = getelementptr inbounds {PartType(type.Element)}, ptr {FixedElements(array, type)}, i64 {count}");
                    StoreElement(type.Element, operands[1], at);
                    var grown = Fresh();
                    Line($"{grown} = add nuw nsw i64 {count}, 1");
                    Line($"store i64 {grown}, ptr {array}, align 8");
                    return true;
                }

                case "array_remove_last": {
                    var count = FixedCount(array, type);
                    var empty = Fresh();
                    Line($"{empty} = icmp sle i64 {count}, 0");
                    TrapIf(empty, "removeLast of an empty array");
                    var lowered = Fresh();
                    Line($"{lowered} = sub nsw i64 {count}, 1");
                    Line($"store i64 {lowered}, ptr {array}, align 8");
                    var at = Fresh();
                    Line($"{at} = getelementptr inbounds {PartType(type.Element)}, ptr {FixedElements(array, type)}, i64 {lowered}");
                    TakeElement(builtin, at, result);
                    return true;
                }

                case "array_clear":
                    Own(false, type, array);
                    Line($"store i64 0, ptr {array}, align 8");
                    return true;

                case "array_truncate":
                    SiteForRuntime();
                    Line($"call void @sable_inline_truncate(ptr {FixedElements(array, type)}, ptr {array}, ptr {ElementsMetadata(type.Element)}, i64 {operands[1]})");
                    return true;

                case "array_rotate":
                    SiteForRuntime();
                    Line($"call void @sable_inline_rotate(ptr {FixedElements(array, type)}, i64 {FixedCount(array, type)}, ptr {ElementsMetadata(type.Element)}, "
                         + $"i64 {operands[1]}, i64 {operands[2]}, i64 {operands[3]})");
                    return true;

                case "array_store_slice": {
                    var source = (SliceType)builtin.Operands[3].Type;
                    SiteForRuntime();
                    Line($"call void @sable_inline_store_slice(ptr {FixedElements(array, type)}, i64 {FixedCount(array, type)}, ptr {ElementsMetadata(type.Element)}, "
                         + $"i64 {operands[1]}, i64 {operands[2]}, ptr {SlicePart(operands[3], source, 0)}, i64 {SlicePart(operands[3], source, 1)}, i64 {SlicePart(operands[3], source, 2)})");
                    return true;
                }

                // Its elements copied out, into a buffer the slice is the whole of.
                case "slice_of" or "slice_of_borrowed": {
                    var count = FixedCount(array, type);
                    var copy = Fresh();
                    Line($"{copy} = call ptr @sable_array_from_elements(ptr {FixedElements(array, type)}, i64 {count}, ptr {ElementsMetadata(type.Element)})");
                    _names[builtin] = MakeSlice((SliceType)builtin.Type, copy, "0", count);
                    return true;
                }

                case "array_of_slice":
                    Line($"{result()} = call ptr @sable_array_from_elements(ptr {FixedElements(array, type)}, i64 {FixedCount(array, type)}, ptr {ElementsMetadata(type.Element)})");
                    return true;

                // A range of its elements copied out.
                case "array_slice": {
                    RangeTraps(operands[1], operands[2], FixedCount(array, type));
                    var start = Fresh();
                    var count = Fresh();
                    var copy = Fresh();
                    Line($"{start} = getelementptr inbounds {PartType(type.Element)}, ptr {FixedElements(array, type)}, i64 {operands[1]}");
                    Line($"{count} = sub nsw i64 {operands[2]}, {operands[1]}");
                    Line($"{copy} = call ptr @sable_array_from_elements(ptr {start}, i64 {count}, ptr {ElementsMetadata(type.Element)})");
                    _names[builtin] = MakeSlice((SliceType)builtin.Type, copy, "0", count);
                    return true;
                }

                // Lent: a view of the storage itself, for as long as nothing can change it.
                case "fixed_lend": {
                    var count = FixedCount(array, type);
                    _names[builtin] = MakeSlice((SliceType)builtin.Type, LendHeader(FixedElements(array, type), count), "0", count);
                    return true;
                }

                case "fixed_lend_slice": {
                    RangeTraps(operands[1], operands[2], FixedCount(array, type));
                    var start = Fresh();
                    var count = Fresh();
                    Line($"{start} = getelementptr inbounds {PartType(type.Element)}, ptr {FixedElements(array, type)}, i64 {operands[1]}");
                    Line($"{count} = sub nsw i64 {operands[2]}, {operands[1]}");
                    _names[builtin] = MakeSlice((SliceType)builtin.Type, LendHeader(start, count), "0", count);
                    return true;
                }

                default:
                    return false;
            }
        }

        /// <summary>
        ///     An operand of <c>array_mismatch</c> or <c>array_shares</c> that
        ///     is an inline array, as the run of elements the runtime compares:
        ///     lent through a header, its buffer for the call.
        /// </summary>
        string FixedRun(string array, FixedArrayType type) {
            var count = FixedCount(array, type);
            return $"ptr {LendHeader(FixedElements(array, type), count)}, i64 0, i64 {count}";
        }
    }
}
