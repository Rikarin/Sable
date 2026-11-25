namespace Sable.SIL;

/// <summary>
///     Inline arrays interpreted (#304): a value of <c>T[k]</c> is an array of
///     its k elements, of <c>T[..k]</c> one of k places and then its count —
///     a value like a struct's fields, held where it is and copied whole, an
///     element's address the place in it (<see cref="Element" />). A slice of
///     one is its elements copied into a buffer, counted as an allocation as
///     natively, unless lent (<c>fixed_lend</c>): then the buffer is the
///     frame's, as natively the lent header is, and counts nothing.
/// </summary>
public sealed partial class SilInterpreter {
    /// <summary>How many elements an inline array value has: a fixed one's count, a bounded one's last place.</summary>
    static int InlineCount(FixedArrayType type, object?[] value) => type.IsBounded ? (int)(long)value[type.Count]! : type.Count;

    /// <summary>An inline array value's places: k, and a bounded one's count after them.</summary>
    static object?[] NewInline(FixedArrayType type, int count) {
        var made = new object?[type.IsBounded ? type.Count + 1 : type.Count];
        if (type.IsBounded) {
            made[type.Count] = (long)count;
        }

        return made;
    }

    /// <summary>The inline array value at an address, or one with nothing in it yet.</summary>
    static object?[] InlineAt(FixedArrayType type, IReference address) => address.Peek() as object?[] ?? NewInline(type, 0);

    /// <summary>
    ///     Its elements in a buffer of their own, each copied; one lent to
    ///     change is the frame's, and counts no allocation. One lent to read
    ///     (<paramref name="borrowed" />) holds them as they are, without a
    ///     count of its own: nothing destroys it, as nothing destroys the
    ///     header natively.
    /// </summary>
    HeapArray InlineBuffer(FixedArrayType type, object?[] value, int from, int count, bool lent, bool borrowed = false) {
        var elements = new List<object?>(count);
        for (var i = 0; i < count; i++) {
            if (!borrowed) {
                Retain(type.Element, value[from + i]);
            }

            elements.Add(value[from + i]);
        }

        var buffer = new HeapArray(new ArrayType(type.Element), elements);
        if (borrowed) {
            buffer.Immortal = true;
            buffer.OnStack = true;
            return buffer;
        }

        return Allocate(buffer, onStack: lent);
    }

    /// <summary>The builtins of an inline array; false for any other.</summary>
    bool InlineArrayBuiltin(BuiltinInst builtin, object?[] operands, int depth, out object? result) {
        result = null;
        if (builtin.Type is FixedArrayType made) {
            switch (builtin.Name) {
                case "array_literal" or "array_empty": {
                    var value = NewInline(made, operands.Length);
                    Array.Copy(operands, value, operands.Length);
                    result = value;
                    return true;
                }

                case "fixed_repeating": {
                    var value = NewInline(made, made.Count);
                    for (var i = 0; i < made.Count; i++) {
                        Retain(made.Element, operands[0]);
                        value[i] = operands[0];
                    }

                    result = value;
                    return true;
                }
            }
        }

        if (builtin.Operands.Count == 0) {
            return false;
        }

        var first = builtin.Operands[0].Type;
        if (first is AddressType { Pointee: FixedArrayType placeType }) {
            var address = (IReference)operands[0]!;
            var element = placeType.Element;
            switch (builtin.Name) {
                case "array_set" or "array_set_unique": {
                    var value = InlineAt(placeType, address);
                    var at = CheckedIndex((long)operands[1]!, InlineCount(placeType, value));
                    var changed = (object?[])value.Clone();
                    var old = changed[at];
                    changed[at] = operands[2];
                    address.Set(changed);
                    Release(element, old, depth);
                    return true;
                }

                case "array_element_addr" or "array_element_addr_unique": {
                    var value = InlineAt(placeType, address);
                    result = new Element(address, CheckedIndex((long)operands[1]!, InlineCount(placeType, value)), value.Length);
                    return true;
                }

                case "array_make_unique":
                    return true;

                case "array_append": {
                    var value = InlineAt(placeType, address);
                    var count = InlineCount(placeType, value);
                    if (count >= placeType.Count) {
                        throw new SilTrap("inline array is full");
                    }

                    var changed = (object?[])value.Clone();
                    changed[count] = operands[1];
                    changed[placeType.Count] = (long)(count + 1);
                    address.Set(changed);
                    return true;
                }

                case "array_remove_last": {
                    var value = InlineAt(placeType, address);
                    var count = InlineCount(placeType, value);
                    if (count == 0) {
                        throw new SilTrap("removeLast of an empty array");
                    }

                    var changed = (object?[])value.Clone();
                    result = changed[count - 1];
                    changed[count - 1] = null;
                    changed[placeType.Count] = (long)(count - 1);
                    address.Set(changed);
                    return true;
                }

                case "array_clear" or "array_truncate": {
                    var value = InlineAt(placeType, address);
                    var count = InlineCount(placeType, value);
                    var keep = builtin.Name == "array_clear" ? 0 : (long)operands[1]!;
                    if (keep < 0 || keep > count) {
                        throw new SilTrap("cannot keep more elements than the array has");
                    }

                    var changed = (object?[])value.Clone();
                    for (var i = (int)keep; i < count; i++) {
                        Release(element, changed[i], depth);
                        changed[i] = null;
                    }

                    changed[placeType.Count] = keep;
                    address.Set(changed);
                    return true;
                }

                case "array_rotate": {
                    var value = InlineAt(placeType, address);
                    var count = InlineCount(placeType, value);
                    var (from, to, by) = ((long)operands[1]!, (long)operands[2]!, (long)operands[3]!);
                    if (from < 0 || to > count || from > to) {
                        throw new SilTrap("array slice out of range");
                    }

                    var length = (int)(to - from);
                    var shift = length == 0 ? 0 : (int)(((by % length) + length) % length);
                    if (shift != 0) {
                        var changed = (object?[])value.Clone();
                        for (var i = 0; i < length; i++) {
                            changed[from + i] = value[from + (i + shift) % length];
                        }

                        address.Set(changed);
                    }

                    return true;
                }

                case "array_store_slice": {
                    var value = InlineAt(placeType, address);
                    var count = InlineCount(placeType, value);
                    var (from, to) = ((long)operands[1]!, (long)operands[2]!);
                    if (from < 0 || to > count || from > to) {
                        throw new SilTrap("array slice out of range");
                    }

                    var (source, sourceStart, sourceCount) = ElementsOf(operands[3]);
                    if (sourceCount != to - from) {
                        throw new SilTrap($"a slice of count {sourceCount} stored over a range of count {to - from}");
                    }

                    StoreRun(placeType, address, value, (int)from, source, sourceStart, sourceCount, depth);
                    return true;
                }

                // Lent to change: a buffer of the elements, the frame's; what
                // comes back is stored over them, as natively.
                case "fixed_lend_mut" or "fixed_lend_mut_copy": {
                    var value = InlineAt(placeType, address);
                    var count = InlineCount(placeType, value);
                    result = count == 0 ? SliceValue.Empty : new SliceValue(InlineBuffer(placeType, value, 0, count, builtin.Name == "fixed_lend_mut"), 0, count);
                    return true;
                }

                case "fixed_restore": {
                    var value = InlineAt(placeType, address);
                    var count = InlineCount(placeType, value);
                    var (source, sourceStart, sourceCount) = ElementsOf(operands[1]);
                    if (sourceCount != count) {
                        throw new SilTrap($"a slice of count {sourceCount} stored over a range of count {count}");
                    }

                    StoreRun(placeType, address, value, 0, source, sourceStart, sourceCount, depth);
                    Release(builtin.Operands[1].Type, operands[1], depth);
                    return true;
                }
            }

            return false;
        }

        if (first is not FixedArrayType type) {
            return false;
        }

        var array = (object?[])operands[0]!;
        switch (builtin.Name) {
            case "array_count":
                result = (long)InlineCount(type, array);
                return true;

            case "array_capacity":
                result = (long)type.Count;
                return true;

            case "array_get" or "array_get_borrowed": {
                var element = array[CheckedIndex((long)operands[1]!, InlineCount(type, array))];
                if (builtin.Name == "array_get") {
                    Retain(builtin.Type, element);
                }

                result = element;
                return true;
            }

            // Its elements copied out into a buffer the slice is the whole of;
            // lent, a buffer of the frame's.
            case "slice_of" or "slice_of_borrowed" or "fixed_lend": {
                var count = InlineCount(type, array);
                result = count == 0 ? SliceValue.Empty : new SliceValue(InlineBuffer(type, array, 0, count, builtin.Name == "fixed_lend", borrowed: builtin.Name == "fixed_lend"), 0, count);
                return true;
            }

            case "array_slice" or "fixed_lend_slice": {
                var (from, to) = ((long)operands[1]!, (long)operands[2]!);
                if (from < 0 || to > InlineCount(type, array) || from > to) {
                    throw new SilTrap("array slice out of range");
                }

                var count = (int)(to - from);
                result = count == 0 ? SliceValue.Empty : new SliceValue(InlineBuffer(type, array, (int)from, count, builtin.Name == "fixed_lend_slice", borrowed: builtin.Name == "fixed_lend_slice"), 0, count);
                return true;
            }

            case "array_of_slice": {
                var count = InlineCount(type, array);
                result = count == 0 ? null : InlineBuffer(type, array, 0, count, lent: false);
                return true;
            }

            case "array_shares":
                result = false;
                return true;

            case "array_mismatch": {
                if (!SilBuiltins.ComparesByBits(type.Element)) {
                    result = -1L;
                    return true;
                }

                var (other, otherStart, otherCount) = builtin.Operands[1].Type is FixedArrayType otherType
                    ? (null, 0, InlineCount(otherType, (object?[])operands[1]!))
                    : ElementsOf(operands[1]);
                var shorter = Math.Min(InlineCount(type, array), otherCount);
                var at = 0;
                while (at < shorter && Equals(array[at], other is null ? ((object?[])operands[1]!)[at] : other.Elements[otherStart + at])) {
                    at++;
                }

                result = (long)at;
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>An index checked against a count, as an element's is.</summary>
    static int CheckedIndex(long index, int count) =>
        index < 0 || index >= count ? throw new SilTrap("array index out of range") : (int)index;

    /// <summary>A run of a slice's elements stored over an inline array's from a place, each replaced: nothing when they are the same values.</summary>
    void StoreRun(FixedArrayType type, IReference address, object?[] value, int from, HeapArray? source, int start, int count, int depth) {
        if (count == 0) {
            return;
        }

        var changed = (object?[])value.Clone();
        for (var i = 0; i < count; i++) {
            var stored = source!.Elements[start + i];
            Retain(type.Element, stored);
            var old = changed[from + i];
            changed[from + i] = stored;
            Release(type.Element, old, depth);
        }

        address.Set(changed);
    }
}
