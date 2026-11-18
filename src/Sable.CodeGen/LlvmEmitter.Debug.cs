using System.Text;
using Sable.SIL;

namespace Sable.CodeGen;

public sealed partial class LlvmEmitter {
    // =======================================================================
    // Debug information
    // =======================================================================

    /// <summary>
    ///     Whether a forwarding thunk's prologue is given a line of its own
    ///     for this compiler: for an LLVM before 20, which ends no prologue
    ///     on line 0 (see <c>DebugInfo.Subprogram</c>). Apple's lldb steps
    ///     through the thunk as a trampoline whatever its lines.
    /// </summary>
    public static bool ThunkPrologueHasALine((int Major, bool Apple)? compiler) => compiler is { Apple: false, Major: < 20 };

    /// <summary>
    ///     The DWARF metadata: one compile unit, a file per source, a
    ///     subprogram per function, a location per instruction, and a variable
    ///     per local and parameter — what lldb needs for frames, locals and
    ///     their types.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Every type is described as it is laid out, so that lldb with no
    ///         help at all shows something true. A type that needs the
    ///         formatter plugin (<c>tools/lldb/sable_lldb.py</c>) to read well
    ///         is a typedef named as Sable names it over a type whose name says
    ///         what kind it is — the plugin matches on that suffix:
    ///     </para>
    ///     <list type="bullet">
    ///         <item><c>String</c>: a pointer to <c>SableString</c> — header, byte length, scalar count, bytes.</item>
    ///         <item><c>[T]</c>: <c>[T]$list</c>, holding <c>$buffer</c>, a pointer to <c>[T]$buffer</c> — header, count, capacity, elements; null when empty.</item>
    ///         <item>A class <c>C</c>: <c>C$ref</c>, holding <c>$object</c>, a pointer to <c>C$object</c> — header, then every field, a superclass's first.</item>
    ///         <item>A union <c>U</c>: <c>U$union</c> — <c>$tag</c>, an enumeration of the case names, then <c>$payload</c>, a C union of the cases' payloads.</item>
    ///         <item><c>T?</c>: <c>T?$optional</c>, laid out as a union; an optional class reference is the reference, null for none.</item>
    ///         <item><c>(A) -&gt; R</c>: <c>(A) -&gt; R$closure</c> — <c>$function</c> and the <c>$context</c> object.</item>
    ///         <item><c>any P</c>: <c>any P$existential</c> — a closure per requirement, all sharing the box that holds the value.</item>
    ///     </list>
    ///     <para>
    ///         An enum whose cases carry nothing is a DWARF enumeration, which
    ///         lldb prints by name unaided; a tuple is a struct with members
    ///         <c>0</c>, <c>1</c>, …; a struct is a struct.
    ///     </para>
    /// </remarks>
    sealed class DebugInfo(string directory) {
        readonly List<string> _nodes = [];
        readonly Dictionary<string, string> _unique = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
        readonly Dictionary<SilType, string?> _types = [];
        readonly List<(string Name, string Node)> _classes = [];
        string? _unit;
        string? _firstFile;

        /// <summary>A metadata node; identical bodies share one, unless it is distinct.</summary>
        public string Node(string body, bool distinct = false) {
            if (!distinct && _unique.TryGetValue(body, out var existing)) {
                return existing;
            }

            var name = $"!{_nodes.Count}";
            _nodes.Add($"{name} = {(distinct ? "distinct " : "")}{body}");
            if (!distinct) {
                _unique[body] = name;
            }

            return name;
        }

        /// <summary>A node whose body is written later, so that a type can refer to itself.</summary>
        string Reserve() {
            var name = $"!{_nodes.Count}";
            _nodes.Add("");
            return name;
        }

        void Define(string name, string body) => _nodes[int.Parse(name.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture)] = $"{name} = {body}";

        /// <summary>
        ///     Starts the module: the compile unit, in the entry point's file,
        ///     and every class's type — a formatter finds an object's dynamic
        ///     type by the name its metadata carries, which need not be a type
        ///     any variable has.
        /// </summary>
        public void Begin(SilModule module) {
            var entry = module.EntryPoint is { } name ? module.Find(name)?.SourceFile : null;
            File(entry ?? module.Functions.Select(static f => f.SourceFile).FirstOrDefault(static f => f is not null) ?? "<unknown>");
            foreach (var type in module.Types.OfType<ClassType>()) {
                if (Type(type) is { } node) {
                    _classes.Add((type.Name, node));
                }
            }
        }

        public string File(string fileName) {
            if (_files.TryGetValue(fileName, out var file)) {
                return file;
            }

            file = Node($"!DIFile(filename: {Quote(fileName)}, directory: {Quote(directory)})");
            _files[fileName] = file;
            _firstFile ??= file;
            return file;
        }

        string Unit => _unit ??= Reserve();

        /// <summary>
        ///     The function's file; for one the compiler made, the compile
        ///     unit's — or, for a forwarding thunk, a file no source is, as
        ///     Swift's thunks are: it has no line of the program's, and may
        ///     be given a line of its own (see <see cref="Subprogram"/>).
        /// </summary>
        public string FileOf(SilFunction function) =>
            function.SourceFile is { } file ? File(file)
            : function.TrampolineTarget is not null ? File(CompilerGenerated)
            : _firstFile!;

        /// <summary>The file a forwarding thunk's prologue is said to be in: one no breakpoint by line can name.</summary>
        const string CompilerGenerated = "<compiler-generated>";

        /// <summary>
        ///     A function's subprogram, named by the function's full Sable name
        ///     — <c>Demo.Point.length</c>, <c>Demo.main$closure0</c> — which is
        ///     what a backtrace shows and what <c>breakpoint set -n</c> takes.
        /// </summary>
        /// <remarks>
        ///     There is no linkage name. The symbol is the same name behind
        ///     <c>sable.</c>, nothing to demangle; but a debugger shows a
        ///     function by its linkage name when it has one and cannot
        ///     demangle it, and the prefix would only be noise there.
        /// </remarks>
        public string Subprogram(SilFunction function) {
            var artificial = function.SourceFile is null;
            var file = FileOf(function);
            var line = artificial ? 0 : function.Line;
            var scopeLine = line;

            // A forwarding thunk names what it forwards to: DW_AT_trampoline,
            // which lldb steps through ('step' into a witness lands in the
            // method, not back at the call).
            //
            // An lldb that does not (Ubuntu 24.04's lldb 18) still steps on
            // through code at line 0 — unless the whole function is one
            // line-0 row: then it steps straight back out, over the call.
            // An LLVM from before 20 writes just that: the row the scope
            // line starts, and no second line-0 row where the prologue
            // ends. For one of those the prologue is given a line of its
            // own, in a file no source is: stepped into, the prologue is
            // skipped, the body is line 0 to the end but not from the
            // start, and lldb steps on through it into the method; a
            // 'finish' from the method returns to line 0, which it steps
            // out of to the caller. (A newer LLVM ends the prologue on the
            // scope line, where lldb would stop; it needs nothing.)
            var trampoline = "";
            if (function.TrampolineTarget is { } target) {
                trampoline = $"targetFuncName: {Quote(target)}, ";
                if (ThunkPrologueHasALine(NativeBuild.CompilerVersion)) {
                    scopeLine = 1;
                }
            }

            return Node(
                $"!DISubprogram(name: {Quote(function.Name)}, scope: {file}, file: {file}, "
                + $"line: {line}, type: {Subroutine(function.Type)}, scopeLine: {scopeLine}, {(artificial ? "flags: DIFlagArtificial, " : "")}spFlags: DISPFlagDefinition, unit: {Unit}, {trampoline})".Replace(", )", ")", StringComparison.Ordinal),
                distinct: true
            );
        }

        /// <summary>A block within a function, inside another scope: its own, for every block.</summary>
        public string LexicalBlock(string parent, string file, int line, int column) =>
            Node($"!DILexicalBlock(scope: {parent}, file: {file}, line: {line}, column: {column})", distinct: true);

        readonly List<string> _globals = [];

        /// <summary>
        ///     A global of the program's, for <c>target variable</c> and for
        ///     expressions to read: named by its last part (<c>limit</c> for
        ///     <c>Demo.limit</c>), since a debugger reads a dot in a name as a
        ///     member's. The <c>!dbg</c> for its definition; null for one with
        ///     no values.
        /// </summary>
        public string? GlobalVariable(string name, SilType type, bool local) {
            if (Type(type) is not { } typeNode) {
                return null;
            }

            var variable = Node(
                $"!DIGlobalVariable(name: {Quote(name[(name.LastIndexOf('.') + 1)..])}, scope: {Unit}, file: {_firstFile}, line: 0, type: {typeNode}, isLocal: {(local ? "true" : "false")}, isDefinition: true)",
                distinct: true
            );
            var expression = Node($"!DIGlobalVariableExpression(var: {variable}, expr: !DIExpression())");
            _globals.Add(expression);
            return expression;
        }

        public string Location(int line, int column, string scope) =>
            Node($"!DILocation(line: {line}, column: {column}, scope: {scope})");

        public string Variable(string name, string scope, string file, int line, SilType type, int argument = 0) {
            var typePart = Type(type) is { } typeNode ? $", type: {typeNode}" : "";
            var argumentPart = argument > 0 ? $", arg: {argument}" : "";
            return Node($"!DILocalVariable(name: {Quote(name)}{argumentPart}, scope: {scope}, file: {file}, line: {line}{typePart})");
        }

        // -------------------------------------------------------------------
        // Types
        // -------------------------------------------------------------------

        string Basic(string name, int bits, string encoding) =>
            Node($"!DIBasicType(name: {Quote(name)}, size: {bits}, encoding: {encoding})");

        string Int64 => Basic("int64_t", 64, "DW_ATE_signed");
        string UInt32 => Basic("uint32_t", 32, "DW_ATE_unsigned");

        string Pointer(string? target) => Node($"!DIDerivedType(tag: DW_TAG_pointer_type, baseType: {target ?? "null"}, size: 64)");

        string Typedef(string name, string? target) => Node($"!DIDerivedType(tag: DW_TAG_typedef, name: {Quote(name)}, baseType: {target ?? "null"})");

        string Member(string name, string? type, int bytes, int offset, string? scope = null, bool artificial = false) =>
            Node(
                $"!DIDerivedType(tag: DW_TAG_member, name: {Quote(name)}{(scope is null ? "" : $", scope: {scope}")}, baseType: {type ?? "null"}, "
                + $"size: {bytes * 8}, offset: {offset * 8}{(artificial ? ", flags: DIFlagArtificial" : "")})"
            );

        string Structure(string name, int bytes, IEnumerable<string> members, string tag = "DW_TAG_structure_type") =>
            Node($"!DICompositeType(tag: {tag}, name: {Quote(name)}, size: {bytes * 8}, elements: !{{{string.Join(", ", members)}}})");

        /// <summary>What every counted object starts with (docs/06 §6.2): its metadata, the strong count, and the flags.</summary>
        string ObjectHeader {
            get {
                var metadata = Structure("SableMetadata", 40, [
                    Member("destroy", Pointer(null), 8, 0),
                    Member("name", Pointer(Basic("char", 8, "DW_ATE_signed_char")), 8, 8),
                    Member("element_size", Int64, 8, 16),
                    Member("copy_element", Pointer(null), 8, 24),
                    Member("destroy_element", Pointer(null), 8, 32),
                ]);
                return Structure("SableObject", 16, [
                    Member("type", Pointer(metadata), 8, 0),
                    Member("strong", UInt32, 4, 8),
                    Member("flags", UInt32, 4, 12),
                ]);
            }
        }

        /// <summary>The name Sable gives the type, for a debugger to show.</summary>
        static string DisplayName(SilType type) =>
            type switch {
                ArrayType array => $"[{DisplayName(array.Element)}]",
                EnumType enumType when OptionalPayload(enumType) is { } payload => DisplayName(payload) + "?",
                StructType { Name: var name } when name.StartsWith("any$", StringComparison.Ordinal) => "any " + name[4..],
                TupleType tuple => $"({string.Join(", ", tuple.Elements.Select(DisplayName))})",
                ThickFunctionType thick => FunctionName(thick.Signature),
                FunctionType function => FunctionName(function),
                AddressType address => "inout " + DisplayName(address.Pointee),
                WeakStorageType weak => "weak " + DisplayName(weak.Optional),
                UnownedStorageType unowned => "unowned " + DisplayName(unowned.Referent),
                _ => type.ToString(),
            };

        static string FunctionName(FunctionType function) =>
            $"({string.Join(", ", function.Parameters.Select(DisplayName))}){(function.Thrown is { } thrown ? $" throws({DisplayName(thrown)})" : "")} -> {DisplayName(function.Result)}";

        /// <summary>The wrapped type, for an optional.</summary>
        static SilType? OptionalPayload(EnumType type) =>
            type.Name.StartsWith("Optional$", StringComparison.Ordinal) && type.Cases is [("Some", { } payload), ("None", null)] ? payload : null;

        /// <summary>The DWARF type for a SIL type; null for one with no values, the empty tuple.</summary>
        public string? Type(SilType type) {
            if (_types.TryGetValue(type, out var known)) {
                return known;
            }

            // A class refers to itself through its fields: its node is
            // known before they are described.
            if (type is ClassType classType) {
                var reference = Reserve();
                _types[type] = reference;
                var objectType = ClassObject(classType);
                var handle = Structure(classType.Name + "$ref", 8, [Member("$object", Pointer(objectType), 8, 0)]);
                Define(reference, $"!DIDerivedType(tag: DW_TAG_typedef, name: {Quote(classType.Name)}, baseType: {handle})");
                return reference;
            }

            // So may a struct, through an array of itself.
            if (type is ArrayType array) {
                var reference = Reserve();
                _types[type] = reference;
                var body = Structure(DisplayName(array) + "$buffer", 40, [
                    Member("$header", ObjectHeader, 16, 0, artificial: true),
                    Member("count", Int64, 8, 16),
                    Member("capacity", Int64, 8, 24),
                    Member("elements", Pointer(Type(array.Element)), 8, 32),
                ]);
                var list = Structure(DisplayName(array) + "$list", 8, [Member("$buffer", Pointer(body), 8, 0)]);
                Define(reference, $"!DIDerivedType(tag: DW_TAG_typedef, name: {Quote(DisplayName(array))}, baseType: {list})");
                return reference;
            }

            // And a struct or a union, through a closure whose signature names
            // it: its node is reserved first, then given what was described.
            if (type is StructType or EnumType) {
                var reference = Reserve();
                _types[type] = reference;
                var described = Describe(type)!;
                var body = _nodes[int.Parse(described.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture)];
                Define(reference, body[(body.IndexOf(" = ", StringComparison.Ordinal) + 3)..]);
                return reference;
            }

            var node = Describe(type);
            _types[type] = node;
            return node;
        }

        string ClassObject(ClassType type) {
            var objectType = Reserve();
            var members = new List<string> { Member("$header", ObjectHeader, ObjectHeaderSize, 0, artificial: true) };
            var end = ObjectHeaderSize;
            foreach (var (fieldName, fieldType, offset) in ObjectFields(type)) {
                if (Type(fieldType) is { } fieldNode) {
                    members.Add(Member(fieldName, fieldNode, Size(fieldType), offset));
                }

                end = offset + Size(fieldType);
            }

            var size = (end + 7) / 8 * 8;
            Define(objectType, $"!DICompositeType(tag: DW_TAG_structure_type, name: {Quote(type.Name + "$object")}, size: {size * 8}, elements: !{{{string.Join(", ", members)}}})");
            return objectType;
        }

        string? Describe(SilType type) {
            switch (type) {
                // Named as Sable names them, over the machine type a debugger
                // knows how to print.
                case var _ when type == SilType.Int:
                    return Typedef("Int", Int64);
                case var _ when type == SilType.Bool:
                    return Typedef("Bool", Basic("bool", 8, "DW_ATE_boolean"));
                case var _ when type == SilType.F64:
                    return Typedef("F64", Basic("double", 64, "DW_ATE_float"));
                case var _ when type == SilType.F32:
                    return Typedef("F32", Basic("float", 32, "DW_ATE_float"));
                case var _ when type == SilType.F16:
                    return Typedef("F16", Basic("_Float16", 16, "DW_ATE_float"));
                case var _ when type == SilType.Char:
                    return Typedef("Char", Basic("char32_t", 32, "DW_ATE_UTF"));
                case var _ when SilType.IntegerInfo(type) is var (bits, signed):
                    return Typedef(type.ToString(), Basic($"{(signed ? "" : "u")}int{bits}_t", bits, signed ? "DW_ATE_signed" : "DW_ATE_unsigned"));
                case var _ when type == SilType.Unit:
                    return null;

                case var _ when type == SilType.String: {
                    var bytes = Node($"!DICompositeType(tag: DW_TAG_array_type, baseType: {Basic("char", 8, "DW_ATE_signed_char")}, elements: !{{{Node("!DISubrange(count: -1)")}}})");
                    var body = Structure("SableString", 32, [
                        Member("$header", ObjectHeader, 16, 0, artificial: true),
                        Member("length", Int64, 8, 16),
                        Member("count", Int64, 8, 24),
                        Member("bytes", bytes, 0, 32),
                    ]);
                    return Typedef("String", Pointer(body));
                }

                case var _ when type == SilType.Object || type == SilType.Region:
                    return Typedef(type.ToString(), Pointer(ObjectHeader));

                case AddressType address:
                    return Pointer(Type(address.Pointee));

                case InlinePointerType or StructType { IsInline: true }:
                    return Typedef(DisplayName(type), Pointer(Basic("uint8_t", 8, "DW_ATE_unsigned_char")));

                case FunctionType function:
                    return Typedef(DisplayName(function), Pointer(Subroutine(function)));

                case ThickFunctionType thick: {
                    var body = Structure(DisplayName(thick) + "$closure", 16, [
                        Member("$function", Pointer(Subroutine(thick.Implementation)), 8, 0),
                        Member("$context", Pointer(ObjectHeader), 8, 8),
                    ]);
                    return Typedef(DisplayName(thick), body);
                }

                case WeakStorageType weak: {
                    var entry = Structure("SableWeakEntry", 16, [
                        Member("object", Type(weak.Referent), 8, 0),
                        Member("count", Int64, 8, 8),
                    ]);
                    return Typedef(DisplayName(weak), Pointer(entry));
                }

                case UnownedStorageType unowned:
                    return Typedef(DisplayName(unowned), Type(unowned.Referent));

                case TupleType tuple:
                    return Aggregate(DisplayName(tuple), tuple.Elements.Select(static (e, i) => (i.ToString(System.Globalization.CultureInfo.InvariantCulture), e)), Size(tuple), live: false);

                case StructType structType when structType.Name.StartsWith("any$", StringComparison.Ordinal):
                    return Typedef(DisplayName(structType), Aggregate(DisplayName(structType) + "$existential", structType.Fields, Size(structType), live: false));

                case StructType structType:
                    return Aggregate(structType.Name, structType.Fields, Size(structType), live: structType.Deinit is not null);

                case EnumType enumType when IsNullable(enumType):
                    return Typedef(DisplayName(enumType), Type(enumType.Cases[SomeCase(enumType)].Payload!));

                case EnumType enumType: {
                    var tag = Node(
                        $"!DICompositeType(tag: DW_TAG_enumeration_type, name: {Quote(DisplayName(enumType) + "$tag")}, size: 64, baseType: {Int64}, "
                        + $"elements: !{{{string.Join(", ", enumType.Cases.Select((c, i) => Node($"!DIEnumerator(name: {Quote(c.Name)}, value: {i})")))}}})"
                    );

                    // Nothing but cases: the enumeration is the whole value.
                    if (PayloadWords(enumType) == 0) {
                        return Typedef(DisplayName(enumType), tag);
                    }

                    var payloads = enumType.Cases.Where(static c => c.Payload is not null && c.Payload != SilType.Unit)
                        .Select(c => Member(c.Name, Payload(enumType, c.Name, c.Payload!), Size(c.Payload!), 0));
                    var union = Structure(DisplayName(enumType) + "$payload", PayloadWords(enumType) * 8, payloads, "DW_TAG_union_type");
                    var body = Structure(DisplayName(enumType) + (OptionalPayload(enumType) is null ? "$union" : "$optional"), Size(enumType), [
                        Member("$tag", tag, 8, 0),
                        Member("$payload", union, PayloadWords(enumType) * 8, 8),
                    ]);
                    return Typedef(DisplayName(enumType), body);
                }

                case ClassType:
                    return Type(type);

                default:
                    return null;
            }
        }

        /// <summary>
        ///     A union case's payload: the value or tuple it is — or, when its
        ///     fields have names, a struct of them laid out as that is, named
        ///     <c>U.Case$case</c> for the plugin to show as the case's fields.
        /// </summary>
        string? Payload(EnumType union, string caseName, SilType payload) {
            if (!union.PayloadLabels.TryGetValue(caseName, out var labels)) {
                return Type(payload);
            }

            var fields = payload is TupleType tuple ? labels.Zip(tuple.Elements) : [(labels[0], payload)];
            return Aggregate($"{DisplayName(union)}.{caseName}$case", fields, Size(payload), live: false);
        }

        string Subroutine(FunctionType function) {
            var types = new List<string> { function.Thrown is null && function.Result != SilType.Unit ? Type(function.Result) ?? "null" : "null" };
            types.AddRange(function.Parameters.Select(p => Type(p) ?? "null"));
            return Node($"!DISubroutineType(types: !{{{string.Join(", ", types)}}})");
        }

        /// <summary>A struct or tuple, field by field, after the hidden live byte of one with a deinit.</summary>
        string Aggregate(string name, IEnumerable<(string Name, SilType Type)> fields, int size, bool live) {
            var members = new List<string>();
            var offset = 0;
            if (live) {
                members.Add(Member("$live", Basic("bool", 8, "DW_ATE_boolean"), 1, 0, artificial: true));
                offset = 1;
            }

            foreach (var (fieldName, fieldType) in fields) {
                var align = Align(fieldType);
                offset = (offset + align - 1) / align * align;
                if (Type(fieldType) is { } fieldNode) {
                    members.Add(Member(fieldName, fieldNode, Size(fieldType), offset));
                }

                offset += Size(fieldType);
            }

            return Structure(name, size, members);
        }

        static string Quote(string text) =>
            "\"" + text.Replace("\\", "\\5C", StringComparison.Ordinal).Replace("\"", "\\22", StringComparison.Ordinal) + "\"";

        /// <summary>
        ///     The metadata, and the global that keeps the classes' types: one
        ///     the program never reads, whose type has a member per class. A
        ///     type no variable has is dropped by <c>dsymutil</c>, retained
        ///     type or not; one a global variable has is kept.
        /// </summary>
        public string Metadata() {
            var output = new StringBuilder();
            var dwarf = Node("!{i32 7, !\"Dwarf Version\", i32 5}");
            var version = Node("!{i32 2, !\"Debug Info Version\", i32 3}");
            var globals = new List<string>(_globals);
            if (_classes.Count > 0) {
                var holder = Structure("$SableClasses", 8 * _classes.Count, _classes.Select((c, i) => Member(c.Name, c.Node, 8, 8 * i)));
                var variable = Node(
                    $"!DIGlobalVariable(name: \"sable.debug.classes\", scope: {Unit}, file: {_firstFile}, line: 0, type: {holder}, isLocal: true, isDefinition: true)",
                    distinct: true
                );
                var expression = Node($"!DIGlobalVariableExpression(var: {variable}, expr: !DIExpression())");
                globals.Add(expression);
                output.Append($"\n@\"sable.debug.classes\" = internal constant [{_classes.Count} x i64] zeroinitializer, !dbg {expression}\n")
                    .Append("@llvm.compiler.used = appending global [1 x ptr] [ptr @\"sable.debug.classes\"], section \"llvm.metadata\"\n");
            }

            Define(Unit,
                $"distinct !DICompileUnit(language: DW_LANG_C, file: {_firstFile}, producer: \"sable\", isOptimized: false, "
                + $"runtimeVersion: 0, emissionKind: FullDebug, globals: {(globals.Count == 0 ? "!{}" : Node($"!{{{string.Join(", ", globals)}}}"))})");
            output.Append($"\n!llvm.dbg.cu = !{{{Unit}}}\n!llvm.module.flags = !{{{dwarf}, {version}}}\n\n");
            foreach (var node in _nodes) {
                output.Append(node).Append('\n');
            }

            return output.ToString();
        }
    }
}
