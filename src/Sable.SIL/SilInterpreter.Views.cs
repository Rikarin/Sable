namespace Sable.SIL;

/// <summary>
///     A slice's bytes seen as another plain type's, interpreted
///     (<c>slice_reinterpret</c>, Array+Views.sb).
/// </summary>
/// <remarks>
///     Natively the view is the same buffer. Here a buffer holds values, not
///     bytes, so the run is written out as its bytes, little endian, as
///     natively, and read back as the other type's values, into a buffer of
///     its own: what may be seen through it is the same, and a view written
///     where the receiver lends its storage is read back the same way before
///     it is stored over the receiver's elements. The checks are the native
///     ones, on the run's bytes and where they start in their buffer.
/// </remarks>
public sealed partial class SilInterpreter {
    object? Reinterpret(BuiltinInst builtin, object?[] operands) {
        var from = Concrete(((SliceType)builtin.Operands[0].Type).Element);
        var to = Concrete(((SliceType)builtin.Type).Element);
        var (buffer, start, count) = ElementsOf(operands[0]);
        var (fromStride, toStride) = ((long)operands[1]!, (long)operands[2]!);
        var firstByte = start * fromStride;
        var byteCount = count * fromStride;
        if (byteCount % toStride != 0) {
            throw new SilTrap("a slice reinterpreted as a type its bytes are not a whole number of");
        }

        if (firstByte % toStride != 0) {
            throw new SilTrap("a slice reinterpreted as a type it does not start at a multiple of the stride of");
        }

        if (buffer is null || byteCount == 0) {
            return SliceValue.Empty;
        }

        var bytes = new byte[byteCount];
        for (var i = 0; i < count; i++) {
            WritePlain(from, Alive(buffer).Elements[start + i], bytes, (int)(i * fromStride));
        }

        var viewed = (int)(byteCount / toStride);
        var elements = new List<object?>(viewed);
        for (var i = 0; i < viewed; i++) {
            elements.Add(ReadPlain(to, bytes, (int)(i * toStride)));
        }

        // Natively the same buffer: no allocation, as a program that counts them sees.
        var view = Allocate(new HeapArray(new ArrayType(to), elements));
        Allocations--;
        return new SliceValue(view, 0, viewed);
    }

    /// <summary>A plain value's bytes, written where they go: a scalar's, a struct's fields', an inline array's elements'.</summary>
    static void WritePlain(SilType type, object? value, byte[] into, int at) {
        switch (type) {
            case StructType structType when value is object?[] fields:
                for (var i = 0; i < structType.Fields.Count; i++) {
                    WritePlain(structType.Fields[i].Type, fields[i], into, at + TypeLayout.FieldOffset(structType, i));
                }

                return;

            case FixedArrayType inline when value is object?[] elements:
                var stride = TypeLayout.Stride(inline.Element);
                var first = at + TypeLayout.ElementsOffset(inline);
                for (var i = 0; i < inline.Count; i++) {
                    WritePlain(inline.Element, elements[i], into, first + i * stride);
                }

                return;

            default:
                // A hole is no value: its bytes are zero, as nothing wrote them.
                if (value is not null && Encode(type, value) is { } bytes) {
                    bytes.CopyTo(into, at);
                }

                return;
        }
    }

    /// <summary>A plain value read from its bytes.</summary>
    static object? ReadPlain(SilType type, byte[] from, int at) {
        switch (type) {
            case StructType structType: {
                var fields = new object?[structType.Fields.Count];
                for (var i = 0; i < fields.Length; i++) {
                    fields[i] = ReadPlain(structType.Fields[i].Type, from, at + TypeLayout.FieldOffset(structType, i));
                }

                return fields;
            }

            case FixedArrayType inline: {
                var stride = TypeLayout.Stride(inline.Element);
                var first = at + TypeLayout.ElementsOffset(inline);
                var elements = new object?[inline.Count];
                for (var i = 0; i < elements.Length; i++) {
                    elements[i] = ReadPlain(inline.Element, from, first + i * stride);
                }

                return elements;
            }

            default:
                return Decode(type, from.AsSpan(at, TypeLayout.Size(type)));
        }
    }
}
