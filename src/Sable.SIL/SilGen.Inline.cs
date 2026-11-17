using Sable.Symbols;

namespace Sable.SIL;

/// <summary>
///     Lowering for <c>@inline_layout</c> structs (docs/02 §2.5, docs/04 §3.6).
/// </summary>
/// <remarks>
///     A value of one is a block: a counted buffer of the bytes, copied on
///     write. As an SSA value it is the block (<c>$Order</c>); inside it,
///     everything is reached through views (<c>$*inline Order</c>) — read
///     with <c>inline_base</c>, written with <c>inline_unique</c>, which makes
///     the block the only reference to its bytes first. A change of size
///     moves what follows, so a view is taken afresh from the block after
///     each one; nothing here holds one across a change. A field, an element
///     or a loop's element is a view into its owner, with no copy; it becomes
///     a block of its own only where a value is wanted.
/// </remarks>
public sealed partial class SilGen {
    sealed partial class FunctionLowering {
        /// <summary>Locals bound to a view rather than a block: a loop's element of an inline array.</summary>
        readonly HashSet<LocalSymbol> _views = [];

        bool IsInlineType(TypeSymbol type) =>
            Sub(type) switch {
                NamedTypeSymbol named => named.OriginalDefinition.IsInlineLayout,
                ReferenceTypeSymbol reference => IsInlineType(reference.ReferencedType),
                _ => false,
            };

        /// <summary>An inline array: a field of an inline struct whose type is an array.</summary>
        bool IsInlineArray(BoundExpression expression) =>
            Unwrapped(expression) is BoundFieldAccess { Receiver: { } receiver, Field.IsComputed: false }
            && IsInlineType(receiver.Type)
            && Sub(expression.Type) is ArrayTypeSymbol;

        /// <summary>A member of an inline struct: what lowers through views.</summary>
        bool IsInlineMember(BoundExpression expression) =>
            Unwrapped(expression) is BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } }
            && IsInlineType(receiver.Type);

        static BoundExpression Unwrapped(BoundExpression expression) =>
            expression is BoundConversion { ConversionKind: ConversionKind.Dereference or ConversionKind.Identity or ConversionKind.Reference } conversion
                ? Unwrapped(conversion.Operand)
                : expression;

        static StructType? InlineOf(SilType type) =>
            type switch {
                StructType { IsInline: true } inline => inline,
                InlinePointerType { Pointee: StructType { IsInline: true } inline } => inline,
                _ => null,
            };

        SilValue Builtin(string name, SilType type, params SilValue[] operands) => Emit(new BuiltinInst(name, type, operands));

        SilValue FieldPointer(SilValue owner, string field) {
            var structType = (StructType)((InlinePointerType)owner.Type).Pointee;
            var index = structType.FieldIndex(field);
            return Builtin($"inline_field.{index}", new InlinePointerType(structType.Fields[index].Type), owner);
        }

        /// <summary>
        ///     Where an inline value, or a part of one, is, to read: a view, with
        ///     what was borrowed to reach it added to <paramref name="ends" />.
        /// </summary>
        SilValue? View(BoundExpression expression, List<SilValue> ends) {
            expression = Unwrapped(expression);
            switch (expression) {
                case BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } field } when IsInlineType(receiver.Type):
                    return View(receiver, ends) is { } owner ? FieldPointer(owner, field.Name) : null;

                case BoundIndexAccess { Receiver: { } receiver, Indices: [var indexExpression] } when IsInlineArray(receiver): {
                    if (View(receiver, ends) is not { } array || Expression(indexExpression) is not { } index) {
                        return null;
                    }

                    return ElementAt(array, index, expression);
                }

                case BoundLocalAccess local when _views.Contains(local.Local):
                    return Emit(new LoadInst(_slots[local.Local]));

                case BoundUnaryOperator { OperatorKind: UnaryOperatorKind.AddressOf, Operand: var target }:
                    return View(target, ends);
            }

            if (Borrow(expression) is not { } value) {
                return null;
            }

            ends.Add(value);
            return value.Type switch {
                InlinePointerType => value,
                StructType { IsInline: true } => Builtin("inline_base", new InlinePointerType(value.Type), value),
                _ => Missing(expression, $"'{Describe(expression.Syntax)}' as inline bytes"),
            };
        }

        SilValue? ElementAt(SilValue array, SilValue index, BoundNode at) {
            var arrayType = (InlineArrayType)((InlinePointerType)array.Type).Pointee;
            return InlineLayout.FixedSizeOf(arrayType.Element) is null
                ? Missing(at, $"indexing an inline array of '{arrayType.Element}', whose elements vary in size — iterate it instead")
                : Builtin("inline_at", new InlinePointerType(arrayType.Element), array, index);
        }

        /// <summary>
        ///     Where an inline value, or a part of one, is, to change: the
        ///     address its block is held at, which a change of size needs, and a
        ///     view into the block, made the only reference to its bytes.
        /// </summary>
        (SilValue Root, SilValue Pointer)? Mutable(BoundExpression expression, List<SilValue> ends) {
            expression = Unwrapped(expression);
            switch (expression) {
                case BoundFieldAccess { Receiver: { } receiver, Field: { IsComputed: false, IsStatic: false } field } when IsInlineType(receiver.Type):
                    return Mutable(receiver, ends) is var (root, owner) ? (root, FieldPointer(owner, field.Name)) : null;

                case BoundIndexAccess { Receiver: { } receiver, Indices: [var indexExpression] } when IsInlineArray(receiver): {
                    if (Expression(indexExpression) is not { } index || Mutable(receiver, ends) is not var (arrayRoot, array)) {
                        return null;
                    }

                    return ElementAt(array, index, expression) is { } element ? (arrayRoot, element) : null;
                }
            }

            if (Address(expression, ends) is not { Type: AddressType { Pointee: StructType { IsInline: true } inline } } address) {
                Missing(expression, $"changing '{Describe(expression.Syntax)}' in place");
                return null;
            }

            return (address, Builtin("inline_unique", new InlinePointerType(inline), address));
        }

        /// <summary>A member of an inline struct, read: a scalar loaded, a struct copied out, a relative reference followed.</summary>
        SilValue? InlineRead(BoundExpression access) {
            var ends = new List<SilValue>();
            if (View(access, ends) is not { } pointer) {
                EndAll(ends);
                return null;
            }

            var result = InlineValue(pointer, access);
            EndAll(ends);
            return result;
        }

        SilValue? InlineValue(SilValue pointer, BoundExpression at) {
            switch (((InlinePointerType)pointer.Type).Pointee) {
                case var scalar when InlineLayout.Scalar(scalar) is not null:
                    return Builtin("inline_load", scalar, pointer);

                case StructType { IsInline: true } inline:
                    return Builtin("inline_copy", inline, pointer);

                case RelativeReferenceType reference: {
                    var target = Builtin("inline_ref_get", new InlinePointerType(reference.Target), pointer);
                    var isNull = Builtin("inline_is_null", SilType.Bool, target);
                    if (LowerType(at.Type) is EnumType { Cases: [("Some", _), ("None", null)] } optional) {
                        var some = function.AddBlock();
                        var none = function.AddBlock();
                        var after = function.AddBlock();
                        var result = after.AddArgument(optional);
                        Jump(new CondBranchInst(isNull, none, [], some, []));
                        _current = some;
                        Branch(after, Emit(new EnumInst(optional, 0, target)));
                        _current = none;
                        Branch(after, Emit(new EnumInst(optional, 1, null)));
                        _current = after;
                        return result;
                    }

                    Emit(new CondFailInst(isNull, "a relative reference that may not be null is null"));
                    return target;
                }

                default:
                    return Missing(at, $"'{Describe(at.Syntax)}' as a value — iterate it, or read its count");
            }
        }

        /// <summary><c>owner.field = value</c>, in place.</summary>
        SilValue? InlineAssign(BoundExpression target, BoundExpression valueExpression) {
            var ends = new List<SilValue>();

            // A reference is to something in the same block: the block is
            // made unique first, so that what it points at is in it.
            if (Unwrapped(target) is BoundFieldAccess { Field.Type: var fieldType } && IsRelative(Sub(fieldType))) {
                if (Mutable(target, ends) is not var (root, field)) {
                    EndAll(ends);
                    return null;
                }

                var referenceEnds = new List<SilValue>();
                var referent = RelativeValue(valueExpression, referenceEnds);
                if (referent is null) {
                    EndAll(referenceEnds);
                    EndAll(ends);
                    return null;
                }

                SetReference(root, field, referent);
                EndAll(referenceEnds);
                EndAll(ends);
                return UnitValue.Instance;
            }

            var value = InlineFieldValue(valueExpression);
            if (value is null) {
                return null;
            }

            Hold(value);
            if (Mutable(target, ends) is not var (slot, pointer)) {
                EndAll(ends);
                End(value);
                return null;
            }

            Unhold(value);
            Put(slot, pointer, value, target);
            EndAll(ends);
            return UnitValue.Instance;
        }

        static bool IsRelative(TypeSymbol type) =>
            type switch {
                OptionalTypeSymbol optional => IsRelative(optional.UnderlyingType),
                ReferenceTypeSymbol { RelativeWidth: not null } => true,
                _ => false,
            };

        /// <summary>What a relative reference is set to: a view of its target, or none.</summary>
        SilValue? RelativeValue(BoundExpression expression, List<SilValue> ends) {
            if (IsNone(expression)) {
                return UnitValue.Instance;
            }

            // '&x', or a reference read from somewhere.
            var inner = expression is BoundConversion { ConversionKind: ConversionKind.OptionalWrap } wrap ? wrap.Operand : expression;
            if (Unwrapped(inner) is BoundUnaryOperator { OperatorKind: UnaryOperatorKind.AddressOf } or BoundFieldAccess or BoundIndexAccess) {
                return View(inner, ends);
            }

            return Expression(expression) is { } value ? Hold(value) : null;
        }

        void SetReference(SilValue root, SilValue field, SilValue referent) {
            var reference = (RelativeReferenceType)((InlinePointerType)field.Type).Pointee;
            switch (referent) {
                case UnitValue:
                    Builtin("inline_ref_clear", SilType.Unit, field);
                    return;

                case { Type: InlinePointerType }:
                    Builtin("inline_ref_set", SilType.Unit, root, field, referent);
                    return;

                case { Type: EnumType optional }: {
                    var some = function.AddBlock();
                    var target = some.AddArgument(new InlinePointerType(reference.Target));
                    var none = function.AddBlock();
                    var after = function.AddBlock();
                    Jump(new SwitchEnumInst(referent, [(optional.CaseIndex("Some"), some)], none));
                    _current = some;
                    Builtin("inline_ref_set", SilType.Unit, root, field, target);
                    Branch(after);
                    _current = none;
                    Builtin("inline_ref_clear", SilType.Unit, field);
                    Branch(after);
                    _current = after;
                    return;
                }
            }
        }

        /// <summary>
        ///     A value for a field of an inline struct: a string for inline
        ///     bytes, an array's elements, a struct as its block, a scalar.
        /// </summary>
        SilValue? InlineFieldValue(BoundExpression expression) {
            // A string literal as bytes: 'customer: "Ann"'.
            if (expression is BoundConversion { ConversionKind: ConversionKind.LiteralTyping, Operand: BoundLiteral { Value: string text } }
                && Sub(expression.Type) is ArrayTypeSymbol) {
                return Emit(new StringLiteralInst(text));
            }

            if (expression is BoundConversion { ConversionKind: ConversionKind.LiteralTyping, Operand: BoundArray array }
                && Sub(expression.Type) is ArrayTypeSymbol { ElementType: var element }) {
                return new ElementList(array.Elements, element);
            }

            if (expression is BoundArray { Type: ArrayTypeSymbol { ElementType: var written } } literal) {
                return new ElementList(literal.Elements, written);
            }

            return Expression(expression);
        }

        /// <summary>The elements of an array literal, put into an inline array one by one.</summary>
        sealed class ElementList(System.Collections.Immutable.ImmutableArray<BoundExpression> elements, TypeSymbol element) : SilValue {
            public System.Collections.Immutable.ImmutableArray<BoundExpression> Elements { get; } = elements;
            public TypeSymbol Element { get; } = element;
            public override SilType Type => SilType.Unit;
        }

        /// <summary>Puts a value where a field is: in place, or by changing the block's size.</summary>
        void Put(SilValue root, SilValue pointer, SilValue value, BoundNode at) {
            var pointee = ((InlinePointerType)pointer.Type).Pointee;
            switch (pointee) {
                case var _ when InlineLayout.Scalar(pointee) is not null:
                    Builtin("inline_store", SilType.Unit, pointer, value);
                    return;

                case InlineArrayType when value is ElementList list:
                    // Each element at the end of the array, the array found afresh each time.
                    var arrayPath = PathOf(root, pointer);
                    foreach (var elementExpression in list.Elements) {
                        if (Expression(elementExpression) is not { } element) {
                            continue;
                        }

                        Hold(element);
                        var array = Rewalk(root, arrayPath);
                        Builtin("inline_append", SilType.Unit, root, array, element);
                        End(element);
                    }

                    return;

                case InlineArrayType when value.Type == SilType.String:
                    Builtin("inline_assign_string", SilType.Unit, root, pointer, value);
                    Destroy(value);
                    return;

                case StructType { IsInline: true } when value.Type == pointee || value.Type == pointer.Type:
                    Builtin("inline_assign", SilType.Unit, root, pointer, value);
                    Destroy(value);
                    return;

                default:
                    Destroy(value);
                    Missing(at, $"putting a {value.Type} into '{pointee}'");
                    return;
            }
        }

        /// <summary>The fields a view was reached by from its block, to take it again after a change of size.</summary>
        static List<string> PathOf(SilValue root, SilValue pointer) {
            var path = new List<string>();
            for (var current = pointer; current is BuiltinInst { Name: var name } builtin && name.StartsWith("inline_field.", StringComparison.Ordinal);
                 current = builtin.Operands[0]) {
                var owner = (StructType)((InlinePointerType)builtin.Operands[0].Type).Pointee;
                path.Insert(0, owner.Fields[int.Parse(name["inline_field.".Length..])].Name);
            }

            return path;
        }

        SilValue Rewalk(SilValue root, List<string> path) {
            var inline = (StructType)((AddressType)root.Type).Pointee;
            var pointer = Builtin("inline_unique", new InlinePointerType(inline), root);
            foreach (var field in path) {
                pointer = FieldPointer(pointer, field);
            }

            return pointer;
        }

        /// <summary>
        ///     <c>Order { id: 7, customer: "Ann", lines: [], parent: none }</c>:
        ///     a block of the empty value, each field then put in place, in
        ///     the order written.
        /// </summary>
        SilValue? InlineRecord(BoundRecordExpression record, StructType inline) {
            var slot = NewSlot(inline);
            Emit(new StoreInst(Builtin("inline_new", inline), slot, StoreKind.Assign));
            foreach (var field in record.Fields) {
                if (IsRelative(Sub(field.Field.Type))) {
                    var ends = new List<SilValue>();
                    if (RelativeValue(field.Value, ends) is { } referent) {
                        SetReference(slot, FieldPointer(Builtin("inline_unique", new InlinePointerType(inline), slot), field.Field.Name), referent);
                    }

                    EndAll(ends);
                    continue;
                }

                if (InlineFieldValue(field.Value) is not { } value) {
                    continue;
                }

                Hold(value);
                var pointer = FieldPointer(Builtin("inline_unique", new InlinePointerType(inline), slot), field.Field.Name);
                Unhold(value);
                Put(slot, pointer, value, field.Value);
            }

            return Emit(new LoadInst(slot, LoadKind.Take));
        }

        /// <summary><c>owner.items.append(x)</c>: the element put at the end of the inline array, the block grown.</summary>
        SilValue? InlineAppend(BoundCall call) {
            if (Expression(call.Arguments[0]) is not { } element) {
                return null;
            }

            Hold(element);
            var ends = new List<SilValue>();
            if (Mutable(call.Receiver!, ends) is not var (root, array)) {
                EndAll(ends);
                End(element);
                return null;
            }

            Builtin("inline_append", SilType.Unit, root, array, element);
            EndAll(ends);
            End(element);
            return UnitValue.Instance;
        }

        /// <summary>A member of an inline array: its count, whether it is empty, its bytes as text.</summary>
        SilValue? InlineArrayMember(BoundExpression receiver, string member, BoundNode at) {
            var ends = new List<SilValue>();
            if (View(receiver, ends) is not { } array) {
                EndAll(ends);
                return null;
            }

            SilValue? result = member switch {
                "count" => Builtin("inline_count", SilType.Int, array),
                "isEmpty" => Builtin("cmp_eq_Int", SilType.Bool, Builtin("inline_count", SilType.Int, array), Emit(new IntegerLiteralInst(SilType.Int, 0))),
                "toString" => Builtin("inline_to_string", SilType.String, array),
                _ => Missing(at, $"the member '{member}' of an inline array"),
            };

            EndAll(ends);
            return result;
        }

        /// <summary>
        ///     <c>for x in owner.items</c>: a cursor walked from element to
        ///     element — each one's size read where it varies. An element that
        ///     is a struct is a view into the owner, for the loop's body.
        /// </summary>
        void ForInline(BoundForEach loop) {
            var ends = new List<SilValue>();
            if (loop.Element is null || View(loop.Sequence, ends) is not { Type: InlinePointerType { Pointee: InlineArrayType arrayType } } array) {
                EndAll(ends);
                Fail(loop.Syntax, "this 'for' over an inline array");
                return;
            }

            var elementPointer = new InlinePointerType(arrayType.Element);
            if (InlineOf(arrayType.Element) is not null) {
                _slots[loop.Element] = NewSlot(elementPointer, loop.Element.Name);
                _views.Add(loop.Element);
            }

            var count = Builtin("inline_count", SilType.Int, array);
            var counter = NewSlot(SilType.Int);
            Emit(new StoreInst(Emit(new IntegerLiteralInst(SilType.Int, 0)), counter, StoreKind.Init));
            var cursor = NewSlot(elementPointer);
            Emit(new StoreInst(Builtin("inline_first", elementPointer, array), cursor, StoreKind.Init));

            var header = function.AddBlock();
            var body = function.AddBlock();
            var step = function.AddBlock();
            var after = function.AddBlock();
            Branch(header);

            _current = header;
            Jump(new CondBranchInst(Builtin("cmp_lt_Int", SilType.Bool, Emit(new LoadInst(counter)), count), body, [], after, []));

            _current = body;
            var element = Emit(new LoadInst(cursor));
            if (_views.Contains(loop.Element)) {
                Emit(new StoreInst(element, _slots[loop.Element], StoreKind.Init));
            } else {
                BindLocal(loop.Element, Builtin("inline_load", arrayType.Element, element), loop.Syntax);
            }

            if (loop.Filter is { } filter) {
                var filtered = function.AddBlock();
                Condition(filter, filtered, step);
                _current = filtered;
            }

            InLoop(loop, after, step, () => Statement(loop.Body));
            if (_current is not null) {
                Branch(step);
            }

            _current = step;
            Emit(new StoreInst(Builtin("inline_next", elementPointer, array, Emit(new LoadInst(cursor))), cursor, StoreKind.Init));
            var one = Emit(new IntegerLiteralInst(SilType.Int, 1));
            Emit(new StoreInst(Builtin("add_Int", SilType.Int, Emit(new LoadInst(counter)), one), counter, StoreKind.Init));
            Branch(header);

            _current = after;
            EndAll(ends);
        }

        /// <summary><c>Bytes.of(value)</c>: the block's bytes, copied out — there is nothing to serialise.</summary>
        SilValue? BytesOf(BoundCall call) {
            var ends = new List<SilValue>();
            if (View(call.Arguments[0], ends) is not { Type: InlinePointerType { Pointee: StructType { IsInline: true } } } pointer) {
                EndAll(ends);
                return Missing(call, "'Bytes.of' of a value that is not '@inline_layout'");
            }

            var bytes = Builtin("inline_bytes", new ArrayType(SilType.U8), pointer);
            EndAll(ends);
            return bytes;
        }

        /// <summary>
        ///     <c>T.fromBytes(bytes)</c>: the bytes in a block, validated —
        ///     <c>.Ok</c> with the block, or <c>.Invalid</c> with why.
        /// </summary>
        SilValue? FromBytes(BoundCall call) {
            if (LowerType(call.Type) is not EnumType decoded || decoded.Cases[0].Payload is not StructType { IsInline: true } inline) {
                return Missing(call, $"'{Describe(call.Syntax)}'");
            }

            if (Expression(call.Arguments[0]) is not { } bytes) {
                return null;
            }

            var block = Builtin("inline_from", inline, bytes);
            Destroy(bytes);
            var why = Builtin("inline_invalid", SilType.String, block);
            var valid = Builtin("cmp_eq_Int", SilType.Bool, Builtin("string_count", SilType.Int, why), Emit(new IntegerLiteralInst(SilType.Int, 0)));

            var ok = function.AddBlock();
            var invalid = function.AddBlock();
            var after = function.AddBlock();
            var result = after.AddArgument(decoded);
            Jump(new CondBranchInst(valid, ok, [], invalid, []));

            _current = ok;
            Emit(new DestroyValueInst(why));
            Branch(after, Emit(new EnumInst(decoded, decoded.CaseIndex("Ok"), block)));

            _current = invalid;
            Emit(new DestroyValueInst(block));
            Branch(after, Emit(new EnumInst(decoded, decoded.CaseIndex("Invalid"), why)));

            _current = after;
            return result;
        }
    }
}
