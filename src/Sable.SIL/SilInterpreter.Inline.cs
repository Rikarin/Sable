using System.Text;

namespace Sable.SIL;

/// <summary>
///     Inline layout, interpreted (docs/02 §2.5): the same bytes the native
///     runtime keeps, walked the same way — so that the two agree on every
///     count, every offset and every refusal.
/// </summary>
public sealed partial class SilInterpreter {
    /// <summary>A block: the bytes of one inline value, counted and copied on write.</summary>
    sealed class InlineBlock(List<byte> bytes) : Counted {
        public List<byte> Bytes { get; } = bytes;
    }

    /// <summary>A view: where in a block something inline is. Null is a null reference.</summary>
    sealed record InlineView(InlineBlock Block, int Offset);

    static uint ReadUnsigned(List<byte> bytes, int at, int width) {
        uint value = 0;
        for (var i = 0; i < width; i++) {
            value |= (uint)bytes[at + i] << (8 * i);
        }

        return value;
    }

    static void WriteUnsigned(List<byte> bytes, int at, int width, long value) {
        for (var i = 0; i < width; i++) {
            bytes[at + i] = (byte)(value >> (8 * i));
        }
    }

    static (long Value, int Size) ReadVarint(List<byte> bytes, int at) {
        long value = 0;
        var i = 0;
        do {
            value |= (long)(bytes[at + i] & 0x7F) << (7 * i);
        } while ((bytes[at + i++] & 0x80) != 0);

        return (value, i);
    }

    static byte[] Varint(long value) {
        var result = new List<byte>();
        do {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            result.Add((byte)(b | (value != 0 ? 0x80 : 0)));
        } while (value != 0);

        return [..result];
    }

    static int ReferenceOffset(List<byte> bytes, int at, int width) =>
        width switch {
            1 => (sbyte)bytes[at],
            2 => (short)ReadUnsigned(bytes, at, 2),
            _ => (int)ReadUnsigned(bytes, at, 4),
        };

    static (long Count, int Elements) ArrayCount(InlineLayout.Slot slot, List<byte> bytes, int at) {
        if (slot.Kind == InlineLayout.BoundedKind) {
            return (ReadUnsigned(bytes, at, slot.Width), at + slot.Width);
        }

        var (count, size) = ReadVarint(bytes, at);
        return (count, at + size);
    }

    static int SkipElement(InlineLayout.Slot slot, List<byte> bytes, int at) =>
        slot.ElementKind == InlineLayout.StructKind ? SkipValue(InlineLayout.Of(slot.Layout!), bytes, at) : at + slot.ElementWidth;

    static int SkipSlot(InlineLayout.Slot slot, List<byte> bytes, int at) {
        if (slot.Kind == InlineLayout.StructKind) {
            return SkipValue(InlineLayout.Of(slot.Layout!), bytes, at);
        }

        var (count, element) = ArrayCount(slot, bytes, at);
        for (var i = 0L; i < count; i++) {
            element = SkipElement(slot, bytes, element);
        }

        return element;
    }

    static int SkipValue(InlineLayout layout, List<byte> bytes, int at) {
        if (layout.FixedSize is { } size) {
            return at + size;
        }

        var q = at + layout.Prefix;
        foreach (var slot in layout.Slots) {
            if (!slot.IsFixed) {
                q = SkipSlot(slot, bytes, q);
            }
        }

        return q;
    }

    static int EmptySize(InlineLayout layout) =>
        layout.Prefix + layout.Slots.Where(static s => !s.IsFixed).Sum(static s =>
            s.Kind switch {
                InlineLayout.BoundedKind => s.Width,
                InlineLayout.GrowKind => 1,
                _ => EmptySize(InlineLayout.Of(s.Layout!)),
            });

    static int FieldOffset(InlineLayout layout, List<byte> bytes, int at, int slot) {
        if (layout.Slots[slot].IsFixed) {
            return at + layout.Slots[slot].Offset;
        }

        var q = at + layout.Prefix;
        for (var i = 0; i < slot; i++) {
            if (!layout.Slots[i].IsFixed) {
                q = SkipSlot(layout.Slots[i], bytes, q);
            }
        }

        return q;
    }

    InlineBlock NewBlock(IEnumerable<byte> bytes, bool onStack = false) => Allocate(new InlineBlock([..bytes]), onStack);

    /// <summary>The block at an address, made the only reference to its bytes.</summary>
    InlineBlock Unique(IReference slot, StructType type, int depth) {
        var block = Alive((InlineBlock)slot.Get()!);
        if (block.Strong == 1 && !block.Immortal) {
            return block;
        }

        var copy = NewBlock(block.Bytes);
        Release(type, block, depth);
        slot.Set(copy);
        return copy;
    }

    sealed record Link(int At, int Target, int Width);

    static void Collect(InlineLayout layout, List<byte> bytes, int at, List<Link> links) {
        var q = at + layout.Prefix;
        foreach (var slot in layout.Slots) {
            var field = slot.IsFixed ? at + slot.Offset : q;
            switch (slot.Kind) {
                case InlineLayout.ReferenceKind when ReferenceOffset(bytes, field, slot.Width) is var offset and not 0:
                    links.Add(new(field, field + offset, slot.Width));
                    break;
                case InlineLayout.StructKind:
                    Collect(InlineLayout.Of(slot.Layout!), bytes, field, links);
                    break;
                case InlineLayout.BoundedKind or InlineLayout.GrowKind: {
                    var (count, element) = ArrayCount(slot, bytes, field);
                    for (var i = 0L; i < count; i++) {
                        if (slot.ElementKind == InlineLayout.StructKind) {
                            Collect(InlineLayout.Of(slot.Layout!), bytes, element, links);
                        }

                        element = SkipElement(slot, bytes, element);
                    }

                    break;
                }
            }

            if (!slot.IsFixed) {
                q = SkipSlot(slot, bytes, q);
            }
        }
    }

    /// <summary>As the runtime's splice: bytes replaced, what follows moved, every relative reference kept pointing where it did.</summary>
    void Splice(IReference slot, InlineLayout root, int at, int removed, byte[] inserted, int depth) {
        var block = Unique(slot, root.Type, depth);
        var bytes = block.Bytes;
        var links = new List<Link>();
        Collect(root, bytes, 0, links);

        var delta = inserted.Length - removed;
        bytes.RemoveRange(at, removed);
        bytes.InsertRange(at, inserted);

        foreach (var link in links) {
            if (link.At >= at && link.At < at + removed) {
                continue;
            }

            if (link.Target >= at && link.Target < at + removed && removed > 0) {
                throw new SilTrap("a relative reference's target was replaced");
            }

            var from = link.At >= at + removed ? link.At + delta : link.At;
            var to = link.Target >= at + removed ? link.Target + delta : link.Target;
            var offset = to - from;
            if ((link.Width == 2 && offset is < short.MinValue or > short.MaxValue) || (link.Width == 1 && offset is < sbyte.MinValue or > sbyte.MaxValue)) {
                throw new SilTrap("a relative reference no longer fits its width");
            }

            WriteUnsigned(bytes, from, link.Width, offset);
        }
    }

    static InlineView View(object? value) =>
        value switch {
            InlineView view => view,
            InlineBlock block => new(Alive(block), 0),
            _ => throw new SilTrap("use of a null inline reference"),
        };

    static StructType InlineOf(SilType type) =>
        type switch {
            InlinePointerType { Pointee: StructType inline } => inline,
            AddressType { Pointee: StructType inline } => inline,
            _ => (StructType)type,
        };

    static byte[] ValueBytes(InlineLayout layout, InlineView view) {
        var end = SkipValue(layout, view.Block.Bytes, view.Offset);
        return [..view.Block.Bytes.GetRange(view.Offset, end - view.Offset)];
    }

    static object LoadScalar(SilType type, List<byte> bytes, int at) {
        var width = InlineLayout.Scalar(type)!.Value.Width;
        ulong raw = 0;
        for (var i = 0; i < width; i++) {
            raw |= (ulong)bytes[at + i] << (8 * i);
        }

        if (type == SilType.Bool) {
            return raw != 0;
        }

        if (type == SilType.Char) {
            return new Rune((int)raw);
        }

        if (type == SilType.F64) {
            return BitConverter.Int64BitsToDouble((long)raw);
        }

        if (type == SilType.F32) {
            return BitConverter.Int32BitsToSingle((int)raw);
        }

        var (bits, signed) = SilType.IntegerInfo(type)!.Value;
        if (!signed) {
            return type == SilType.U64 || type == SilType.UInt ? raw : (object)(long)raw;
        }

        var shift = 64 - bits;
        return (long)raw << shift >> shift;
    }

    static byte[] ScalarBytes(SilType type, object? value) {
        var width = InlineLayout.Scalar(type)!.Value.Width;
        ulong raw = value switch {
            bool b => b ? 1UL : 0UL,
            Rune r => (ulong)r.Value,
            double d => (ulong)BitConverter.DoubleToInt64Bits(d),
            float f => (uint)BitConverter.SingleToInt32Bits(f),
            long l => unchecked((ulong)l),
            ulong u => u,
            _ => throw new SilTrap($"not a scalar: {value}"),
        };

        var bytes = new byte[width];
        for (var i = 0; i < width; i++) {
            bytes[i] = (byte)(raw >> (8 * i));
        }

        return bytes;
    }

    object? Inline(BuiltinInst builtin, object?[] operands, int depth) {
        var name = builtin.Name;
        if (name.StartsWith("inline_field.", StringComparison.Ordinal)) {
            var view = View(operands[0]);
            var layout = InlineLayout.Of(InlineOf(builtin.Operands[0].Type));
            var slot = layout.SlotOf(int.Parse(name["inline_field.".Length..]));
            return view with { Offset = FieldOffset(layout, view.Block.Bytes, view.Offset, slot) };
        }

        switch (name) {
            case "inline_new" or "inline_new_stack":
                return NewBlock(new byte[EmptySize(InlineLayout.Of((StructType)builtin.Type))], name == "inline_new_stack");

            case "inline_base":
                return new InlineView(Alive((InlineBlock)operands[0]!), 0);

            case "inline_unique":
                return new InlineView(Unique((IReference)operands[0]!, InlineOf(builtin.Operands[0].Type), depth), 0);

            case "inline_copy":
                return NewBlock(ValueBytes(InlineLayout.Of((StructType)builtin.Type), View(operands[0])));

            case "inline_load": {
                var view = View(operands[0]);
                return LoadScalar(builtin.Type, view.Block.Bytes, view.Offset);
            }

            case "inline_store": {
                var view = View(operands[0]);
                var bytes = ScalarBytes(builtin.Operands[1].Type, operands[1]);
                for (var i = 0; i < bytes.Length; i++) {
                    view.Block.Bytes[view.Offset + i] = bytes[i];
                }

                return null;
            }

            case "inline_count": {
                var view = View(operands[0]);
                return ArrayCount(ArraySlot(builtin.Operands[0]), view.Block.Bytes, view.Offset).Count;
            }

            case "inline_first": {
                var view = View(operands[0]);
                return view with { Offset = ArrayCount(ArraySlot(builtin.Operands[0]), view.Block.Bytes, view.Offset).Elements };
            }

            case "inline_next": {
                var element = View(operands[1]);
                return element with { Offset = SkipElement(ArraySlot(builtin.Operands[0]), element.Block.Bytes, element.Offset) };
            }

            case "inline_at": {
                var view = View(operands[0]);
                var slot = ArraySlot(builtin.Operands[0]);
                var (count, elements) = ArrayCount(slot, view.Block.Bytes, view.Offset);
                var index = (long)operands[1]!;
                if (index < 0 || index >= count) {
                    throw new SilTrap("array index out of range");
                }

                var size = slot.ElementKind == InlineLayout.StructKind ? InlineLayout.Of(slot.Layout!).FixedSize!.Value : slot.ElementWidth;
                return view with { Offset = elements + (int)index * size };
            }

            case "inline_append": {
                var root = (IReference)operands[0]!;
                var rootLayout = InlineLayout.Of(InlineOf(builtin.Operands[0].Type));
                var slot = ArraySlot(builtin.Operands[1]);
                var array = View(operands[1]);
                var bytes = array.Block.Bytes;
                var (count, elements) = ArrayCount(slot, bytes, array.Offset);
                if (slot.Kind == InlineLayout.BoundedKind && count >= slot.Capacity) {
                    throw new SilTrap("inline array is full");
                }

                var element = InlineLayout.Scalar(builtin.Operands[2].Type) is not null
                    ? ScalarBytes(builtin.Operands[2].Type, operands[2])
                    : ValueBytes(InlineLayout.Of(slot.Layout!), View(operands[2]));
                var end = SkipSlot(slot, bytes, array.Offset);
                Splice(root, rootLayout, end, 0, element, depth);
                var encoded = slot.Kind == InlineLayout.BoundedKind ? CountBytes(slot.Width, count + 1) : Varint(count + 1);
                Splice(root, rootLayout, array.Offset, elements - array.Offset, encoded, depth);
                return null;
            }

            case "inline_assign_string": {
                var root = (IReference)operands[0]!;
                var slot = ArraySlot(builtin.Operands[1]);
                var array = View(operands[1]);
                var text = Encoding.UTF8.GetBytes(Text(operands[2]).Value);
                if (slot.Kind == InlineLayout.BoundedKind && text.Length > slot.Capacity) {
                    throw new SilTrap("too many bytes for the inline array");
                }

                var old = SkipSlot(slot, array.Block.Bytes, array.Offset) - array.Offset;
                var count = slot.Kind == InlineLayout.BoundedKind ? CountBytes(slot.Width, text.Length) : Varint(text.Length);
                Splice(root, InlineLayout.Of(InlineOf(builtin.Operands[0].Type)), array.Offset, old, [..count, ..text], depth);
                return null;
            }

            case "inline_assign": {
                var root = (IReference)operands[0]!;
                var layout = InlineLayout.Of(InlineOf(builtin.Operands[1].Type));
                var field = View(operands[1]);
                var value = ValueBytes(layout, View(operands[2]));
                var old = SkipValue(layout, field.Block.Bytes, field.Offset) - field.Offset;
                Splice(root, InlineLayout.Of(InlineOf(builtin.Operands[0].Type)), field.Offset, old, value, depth);
                return null;
            }

            case "inline_ref_get": {
                var field = View(operands[0]);
                var width = ((RelativeReferenceType)((InlinePointerType)builtin.Operands[0].Type).Pointee).Width;
                var offset = ReferenceOffset(field.Block.Bytes, field.Offset, width);
                return offset == 0 ? null : field with { Offset = field.Offset + offset };
            }

            case "inline_is_null":
                return operands[0] is null;

            case "inline_ref_set": {
                var block = Alive((InlineBlock)((IReference)operands[0]!).Get()!);
                var field = View(operands[1]);
                var target = View(operands[2]);
                var width = ((RelativeReferenceType)((InlinePointerType)builtin.Operands[1].Type).Pointee).Width;
                if (!ReferenceEquals(target.Block, block) || target.Offset < 0 || target.Offset >= block.Bytes.Count) {
                    throw new SilTrap("a relative reference must point into its own block");
                }

                var offset = target.Offset - field.Offset;
                if (offset == 0) {
                    throw new SilTrap("a relative reference cannot point at itself");
                }

                if ((width == 2 && offset is < short.MinValue or > short.MaxValue) || (width == 1 && offset is < sbyte.MinValue or > sbyte.MaxValue)) {
                    throw new SilTrap("a relative reference does not fit its width");
                }

                WriteUnsigned(field.Block.Bytes, field.Offset, width, offset);
                return null;
            }

            case "inline_ref_clear": {
                var field = View(operands[0]);
                WriteUnsigned(field.Block.Bytes, field.Offset, ((RelativeReferenceType)((InlinePointerType)builtin.Operands[0].Type).Pointee).Width, 0);
                return null;
            }

            case "inline_to_string": {
                var array = View(operands[0]);
                var (count, elements) = ArrayCount(ArraySlot(builtin.Operands[0]), array.Block.Bytes, array.Offset);
                return Allocate(new HeapString(DecodeUtf8(array.Block.Bytes.GetRange(elements, (int)count))));
            }

            case "inline_bytes": {
                var bytes = ValueBytes(InlineLayout.Of(InlineOf(builtin.Operands[0].Type)), View(operands[0]));
                return bytes.Length == 0 ? null : Allocate(new HeapArray(new ArrayType(SilType.U8), [..bytes.Select(static b => (object?)(long)b)]));
            }

            case "inline_from": {
                var elements = (operands[0] as HeapArray)?.Elements ?? [];
                return NewBlock(elements.Select(static e => (byte)(long)e!));
            }

            case "inline_invalid": {
                var why = Validate(InlineLayout.Of((StructType)builtin.Operands[0].Type), Alive((InlineBlock)operands[0]!).Bytes);
                return why is null ? EmptyString : Allocate(new HeapString(why));
            }
        }

        throw new SilTrap($"builtin \"{name}\" cannot take these operands");
    }

    static readonly HeapString EmptyString = new("") { Immortal = true };

    static byte[] CountBytes(int width, long count) {
        var bytes = new byte[width];
        for (var i = 0; i < width; i++) {
            bytes[i] = (byte)(count >> (8 * i));
        }

        return bytes;
    }

    static InlineLayout.Slot ArraySlot(SilValue array) => InlineLayout.DescribeArray((InlineArrayType)((InlinePointerType)array.Type).Pointee);

    /// <summary>UTF-8, as the runtime decodes it: each byte of an ill-formed sequence read as U+FFFD.</summary>
    static string DecodeUtf8(List<byte> bytes) {
        var text = new StringBuilder();
        for (var i = 0; i < bytes.Count;) {
            var b = bytes[i];
            var need = b < 0x80 ? 0 : b is >= 0xC2 and <= 0xDF ? 1 : b is >= 0xE0 and <= 0xEF ? 2 : b is >= 0xF0 and <= 0xF4 ? 3 : -1;
            var ok = need >= 0 && i + need < bytes.Count;
            for (var k = 1; ok && k <= need; k++) {
                var c = bytes[i + k];
                var (low, high) = (k, b) switch {
                    (1, 0xE0) => (0xA0, 0xBF),
                    (1, 0xED) => (0x80, 0x9F),
                    (1, 0xF0) => (0x90, 0xBF),
                    (1, 0xF4) => (0x80, 0x8F),
                    _ => (0x80, 0xBF),
                };
                ok = c >= low && c <= high;
            }

            if (ok) {
                text.Append(Encoding.UTF8.GetString([..bytes.GetRange(i, need + 1)]));
                i += need + 1;
            } else {
                text.Append('�');
                i++;
            }
        }

        return text.ToString();
    }

    // ---- Validation, as the runtime's ---------------------------------------

    sealed class Check(List<byte> bytes) {
        public List<byte> Bytes { get; } = bytes;
        public HashSet<(int, InlineLayout)> Places { get; } = [];
        public List<(int At, int Target, InlineLayout Layout)> Links { get; } = [];
        public string? Why;

        public bool Fail(string why) {
            Why ??= why;
            return false;
        }
    }

    /// <summary>Null when the bytes are a valid value of the layout; why not, otherwise.</summary>
    internal static string? Validate(InlineLayout layout, List<byte> bytes) {
        var check = new Check(bytes);
        if (CheckValue(check, layout, 0, out var end, 0) && end != bytes.Count) {
            check.Fail("bytes after the value");
        }

        if (check.Why is null) {
            foreach (var (_, target, targetLayout) in check.Links) {
                if (target < 0 || target >= bytes.Count || !check.Places.Contains((target, targetLayout))) {
                    check.Fail("a relative reference that does not point at a value of its type");
                    break;
                }
            }
        }

        return check.Why;
    }

    static bool CheckScalar(Check check, int kind, int width, int at) {
        if (check.Bytes.Count - at < width) {
            return check.Fail("truncated");
        }

        if (kind == InlineLayout.BoolKind && check.Bytes[at] > 1) {
            return check.Fail("a Bool that is neither 0 nor 1");
        }

        if (kind == InlineLayout.CharKind && ReadUnsigned(check.Bytes, at, 4) is var c && (c > 0x10FFFF || c is >= 0xD800 and <= 0xDFFF)) {
            return check.Fail("a Char that is not a Unicode scalar value");
        }

        return true;
    }

    static bool CheckSlot(Check check, InlineLayout.Slot slot, int at, out int end, int depth) {
        end = at;
        switch (slot.Kind) {
            case InlineLayout.ScalarKind or InlineLayout.BoolKind or InlineLayout.CharKind:
                end = at + slot.Width;
                return CheckScalar(check, slot.Kind, slot.Width, at);

            case InlineLayout.ReferenceKind: {
                if (check.Bytes.Count - at < slot.Width) {
                    return check.Fail("truncated");
                }

                var offset = ReferenceOffset(check.Bytes, at, slot.Width);
                if (offset == 0 && !slot.Optional) {
                    return check.Fail("a relative reference that may not be null is null");
                }

                if (offset != 0) {
                    check.Links.Add((at, at + offset, InlineLayout.Of(slot.Layout!)));
                }

                end = at + slot.Width;
                return true;
            }

            case InlineLayout.StructKind:
                return CheckValue(check, InlineLayout.Of(slot.Layout!), at, out end, depth + 1);

            default: {
                long count;
                int element;
                if (slot.Kind == InlineLayout.BoundedKind) {
                    if (check.Bytes.Count - at < slot.Width) {
                        return check.Fail("truncated");
                    }

                    count = ReadUnsigned(check.Bytes, at, slot.Width);
                    if (count > slot.Capacity) {
                        return check.Fail("an inline array longer than its bound");
                    }

                    element = at + slot.Width;
                } else {
                    count = 0;
                    var i = 0;
                    for (;; i++) {
                        if (i == 5) {
                            return check.Fail("a count that is not a varint");
                        }

                        if (at + i >= check.Bytes.Count) {
                            return check.Fail("truncated");
                        }

                        count |= (long)(check.Bytes[at + i] & 0x7F) << (7 * i);
                        if ((check.Bytes[at + i] & 0x80) == 0) {
                            break;
                        }
                    }

                    if (count > int.MaxValue) {
                        return check.Fail("a count that is not a varint");
                    }

                    element = at + i + 1;
                }

                long least = slot.ElementKind == InlineLayout.StructKind ? EmptySize(InlineLayout.Of(slot.Layout!)) : slot.ElementWidth;
                if (least > 0 ? count > (check.Bytes.Count - element) / least : count > 65536) {
                    return check.Fail("a count longer than the bytes");
                }

                for (var i = 0L; i < count; i++) {
                    if (slot.ElementKind == InlineLayout.StructKind) {
                        if (!CheckValue(check, InlineLayout.Of(slot.Layout!), element, out element, depth + 1)) {
                            return false;
                        }
                    } else {
                        if (!CheckScalar(check, slot.ElementKind, slot.ElementWidth, element)) {
                            return false;
                        }

                        element += slot.ElementWidth;
                    }
                }

                end = element;
                return true;
            }
        }
    }

    static bool CheckValue(Check check, InlineLayout layout, int at, out int end, int depth) {
        end = at;
        if (depth > 64) {
            return check.Fail("nested too deeply");
        }

        if (check.Bytes.Count - at < layout.Prefix) {
            return check.Fail("truncated");
        }

        check.Places.Add((at, layout));
        var q = at + layout.Prefix;
        foreach (var slot in layout.Slots) {
            if (slot.IsFixed) {
                if (!CheckSlot(check, slot, at + slot.Offset, out _, depth)) {
                    return false;
                }
            } else if (!CheckSlot(check, slot, q, out q, depth)) {
                return false;
            }
        }

        end = q;
        return true;
    }
}
