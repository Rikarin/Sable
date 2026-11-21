using System.Collections.Immutable;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.SIL;

/// <summary>
///     Reflection (docs/06 §6.3): <c>Mirror(of:)</c> and <c>TypeInfo.of</c>,
///     written in <c>Sable.Core</c> over <c>Reflect</c>'s <c>@intrinsic</c>
///     members, which are expanded here for the type each is called with.
/// </summary>
/// <remarks>
///     <para>
///         Nothing is emitted for a type until a program reflects on it: the
///         first call for a type synthesises a function per question asked —
///         describe it, name its parts, give their types, describe them, name
///         its case — and every later call shares it. A program that never
///         reflects carries none of it, which is how hello world stays the
///         size it was (docs/04 §4.4). docs/06 §6.2 has field descriptors in
///         every type's metadata; generating code per type where it is asked
///         for gives the same answers without the always-present table, and
///         the interpreter and the native build agree because both run the
///         same SIL.
///     </para>
///     <para>
///         <c>TypeInfo.of(T.self)</c> needs <c>T: Reflectable</c>, which
///         <c>@reflectable</c> gives; its information is a constant built by a
///         function per type, so it too costs only the types asked about.
///     </para>
/// </remarks>
public sealed partial class SilGen {
    /// <summary>How many levels deep a description follows a value before writing '...': objects may form cycles.</summary>
    const int ReflectionDepth = 8;

    /// <summary>The functions synthesised so far, by question and type.</summary>
    readonly Dictionary<(string Question, TypeSymbol Type), string> _reflectors = [];

    /// <summary>True for a member <c>Sable.Core</c> declares <c>@intrinsic</c>: its body is the compiler's to give.</summary>
    internal static bool IsIntrinsic(FunctionSymbol function) {
        if (function.FunctionKind is FunctionKind.Accessor || function.GetAttribute("intrinsic") is not { FromExtension: false }) {
            return false;
        }

        Symbol? container = function;
        while (container is not null and not ModuleSymbol) {
            container = container.ContainingSymbol;
        }

        return container is ModuleSymbol { Name: "Sable.Core" };
    }

    // -----------------------------------------------------------------------
    // What a type is, to a mirror
    // -----------------------------------------------------------------------

    enum Shape { Scalar, Struct, Class, Tuple, Union, Enum, Optional, Array, Dictionary, Opaque }

    /// <summary>How a value of the type is taken apart; <see cref="Shape.Opaque" /> for what is only named.</summary>
    Shape ShapeOf(TypeSymbol type, SilType lowered) =>
        (type, lowered) switch {
            (_, ScalarType scalar) when scalar == SilType.Region || scalar == SilType.Object => Shape.Opaque,
            (_, OpaqueType) => Shape.Opaque,
            (_, ScalarType) => Shape.Scalar,
            (OptionalTypeSymbol, EnumType) => Shape.Optional,
            (TupleTypeSymbol, TupleType) => Shape.Tuple,
            (ArrayTypeSymbol, ArrayType or SliceType) => Shape.Array,
            (NamedTypeSymbol named, StructType { IsInline: false }) when Core.IsDictionary(named) => Shape.Dictionary,
            (NamedTypeSymbol { TypeKind: TypeKind.Struct }, StructType { IsInline: false }) => Shape.Struct,
            (NamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Actor } named, ClassType) when !named.OriginalDefinition.Equals(Core.Task) => Shape.Class,
            (NamedTypeSymbol { TypeKind: TypeKind.Union }, EnumType) => Shape.Union,
            (NamedTypeSymbol { TypeKind: TypeKind.Enum }, EnumType) => Shape.Enum,
            _ => Shape.Opaque,
        };

    /// <summary>What <c>Reflect.kind</c> says of a type.</summary>
    static string KindOf(TypeSymbol type) =>
        type switch {
            OptionalTypeSymbol => "optional",
            TupleTypeSymbol => "tuple",
            ArrayTypeSymbol => "array",
            FunctionTypeSymbol => "function",
            ExistentialTypeSymbol => "existential",
            ReferenceTypeSymbol => "reference",
            NamedTypeSymbol { Name: "Dictionary", TypeKind: TypeKind.Struct, ContainingSymbol: ModuleSymbol { Name: "Sable.Core" } } => "dictionary",
            NamedTypeSymbol named => named.TypeKind switch {
                TypeKind.Struct => "struct",
                TypeKind.Class => "class",
                TypeKind.Actor => "actor",
                TypeKind.Union => "union",
                TypeKind.Enum => "enum",
                _ => "primitive",
            },
            _ => "primitive",
        };

    /// <summary>One part of a value: its name, its type, and how to read it from the value.</summary>
    /// <param name="Read">Gives the part, and whether it is owned (to destroy after) rather than borrowed.</param>
    sealed record Part(string Name, TypeSymbol Type, Func<Synthesis, (SilValue Value, bool Owned)> Read);

    /// <summary>A way the value can be: the one way of a struct, or a case of a union.</summary>
    sealed record Arm(string? Case, IReadOnlyList<Part> Parts);

    /// <summary>The stored fields of a struct or class, a superclass's first, with their types as this type has them.</summary>
    List<(FieldSymbol Field, TypeSymbol Type)> StoredParts(NamedTypeSymbol named) {
        var parts = new List<(FieldSymbol, TypeSymbol)>();
        var members = TypeSubstitution.For(named);
        if (named.TypeKind is TypeKind.Class or TypeKind.Actor && SuperclassOf(named.OriginalDefinition) is { } superclass
            && members.Apply(superclass) is NamedTypeSymbol super) {
            parts.AddRange(StoredParts(super));
        }

        foreach (var field in StoredFields(named.OriginalDefinition)) {
            parts.Add((field, members.Apply(field.Type)));
        }

        return parts;
    }

    /// <summary>The payload of each case of a union, with its types as this union has them.</summary>
    static IReadOnlyList<(string Name, TypeSymbol Type)> PayloadOf(NamedTypeSymbol union, string caseName) {
        var members = TypeSubstitution.For(union);
        var unionCase = union.OriginalDefinition.Members.OfType<UnionCaseSymbol>().FirstOrDefault(c => c.Name == caseName);
        return unionCase is null
            ? []
            : [..unionCase.Payload.Select(static (p, i) => (p.Label ?? i.ToString(System.Globalization.CultureInfo.InvariantCulture), p.Type)).Select(p => (p.Item1, members.Apply(p.Type)))];
    }

    /// <summary>
    ///     Emits, for each way the value can be, its parts and then
    ///     <paramref name="arm" />, which ends the block: once for a struct,
    ///     class or tuple; once per case, after a switch, for a union, enum or
    ///     optional.
    /// </summary>
    void ForEachArm(Synthesis s, SilValue value, TypeSymbol type, Shape shape, Action<Arm> arm) {
        switch (shape) {
            case Shape.Struct: {
                var structType = (StructType)value.Type;
                var parts = new List<Part>();
                foreach (var (field, fieldType) in StoredParts((NamedTypeSymbol)type)) {
                    var index = structType.FieldIndex(field.Name);
                    parts.Add(new(field.DisplayName, fieldType, x => (x.Emit(new StructExtractInst(value, index)), false)));
                }

                arm(new(null, parts));
                return;
            }

            case Shape.Class: {
                var classType = (ClassType)value.Type;
                var stored = StoredParts((NamedTypeSymbol)type);
                var parts = new List<Part>();
                for (var i = 0; i < stored.Count && i < classType.Fields.Count; i++) {
                    var index = i;
                    parts.Add(new(stored[i].Field.DisplayName, stored[i].Type, x => {
                        var address = x.Emit(new RefElementAddrInst(value, index));
                        return classType.Fields[index].Type switch {
                            WeakStorageType => (x.Emit(new LoadWeakInst(address)), true),
                            UnownedStorageType => (x.Emit(new LoadUnownedInst(address)), true),
                            var plain => (x.Emit(new LoadInst(address)), !plain.IsTrivial),
                        };
                    }));
                }

                arm(new(null, parts));
                return;
            }

            case Shape.Tuple: {
                var tuple = (TupleTypeSymbol)type;
                var parts = new List<Part>();
                for (var i = 0; i < tuple.ElementTypes.Length; i++) {
                    var index = i;
                    var name = i < tuple.ElementNames.Length && tuple.ElementNames[i] is { Length: > 0 } label ? label : i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    parts.Add(new(name, tuple.ElementTypes[i], x => (x.Emit(new TupleExtractInst(value, index)), false)));
                }

                arm(new(null, parts));
                return;
            }

            case Shape.Union or Shape.Enum or Shape.Optional: {
                var enumType = (EnumType)value.Type;
                var targets = new List<(int, SilBlock)>();
                var arms = new List<(SilBlock Block, Arm Arm)>();
                for (var c = 0; c < enumType.Cases.Count; c++) {
                    var block = s.Function.AddBlock();
                    targets.Add((c, block));
                    var (caseName, payloadType) = enumType.Cases[c];
                    var parts = new List<Part>();
                    if (payloadType is not null) {
                        var payload = block.AddArgument(payloadType, !payloadType.IsTrivial);
                        if (shape == Shape.Optional) {
                            parts.Add(new("some", ((OptionalTypeSymbol)type).UnderlyingType, _ => (payload, false)));
                        } else if (IsIndirect(enumType, c)) {
                            // A boxed payload: each part copied out of the box.
                            var fields = PayloadOf((NamedTypeSymbol)type, caseName);
                            for (var i = 0; i < fields.Count; i++) {
                                var index = i;
                                parts.Add(new(fields[i].Name, fields[i].Type, x => {
                                        SilValue address = x.Emit(new RefElementAddrInst(payload, 0));
                                        if (fields.Count > 1) {
                                            address = x.Emit(new TupleElementAddrInst(address, index));
                                        }

                                        var part = x.Emit(new LoadInst(address));
                                        return (part, !part.Type.IsTrivial);
                                    }
                                ));
                            }
                        } else {
                            var fields = PayloadOf((NamedTypeSymbol)type, caseName);
                            for (var i = 0; i < fields.Count; i++) {
                                var index = i;
                                parts.Add(new(fields[i].Name, fields[i].Type,
                                    x => fields.Count == 1 ? (payload, false) : (x.Emit(new TupleExtractInst(payload, index)), false)));
                            }
                        }
                    }

                    var shown = shape == Shape.Optional ? caseName.ToLowerInvariant() : caseName;
                    arms.Add((block, new(shown, parts)));
                }

                s.Emit(new SwitchEnumInst(value, targets, null));
                foreach (var (block, each) in arms) {
                    s.Block = block;
                    arm(each);
                }

                return;
            }

            default:
                arm(new(null, []));
                return;
        }
    }

    // -----------------------------------------------------------------------
    // The synthesised functions
    // -----------------------------------------------------------------------

    /// <summary>A function being written by hand: its blocks, and the one being appended to.</summary>
    sealed class Synthesis(SilFunction function) {
        public SilFunction Function { get; } = function;
        public SilBlock Block { get; set; } = function.AddBlock();

        public T Emit<T>(T instruction) where T : SilInstruction => Block.Append(instruction);

        public SilValue Text(string text) => Emit(new StringLiteralInst(text));

        public SilValue Int(long value) => Emit(new IntegerLiteralInst(SilType.Int, value));

        /// <summary>Two strings joined; both were owned, and are consumed.</summary>
        public SilValue Join(SilValue owned, SilValue alsoOwned) {
            var joined = Emit(new BuiltinInst("string_concat", SilType.String, owned, alsoOwned));
            Emit(new DestroyValueInst(owned));
            Emit(new DestroyValueInst(alsoOwned));
            return joined;
        }

        /// <summary>A string slot: what is in it, with more on the end.</summary>
        public void Append(SilValue slot, SilValue owned) {
            var before = Emit(new LoadInst(slot, LoadKind.Take));
            Emit(new StoreInst(Join(before, owned), slot, StoreKind.Init));
        }

        public SilValue Slot(SilType type, SilValue initial) {
            var slot = Emit(new AllocStackInst(type));
            Emit(new StoreInst(initial, slot, StoreKind.Init));
            return slot;
        }

        /// <summary>What a slot holds, taken out, and the slot gone.</summary>
        public SilValue Close(SilValue slot) {
            var value = Emit(new LoadInst(slot, LoadKind.Take));
            Emit(new DeallocStackInst(slot));
            return value;
        }

        public SilValue Strings(IEnumerable<string> texts) {
            var slot = Slot(new ArrayType(SilType.String), Emit(new BuiltinInst("array_empty", new ArrayType(SilType.String))));
            foreach (var text in texts) {
                Emit(new BuiltinInst("array_append", SilType.Unit, slot, Text(text)));
            }

            return Close(slot);
        }

        /// <summary>
        ///     A loop over an array's elements, each borrowed; <paramref name="body" />
        ///     appends to whatever block is current. Leaves the exit block current.
        /// </summary>
        public void Loop(SilValue array, Action<SilValue, SilValue> body) {
            var count = Emit(new BuiltinInst("array_count", SilType.Int, array));
            var header = Function.AddBlock();
            var index = header.AddArgument(SilType.Int);
            var each = Function.AddBlock();
            var exit = Function.AddBlock();
            Emit(new BranchInst(header, Int(0)));
            Block = header;
            Emit(new CondBranchInst(Emit(new BuiltinInst("cmp_lt_Int", SilType.Bool, index, count)), each, [], exit, []));
            Block = each;
            var element = Emit(new BuiltinInst("array_get_borrowed", ArrayType.ElementOf(array.Type)!, array, index));
            body(index, element);
            Emit(new BranchInst(header, Emit(new BuiltinInst("add_Int", SilType.Int, index, Int(1)))));
            Block = exit;
        }

        /// <summary>Appends <paramref name="separator" /> to the slot unless this is the first element.</summary>
        public void Separate(SilValue slot, SilValue index, string separator) {
            var first = Emit(new BuiltinInst("cmp_eq_Int", SilType.Bool, index, Int(0)));
            var between = Function.AddBlock();
            var go = Function.AddBlock();
            Emit(new CondBranchInst(first, go, [], between, []));
            Block = between;
            Append(slot, Text(separator));
            Emit(new BranchInst(go));
            Block = go;
        }
    }

    /// <summary>The slot of <c>any Error</c>'s vtable that writes out the error an object holds.</summary>
    const string ErrorDescribeSlot = "describe";

    /// <summary>What is in that slot: the object and a depth, to a string.</summary>
    internal static FunctionType ErrorDescriberType(ClassType anyError) => new([anyError, SilType.Int], SilType.String);

    /// <summary>
    ///     The function in a box's slot: the error it holds, written out as
    ///     <c>Reflect.describe</c> writes its type. The base class's, which no
    ///     object has, writes only that it is an error.
    /// </summary>
    string ErrorDescriber(ClassType anyError, ClassType? box, SilType? error) {
        var name = box is null ? "$error.describe" : $"$error.describe${Sanitise(error!.ToString())}";
        if (_module.Find(name) is not null) {
            return name;
        }

        var function = new SilFunction(name, ErrorDescriberType(anyError));
        _module.Add(function);
        var s = new Synthesis(function);
        var self = s.Block.AddArgument(anyError);
        var depth = s.Block.AddArgument(SilType.Int);
        if (box is null) {
            s.Emit(new ReturnInst(s.Text("<any Error>")));
            return name;
        }

        // The error's type, as the program named it: lowering it is what
        // made this SIL type.
        var type = _types.FirstOrDefault(pair => error!.Equals(pair.Value)).Key;
        var held = s.Emit(new LoadInst(s.Emit(new RefElementAddrInst(s.Emit(new UncheckedRefCastInst(self, box)), 0))));
        var text = type is null ? s.Text($"<{error}>") : DescribePart(s, held, type, depth);
        if (!held.Type.IsTrivial) {
            s.Emit(new DestroyValueInst(held));
        }

        s.Emit(new ReturnInst(text));
        return name;
    }

    /// <summary>A function a question about a type is answered by, made the first time it is asked.</summary>
    string? Reflector(string question, TypeSymbol type) {
        if (_reflectors.TryGetValue((question, type), out var known)) {
            return known;
        }

        if (Lower(type) is not { } lowered) {
            return null;
        }

        var name = $"$reflect.{question}${Sanitise(type.ToDisplayString())}";
        for (var n = 2; _module.Find(name) is not null; n++) {
            name = $"$reflect.{question}${Sanitise(type.ToDisplayString())}${n}";
        }

        var function = new SilFunction(name, ReflectorType(question, lowered));
        _reflectors[(question, type)] = name;
        _module.Add(function);

        var s = new Synthesis(function);
        var value = s.Block.AddArgument(lowered);
        var shape = ShapeOf(type, lowered);
        switch (question) {
            case "describe":
                SynthesiseDescribe(s, value, s.Block.AddArgument(SilType.Int), type, shape);
                break;
            case "case":
                ForEachArm(s, value, type, shape, arm => s.Emit(new ReturnInst(s.Text(arm.Case ?? ""))));
                break;
            default:
                SynthesiseParts(s, value, type, shape, question);
                break;
        }

        return name;
    }

    static FunctionType ReflectorType(string question, SilType lowered) =>
        question switch {
            "describe" => new([lowered, SilType.Int], SilType.String),
            "case" => new([lowered], SilType.String),
            _ => new([lowered], new ArrayType(SilType.String)),
        };

    /// <summary>A part written out, owned: scalars in place, anything else by its own describing function.</summary>
    SilValue DescribePart(Synthesis s, SilValue value, TypeSymbol type, SilValue depth) {
        var lowered = value.Type;
        if (lowered == SilType.String) {
            return s.Join(s.Join(s.Text("\""), s.Emit(new CopyValueInst(value))), s.Text("\""));
        }

        if (lowered == SilType.Char) {
            return s.Join(s.Join(s.Text("'"), s.Emit(new BuiltinInst("string_from_Char", SilType.String, value))), s.Text("'"));
        }

        if (lowered == SilType.Unit) {
            return s.Text("()");
        }

        if (lowered is ScalarType && SilBuiltins.Find($"string_from_{lowered}") is not null) {
            return s.Emit(new BuiltinInst($"string_from_{lowered}", SilType.String, value));
        }

        // 'any Error': the error the object holds, as its box writes it.
        if (lowered is ClassType anyError && anyError == AnyErrorType()) {
            var describer = s.Emit(new ClassMethodInst(value, ErrorDescribeSlot, ErrorDescriberType(anyError)));
            return s.Emit(new ApplyInst(describer, SilType.String, value, depth));
        }

        if (ShapeOf(type, lowered) == Shape.Opaque || Reflector("describe", type) is not { } describe) {
            return s.Text($"<{type.ToDisplayString()}>");
        }

        // An optional is no deeper than what it holds.
        var deeper = type is OptionalTypeSymbol ? depth : s.Emit(new BuiltinInst("add_Int", SilType.Int, depth, s.Int(1)));
        var callee = s.Emit(new FunctionRefInst(describe, ReflectorType("describe", lowered)));
        return s.Emit(new ApplyInst(callee, SilType.String, value, deeper));
    }

    /// <summary>
    ///     A loop over a dictionary's keys and values in their order — its
    ///     <c>entries</c>, passing over the none a removed one left — each
    ///     borrowed, with how many came before it; <paramref name="body" />
    ///     appends to whatever block is current. Leaves the exit block current.
    /// </summary>
    static void EachEntry(Synthesis s, SilValue dictionary, Action<SilValue, SilValue, SilValue> body) {
        var layout = (StructType)dictionary.Type;
        var entries = s.Emit(new StructExtractInst(dictionary, layout.FieldIndex("entries")));
        var optional = (EnumType)ArrayType.ElementOf(entries.Type)!;
        var some = optional.CaseIndex("Some");
        var entry = (StructType)optional.Cases[some].Payload!;
        var shown = s.Slot(SilType.Int, s.Int(0));
        s.Loop(entries, (_, item) => {
            var present = s.Function.AddBlock();
            var next = s.Function.AddBlock();
            var payload = present.AddArgument(entry, !entry.IsTrivial);
            s.Emit(new SwitchEnumInst(item, [(some, present)], next));
            s.Block = present;
            var index = s.Emit(new LoadInst(shown));
            body(index, s.Emit(new StructExtractInst(payload, entry.FieldIndex("key"))), s.Emit(new StructExtractInst(payload, entry.FieldIndex("value"))));
            s.Emit(new StoreInst(s.Emit(new BuiltinInst("add_Int", SilType.Int, index, s.Int(1))), shown, StoreKind.Assign));
            s.Emit(new BranchInst(next));
            s.Block = next;
        });
        s.Emit(new DeallocStackInst(shown));
    }

    /// <summary>A part read, written out, and let go.</summary>
    SilValue DescribePart(Synthesis s, Part part, SilValue depth) {
        var (value, owned) = part.Read(s);
        var text = DescribePart(s, value, part.Type, depth);
        if (owned) {
            s.Emit(new DestroyValueInst(value));
        }

        return text;
    }

    /// <summary>'Point(x: 1, y: 2)', 'Shape.circle(radius: 2.0)', '[1, 2]', '["a": 1]', 'none'.</summary>
    void SynthesiseDescribe(Synthesis s, SilValue value, SilValue depth, TypeSymbol type, Shape shape) {
        var shown = type.ToDisplayString();

        // Too deep: a cycle, or as good as one.
        var deep = s.Function.AddBlock();
        var body = s.Function.AddBlock();
        s.Emit(new CondBranchInst(s.Emit(new BuiltinInst("cmp_ge_Int", SilType.Bool, depth, s.Int(ReflectionDepth))), deep, [], body, []));
        s.Block = deep;
        s.Emit(new ReturnInst(s.Text("...")));
        s.Block = body;

        if (shape is Shape.Array) {
            var element = ((ArrayTypeSymbol)type).ElementType;
            var slot = s.Slot(SilType.String, s.Text("["));
            s.Loop(value, (index, item) => {
                s.Separate(slot, index, ", ");
                s.Append(slot, DescribePart(s, item, element, depth));
            });

            s.Append(slot, s.Text("]"));
            s.Emit(new ReturnInst(s.Close(slot)));
            return;
        }

        if (shape is Shape.Dictionary) {
            var arguments = ((NamedTypeSymbol)type).TypeArguments;
            var slot = s.Slot(SilType.String, s.Text("["));
            EachEntry(s, value, (index, key, item) => {
                s.Separate(slot, index, ", ");
                s.Append(slot, DescribePart(s, key, arguments[0], depth));
                s.Append(slot, s.Text(": "));
                s.Append(slot, DescribePart(s, item, arguments[1], depth));
            });

            // '[:]' for an empty one, as it is written.
            var empty = s.Function.AddBlock();
            var full = s.Function.AddBlock();
            var count = s.Emit(new StructExtractInst(value, ((StructType)value.Type).FieldIndex("live")));
            s.Emit(new CondBranchInst(s.Emit(new BuiltinInst("cmp_eq_Int", SilType.Bool, count, s.Int(0))), empty, [], full, []));
            s.Block = empty;
            s.Append(slot, s.Text(":"));
            s.Emit(new BranchInst(full));
            s.Block = full;

            s.Append(slot, s.Text("]"));
            s.Emit(new ReturnInst(s.Close(slot)));
            return;
        }

        if (shape is Shape.Scalar or Shape.Opaque) {
            s.Emit(new ReturnInst(DescribePart(s, value, type, depth)));
            return;
        }

        ForEachArm(s, value, type, shape, arm => {
            // An optional is its value, or 'none'.
            if (shape == Shape.Optional) {
                s.Emit(new ReturnInst(arm.Parts.Count == 0 ? s.Text("none") : DescribePart(s, arm.Parts[0], depth)));
                return;
            }

            var head = arm.Case is null ? shown : $"{shown}.{arm.Case}";
            if (arm.Parts.Count == 0 && shape is Shape.Union or Shape.Enum) {
                s.Emit(new ReturnInst(s.Text(head)));
                return;
            }

            var labelled = shape != Shape.Tuple || arm.Parts.Any(static p => !char.IsDigit(p.Name[0]));
            var text = s.Text(shape == Shape.Tuple ? "(" : head + "(");
            for (var i = 0; i < arm.Parts.Count; i++) {
                var part = arm.Parts[i];
                var label = (i > 0 ? ", " : "") + (labelled && !char.IsDigit(part.Name[0]) ? part.Name + ": " : "");
                if (label.Length > 0) {
                    text = s.Join(text, s.Text(label));
                }

                text = s.Join(text, DescribePart(s, part, depth));
            }

            s.Emit(new ReturnInst(s.Join(text, s.Text(")"))));
        });
    }

    /// <summary>The names, types or descriptions of a value's parts, as an array of strings.</summary>
    void SynthesiseParts(Synthesis s, SilValue value, TypeSymbol type, Shape shape, string question) {
        var strings = new ArrayType(SilType.String);
        var zero = s.Int(0);
        if (shape is Shape.Array) {
            var element = ((ArrayTypeSymbol)type).ElementType;
            var slot = s.Slot(strings, s.Emit(new BuiltinInst("array_empty", strings)));
            s.Loop(value, (index, item) => {
                var part = question switch {
                    "names" => s.Emit(new BuiltinInst("string_from_Int", SilType.String, index)),
                    "types" => s.Text(element.ToDisplayString()),
                    _ => DescribePart(s, item, element, zero),
                };
                s.Emit(new BuiltinInst("array_append", SilType.Unit, slot, part));
            });
            s.Emit(new ReturnInst(s.Close(slot)));
            return;
        }

        // A dictionary's parts are its values, each named by its key.
        if (shape is Shape.Dictionary) {
            var key = ((NamedTypeSymbol)type).TypeArguments[0];
            var element = ((NamedTypeSymbol)type).TypeArguments[1];
            var slot = s.Slot(strings, s.Emit(new BuiltinInst("array_empty", strings)));
            EachEntry(s, value, (_, entryKey, item) => {
                var part = question switch {
                    "names" => DescribePart(s, entryKey, key, zero),
                    "types" => s.Text(element.ToDisplayString()),
                    _ => DescribePart(s, item, element, zero),
                };
                s.Emit(new BuiltinInst("array_append", SilType.Unit, slot, part));
            });
            s.Emit(new ReturnInst(s.Close(slot)));
            return;
        }

        ForEachArm(s, value, type, shape, arm => {
            var slot = s.Slot(strings, s.Emit(new BuiltinInst("array_empty", strings)));
            foreach (var part in arm.Parts) {
                var text = question switch {
                    "names" => s.Text(part.Name),
                    "types" => s.Text(part.Type.ToDisplayString()),
                    _ => DescribePart(s, part, zero),
                };
                s.Emit(new BuiltinInst("array_append", SilType.Unit, slot, text));
            }

            s.Emit(new ReturnInst(s.Close(slot)));
        });
    }

    // -----------------------------------------------------------------------
    // TypeInfo
    // -----------------------------------------------------------------------

    /// <summary>
    ///     The function that builds a type's <c>TypeInfo</c>: its fields,
    ///     methods, cases, conformances and attributes, all constants.
    /// </summary>
    string? TypeInfoBuilder(NamedTypeSymbol type, StructType info) {
        if (_reflectors.TryGetValue(("info", type), out var known)) {
            return known;
        }

        if (Element(info, "fields") is not { } fieldInfo || Element(info, "methods") is not { } methodInfo
            || Element(info, "cases") is not { } caseInfo) {
            return null;
        }

        var name = $"$reflect.info${Sanitise(type.ToDisplayString())}";
        var function = new SilFunction(name, new FunctionType([], info));
        var s = new Synthesis(function);
        var values = new Dictionary<string, SilValue>(StringComparer.Ordinal) {
            ["name"] = s.Text(type.ToDisplayString()),
            ["kind"] = s.Text(KindOf(type)),
        };

        // Each array is filled in a slot of its own, taken out before the next.
        SilValue Records(StructType element, IEnumerable<Dictionary<string, Func<SilValue>>> records) {
            var slot = s.Slot(new ArrayType(element), s.Emit(new BuiltinInst("array_empty", new ArrayType(element))));
            foreach (var record in records) {
                var fields = new SilValue[element.Fields.Count];
                for (var i = 0; i < fields.Length; i++) {
                    fields[i] = record.TryGetValue(element.Fields[i].Name, out var make) ? make() : Zero(s, element.Fields[i].Type);
                }

                s.Emit(new BuiltinInst("array_append", SilType.Unit, slot, s.Emit(new StructInst(element, fields))));
            }

            return s.Close(slot);
        }

        SilValue Flag(bool value) => s.Emit(new IntegerLiteralInst(SilType.Bool, value ? 1 : 0));

        values["fields"] = Records(fieldInfo, StoredParts(type).Select(p => new Dictionary<string, Func<SilValue>> {
            // A property kept in 'field' is the property, as its users see it.
            ["name"] = () => s.Text(p.Field.DisplayName),
            ["typeName"] = () => s.Text(p.Type.ToDisplayString()),
            ["isMutable"] = () => Flag(p.Field.BackedProperty is { } property ? property.HasSetter : p.Field.IsMutable),
            ["isPublic"] = () => Flag((p.Field.BackedProperty ?? p.Field).DeclaredAccessibility == Accessibility.Public),
        }));

        var members = TypeSubstitution.For(type);
        var methods = type.OriginalDefinition.Members.OfType<FunctionSymbol>()
            .Where(static f => f.FunctionKind is FunctionKind.Ordinary or FunctionKind.Initializer or FunctionKind.Operator);
        values["methods"] = Records(methodInfo, methods.Select(m => new Dictionary<string, Func<SilValue>> {
            ["name"] = () => s.Text(m.FunctionKind == FunctionKind.Initializer ? "init" : m.Name),
            ["signature"] = () => s.Text(Signature(m, members)),
            ["parameterNames"] = () => s.Strings(m.Parameters.Select(static p => p.Name)),
            ["parameterTypeNames"] = () => s.Strings(m.Parameters.Select(p => members.Apply(p.Type).ToDisplayString())),
            ["returnTypeName"] = () => s.Text(members.Apply(m.ReturnType).ToDisplayString()),
            ["isStatic"] = () => Flag(m.IsStatic),
            ["isPublic"] = () => Flag(m.DeclaredAccessibility == Accessibility.Public),
            ["isMutating"] = () => Flag(m.IsMutating),
        }));

        var cases = type.TypeKind == TypeKind.Union
            ? type.OriginalDefinition.Members.OfType<UnionCaseSymbol>().Select(c => (c.Name, Payload: PayloadOf(type, c.Name)))
            : type.OriginalDefinition.Members.OfType<EnumCaseSymbol>().Select(static c => (c.Name, Payload: (IReadOnlyList<(string Name, TypeSymbol Type)>)[]));
        values["cases"] = Records(caseInfo, cases.Select(c => new Dictionary<string, Func<SilValue>> {
            ["name"] = () => s.Text(c.Name),
            ["fieldNames"] = () => s.Strings(c.Payload.Select(static p => p.Name)),
            ["fieldTypeNames"] = () => s.Strings(c.Payload.Select(static p => p.Type.ToDisplayString())),
        }));

        values["conformances"] = s.Strings(type.OriginalDefinition.BaseTypes
            .Where(static b => b.TypeKind == TypeKind.Protocol)
            .Select(b => members.Apply(b).ToDisplayString()));

        values["attributeNames"] = s.Strings(type.OriginalDefinition.Attributes.Where(static a => !a.FromExtension).Select(static a => a.Written));

        var valuesIndex = info.FieldIndex("attributeValues");
        if (valuesIndex >= 0) {
            if (AttributeValuesBuilder(type, info.Fields[valuesIndex].Type) is not { } builder) {
                return null;
            }

            values["attributeValues"] = s.Emit(new ApplyInst(s.Emit(new FunctionRefInst(builder, new FunctionType([], info.Fields[valuesIndex].Type))), info.Fields[valuesIndex].Type, []));
        }

        var made = new SilValue[info.Fields.Count];
        for (var i = 0; i < made.Length; i++) {
            if (!values.Remove(info.Fields[i].Name, out var field)) {
                return null;
            }

            made[i] = field;
        }

        s.Emit(new ReturnInst(s.Emit(new StructInst(info, made))));
        _reflectors[("info", type)] = name;
        _module.Add(function);
        return name;
    }

    /// <summary>
    ///     The function that makes a type's own attributes' values, as
    ///     <c>(any Attribute)[&gt;..]</c>: each attribute's initialiser called
    ///     with its arguments, constants all, lowered as the binder bound them.
    /// </summary>
    string? AttributeValuesBuilder(NamedTypeSymbol type, SilType arrayType) {
        var name = $"$reflect.attributes${Sanitise(type.ToDisplayString())}";
        if (_module.Find(name) is not null) {
            return name;
        }

        var function = new SilFunction(name, new FunctionType([], arrayType));
        var attributes = _compilation.UserAttributesOf(type.OriginalDefinition);
        if (attributes.Count == 0 || Core.Attribute is not { } protocol || type.OriginalDefinition.DeclaringSyntax is not [var declaration, ..]) {
            var s = new Synthesis(function);
            s.Emit(new ReturnInst(s.Emit(new BuiltinInst("array_empty", arrayType))));
            _module.Add(function);
            return name;
        }

        var root = declaration;
        while (root.Parent is { } parent) {
            root = parent;
        }

        if (_compilation.SyntaxTrees.FirstOrDefault(t => ReferenceEquals(t.Root, root)) is not { } tree) {
            return null;
        }

        var existential = new ExistentialTypeSymbol(protocol);
        var array = new BoundArray(declaration, new ArrayTypeSymbol(existential, ArrayKind.Grow),
            [..attributes.Select(a => (BoundExpression)new BoundConversion(a.Value.Syntax, existential, a.Value, ConversionKind.Existential))]);
        var symbol = FunctionSymbol.Synthesized(name, type.OriginalDefinition);
        new FunctionLowering(this, symbol, function, tree.Text, TypeSubstitution.Empty).Lower(new BoundBlock(declaration, [], [new BoundReturn(declaration, array)]));
        function.RemoveUnreachableBlocks();
        _module.Add(function);
        return name;
    }

    /// <summary>The struct an array field of TypeInfo holds.</summary>
    static StructType? Element(StructType info, string field) {
        var index = info.FieldIndex(field);
        return index >= 0 && info.Fields[index].Type is ArrayType { Element: StructType element } ? element : null;
    }

    /// <summary>A field the compiler has nothing for: false, zero, empty.</summary>
    static SilValue Zero(Synthesis s, SilType type) =>
        type == SilType.String ? s.Text("")
        : type is ArrayType or SliceType ? s.Emit(new BuiltinInst("array_empty", type))
        : s.Emit(new IntegerLiteralInst(type, 0));

    /// <summary>A member as it would be declared: 'static fn origin() -> Point', 'init(x: Int, y: Int)'.</summary>
    static string Signature(FunctionSymbol function, TypeSubstitution members) {
        var parameters = string.Join(", ", function.Parameters.Select(p => {
            var label = p.Label is { } l && l != p.Name ? l + " " : "";
            return $"{label}{p.Name}: {members.Apply(p.Type).ToDisplayString()}";
        }));

        var generic = function.TypeParameters.IsEmpty ? "" : $"<{string.Join(", ", function.TypeParameters.Select(static t => t.Name))}>";
        var head = function.FunctionKind switch {
            FunctionKind.Initializer => $"init{generic}",
            FunctionKind.Operator => $"operator {function.Name}{generic}",
            _ => $"fn {function.Name}{generic}",
        };

        var modifiers = (function.IsStatic && function.FunctionKind != FunctionKind.Operator ? "static " : "")
            + (function.IsMutating ? "mutating " : "")
            + (function.IsAsync ? "async " : "");
        var throws = function.ThrownType is null ? "" : " throws";
        var result = function.FunctionKind == FunctionKind.Initializer || function.ReturnType.ToDisplayString() == "Void"
            ? ""
            : $" -> {members.Apply(function.ReturnType).ToDisplayString()}";
        return $"{modifiers}{head}({parameters}){throws}{result}";
    }

    sealed partial class FunctionLowering {
        /// <summary>
        ///     A call to one of <c>Sable.Core</c>'s <c>@intrinsic</c> members:
        ///     <c>Reflect</c>'s questions and <c>TypeInfo.of</c>, answered for
        ///     the type the argument has here. A type named with <c>.self</c>
        ///     is not evaluated; a value is borrowed for the call.
        /// </summary>
        SilValue? Intrinsic(BoundCall call) {
            if (call.Arguments.Length != 1) {
                return Missing(call, $"the intrinsic '{call.Function.Name}'");
            }

            var argument = call.Arguments[0];
            var type = Sub(argument.Type);
            var owner = OwnerOf(call.Function)?.Name;

            // The embedded build (docs/06 §6.1) carries no reflection.
            if (generator._denyReflection && owner is "Reflect" or "TypeInfo") {
                generator.Report(
                    ErrorCode.ERR_ReflectionDenied,
                    call.Syntax.Span,
                    $"'{owner}.{call.Function.Name}' reflects on '{type.ToDisplayString()}', and this program is built with --deny-reflection");
                return null;
            }

            if (owner == "TypeInfo" && call.Function.Name == "of") {
                // 'TypeInfo.of(Point.self)' describes Point, not Point.Type.
                if (type is MetatypeTypeSymbol { InstanceType: var instance }) {
                    type = instance;
                }

                if (type is not NamedTypeSymbol named || LowerType(call.Type) is not StructType info) {
                    return Missing(call, $"the TypeInfo of '{type.ToDisplayString()}'");
                }

                if (generator.TypeInfoBuilder(named, info) is not { } builder) {
                    return Missing(call, "a TypeInfo laid out as the compiler expects");
                }

                Discard(argument);
                return Emit(new ApplyInst(Emit(new FunctionRefInst(builder, new FunctionType([], info))), info));
            }

            if (owner != "Reflect") {
                return Missing(call, $"the intrinsic '{owner}.{call.Function.Name}'");
            }

            // Questions about the type alone are answered here and now.
            switch (call.Function.Name) {
                case "typeName":
                    Discard(argument);
                    return Emit(new StringLiteralInst(type.ToDisplayString()));
                case "kind":
                    Discard(argument);
                    return Emit(new StringLiteralInst(KindOf(type)));
            }

            var question = call.Function.Name switch {
                "describe" => "describe",
                "caseName" => "case",
                "fieldNames" => "names",
                "fieldTypeNames" => "types",
                "fieldValues" => "values",
                _ => null,
            };

            if (question is null) {
                return Missing(call, $"the intrinsic 'Reflect.{call.Function.Name}'");
            }

            if (argument is BoundTypeExpression or BoundMetatype) {
                return Missing(call, $"'Reflect.{call.Function.Name}' of a type rather than a value");
            }

            // '()' is no value to pass: its answers are known.
            if (LowerType(type) == SilType.Unit) {
                Discard(argument);
                return question switch {
                    "describe" => Emit(new StringLiteralInst("()")),
                    "case" => Emit(new StringLiteralInst("")),
                    _ => Emit(new BuiltinInst("array_empty", new ArrayType(SilType.String))),
                };
            }

            if (Borrow(argument) is not { } value) {
                return null;
            }

            if (generator.Reflector(question, type) is not { } reflector) {
                End(value);
                return Missing(call, $"reflecting on '{type.ToDisplayString()}'");
            }

            var signature = ReflectorType(question, value.Type);
            var arguments = question == "describe"
                ? new[] { value, Emit(new IntegerLiteralInst(SilType.Int, 0)) }
                : [value];
            var result = Emit(new ApplyInst(Emit(new FunctionRefInst(reflector, signature)), signature.Result, arguments));
            End(value);
            return result;
        }

        /// <summary>An argument only its type is wanted of: a type names nothing to run; a value is run and let go.</summary>
        void Discard(BoundExpression argument) {
            if (argument is BoundTypeExpression or BoundMetatype) {
                return;
            }

            if (Borrow(argument) is { } value) {
                End(value);
            }
        }
    }
}
