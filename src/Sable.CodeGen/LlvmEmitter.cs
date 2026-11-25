using System.Numerics;
using System.Globalization;
using System.Text;
using Sable.SIL;

namespace Sable.CodeGen;

/// <summary>
///     Writes a SIL module as LLVM IR text (docs/04 §2.3).
/// </summary>
/// <remarks>
///     <para>
///         The plan's backend emits object files through LLVM's C API; text is
///         where it starts, because it needs nothing but a <c>clang</c> to turn
///         into a binary and it is what <c>--emit=llvm-ir</c> shows anyway.
///         Moving to LLVMSharp later changes how the module is built, not what
///         is in it.
///     </para>
///     <para>
///         The mapping: <c>Int</c> is <c>i64</c>, <c>Bool</c> <c>i1</c>,
///         <c>F64</c> <c>double</c>, <c>Char</c> <c>i32</c>; a string, an
///         object and an array are each a <c>ptr</c> to a counted object
///         (docs/06 §6.2), a string literal an immortal one in read-only
///         memory; an address is a <c>ptr</c>. Copying and destroying a value
///         retain and release the references in it, through a function per
///         aggregate type — its value witnesses. Block arguments
///         become <c>phi</c>s; a <c>cond_br</c> that passes arguments gets an
///         edge block per target, so each <c>phi</c> sees one value per
///         predecessor. Integer arithmetic checks for overflow with LLVM's
///         intrinsics and branches to a trap, as do division by zero and
///         out-of-range shifts — the language's promise, kept in the code.
///     </para>
/// </remarks>
public sealed partial class LlvmEmitter {
    readonly StringBuilder _globals = new();
    readonly StringBuilder _helpers = new();
    readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);

    /// <summary>The arrays of constants written so far, by their type and elements: the same array shares one.</summary>
    readonly Dictionary<string, string> _constantArrays = new(StringComparer.Ordinal);
    readonly HashSet<string> _emitted = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _trapMessages = new(StringComparer.Ordinal);

    /// <param name="target">What the program is built for, whose C calling convention it follows; the host when null.</param>
    public static string Emit(SilModule module, NativeTarget? target = null) =>
        new LlvmEmitter { _target = target ?? NativeTarget.Host, _arch = (target ?? NativeTarget.Host).Arch, _windows = (target ?? NativeTarget.Host).Platform == NativePlatform.Windows }.EmitModule(module);

    /// <summary>The same, with debug information, so that a debugger can show frames, locals and types.</summary>
    /// <param name="directory">The directory the source file names are relative to.</param>
    /// <param name="target">What the program is built for: a Windows target is described in CodeView, any other in DWARF 5.</param>
    /// <param name="optimised">The program was optimised: the compile unit says so, and a debugger trusts what it reads less.</param>
    /// <param name="sources">
    ///     The text of files no debugger could find on disk — the standard
    ///     library's, embedded in the compiler — by the name a function's
    ///     <see cref="SilFunction.SourceFile" /> gives: carried in the line
    ///     table, so that stepping into the library shows its lines.
    /// </param>
    public static string EmitWithDebugInfo(SilModule module, string directory, NativeTarget? target = null, bool optimised = false, IReadOnlyDictionary<string, string>? sources = null) =>
        new LlvmEmitter {
            _debug = new DebugInfo(directory, codeView: (target ?? NativeTarget.Host).Platform == NativePlatform.Windows, optimised, sources), _target = target ?? NativeTarget.Host, _arch = (target ?? NativeTarget.Host).Arch, _windows = (target ?? NativeTarget.Host).Platform == NativePlatform.Windows,
        }.EmitModule(module);

    DebugInfo? _debug;

    /// <summary>What the program is built for.</summary>
    NativeTarget _target = NativeTarget.Host;

    /// <summary>The target's architecture, as <see cref="NativeTarget.Arch" /> names it.</summary>
    string _arch = "arm64";

    /// <summary>
    ///     The target is Windows: its C calling convention is Microsoft's
    ///     (LlvmEmitter.CAbi.cs), and a definition the linker keeps one copy
    ///     of (<c>linkonce_odr</c>) goes in a COMDAT of its own name — COFF's
    ///     linker keeps one of several only when each is in one, and reports
    ///     the rest as duplicates. ELF and Mach-O need none.
    /// </summary>
    bool _windows;

    readonly List<string> _comdatNames = [];

    /// <summary>What a <c>linkonce_odr</c> definition of <paramref name="name" /> (as <c>@"..."</c>) adds after its value: its COMDAT, where the target needs one.</summary>
    string Comdat(string name, string linkage) {
        if (!_windows || !linkage.StartsWith("linkonce_odr", StringComparison.Ordinal)) {
            return "";
        }

        _comdatNames.Add(name);
        return ", comdat";
    }

    bool _checkExclusivity = true;

    // Numbered high, clear of the debug information's nodes.
    const string Unpredictable = "!9000011";
    const string TbaaRoot = "!9000000", TbaaHeaderType = "!9000001", TbaaCountType = "!9000002", TbaaValueType = "!9000003", TbaaScalarType = "!9000004", TbaaReferenceType = "!9000005";
    const string TbaaHeader = "!9000006", TbaaCount = "!9000007", TbaaValue = "!9000008", TbaaScalar = "!9000009", TbaaReference = "!9000010";

    /// <summary>
    ///     What an access of a value of the type says of what it may alias: a
    ///     number is never where a reference is, and an aggregate may hold
    ///     either. Storage has one type for as long as it lasts.
    /// </summary>
    static string TbaaOf(string type) =>
        type switch {
            "ptr" => TbaaReference,
            "half" or "bfloat" or "float" or "double" => TbaaScalar,
            _ when type[0] == 'i' && type.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0 => TbaaScalar,
            _ => TbaaValue,
        };

    /// <summary>The same, after an access's alignment: for storage that is a value's own — a local's, a field's, an element's, a caller's — and nothing for what may be anything's bits.</summary>
    static string TbaaAt(SilValue address, SilType type) => IsTyped(address) ? $", !tbaa {TbaaOf(Type(type))}" : "";

    static bool IsTyped(SilValue address) =>
        address switch {
            AllocStackInst => true,
            SilArgument argument => argument.Block == argument.Block.Function.Entry,
            RefElementAddrInst { Reference.Type: ClassType { IsCoroutineFrame: false } } => true,
            StructElementAddrInst { Address.Type: AddressType { Pointee: StructType { IsInline: false } } } element => IsTyped(element.Address),
            TupleElementAddrInst element => IsTyped(element.Address),
            BuiltinInst { Name: "array_element_addr" or "array_element_addr_unique" } => true,
            _ => false,
        };

    SilModule? _module;
    HashSet<string>? _reachable;
    HashSet<ClassType>? _inherited;

    /// <summary>
    ///     True for a class no other class inherits from, in a whole program;
    ///     one unit of a program compiled separately cannot know the others'.
    /// </summary>
    bool IsLeaf(ClassType type) {
        if (_module is not { Unit: null } module) {
            return false;
        }

        _inherited ??= [..module.Types.OfType<ClassType>().Select(static c => c.Superclass).OfType<ClassType>()];
        return !_inherited.Contains(type);
    }

    string EmitModule(SilModule module) {
        AsyncLowering.Run(module);
        _module = module;

        // A specialised build that opens a Type has code compiled once too, which may make a generic class's object.
        module.UnspecialisedGenerics |= module.Functions.Exists(static f => f.IsUnspecialised);
        _inherited = null;
        TypeLayout.PointerBytes = _arch == "wasm32" ? 4 : 8;
        _checkExclusivity = module.CheckExclusivity;
        _debug?.Begin(module);

        // Only what the program reaches is built: the library's other functions
        // are not. One unit of a separately compiled program cannot know what
        // the rest reaches, so it builds everything it has.
        _reachable = module.Unit is null ? SilPassManager.Reachable(module) : null;
        var functions = new StringBuilder();
        foreach (var function in module.Functions.Where(f => _reachable?.Contains(f.Name) ?? true)) {
            Function(function, functions);
        }

        if (module.EntryPoint is { } entry && module.Find(entry) is { } main) {
            functions.Append(MainShim(main, keepEntry: _debug is not null, wasm: _arch == "wasm32"));
        }

        // What runs as the image is loaded: a library's start, and the witness tables a unit registers.
        var constructors = new List<string>();
        if (WitnessRegistration(module) is { } registration) {
            constructors.Add(registration);
        }

        if (GlobalsRegistration(module) is { } globals) {
            constructors.Add(globals);
        }

        if (TemplateRegistration(module) is { } templates) {
            constructors.Add(templates);
        }

        if (GenericDeinitRegistration(module) is { } genericDeinits) {
            constructors.Add(genericDeinits);
        }

        if (DescriberRegistration(module) is { } describers) {
            constructors.Add(describers);
        }

        if (ErrorEraserRegistration(module) is { } erasers) {
            constructors.Add(erasers);
        }

        if (GenericClassRegistration(module) is { } classes) {
            constructors.Add(classes);
        }

        if (BoxerRegistration(module) is { } boxers) {
            constructors.Add(boxers);
        }

        if (module.IsLibrary) {
            functions.Append(LibraryLifetime(module));
            constructors.Add("@\"sable.library.start\"");
        }

        if (constructors.Count > 0) {
            functions.Append($"@llvm.global_ctors = appending global [{constructors.Count} x {{ i32, ptr, ptr }}] [")
                .Append(string.Join(", ", constructors.Select(static c => $"{{ i32, ptr, ptr }} {{ i32 65535, ptr {c}, ptr null }}")))
                .Append("]\n\n");
        }

        var output = new StringBuilder();
        output.Append("; Generated by the Sable compiler from SIL.\n\n");
        output.Append("%SableMetadata = type { ptr, ptr, i64, ptr, ptr, i64, i64, ptr, ptr }\n");
        output.Append($"%SableTypeMetadata = type {TypeMetadataRecord}\n");
        output.Append("%SableInlineSlot = type { i32, i32, i32, i32, i32, i32, i32, ptr }\n");
        // The runtime's headers (docs/06 §6.2), whose fields the code reads in
        // place: laid out by the target's data layout, as clang lays out the
        // runtime's structs — not at offsets that hold for 64-bit pointers only.
        output.Append("%SableObjectHeader = type { ptr, i32, i32 }\n");
        output.Append("%SableStringHeader = type { %SableObjectHeader, i64, i64 }\n");
        output.Append("%SableArrayHeader = type { %SableObjectHeader, i64, i64, ptr }\n\n");
        // Globals: zero until the entry point's start initialises them.
        // Separately compiled, every unit has the globals, and the linker keeps one of each.
        // One C sees ('@export var') is named for C, and visible to it.
        foreach (var (globalName, globalType) in module.Globals) {
            var exported = IsExportedGlobal(globalName);
            var linkage = module.Unit is not null ? "linkonce_odr" : exported ? "dso_local" : "internal";
            output.Append(GlobalStorage(globalName)).Append($" = {linkage} global ")
                .Append(Type(globalType)).Append(" zeroinitializer").Append(Comdat(GlobalStorage(globalName), linkage)).Append(StorageAlign(globalType));
            if (_debug?.GlobalVariable(globalName, globalType, local: module.Unit is null) is { } variable) {
                output.Append(", !dbg ").Append(variable);
            }

            output.Append('\n');
        }

        output.Append(_globals);
        if (_globals.Length > 0) {
            output.Append('\n');
        }

        // The checked I128 multiply helper is linkonce_odr like the rest:
        // on COFF that needs a COMDAT, or every unit's copy is a duplicate.
        if (_windows) {
            output.Append(Declarations.Replace(SmulOverflowHeader, SmulOverflowHeader[..^1] + "comdat {", StringComparison.Ordinal));
            _comdatNames.Add("@sable_smul_overflow_i128");
        } else {
            output.Append(Declarations);
        }
        output.Append(MetadataDeclarations);
        output.Append(IntrinsicDeclarations);
        output.Append(MathDeclarations);
        foreach (var declaration in _simdDeclarations) {
            output.Append(declaration).Append('\n');
        }
        output.Append('\n');
        output.Append(RetainAndRelease(module.CheckArc));
        output.Append('\n');
        output.Append(functions);
        output.Append(ForeignFunctions(module));
        output.Append(ExportedFunctions(module));
        output.Append(_helpers);
        if (module.Unit is { } unit) {
            output.Append(ExternalDeclarations(unit, output));
        }

        foreach (var name in _comdatNames.Distinct(StringComparer.Ordinal)) {
            output.Append('$').Append(name[1..]).Append(" = comdat any\n");
        }

        if (_debug is not null) {
            output.Append(_debug.Metadata());
        }

        return output.ToString();
    }

    /// <summary>
    ///     Retain and release, inline: the runtime's <c>sable_retain</c> and
    ///     <c>sable_release</c>, which only a link-time optimised build could
    ///     otherwise inline — a call for every count, on Linux and Windows
    ///     without lld, or with LTO off. The object header is { metadata,
    ///     strong count, flags }; an immortal object (a string literal) is not
    ///     counted, nor is null. An object more than one thread can reach
    ///     (the flags' shared bit, 512) is counted atomically, the rest with a
    ///     plain load and store (docs/02 §2.7). Only a last release calls the
    ///     runtime, to run the deinit and free the memory. Under SABLE_STATS
    ///     (<c>sable_counting</c>) the runtime's counts of retains and
    ///     releases are kept here as there. With <paramref name="checkArc" />
    ///     (a profile's <c>arc-checks</c>) a retain or release of an object
    ///     whose count is already zero — whose deinit has run, so that the
    ///     reference outlived it — calls the runtime's <c>sable_arc_dead</c>,
    ///     which traps, rather than counting what may be freed memory. (The
    ///     runtime's own retains and releases, compiled once for every
    ///     program, do not check.)
    /// </summary>
    public static string RetainAndRelease(bool checkArc) => checkArc ? CheckedCounting : Counting;

    /// <summary>
    ///     Type-based alias information (docs/04 §3.8): an array's header —
    ///     its count, its buffer — is never a value, and an object's count of
    ///     references is neither, so a store to an element or a field, or a
    ///     retain, leaves them where LLVM last read them; and a number is
    ///     never a reference, so a store of one leaves the array or the object
    ///     a variable holds. With the counting, which every module has.
    /// </summary>
    static string AliasTypes() {
        var text = new StringBuilder($"\n{Unpredictable} = !{{}}\n{TbaaRoot} = !{{!\"Sable TBAA\"}}\n")
            .Append($"{TbaaHeaderType} = !{{!\"array header\", {TbaaRoot}, i64 0}}\n")
            .Append($"{TbaaCountType} = !{{!\"reference count\", {TbaaRoot}, i64 0}}\n")
            .Append($"{TbaaValueType} = !{{!\"value\", {TbaaRoot}, i64 0}}\n")
            .Append($"{TbaaScalarType} = !{{!\"number\", {TbaaValueType}, i64 0}}\n")
            .Append($"{TbaaReferenceType} = !{{!\"reference\", {TbaaValueType}, i64 0}}\n");
        foreach (var (tag, type) in new[] { (TbaaHeader, TbaaHeaderType), (TbaaCount, TbaaCountType), (TbaaValue, TbaaValueType), (TbaaScalar, TbaaScalarType), (TbaaReference, TbaaReferenceType) }) {
            text.Append($"{tag} = !{{{type}, {type}, i64 0}}\n");
        }

        return text.ToString();
    }

    static readonly string Counting = BuildCounting(false) + AliasTypes();
    static readonly string CheckedCounting = BuildCounting(true) + AliasTypes();

    static string BuildCounting(bool check) {
        string Dead(int operation) => check
            ? $$"""
                 %dead = icmp eq i32 %strong, 0
                 br i1 %dead, label %deinited, label %plain, !prof !{!"branch_weights", i32 1, i32 1000}
               deinited:
                 call void @sable_arc_dead(ptr %object, i32 {{operation}})
                 unreachable
               plain:

               """
            : "";

        // The shared path reads the count first only to check it is not zero.
        string SharedDead(int operation) => check
            ? $$"""
                 %sharedStrong = load atomic i32, ptr %strongField monotonic, align 4
                 %sharedDead = icmp eq i32 %sharedStrong, 0
                 br i1 %sharedDead, label %sharedDeinited, label %sharedLive, !prof !{!"branch_weights", i32 1, i32 1000}
               sharedDeinited:
                 call void @sable_arc_dead(ptr %object, i32 {{operation}})
                 unreachable
               sharedLive:

               """
            : "";

        return $"""
                @sable_retains = external global i64
                @sable_releases = external global i64
                @sable_counting = external global i8
                @sable_threaded = external global i8
                declare void @sable_release_last(ptr) cold
                {(check ? "declare void @sable_arc_dead(ptr, i32) cold noreturn" : "")}

                define internal void @sable.retain(ptr %object) alwaysinline nounwind {"{"}
                entry:
                  %none = icmp eq ptr %object, null
                  br i1 %none, label %done, label %present
                present:
                  %flagsField = getelementptr inbounds {"{"} ptr, i32, i32 {"}"}, ptr %object, i32 0, i32 2
                  %flags = load atomic i32, ptr %flagsField unordered, align 4
                  %special = and i32 %flags, 513
                  %ordinary = icmp eq i32 %special, 0
                  %strongField = getelementptr inbounds {"{"} ptr, i32, i32 {"}"}, ptr %object, i32 0, i32 1
                  br i1 %ordinary, label %local, label %unusual, !prof !{"{"}!"branch_weights", i32 1000, i32 1{"}"}
                local:
                  %strong = load i32, ptr %strongField, !tbaa {TbaaCount}
                {Dead(0)}  %more = add i32 %strong, 1
                  store i32 %more, ptr %strongField, !tbaa {TbaaCount}
                  br label %counted
                unusual:
                  %immortalBit = and i32 %flags, 1
                  %immortal = icmp ne i32 %immortalBit, 0
                  br i1 %immortal, label %done, label %shared
                shared:
                {SharedDead(0)}  %old = atomicrmw add ptr %strongField, i32 1 monotonic, align 4
                  br label %counted
                counted:
                  %counting = load i8, ptr @sable_counting
                  %counts = icmp ne i8 %counting, 0
                  br i1 %counts, label %count, label %done, !prof !{"{"}!"branch_weights", i32 1, i32 1000{"}"}
                count:
                  %retains = atomicrmw add ptr @sable_retains, i64 1 monotonic, align 8
                  br label %done
                done:
                  ret void
                {"}"}

                define internal void @sable.share(ptr %object) alwaysinline nounwind {"{"}
                entry:
                  %none = icmp eq ptr %object, null
                  br i1 %none, label %done, label %present
                present:
                  %flagsField = getelementptr inbounds {"{"} ptr, i32, i32 {"}"}, ptr %object, i32 0, i32 2
                  %flags = load atomic i32, ptr %flagsField monotonic, align 4
                  %special = and i32 %flags, 513
                  %ordinary = icmp eq i32 %special, 0
                  br i1 %ordinary, label %local, label %done
                local:
                  call void @sable_share(ptr %object)
                  br label %done
                done:
                  ret void
                {"}"}

                define internal void @sable.release(ptr %object) alwaysinline nounwind {"{"}
                entry:
                  %none = icmp eq ptr %object, null
                  br i1 %none, label %done, label %present
                present:
                  %flagsField = getelementptr inbounds {"{"} ptr, i32, i32 {"}"}, ptr %object, i32 0, i32 2
                  %flags = load atomic i32, ptr %flagsField unordered, align 4
                  %special = and i32 %flags, 513
                  %ordinary = icmp eq i32 %special, 0
                  %strongField = getelementptr inbounds {"{"} ptr, i32, i32 {"}"}, ptr %object, i32 0, i32 1
                  %counting = load i8, ptr @sable_counting
                  %counts = icmp ne i8 %counting, 0
                  br i1 %ordinary, label %local, label %unusual, !prof !{"{"}!"branch_weights", i32 1000, i32 1{"}"}
                local:
                  %strong = load i32, ptr %strongField, !tbaa {TbaaCount}
                {Dead(1)}  %less = sub i32 %strong, 1
                  store i32 %less, ptr %strongField, !tbaa {TbaaCount}
                  %gone = icmp eq i32 %less, 0
                  br i1 %gone, label %last, label %counted, !prof !{"{"}!"branch_weights", i32 1, i32 1000{"}"}
                unusual:
                  %immortalBit = and i32 %flags, 1
                  %immortal = icmp ne i32 %immortalBit, 0
                  br i1 %immortal, label %done, label %shared
                shared:
                {SharedDead(1)}  %old = atomicrmw sub ptr %strongField, i32 1 acq_rel, align 4
                  %sharedGone = icmp eq i32 %old, 1
                  br i1 %sharedGone, label %last, label %counted, !prof !{"{"}!"branch_weights", i32 1, i32 1000{"}"}
                last:
                  br i1 %counts, label %countLast, label %free
                countLast:
                  %releasesLast = atomicrmw add ptr @sable_releases, i64 1 monotonic, align 8
                  br label %free
                free:
                  call void @sable_release_last(ptr %object)
                  br label %done
                counted:
                  br i1 %counts, label %count, label %done, !prof !{"{"}!"branch_weights", i32 1, i32 1000{"}"}
                count:
                  %releases = atomicrmw add ptr @sable_releases, i64 1 monotonic, align 8
                  br label %done
                done:
                  ret void
                {"}"}
                """;
    }

    /// <summary>
    ///     The LLVM intrinsics (and the two libm functions) the numeric
    ///     protocols' builtins call, at each width: bit counts and swaps,
    ///     funnel shifts for rotation, saturating and overflow-reporting
    ///     arithmetic, and the floats' IEEE operations.
    /// </summary>
    static readonly string IntrinsicDeclarations = BuildIntrinsicDeclarations();

    static string BuildIntrinsicDeclarations() {
        var text = new StringBuilder();
        foreach (var bits in new[] { 8, 16, 32, 64, 128 }) {
            var t = $"i{bits}";
            text.Append($"declare {t} @llvm.ctlz.{t}({t}, i1)\n")
                .Append($"declare {t} @llvm.cttz.{t}({t}, i1)\n")
                .Append($"declare {t} @llvm.ctpop.{t}({t})\n")
                .Append($"declare {t} @llvm.bitreverse.{t}({t})\n")
                .Append($"declare {t} @llvm.fshl.{t}({t}, {t}, {t})\n")
                .Append($"declare {t} @llvm.fshr.{t}({t}, {t}, {t})\n");
            if (bits > 8) {
                text.Append($"declare {t} @llvm.bswap.{t}({t})\n");
            }

            foreach (var op in new[] { "sadd", "ssub", "uadd", "usub" }) {
                text.Append($"declare {t} @llvm.{op}.sat.{t}({t}, {t})\n");
            }
        }

        text.Append("declare i64 @llvm.smax.i64(i64, i64)\n");

        foreach (var t in new[] { "half", "float", "double" }) {
            var suffix = t switch { "half" => "f16", "float" => "f32", _ => "f64" };
            foreach (var op in new[] { "sqrt", "floor", "ceil", "trunc", "round", "nearbyint" }) {
                text.Append($"declare {t} @llvm.{op}.{suffix}({t})\n");
            }

            foreach (var op in new[] { "copysign", "minimum", "maximum" }) {
                text.Append($"declare {t} @llvm.{op}.{suffix}({t}, {t})\n");
            }

            text.Append($"declare {t} @llvm.fma.{suffix}({t}, {t}, {t})\n")
                .Append($"declare {t} @llvm.ldexp.{suffix}.i32({t}, i32)\n")
                .Append($"declare {{ {t}, i32 }} @llvm.frexp.{suffix}.i32({t})\n");
        }

        // IEEE remainder has no intrinsic: C's, from libm.
        text.Append("declare double @remainder(double, double)\n")
            .Append("declare float @remainderf(float, float)\n\n");
        return text.ToString();
    }

    const string SmulOverflowHeader = "@sable_smul_overflow_i128(i128 %a, i128 %b) {";

    const string Declarations = """
                                @sable_string_metadata = external constant %SableMetadata

                                declare void @sable_print_string(ptr)
                                declare void @sable_print_int(i64)
                                declare void @sable_print_f64(double)
                                declare void @sable_print_bool(i1 zeroext)
                                declare void @sable_trap(ptr) noreturn cold
                                declare void @sable_trap_at(ptr, ptr) noreturn cold
                                declare void @sable_trap_with(ptr, ptr) noreturn cold
                                declare ptr @sable_backtrace_capture()
                                declare void @sable_trap_uncaught(ptr, ptr) noreturn cold
                                declare void @sable_trap_hook_set(ptr, ptr)
                                declare void @sable_trap_hook_take(ptr, ptr, ptr)
                                declare zeroext i1 @sable_debugger_attached()
                                declare void @llvm.debugtrap()
                                @sable_trap_site = external thread_local global ptr
                                declare ptr @sable_alloc(ptr, i64)
                                declare ptr @sable_alloc_uninitialised(ptr, i64)
                                declare void @sable_retain(ptr)
                                declare void @sable_release(ptr)
                                declare void @sable_destroy_uncounted(ptr)
                                declare void @sable_heap_visit(ptr)
                                declare void @sable_heap_visit_unowned(ptr)
                                declare void @sable_make_immortal(ptr)
                                declare void @sable_array_destroy(ptr)
                                declare void @sable_unowned_retain(ptr)
                                declare void @sable_unowned_release(ptr)
                                declare void @sable_unowned_load(ptr)
                                declare ptr @sable_weak_entry(ptr)
                                declare void @sable_weak_retain(ptr)
                                declare void @sable_weak_release(ptr)
                                declare ptr @sable_weak_load(ptr)
                                declare zeroext i1 @sable_string_equal(ptr, ptr)
                                declare ptr @sable_string_concat(ptr, ptr)
                                declare ptr @sable_string_concat_many(i64, ptr)
                                declare i64 @sable_string_count(ptr)
                                declare i32 @sable_string_at(ptr, i64)
                                declare ptr @sable_string_slice(ptr, i64, i64)
                                declare zeroext i1 @sable_string_contains(ptr, i32)
                                declare i64 @sable_string_index_of(ptr, i32)
                                declare ptr @sable_string_trimmed(ptr)
                                declare void @sable_io_write(ptr)
                                declare void @sable_io_write_error(ptr)
                                declare ptr @sable_io_read_line()
                                declare ptr @sable_io_read_file(ptr)
                                declare zeroext i1 @sable_io_write_file(ptr, ptr, i1 zeroext)
                                declare i64 @sable_file_error()
                                declare i64 @sable_file_error_code()
                                declare i64 @sable_file_open(ptr, i64, i64)
                                declare ptr @sable_file_read(i64, i64, i64, ptr)
                                declare i64 @sable_file_write(i64, ptr, i64, i64, i64)
                                declare i64 @sable_file_seek(i64, i64, i64)
                                declare zeroext i1 @sable_file_sync(i64)
                                declare zeroext i1 @sable_file_set_length(i64, i64)
                                declare ptr @sable_file_metadata(i64, ptr)
                                declare zeroext i1 @sable_file_close(i64)
                                declare i64 @sable_file_standard(i64)
                                declare zeroext i1 @sable_file_copy(ptr, ptr, i1 zeroext)
                                declare ptr @sable_fs_metadata(ptr, i1 zeroext, ptr)
                                declare zeroext i1 @sable_fs_set_permissions(ptr, i64)
                                declare zeroext i1 @sable_fs_set_time(ptr, i64, i64, i64)
                                declare zeroext i1 @sable_fs_make_directory(ptr, i64)
                                declare zeroext i1 @sable_fs_remove(ptr, i1 zeroext)
                                declare zeroext i1 @sable_fs_remove_tree(ptr)
                                declare zeroext i1 @sable_fs_rename(ptr, ptr, i1 zeroext)
                                declare zeroext i1 @sable_fs_link(ptr, ptr, i64)
                                declare ptr @sable_fs_read_link(ptr, ptr)
                                declare i64 @sable_fs_open_directory(ptr)
                                declare ptr @sable_fs_next_entry(i64, ptr)
                                declare void @sable_fs_close_directory(i64)
                                declare i64 @sable_fs_watch(ptr, i1 zeroext)
                                declare i64 @sable_fs_watch_event(i64)
                                declare ptr @sable_fs_watch_take(i64, ptr)
                                declare void @sable_fs_unwatch(i64)
                                declare ptr @sable_file_canonical(ptr)
                                declare ptr @sable_file_map(i64, i64, i64)
                                declare zeroext i1 @sable_file_map_store(ptr, i64, ptr, i64, i64)
                                declare zeroext i1 @sable_file_map_flush(ptr, i64, i64)
                                declare void @sable_file_map_advise(ptr, i64)
                                declare zeroext i1 @sable_io_file_exists(ptr)
                                declare zeroext i1 @sable_io_is_directory(ptr)
                                declare zeroext i1 @sable_io_remove_file(ptr)
                                declare zeroext i1 @sable_io_make_directory(ptr)
                                declare ptr @sable_io_list_directory(ptr)
                                declare ptr @sable_io_environment(ptr)
                                declare i64 @sable_io_argument_count()
                                declare ptr @sable_io_argument(i64)
                                declare ptr @sable_io_argument_bytes(i64, ptr)
                                declare zeroext i1 @sable_env_set(ptr, ptr)
                                declare zeroext i1 @sable_env_remove(ptr)
                                declare ptr @sable_env_all()
                                declare ptr @sable_system_text(i64)
                                declare i64 @sable_system_number(i64)
                                declare i64 @sable_memory_statistic(i64)
                                declare zeroext i1 @sable_io_succeeded()
                                declare void @sable_set_arguments(i32, ptr)
                                declare i64 @sable_time_monotonic()
                                declare i64 @sable_time_suspending()
                                declare i64 @sable_time_wall_clock()
                                declare void @sable_time_sleep(i64)
                                declare ptr @sable_process_run(ptr, ptr, ptr)
                                declare i64 @sable_process_status()
                                declare ptr @sable_process_error_output()
                                declare i64 @sable_process_status_signal()
                                declare i64 @sable_process_error()
                                declare i64 @sable_process_error_code()
                                declare i64 @sable_process_spawn(ptr, ptr, ptr, i64, ptr, ptr)
                                declare i64 @sable_process_pipe(i64, i64)
                                declare i64 @sable_process_child_id(i64)
                                declare zeroext i1 @sable_process_ended(i64)
                                declare i64 @sable_process_exit_code(i64, i1 zeroext)
                                declare i64 @sable_process_exit_event(i64)
                                declare i64 @sable_process_signal(i64, i64)
                                declare void @sable_process_release(i64)
                                declare void @sable_process_exit(i64)
                                declare void @sable_process_abort()
                                declare i64 @sable_signal_watch(i64)
                                declare i64 @sable_signal_event(i64)
                                declare i64 @sable_signal_take(i64)
                                declare void @sable_signal_unwatch(i64)
                                declare zeroext i1 @sable_signal_raise(i64)
                                declare ptr @sable_process_current_directory()
                                declare ptr @sable_process_executable_path()
                                declare zeroext i1 @sable_process_change_directory(ptr)
                                declare i64 @sable_process_id()
                                declare i64 @sable_process_notify(ptr, ptr)
                                declare i64 @sable_memory_allocations()
                                declare ptr @sable_string_split(ptr, i32)
                                declare i64 @sable_string_hash(ptr, i64)
                                declare i64 @sable_string_hash_range(ptr, i64, i64, i64)
                                declare i64 @sable_string_count_range(ptr, i64, i64)
                                declare i64 @sable_string_compare_range(ptr, i64, i64, ptr, i64, i64)
                                declare i64 @sable_string_utf8_find_range(ptr, i64, i64, ptr, i64, i64)
                                declare i64 @sable_string_utf8_find_any(ptr, i64, i64, ptr)
                                declare i64 @sable_string_utf8_find_last_range(ptr, i64, i64, ptr, i64, i64)
                                @sable_hash_seed = external global i64
                                declare i64 @sable_random_bits()
                                declare void @sable_random_fill(ptr, i64)
                                declare void @sable_secure_zero(ptr, i64)
                                declare zeroext i1 @sable_crypto_blocks(i64, ptr, ptr, i64, i64)
                                declare zeroext i1 @sable_crypto_blocks_raw(i64, ptr, ptr, i64)
                                declare zeroext i1 @sable_vector_available()
                                @sable_processor_known = external global i32
                                declare float @sable_vector_reduce_F32(i64, ptr, i64, i64, ptr, i64)
                                declare double @sable_vector_reduce_F64(i64, ptr, i64, i64, ptr, i64)
                                declare void @sable_vector_map_F32(ptr, i64, ptr, i64, i64, ptr, i64)
                                declare void @sable_vector_map_F64(ptr, i64, ptr, i64, i64, ptr, i64)
                                declare i64 @sable_codec_bytes(i64, ptr, i64, i64, ptr, i64)
                                declare i64 @sable_codec_string(i64, ptr, i64, i64, ptr, i64)
                                declare i64 @sable_utf8_to_utf16(ptr, i64, i64, ptr, i64)
                                declare i64 @sable_utf16_to_utf8(ptr, i64, i64, ptr, i64)
                                declare ptr @sable_string_join(ptr, ptr)
                                declare i64 @sable_string_compare(ptr, ptr)
                                declare i64 @sable_string_find(ptr, ptr, i64)
                                declare ptr @sable_string_from_char(i32)
                                declare ptr @sable_string_from_int(i64)
                                declare ptr @sable_string_from_f64(double)
                                declare ptr @sable_string_from_bool(i1 zeroext)
                                declare zeroext i1 @sable_string_to_int(ptr, ptr)
                                declare zeroext i1 @sable_string_to_f64(ptr, i64, i64, ptr)
                                declare zeroext i1 @sable_string_to_f32(ptr, i64, i64, ptr)
                                declare zeroext i1 @sable_char_is_digit(i32)
                                declare zeroext i1 @sable_char_from_int(i64, ptr)
                                declare i64 @sable_array_count(ptr)
                                declare ptr @sable_array_element(ptr, i64)
                                declare ptr @sable_array_append_slot(ptr nocapture, ptr) cold
                                declare ptr @sable_array_mutable_element(ptr nocapture, ptr, i64) cold
                                declare ptr @sable_array_remove_last(ptr nocapture, ptr) cold
                                declare i64 @sable_array_capacity(ptr)
                                declare void @sable_array_reserve(ptr nocapture, ptr, i64)
                                declare void @sable_array_truncate(ptr nocapture, ptr, i64)
                                declare void @sable_array_append_utf8(ptr nocapture, ptr, ptr, i64, i64)
                                declare void @sable_array_append_all(ptr nocapture, ptr, ptr, i64, i64)
                                declare void @sable_array_append_holes(ptr nocapture, ptr, i64)
                                declare void @sable_array_append_repeating(ptr nocapture, ptr, ptr nocapture readonly, i64)
                                declare void @sable_array_rotate(ptr nocapture, ptr, i64, i64, i64)
                                declare void @sable_slice_rotate(ptr nocapture, ptr, i64, i64, i64)
                                declare void @sable_array_store_slice(ptr nocapture, ptr, i64, i64, ptr, i64, i64)
                                declare void @sable_slice_store_slice(ptr nocapture, ptr, i64, i64, ptr, i64, i64)
                                declare ptr @sable_slice_mutable_element(ptr nocapture, ptr, i64)
                                declare void @sable_slice_make_unique(ptr nocapture, ptr)
                                declare ptr @sable_array_of_slice(ptr, i64, i64, ptr)
                                declare ptr @sable_region_create()
                                declare void @sable_region_reset(ptr)
                                declare void @sable_region_push(ptr)
                                declare void @sable_region_pop(ptr)
                                declare ptr @sable_region_alloc(ptr, i64, ptr)
                                declare double @llvm.fabs.f64(double)
                                declare double @llvm.minnum.f64(double, double)
                                declare double @llvm.maxnum.f64(double, double)

                                declare { i64, i1 } @llvm.sadd.with.overflow.i64(i64, i64)
                                declare { i64, i1 } @llvm.ssub.with.overflow.i64(i64, i64)
                                declare { i64, i1 } @llvm.smul.with.overflow.i64(i64, i64)
                                declare i64 @llvm.smin.i64(i64, i64)
                                declare { i8, i1 } @llvm.sadd.with.overflow.i8(i8, i8)
                                declare { i8, i1 } @llvm.ssub.with.overflow.i8(i8, i8)
                                declare { i8, i1 } @llvm.smul.with.overflow.i8(i8, i8)
                                declare { i8, i1 } @llvm.uadd.with.overflow.i8(i8, i8)
                                declare { i8, i1 } @llvm.usub.with.overflow.i8(i8, i8)
                                declare { i8, i1 } @llvm.umul.with.overflow.i8(i8, i8)
                                declare { i16, i1 } @llvm.sadd.with.overflow.i16(i16, i16)
                                declare { i16, i1 } @llvm.ssub.with.overflow.i16(i16, i16)
                                declare { i16, i1 } @llvm.smul.with.overflow.i16(i16, i16)
                                declare { i16, i1 } @llvm.uadd.with.overflow.i16(i16, i16)
                                declare { i16, i1 } @llvm.usub.with.overflow.i16(i16, i16)
                                declare { i16, i1 } @llvm.umul.with.overflow.i16(i16, i16)
                                declare { i32, i1 } @llvm.sadd.with.overflow.i32(i32, i32)
                                declare { i32, i1 } @llvm.ssub.with.overflow.i32(i32, i32)
                                declare { i32, i1 } @llvm.smul.with.overflow.i32(i32, i32)
                                declare { i32, i1 } @llvm.uadd.with.overflow.i32(i32, i32)
                                declare { i32, i1 } @llvm.usub.with.overflow.i32(i32, i32)
                                declare { i32, i1 } @llvm.umul.with.overflow.i32(i32, i32)
                                declare { i64, i1 } @llvm.uadd.with.overflow.i64(i64, i64)
                                declare { i64, i1 } @llvm.usub.with.overflow.i64(i64, i64)
                                declare { i64, i1 } @llvm.umul.with.overflow.i64(i64, i64)
                                declare ptr @sable_inline_new(ptr)
                                declare zeroext i1 @sable_class_is(ptr, ptr)
                                declare zeroext i1 @sable_conforms(ptr, ptr)
                                declare ptr @sable_task_spawn(ptr, ptr, ptr, ptr, i64, i1 zeroext)
                                declare i64 @sable_task_priority()
                                declare void @sable_task_handler_push(ptr, ptr)
                                declare void @sable_task_handler_pop()
                                declare ptr @sable_group_new()
                                declare void @sable_group_add(ptr, ptr)
                                declare zeroext i1 @sable_group_wait(ptr)
                                declare ptr @sable_group_take(ptr)
                                declare void @sable_group_free(ptr)
                                declare zeroext i1 @sable_task_wait(ptr)
                                declare void @sable_task_release(ptr)
                                declare void @sable_task_cancel(ptr)
                                declare zeroext i1 @sable_task_is_cancelled()
                                declare zeroext i1 @sable_task_yield()
                                declare zeroext i1 @sable_task_sleep(i64)
                                declare void @sable_share(ptr) cold
                                declare void @sable_register_globals(ptr)
                                declare void @sable_threads_begin()
                                declare void @sable_object_unmade(ptr)
                                declare void @sable_task_spawn_cancelled()
                                declare void @sable_task_timed_begin()
                                declare zeroext i1 @sable_task_timed_woken()
                                declare void @sable_task_timed_end()
                                declare zeroext i1 @sable_actor_hop(ptr)
                                declare ptr @sable_actor_current()
                                declare ptr @sable_waitlist_new()
                                declare ptr @sable_lock_new()
                                declare void @sable_lock_free(ptr)
                                declare void @sable_lock_acquire(ptr)
                                declare void @sable_lock_acquire_later(ptr)
                                declare zeroext i1 @sable_lock_try_acquire(ptr)
                                declare void @sable_lock_release(ptr)
                                declare ptr @sable_rwlock_new()
                                declare void @sable_rwlock_free(ptr)
                                declare ptr @sable_rwlock_read(ptr)
                                declare void @sable_rwlock_read_done(ptr, ptr)
                                declare void @sable_rwlock_write(ptr)
                                declare void @sable_rwlock_write_done(ptr)
                                declare ptr @sable_condition_new()
                                declare void @sable_condition_free(ptr)
                                declare void @sable_condition_wait(ptr, ptr, i64)
                                declare void @sable_condition_signal(ptr)
                                declare void @sable_condition_broadcast(ptr)
                                declare void @sable_waitlist_free(ptr)
                                declare void @sable_waitlist_wake(ptr)
                                declare void @sable_waitlist_wake_one(ptr)
                                declare void @sable_waitlist_grant(ptr)
                                declare zeroext i1 @sable_task_park(ptr)
                                declare zeroext i1 @sable_task_park_cancellable(ptr, i64)
                                declare i64 @sable_waitlist_ticket(ptr)
                                declare zeroext i1 @sable_task_io_wait(i64, i1 zeroext)
                                declare zeroext i1 @sable_task_blocking(ptr, ptr)
                                declare i64 @sable_net_status()
                                declare i64 @sable_net_error()
                                declare i64 @sable_net_error_code()
                                declare i64 @sable_net_address(i64)
                                declare ptr @sable_net_path()
                                declare i64 @sable_net_accept(i64)
                                declare zeroext i1 @sable_net_connected(i64)
                                declare ptr @sable_net_read(i64, i64, ptr)
                                declare i64 @sable_net_write(i64, ptr, i64, i64)
                                declare ptr @sable_net_receive(i64, i64, ptr)
                                declare zeroext i1 @sable_net_name(i64, i1 zeroext)
                                declare zeroext i1 @sable_net_set_option(i64, i64, i64)
                                declare i64 @sable_net_option(i64, i64)
                                declare zeroext i1 @sable_net_membership(i64, i1 zeroext, i64, i64, i64, i64)
                                declare void @sable_net_shutdown(i64, i1 zeroext)
                                declare void @sable_net_close(i64)
                                declare i64 @sable_net_listen(i64, i64, i64, i64, i64, ptr, i64, i64)
                                declare i64 @sable_net_connect(i64, i64, i64, i64, i64, ptr)
                                declare i64 @sable_net_datagram(i64, i64, i64, i64, i64, ptr, i64)
                                declare zeroext i1 @sable_net_connect_datagram(i64, i64, i64, i64, i64, i64, ptr)
                                declare i64 @sable_net_send(i64, ptr, i64, i64, i64, i64, i64, i64, i64, ptr)
                                declare i64 @sable_net_resolve(ptr, i64)
                                declare i64 @sable_net_resolve_event(i64)
                                declare i64 @sable_net_resolved(i64)
                                declare void @sable_net_resolved_take(i64, i64)
                                declare void @sable_net_resolve_end(i64)
                                declare i64 @sable_net_interfaces(i1 zeroext)
                                declare ptr @sable_net_interface_name(i64)
                                declare i64 @sable_net_interface(i64, i64)
                                declare i64 @sable_net_interface_index(ptr)
                                declare void @sable_tasks_drain()
                                declare void @sable_task_run_until_main()
                                declare ptr @sable_frame_alloc(i64)
                                declare void @sable_frame_free(ptr)
                                declare void @sable_coverage_hit(i64)
                                declare void @sable_array_make_unique(ptr nocapture, ptr) cold
                                @sable_empty_array_count = internal constant %SableArrayHeader zeroinitializer
                                declare void @sable_access_begin(ptr, i64, i32)
                                declare void @sable_access_check(ptr, i64, i32)
                                @sable_access_count = external thread_local global i64
                                declare void @sable_access_end(ptr)
                                declare ptr @sable_inline_new_stack(ptr, ptr, i64)
                                declare ptr @sable_stack_object(ptr, ptr, i64)
                                declare ptr @sable_stack_array(ptr, ptr, i64)
                                declare ptr @sable_inline_unique(ptr)
                                declare ptr @sable_inline_copy(ptr, ptr)
                                declare ptr @sable_inline_field(ptr, ptr, i32)
                                declare i64 @sable_inline_count(ptr, ptr)
                                declare ptr @sable_inline_first(ptr, ptr)
                                declare ptr @sable_inline_next(ptr, ptr)
                                declare ptr @sable_inline_at(ptr, ptr, i64)
                                declare void @sable_inline_append(ptr, ptr, ptr, ptr, ptr, i64)
                                declare void @sable_inline_append_at(ptr, ptr, ptr, ptr, ptr, ptr, i64)
                                declare void @sable_inline_assign_string(ptr, ptr, ptr, ptr, ptr)
                                declare void @sable_inline_assign(ptr, ptr, ptr, ptr, ptr)
                                declare ptr @sable_inline_ref_get(ptr, i32)
                                declare void @sable_inline_ref_set(ptr, ptr, i32, ptr)
                                declare ptr @sable_inline_to_string(ptr, ptr)
                                declare ptr @sable_inline_bytes(ptr, ptr, ptr)
                                declare ptr @sable_inline_from(ptr)
                                declare ptr @sable_inline_invalid(ptr, ptr)
                                declare float @llvm.fabs.f32(float)
                                declare float @llvm.minnum.f32(float, float)
                                declare float @llvm.maxnum.f32(float, float)
                                declare void @sable_print_uint(i64)
                                declare void @sable_print_f32(float)
                                declare ptr @sable_string_from_uint(i64)
                                declare ptr @sable_string_from_f32(float)
                                declare { i128, i1 } @llvm.sadd.with.overflow.i128(i128, i128)
                                declare { i128, i1 } @llvm.ssub.with.overflow.i128(i128, i128)
                                define linkonce_odr hidden { i128, i1 } @sable_smul_overflow_i128(i128 %a, i128 %b) {
                                entry:
                                  %different = xor i128 %a, %b
                                  %negative = icmp slt i128 %different, 0
                                  %aNegative = icmp slt i128 %a, 0
                                  %aNegated = sub i128 0, %a
                                  %aSize = select i1 %aNegative, i128 %aNegated, i128 %a
                                  %bNegative = icmp slt i128 %b, 0
                                  %bNegated = sub i128 0, %b
                                  %bSize = select i1 %bNegative, i128 %bNegated, i128 %b
                                  %sizes = call { i128, i1 } @llvm.umul.with.overflow.i128(i128 %aSize, i128 %bSize)
                                  %size = extractvalue { i128, i1 } %sizes, 0
                                  %past = extractvalue { i128, i1 } %sizes, 1
                                  %most = select i1 %negative, i128 170141183460469231731687303715884105728, i128 170141183460469231731687303715884105727
                                  %tooBig = icmp ugt i128 %size, %most
                                  %overflowed = or i1 %past, %tooBig
                                  %sizeNegated = sub i128 0, %size
                                  %product = select i1 %negative, i128 %sizeNegated, i128 %size
                                  %pair = insertvalue { i128, i1 } undef, i128 %product, 0
                                  %result = insertvalue { i128, i1 } %pair, i1 %overflowed, 1
                                  ret { i128, i1 } %result
                                }
                                declare { i128, i1 } @llvm.uadd.with.overflow.i128(i128, i128)
                                declare { i128, i1 } @llvm.usub.with.overflow.i128(i128, i128)
                                declare { i128, i1 } @llvm.umul.with.overflow.i128(i128, i128)
                                declare half @llvm.fabs.f16(half)
                                declare half @llvm.minnum.f16(half, half)
                                declare half @llvm.maxnum.f16(half, half)
                                declare void @sable_print_i128(i64, i64)
                                declare void @sable_print_u128(i64, i64)
                                declare void @sable_print_f16(float)
                                declare void @sable_print_bf16(float)
                                declare void @sable_print_char(i32)
                                declare ptr @sable_string_from_i128(i64, i64)
                                declare ptr @sable_string_from_u128(i64, i64)
                                declare ptr @sable_string_from_f16(float)
                                declare ptr @sable_string_from_bf16(float)
                                declare i64 @sable_string_cursor_end(ptr)
                                declare i32 @sable_string_cursor_scalar(ptr, i64)
                                declare i64 @sable_string_cursor_next(ptr, i64)
                                declare i64 @sable_string_cursor_previous(ptr, i64)
                                declare i32 @sable_string_index_scalar(ptr, i64)
                                declare i64 @sable_string_index_after(ptr, i64)
                                declare i64 @sable_string_index_before(ptr, i64)
                                declare ptr @sable_string_utf8_slice(ptr, i64, i64)
                                declare i64 @sable_string_utf8_find(ptr, ptr, i64)
                                declare i64 @sable_string_utf8_find_last(ptr, ptr, i64)
                                declare i64 @sable_utf8_validate(ptr, i64, i64)
                                declare i64 @sable_array_mismatch(ptr, i64, i64, ptr, i64, i64, i64)
                                declare i64 @sable_opaque_mismatch(ptr, i64, i64, ptr, i64, i64, ptr)
                                declare i64 @sable_bytes_find(ptr, i64, i64, i8)
                                declare i64 @sable_bytes_find_any(ptr, i64, i64, ptr, i64, i64)
                                declare i64 @sable_bytes_count(ptr, i64, i64, i8)
                                declare ptr @sable_string_from_utf8_unchecked(ptr, i64, i64)
                                declare ptr @sable_string_from_ascii_words(i64, i64, i64, i64, i64, i64)
                                declare ptr @sable_string_buffer_make(i64)
                                declare ptr @sable_string_buffer_grow(ptr, i64, i64)
                                declare ptr @sable_string_buffer_take(ptr, i64, i64)
                                declare zeroext i1 @sable_string_buffer_valid(ptr, i64)
                                declare void @llvm.dbg.declare(metadata, metadata, metadata)
                                declare void @llvm.dbg.value(metadata, metadata, metadata)

                                """;

    /// <summary>The C <c>main</c>: calls the program's entry point and returns its result, if it has one.</summary>
    /// <param name="keepEntry">
    ///     Never inline the entry point into the shim: with debug information,
    ///     as the shim has none, and what LLVM inlines into a function without
    ///     any loses its own — in an optimised build, often the whole program.
    /// </param>
    /// <param name="wasm">
    ///     For WebAssembly, where wasi-libc's start code calls a
    ///     <c>main(argc, argv)</c> by the name clang gives it when compiling C,
    ///     <c>__main_argc_argv</c>; from IR nothing renames it, and a
    ///     <c>main</c> of that shape is left undefined.
    /// </param>
    static string MainShim(SilFunction entry, bool keepEntry, bool wasm = false) {
        var call = $"@{Global(entry.Name)}()" + (keepEntry ? " noinline" : "");
        var name = wasm ? "__main_argc_argv" : "main";
        // By the type the function has: a throwing one's pair comes back, where
        // the ABI has it so, through a pointer its caller passes. Nothing is
        // thrown here: an error that would leave main ends the program in it.
        var type = ReturnType(entry.Type);
        var body = (entry.Type.Result == SilType.Int, entry.Type.Thrown) switch {
            (true, null) => $"  %r = call {type} {call}\n  %c = trunc i64 %r to i32\n  ret i32 %c\n",
            (true, _) => $"  %p = call {type} {call}\n  %r = extractvalue {type} %p, 1\n  %c = trunc i64 %r to i32\n  ret i32 %c\n",
            (false, null) => $"  call {type} {call}\n  ret i32 0\n",
            _ => $"  %p = call {type} {call}\n  ret i32 0\n",
        };
        return $"define i32 @{name}(i32 %argc, ptr %argv) {{\n  call void @sable_set_arguments(i32 %argc, ptr %argv)\n{body}}}\n";
    }

    /// <summary>
    ///     What a library does where a program's <c>main</c> would: as it is
    ///     loaded — before the program that links it calls into it — it takes
    ///     the process's arguments and gives its globals their initial values;
    ///     as the process exits, it destroys them. It is a constructor of the
    ///     object file (<c>llvm.global_ctors</c>), as the runtime's own
    ///     start-up is, so a static library starts as a dynamic one does.
    ///     Which of one image's constructors and destructors runs first is the
    ///     linker's choice, so the library starts the runtime itself
    ///     (<c>sable_runtime_start</c>, which runs once) before its globals are
    ///     made, and has the runtime destroy them as the process exits
    ///     (<c>sable_runtime_at_exit</c>), before the leak check counts what
    ///     is left.
    /// </summary>
    /// <remarks>
    ///     Apple's dyld and glibc call a constructor with <c>main</c>'s
    ///     <c>argc</c> and <c>argv</c>; Windows' runtime calls it with nothing,
    ///     and the runtime asks Windows for the command line itself; any other
    ///     C library may not pass them, so there the library sees none.
    /// </remarks>
    string LibraryLifetime(SilModule module) {
        const string init = "$globals.init", deinit = "$globals.deinit";
        var takesArguments = _target.Platform == NativePlatform.MacOS
            || _target.Platform == NativePlatform.Linux && !_target.Triple.Contains("musl", StringComparison.Ordinal);
        var text = new StringBuilder();
        text.Append("declare void @sable_runtime_start()\ndeclare void @sable_runtime_at_exit(ptr)\n\n")
            .Append("define internal void @\"sable.library.start\"(i32 %argc, ptr %argv) {\nentry:\n  call void @sable_runtime_start()\n  call void @sable_threads_begin()\n")
            .Append(takesArguments ? "  call void @sable_set_arguments(i32 %argc, ptr %argv)\n" : "  call void @sable_set_arguments(i32 0, ptr null)\n");
        if (module.Find(init) is not null && (_reachable?.Contains(init) ?? true)) {
            text.Append($"  call void @{Global(init)}()\n");
        }

        if (module.Find(deinit) is not null && (_reachable?.Contains(deinit) ?? true)) {
            text.Append($"  call void @sable_runtime_at_exit(ptr @{Global(deinit)})\n");
        }

        text.Append("  ret void\n}\n\n");
        return text.ToString();
    }

    /// <summary>
    ///     A constructor that gives the runtime a function which shares what
    ///     the module's globals hold, as the first thread is started
    ///     (<c>sable_threads_begin</c>): every thread reaches them. A program
    ///     C may call into is called on threads it did not start. Null when
    ///     no global holds a reference.
    /// </summary>
    string? GlobalsRegistration(SilModule module) {
        var held = module.Globals.Where(static g => !g.Type.IsTrivial).ToList();
        if (held.Count == 0) {
            return null;
        }

        var body = new StringBuilder("define internal void @\"sable.globals.share\"() {\nentry:\n");
        foreach (var (name, type) in held) {
            var value = $"%v{held.FindIndex(g => g.Name == name)}";
            body.Append($"  {value} = load {Type(type)}, ptr {GlobalStorage(name)}{StorageAlign(type)}\n");
            Ownership(body, "  ", Witnessing.Share, type, value);
        }

        body.Append("  ret void\n}\n\ndefine internal void @\"sable.globals.register\"() {\nentry:\n")
            .Append("  call void @sable_register_globals(ptr @\"sable.globals.share\")\n");
        if (module.Exports.Count > 0 && !module.IsLibrary) {
            body.Append("  call void @sable_threads_begin()\n");
        }

        _helpers.Append(body.Append("  ret void\n}\n\n"));
        return "@\"sable.globals.register\"";
    }

    /// <summary>
    ///     The LLVM symbol for a SIL function: its name behind a <c>sable.</c>
    ///     prefix, so that no Sable name can collide with a C one — <c>main</c>
    ///     above all, or the runtime's own. Not Swift's <c>$s</c>: debuggers
    ///     read that as a Swift symbol and try to demangle it.
    /// </summary>
    static string Global(string name) => $"\"{SymbolPrefix}{name}\"";

    /// <summary>An <c>@export</c> variable: in the module's exports, and no function.</summary>
    bool IsExportedGlobal(string name) => _module!.Exports.ContainsKey(name) && _module.Find(name) is null;

    /// <summary>A global's storage, as an LLVM value: by its C name when C sees it.</summary>
    string GlobalStorage(string name) => IsExportedGlobal(name) ? $"@\"{_module!.Exports[name]}\"" : "@" + Global("global." + name);

    public const string SymbolPrefix = "sable.";

    static string Type(SilType type) =>
        type switch {
            _ when type == SilType.Int => "i64",
            _ when type == SilType.Bool => "i1",
            _ when type == SilType.F64 => "double",
            _ when type == SilType.F32 => "float",
            _ when type == SilType.F16 => "half",
            _ when type == SilType.BF16 => "i16",
            _ when SilType.IntegerInfo(type) is var (bits, _) => $"i{bits}",
            _ when type == SilType.Char => "i32",
            _ when type == SilType.String => "ptr",
            _ when type == SilType.Unit => "void",
            AddressType or FunctionType or ClassType or ArrayType => "ptr",
            StructType { IsInline: true } or InlinePointerType => "ptr",
            _ when type == SilType.Object || type == SilType.Region => "ptr",
            _ when SilType.IsIndirect(type) => "ptr",
            _ when type == SilType.Metadata || type == SilType.WitnessTable => "ptr",
            ThickFunctionType => "{ ptr, ptr }",
            VectorType vector => $"<{vector.Count} x {Type(vector.Element)}>",
            WeakStorageType or UnownedStorageType => "ptr",
            StructType structType => LayoutOf(structType).Llvm,
            TupleType tuple => LayoutOf(tuple).Llvm,
            SliceType slice => Type(slice.Parts),
            EnumType enumType when IsNullable(enumType) => "ptr",
            EnumType enumType when Niche(enumType) is { } niche => NicheType(niche),
            EnumType enumType => Area(enumType) is { Count: > 0 } area
                ? $"{{ {TagType(enumType)}, [{area.Count} x i{area.Unit * 8}] }}"
                : $"{{ {TagType(enumType)} }}",
            _ => throw new NotSupportedException($"no LLVM type for {type}"),
        };

    /// <summary>
    ///     A value's LLVM type as a part of an aggregate: a unit part is the
    ///     empty struct, which takes no storage, where LLVM allows no
    ///     <c>void</c>.
    /// </summary>
    static string PartType(SilType type) => type == SilType.Unit ? "{}" : Type(type);

    /// <summary>
    ///     What a struct is laid out as: its fields, after a hidden byte for one
    ///     with a deinit — set when a value is made, so that storage left empty
    ///     by a move, all zeros, is not mistaken for a value to deinit.
    /// </summary>
    static IEnumerable<SilType> StructParts(StructType type) => TypeLayout.StructParts(type);

    /// <summary>Where a struct's field is in its LLVM type: one further along when there is a live byte.</summary>
    static int FieldSlot(StructType type, int field) => LayoutOf(type).Slots[PartIndex(type, field)];

    /// <summary>Where a struct's field is among its parts: one further along when there is a live byte.</summary>
    static int PartIndex(StructType type, int field) => TypeLayout.PartIndex(type, field);

    /// <summary>
    ///     What a function returns at the LLVM level. A throwing one returns
    ///     <c>{ i1 thrown, result, error }</c> — the discriminated return of
    ///     docs/04 §2.2 — and the caller branches on the flag.
    /// </summary>
    static string ReturnType(FunctionType type) =>
        type.Thrown is not { } thrown
            ? Type(type.Result)
            : type.Result == SilType.Unit
                ? $"{{ i1, {Type(thrown)} }}"
                : $"{{ i1, {Type(type.Result)}, {Type(thrown)} }}";

    // ---- Enum layout: TypeLayout's (a nullable reference, a niche, or a tag and a payload area) ----

    static bool IsNullable(EnumType type) => TypeLayout.IsNullable(type);

    static int SomeCase(EnumType type) => TypeLayout.SomeCase(type);

    static TypeLayout.NicheLayout? Niche(EnumType type) => TypeLayout.Niche(type);

    /// <summary>The integer type a niche-held enum is.</summary>
    static string NicheType(TypeLayout.NicheLayout niche) => $"i{niche.Bytes * 8}";

    /// <summary>A tagged enum's tag: a byte, or four for more than 256 cases.</summary>
    static string TagType(EnumType type) => type.Cases.Count <= 256 ? "i8" : "i32";

    static int TagBytes(EnumType type) => TypeLayout.TagBytes(type);

    /// <summary>A tagged enum's payload area: so many units of the most aligned payload's alignment, room for the largest.</summary>
    static (int Count, int Unit) Area(EnumType type) => TypeLayout.Area(type);

    /// <summary>Where a tagged enum's payload area starts: after the tag, aligned for the area.</summary>
    static int AreaOffset(EnumType type) => TypeLayout.AreaOffset(type);

    /// <summary>Bytes a value of the type occupies in memory, as LLVM lays it out.</summary>
    static int Size(SilType type) => TypeLayout.Size(type);

    static int Align(SilType type) => TypeLayout.Align(type);

    /// <summary>
    ///     A string literal: an immortal string object in read-only memory —
    ///     header, length, scalar count, bytes. The same text shares one.
    /// </summary>
    string StringConstant(string value) {
        if (_strings.TryGetValue(value, out var name)) {
            return name;
        }

        name = $"@.str.{_strings.Count}";
        var bytes = Encoding.UTF8.GetBytes(value);
        var scalars = value.EnumerateRunes().Count();
        _globals.Append(name).Append(" = private unnamed_addr constant { ptr, i32, i32, i64, i64, [").Append(bytes.Length)
            .Append(" x i8] } { ptr @sable_string_metadata, i32 1, i32 1, i64 ").Append(bytes.Length)
            .Append(", i64 ").Append(scalars).Append(", [").Append(bytes.Length).Append(" x i8] c\"").Append(Escape(bytes))
            .Append("\" }, align 8\n");
        _strings[value] = name;
        return name;
    }

    /// <summary>
    ///     An array literal of constants (#265): an immortal array in
    ///     read-only memory, as a string literal is — header, count, capacity,
    ///     and its elements beside it. Its strong count is 2 and never
    ///     changes, retains and releases passing an immortal object by, so
    ///     copy on write always finds it shared: a change copies it first.
    /// </summary>
    string ArrayConstant(ArrayType type, IReadOnlyList<string> elements) {
        var element = Type(type.Element);
        var body = string.Join(", ", elements.Select(e => $"{element} {e}"));
        var key = $"{type}|{body}";
        if (_constantArrays.TryGetValue(key, out var name)) {
            return name;
        }

        // Its metadata first, which may write globals of its own.
        var metadata = ArrayMetadata(type);
        name = $"@.array.{_constantArrays.Count}";
        _globals.Append(name).Append(".elements = private unnamed_addr constant [").Append(elements.Count).Append(" x ").Append(element).Append("] [")
            .Append(body).Append("], align ").Append(Align(type.Element)).Append('\n');
        _globals.Append(name).Append(" = private unnamed_addr constant %SableArrayHeader { %SableObjectHeader { ptr ").Append(metadata)
            .Append(", i32 2, i32 1 }, i64 ").Append(elements.Count).Append(", i64 ").Append(elements.Count).Append(", ptr ").Append(name)
            .Append(".elements }, align 8\n");
        _constantArrays[key] = name;
        return name;
    }

    // =======================================================================
    // Value witnesses and metadata
    // =======================================================================

    /// <summary>The bytes of the header every counted object starts with (docs/06 §6.2).</summary>
    const int ObjectHeaderSize = 16;

    /// <summary>A class's fields and where each is in an object, past the header — as <see cref="ObjectType" /> lays them out.</summary>
    static IEnumerable<(string Name, SilType Type, int Offset)> ObjectFields(ClassType type) {
        var layout = ObjectLayout(type);
        for (var i = 0; i < type.Fields.Count; i++) {
            yield return (type.Fields[i].Name, type.Fields[i].Type, layout.Offsets[i + 3]);
        }
    }

    /// <summary>Where an object's field is in its LLVM type: past the header's three parts, and any padding.</summary>
    static int ObjectSlot(ClassType type, int field) => ObjectLayout(type).Slots[field + 3];

    /// <summary>What a box holds — the class a local captured by reference lives in — or null for any other type.</summary>
    static SilType? BoxedType(SilType type) =>
        type is ClassType { Fields: [("value", var boxed)] } box && box.Name.StartsWith("Box$of$", StringComparison.Ordinal) ? boxed : null;

    /// <summary>The LLVM struct an object of a class is: the header, then the fields.</summary>
    static string ObjectType(ClassType type) => ObjectLayout(type).Llvm;

    /// <summary>A name for a type inside a symbol: quoted, so anything but quotes and backslashes may appear.</summary>
    static string Mangled(SilType type) => type.ToString().Replace("\"", "", StringComparison.Ordinal).Replace("\\", "", StringComparison.Ordinal);

    /// <summary>
    ///     Writes the code that copies (<c>retain</c>) or destroys
    ///     (<c>release</c>) a non-trivial value: a call to the runtime for a
    ///     reference, to the type's witness function for an aggregate.
    /// </summary>
    void Ownership(StringBuilder output, string indent, bool copy, SilType type, string value, string dbg = "") =>
        Ownership(output, indent, copy ? Witnessing.Copy : Witnessing.Destroy, type, value, dbg);

    /// <summary>What a value witness does to every reference a value holds.</summary>
    enum Witnessing {
        Copy,
        Destroy,

        /// <summary>
        ///     Hands each strong reference to <c>sable_heap_visit</c>: how the
        ///     runtime walks the heap, to say what a leak's objects keep alive.
        /// </summary>
        Visit,

        /// <summary>Hands each strong reference to <c>sable_make_immortal</c>: an '@immortal' global's objects.</summary>
        Immortal,

        /// <summary>
        ///     Hands each strong reference to <c>sable_share</c>: a value
        ///     crossing from one task to another, whose objects are counted
        ///     atomically from then on.
        /// </summary>
        Share,
    }

    void Ownership(StringBuilder output, string indent, Witnessing what, SilType type, string value, string dbg = "") {
        // Nothing to count in a value known to be null: the empty array, a none.
        if (type.IsTrivial || value == "null") {
            return;
        }

        var copy = what == Witnessing.Copy;
        if (type is ScalarType or ClassType or ArrayType or StructType { IsInline: true } || type is EnumType nullable && IsNullable(nullable)) {
            var call = what switch {
                Witnessing.Copy => "sable.retain",
                Witnessing.Destroy => "sable.release",
                Witnessing.Immortal => "sable_make_immortal",
                Witnessing.Share => "sable.share",
                _ => "sable_heap_visit",
            };
            output.Append(indent).Append($"call void @{call}(ptr {value}){dbg}\n");
            return;
        }

        // A weak or unowned reference keeps nothing alive.
        if (type is WeakStorageType or UnownedStorageType) {
            // But another thread may read it, and retain the object: an unowned
            // one's object is shared with what holds the reference (a weak
            // one's is as the reference is made, sable_weak_entry).
            if (what is Witnessing.Visit or Witnessing.Immortal or Witnessing.Share) {
                if (type is UnownedStorageType) {
                    output.Append(indent).Append($"call void @{(what == Witnessing.Visit ? "sable_heap_visit_unowned" : "sable_share")}(ptr {value}){dbg}\n");
                }

                return;
            }

            var kind = type is WeakStorageType ? "weak" : "unowned";
            output.Append(indent).Append($"call void @sable_{kind}_{(copy ? "retain" : "release")}(ptr {value}){dbg}\n");
            return;
        }

        output.Append(indent).Append($"call void {Witness(type, what)}({Type(type)} {value}){dbg}\n");
    }

    /// <summary>The function that copies, destroys or visits a value of an aggregate type, written the first time it is needed.</summary>
    string Witness(SilType type, Witnessing what) {
        var name = $"@\"sable.{what.ToString().ToLowerInvariant()}.{Mangled(type)}\"";
        if (!_emitted.Add(name)) {
            return name;
        }

        var body = new StringBuilder();
        var temporaries = 0;
        string Fresh() => $"%w{temporaries++}";
        var llvmType = Type(type);
        body.Append($"define internal void {name}({llvmType} %v) {{\nentry:\n");

        switch (type) {
            case StructType structType:
                // A move-only struct's deinit runs first, seeing it whole —
                // if it is a value at all, and not storage a move emptied.
                if (what == Witnessing.Destroy && structType.Deinit is { } deinit) {
                    body.Append("  %live = extractvalue ").Append(llvmType).Append(" %v, 0\n")
                        .Append("  br i1 %live, label %value, label %empty\n")
                        .Append("empty:\n  ret void\n")
                        .Append("value:\n")
                        .Append($"  call void @{Global(deinit)}({llvmType} %v)\n");
                }

                Parts([..StructParts(structType)], LayoutOf(structType).Slots);
                body.Append("  ret void\n");
                break;

            case TupleType tuple:
                Parts([..tuple.Elements], LayoutOf(tuple).Slots);
                body.Append("  ret void\n");
                break;

            // A slice: the buffer it shares.
            case SliceType slice:
                Parts([..slice.Parts.Elements], LayoutOf(slice.Parts).Slots);
                body.Append("  ret void\n");
                break;

            case EnumType enumType: {
                // By case: each payload that holds references is unpacked and handled.
                var slot = Fresh();
                var tag = Fresh();
                var tagType = TagType(enumType);
                body.Append($"  {slot} = alloca {llvmType}\n  store {llvmType} %v, ptr {slot}\n  {tag} = extractvalue {llvmType} %v, 0\n");
                var arms = new StringBuilder();
                var cases = new StringBuilder();
                for (var i = 0; i < enumType.Cases.Count; i++) {
                    if (enumType.Cases[i].Payload is not { IsTrivial: false } payload) {
                        continue;
                    }

                    arms.Append($" {tagType} {i}, label %case{i}");
                    var area = Fresh();
                    var loaded = Fresh();
                    cases.Append($"case{i}:\n  {area} = getelementptr inbounds {llvmType}, ptr {slot}, i32 0, i32 1\n")
                        .Append($"  {loaded} = load {Type(payload)}, ptr {area}\n");
                    Ownership(cases, "  ", what, payload, loaded);
                    cases.Append("  ret void\n");
                }

                body.Append($"  switch {tagType} {tag}, label %done [{arms} ]\n").Append(cases).Append("done:\n  ret void\n");
                break;
            }

            // A closure: its context.
            case ThickFunctionType: {
                var context = Fresh();
                body.Append($"  {context} = extractvalue {{ ptr, ptr }} %v, 1\n");
                Ownership(body, "  ", what, SilType.Object, context);
                body.Append("  ret void\n");
                break;
            }

            default:
                throw new NotSupportedException($"no value witness for {type}");
        }

        body.Append("}\n\n");
        _helpers.Append(body);
        return name;

        void Parts(List<SilType> parts, int[] slots) {
            for (var i = 0; i < parts.Count; i++) {
                if (parts[i].IsTrivial) {
                    continue;
                }

                var part = Fresh();
                body.Append($"  {part} = extractvalue {llvmType} %v, {slots[i]}\n");
                Ownership(body, "  ", what, parts[i], part);
            }
        }
    }

    /// <summary>
    ///     A class's metadata: the function that destroys an object — its
    ///     <c>deinit</c>, then its fields, last first (docs/02 §2.2).
    /// </summary>
    string ClassMetadata(ClassType type) {
        var name = $"@\"sable.meta.{type.Name}\"";
        if (!_emitted.Add(name)) {
            return name;
        }

        // A generic class's, which metadata made at run time for the same
        // type arguments must be (GenericClassRegistration).
        if (_module?.UnspecialisedGenerics == true && type.IsGenericInstance
            && type.Superclass is null && type.VTable.Count == 0) {
            _genericClasses.Add(type);
        }

        var destroy = $"@\"sable.deinit.{type.Name}\"";
        var objectType = ObjectType(type);
        var body = new StringBuilder($"define internal void {destroy}(ptr %o) {{\nentry:\n");

        // One whose initialiser threw was never made (SABLE_UNMADE, 1024, in
        // its header's flag word, at 12): no deinit sees it.
        var deinits = false;
        for (var level = type; level is not null; level = level.Superclass) {
            if (level.Deinit is not { } deinit) {
                continue;
            }

            if (!deinits) {
                deinits = true;
                body.Append("  %flagsField = getelementptr inbounds { ptr, i32, i32 }, ptr %o, i32 0, i32 2\n")
                    .Append("  %flags = load atomic i32, ptr %flagsField unordered, align 4\n")
                    .Append("  %unmadeBit = and i32 %flags, 1024\n")
                    .Append("  %made = icmp eq i32 %unmadeBit, 0\n")
                    .Append("  br i1 %made, label %deinit, label %fields\n")
                    .Append("deinit:\n");
            }

            body.Append($"  call void @{Global(deinit)}(ptr %o)\n");
        }

        if (deinits) {
            body.Append("  br label %fields\nfields:\n");
        }

        for (var i = type.Fields.Count - 1; i >= 0; i--) {
            if (type.Fields[i].Type.IsTrivial) {
                continue;
            }

            body.Append($"  %a{i} = getelementptr inbounds {objectType}, ptr %o, i32 0, i32 {ObjectSlot(type, i)}\n")
                .Append($"  %f{i} = load {Type(type.Fields[i].Type)}, ptr %a{i}\n");
            Ownership(body, "  ", false, type.Fields[i].Type, $"%f{i}");
        }

        body.Append("  ret void\n}\n\n");

        // What the object keeps alive: each field's strong references, for the runtime's walk of the heap.
        var visit = $"@\"sable.visit.{type.Name}\"";
        body.Append($"define internal void {visit}(ptr %o) {{\nentry:\n");
        for (var i = 0; i < type.Fields.Count; i++) {
            if (type.Fields[i].Type.IsTrivial) {
                continue;
            }

            body.Append($"  %a{i} = getelementptr inbounds {objectType}, ptr %o, i32 0, i32 {ObjectSlot(type, i)}\n")
                .Append($"  %f{i} = load {Type(type.Fields[i].Type)}, ptr %a{i}\n");
            Ownership(body, "  ", Witnessing.Visit, type.Fields[i].Type, $"%f{i}");
        }

        body.Append("  ret void\n}\n\n");
        _helpers.Append(body);

        // The runtime reads the first nine fields — the last four an
        // object's size and alignment, its visitor and its type's metadata
        // — and the vtable follows them.
        // A class has no elements to copy: that slot holds its superclass's
        // metadata, which 'is' and 'as?' walk.
        var typeName = TrapMessage(type.Name);
        var superclass = type.Superclass is { } parent ? ClassMetadata(parent) : "null";
        var info = TypeMetadataGlobal(type);
        // A method no reachable code can call was not built: its slot is empty.
        var vtable = string.Join(", ", type.VTable.Select(m => _reachable?.Contains(m.Function) == false ? "ptr null" : $"ptr @{Global(m.Function)}"));
        _globals.Append($"{name} = {SharedLinkage} constant {MetadataType(type)} {{ ptr {destroy}, ptr {typeName}, i64 0, ptr {superclass}, ptr null, ")
            .Append($"i64 {SizeOf(objectType)}, i64 {(ObjectLayout(type).Explicit ? ObjectLayout(type).Align.ToString(System.Globalization.CultureInfo.InvariantCulture) : AlignmentOf(objectType))}, ptr {visit}, ptr {info}, [{type.VTable.Count} x ptr] [{vtable}] }}{Comdat(name, SharedLinkage)}\n");
        return name;
    }

    /// <summary>
    ///     How metadata is linked: kept to the module, or — when the module is
    ///     one unit of several — one copy for the program, since a class's
    ///     metadata is its identity to <c>is</c> and <c>as?</c>.
    /// </summary>
    string SharedLinkage => _module?.Unit is null ? "internal" : "linkonce_odr";

    /// <summary>
    ///     How a function is linked. A whole program's are its own: nothing
    ///     outside calls them by name — C reaches the exported ones through
    ///     wrappers of their own — so they are internal, which lets LLVM
    ///     inline one with a single caller and drop one it no longer calls.
    ///     From a unit of several, one only it defines is the program's; one
    ///     another unit may define as well — a generic instance, a thunk — is
    ///     kept once by the linker.
    /// </summary>
    string Linkage(string function) =>
        _module?.Unit is not { } unit ? "internal "
        : unit.Owned.Contains(function) ? ""
        : "linkonce_odr ";

    /// <summary>
    ///     A <c>declare</c> for every function the unit refers to and does not
    ///     define: the program's other functions, with the signatures their
    ///     own units give them.
    /// </summary>
    static string ExternalDeclarations(SilUnitInfo unit, StringBuilder output) {
        var text = output.ToString();
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match match in DefinedSymbol().Matches(text)) {
            defined.Add(match.Groups[1].Value);
        }

        var declarations = new StringBuilder("\n");
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match match in ReferencedSymbol().Matches(text)) {
            var symbol = match.Groups[1].Value;
            if (defined.Contains(symbol) || !declared.Add(symbol)) {
                continue;
            }

            var name = symbol[SymbolPrefix.Length..];
            declarations.Append(unit.External.TryGetValue(name, out var type)
                ? $"declare {ReturnType(type)} @{Global(name)}({string.Join(", ", type.Parameters.Select(static p => p is AddressType ? "ptr noalias" : Type(p)))})\n"
                : $"declare void @{Global(name)}()\n");
        }

        return declarations.ToString();
    }

    [System.Text.RegularExpressions.GeneratedRegex("^(?:define [^@\\n]*@|@)\"(sable\\.[^\"]+)\"(?: = |\\()", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex DefinedSymbol();

    [System.Text.RegularExpressions.GeneratedRegex("@\"(sable\\.[^\"]+)\"")]
    private static partial System.Text.RegularExpressions.Regex ReferencedSymbol();

    /// <summary>Where a class's vtable is in its metadata: after the runtime's record.</summary>
    const int VTableField = 9;

    /// <summary>A class's metadata: the runtime's record, then its vtable.</summary>
    static string MetadataType(ClassType type) => $"{{ ptr, ptr, i64, ptr, ptr, i64, i64, ptr, ptr, [{type.VTable.Count} x ptr] }}";

    /// <summary>The bytes a value of the LLVM type takes, as a constant.</summary>
    static string SizeOf(string llvmType) => $"ptrtoint (ptr getelementptr ({llvmType}, ptr null, i32 1) to i64)";

    /// <summary>The alignment of the LLVM type, as a constant: where it lands after a byte.</summary>
    static string AlignmentOf(string llvmType) => $"ptrtoint (ptr getelementptr ({{ i8, {llvmType} }}, ptr null, i32 0, i32 1) to i64)";

    /// <summary>The function that destroys a value of the type in memory, given its address; or null for a trivial one.</summary>
    string ElementDestroyer(SilType type) {
        if (type.IsTrivial) {
            return "null";
        }

        var name = $"@\"sable.elementdestroy.{Mangled(type)}\"";
        if (_emitted.Add(name + "#region")) {
            ArrayMetadata(new ArrayType(type));
        }

        return name;
    }

    /// <summary>An array's metadata, per element type: the element's size, and how one is copied and destroyed.</summary>
    string ArrayMetadata(ArrayType type) {
        var name = $"@\"sable.meta.{Mangled(type)}\"";
        if (!_emitted.Add(name)) {
            return name;
        }

        var element = type.Element;
        var llvmElement = Type(element);
        string copy = "null", destroy = "null", visit = "null";
        if (!element.IsTrivial) {
            copy = $"@\"sable.elementcopy.{Mangled(element)}\"";
            destroy = $"@\"sable.elementdestroy.{Mangled(element)}\"";
            visit = $"@\"sable.elementvisit.{Mangled(element)}\"";
            var body = new StringBuilder();
            body.Append($"define internal void {copy}(ptr %d, ptr %s) {{\nentry:\n  %v = load {llvmElement}, ptr %s\n  store {llvmElement} %v, ptr %d\n");
            Ownership(body, "  ", true, element, "%v");
            body.Append("  ret void\n}\n\n");
            body.Append($"define internal void {destroy}(ptr %e) {{\nentry:\n  %v = load {llvmElement}, ptr %e\n");
            Ownership(body, "  ", false, element, "%v");
            body.Append("  ret void\n}\n\n");
            body.Append($"define internal void {visit}(ptr %e) {{\nentry:\n  %v = load {llvmElement}, ptr %e\n");
            Ownership(body, "  ", Witnessing.Visit, element, "%v");
            body.Append("  ret void\n}\n\n");
            _helpers.Append(body);
        }

        var size = SizeOf(llvmElement);
        var typeName = TrapMessage(type.ToString());
        _debug?.Keep(type, type.ToString());
        // An array object is the runtime's: its header, count, capacity and buffer.
        _globals.Append($"{name} = {SharedLinkage} constant %SableMetadata {{ ptr @sable_array_destroy, ptr {typeName}, i64 {size}, ptr {copy}, ptr {destroy}, ")
            .Append($"i64 {SizeOf("%SableArrayHeader")}, i64 {AlignmentOf("%SableArrayHeader")}, ptr {visit}, ptr null }}{Comdat(name, SharedLinkage)}\n");
        return name;
    }

    /// <summary>
    ///     An inline struct's layout, as the runtime walks it: the prefix,
    ///     and a slot per field in the order stored — docs/02 §2.5.
    /// </summary>
    string InlineLayoutGlobal(StructType type) {
        var name = $"@\"sable.inline.{Mangled(type)}\"";
        if (!_emitted.Add(name)) {
            return name;
        }

        // Everything it refers to is written first, so that no other global lands inside this one.
        var layout = InlineLayout.Of(type);
        var slots = string.Join(", ", layout.Slots.Select(SlotConstant));
        var count = layout.Slots.Length;
        var typeName = TrapMessage(type.Name);
        _globals.Append($"{name} = internal constant {{ ptr, i32, i32, i32, i32, i32, [{count} x %SableInlineSlot] }} ")
            .Append($"{{ ptr {typeName}, i32 {layout.Prefix}, i32 {layout.FixedSize ?? -1}, i32 {layout.EmptySize}, i32 {(layout.HasReferences ? 1 : 0)}, ")
            .Append($"i32 {count}, [{count} x %SableInlineSlot] [{slots}] }}\n");
        return name;
    }

    string SlotConstant(InlineLayout.Slot slot) =>
        $"%SableInlineSlot {{ i32 {slot.Offset}, i32 {slot.Kind}, i32 {slot.Width}, i32 {(slot.Optional ? 1 : 0)}, i32 {slot.Capacity}, "
        + $"i32 {slot.ElementKind}, i32 {slot.ElementWidth}, ptr {(slot.Layout is { } nested ? InlineLayoutGlobal(nested) : "null")} }}";

    /// <summary>An inline array's description on its own: how its count and its elements are read.</summary>
    string InlineArrayGlobal(InlineArrayType type) {
        var name = $"@\"sable.inlinearray.{Mangled(type)}\"";
        if (_emitted.Add(name)) {
            var constant = SlotConstant(InlineLayout.DescribeArray(type));
            _globals.Append($"{name} = internal constant {constant}\n");
        }

        return name;
    }

    // ---- Inline layout walkers, specialised per layout ---------------------

    /// <summary>A LEB128 count: its value, and where what follows it starts.</summary>
    string VarintFunction() {
        const string name = "@\"sable.inline.varint\"";
        if (_emitted.Add(name)) {
            _helpers.Append($$"""
                define internal { i64, ptr } {{name}}(ptr %p) {
                entry:
                  br label %loop
                loop:
                  %i = phi i64 [ 0, %entry ], [ %next, %loop ]
                  %v = phi i64 [ 0, %entry ], [ %value, %loop ]
                  %at = getelementptr inbounds i8, ptr %p, i64 %i
                  %b = load i8, ptr %at, align 1
                  %low = and i8 %b, 127
                  %wide = zext i8 %low to i64
                  %shift = mul i64 %i, 7
                  %part = shl i64 %wide, %shift
                  %value = or i64 %v, %part
                  %next = add i64 %i, 1
                  %more = icmp uge i8 %b, -128
                  br i1 %more, label %loop, label %done
                done:
                  %end = getelementptr inbounds i8, ptr %p, i64 %next
                  %r0 = insertvalue { i64, ptr } poison, i64 %value, 0
                  %r1 = insertvalue { i64, ptr } %r0, ptr %end, 1
                  ret { i64, ptr } %r1
                }


                """);
        }

        return name;
    }

    /// <summary>Where a value of the layout ends.</summary>
    internal string SkipFunction(StructType type) {
        var name = $"@\"sable.inline.skip.{Mangled(type)}\"";
        if (!_emitted.Add(name)) {
            return name;
        }

        var layout = InlineLayout.Of(type);
        var body = new StringBuilder($"define internal ptr {name}(ptr %p) {{\nentry:\n");
        if (layout.FixedSize is { } size) {
            body.Append($"  %end = getelementptr inbounds i8, ptr %p, i64 {size}\n  ret ptr %end\n}}\n\n");
        } else {
            body.Append($"  %q0 = getelementptr inbounds i8, ptr %p, i64 {layout.Prefix}\n");
            var q = "%q0";
            var n = 1;
            foreach (var slot in layout.Slots.Where(static s => !s.IsFixed)) {
                var next = $"%q{n++}";
                body.Append($"  {next} = call ptr {SlotSkip(slot)}(ptr {q})\n");
                q = next;
            }

            body.Append($"  ret ptr {q}\n}}\n\n");
        }

        _helpers.Append(body);
        return name;
    }

    /// <summary>The function that skips a variable-size field: an inline array, or a nested struct.</summary>
    string SlotSkip(InlineLayout.Slot slot) =>
        slot.Kind == InlineLayout.StructKind ? SkipFunction(slot.Layout!) : ArraySkip((InlineArrayType)slot.Type);

    /// <summary>An inline array's count and the start of its elements.</summary>
    string ArrayHead(InlineArrayType array) {
        var name = $"@\"sable.inline.head.{Mangled(array)}\"";
        if (!_emitted.Add(name)) {
            return name;
        }

        var slot = InlineLayout.DescribeArray(array);
        var body = new StringBuilder($"define internal {{ i64, ptr }} {name}(ptr %a) {{\nentry:\n");
        if (slot.Kind == InlineLayout.BoundedKind) {
            body.Append($"  %raw = load i{slot.Width * 8}, ptr %a, align 1\n")
                .Append($"  %count = zext i{slot.Width * 8} %raw to i64\n")
                .Append($"  %elements = getelementptr inbounds i8, ptr %a, i64 {slot.Width}\n")
                .Append("  %r0 = insertvalue { i64, ptr } poison, i64 %count, 0\n")
                .Append("  %r1 = insertvalue { i64, ptr } %r0, ptr %elements, 1\n")
                .Append("  ret { i64, ptr } %r1\n}\n\n");
        } else {
            body.Append($"  %r = call {{ i64, ptr }} {VarintFunction()}(ptr %a)\n  ret {{ i64, ptr }} %r\n}}\n\n");
        }

        _helpers.Append(body);
        return name;
    }

    /// <summary>The size of an element that does not vary, or null.</summary>
    static int? ElementSize(InlineArrayType array) =>
        array.Element is StructType { IsInline: true } element ? InlineLayout.Of(element).FixedSize : InlineLayout.Scalar(array.Element)!.Value.Width;

    /// <summary>Where an inline array ends: past its count and every element.</summary>
    string ArraySkip(InlineArrayType array) {
        var name = $"@\"sable.inline.skiparray.{Mangled(array)}\"";
        if (!_emitted.Add(name)) {
            return name;
        }

        var head = ArrayHead(array);
        var body = new StringBuilder($"define internal ptr {name}(ptr %a) {{\nentry:\n")
            .Append($"  %h = call {{ i64, ptr }} {head}(ptr %a)\n")
            .Append("  %count = extractvalue { i64, ptr } %h, 0\n")
            .Append("  %elements = extractvalue { i64, ptr } %h, 1\n");
        if (ElementSize(array) is { } size) {
            body.Append($"  %bytes = mul i64 %count, {size}\n")
                .Append("  %end = getelementptr inbounds i8, ptr %elements, i64 %bytes\n  ret ptr %end\n}\n\n");
        } else {
            var skip = SkipFunction((StructType)array.Element);
            body.Append("  br label %loop\nloop:\n")
                .Append("  %i = phi i64 [ 0, %entry ], [ %i1, %step ]\n")
                .Append("  %at = phi ptr [ %elements, %entry ], [ %after, %step ]\n")
                .Append("  %more = icmp ult i64 %i, %count\n")
                .Append("  br i1 %more, label %step, label %done\nstep:\n")
                .Append($"  %after = call ptr {skip}(ptr %at)\n")
                .Append("  %i1 = add i64 %i, 1\n  br label %loop\ndone:\n  ret ptr %at\n}\n\n");
        }

        _helpers.Append(body);
        return name;
    }

    /// <summary>Where a variable-size field is: past the prefix and the variable fields before it.</summary>
    internal string FieldFunction(StructType type, int slotIndex) {
        var name = $"@\"sable.inline.field.{Mangled(type)}.{slotIndex}\"";
        if (!_emitted.Add(name)) {
            return name;
        }

        var layout = InlineLayout.Of(type);
        var body = new StringBuilder($"define internal ptr {name}(ptr %p) {{\nentry:\n")
            .Append($"  %q0 = getelementptr inbounds i8, ptr %p, i64 {layout.Prefix}\n");
        var q = "%q0";
        var n = 1;
        for (var i = 0; i < slotIndex; i++) {
            if (!layout.Slots[i].IsFixed) {
                var next = $"%q{n++}";
                body.Append($"  {next} = call ptr {SlotSkip(layout.Slots[i])}(ptr {q})\n");
                q = next;
            }
        }

        body.Append($"  ret ptr {q}\n}}\n\n");
        _helpers.Append(body);
        return name;
    }

    internal string InlineHead(InlineArrayType array) => ArrayHead(array);

    internal string ArraySkipFunction(InlineArrayType array) => ArraySkip(array);

    internal string InlineNext(InlineArrayType array, string element, Func<string> fresh, Action<string> line) {
        if (ElementSize(array) is { } size) {
            var next = fresh();
            line($"{next} = getelementptr inbounds i8, ptr {element}, i64 {size}");
            return next;
        }

        var after = fresh();
        line($"{after} = call ptr {SkipFunction((StructType)array.Element)}(ptr {element})");
        return after;
    }

    /// <summary>A NUL-terminated message for <c>sable_trap</c>.</summary>
    string TrapMessage(string message) {
        if (_trapMessages.TryGetValue(message, out var name)) {
            return name;
        }

        name = $"@.trap.{_trapMessages.Count}";
        var bytes = Encoding.UTF8.GetBytes(message + "\0");
        _globals.Append(name).Append(" = private unnamed_addr constant [").Append(bytes.Length)
            .Append(" x i8] c\"").Append(Escape(bytes)).Append("\", align 1\n");
        _trapMessages[message] = name;
        return name;
    }

    static string Escape(byte[] bytes) {
        var builder = new StringBuilder();
        foreach (var b in bytes) {
            if (b is >= 0x20 and < 0x7F && b != (byte)'"' && b != (byte)'\\') {
                builder.Append((char)b);
            } else {
                builder.Append('\\').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    // =======================================================================

    sealed partial class FunctionEmitter(LlvmEmitter module, SilFunction function, string suffix = "") {
        /// <summary>What each SIL value is called, or its constant text.</summary>
        readonly Dictionary<SilValue, string> _names = [];

        readonly Dictionary<SilBlock, string> _labels = [];

        /// <summary>The instructions of each SIL block, already written, keyed by the block.</summary>
        readonly Dictionary<SilBlock, StringBuilder> _bodies = [];

        /// <summary>For each block argument, the values that reach it and the label each comes from.</summary>
        readonly Dictionary<SilArgument, List<(string Value, string From)>> _incoming = [];

        /// <summary>Blocks created while writing: edge blocks and trap blocks.</summary>
        readonly StringBuilder _extra = new();

        /// <summary>Scratch slots, allocated once in the entry block so that a loop does not grow the stack.</summary>
        readonly StringBuilder _entryAllocas = new();

        /// <summary>Each type parameter's metadata, by name (<c>$metadata.T</c>), in unspecialised code.</summary>
        readonly Dictionary<string, string> _opaqueMetadata = new(StringComparer.Ordinal);

        readonly Dictionary<string, string> _opaqueSizes = new(StringComparer.Ordinal);

        /// <summary>
        ///     The metadata of a type unspecialised code holds indirectly: a
        ///     type parameter's as the function is given it; an optional's or a
        ///     tuple's made at run time from its parts' where the function starts
        ///     (<c>sable_aggregate_type</c>, <c>sable_tuple_type</c>), once per
        ///     instance and kept at the call that asks.
        /// </summary>
        string OpaqueMetadata(SilType type) {
            if (type is OpaqueType opaque) {
                return _opaqueMetadata.TryGetValue(opaque.Name, out var metadata) ? metadata
                    : function.AssociatedMetadata.Find(a => a.Name == opaque.Name) is { } associated ? AssociatedMetadata(associated)
                    : throw new NotSupportedException($"{function.Name} has no metadata for {type}\n{SilPrinter.Print(function)}");
            }

            var key = type.ToString();
            if (_runtimeMetadata.TryGetValue(key, out var made)) {
                return made;
            }

            List<string> parts = type switch {
                TupleType tuple => tuple.Elements.Select(PartMetadata).ToList(),
                EnumType enumType => [..enumType.Cases.Select(c => c.Payload is { } payload ? PartMetadata(payload) : "null"), ..enumType.TypeArguments.Select(PartMetadata)],
                StructType structType => [..structType.Fields.Select(f => PartMetadata(f.Type)), ..structType.TypeArguments.Select(PartMetadata)],
                ClassType classType => [..classType.Fields.Select(f => PartMetadata(f.Type)), ..classType.TypeArguments.Select(PartMetadata)],
                _ => throw new NotSupportedException($"no metadata made at run time for {type} in {function.Name}"),
            };
            var list = Fresh();
            _entryAllocas.Append($"  {list} = alloca [{parts.Count} x ptr], align 8\n");
            for (var i = 0; i < parts.Count; i++) {
                var at = Fresh();
                _entryAllocas.Append($"  {at} = getelementptr inbounds [{parts.Count} x ptr], ptr {list}, i32 0, i32 {i}\n")
                    .Append($"  store ptr {parts[i]}, ptr {at}\n");
            }

            made = Fresh();
            var cache = module.RuntimeCache();
            _entryAllocas.Append(type switch {
                TupleType => $"  {made} = call ptr @sable_tuple_type(ptr {cache}, i64 {parts.Count}, ptr {list})\n",
                ClassType => $"  {made} = call ptr @sable_class_type(ptr {cache}, ptr {module.AggregateShape((NominalType)type)}, ptr {list})\n",
                _ => $"  {made} = call ptr @sable_aggregate_type(ptr {cache}, ptr {module.AggregateShape((NominalType)type)}, ptr {list})\n",
            });
            _runtimeMetadata[key] = made;
            return made;
        }

        /// <summary>
        ///     An associated type's metadata, found where the function starts:
        ///     from the table of its base's conformance — one the function is
        ///     given, through its bases, or the registry's — as its conformer binds it.
        /// </summary>
        string AssociatedMetadata(SilAssociatedMetadata associated) {
            var baseMetadata = OpaqueMetadata(new OpaqueType(associated.Base));
            string table;
            if (associated.Table is { } given && Hidden(given) is { } passed) {
                table = passed;
                foreach (var step in associated.Path) {
                    table = EntryTableEntry(table, WitnessTableBases, step);
                }
            } else {
                table = Fresh();
                _entryAllocas.Append($"  {table} = call ptr @sable_witness_table_cached(ptr {module.WitnessCache()}, ptr {baseMetadata}, ptr {module.ProtocolDescriptor(associated.Protocol)})\n");
            }

            var made = EntryTableEntry(table, WitnessTableAssociated, associated.Index);
            _opaqueMetadata[associated.Name] = made;
            return made;
        }

        /// <summary>
        ///     A hidden parameter by its name (<c>$metadata.T</c>, a table): the
        ///     function's own, or, for an unspecialised async function's resume
        ///     and drop functions, its frame's field of that name, read where
        ///     the function starts.
        /// </summary>
        string? Hidden(string name) {
            if (function.ParameterNames.IndexOf(name) is var at and >= 0 && at < function.Entry.Arguments.Count && function.HiddenFrame is null) {
                return $"%p{at}";
            }

            if (function.HiddenFrame is not { } frame || frame.FieldIndex(name) is not (var field and >= 0)) {
                return null;
            }

            if (!_hiddenFields.TryGetValue(name, out var loaded)) {
                var address = Fresh();
                loaded = Fresh();
                _entryAllocas.Append($"  {address} = getelementptr inbounds {ObjectType(frame)}, ptr %p0, i32 0, i32 {ObjectSlot(frame, field)}\n")
                    .Append($"  {loaded} = load ptr, ptr {address}\n");
                _hiddenFields[name] = loaded;
            }

            return loaded;
        }

        readonly Dictionary<string, string> _hiddenFields = new(StringComparer.Ordinal);

        /// <summary>An entry of a witness table's array, read where the function starts.</summary>
        string EntryTableEntry(string table, int field, int index) {
            var at = Fresh();
            var array = Fresh();
            var slot = Fresh();
            var entry = Fresh();
            _entryAllocas.Append($"  {at} = getelementptr inbounds {WitnessTableRecord}, ptr {table}, i32 0, i32 {field}\n")
                .Append($"  {array} = load ptr, ptr {at}\n")
                .Append($"  {slot} = getelementptr inbounds ptr, ptr {array}, i64 {index}\n")
                .Append($"  {entry} = load ptr, ptr {slot}\n");
            return entry;
        }

        /// <summary>A part's metadata, for one made at run time: an indirect one's, an array of them's, or a constant.</summary>
        string PartMetadata(SilType part) {
            if (!SilType.MentionsOpaque(part)) {
                return module.TypeMetadataGlobal(part);
            }

            if (part is ArrayType or SliceType) {
                var of = PartMetadata(ArrayType.ElementOf(part)!);
                var made = Fresh();
                _entryAllocas.Append($"  {made} = call ptr @sable_{(part is SliceType ? "slice" : "array")}_type(ptr {module.RuntimeCache()}, ptr {of})\n");
                return made;
            }

            // A function, of any type: two words, the second counted.
            if (part is ThickFunctionType) {
                return module.TypeMetadataGlobal(new ThickFunctionType(new FunctionType([], SilType.Unit)));
            }

            return OpaqueMetadata(part);
        }

        readonly Dictionary<string, string> _runtimeMetadata = new(StringComparer.Ordinal);

        /// <summary>The size of an opaque type's values, read from its metadata where the function starts.</summary>
        string OpaqueSize(SilType type) {
            if (!_opaqueSizes.TryGetValue(type.ToString(), out var size)) {
                var of = OpaqueMetadata(type);
                var at = Fresh();
                size = Fresh();
                _entryAllocas.Append($"  {at} = getelementptr inbounds %SableTypeMetadata, ptr {of}, i32 0, i32 {MetadataSizeField}\n")
                    .Append($"  {size} = load i64, ptr {at}\n");
                _opaqueSizes[type.ToString()] = size;
            }

            return size;
        }

        /// <summary>
        ///     A buffer for an opaque value, of its metadata's size, made where
        ///     the function starts — one for each value, which a loop reuses,
        ///     so the frame does not grow as it runs.
        /// </summary>
        string OpaqueBuffer(SilType type) {
            var size = OpaqueSize(type);
            var buffer = Fresh();
            _entryAllocas.Append($"  {buffer} = alloca i8, i64 {size}, align 16\n");
            return buffer;
        }

        /// <summary>The stride of an opaque type's values in an array, read from its metadata where the function starts.</summary>
        string OpaqueStride(SilType type) {
            if (!_opaqueStrides.TryGetValue(type.ToString(), out var stride)) {
                var of = OpaqueMetadata(type);
                var at = Fresh();
                stride = Fresh();
                _entryAllocas.Append($"  {at} = getelementptr inbounds %SableTypeMetadata, ptr {of}, i32 0, i32 {MetadataStrideField}\n")
                    .Append($"  {stride} = load i64, ptr {at}\n");
                _opaqueStrides[type.ToString()] = stride;
            }

            return stride;
        }

        readonly Dictionary<string, string> _opaqueStrides = new(StringComparer.Ordinal);

        readonly Dictionary<string, string> _opaqueArrays = new(StringComparer.Ordinal);

        /// <summary>
        ///     An array's object metadata: a constant for a type known here;
        ///     for an opaque element, the runtime's, made from the element's
        ///     metadata the first time any code asks (<c>sable_array_metadata</c>),
        ///     found where the function starts.
        /// </summary>
        string ArrayMetadataOf(ArrayType type) {
            if (!SilType.IsIndirect(type.Element)) {
                return module.ArrayMetadata(type);
            }

            var element = type.Element;
            if (!_opaqueArrays.TryGetValue(element.ToString(), out var metadata)) {
                var cache = module.RuntimeCache();
                var of = OpaqueMetadata(element);
                metadata = Fresh();
                _entryAllocas.Append($"  {metadata} = call ptr @sable_array_metadata(ptr {cache}, ptr {of})\n");
                _opaqueArrays[element.ToString()] = metadata;
            }

            return metadata;
        }

        /// <summary>The address of an element of an array's buffer, by its index: by the stride, for an opaque one.</summary>
        string ElementAt(SilType element, string elements, string index) {
            if (!SilType.IsIndirect(element)) {
                return $"getelementptr inbounds {Type(element)}, ptr {elements}, i64 {index}";
            }

            var offset = Fresh();
            Line($"{offset} = mul nsw i64 {index}, {OpaqueStride(element)}");
            return $"getelementptr inbounds i8, ptr {elements}, i64 {offset}";
        }

        /// <summary>A value stored into an element's place: an opaque one moved there.</summary>
        void StoreElement(SilType element, string value, string at) {
            if (SilType.IsIndirect(element)) {
                OpaqueMove(element, at, value);
            } else {
                Line($"store {Type(element)} {value}, ptr {at}, !tbaa {TbaaOf(Type(element))}");
            }
        }

        /// <summary>
        ///     A BF16 is lowered to its bits, an i16, and widened and narrowed
        ///     by integer arithmetic alone: LLVM's own bfloat is selected on
        ///     some targets not at all (AArch64 without +bf16, LLVM 18) and
        ///     narrowed from a double on others through a float, rounding twice.
        ///     This is the float a BF16 widens to exactly: its bits, the float's
        ///     top sixteen.
        /// </summary>
        string Bf16ToFloat(string value) {
            var wide = Fresh();
            var shifted = Fresh();
            var result = Fresh();
            Line($"{wide} = zext i16 {value} to i32");
            Line($"{shifted} = shl i32 {wide}, 16");
            Line($"{result} = bitcast i32 {shifted} to float");
            return result;
        }

        /// <summary>The double a BF16 widens to exactly.</summary>
        string Bf16ToDouble(string value) {
            var result = Fresh();
            Line($"{result} = fpext float {Bf16ToFloat(value)} to double");
            return result;
        }

        /// <summary>
        ///     The float rounded to the nearest BF16, ties to even, as
        ///     <c>BFloat16.FromSingle</c>: a NaN stays one, made quiet.
        /// </summary>
        void FloatToBf16(string value, string result) {
            var bits = Fresh();
            var high = Fresh();
            var odd = Fresh();
            var bias = Fresh();
            var rounded = Fresh();
            var roundedHigh = Fresh();
            var quiet = Fresh();
            var isNaN = Fresh();
            var chosen = Fresh();
            Line($"{bits} = bitcast float {value} to i32");
            Line($"{high} = lshr i32 {bits}, 16");
            Line($"{odd} = and i32 {high}, 1");
            Line($"{bias} = add i32 {odd}, 32767");
            Line($"{rounded} = add i32 {bits}, {bias}");
            Line($"{roundedHigh} = lshr i32 {rounded}, 16");
            Line($"{quiet} = or i32 {high}, 64");
            Line($"{isNaN} = fcmp uno float {value}, {value}");
            Line($"{chosen} = select i1 {isNaN}, i32 {quiet}, i32 {roundedHigh}");
            Line($"{result} = trunc i32 {chosen} to i16");
        }

        /// <summary>
        ///     The double rounded once to the nearest BF16, as
        ///     <c>BFloat16.FromDouble</c>: through a float rounded to odd, its
        ///     last bit set where anything was cut off, so the second rounding
        ///     never lands on a tie the first made. A NaN is the quiet one.
        /// </summary>
        void DoubleToBf16(string value, string result) {
            var single = Fresh();
            var back = Fresh();
            var inexact = Fresh();
            var magnitude = Fresh();
            var finite = Fresh();
            var adjust = Fresh();
            var bits = Fresh();
            var backMagnitude = Fresh();
            var valueMagnitude = Fresh();
            var away = Fresh();
            var down = Fresh();
            var towardZero = Fresh();
            var odd = Fresh();
            var adjusted = Fresh();
            var oddSingle = Fresh();
            var narrowed = Fresh();
            var isNaN = Fresh();
            Line($"{single} = fptrunc double {value} to float");
            Line($"{back} = fpext float {single} to double");
            Line($"{inexact} = fcmp one double {back}, {value}");
            Line($"{magnitude} = call float @llvm.fabs.f32(float {single})");
            Line($"{finite} = fcmp one float {magnitude}, 0x7FF0000000000000");
            Line($"{adjust} = and i1 {inexact}, {finite}");
            Line($"{bits} = bitcast float {single} to i32");
            Line($"{backMagnitude} = call double @llvm.fabs.f64(double {back})");
            Line($"{valueMagnitude} = call double @llvm.fabs.f64(double {value})");
            Line($"{away} = fcmp ogt double {backMagnitude}, {valueMagnitude}");
            Line($"{down} = sub i32 {bits}, 1");
            Line($"{towardZero} = select i1 {away}, i32 {down}, i32 {bits}");
            Line($"{odd} = or i32 {towardZero}, 1");
            Line($"{adjusted} = select i1 {adjust}, i32 {odd}, i32 {bits}");
            Line($"{oddSingle} = bitcast i32 {adjusted} to float");
            FloatToBf16(oddSingle, narrowed);
            Line($"{isNaN} = fcmp uno double {value}, {value}");
            Line($"{result} = select i1 {isNaN}, i16 32704, i16 {narrowed}");
        }

        /// <summary>
        ///     A BF16's intrinsic: on the double it widens to exactly, then
        ///     narrowed once — as the interpreter computes it, and with nothing
        ///     asked of the machine's bfloat support but the two conversions.
        ///     Its next value up, and its exponent, from its bits and the float
        ///     it widens to.
        /// </summary>
        bool Bf16Intrinsic(string op, BuiltinInst builtin, string[] operands) {
            string Widened(int index) {
                if (builtin.Operands[index].Type != SilType.BF16) {
                    return operands[index];
                }

                return Bf16ToDouble(operands[index]);
            }

            string Narrowed(string wide) {
                var name = Fresh();
                _names[builtin] = name;
                DoubleToBf16(wide, name);
                return name;
            }

            switch (op) {
                case "sqrt" or "floor" or "ceil" or "trunc" or "round" or "round_even": {
                    var x = Widened(0);
                    var computed = Fresh();
                    Line($"{computed} = call double @llvm.{(op == "round_even" ? "nearbyint" : op)}.f64(double {x})");
                    Narrowed(computed);
                    return true;
                }

                case "copysign" or "minimum" or "maximum": {
                    var (x, y) = (Widened(0), Widened(1));
                    var computed = Fresh();
                    Line($"{computed} = call double @llvm.{op}.f64(double {x}, double {y})");
                    Narrowed(computed);
                    return true;
                }

                case "remainder": {
                    var (x, y) = (Widened(0), Widened(1));
                    var computed = Fresh();
                    Line($"{computed} = call double @remainder(double {x}, double {y})");
                    Narrowed(computed);
                    return true;
                }

                case "fma": {
                    var (x, y, z) = (Widened(0), Widened(1), Widened(2));
                    var computed = Fresh();
                    Line($"{computed} = call double @llvm.fma.f64(double {x}, double {y}, double {z})");
                    Narrowed(computed);
                    return true;
                }

                case "scale": {
                    var x = Widened(0);
                    var low = Fresh();
                    var clamped = Fresh();
                    var power = Fresh();
                    var computed = Fresh();
                    Line($"{low} = call i64 @llvm.smax.i64(i64 {operands[1]}, i64 -100000)");
                    Line($"{clamped} = call i64 @llvm.smin.i64(i64 {low}, i64 100000)");
                    Line($"{power} = trunc i64 {clamped} to i32");
                    Line($"{computed} = call double @llvm.ldexp.f64.i32(double {x}, i32 {power})");
                    Narrowed(computed);
                    return true;
                }

                case "exponent": {
                    var x = Widened(0);
                    var parts = Fresh();
                    var exponent = Fresh();
                    var less = Fresh();
                    var wide = Fresh();
                    var magnitude = Fresh();
                    var finite = Fresh();
                    var isZero = Fresh();
                    var unlessZero = Fresh();
                    Line($"{parts} = call {{ double, i32 }} @llvm.frexp.f64.i32(double {x})");
                    Line($"{exponent} = extractvalue {{ double, i32 }} {parts}, 1");
                    Line($"{less} = sub i32 {exponent}, 1");
                    Line($"{wide} = sext i32 {less} to i64");
                    Line($"{magnitude} = call double @llvm.fabs.f64(double {x})");
                    Line($"{finite} = fcmp olt double {magnitude}, {FloatInfinity}");
                    Line($"{isZero} = fcmp oeq double {magnitude}, {FloatZero}");
                    Line($"{unlessZero} = select i1 {finite}, i64 {wide}, i64 {long.MaxValue}");
                    var result = Fresh();
                    _names[builtin] = result;
                    Line($"{result} = select i1 {isZero}, i64 {long.MinValue}, i64 {unlessZero}");
                    return true;
                }

                // As any float's: the bits one away from zero, or toward it.
                case "next_up": {
                    var x = Widened(0);
                    var bits = Fresh();
                    var up = Fresh();
                    var down = Fresh();
                    var positive = Fresh();
                    var stepped = Fresh();
                    var isZero = Fresh();
                    var fromZero = Fresh();
                    var isStuck = Fresh();
                    var value = Fresh();
                    Line($"{bits} = or i16 {operands[0]}, 0");
                    Line($"{up} = add i16 {bits}, 1");
                    Line($"{down} = sub i16 {bits}, 1");
                    Line($"{positive} = fcmp ogt double {x}, {FloatZero}");
                    Line($"{stepped} = select i1 {positive}, i16 {up}, i16 {down}");
                    Line($"{isZero} = fcmp oeq double {x}, {FloatZero}");
                    Line($"{fromZero} = select i1 {isZero}, i16 1, i16 {stepped}");
                    Line($"{value} = or i16 {fromZero}, 0");
                    Line($"{isStuck} = fcmp ueq double {x}, {FloatInfinity}");
                    var result = Fresh();
                    _names[builtin] = result;
                    Line($"{result} = select i1 {isStuck}, i16 {operands[0]}, i16 {value}");
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        ///     An atomic operation on the value at a raw address. Its memory
        ///     ordering is an Int, 0 relaxed to 4 sequentially consistent, and
        ///     a branch for each is emitted, joined after: LLVM folds them to
        ///     the one a constant names, as every call through Atomic's members
        ///     has once they are inlined. An ordering an instruction cannot
        ///     take is the nearest one it can (the library refuses such a one
        ///     before it gets here). A Bool is a byte in memory.
        /// </summary>
        void Atomic(BuiltinInst builtin, string[] operands, Func<string> result) {
            string[] names = ["monotonic", "acquire", "release", "acq_rel", "seq_cst"];
            if (builtin.Name == "atomic_fence") {
                // A relaxed fence orders nothing: it is no instruction at all.
                Ordered(operands[0], null, ordering => {
                    if (ordering != 0) {
                        Line($"fence {names[ordering]}");
                    }

                    return null;
                });
                return;
            }

            var separator = builtin.Name.LastIndexOf('_');
            var op = builtin.Name["atomic_".Length..separator];
            var type = SilType.AtomicTypes.First(t => t.ToString() == builtin.Name[(separator + 1)..]);
            var isBool = type == SilType.Bool;
            var stored = isBool ? "i8" : Type(type);
            var align = TypeLayout.Size(type);
            var signed = SilType.IntegerInfo(type) is (_, true);
            var at = RawAddress(operands[0]);

            string Widened(string value) {
                if (!isBool) {
                    return value;
                }

                var wide = Fresh();
                Line($"{wide} = zext i1 {value} to i8");
                return wide;
            }

            string Narrowed(string value) {
                if (!isBool) {
                    return value;
                }

                var narrow = Fresh();
                Line($"{narrow} = icmp ne i8 {value}, 0");
                return narrow;
            }

            switch (op) {
                case "load":
                    Ordered(operands[1], Type(type), ordering => {
                        var loaded = Fresh();
                        Line($"{loaded} = load atomic {stored}, ptr {at} {names[ordering switch { 2 => 0, 3 => 1, _ => ordering }]}, align {align}");
                        return Narrowed(loaded);
                    }, result);
                    return;

                case "store": {
                    var value = Widened(operands[1]);
                    Ordered(operands[2], null, ordering => {
                        Line($"store atomic {stored} {value}, ptr {at} {names[ordering switch { 1 => 0, 3 => 2, _ => ordering }]}, align {align}");
                        return null;
                    });
                    return;
                }

                case "compare_exchange" or "compare_exchange_weak": {
                    var expected = Widened(operands[1]);
                    var desired = Widened(operands[2]);
                    var tupleType = Type(builtin.Type);
                    Ordered(operands[3], tupleType, ordering => {
                        var failure = ordering switch { 2 => 0, 3 => 1, _ => ordering };
                        var pair = Fresh();
                        var old = Fresh();
                        var swapped = Fresh();
                        var first = Fresh();
                        var both = Fresh();
                        Line($"{pair} = cmpxchg {(op == "compare_exchange_weak" ? "weak " : "")}ptr {at}, {stored} {expected}, {stored} {desired} {names[ordering]} {names[failure]}, align {align}");
                        Line($"{old} = extractvalue {{ {stored}, i1 }} {pair}, 0");
                        Line($"{swapped} = extractvalue {{ {stored}, i1 }} {pair}, 1");
                        Line($"{first} = insertvalue {tupleType} undef, {Type(type)} {Narrowed(old)}, 0");
                        Line($"{both} = insertvalue {tupleType} {first}, i1 {swapped}, 1");
                        return both;
                    }, result);
                    return;
                }

                default: {
                    var rmw = op switch {
                        "exchange" => "xchg",
                        "min" => signed ? "min" : "umin",
                        "max" => signed ? "max" : "umax",
                        _ => op,
                    };
                    var operand = Widened(operands[1]);
                    Ordered(operands[2], Type(type), ordering => {
                        var found = Fresh();
                        Line($"{found} = atomicrmw {rmw} ptr {at}, {stored} {operand} {names[ordering]}, align {align}");
                        return Narrowed(found);
                    }, result);
                    return;
                }
            }
        }

        /// <summary>
        ///     A branch for each memory ordering, 0 to 4, of the Int given —
        ///     anything else taken as 4 — each emitted by 'body', and their
        ///     values, of the type given, joined in the result.
        /// </summary>
        void Ordered(string ordering, string? type, Func<int, string?> body, Func<string>? result = null) {
            var join = NewLabel("ordered");
            var labels = Enumerable.Range(0, 5).Select(i => NewLabel($"ordering{i}")).ToArray();
            Line($"switch i64 {ordering}, label %{labels[4]} [ i64 0, label %{labels[0]} i64 1, label %{labels[1]} i64 2, label %{labels[2]} i64 3, label %{labels[3]} ]");
            var incoming = new List<(string Value, string Label)>();
            for (var i = 0; i < 5; i++) {
                Continue(labels[i]);
                var value = body(i);
                if (value is not null) {
                    incoming.Add((value, _currentLabel));
                }

                Line($"br label %{join}");
            }

            Continue(join);
            if (type is not null && result is not null) {
                Line($"{result()} = phi {type} {string.Join(", ", incoming.Select(static v => $"[ {v.Value}, %{v.Label} ]"))}");
            }
        }

        /// <summary>A raw address, an Int, as a pointer.</summary>
        string RawAddress(string address) {
            var pointer = Fresh();
            Line($"{pointer} = inttoptr i64 {address} to ptr");
            return pointer;
        }

        /// <summary>An opaque value moved from one place to another: its bits, as every type's take is.</summary>
        void OpaqueMove(SilType type, string destination, string source) =>
            Line($"call void @llvm.memmove.p0.p0.i64(ptr {destination}, ptr {source}, i64 {OpaqueSize(type)}, i1 false)");

        /// <summary>One of an opaque type's value witnesses, called on its values' addresses and its metadata.</summary>
        void OpaqueWitness(SilType type, int witness, params string[] addresses) {
            var metadata = OpaqueMetadata(type);
            var field = Fresh();
            var table = Fresh();
            var at = Fresh();
            var code = Fresh();
            Line($"{field} = getelementptr inbounds %SableTypeMetadata, ptr {metadata}, i32 0, i32 {MetadataWitnessesField}");
            Line($"{table} = load ptr, ptr {field}");
            Line($"{at} = getelementptr inbounds %SableValueWitnesses, ptr {table}, i32 0, i32 {witness}");
            Line($"{code} = load ptr, ptr {at}");
            Line($"call void {code}({string.Join(", ", addresses.Select(static a => $"ptr {a}"))}, ptr {metadata})");
        }

        string Scratch(string type, int align = 0) {
            var name = Fresh();
            _entryAllocas.Append("  ").Append(name).Append(" = alloca ").Append(type).Append(align > 0 ? $", align {align}" : "").Append('\n');
            return name;
        }

        StringBuilder _out = null!;
        string _currentLabel = "";
        int _temporaries;
        int _extraBlocks;

        /// <summary>The function's subprogram, when there is debug information.</summary>
        string? _scope;

        /// <summary>The lexical blocks' nodes, made as an instruction or a variable is first in one.</summary>
        readonly Dictionary<int, string> _blocks = [];

        /// <summary>
        ///     The node for a lexical scope of the function: the subprogram
        ///     for its own (0), else a block inside its parent's. One the
        ///     function does not know — code a pass cloned — is the function's.
        /// </summary>
        string ScopeNode(int scope) {
            if (scope <= 0 || scope > function.Scopes.Count) {
                return _scope!;
            }

            if (!_blocks.TryGetValue(scope, out var node)) {
                var (parent, line, column) = function.Scopes[scope - 1];
                _blocks[scope] = node = Debug!.LexicalBlock(ScopeNode(parent), Debug.FileOf(function), line, column);
            }

            return node;
        }

        /// <summary>The <c>!dbg</c> attachment for what is being written now.</summary>
        string _dbg = "";

        DebugInfo? Debug => module._debug;

        /// <summary>
        ///     What a parameter's pointer is known to point to for the whole
        ///     call: storage the caller lends, or an object it keeps alive.
        ///     Read before it is known to be needed, it faults no more than
        ///     when it is: what lets LLVM load an array's buffer once before
        ///     a loop that may not go round.
        /// </summary>
        static string Dereferenceable(SilArgument argument) {
            var (size, align) = argument.Type switch {
                AddressType { Pointee: var pointee } when !SilType.IsIndirect(pointee) && !SilType.MentionsOpaque(pointee) => (TypeLayout.Size(pointee), TypeLayout.Align(pointee)),
                ClassType { IsCoroutineFrame: false } held when !SilType.MentionsOpaque(held) && SilVerifier.OwnershipOf(argument) == SIL.Ownership.Guaranteed
                    => (TypeLayout.Object(held).Size, TypeLayout.PointerBytes),
                _ => (0, 1),
            };
            return size > 0 ? $" dereferenceable({size}) align {Math.Min(align, HeapAlign)}" : "";
        }

        public string Emit() {
            // Every function has a subprogram; one the compiler made, with no
            // source of its own, is artificial, its code all at line 0.
            if (Debug is not null) {
                _scope = Debug.Subprogram(function);
                _dbg = EntryLocation;
            }

            for (var i = 0; i < function.Blocks.Count; i++) {
                _labels[function.Blocks[i]] = $"bb{i}";
            }

            // Parameters are the entry block's arguments.
            var parameters = new List<string>();
            for (var i = 0; i < function.Entry.Arguments.Count; i++) {
                var argument = function.Entry.Arguments[i];
                _names[argument] = $"%p{i}";

                // An inout or ref parameter is exclusive for the call (docs/02
                // §2.3): nothing else the callee can reach is the same storage.
                // That is LLVM's noalias, and what lets it keep an array's
                // buffer and count in registers across writes to elements.
                parameters.Add(argument.Type is AddressType ? $"ptr noalias{Dereferenceable(argument)} %p{i}" : $"{ArgumentType(argument.Type)}{Dereferenceable(argument)} %p{i}");
            }

            // Unspecialised code's metadata (#297): what its opaque values are
            // sized and copied by — in an async function's resume and drop
            // functions, as its frame keeps them.
            if (function.HiddenFrame is { } hiddenFrame) {
                foreach (var (fieldName, _) in hiddenFrame.Fields) {
                    if (fieldName.StartsWith("$metadata.", StringComparison.Ordinal)) {
                        _opaqueMetadata[fieldName["$metadata.".Length..]] = Hidden(fieldName)!;
                    }
                }
            } else {
                for (var i = 0; i < function.ParameterNames.Count && i < function.Entry.Arguments.Count; i++) {
                    if (function.ParameterNames[i].StartsWith("$metadata.", StringComparison.Ordinal)) {
                        _opaqueMetadata[function.ParameterNames[i]["$metadata.".Length..]] = $"%p{i}";
                    }
                }
            }

            foreach (var block in function.Blocks.Skip(1)) {
                foreach (var argument in block.Arguments) {
                    // A block's opaque argument is a buffer of its own, which each edge moves the value into.
                    if (argument.Type is var opaqueArgument && SilType.IsIndirect(opaqueArgument)) {
                        _names[argument] = OpaqueBuffer(opaqueArgument);
                        continue;
                    }

                    _names[argument] = Fresh();
                    _incoming[argument] = [];
                }
            }

            // Written in reverse postorder, where every definition comes before
            // its uses; printed in the function's own order.
            foreach (var block in ReversePostorder(function)) {
                _out = new StringBuilder();
                _bodies[block] = _out;
                _currentLabel = _labels[block];
                foreach (var instruction in block.Instructions) {
                    Instruction(instruction);
                }
            }

            foreach (var block in function.Blocks) {
                _bodies.TryAdd(block, new StringBuilder("  unreachable\n"));
            }

            var text = new StringBuilder();
            // Sable has no unwinding: an error is a returned value and a trap
            // ends the program. Said, LLVM needs no unwind paths for calls.
            var linkage = module.Linkage(function.Name);
            if (suffix.Length > 0 && linkage.Length == 0) {
                linkage = "internal ";
            }

            var symbol = Global(function.Name + suffix);
            text.Append("define ").Append(linkage).Append(ReturnType(function.Type)).Append(" @").Append(symbol)
                .Append('(').Append(string.Join(", ", parameters)).Append(") nounwind")
                .Append(suffix == BaselineSuffix ? " alwaysinline"
                    : suffix == WideSuffix ? " " + WideAttributes
                    : function.Inline switch {
                        SilInline.Always => " alwaysinline",
                        SilInline.Never => " noinline",
                        _ => "",
                    })
                .Append(module.Comdat("@" + symbol, linkage) is { Length: > 0 } ? " comdat" : "");
            if (_scope is not null) {
                text.Append(" !dbg ").Append(_scope);
            }

            text.Append(" {\n");

            // The parameters, for the debugger: each stored in a slot of its
            // own, as clang does at -O0, so that it can be read anywhere in
            // the function and not only while its register still holds it.
            if (_scope is not null && Debug is not null) {
                var file = Debug.FileOf(function);
                var line = function.SourceFile is null ? 0 : function.Line;
                for (var i = 0; i < function.Entry.Arguments.Count; i++) {
                    var argument = function.Entry.Arguments[i];
                    if (argument.Type == SilType.Unit) {
                        continue;
                    }

                    var slot = $"%p{i}.debug";
                    var declarations = new List<(string Variable, string Expression)>();
                    if (argument.Type == SilType.Object && ContextType(argument) is { IsCoroutineFrame: true } frame) {
                        // An async function's resume function is given its
                        // frame, where its parameters and locals live across
                        // its awaits (AsyncLowering): each shown by its own
                        // name — a local's field is the name and a number —
                        // through the box of one a closure captures.
                        foreach (var (fieldName, fieldType, offset) in ObjectFields(frame)) {
                            if (fieldName.StartsWith('$') && !fieldName.StartsWith("$metadata.", StringComparison.Ordinal) || fieldType == SilType.Unit) {
                                continue;
                            }

                            var cut = fieldName.LastIndexOf('$');
                            var shown = cut > 0 && fieldName[(cut + 1)..].All(char.IsAsciiDigit) ? fieldName[..cut] : fieldName;
                            // One whose type only metadata knows: its address is in the frame.
                            if (fieldType is AddressType { Pointee: var held } && SilType.IsIndirect(held)) {
                                var at = Fresh();
                                var loaded = Fresh();
                                _entryAllocas.Append($"  {at} = getelementptr inbounds i8, ptr %p{i}, i64 {offset}\n  {loaded} = load ptr, ptr {at}\n");
                                DeclareDynamic($"%p{i}.{offset}.debug", loaded, held, shown, _scope, file, line, 0, EntryLocation);
                                continue;
                            }

                            declarations.Add(BoxedType(fieldType) is { } inner && !SilType.IsIndirect(inner)
                                ? (Debug.Variable(shown, _scope, file, line, inner), $"DW_OP_deref, DW_OP_plus_uconst, {offset}, DW_OP_deref, DW_OP_plus_uconst, {ObjectHeaderSize}")
                                : (Debug.Variable(shown, _scope, file, line, fieldType), $"DW_OP_deref, DW_OP_plus_uconst, {offset}"));
                        }
                    } else if (i < function.ParameterNames.Count && SilType.IsIndirect(argument.Type is AddressType { Pointee: var pointee } ? pointee : argument.Type)) {
                        // A value whose type only metadata knows, and an inout
                        // parameter of one: given by address either way.
                        if (!function.ParameterNames[i].StartsWith('$')) {
                            DeclareDynamic(slot, _names[argument], argument.Type is AddressType { Pointee: var given } ? given : argument.Type, function.ParameterNames[i], _scope, file, line, i + 1, EntryLocation);
                        }
                    } else if (i < function.ParameterNames.Count && argument.Type is AddressType { Pointee: StructType or ArrayType or SliceType } inout && SilType.MentionsOpaque(inout)) {
                        // So is an inout parameter whose type's name has a type
                        // parameter in it, shown as the value.
                        declarations.Add((Debug.Variable(function.ParameterNames[i], _scope, file, line, inout.Pointee, i + 1), "DW_OP_deref"));
                    } else if (i < function.ParameterNames.Count) {
                        declarations.Add((Debug.Variable(function.ParameterNames[i], _scope, file, line, argument.Type, i + 1), ""));
                    } else if (argument.Type == SilType.Object && ContextType(argument) is { } context) {
                        if (BoxedType(context) is { } boxed && !SilType.IsIndirect(boxed)) {
                            // A protocol witness's context is the box an
                            // existential keeps its value in: that value is its self.
                            declarations.Add((Debug.Variable("self", _scope, file, line, boxed, i + 1), $"DW_OP_deref, DW_OP_plus_uconst, {ObjectHeaderSize}"));
                        } else if (context.Name.EndsWith("$context", StringComparison.Ordinal)) {
                            // A closure's: what it captured, each by its own
                            // name, as if a local — through the box of one
                            // captured by reference.
                            foreach (var (fieldName, fieldType, offset) in ObjectFields(context)) {
                                // One whose type only metadata knows is a local of the closure's own code, read from here.
                                if (fieldType is ArrayType { Element: var element } && SilType.IsIndirect(element)
                                    && function.Blocks.SelectMany(static b => b.Instructions).OfType<AllocStackInst>().Any(a => a.VariableName == fieldName && a.ElementType == element)) {
                                    continue;
                                }

                                declarations.Add(BoxedType(fieldType) is { } inner && !SilType.IsIndirect(inner)
                                    ? (Debug.Variable(fieldName, _scope, file, line, inner), $"DW_OP_deref, DW_OP_plus_uconst, {offset}, DW_OP_deref, DW_OP_plus_uconst, {ObjectHeaderSize}")
                                    : (Debug.Variable(fieldName, _scope, file, line, fieldType), $"DW_OP_deref, DW_OP_plus_uconst, {offset}"));
                            }
                        }
                    }

                    if (declarations.Count == 0) {
                        continue;
                    }

                    _entryAllocas.Append($"  {slot} = alloca {Type(argument.Type)}{StorageAlign(argument.Type)}\n")
                        .Append($"  store {Type(argument.Type)} {_names[argument]}, ptr {slot}\n");
                    foreach (var (variable, expression) in declarations) {
                        _entryAllocas.Append($"  call void @llvm.dbg.declare(metadata ptr {slot}, metadata {variable}, metadata !DIExpression({expression})){EntryLocation}\n");
                    }
                }
            }

            foreach (var block in function.Blocks) {
                text.Append(_labels[block]).Append(":\n");
                if (block == function.Entry) {
                    text.Append(_entryAllocas);
                }

                foreach (var argument in block.Arguments) {
                    // A block given nothing — a case's payload of Never, laid out as nothing — has no phi.
                    if (!_incoming.TryGetValue(argument, out var incoming) || argument.Type == SilType.Unit) {
                        continue;
                    }

                    text.Append("  ").Append(_names[argument]).Append(" = phi ").Append(Type(argument.Type)).Append(' ')
                        .Append(string.Join(", ", incoming.Select(static i => $"[ {i.Value}, %{i.From} ]")))
                        .Append('\n');
                }

                text.Append(_bodies[block]);
            }

            text.Append(_extra);
            text.Append("}\n");
            return text.ToString();
        }

        string Fresh() => $"%t{_temporaries++}";

        /// <summary>
        ///     Declares, where the function starts, a variable whose type only
        ///     metadata knows: a slot holding the value's address and its
        ///     type's metadata, so that a debugger reads both at any place in
        ///     the function. The metadata is null where it is not known without
        ///     asking the registry, which a debugger's slot is no reason to.
        /// </summary>
        void DeclareDynamic(string slot, string address, SilType type, string variable, string scope, string file, int line, int argument, string location) {
            var metadata = (type is OpaqueType || function.SourceFile is not null) && MetadataAtHand(type, []) ? OpaqueMetadata(type) : "null";
            var node = Debug!.DynamicVariable(variable, scope, file, line, type, argument);
            _entryAllocas.Append($"  {slot} = alloca {{ ptr, ptr }}, align 8\n")
                .Append($"  {slot}.metadata = getelementptr inbounds {{ ptr, ptr }}, ptr {slot}, i32 0, i32 1\n")
                .Append($"  store ptr {address}, ptr {slot}\n")
                .Append($"  store ptr {metadata}, ptr {slot}.metadata\n")
                .Append($"  call void @llvm.dbg.declare(metadata ptr {slot}, metadata {node}, metadata !DIExpression()){location}\n");
        }

        /// <summary>Whether a type's metadata is had or made from what the function has already, asking nothing of the registry.</summary>
        bool MetadataAtHand(SilType type, HashSet<string> inside) {
            if (!SilType.MentionsOpaque(type)) {
                return true;
            }

            return type switch {
                OpaqueType opaque => _opaqueMetadata.ContainsKey(opaque.Name)
                                     || function.AssociatedMetadata.Find(a => a.Name == opaque.Name) is { Table: { } table } associated && Hidden(table) is not null
                                     && MetadataAtHand(new OpaqueType(associated.Base), inside),
                _ when _runtimeMetadata.ContainsKey(type.ToString()) => true,
                ArrayType or SliceType => MetadataAtHand(ArrayType.ElementOf(type)!, inside),
                TupleType tuple => tuple.Elements.All(e => MetadataAtHand(e, inside)),
                NominalType nominal when !inside.Contains(nominal.Name) => Parts(nominal).Concat(nominal.TypeArguments).All(p => MetadataAtHand(p, [..inside, nominal.Name])),
                _ => false,
            };

            static IEnumerable<SilType> Parts(NominalType nominal) =>
                nominal switch {
                    EnumType enumType => enumType.Cases.Select(static c => c.Payload).OfType<SilType>(),
                    StructType structType => structType.Fields.Select(static f => f.Type),
                    ClassType classType => classType.Fields.Select(static f => f.Type),
                    _ => [],
                };
        }

        /// <summary>The <c>!dbg</c> attachment for the function's prologue: its own line.</summary>
        string EntryLocation => function.SourceFile is null
            ? $", !dbg {Debug!.Location(0, 0, _scope!)}"
            : $", !dbg {Debug!.Location(function.Line, 1, _scope!)}";

        /// <summary>The class a closure's context is, from the cast its code starts with; or null.</summary>
        ClassType? ContextType(SilArgument context) =>
            function.Entry.Instructions.OfType<UncheckedRefCastInst>().FirstOrDefault(c => c.Value == context)?.Type as ClassType;

        static List<SilBlock> ReversePostorder(SilFunction function) {
            var visited = new HashSet<SilBlock>();
            var postorder = new List<SilBlock>();

            // Iterative, so that a long chain of blocks cannot overflow the stack.
            var stack = new Stack<(SilBlock Block, IEnumerator<SilBlock> Successors)>();
            visited.Add(function.Entry);
            stack.Push((function.Entry, function.Entry.Successors.GetEnumerator()));
            while (stack.Count > 0) {
                var (block, successors) = stack.Peek();
                if (successors.MoveNext()) {
                    if (visited.Add(successors.Current)) {
                        stack.Push((successors.Current, successors.Current.Successors.GetEnumerator()));
                    }

                    continue;
                }

                stack.Pop();
                postorder.Add(block);
            }

            postorder.Reverse();
            return postorder;
        }

        string Name(SilValue value) =>
            _names.TryGetValue(value, out var name)
                ? name
                : throw new InvalidOperationException("a value used before it was written");

        void Line(string text) => _out.Append("  ").Append(text).Append(_dbg).Append('\n');

        /// <summary>The object an optional class reference holds, or null: what a weak reference is made from.</summary>
        string WeakTarget(EnumType optional, string value) {
            if (IsNullable(optional)) {
                return value;
            }

            var type = Type(optional);
            var tag = Fresh();
            var word = Fresh();
            var pointer = Fresh();
            var present = Fresh();
            var target = Fresh();
            Line($"{tag} = extractvalue {type} {value}, 0");
            Line($"{word} = extractvalue {type} {value}, 1, 0");
            Line($"{pointer} = inttoptr i64 {word} to ptr");
            Line($"{present} = icmp eq {TagType(optional)} {tag}, {optional.CaseIndex("Some")}");
            Line($"{target} = select i1 {present}, ptr {pointer}, ptr null");
            return target;
        }

        /// <summary>An object a weak reference held, or null, as the optional it reads as.</summary>
        string WeakOptional(EnumType optional, string target) {
            if (IsNullable(optional)) {
                return target;
            }

            var type = Type(optional);
            var absent = Fresh();
            var tag = Fresh();
            var word = Fresh();
            var tagged = Fresh();
            var name = Fresh();
            Line($"{absent} = icmp eq ptr {target}, null");
            Line($"{tag} = select i1 {absent}, {TagType(optional)} {optional.CaseIndex("None")}, {TagType(optional)} {optional.CaseIndex("Some")}");
            Line($"{word} = ptrtoint ptr {target} to i64");
            Line($"{tagged} = insertvalue {type} zeroinitializer, {TagType(optional)} {tag}, 0");
            Line($"{name} = insertvalue {type} {tagged}, i64 {word}, 1, 0");
            return name;
        }

        /// <summary>A retain (copy) or release (destroy) of a value, as its type needs.</summary>
        void Own(bool copy, SilType type, string value) => module.Ownership(_out, "  ", copy, type, value, _dbg);

        /// <summary>Starts a new LLVM block inside the current SIL block: after a check, the code that runs when it passes.</summary>
        void Continue(string label) {
            _out.Append(label).Append(":\n");
            _currentLabel = label;
        }

        string NewLabel(string stem) => $"{stem}{_extraBlocks++}";

        /// <summary>Branches to a trap with this message when the condition holds, and carries on otherwise.</summary>
        /// <summary>An offset into bytes below zero traps, as an index out of range.</summary>
        /// <summary>
        ///     Traps unless the index, and the elements after it, as many as
        ///     are wanted in all, are in range: one comparison, of the index
        ///     with the first that has too few from it on — none has enough,
        ///     and that is 0, in an array of fewer.
        /// </summary>
        void IndexTraps(string index, string count, int elements) {
            var limit = count;
            if (elements > 1) {
                limit = Fresh();
                Line($"{limit} = call i64 @llvm.usub.sat.i64(i64 {count}, i64 {elements - 1})");
            }

            var outside = Fresh();
            Line($"{outside} = icmp uge i64 {index}, {limit}");
            TrapIf(outside, "array index out of range");
        }

        void NegativeOffsetTraps(string offset) {
            var negative = Fresh();
            Line($"{negative} = icmp slt i64 {offset}, 0");
            TrapIf(negative, "array index out of range");
        }

        /// <summary>
        ///     The scalar at a byte of a string, or where the next starts: an
        ///     ASCII byte's read in place, any other's a call — which is also
        ///     where an offset not known to be in range traps.
        /// </summary>
        void AsciiOrCall(BuiltinInst builtin, string[] operands, Func<string> Result, bool inRange) {
            var (call, done) = (NewLabel("wide"), NewLabel("scalar"));
            if (!inRange) {
                var field = Fresh();
                var length = Fresh();
                var inside = Fresh();
                var read = NewLabel("inside");
                Line($"{field} = getelementptr inbounds %SableStringHeader, ptr {operands[0]}, i32 0, i32 1");
                Line($"{length} = load i64, ptr {field}, !tbaa {TbaaHeader}");
                Line($"{inside} = icmp ult i64 {operands[1]}, {length}");
                Line($"br i1 {inside}, label %{read}, label %{call}");
                Continue(read);
            }

            var bytes = Fresh();
            var at = Fresh();
            var value = Fresh();
            var ascii = Fresh();
            var fast = Fresh();
            var slow = Fresh();
            var narrow = NewLabel("ascii");
            Line($"{bytes} = getelementptr inbounds %SableStringHeader, ptr {operands[0]}, i64 1");
            Line($"{at} = getelementptr inbounds i8, ptr {bytes}, i64 {operands[1]}");
            Line($"{value} = load i8, ptr {at}");
            Line($"{ascii} = icmp sgt i8 {value}, -1");
            Line($"br i1 {ascii}, label %{narrow}, label %{call}");
            Continue(narrow);
            var scalar = builtin.Name.EndsWith("_scalar", StringComparison.Ordinal);
            Line(scalar ? $"{fast} = zext i8 {value} to i32" : $"{fast} = add i64 {operands[1]}, 1");
            Line($"br label %{done}");
            Continue(call);
            if (!inRange) {
                SiteForRuntime();
            }

            Line($"{slow} = call {(scalar ? "i32" : "i64")} @sable_{builtin.Name}(ptr {operands[0]}, i64 {operands[1]})");
            Line($"br label %{done}");
            Continue(done);
            Line($"{Result()} = phi {(scalar ? "i32" : "i64")} [ {fast}, %{narrow} ], [ {slow}, %{call} ]");
        }

        void TrapIf(string condition, string message) {
            var trap = NewLabel("trap");
            var ok = NewLabel("ok");
            Line($"br i1 {condition}, label %{trap}, label %{ok}");
            _extra.Append(trap).Append(":\n")
                .Append("  call void @sable_trap_at(ptr ").Append(module.TrapMessage(message)).Append(", ptr ").Append(_site).Append(')').Append(_dbg).Append('\n')
                .Append("  unreachable\n");
            Continue(ok);
        }

        /// <summary>
        ///     The current instruction's place in the source, for a trap to
        ///     report: a constant string, or <c>null</c> when it has none.
        /// </summary>
        string _site = "null";

        /// <summary>
        ///     Before a call into the runtime that can trap: tells it where the
        ///     call is, so that its trap names the line (the runtime's
        ///     <c>sable_trap_site</c>).
        /// </summary>
        void SiteForRuntime() => Line($"store ptr {_site}, ptr @sable_trap_site");

        /// <summary>The size of what an address holds, in bytes, as LLVM lays it out.</summary>
        string StorageSize(SilValue address) =>
            $"ptrtoint (ptr getelementptr ({Type(((AddressType)address.Type).Pointee)}, ptr null, i32 1) to i64)";

        /// <summary>
        ///     A plain read or write of shared storage, in a checked build: a
        ///     trap if it overlaps an access in progress. Inline, the check is
        ///     whether any access is in progress at all; the runtime looks
        ///     further only when one is.
        /// </summary>
        void CheckAccess(SilValue address, bool modify) {
            var count = Fresh();
            var busy = Fresh();
            var check = NewLabel("access");
            var free = NewLabel("unaccessed");
            Line($"{count} = load i64, ptr @sable_access_count");
            Line($"{busy} = icmp ne i64 {count}, 0");
            Line($"br i1 {busy}, label %{check}, label %{free}, !prof !{{!\"branch_weights\", i32 1, i32 2000}}");
            Continue(check);
            SiteForRuntime();
            Line($"call void @sable_access_check(ptr {Name(address)}, i64 {StorageSize(address)}, i32 {(modify ? 1 : 0)})");
            Line($"br label %{free}");
            Continue(free);
        }

        void Instruction(SilInstruction instruction) {
            _site = instruction.TrapSite(function) is { } site ? module.TrapMessage(site) : "null";

            // A slot is made where the function starts, whatever line declared its variable.
            if (_scope is not null && instruction.Location is { } location && instruction is not AllocStackInst) {
                _dbg = $", !dbg {Debug!.Location(location.Line, location.Column, ScopeNode(location.Scope))}";
            }

            switch (instruction) {
                case IntegerLiteralInst literal:
                    _names[literal] = literal.Type == SilType.Bool
                        ? literal.Value != 0 ? "true" : "false"
                        : literal.WideValue.ToString(CultureInfo.InvariantCulture);
                    return;

                // A BF16's is its own bits, the i16 it is lowered to.
                case FloatLiteralInst { Type: var bf16 } literal when bf16 == SilType.BF16:
                    _names[literal] = ((short)Sable.SIL.BFloat16.FromDouble(literal.Value).Bits).ToString(CultureInfo.InvariantCulture);
                    return;

                case FloatLiteralInst literal:
                    // The bit pattern: exact, whatever the value.
                    // An F32 or F16 one is written as the double of the value it rounds to, as LLVM wants.
                    var exact = literal.Type == SilType.F32 ? (double)(float)literal.Value
                        : literal.Type == SilType.F16 ? (double)(Half)literal.Value
                        : literal.Value;
                    _names[literal] = "0x" + BitConverter.DoubleToInt64Bits(exact).ToString("X16", CultureInfo.InvariantCulture);
                    return;

                case StringLiteralInst literal:
                    _names[literal] = module.StringConstant(literal.Value);
                    return;

                // An optional or a tuple of a type parameter's values: made at run time.
                case MetadataInst indirectMetadata when SilType.MentionsOpaque(indirectMetadata.Described):
                    _names[indirectMetadata] = PartMetadata(indirectMetadata.Described);
                    return;

                case MetadataInst metadata:
                    _names[metadata] = module.TypeMetadataGlobal(metadata.Described);
                    return;

                case BuiltinInst builtin:
                    Builtin(builtin);
                    return;

                // Storage for an opaque value: a buffer of its metadata's size, empty.
                case AllocStackInst { ElementType: var opaqueSlot } alloc when SilType.IsIndirect(opaqueSlot): {
                    var buffer = OpaqueBuffer(opaqueSlot);
                    _names[alloc] = buffer;
                    var here = _dbg;
                    if (alloc.Block == function.Entry) {
                        _dbg = "";
                    }

                    Line($"call void @llvm.memset.p0.i64(ptr align 16 {buffer}, i8 0, i64 {OpaqueSize(opaqueSlot)}, i1 false)");
                    _dbg = here;

                    // A source variable: where it is and its type's metadata, kept for a debugger to read.
                    if (_scope is not null && alloc.VariableName is { } name && !name.StartsWith('$')) {
                        var scope = ScopeNode(alloc.Location?.Scope ?? 0);
                        var declaredAt = alloc.Location is { Scope: > 0 } place ? $", !dbg {Debug!.Location(place.Line, place.Column, scope)}" : EntryLocation;
                        DeclareDynamic($"{buffer}.debug", buffer, opaqueSlot, name, scope, Debug!.FileOf(function), alloc.Location?.Line ?? function.Line, 0, declaredAt);
                    }

                    return;
                }

                case AllocStackInst alloc: {
                    var name = Fresh();
                    _names[alloc] = name;

                    // With debug information, a slot of the entry block is
                    // made before anything else, with no line: the code that
                    // makes it is the prologue's, no statement to stop at. A
                    // breakpoint on its variable's line must find the
                    // statement, not this, and one on the function its first
                    // statement, the parameters' slots written by then.
                    var output = _out;
                    var savedDbg = _dbg;
                    if (_scope is not null && alloc.Block == function.Entry) {
                        _out = _entryAllocas;
                        _dbg = "";
                    }

                    // The storage itself is the frame's, made where the
                    // function starts: a slot of a block in a loop — a
                    // callee's, inlined there — is the same storage each
                    // time round, not a new one that grows the stack.
                    if (_out == _entryAllocas) {
                        Line($"{name} = alloca {PartType(alloc.ElementType)}{StorageAlign(alloc.ElementType)}");
                    } else {
                        _entryAllocas.Append($"  {name} = alloca {PartType(alloc.ElementType)}{StorageAlign(alloc.ElementType)}\n");
                    }

                    // Storage for something with references starts empty: all
                    // zeros. One that holds without owning is never destroyed,
                    // so what it held before it is written is never read.
                    if (!alloc.ElementType.IsTrivial && !alloc.Unmanaged) {
                        Line($"store {Type(alloc.ElementType)} zeroinitializer, ptr {name}");
                    }

                    // A source variable lives in this slot for the whole function,
                    // though a debugger shows it only in the block declaring it.
                    // One a closure captures lives in a box, which the slot
                    // points to: the variable is the value inside it.
                    // A unit variable has no storage for a debugger to show.
                    if (_scope is not null && alloc.VariableName is { } variable && alloc.ElementType != SilType.Unit) {
                        var line = alloc.Location?.Line ?? function.Line;
                        var scope = ScopeNode(alloc.Location?.Scope ?? 0);

                        // LLVM puts the variable in the scope of the
                        // declaration's location, not the variable's own.
                        if (_dbg.Length == 0) {
                            _dbg = EntryLocation;
                        }

                        if (alloc.Location is { Scope: > 0 } declared) {
                            _dbg = $", !dbg {Debug!.Location(declared.Line, declared.Column, scope)}";
                        }

                        if (BoxedType(alloc.ElementType) is { } boxed && !SilType.IsIndirect(boxed)) {
                            var node = Debug!.Variable(variable, scope, Debug.FileOf(function), line, boxed);
                            Line($"call void @llvm.dbg.declare(metadata ptr {name}, metadata {node}, metadata !DIExpression(DW_OP_deref, DW_OP_plus_uconst, {ObjectHeaderSize}))");
                        } else {
                            var node = Debug!.Variable(variable, scope, Debug.FileOf(function), line, alloc.ElementType);
                            Line($"call void @llvm.dbg.declare(metadata ptr {name}, metadata {node}, metadata !DIExpression())");
                        }
                    }

                    _out = output;
                    _dbg = savedDbg;
                    return;
                }

                case DeallocStackInst:
                    return;

                // A unit value takes no storage: nothing to read or write.
                case LoadInst unitLoad when unitLoad.Type == SilType.Unit:
                    _names[unitLoad] = "zeroinitializer";
                    return;

                case StoreInst unitStore when unitStore.Value.Type == SilType.Unit:
                    return;

                // An opaque value read: copied by its value witness into a buffer
                // of its own, or moved there, leaving the storage empty.
                case LoadInst { Type: var opaqueLoad } load when SilType.IsIndirect(opaqueLoad): {
                    var buffer = OpaqueBuffer(opaqueLoad);
                    _names[load] = buffer;
                    if (load.Kind == LoadKind.Take) {
                        OpaqueMove(opaqueLoad, buffer, Name(load.Address));
                        Line($"call void @llvm.memset.p0.i64(ptr {Name(load.Address)}, i8 0, i64 {OpaqueSize(opaqueLoad)}, i1 false)");
                    } else {
                        OpaqueWitness(opaqueLoad, CopyWitness, buffer, Name(load.Address));
                    }

                    return;
                }

                case StoreInst { Value.Type: var opaqueStore } store when SilType.IsIndirect(opaqueStore): {
                    if (store.Kind == StoreKind.Assign && store.Address is not AllocStackInst { Unmanaged: true }) {
                        OpaqueWitness(opaqueStore, AssignWithTakeWitness, Name(store.Address), Name(store.Value));
                    } else {
                        OpaqueMove(opaqueStore, Name(store.Address), Name(store.Value));
                    }

                    return;
                }

                case LoadInst load: {
                    var name = Fresh();
                    _names[load] = name;
                    Line($"{name} = load {Type(load.Type)}, ptr {Name(load.Address)}{AccessAlign(load.Address, load.Type)}{TbaaAt(load.Address, load.Type)}");
                    switch (load.Kind) {
                        case LoadKind.Copy:
                            Own(true, load.Type, name);
                            break;
                        case LoadKind.Take:
                            Line($"store {Type(load.Type)} zeroinitializer, ptr {Name(load.Address)}{AccessAlign(load.Address, load.Type)}{TbaaAt(load.Address, load.Type)}");
                            break;
                    }

                    return;
                }

                case StoreInst store: {
                    // Assigning destroys what was there, after the new value is in.
                    string? old = null;
                    if (store.Kind == StoreKind.Assign && !store.Value.Type.IsTrivial && store.Address is not AllocStackInst { Unmanaged: true }) {
                        old = Fresh();
                        Line($"{old} = load {Type(store.Value.Type)}, ptr {Name(store.Address)}{AccessAlign(store.Address, store.Value.Type)}{TbaaAt(store.Address, store.Value.Type)}");
                    }

                    Line($"store {Type(store.Value.Type)} {Name(store.Value)}, ptr {Name(store.Address)}{AccessAlign(store.Address, store.Value.Type)}{TbaaAt(store.Address, store.Value.Type)}");
                    if (old is not null) {
                        Own(false, store.Value.Type, old);
                    }

                    return;
                }

                // A weak reference read: the object, retained, or null — as an optional.
                case LoadWeakInst weak: {
                    var entry = Fresh();
                    var target = Fresh();
                    Line($"{entry} = load ptr, ptr {Name(weak.Address)}");
                    Line($"{target} = call ptr @sable_weak_load(ptr {entry})");
                    _names[weak] = WeakOptional((EnumType)weak.Type, target);
                    return;
                }

                case StoreWeakInst weak: {
                    var entry = Fresh();
                    var old = Fresh();
                    Line($"{entry} = call ptr @sable_weak_entry(ptr {WeakTarget((EnumType)weak.Value.Type, Name(weak.Value))})");
                    Line($"{old} = load ptr, ptr {Name(weak.Address)}");
                    Line($"store ptr {entry}, ptr {Name(weak.Address)}");
                    Line($"call void @sable_weak_release(ptr {old})");
                    return;
                }

                // A struct's weak field as a value is the entry it holds, retained.
                case WeakCopyValueInst weak: {
                    var name = Fresh();
                    _names[weak] = name;
                    Line($"{name} = call ptr @sable_weak_entry(ptr {WeakTarget((EnumType)weak.Value.Type, Name(weak.Value))})");
                    return;
                }

                case StrongCopyWeakValueInst weak: {
                    var target = Fresh();
                    Line($"{target} = call ptr @sable_weak_load(ptr {Name(weak.Value)})");
                    _names[weak] = WeakOptional((EnumType)weak.Type, target);
                    return;
                }

                // A struct's unowned field as a value is the object, its unowned count raised.
                case UnownedCopyValueInst unowned: {
                    Line($"call void @sable_unowned_retain(ptr {Name(unowned.Value)})");
                    _names[unowned] = Name(unowned.Value);
                    return;
                }

                case StrongCopyUnownedValueInst unowned: {
                    SiteForRuntime();
                    Line($"call void @sable_unowned_load(ptr {Name(unowned.Value)})");
                    _names[unowned] = Name(unowned.Value);
                    return;
                }

                case LoadUnownedInst unowned: {
                    var name = Fresh();
                    _names[unowned] = name;
                    Line($"{name} = load ptr, ptr {Name(unowned.Address)}");
                    SiteForRuntime();
                    Line($"call void @sable_unowned_load(ptr {name})");
                    return;
                }

                case StoreUnownedInst unowned: {
                    var old = Fresh();
                    Line($"call void @sable_unowned_retain(ptr {Name(unowned.Value)})");
                    Line($"{old} = load ptr, ptr {Name(unowned.Address)}");
                    Line($"store ptr {Name(unowned.Value)}, ptr {Name(unowned.Address)}");
                    Line($"call void @sable_unowned_release(ptr {old})");
                    return;
                }

                // Borrowed where it is: the storage is the value's buffer.
                case LoadBorrowInst opaqueBorrow when SilType.IsIndirect(opaqueBorrow.Type):
                    _names[opaqueBorrow] = Name(opaqueBorrow.Address);
                    return;

                case LoadBorrowInst borrow: {
                    var name = Fresh();
                    _names[borrow] = name;
                    Line($"{name} = load {Type(borrow.Type)}, ptr {Name(borrow.Address)}{AccessAlign(borrow.Address, borrow.Type)}{TbaaAt(borrow.Address, borrow.Type)}");
                    return;
                }

                case EndBorrowInst:
                    return;

                case CopyValueInst { Type: var opaqueCopy } copy when SilType.IsIndirect(opaqueCopy): {
                    var buffer = OpaqueBuffer(opaqueCopy);
                    _names[copy] = buffer;
                    OpaqueWitness(opaqueCopy, CopyWitness, buffer, Name(copy.Value));
                    return;
                }

                case DestroyValueInst { Value.Type: var opaqueDestroy } destroy when SilType.IsIndirect(opaqueDestroy):
                    OpaqueWitness(opaqueDestroy, DestroyWitness, Name(destroy.Value));
                    return;

                case DestroyAddrInst { Address.Type: AddressType { Pointee: var opaqueDestroyed } } destroy when SilType.IsIndirect(opaqueDestroyed) && destroy.Address is not AllocStackInst { Unmanaged: true }:
                    OpaqueWitness(opaqueDestroyed, DestroyWitness, Name(destroy.Address));
                    Line($"call void @llvm.memset.p0.i64(ptr {Name(destroy.Address)}, i8 0, i64 {OpaqueSize(opaqueDestroyed)}, i1 false)");
                    return;

                case CopyValueInst copy:
                    _names[copy] = Name(copy.Value);
                    Own(true, copy.Type, _names[copy]);
                    return;

                // The only reference to an object made in the frame: destroyed where it is, uncounted.
                case DestroyValueInst { Uncounted: true } destroy when Type(destroy.Value.Type) == "ptr":
                    Line($"call void @sable_destroy_uncounted(ptr {Name(destroy.Value)})");
                    return;

                case DestroyValueInst destroy:
                    Own(false, destroy.Value.Type, Name(destroy.Value));
                    return;

                // A slot that holds without owning gives nothing back.
                case DestroyAddrInst { Address: AllocStackInst { Unmanaged: true } }:
                    return;

                case DestroyAddrInst destroy: {
                    var type = ((AddressType)destroy.Address.Type).Pointee;
                    var old = Fresh();
                    Line($"{old} = load {Type(type)}, ptr {Name(destroy.Address)}{AccessAlign(destroy.Address, type)}{TbaaAt(destroy.Address, type)}");
                    Line($"store {Type(type)} zeroinitializer, ptr {Name(destroy.Address)}{AccessAlign(destroy.Address, type)}{TbaaAt(destroy.Address, type)}");
                    if (destroy.Uncounted && Type(type) == "ptr") {
                        Line($"call void @sable_destroy_uncounted(ptr {old})");
                    } else {
                        Own(false, type, old);
                    }

                    return;
                }

                case AllocRefInst alloc: {
                    var name = Fresh();
                    _names[alloc] = name;
                    var size = $"ptrtoint (ptr getelementptr ({ObjectType(alloc.ClassType)}, ptr null, i32 1) to i64)";

                    // An async function's frame: on its task's slab, never counted (AsyncLowering).
                    if (alloc.ClassType.IsCoroutineFrame) {
                        if (alloc.ClassType.TailSlots.Count == 0) {
                            Line($"{name} = call ptr @sable_frame_alloc(i64 {size})");
                            return;
                        }

                        // An unspecialised one's (#297): its buffers sized by the
                        // metadata it is given, after its fixed part, each aligned
                        // as the slab aligns frames and empty.
                        var frameClass = alloc.ClassType;
                        var offsets = new List<string>();
                        var total = Fresh();
                        Line($"{total} = add i64 {size}, 15");
                        var running = Fresh();
                        Line($"{running} = and i64 {total}, -16");
                        foreach (var slot in frameClass.TailSlots) {
                            offsets.Add(running);
                            var pointee = ((AddressType)frameClass.Fields[slot].Type).Pointee;
                            var grown = Fresh();
                            Line($"{grown} = add i64 {running}, {OpaqueSize(pointee)}");
                            var padded = Fresh();
                            Line($"{padded} = add i64 {grown}, 15");
                            running = Fresh();
                            Line($"{running} = and i64 {padded}, -16");
                        }

                        Line($"{name} = call ptr @sable_frame_alloc(i64 {running})");
                        var tail = Fresh();
                        Line($"{tail} = getelementptr inbounds i8, ptr {name}, i64 {offsets[0]}");
                        var tailSize = Fresh();
                        Line($"{tailSize} = sub i64 {running}, {offsets[0]}");
                        Line($"call void @llvm.memset.p0.i64(ptr align 16 {tail}, i8 0, i64 {tailSize}, i1 false)");
                        for (var i = 0; i < frameClass.TailSlots.Count; i++) {
                            var buffer = Fresh();
                            Line($"{buffer} = getelementptr inbounds i8, ptr {name}, i64 {offsets[i]}");
                            var field = Fresh();
                            Line($"{field} = getelementptr inbounds {ObjectType(frameClass)}, ptr {name}, i32 0, i32 {ObjectSlot(frameClass, frameClass.TailSlots[i])}");
                            Line($"store ptr {buffer}, ptr {field}");
                        }

                        return;
                    }

                    // A generic class of types only metadata knows (#297): its
                    // metadata made at run time, which says how large it is.
                    if (alloc.ClassType.IsGenericInstance && SilType.MentionsOpaque(alloc.ClassType)) {
                        var classMetadata = OpaqueMetadata(alloc.ClassType);
                        var objectField = Fresh();
                        Line($"{objectField} = getelementptr inbounds %SableTypeMetadata, ptr {classMetadata}, i32 0, i32 13");
                        var objectMetadata = Fresh();
                        Line($"{objectMetadata} = load ptr, ptr {objectField}");
                        var sizeField = Fresh();
                        Line($"{sizeField} = getelementptr inbounds %SableMetadata, ptr {objectMetadata}, i32 0, i32 5");
                        var objectSize = Fresh();
                        Line($"{objectSize} = load i64, ptr {sizeField}");
                        Line($"{name} = call ptr @sable_alloc(ptr {objectMetadata}, i64 {objectSize})");
                        return;
                    }

                    if (alloc.OnStack) {
                        // In the frame: the header made there, freed with it.
                        // Aligned as the heap's blocks are, which the layout assumes.
                        var memory = Scratch(ObjectType(alloc.ClassType), HeapAlign);
                        Line($"{name} = call ptr @sable_stack_object(ptr {memory}, ptr {module.ClassMetadata(alloc.ClassType)}, i64 {size})");
                        return;
                    }

                    // Its fields cleared here, where LLVM sees the stores that
                    // follow to them and drops what they make pointless.
                    Line($"{name} = call ptr @sable_alloc_uninitialised(ptr {module.ClassMetadata(alloc.ClassType)}, i64 {size})");
                    var fields = Fresh();
                    // Past the header, whose size is the target's.
                    Line($"{fields} = getelementptr inbounds %SableObjectHeader, ptr {name}, i32 1");
                    Line($"call void @llvm.memset.p0.i64(ptr align {TypeLayout.PointerBytes} {fields}, i8 0, i64 sub (i64 {size}, i64 ptrtoint (ptr getelementptr (%SableObjectHeader, ptr null, i32 1) to i64)), i1 false)");
                    return;
                }

                case RefElementAddrInst element: {
                    var name = Fresh();
                    _names[element] = name;
                    var classType = (ClassType)element.Reference.Type;

                    // Where its metadata, made at run time, says (#297).
                    if (classType.IsLaidOutAtRunTime) {
                        Line($"{name} = call ptr @sable_field_address(ptr {Name(element.Reference)}, i64 {element.Field}, ptr {OpaqueMetadata(classType)})");
                        return;
                    }

                    Line($"{name} = getelementptr inbounds {ObjectType(classType)}, ptr {Name(element.Reference)}, i32 0, i32 {ObjectSlot(classType, element.Field)}");
                    return;
                }

                case FunctionRefInst reference:
                    _names[reference] = "@" + Global(reference.Function);
                    return;

                case GlobalAddrInst global:
                    _names[global] = module.GlobalStorage(global.Global);
                    return;

                case PartialApplyInst partial: {
                    var code = Fresh();
                    var name = Fresh();
                    _names[partial] = name;
                    Line($"{code} = insertvalue {{ ptr, ptr }} poison, ptr {Name(partial.Function)}, 0");
                    Line($"{name} = insertvalue {{ ptr, ptr }} {code}, ptr {(partial.Context is { } context ? Name(context) : "null")}, 1");
                    return;
                }

                case UncheckedRefCastInst cast:
                    _names[cast] = Name(cast.Value);
                    return;

                case UpcastInst upcast:
                    _names[upcast] = Name(upcast.Value);
                    return;

                // The object's metadata is its first word; the method is in its slot there.
                case ClassMethodInst method: {
                    var owner = (ClassType)method.Reference.Type;
                    var metadata = Fresh();
                    var slot = Fresh();
                    var name = Fresh();
                    _names[method] = name;
                    Line($"{metadata} = load ptr, ptr {Name(method.Reference)}");
                    Line($"{slot} = getelementptr inbounds {MetadataType(owner)}, ptr {metadata}, i32 0, i32 {VTableField}, i64 {owner.MethodIndex(method.Method)}");
                    Line($"{name} = load ptr, ptr {slot}");
                    return;
                }

                case ApplyInst indirectApply when SilType.IsIndirect(indirectApply.Type):
                    throw new NotSupportedException("an opaque value is returned through an address its caller passes");
                case ReturnInst { Value: { } indirectReturned } when SilType.IsIndirect(indirectReturned.Type):
                    throw new NotSupportedException("an opaque value is returned through an address its caller passes");

                case ApplyInst apply: {
                    var (target, arguments) = Callee(apply.Callee, apply.Arguments);
                    var call = $"call {Type(apply.Type)} {target}({arguments})";
                    if (apply.Type == SilType.Unit) {
                        Line(call);
                    } else {
                        var name = Fresh();
                        _names[apply] = name;
                        Line($"{name} = {call}");
                    }

                    WriteBack();

                    return;
                }

                case BranchInst branch:
                    Line($"br label %{Edge(branch.Target, branch.Operands)}");
                    return;

                case CondBranchInst conditional: {
                    var whenTrue = Edge(conditional.WhenTrue, [..conditional.TrueArguments], conditional.TrueArgumentCount > 0);
                    var whenFalse = Edge(conditional.WhenFalse, [..conditional.FalseArguments], conditional.Operands.Count > 1 + conditional.TrueArgumentCount);
                    Line($"br i1 {Name(conditional.Condition)}, label %{whenTrue}, label %{whenFalse}");
                    return;
                }

                case ReturnInst returnInst when function.Type.Thrown is not null:
                    Line($"ret {ReturnType(function.Type)} {Discriminated(false, returnInst.Value)}");
                    return;

                case ThrowInst throwInst:
                    Line($"ret {ReturnType(function.Type)} {Discriminated(true, throwInst.Error)}");
                    return;

                case ReturnInst { Value: { } value }:
                    Line($"ret {Type(value.Type)} {Name(value)}");
                    return;

                case ReturnInst:
                    Line("ret void");
                    return;

                case StructInst indirectStruct when SilType.IsIndirect(indirectStruct.Type):
                    IndirectFields(indirectStruct, [..((StructType)indirectStruct.Type).Fields.Select(static f => f.Type)]);
                    return;

                case StructExtractInst indirectField when SilType.IsIndirect(indirectField.Value.Type):
                    _names[indirectField] = IndirectField(Name(indirectField.Value), indirectField.Value.Type, ((StructType)indirectField.Value.Type).Fields[indirectField.Field].Type, indirectField.Field, load: true);
                    return;

                case StructElementAddrInst indirectFieldAddress when SilType.IsIndirect(((AddressType)indirectFieldAddress.Address.Type).Pointee):
                    _names[indirectFieldAddress] = IndirectField(Name(indirectFieldAddress.Address), ((AddressType)indirectFieldAddress.Address.Type).Pointee,
                        ((StructType)((AddressType)indirectFieldAddress.Address.Type).Pointee).Fields[indirectFieldAddress.Field].Type, indirectFieldAddress.Field, load: false);
                    return;

                case StructInst { StructType.Deinit: not null } made: {
                    // The live byte, then the fields.
                    var type = Type(made.Type);
                    var current = Fresh();
                    Line($"{current} = insertvalue {type} {(LayoutOf(made.StructType).Explicit ? "zeroinitializer" : "poison")}, i1 true, 0");
                    for (var i = 0; i < made.Operands.Count; i++) {
                        var next = Fresh();
                        Line($"{next} = insertvalue {type} {current}, {PartType(made.Operands[i].Type)} {Name(made.Operands[i])}, {FieldSlot(made.StructType, i)}");
                        current = next;
                    }

                    _names[made] = current;
                    return;
                }

                // An aggregate laid out with padding of its own starts from
                // zeros, so that its padding is never poison where C reads it.
                case TupleInst indirectTuple when SilType.IsIndirect(indirectTuple.Type):
                    IndirectFields(indirectTuple, ((TupleType)indirectTuple.Type).Elements);
                    return;

                case TupleExtractInst indirectExtract when SilType.IsIndirect(indirectExtract.Value.Type):
                    _names[indirectExtract] = IndirectField(Name(indirectExtract.Value), indirectExtract.Value.Type, ((TupleType)indirectExtract.Value.Type).Elements[indirectExtract.Element], indirectExtract.Element, load: true);
                    return;

                case TupleElementAddrInst indirectElement when SilType.IsIndirect(((AddressType)indirectElement.Address.Type).Pointee):
                    _names[indirectElement] = IndirectField(Name(indirectElement.Address), ((AddressType)indirectElement.Address.Type).Pointee, ((TupleType)((AddressType)indirectElement.Address.Type).Pointee).Elements[indirectElement.Element], indirectElement.Element, load: false);
                    return;

                case StructInst or TupleInst: {
                    var layout = instruction.Type is StructType madeStruct ? LayoutOf(madeStruct) : LayoutOf((TupleType)instruction.Type);
                    _names[instruction] = Aggregate(Type(instruction.Type), instruction.Operands, layout.Explicit ? layout.Slots : null);
                    return;
                }

                // A unit part takes no storage: there is nothing to read.
                case StructExtractInst or TupleExtractInst when instruction.Type == SilType.Unit:
                    _names[instruction] = "zeroinitializer";
                    return;

                case StructExtractInst extract: {
                    var name = Fresh();
                    _names[extract] = name;
                    Line($"{name} = extractvalue {Type(extract.Value.Type)} {Name(extract.Value)}, {FieldSlot((StructType)extract.Value.Type, extract.Field)}");
                    return;
                }

                case TupleExtractInst extract: {
                    var name = Fresh();
                    _names[extract] = name;
                    Line($"{name} = extractvalue {Type(extract.Value.Type)} {Name(extract.Value)}, {LayoutOf((TupleType)extract.Value.Type).Slots[extract.Element]}");
                    return;
                }

                case StructElementAddrInst element: {
                    var name = Fresh();
                    _names[element] = name;
                    var structType = (StructType)((AddressType)element.Address.Type).Pointee;
                    Line($"{name} = getelementptr inbounds {Type(structType)}, ptr {Name(element.Address)}, i32 0, i32 {FieldSlot(structType, element.Field)}");
                    return;
                }

                case TupleElementAddrInst element: {
                    var name = Fresh();
                    _names[element] = name;
                    var tupleType = (TupleType)((AddressType)element.Address.Type).Pointee;
                    Line($"{name} = getelementptr inbounds {Type(tupleType)}, ptr {Name(element.Address)}, i32 0, i32 {LayoutOf(tupleType).Slots[element.Element]}");
                    return;
                }

                case EnumInst indirectEnum when SilType.IsIndirect(indirectEnum.Type):
                    IndirectEnum(indirectEnum);
                    return;

                case EnumInst nullableInst when IsNullable(nullableInst.EnumType):
                    _names[nullableInst] = nullableInst.Payload is { } held ? Name(held) : "null";
                    return;

                // A niche: the payload as the scalar, or the case's spare value.
                case EnumInst nicheInst when Niche(nicheInst.EnumType) is { } niche: {
                    if (nicheInst.Payload is not { } payload) {
                        _names[nicheInst] = niche.ValueOf(nicheInst.EnumType, nicheInst.Case).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    } else if (niche.Payload == SilType.Bool) {
                        var widened = Fresh();
                        _names[nicheInst] = widened;
                        Line($"{widened} = zext i1 {Name(payload)} to i8");
                    } else {
                        _names[nicheInst] = Name(payload);
                    }

                    return;
                }

                case EnumInst enumInst: {
                    var type = Type(enumInst.Type);
                    var slot = Scratch(type);
                    Line($"store {type} zeroinitializer, ptr {slot}");
                    Line($"store {TagType(enumInst.EnumType)} {enumInst.Case}, ptr {slot}");
                    // A payload of no bytes, an empty struct's, has no area to
                    // go in; one laid out as nothing (Unit — Never's, say, in
                    // a generic union's case) has no value to store.
                    if (enumInst.Payload is { } payload && Area(enumInst.EnumType).Count > 0 && payload.Type != SilType.Unit) {
                        var area = Fresh();
                        Line($"{area} = getelementptr inbounds {type}, ptr {slot}, i32 0, i32 1");
                        Line($"store {Type(payload.Type)} {Name(payload)}, ptr {area}");
                    }

                    var name = Fresh();
                    _names[enumInst] = name;
                    Line($"{name} = load {type}, ptr {slot}");
                    return;
                }

                case SwitchEnumInst switchEnum:
                    SwitchEnum(switchEnum);
                    return;

                case TryApplyInst tryApply:
                    TryApply(tryApply);
                    return;

                case UnreachableInst:
                    Line("unreachable");
                    return;

                case CondFailInst condFail:
                    TrapIf(Name(condFail.Condition), condFail.Message);
                    return;

                default:
                    throw new NotSupportedException($"no LLVM lowering for '{instruction.Opcode}'");
            }
        }

        /// <summary>
        ///     The label to branch to for a SIL edge, recording what it passes
        ///     to the target's arguments. With <paramref name="split" />, the
        ///     edge gets a block of its own, so that the two targets of one
        ///     <c>cond_br</c> are always different predecessors.
        /// </summary>
        string Edge(SilBlock target, IReadOnlyList<SilValue> arguments, bool split = false) {
            var from = _currentLabel;
            var label = _labels[target];

            // An opaque argument is moved into the target's buffer for it, on
            // the edge: through a buffer of each first when there are several,
            // which may be one another's.
            var moves = new StringBuilder();
            var opaque = Enumerable.Range(0, arguments.Count).Where(i => SilType.IsIndirect(target.Arguments[i].Type)).ToList();
            if (opaque.Count > 0) {
                var output = _out;
                _out = moves;
                var sources = opaque.Select(i => Name(arguments[i])).ToList();
                if (opaque.Count > 1) {
                    for (var j = 0; j < opaque.Count; j++) {
                        var staged = OpaqueBuffer(target.Arguments[opaque[j]].Type);
                        OpaqueMove(target.Arguments[opaque[j]].Type, staged, sources[j]);
                        sources[j] = staged;
                    }
                }

                for (var j = 0; j < opaque.Count; j++) {
                    if (sources[j] != Name(target.Arguments[opaque[j]])) {
                        OpaqueMove(target.Arguments[opaque[j]].Type, Name(target.Arguments[opaque[j]]), sources[j]);
                    }
                }

                _out = output;
            }

            if (split) {
                var edge = NewLabel("edge");
                _extra.Append(edge).Append(":\n").Append(moves).Append("  br label %").Append(label).Append('\n');
                from = edge;
            } else {
                _out.Append(moves);
            }

            for (var i = 0; i < arguments.Count; i++) {
                if (SilType.IsIndirect(target.Arguments[i].Type)) {
                    continue;
                }

                _incoming[target.Arguments[i]].Add((Name(arguments[i]), from));
            }

            return split ? from : label;
        }

        /// <summary>An aggregate built by inserting each part into the previous, from poison.</summary>
        string Aggregate(string type, IReadOnlyList<SilValue> parts, int[]? slots = null) {
            if (parts.Count == 0) {
                return "zeroinitializer";
            }

            var current = slots is null ? "poison" : "zeroinitializer";
            for (var i = 0; i < parts.Count; i++) {
                var next = Fresh();
                Line($"{next} = insertvalue {type} {current}, {PartType(parts[i].Type)} {Name(parts[i])}, {slots?[i] ?? i}");
                current = next;
            }

            return current;
        }

        /// <summary>A throwing function's return value: the flag, and the result or the error.</summary>
        string Discriminated(bool thrown, SilValue? value) {
            var type = ReturnType(function.Type);
            var withResult = function.Type.Result != SilType.Unit;
            var flag = Fresh();
            Line($"{flag} = insertvalue {type} poison, i1 {(thrown ? "true" : "false")}, 0");
            if (value is null) {
                return flag;
            }

            var index = thrown ? (withResult ? 2 : 1) : 1;
            var filled = Fresh();
            Line($"{filled} = insertvalue {type} {flag}, {Type(value.Type)} {Name(value)}, {index}");
            return filled;
        }

        // -------------------------------------------------------------------
        // Optionals, enums and tuples laid out at run time (#297): a value is
        // the address of a buffer its metadata sizes, and the runtime says
        // where a payload or an element is and what case a value is.
        // -------------------------------------------------------------------

        /// <summary>A part moved into its place: an indirect one's bits, any other stored; a unit one has none.</summary>
        void PlacePart(SilType type, string value, string at) {
            if (SilType.IsIndirect(type)) {
                OpaqueMove(type, at, value);
            } else if (type != SilType.Unit) {
                Line($"store {Type(type)} {value}, ptr {at}");
            }
        }

        /// <summary>A case made in a buffer of its own: emptied, its payload put where the metadata says, and the case written.</summary>
        void IndirectEnum(EnumInst made) {
            var metadata = OpaqueMetadata(made.Type);
            var buffer = OpaqueBuffer(made.Type);
            _names[made] = buffer;
            Line($"call void @llvm.memset.p0.i64(ptr align 16 {buffer}, i8 0, i64 {OpaqueSize(made.Type)}, i1 false)");
            if (made.Payload is { } payload) {
                var at = Fresh();
                Line($"{at} = call ptr @sable_enum_payload(ptr {buffer}, ptr {metadata})");
                PlacePart(payload.Type, Name(payload), at);
            }

            Line($"call void @sable_enum_inject(ptr {buffer}, i64 {made.Case}, ptr {metadata})");
        }

        /// <summary>
        ///     A switch on a value laid out at run time: its case, as the
        ///     runtime reads it, and a payload, on an edge of its own, moved
        ///     into the target's buffer or loaded.
        /// </summary>
        void IndirectSwitch(SwitchEnumInst switchEnum) {
            var enumType = (EnumType)switchEnum.Value.Type;
            var value = Name(switchEnum.Value);
            var metadata = OpaqueMetadata(enumType);
            var tag = Fresh();
            Line($"{tag} = call i64 @sable_enum_tag(ptr {value}, ptr {metadata})");

            string fallback;
            if (switchEnum.Default is { } defaultBlock) {
                fallback = _labels[defaultBlock];
            } else {
                fallback = NewLabel("nocase");
                _extra.Append(fallback).Append(":\n  unreachable\n");
            }

            var arms = new List<string>();
            foreach (var (caseIndex, target) in switchEnum.Cases) {
                if (enumType.Cases[caseIndex].Payload is not { } payload || target.Arguments.Count == 0) {
                    arms.Add($"i64 {caseIndex}, label %{_labels[target]}");
                    continue;
                }

                var edge = NewLabel("case");
                var output = _out;
                _out = new StringBuilder();
                var at = Fresh();
                Line($"{at} = call ptr @sable_enum_payload(ptr {value}, ptr {metadata})");
                if (SilType.IsIndirect(payload)) {
                    OpaqueMove(payload, Name(target.Arguments[0]), at);
                } else if (payload == SilType.Unit) {
                    _incoming[target.Arguments[0]].Add(("zeroinitializer", edge));
                } else {
                    var unpacked = Fresh();
                    Line($"{unpacked} = load {Type(payload)}, ptr {at}");
                    _incoming[target.Arguments[0]].Add((unpacked, edge));
                }

                Line($"br label %{_labels[target]}");
                _extra.Append(edge).Append(":\n").Append(_out);
                _out = output;
                arms.Add($"i64 {caseIndex}, label %{edge}");
            }

            Line($"switch i64 {tag}, label %{fallback} [ {string.Join(" ", arms)} ]");
        }

        /// <summary>
        ///     Shares the objects a value holds, as it crosses to another task
        ///     or into a shared object: one held indirectly (#297) through its
        ///     metadata's visit witness.
        /// </summary>
        void Share(SilType type, string value) {
            if (SilType.IsIndirect(type)) {
                Line($"call void @sable_opaque_share(ptr {value}, ptr {OpaqueMetadata(type)})");
                return;
            }

            module.Ownership(_out, "  ", Witnessing.Share, type, value, _dbg);
        }

        /// <summary>
        ///     Goes to <paramref name="yes" /> where <paramref name="also" />
        ///     holds and the object's count is one, so that what it holds may
        ///     be changed in place, else to <paramref name="no" />, the
        ///     runtime's. An object several threads reach is looked at by an
        ///     acquire, so that what the threads that held it read of it was
        ///     read before it is changed: their releases are the other half. An
        ///     acquire, even on a path hardly taken, keeps LLVM from holding
        ///     the buffer and its count in registers round a loop: without
        ///     <paramref name="shared" />, as for an append, the runtime makes it.
        ///     The count and the flags are two loads: one of both would wait for
        ///     a retain's store of the count alone.
        /// </summary>
        void BranchIfSole(string also, string strongField, string yes, string no, bool shared) {
            var count = Fresh();
            var one = Fresh();
            var flagsField = Fresh();
            var flags = Fresh();
            var bit = Fresh();
            var alone = Fresh();
            var sole = Fresh();
            var both = Fresh();
            Line($"{count} = load atomic i32, ptr {strongField} unordered, align 4, !tbaa {TbaaCount}");
            Line($"{one} = icmp eq i32 {count}, 1");
            Line($"{flagsField} = getelementptr inbounds i8, ptr {strongField}, i64 4");
            Line($"{flags} = load atomic i32, ptr {flagsField} unordered, align 4");
            Line($"{bit} = and i32 {flags}, 512");
            Line($"{alone} = icmp eq i32 {bit}, 0");
            Line($"{sole} = and i1 {one}, {alone}");
            Line($"{both} = and i1 {also}, {sole}");
            if (!shared) {
                Line($"br i1 {both}, label %{yes}, label %{no}");
                return;
            }

            var look = NewLabel("look");
            var again = Fresh();
            var last = Fresh();
            var still = Fresh();
            Line($"br i1 {both}, label %{yes}, label %{look}, !prof !{{!\"branch_weights\", i32 1000, i32 1}}");
            Continue(look);
            Line($"{again} = load atomic i32, ptr {strongField} acquire, align 4");
            Line($"{last} = icmp eq i32 {again}, 1");
            Line($"{still} = and i1 {also}, {last}");
            Line($"br i1 {still}, label %{yes}, label %{no}");
        }

        /// <summary>The type of what one of <see cref="ShareStores" />' builtins shares: the value, or what is at the address.</summary>
        static SilType Shareable(BuiltinInst share) =>
            share.Operands[^1].Type is AddressType at && share.Name.EndsWith("_at", StringComparison.Ordinal) ? at.Pointee : share.Operands[^1].Type;

        /// <summary>What it shares: the value, or the storage's, read — or, of a type known only by its metadata, where it is.</summary>
        string ShareValue(BuiltinInst share, string operand) {
            var type = Shareable(share);
            if (!share.Name.EndsWith("_at", StringComparison.Ordinal) || SilType.IsIndirect(type)) {
                return operand;
            }

            var held = Fresh();
            Line($"{held} = load {Type(type)}, ptr {operand}{AccessAlign(share.Operands[^1], type)}");
            return held;
        }

        /// <summary>
        ///     Shares a value where the object that holds it, or will, is
        ///     reachable from more than one thread already: what a shared
        ///     object reaches is shared (runtime/tasks.c's sable_share), and its
        ///     walk stops at one that is. Null owns nothing.
        /// </summary>
        void ShareInto(string owner, Action share, bool mayBeNull) {
            var sharing = NewLabel("share");
            var after = NewLabel("stored");
            if (mayBeNull) {
                var isNull = Fresh();
                var look = NewLabel("owner");
                Line($"{isNull} = icmp eq ptr {owner}, null");
                Line($"br i1 {isNull}, label %{after}, label %{look}");
                Continue(look);
            }

            var at = Fresh();
            var flags = Fresh();
            var bit = Fresh();
            var shared = Fresh();
            Line($"{at} = getelementptr inbounds i8, ptr {owner}, i64 12");
            Line($"{flags} = load atomic i32, ptr {at} monotonic, align 4");
            Line($"{bit} = and i32 {flags}, 513");
            Line($"{shared} = icmp ne i32 {bit}, 0");
            Line($"br i1 {shared}, label %{sharing}, label %{after}, !prof !{{!\"branch_weights\", i32 1, i32 1000}}");
            Continue(sharing);
            share();
            Line($"br label %{after}");
            Continue(after);
        }

        /// <summary>As <see cref="ShareInto"/>, into the buffer of the array, or the slice, in a slot.</summary>
        void ShareIntoArray(string slot, SilType element, string value) {
            if (element.IsTrivial) {
                return;
            }

            var buffer = Fresh();
            Line($"{buffer} = load ptr, ptr {slot}");
            ShareInto(buffer, () => Share(element, value), mayBeNull: true);
        }

        /// <summary>A struct or a tuple made in a buffer of its own, each field moved or stored where the metadata says.</summary>
        void IndirectFields(SilInstruction made, IReadOnlyList<SilType> parts) {
            var metadata = OpaqueMetadata(made.Type);
            var buffer = OpaqueBuffer(made.Type);
            _names[made] = buffer;
            for (var i = 0; i < parts.Count; i++) {
                var at = Fresh();
                Line($"{at} = call ptr @sable_field_address(ptr {buffer}, i64 {i}, ptr {metadata})");
                PlacePart(parts[i], Name(made.Operands[i]), at);
            }
        }

        /// <summary>
        ///     A field of a struct or a tuple laid out at run time: its address;
        ///     read, an indirect one borrowed where it is and any other loaded.
        /// </summary>
        string IndirectField(string value, SilType type, SilType part, int index, bool load) {
            var metadata = OpaqueMetadata(type);
            var address = Fresh();
            Line($"{address} = call ptr @sable_field_address(ptr {value}, i64 {index}, ptr {metadata})");
            if (!load || SilType.IsIndirect(part)) {
                return address;
            }

            if (part == SilType.Unit) {
                return "zeroinitializer";
            }

            var loaded = Fresh();
            Line($"{loaded} = load {Type(part)}, ptr {address}");
            return loaded;
        }

        void SwitchEnum(SwitchEnumInst switchEnum) {
            var enumType = (EnumType)switchEnum.Value.Type;
            if (SilType.IsIndirect(enumType)) {
                IndirectSwitch(switchEnum);
                return;
            }

            var type = Type(enumType);
            var value = Name(switchEnum.Value);

            // A nullable reference: null or not, the reference its own payload.
            if (IsNullable(enumType)) {
                var some = SomeCase(enumType);
                string Target(int caseIndex, bool payload) {
                    foreach (var (listed, block) in switchEnum.Cases) {
                        if (listed != caseIndex) {
                            continue;
                        }

                        if (!payload) {
                            return _labels[block];
                        }

                        var edge = NewLabel("case");
                        _extra.Append(edge).Append(":\n").Append($"  br label %{_labels[block]}\n");
                        _incoming[block.Arguments[0]].Add((value, edge));
                        return edge;
                    }

                    if (switchEnum.Default is { } otherwise) {
                        return _labels[otherwise];
                    }

                    var unreachable = NewLabel("nocase");
                    _extra.Append(unreachable).Append(":\n  unreachable\n");
                    return unreachable;
                }

                var isNull = Fresh();
                Line($"{isNull} = icmp eq ptr {value}, null");
                Line($"br i1 {isNull}, label %{Target(1 - some, false)}, label %{Target(some, true)}");
                return;
            }

            if (Niche(enumType) is { } niche) {
                SwitchNiche(switchEnum, niche);
                return;
            }

            var tagType = TagType(enumType);
            var tag = Fresh();
            Line($"{tag} = extractvalue {type} {value}, 0");

            string fallback;
            if (switchEnum.Default is { } defaultBlock) {
                fallback = _labels[defaultBlock];
            } else {
                // Every case is listed, so this cannot be taken.
                fallback = NewLabel("nocase");
                _extra.Append(fallback).Append(":\n  unreachable\n");
            }

            var arms = new List<string>();
            foreach (var (caseIndex, target) in switchEnum.Cases) {
                if (enumType.Cases[caseIndex].Payload is not { } payload) {
                    arms.Add($"{tagType} {caseIndex}, label %{_labels[target]}");
                    continue;
                }

                // A payload of no bytes is all of its one value; one laid
                // out as nothing (Unit — Never's, say) has no value to pass.
                if (payload == SilType.Unit) {
                    arms.Add($"{tagType} {caseIndex}, label %{_labels[target]}");
                    continue;
                }

                if (Area(enumType).Count == 0) {
                    var empty = NewLabel("case");
                    _extra.Append(empty).Append(":\n").Append($"  br label %{_labels[target]}\n");
                    _incoming[target.Arguments[0]].Add(("zeroinitializer", empty));
                    arms.Add($"{tagType} {caseIndex}, label %{empty}");
                    continue;
                }

                // An edge of its own, which unpacks the payload for the target.
                var edge = NewLabel("case");
                var slot = Scratch(type);
                var area = Fresh();
                var unpacked = Fresh();
                _extra.Append(edge).Append(":\n")
                    .Append($"  store {type} {value}, ptr {slot}\n")
                    .Append($"  {area} = getelementptr inbounds {type}, ptr {slot}, i32 0, i32 1\n")
                    .Append($"  {unpacked} = load {Type(payload)}, ptr {area}\n")
                    .Append($"  br label %{_labels[target]}\n");
                _incoming[target.Arguments[0]].Add((unpacked, edge));
                arms.Add($"{tagType} {caseIndex}, label %{edge}");
            }

            Line($"switch {tagType} {tag}, label %{fallback} [ {string.Join(" ", arms)} ]");
        }

        /// <summary>
        ///     A switch on an enum in a scalar's niche: a value the payload can
        ///     take is the payload's case, unpacked on an edge of its own; each
        ///     spare value is its case.
        /// </summary>
        void SwitchNiche(SwitchEnumInst switchEnum, TypeLayout.NicheLayout niche) {
            var enumType = (EnumType)switchEnum.Value.Type;
            var value = Name(switchEnum.Value);

            string fallback;
            if (switchEnum.Default is { } defaultBlock) {
                fallback = _labels[defaultBlock];
            } else {
                fallback = NewLabel("nocase");
                _extra.Append(fallback).Append(":\n  unreachable\n");
            }

            var payloadTarget = fallback;
            var arms = new List<string>();
            foreach (var (caseIndex, target) in switchEnum.Cases) {
                if (caseIndex != niche.Some) {
                    arms.Add($"{NicheType(niche)} {niche.ValueOf(enumType, caseIndex)}, label %{_labels[target]}");
                    continue;
                }

                var edge = NewLabel("case");
                var unpacked = Fresh();
                _extra.Append(edge).Append(":\n")
                    .Append(niche.Payload == SilType.Bool ? $"  {unpacked} = trunc i8 {value} to i1\n" : $"  {unpacked} = add i32 {value}, 0\n")
                    .Append($"  br label %{_labels[target]}\n");
                _incoming[target.Arguments[0]].Add((unpacked, edge));
                payloadTarget = edge;
            }

            var spare = NewLabel("spare");
            var isPayload = Fresh();
            Line($"{isPayload} = icmp ult {NicheType(niche)} {value}, {niche.Spare}");
            Line($"br i1 {isPayload}, label %{payloadTarget}, label %{spare}");
            _extra.Append(spare).Append(":\n").Append($"  switch {NicheType(niche)} {value}, label %{fallback} [ {string.Join(" ", arms)} ]\n");
        }

        /// <summary>
        ///     What a call calls, and with what: a function as it is; a closure
        ///     by its code, with its context passed after the arguments.
        /// </summary>
        /// <summary>
        ///     A parameter's or an argument's type: Unit — a generic
        ///     function's T where T is Void, or Never — is passed as the empty
        ///     struct, since LLVM has no parameters of type void.
        /// </summary>
        string ArgumentType(SilType type) => type == SilType.Unit ? "{}" : Type(type);

        (string Target, string Arguments) Callee(SilValue callee, IEnumerable<SilValue> arguments) {
            var passed = arguments.Select(a => $"{ArgumentType(a.Type)} {(a.Type == SilType.Unit ? "zeroinitializer" : Aligned(a))}").ToList();
            if (callee.Type is not ThickFunctionType) {
                return (Name(callee), string.Join(", ", passed));
            }

            var code = Fresh();
            var context = Fresh();
            Line($"{code} = extractvalue {{ ptr, ptr }} {Name(callee)}, 0");
            Line($"{context} = extractvalue {{ ptr, ptr }} {Name(callee)}, 1");
            passed.Add($"ptr {context}");
            return (code, string.Join(", ", passed));
        }

        /// <summary>
        ///     An address passed to a function, as the function may use it:
        ///     aligned as its type is. A field of a packed struct may not be,
        ///     so it is copied to a slot that is, and back after the call.
        /// </summary>
        string Aligned(SilValue argument) {
            if (argument.Type is not AddressType { Pointee: var pointee } || SilType.IsIndirect(pointee) || AddressAlign(argument) >= Align(pointee)) {
                return Name(argument);
            }

            var access = AccessAlign(argument, pointee);
            var slot = Fresh();
            var value = Fresh();
            _entryAllocas.Append($"  {slot} = alloca {Type(pointee)}, align {Align(pointee)}\n");
            Line($"{value} = load {Type(pointee)}, ptr {Name(argument)}{access}");
            Line($"store {Type(pointee)} {value}, ptr {slot}, align {Align(pointee)}");
            _writeBacks.Add($"{Type(pointee)}|{slot}|{Name(argument)}|{access}|{Align(pointee)}");
            return slot;
        }

        /// <summary>Copies what a call left in the aligned slots back to the fields they stood for.</summary>
        void WriteBack() {
            foreach (var entry in _writeBacks) {
                var parts = entry.Split('|');
                var value = Fresh();
                Line($"{value} = load {parts[0]}, ptr {parts[1]}, align {parts[4]}");
                Line($"store {parts[0]} {value}, ptr {parts[2]}{parts[3]}");
            }

            _writeBacks.Clear();
        }

        readonly List<string> _writeBacks = [];

        void TryApply(TryApplyInst tryApply) {
            var callee = tryApply.Callee.Type as FunctionType ?? ((ThickFunctionType)tryApply.Callee.Type).Signature;
            var type = ReturnType(callee);
            var (target, arguments) = Callee(tryApply.Callee, tryApply.Arguments);

            var result = Fresh();
            var flag = Fresh();
            Line($"{result} = call {type} {target}({arguments})");
            WriteBack();
            Line($"{flag} = extractvalue {type} {result}, 0");

            var withResult = callee.Result != SilType.Unit;

            var normal = NewLabel("normal");
            _extra.Append(normal).Append(":\n");
            if (withResult) {
                var value = Fresh();
                _extra.Append($"  {value} = extractvalue {type} {result}, 1\n");
                _incoming[tryApply.Normal.Arguments[0]].Add((value, normal));
            }

            _extra.Append($"  br label %{_labels[tryApply.Normal]}\n");

            var failed = NewLabel("error");
            var error = Fresh();
            _extra.Append(failed).Append(":\n")
                .Append($"  {error} = extractvalue {type} {result}, {(withResult ? 2 : 1)}\n")
                .Append($"  br label %{_labels[tryApply.Error]}\n");
            _incoming[tryApply.Error.Arguments[0]].Add((error, failed));

            Line($"br i1 {flag}, label %{failed}, label %{normal}");
        }

        void Builtin(BuiltinInst builtin) {
            var operands = builtin.Operands.Select(Name).ToArray();
            string Result() {
                var name = Fresh();
                _names[builtin] = name;
                return name;
            }

            if (builtin.Name.StartsWith("atomic_", StringComparison.Ordinal)) {
                Atomic(builtin, operands, Result);
                return;
            }

            if (FloatFunctions.Find(builtin.Name) is var (elementary, floatType)) {
                Line($"{Result()} = {MathCall(elementary, floatType, operands, module._windows)}");
                return;
            }

            // Bytes read as an integer: the last byte's index checked as an
            // element's would be, and a negative offset refused, so that all
            // of them are in the slice; then one load, aligned or not.
            if (builtin.Name.StartsWith("bytes_load_", StringComparison.Ordinal)) {
                var integer = Type(builtin.Type);
                var width = SilType.IntegerInfo(builtin.Type)!.Value.Bits / 8;
                var first = builtin.Operands[0].Type is ArrayType whole ? InlineElement(operands[0], operands[1], whole.Element, elements: width) : SliceElement(operands[0], operands[1], (SliceType)builtin.Operands[0].Type, elements: width);
                Line($"{Result()} = load {integer}, ptr {first}, align 1, !tbaa {TbaaScalar}");
                return;
            }

            // An integer written as bytes: the slice made the only reference to
            // its run and the last byte's index checked, as writing that byte
            // would, then one store.
            if (builtin.Name.StartsWith("bytes_store_", StringComparison.Ordinal)) {
                var integer = Type(builtin.Operands[2].Type);
                var width = SilType.IntegerInfo(builtin.Operands[2].Type)!.Value.Bits / 8;
                if (((AddressType)builtin.Operands[0].Type).Pointee is ArrayType bytes) {
                    if (!builtin.Name.StartsWith("bytes_store_array_unique_", StringComparison.Ordinal)) {
                        MakeUnique(operands[0], bytes);
                    }

                    var array = Fresh();
                    Line($"{array} = load ptr, ptr {operands[0]}");
                    var start = InlineElement(array, operands[1], bytes.Element, elements: width);
                    Line($"store {integer} {operands[2]}, ptr {start}, align 1, !tbaa {TbaaScalar}");
                    return;
                }

                var written = (SliceType)((AddressType)builtin.Operands[0].Type).Pointee;
                string first;
                if (builtin.Name.StartsWith("bytes_store_unique_", StringComparison.Ordinal)) {
                    // The only reference to its buffer already (an array's
                    // place viewed in place): the index checked, and no call.
                    var slice = Fresh();
                    Line($"{slice} = load {Type(written)}, ptr {operands[0]}");
                    first = SliceElement(slice, operands[1], written, elements: width);
                } else {
                    NegativeOffsetTraps(operands[1]);
                    var (lastIndex, from) = (Fresh(), Fresh());
                    Line($"{lastIndex} = add i64 {operands[1]}, {width - 1}");
                    var last = SliceMutableElement(operands[0], lastIndex, written);
                    Line($"{from} = getelementptr inbounds i8, ptr {last}, i64 {1 - width}");
                    first = from;
                }

                Line($"store {integer} {operands[2]}, ptr {first}, align 1, !tbaa {TbaaScalar}");
                return;
            }

            if (builtin.Name.StartsWith("bitcast_", StringComparison.Ordinal)) {
                Line($"{Result()} = bitcast {Type(builtin.Operands[0].Type)} {operands[0]} to {Type(builtin.Type)}");
                return;
            }

            if (SimdBuiltins.Is(builtin.Name)) {
                Simd(builtin, operands, Result);
                return;
            }

            if (NumericIntrinsic(builtin, operands, Result)) {
                return;
            }

            if (Numeric(builtin, operands, Result)) {
                return;
            }

            if (builtin.Name.StartsWith("inline_", StringComparison.Ordinal)) {
                Inline(builtin, operands, Result);
                return;
            }

            switch (builtin.Name) {
                // A task: its root frame's start, resume and drop functions,
                // what the start is given, its priority, and whether it is detached.
                case "task_spawn": {
                    var handle = Fresh();
                    Line($"{handle} = call ptr @sable_task_spawn(ptr {operands[0]}, ptr {operands[1]}, ptr {operands[2]}, ptr {operands[3]}, i64 {operands[4]}, i1 zeroext {operands[5]})");
                    Line($"{Result()} = ptrtoint ptr {handle} to i64");
                    return;
                }

                case "task_priority":
                    Line($"{Result()} = call i64 @sable_task_priority()");
                    return;

                case "task_timed_begin":
                    Line("call void @sable_task_timed_begin()");
                    return;

                case "task_timed_end":
                    Line("call void @sable_task_timed_end()");
                    return;

                case "task_timed_woken":
                    Line($"{Result()} = call zeroext i1 @sable_task_timed_woken()");
                    return;

                case "task_spawn_cancelled":
                    Line("call void @sable_task_spawn_cancelled()");
                    return;

                case "task_handler_push":
                    Line($"call void @sable_task_handler_push(ptr {operands[0]}, ptr {operands[1]})");
                    return;

                case "task_handler_pop":
                    Line("call void @sable_task_handler_pop()");
                    return;

                // Groups, by handle: their children, in the order they finish.
                case "task_group_new": {
                    var group = Fresh();
                    Line($"{group} = call ptr @sable_group_new()");
                    Line($"{Result()} = ptrtoint ptr {group} to i64");
                    return;
                }

                case "task_group_add": {
                    var group = Fresh();
                    var task = Fresh();
                    Line($"{group} = inttoptr i64 {operands[0]} to ptr");
                    Line($"{task} = inttoptr i64 {operands[1]} to ptr");
                    Line($"call void @sable_group_add(ptr {group}, ptr {task})");
                    return;
                }

                case "task_group_take": {
                    var group = Fresh();
                    var task = Fresh();
                    Line($"{group} = inttoptr i64 {operands[0]} to ptr");
                    Line($"{task} = call ptr @sable_group_take(ptr {group})");
                    Line($"{Result()} = ptrtoint ptr {task} to i64");
                    return;
                }

                case "task_group_free": {
                    var group = Fresh();
                    Line($"{group} = inttoptr i64 {operands[0]} to ptr");
                    Line($"call void @sable_group_free(ptr {group})");
                    return;
                }

                case "async_group_wait": {
                    var group = Fresh();
                    Line($"{group} = inttoptr i64 {operands[0]} to ptr");
                    Line($"{Result()} = call zeroext i1 @sable_group_wait(ptr {group})");
                    return;
                }

                case "task_release" or "task_cancel": {
                    var handle = Fresh();
                    Line($"{handle} = inttoptr i64 {operands[0]} to ptr");
                    Line($"call void @sable_{builtin.Name}(ptr {handle})");
                    return;
                }

                // The places a task waits (AsyncLowering): each says whether it must suspend.
                case "async_wait": {
                    var handle = Fresh();
                    Line($"{handle} = inttoptr i64 {operands[0]} to ptr");
                    Line($"{Result()} = call zeroext i1 @sable_task_wait(ptr {handle})");
                    return;
                }

                case "async_yield":
                    Line($"{Result()} = call zeroext i1 @sable_task_yield()");
                    return;

                case "async_sleep":
                    Line($"{Result()} = call zeroext i1 @sable_task_sleep(i64 {operands[0]})");
                    return;

                case "async_frame_free":
                    Line($"call void @sable_frame_free(ptr {operands[0]})");
                    return;

                case "task_is_cancelled":
                    Line($"{Result()} = call zeroext i1 @sable_task_is_cancelled()");
                    return;

                case "task_drain":
                    Line("call void @sable_tasks_drain()");
                    return;

                case "object_unmade":
                    Line($"call void @sable_object_unmade(ptr {operands[0]})");
                    return;

                case "task_run_until_main":
                    Line("call void @sable_task_run_until_main()");
                    return;

                // A value crossing from one task to another: its objects shared.
                case "share":
                    Share(builtin.Operands[0].Type, operands[0]);
                    return;

                // A value about to be stored into an object, or what storage
                // inside it holds now: shared too, if the object is — its
                // header's flag word, at 12, has SABLE_SHARED (512), or
                // SABLE_IMMORTAL (1), which any thread reaches. The object
                // itself, or the array's buffer or the box in a slot.
                case "share_into" or "share_into_at": {
                    var owner = operands[0];
                    var inSlot = builtin.Operands[0].Type is AddressType;
                    if (inSlot) {
                        owner = Fresh();
                        Line($"{owner} = load ptr, ptr {operands[0]}");
                    }

                    if (builtin.Operands is [_, _, var described]) {
                        ShareInto(owner, () => Line($"call void @sable_opaque_share(ptr {operands[1]}, ptr {Name(described)})"), mayBeNull: true);
                        return;
                    }

                    ShareInto(owner, () => Share(Shareable(builtin), ShareValue(builtin, operands[1])), mayBeNull: inSlot);
                    return;
                }

                // Into a global, which every thread reaches: if there are any
                // but the first (threads.c), which shares them as it starts one.
                case "share_global" or "share_global_at": {
                    var threads = Fresh();
                    var several = Fresh();
                    var share = NewLabel("share");
                    var after = NewLabel("shared");
                    Line($"{threads} = load atomic i8, ptr @sable_threaded monotonic, align 1");
                    Line($"{several} = icmp ne i8 {threads}, 0");
                    Line($"br i1 {several}, label %{share}, label %{after}");
                    Continue(share);
                    Share(Shareable(builtin), ShareValue(builtin, operands[0]));
                    Line($"br label %{after}");
                    Continue(after);
                    return;
                }

                // Raw memory (RawPtr, NativeMemory): addresses as Ints. The
                // runtime allocates at any alignment; bytes are moved and
                // filled by LLVM's own memmove and memset.
                case "raw_alloc": {
                    var room = Fresh();
                    Line($"{room} = call ptr @sable_raw_alloc(i64 {operands[0]}, i64 {operands[1]})");
                    Line($"{Result()} = ptrtoint ptr {room} to i64");
                    return;
                }

                case "raw_free":
                    Line($"call void @sable_raw_free(ptr {RawAddress(operands[0])})");
                    return;

                case "raw_copy":
                    Line($"call void @llvm.memmove.p0.p0.i64(ptr {RawAddress(operands[0])}, ptr {RawAddress(operands[1])}, i64 {operands[2]}, i1 false)");
                    return;

                case "raw_fill":
                    Line($"call void @llvm.memset.p0.i64(ptr {RawAddress(operands[0])}, i8 {operands[1]}, i64 {operands[2]}, i1 false)");
                    return;

                // A value at an address: copied out (retained), moved out (as
                // it is), or moved in. An opaque one through its buffer, by its
                // value witnesses. Aligned as its type is, or to a byte.
                case "raw_load" or "raw_load_unaligned" or "raw_take": {
                    var at = RawAddress(operands[0]);
                    if (SilType.IsIndirect(builtin.Type)) {
                        var buffer = OpaqueBuffer(builtin.Type);
                        _names[builtin] = buffer;
                        if (builtin.Name == "raw_take") {
                            OpaqueMove(builtin.Type, buffer, at);
                        } else {
                            OpaqueWitness(builtin.Type, CopyWitness, buffer, at);
                        }

                        return;
                    }

                    var align = builtin.Name == "raw_load_unaligned" ? 1 : Math.Max(TypeLayout.Align(builtin.Type), 1);
                    var value = Result();
                    Line($"{value} = load {Type(builtin.Type)}, ptr {at}, align {align}");
                    if (builtin.Name != "raw_take") {
                        Own(true, builtin.Type, value);
                    }

                    return;
                }

                case "raw_init" or "raw_init_unaligned": {
                    var at = RawAddress(operands[0]);
                    var type = builtin.Operands[1].Type;
                    // Whose the memory is, and which threads reach it, no one knows: shared.
                    if (!type.IsTrivial) {
                        Share(type, operands[1]);
                    }

                    if (SilType.IsIndirect(type)) {
                        OpaqueMove(type, at, operands[1]);
                        return;
                    }

                    var align = builtin.Name == "raw_init_unaligned" ? 1 : Math.Max(TypeLayout.Align(type), 1);
                    Line($"store {Type(type)} {operands[1]}, ptr {at}, align {align}");
                    return;
                }

                // Actors: where the running task is, by identity (an actor
                // object's address, 1 for the main actor, 0 for anywhere), and
                // a hop to one — a place the task may suspend.
                case "address_to_int":
                    Line($"{Result()} = ptrtoint ptr {operands[0]} to i64");
                    return;

                case "task_actor_current": {
                    var actor = Fresh();
                    Line($"{actor} = call ptr @sable_actor_current()");
                    Line($"{Result()} = ptrtoint ptr {actor} to i64");
                    return;
                }

                case "async_hop": {
                    var actor = Fresh();
                    Line($"{actor} = inttoptr i64 {operands[0]} to ptr");
                    Line($"{Result()} = call zeroext i1 @sable_actor_hop(ptr {actor})");
                    return;
                }

                // Locks and conditions, by handle (runtime/locks.c).
                case "lock_new" or "rwlock_new" or "condition_new": {
                    var made = Fresh();
                    Line($"{made} = call ptr @sable_{builtin.Name}()");
                    Line($"{Result()} = ptrtoint ptr {made} to i64");
                    return;
                }

                case "lock_free" or "lock_acquire" or "lock_acquire_later" or "lock_release" or "condition_free" or "condition_signal" or "condition_broadcast"
                    or "rwlock_free" or "rwlock_write" or "rwlock_write_done":
                    Line($"call void @sable_{builtin.Name}(ptr {RawAddress(operands[0])})");
                    return;

                case "lock_try_acquire":
                    Line($"{Result()} = call zeroext i1 @sable_lock_try_acquire(ptr {RawAddress(operands[0])})");
                    return;

                case "rwlock_read": {
                    var counted = Fresh();
                    Line($"{counted} = call ptr @sable_rwlock_read(ptr {RawAddress(operands[0])})");
                    Line($"{Result()} = ptrtoint ptr {counted} to i64");
                    return;
                }

                case "rwlock_read_done":
                    Line($"call void @sable_rwlock_read_done(ptr {RawAddress(operands[0])}, ptr {RawAddress(operands[1])})");
                    return;

                case "condition_wait":
                    Line($"call void @sable_condition_wait(ptr {RawAddress(operands[0])}, ptr {RawAddress(operands[1])}, i64 {operands[2]})");
                    return;

                // Wait lists, by handle: what a channel's receivers park in.
                case "task_waitlist_new": {
                    var list = Fresh();
                    Line($"{list} = call ptr @sable_waitlist_new()");
                    Line($"{Result()} = ptrtoint ptr {list} to i64");
                    return;
                }

                case "task_waitlist_free" or "task_waitlist_wake" or "task_waitlist_wake_one" or "task_waitlist_grant": {
                    var list = Fresh();
                    Line($"{list} = inttoptr i64 {operands[0]} to ptr");
                    Line($"call void @sable_{builtin.Name[5..]}(ptr {list})");
                    return;
                }

                // A socket to be ready (the reactor), and work on the blocking pool.
                case "async_io_wait":
                    Line($"{Result()} = call zeroext i1 @sable_task_io_wait(i64 {operands[0]}, i1 zeroext {operands[1]})");
                    return;

                case "async_blocking":
                    Line($"{Result()} = call zeroext i1 @sable_task_blocking(ptr {operands[0]}, ptr {operands[1]})");
                    return;

                // Sockets (runtime/net.c), by handle: each builtin the runtime's
                // function of its name, a slice its buffer, start and count,
                // and an array given the metadata to make it with.
                case var net when net.StartsWith("net_", StringComparison.Ordinal): {
                    List<string> passed = [];
                    for (var i = 0; i < operands.Length; i++) {
                        var type = builtin.Operands[i].Type;
                        if (type is SliceType slice) {
                            passed.Add($"ptr {SlicePart(operands[i], slice, 0)}");
                            passed.Add($"i64 {SlicePart(operands[i], slice, 1)}");
                            passed.Add($"i64 {SlicePart(operands[i], slice, 2)}");
                        } else {
                            passed.Add((type == SilType.Int ? "i64 " : type == SilType.Bool ? "i1 zeroext " : "ptr ") + operands[i]);
                        }
                    }

                    if (builtin.Type is ArrayType made) {
                        passed.Add($"ptr {module.ArrayMetadata(made)}");
                    }

                    var call = $"@sable_{net}({string.Join(", ", passed)})";
                    Line(
                        builtin.Type == SilType.Unit ? $"call void {call}"
                        : builtin.Type == SilType.Int ? $"{Result()} = call i64 {call}"
                        : builtin.Type == SilType.Bool ? $"{Result()} = call zeroext i1 {call}"
                        : $"{Result()} = call ptr {call}"
                    );
                    return;
                }

                case "async_park": {
                    var list = Fresh();
                    Line($"{list} = inttoptr i64 {operands[0]} to ptr");
                    Line($"{Result()} = call zeroext i1 @sable_task_park(ptr {list})");
                    return;
                }

                case "async_park_cancellable": {
                    var list = Fresh();
                    Line($"{list} = inttoptr i64 {operands[0]} to ptr");
                    Line($"{Result()} = call zeroext i1 @sable_task_park_cancellable(ptr {list}, i64 {operands[1]})");
                    return;
                }

                case "task_waitlist_ticket": {
                    var list = Fresh();
                    Line($"{list} = inttoptr i64 {operands[0]} to ptr");
                    Line($"{Result()} = call i64 @sable_waitlist_ticket(ptr {list})");
                    return;
                }

                case Coverage.Builtin:
                    Line($"call void @sable_coverage_hit(i64 {operands[0]})");
                    return;
            }

            // Asked of the registry: each protocol's descriptor, looked up
            // in the records of the object's class and its superclasses.
            if (MetadataBuiltin(builtin, operands)) {
                return;
            }

            if (builtin.Name.StartsWith("class_conforms:", StringComparison.Ordinal)) {
                string? all = null;
                foreach (var protocol in builtin.Name["class_conforms:".Length..].Split('&')) {
                    var conforms = Fresh();
                    Line($"{conforms} = call zeroext i1 @sable_conforms(ptr {operands[0]}, ptr {module.ProtocolDescriptor(protocol)})");
                    if (all is null) {
                        all = conforms;
                    } else {
                        var both = Fresh();
                        Line($"{both} = and i1 {all}, {conforms}");
                        all = both;
                    }
                }

                _names[builtin] = all!;
                return;
            }

            if (builtin.Name.StartsWith("class_is:", StringComparison.Ordinal)) {
                var target = (ClassType)function.Module!.FindType(builtin.Name["class_is:".Length..])!;
                var metadata = Fresh();
                Line($"{metadata} = load ptr, ptr {operands[0]}");

                // A class nothing in the whole program inherits from is its
                // object's class exactly, or not: one comparison, which a
                // speculatively devirtualised call is guarded by.
                if (module.IsLeaf(target)) {
                    Line($"{Result()} = icmp eq ptr {metadata}, {module.ClassMetadata(target)}");
                } else {
                    Line($"{Result()} = call zeroext i1 @sable_class_is(ptr {metadata}, ptr {module.ClassMetadata(target)})");
                }

                return;
            }

            // Exclusivity markers: kept in checked builds, gone in the others.
            if (builtin.Name.StartsWith("access_", StringComparison.Ordinal)) {
                if (module._checkExclusivity && builtin.Name.StartsWith("access_check_", StringComparison.Ordinal)) {
                    CheckAccess(builtin.Operands[0], builtin.Name == "access_check_modify");
                } else if (module._checkExclusivity) {
                    var call = builtin.Name == "access_end" ? "sable_access_end" : "sable_access_begin";
                    var kind = builtin.Name == "access_end" ? "" : $", i64 {StorageSize(builtin.Operands[0])}, i32 {(builtin.Name == "access_begin_modify" ? 1 : 0)}";
                    if (builtin.Name != "access_end") {
                        SiteForRuntime();
                    }

                    Line($"call void @{call}(ptr {operands[0]}{kind})");
                }

                return;
            }

            switch (builtin.Name) {
                case "add_Int" or "sub_Int" or "mul_Int": {
                    var intrinsic = builtin.Name[..3] switch { "add" => "sadd", "sub" => "ssub", _ => "smul" };
                    Overflowing(intrinsic, operands[0], operands[1], Result());
                    return;
                }

                case "neg_Int":
                    Overflowing("ssub", "0", operands[0], Result());
                    return;

                case "wrapping_add_Int" or "wrapping_sub_Int" or "wrapping_mul_Int":
                    Line($"{Result()} = {builtin.Name[9..12]} i64 {operands[0]}, {operands[1]}");
                    return;

                // The iteration, when this one overflowed and it is the first:
                // a select and a min, which vectorise. The overflow is worked
                // out in plain arithmetic, as the vectoriser takes no
                // with.overflow intrinsic: a sum overflowed when it has the
                // sign of neither operand, a difference when the operands'
                // signs differ and it has the second's; a product is made in
                // 128 bits and must fit back in 64.
                case "overflow_at_add_Int" or "overflow_at_sub_Int" or "overflow_at_mul_Int": {
                    var overflowed = Fresh();
                    var at = Fresh();
                    if (builtin.Name[12..15] == "mul") {
                        var (left, right, wide, narrow, back) = (Fresh(), Fresh(), Fresh(), Fresh(), Fresh());
                        Line($"{left} = sext i64 {operands[0]} to i128");
                        Line($"{right} = sext i64 {operands[1]} to i128");
                        Line($"{wide} = mul i128 {left}, {right}");
                        Line($"{narrow} = trunc i128 {wide} to i64");
                        Line($"{back} = sext i64 {narrow} to i128");
                        Line($"{overflowed} = icmp ne i128 {back}, {wide}");
                    } else {
                        var add = builtin.Name[12..15] == "add";
                        var (result, first, second, both) = (Fresh(), Fresh(), Fresh(), Fresh());
                        Line($"{result} = {(add ? "add" : "sub")} i64 {operands[0]}, {operands[1]}");
                        Line($"{first} = xor i64 {operands[0]}, {result}");
                        Line($"{second} = xor i64 {(add ? operands[1] : operands[0])}, {(add ? result : operands[1])}");
                        Line($"{both} = and i64 {first}, {second}");
                        Line($"{overflowed} = icmp slt i64 {both}, 0");
                    }

                    Line($"{at} = select i1 {overflowed}, i64 {operands[2]}, i64 9223372036854775807");
                    Line($"{Result()} = call i64 @llvm.smin.i64(i64 {operands[3]}, i64 {at})");
                    return;
                }

                case "div_Int" or "rem_Int": {
                    var zero = Fresh();
                    Line($"{zero} = icmp eq i64 {operands[1]}, 0");
                    TrapIf(zero, "division by zero");

                    // Int.min / -1 does not fit.
                    var isMin = Fresh();
                    var isMinusOne = Fresh();
                    var both = Fresh();
                    Line($"{isMin} = icmp eq i64 {operands[0]}, -9223372036854775808");
                    Line($"{isMinusOne} = icmp eq i64 {operands[1]}, -1");
                    Line($"{both} = and i1 {isMin}, {isMinusOne}");
                    TrapIf(both, "integer overflow");

                    var op = builtin.Name.StartsWith("div", StringComparison.Ordinal) ? "sdiv" : "srem";
                    Line($"{Result()} = {op} i64 {operands[0]}, {operands[1]}");
                    return;
                }

                case "shl_Int" or "shr_Int": {
                    var outside = Fresh();
                    Line($"{outside} = icmp uge i64 {operands[1]}, 64");
                    TrapIf(outside, "shift out of range");
                    var op = builtin.Name.StartsWith("shl", StringComparison.Ordinal) ? "shl" : "ashr";
                    Line($"{Result()} = {op} i64 {operands[0]}, {operands[1]}");
                    return;
                }

                // Which is never made a branch of: said to be one no guess would serve.
                case "select_Int":
                    Line($"{Result()} = select i1 {operands[0]}, i64 {operands[1]}, i64 {operands[2]}, !unpredictable {Unpredictable}");
                    return;

                case "and_Int" or "or_Int" or "xor_Int":
                    Line($"{Result()} = {builtin.Name[..builtin.Name.IndexOf('_')]} i64 {operands[0]}, {operands[1]}");
                    return;

                case "not_Int":
                    Line($"{Result()} = xor i64 {operands[0]}, -1");
                    return;

                case "and_Bool" or "or_Bool" or "xor_Bool":
                    Line($"{Result()} = {builtin.Name[..builtin.Name.IndexOf('_')]} i1 {operands[0]}, {operands[1]}");
                    return;

                case "not_Bool":
                    Line($"{Result()} = xor i1 {operands[0]}, true");
                    return;

                case "add_F64" or "sub_F64" or "mul_F64" or "div_F64" or "rem_F64":
                    Line($"{Result()} = f{builtin.Name[..3]} double {operands[0]}, {operands[1]}");
                    return;

                case "neg_F64":
                    Line($"{Result()} = fneg double {operands[0]}");
                    return;

                case "print_String":
                    Line($"call void @sable_print_string(ptr {operands[0]})");
                    return;

                case "cmp_eq_String" or "cmp_ne_String": {
                    // The same object, or lengths that differ, decide it without a call.
                    var equal = builtin.Name.StartsWith("cmp_eq", StringComparison.Ordinal) ? Result() : Fresh();
                    var same = Fresh();
                    var leftLength = Fresh();
                    var rightLength = Fresh();
                    var leftField = Fresh();
                    var rightField = Fresh();
                    var lengths = Fresh();
                    var start = _currentLabel;
                    var measure = NewLabel("lengths");
                    var bytes = NewLabel("bytes");
                    var decided = NewLabel("compared");
                    Line($"{same} = icmp eq ptr {operands[0]}, {operands[1]}");
                    Line($"br i1 {same}, label %{decided}, label %{measure}");
                    Continue(measure);
                    Line($"{leftField} = getelementptr inbounds %SableStringHeader, ptr {operands[0]}, i32 0, i32 1");
                    Line($"{rightField} = getelementptr inbounds %SableStringHeader, ptr {operands[1]}, i32 0, i32 1");
                    Line($"{leftLength} = load i64, ptr {leftField}");
                    Line($"{rightLength} = load i64, ptr {rightField}");
                    Line($"{lengths} = icmp eq i64 {leftLength}, {rightLength}");
                    Line($"br i1 {lengths}, label %{bytes}, label %{decided}");
                    Continue(bytes);
                    var called = Fresh();
                    Line($"{called} = call zeroext i1 @sable_string_equal(ptr {operands[0]}, ptr {operands[1]})");
                    Line($"br label %{decided}");
                    Continue(decided);
                    Line($"{equal} = phi i1 [ true, %{start} ], [ false, %{measure} ], [ {called}, %{bytes} ]");
                    if (builtin.Name.StartsWith("cmp_ne", StringComparison.Ordinal)) {
                        Line($"{Result()} = xor i1 {equal}, true");
                    }

                    return;
                }

                case "string_concat":
                    Line($"{Result()} = call ptr @sable_string_concat(ptr {operands[0]}, ptr {operands[1]})");
                    return;

                // Each object an '@immortal' global holds.
                case "make_immortal":
                    module.Ownership(_out, "  ", Witnessing.Immortal, builtin.Operands[0].Type, operands[0], _dbg);
                    return;

                case "ref_identical":
                    Line($"{Result()} = icmp eq ptr {operands[0]}, {operands[1]}");
                    return;

                // An empty assembly statement given the slot, that may read
                // and write any memory: LLVM must store the value before and
                // load it after, knowing nothing of either.
                case SilBuiltins.BlackBox:
                    Line($"call void asm sideeffect \"\", \"r,~{{memory}}\"(ptr {operands[0]})");
                    return;

                // The only reference, and not immortal: free to change in place.
                case "ref_is_unique": {
                    var objectPointer = Fresh();
                    var strongField = Fresh();
                    var strong = Fresh();
                    var flagsField = Fresh();
                    var flags = Fresh();
                    var immortal = Fresh();
                    var sole = Fresh();
                    var mortal = Fresh();
                    Line($"{objectPointer} = load ptr, ptr {operands[0]}");
                    Line($"{strongField} = getelementptr inbounds %SableObjectHeader, ptr {objectPointer}, i32 0, i32 1");
                    Line($"{strong} = load atomic i32, ptr {strongField} acquire, align 4");
                    Line($"{flagsField} = getelementptr inbounds %SableObjectHeader, ptr {objectPointer}, i32 0, i32 2");
                    Line($"{flags} = load atomic i32, ptr {flagsField} monotonic, align 4");
                    Line($"{immortal} = and i32 {flags}, 1");
                    Line($"{mortal} = icmp eq i32 {immortal}, 0");
                    Line($"{sole} = icmp eq i32 {strong}, 1");
                    Line($"{Result()} = and i1 {sole}, {mortal}");
                    return;
                }

                // The pieces in an array in the frame, joined by one call.
                case "string_concat_many": {
                    var pieces = Scratch($"[{operands.Length} x ptr]");
                    for (var i = 0; i < operands.Length; i++) {
                        var at = Fresh();
                        Line($"{at} = getelementptr inbounds [{operands.Length} x ptr], ptr {pieces}, i64 0, i64 {i}");
                        Line($"store ptr {operands[i]}, ptr {at}");
                    }

                    Line($"{Result()} = call ptr @sable_string_concat_many(i64 {operands.Length}, ptr {pieces})");
                    return;
                }

                case "string_count":
                    Line($"{Result()} = call i64 @sable_string_count(ptr {operands[0]})");
                    return;

                case "string_join":
                    Line($"{Result()} = call ptr @sable_string_join(ptr {operands[0]}, ptr {operands[1]})");
                    return;

                case "string_hash":
                    Line($"{Result()} = call i64 @sable_string_hash(ptr {operands[0]}, i64 {operands[1]})");
                    return;

                // The process's seed, set as the runtime starts: a load, not a
                // call, since every Hasher made reads it.
                case "hash_seed":
                    Line($"{Result()} = load i64, ptr @sable_hash_seed");
                    return;

                // Bits, or bytes, from the operating system's random source:
                // the runtime traps rather than give weak ones.
                case "random_bits":
                    Line($"{Result()} = call i64 @sable_random_bits()");
                    return;

                case "raw_random" or "raw_secure_zero": {
                    var function = builtin.Name == "raw_random" ? "random_fill" : "secure_zero";
                    Line($"call void @sable_{function}(ptr {RawAddress(operands[0])}, i64 {operands[1]})");
                    return;
                }

                // The same over a slice of bytes, made the only reference to
                // its run first, as a write of its first byte would.
                case "bytes_random" or "bytes_secure_zero": {
                    var type = (SliceType)((AddressType)builtin.Operands[0].Type).Pointee;
                    var function = builtin.Name == "bytes_random" ? "random_fill" : "secure_zero";
                    var slice = Fresh();
                    var empty = Fresh();
                    var any = NewLabel("bytes");
                    var after = NewLabel("after");
                    Line($"{slice} = load {Type(type)}, ptr {operands[0]}");
                    var count = SlicePart(slice, type, 2);
                    Line($"{empty} = icmp eq i64 {count}, 0");
                    Line($"br i1 {empty}, label %{after}, label %{any}");
                    Continue(any);
                    var first = SliceMutableElement(operands[0], "0", type);
                    Line($"call void @sable_{function}(ptr {first}, i64 {count})");
                    Line($"br label %{after}");
                    Continue(after);
                    return;
                }

                // Whole blocks of a hash worked into the state at an address,
                // by the processor's instructions where it has them.
                case "crypto_blocks": {
                    var type = (SliceType)builtin.Operands[2].Type;
                    var buffer = SlicePart(operands[2], type, 0);
                    var start = SlicePart(operands[2], type, 1);
                    var count = SlicePart(operands[2], type, 2);
                    Line($"{Result()} = call zeroext i1 @sable_crypto_blocks(i64 {operands[0]}, ptr {RawAddress(operands[1])}, ptr {buffer}, i64 {start}, i64 {count})");
                    return;
                }

                // Blocks of Base64 or Hex, from a slice of bytes or a range of a
                // string's, to a slice from an index on, made the only reference
                // to its run, or to a string being written: how many bytes of
                // the source were done.
                case "codec_bytes_bytes" or "codec_string_bytes": {
                    var fromString = builtin.Name == "codec_string_bytes";
                    var index = operands[^1];
                    var type = (SliceType)((AddressType)builtin.Operands[0].Type).Pointee;
                    var first = SliceMutableElement(operands[0], index, type);
                    var slice = Fresh();
                    var room = Fresh();
                    Line($"{slice} = load {Type(type)}, ptr {operands[0]}");
                    var count = SlicePart(slice, type, 2);
                    Line($"{room} = sub i64 {count}, {index}");
                    if (fromString) {
                        Line($"{Result()} = call i64 @sable_codec_string(i64 {operands[1]}, ptr {operands[2]}, i64 {operands[3]}, i64 {operands[4]}, ptr {first}, i64 {room})");
                    } else {
                        var sourceType = (SliceType)builtin.Operands[2].Type;
                        var buffer = SlicePart(operands[2], sourceType, 0);
                        var start = SlicePart(operands[2], sourceType, 1);
                        var length = SlicePart(operands[2], sourceType, 2);
                        Line($"{Result()} = call i64 @sable_codec_bytes(i64 {operands[1]}, ptr {buffer}, i64 {start}, i64 {length}, ptr {first}, i64 {room})");
                    }

                    return;
                }

                // UTF-8 as UTF-16 or the other way, from a slice to another from
                // an index on, made the only reference to its run.
                case "utf8_to_utf16" or "utf16_to_utf8": {
                    var type = (SliceType)((AddressType)builtin.Operands[0].Type).Pointee;
                    var first = SliceMutableElement(operands[0], operands[2], type);
                    var slice = Fresh();
                    var room = Fresh();
                    Line($"{slice} = load {Type(type)}, ptr {operands[0]}");
                    var count = SlicePart(slice, type, 2);
                    Line($"{room} = sub i64 {count}, {operands[2]}");
                    var sourceType = (SliceType)builtin.Operands[1].Type;
                    var buffer = SlicePart(operands[1], sourceType, 0);
                    var start = SlicePart(operands[1], sourceType, 1);
                    var length = SlicePart(operands[1], sourceType, 2);
                    Line($"{Result()} = call i64 @sable_{builtin.Name}(ptr {buffer}, i64 {start}, i64 {length}, ptr {first}, i64 {room})");
                    return;
                }

                case "codec_bytes_raw": {
                    var sourceType = (SliceType)builtin.Operands[1].Type;
                    var buffer = SlicePart(operands[1], sourceType, 0);
                    var start = SlicePart(operands[1], sourceType, 1);
                    var length = SlicePart(operands[1], sourceType, 2);
                    Line($"{Result()} = call i64 @sable_codec_bytes(i64 {operands[0]}, ptr {buffer}, i64 {start}, i64 {length}, ptr {RawAddress(operands[2])}, i64 {operands[3]})");
                    return;
                }

                case "crypto_blocks_raw":
                    Line($"{Result()} = call zeroext i1 @sable_crypto_blocks_raw(i64 {operands[0]}, ptr {RawAddress(operands[1])}, ptr {RawAddress(operands[2])}, i64 {operands[3]})");
                    return;

                // Only an x86-64 processor has kernels to be asked for: elsewhere
                // the calls are not there.
                case "vector_available":
                    Line(module._target.Triple.StartsWith("x86_64", StringComparison.Ordinal)
                        ? $"{Result()} = call zeroext i1 @sable_vector_available()"
                        : $"{Result()} = and i1 false, false");
                    return;

                case "vector_reduce_F32" or "vector_reduce_F64": {
                    var type = (SliceType)builtin.Operands[1].Type;
                    var parts = VectorOperands(operands[1], operands[2], type);
                    Line($"{Result()} = call {Type(type.Element)} @sable_{builtin.Name}(i64 {operands[0]}, {parts})");
                    return;
                }

                // To a slice made the only reference to its run first, as a
                // write of its first element would.
                case "vector_map_F32" or "vector_map_F64": {
                    var type = (SliceType)builtin.Operands[2].Type;
                    var empty = Fresh();
                    var any = NewLabel("elements");
                    var after = NewLabel("after");
                    Line($"{empty} = icmp eq i64 {SlicePart(operands[2], type, 2)}, 0");
                    Line($"br i1 {empty}, label %{after}, label %{any}");
                    Continue(any);
                    var first = SliceMutableElement(operands[0], "0", type);
                    Line($"call void @sable_{builtin.Name}(ptr {first}, i64 {operands[1]}, {VectorOperands(operands[2], operands[3], type)})");
                    Line($"br label %{after}");
                    Continue(after);
                    return;
                }

                case "string_find":
                    Line($"{Result()} = call i64 @sable_string_find(ptr {operands[0]}, ptr {operands[1]}, i64 {operands[2]})");
                    return;

                case "string_compare":
                    Line($"{Result()} = call i64 @sable_string_compare(ptr {operands[0]}, ptr {operands[1]})");
                    return;

                case "string_hash_range":
                    Line($"{Result()} = call i64 @sable_string_hash_range(ptr {operands[0]}, i64 {operands[1]}, i64 {operands[2]}, i64 {operands[3]})");
                    return;

                case "string_count_range":
                    Line($"{Result()} = call i64 @sable_string_count_range(ptr {operands[0]}, i64 {operands[1]}, i64 {operands[2]})");
                    return;

                case "string_compare_range" or "string_utf8_find_range" or "string_utf8_find_last_range":
                    Line($"{Result()} = call i64 @sable_{builtin.Name}(ptr {operands[0]}, i64 {operands[1]}, i64 {operands[2]}, ptr {operands[3]}, i64 {operands[4]}, i64 {operands[5]})");
                    return;

                case "string_utf8_find_any":
                    Line($"{Result()} = call i64 @sable_string_utf8_find_any(ptr {operands[0]}, i64 {operands[1]}, i64 {operands[2]}, ptr {operands[3]})");
                    return;

                case "string_at":
                    SiteForRuntime();
                    Line($"{Result()} = call i32 @sable_string_at(ptr {operands[0]}, i64 {operands[1]})");
                    return;

                case "string_slice":
                    SiteForRuntime();
                    Line($"{Result()} = call ptr @sable_string_slice(ptr {operands[0]}, i64 {operands[1]}, i64 {operands[2]})");
                    return;

                case "string_contains":
                    Line($"{Result()} = call zeroext i1 @sable_string_contains(ptr {operands[0]}, i32 {operands[1]})");
                    return;

                case "string_index_of":
                    Line($"{Result()} = call i64 @sable_string_index_of(ptr {operands[0]}, i32 {operands[1]})");
                    return;

                case "string_cursor_end":
                    Line($"{Result()} = call i64 @sable_string_cursor_end(ptr {operands[0]})");
                    return;

                case "string_cursor_scalar" or "string_cursor_next":
                    AsciiOrCall(builtin, operands, Result, inRange: true);
                    return;

                case "string_cursor_previous":
                    Line($"{Result()} = call i64 @sable_string_cursor_previous(ptr {operands[0]}, i64 {operands[1]})");
                    return;

                // The UTF-8 length is the header's: no call.
                case "string_utf8_count": {
                    var field = Fresh();
                    Line($"{field} = getelementptr inbounds %SableStringHeader, ptr {operands[0]}, i32 0, i32 1");
                    Line($"{Result()} = load i64, ptr {field}, !tbaa {TbaaHeader}");
                    return;
                }

                // A byte of the text, read in place after the length is
                // checked, as an array's element is: what the Unicode tables,
                // kept as string literals, are read with, many times a lookup.
                case "string_utf8_at": {
                    var field = Fresh();
                    var length = Fresh();
                    var outside = Fresh();
                    var bytes = Fresh();
                    var at = Fresh();
                    Line($"{field} = getelementptr inbounds %SableStringHeader, ptr {operands[0]}, i32 0, i32 1");
                    Line($"{length} = load i64, ptr {field}, !tbaa {TbaaHeader}");
                    Line($"{outside} = icmp uge i64 {operands[1]}, {length}");
                    TrapIf(outside, "string index out of range");
                    Line($"{bytes} = getelementptr inbounds %SableStringHeader, ptr {operands[0]}, i64 1");
                    Line($"{at} = getelementptr inbounds i8, ptr {bytes}, i64 {operands[1]}");
                    Line($"{Result()} = load i8, ptr {at}");
                    return;
                }

                case "string_index_scalar" or "string_index_after":
                    AsciiOrCall(builtin, operands, Result, inRange: false);
                    return;

                // Eight bytes of the text as one number, the first the lowest.
                case "string_utf8_load_U64": {
                    var field = Fresh();
                    var length = Fresh();
                    var negative = Fresh();
                    var end = Fresh();
                    var past = Fresh();
                    var outside = Fresh();
                    var bytes = Fresh();
                    var at = Fresh();
                    Line($"{field} = getelementptr inbounds %SableStringHeader, ptr {operands[0]}, i32 0, i32 1");
                    Line($"{length} = load i64, ptr {field}, !tbaa {TbaaHeader}");
                    Line($"{end} = add i64 {operands[1]}, 8");
                    Line($"{negative} = icmp slt i64 {operands[1]}, 0");
                    Line($"{past} = icmp ugt i64 {end}, {length}");
                    Line($"{outside} = or i1 {negative}, {past}");
                    TrapIf(outside, "string index out of range");
                    Line($"{bytes} = getelementptr inbounds %SableStringHeader, ptr {operands[0]}, i64 1");
                    Line($"{at} = getelementptr inbounds i8, ptr {bytes}, i64 {operands[1]}");
                    Line($"{Result()} = load i64, ptr {at}, align 1");
                    return;
                }

                // A string written in place, by the address of its bytes.
                case "string_buffer_make" or "string_buffer_grow": {
                    var made = Fresh();
                    var arguments = builtin.Name == "string_buffer_make" ? $"i64 {operands[0]}" : $"ptr {RawAddress(operands[0])}, i64 {operands[1]}, i64 {operands[2]}";
                    Line($"{made} = call ptr @sable_{builtin.Name}({arguments})");
                    Line($"{Result()} = ptrtoint ptr {made} to i64");
                    return;
                }

                case "string_buffer_copy": {
                    var bytes = Fresh();
                    var from = Fresh();
                    Line($"{bytes} = getelementptr inbounds %SableStringHeader, ptr {operands[1]}, i64 1");
                    Line($"{from} = getelementptr inbounds i8, ptr {bytes}, i64 {operands[2]}");
                    Line($"call void @llvm.memmove.p0.p0.i64(ptr {RawAddress(operands[0])}, ptr {from}, i64 {operands[3]}, i1 false)");
                    return;
                }

                case "string_buffer_valid":
                    Line($"{Result()} = call zeroext i1 @sable_string_buffer_valid(ptr {RawAddress(operands[0])}, i64 {operands[1]})");
                    return;

                case "string_buffer_take":
                    Line($"{Result()} = call ptr @sable_string_buffer_take(ptr {RawAddress(operands[0])}, i64 {operands[1]}, i64 {operands[2]})");
                    return;

                // The first bytes of five words as a string, in one call that
                // makes nothing else.
                case "string_from_ascii_words":
                    Line($"{Result()} = call ptr @sable_string_from_ascii_words(i64 {operands[0]}, i64 {operands[1]}, i64 {operands[2]}, i64 {operands[3]}, i64 {operands[4]}, i64 {operands[5]})");
                    return;

                case "string_index_before":
                    SiteForRuntime();
                    Line($"{Result()} = call i64 @sable_{builtin.Name}(ptr {operands[0]}, i64 {operands[1]})");
                    return;

                case "string_utf8_slice":
                    SiteForRuntime();
                    Line($"{Result()} = call ptr @sable_string_utf8_slice(ptr {operands[0]}, i64 {operands[1]}, i64 {operands[2]})");
                    return;

                case "string_utf8_find" or "string_utf8_find_last":
                    Line($"{Result()} = call i64 @sable_{builtin.Name}(ptr {operands[0]}, ptr {operands[1]}, i64 {operands[2]})");
                    return;

                // A byte searched for in a slice of bytes, or counted.
                case "bytes_find" or "bytes_count": {
                    var type = (SliceType)builtin.Operands[0].Type;
                    var buffer = SlicePart(operands[0], type, 0);
                    var start = SlicePart(operands[0], type, 1);
                    var count = SlicePart(operands[0], type, 2);
                    Line($"{Result()} = call i64 @sable_{builtin.Name}(ptr {buffer}, i64 {start}, i64 {count}, i8 {operands[1]})");
                    return;
                }

                case "bytes_find_any": {
                    var type = (SliceType)builtin.Operands[0].Type;
                    var buffer = SlicePart(operands[0], type, 0);
                    var start = SlicePart(operands[0], type, 1);
                    var count = SlicePart(operands[0], type, 2);
                    var setType = (SliceType)builtin.Operands[1].Type;
                    var setBuffer = SlicePart(operands[1], setType, 0);
                    var setStart = SlicePart(operands[1], setType, 1);
                    var setCount = SlicePart(operands[1], setType, 2);
                    Line($"{Result()} = call i64 @sable_bytes_find_any(ptr {buffer}, i64 {start}, i64 {count}, ptr {setBuffer}, i64 {setStart}, i64 {setCount})");
                    return;
                }

                // A slice of bytes, to the runtime as its buffer, start and count.
                case "utf8_validate" or "string_from_utf8_unchecked": {
                    var type = (SliceType)builtin.Operands[0].Type;
                    var buffer = SlicePart(operands[0], type, 0);
                    var start = SlicePart(operands[0], type, 1);
                    var count = SlicePart(operands[0], type, 2);
                    var result = builtin.Name == "utf8_validate" ? "i64 @sable_utf8_validate" : "ptr @sable_string_from_utf8_unchecked";
                    Line($"{Result()} = call {result}(ptr {buffer}, i64 {start}, i64 {count})");
                    return;
                }

                case "io_write" or "io_write_error":
                    Line($"call void @sable_{builtin.Name}(ptr {operands[0]})");
                    return;

                case "trap":
                    Line($"call void @sable_trap_with(ptr {operands[0]}, ptr {operands[1]})");
                    return;

                case "backtrace_capture":
                    Line($"{Result()} = call ptr @sable_backtrace_capture()");
                    return;

                case "trap_uncaught":
                    Line($"call void @sable_trap_uncaught(ptr {operands[0]}, ptr {operands[1]})");
                    return;

                // A closure to the runtime as its code and context, two
                // pointers, as C takes them on every target; and back the
                // same way, through a slot, rather than as a struct C returns.
                case "trap_hook_set": {
                    var code = Fresh();
                    var context = Fresh();
                    Line($"{code} = extractvalue {{ ptr, ptr }} {operands[0]}, 0");
                    Line($"{context} = extractvalue {{ ptr, ptr }} {operands[0]}, 1");
                    Line($"call void @sable_trap_hook_set(ptr {code}, ptr {context})");
                    return;
                }

                case "trap_hook_take": {
                    var code = Fresh();
                    var context = Fresh();
                    var slot = Fresh();
                    _entryAllocas.Append($"  {slot} = alloca {{ ptr, ptr }}, align 8\n");
                    Line($"{code} = extractvalue {{ ptr, ptr }} {operands[0]}, 0");
                    Line($"{context} = extractvalue {{ ptr, ptr }} {operands[0]}, 1");
                    Line($"call void @sable_trap_hook_take(ptr {slot}, ptr {code}, ptr {context})");
                    Line($"{Result()} = load {{ ptr, ptr }}, ptr {slot}");
                    return;
                }

                case "debugger_attached":
                    Line($"{Result()} = call zeroext i1 @sable_debugger_attached()");
                    return;

                // The trap instruction a debugger stops at, in the caller's
                // own code, so that it stops on the line that asked — when
                // one is attached: with none, a trap instruction would end
                // the program, as Debugger.break() does not.
                case "debugger_break": {
                    var attached = Fresh();
                    Line($"{attached} = call zeroext i1 @sable_debugger_attached()");
                    if (module._target.Platform == NativePlatform.Wasi) {
                        return;
                    }

                    var stop = NewLabel("debugtrap");
                    var go = NewLabel("debugged");
                    Line($"br i1 {attached}, label %{stop}, label %{go}");
                    _extra.Append(stop).Append(":\n")
                        .Append("  call void @llvm.debugtrap()").Append(_dbg).Append('\n')
                        .Append($"  br label %{go}\n");
                    Continue(go);
                    return;
                }

                case "io_read_line" or "io_succeeded" or "io_argument_count":
                    Line($"{Result()} = call {(builtin.Name == "io_read_line" ? "ptr" : builtin.Name == "io_succeeded" ? "zeroext i1" : "i64")} @sable_{builtin.Name}()");
                    return;

                case "io_read_file" or "io_list_directory" or "io_environment":
                    Line($"{Result()} = call ptr @sable_{builtin.Name}(ptr {operands[0]})");
                    return;

                case "io_file_exists" or "io_is_directory" or "io_remove_file" or "io_make_directory":
                    Line($"{Result()} = call zeroext i1 @sable_{builtin.Name}(ptr {operands[0]})");
                    return;

                case "io_write_file":
                    Line($"{Result()} = call zeroext i1 @sable_io_write_file(ptr {operands[0]}, ptr {operands[1]}, i1 zeroext {operands[2]})");
                    return;

                // Files by handle (runtime/io.c).
                case "file_error" or "file_error_code":
                    Line($"{Result()} = call i64 @sable_{builtin.Name}()");
                    return;

                case "file_open":
                    Line($"{Result()} = call i64 @sable_file_open(ptr {operands[0]}, i64 {operands[1]}, i64 {operands[2]})");
                    return;

                case "file_read":
                    Line($"{Result()} = call ptr @sable_file_read(i64 {operands[0]}, i64 {operands[1]}, i64 {operands[2]}, ptr {module.ArrayMetadata(new ArrayType(SilType.U8))})");
                    return;

                case "file_write": {
                    var type = (SliceType)builtin.Operands[1].Type;
                    var buffer = SlicePart(operands[1], type, 0);
                    var start = SlicePart(operands[1], type, 1);
                    var count = SlicePart(operands[1], type, 2);
                    Line($"{Result()} = call i64 @sable_file_write(i64 {operands[0]}, ptr {buffer}, i64 {start}, i64 {count}, i64 {operands[2]})");
                    return;
                }

                case "file_seek":
                    Line($"{Result()} = call i64 @sable_file_seek(i64 {operands[0]}, i64 {operands[1]}, i64 {operands[2]})");
                    return;

                case "file_sync" or "file_close":
                    Line($"{Result()} = call zeroext i1 @sable_{builtin.Name}(i64 {operands[0]})");
                    return;

                case "file_set_length":
                    Line($"{Result()} = call zeroext i1 @sable_file_set_length(i64 {operands[0]}, i64 {operands[1]})");
                    return;

                case "file_metadata":
                    Line($"{Result()} = call ptr @sable_file_metadata(i64 {operands[0]}, ptr {module.ArrayMetadata(new ArrayType(SilType.Int))})");
                    return;

                case "file_standard":
                    Line($"{Result()} = call i64 @sable_file_standard(i64 {operands[0]})");
                    return;

                case "file_map":
                    Line($"{Result()} = call ptr @sable_file_map(i64 {operands[0]}, i64 {operands[1]}, i64 {operands[2]})");
                    return;

                case "file_map_store": {
                    var type = (SliceType)builtin.Operands[2].Type;
                    var buffer = SlicePart(operands[2], type, 0);
                    var start = SlicePart(operands[2], type, 1);
                    var count = SlicePart(operands[2], type, 2);
                    Line($"{Result()} = call zeroext i1 @sable_file_map_store(ptr {operands[0]}, i64 {operands[1]}, ptr {buffer}, i64 {start}, i64 {count})");
                    return;
                }

                case "file_map_flush":
                    Line($"{Result()} = call zeroext i1 @sable_file_map_flush(ptr {operands[0]}, i64 {operands[1]}, i64 {operands[2]})");
                    return;

                case "file_map_advise":
                    Line($"call void @sable_file_map_advise(ptr {operands[0]}, i64 {operands[1]})");
                    return;

                case "file_canonical":
                    Line($"{Result()} = call ptr @sable_file_canonical(ptr {operands[0]})");
                    return;

                case "file_copy":
                    Line($"{Result()} = call zeroext i1 @sable_file_copy(ptr {operands[0]}, ptr {operands[1]}, i1 zeroext {operands[2]})");
                    return;

                // The file system by path (runtime/fs.c), and watching it (runtime/watch.c).
                case "fs_metadata":
                    Line($"{Result()} = call ptr @sable_fs_metadata(ptr {operands[0]}, i1 zeroext {operands[1]}, ptr {module.ArrayMetadata(new ArrayType(SilType.Int))})");
                    return;

                case "fs_set_permissions" or "fs_make_directory":
                    Line($"{Result()} = call zeroext i1 @sable_{builtin.Name}(ptr {operands[0]}, i64 {operands[1]})");
                    return;

                case "fs_set_time":
                    Line($"{Result()} = call zeroext i1 @sable_fs_set_time(ptr {operands[0]}, i64 {operands[1]}, i64 {operands[2]}, i64 {operands[3]})");
                    return;

                case "fs_remove":
                    Line($"{Result()} = call zeroext i1 @sable_fs_remove(ptr {operands[0]}, i1 zeroext {operands[1]})");
                    return;

                case "fs_remove_tree":
                    Line($"{Result()} = call zeroext i1 @sable_fs_remove_tree(ptr {operands[0]})");
                    return;

                case "fs_rename":
                    Line($"{Result()} = call zeroext i1 @sable_fs_rename(ptr {operands[0]}, ptr {operands[1]}, i1 zeroext {operands[2]})");
                    return;

                case "fs_link":
                    Line($"{Result()} = call zeroext i1 @sable_fs_link(ptr {operands[0]}, ptr {operands[1]}, i64 {operands[2]})");
                    return;

                case "fs_read_link":
                    Line($"{Result()} = call ptr @sable_fs_read_link(ptr {operands[0]}, ptr {module.ArrayMetadata(new ArrayType(SilType.U8))})");
                    return;

                case "fs_open_directory":
                    Line($"{Result()} = call i64 @sable_fs_open_directory(ptr {operands[0]})");
                    return;

                case "fs_watch":
                    Line($"{Result()} = call i64 @sable_fs_watch(ptr {operands[0]}, i1 zeroext {operands[1]})");
                    return;

                case "fs_watch_event":
                    Line($"{Result()} = call i64 @sable_fs_watch_event(i64 {operands[0]})");
                    return;

                case "fs_next_entry" or "fs_watch_take":
                    Line($"{Result()} = call ptr @sable_{builtin.Name}(i64 {operands[0]}, ptr {module.ArrayMetadata(new ArrayType(SilType.U8))})");
                    return;

                case "fs_close_directory" or "fs_unwatch":
                    Line($"call void @sable_{builtin.Name}(i64 {operands[0]})");
                    return;

                case "time_monotonic" or "time_suspending" or "time_wall_clock":
                    Line($"{Result()} = call i64 @sable_{builtin.Name}()");
                    return;

                case "process_run":
                    Line($"{Result()} = call ptr @sable_process_run(ptr {operands[0]}, ptr {operands[1]}, ptr {operands[2]})");
                    return;

                case "process_spawn":
                    Line($"{Result()} = call i64 @sable_process_spawn(ptr {operands[0]}, ptr {operands[1]}, ptr {operands[2]}, i64 {operands[3]}, ptr {operands[4]}, ptr {operands[5]})");
                    return;

                case "process_pipe" or "process_signal":
                    Line($"{Result()} = call i64 @sable_{builtin.Name}(i64 {operands[0]}, i64 {operands[1]})");
                    return;

                case "process_exit_code":
                    Line($"{Result()} = call i64 @sable_process_exit_code(i64 {operands[0]}, i1 zeroext {operands[1]})");
                    return;

                case "process_child_id" or "process_exit_event" or "signal_watch" or "signal_event" or "signal_take":
                    Line($"{Result()} = call i64 @sable_{builtin.Name}(i64 {operands[0]})");
                    return;

                case "process_ended" or "signal_raise":
                    Line($"{Result()} = call zeroext i1 @sable_{builtin.Name}(i64 {operands[0]})");
                    return;

                case "process_release" or "process_exit" or "signal_unwatch":
                    Line($"call void @sable_{builtin.Name}(i64 {operands[0]})");
                    return;

                case "process_abort":
                    Line("call void @sable_process_abort()");
                    return;

                case "process_status" or "process_status_signal" or "process_error" or "process_error_code" or "process_id" or "memory_allocations":
                    Line($"{Result()} = call i64 @sable_{builtin.Name}()");
                    return;

                case "process_error_output" or "process_current_directory" or "process_executable_path":
                    Line($"{Result()} = call ptr @sable_{builtin.Name}()");
                    return;

                case "process_notify":
                    Line($"{Result()} = call i64 @sable_process_notify(ptr {operands[0]}, ptr {operands[1]})");
                    return;

                case "process_change_directory":
                    Line($"{Result()} = call zeroext i1 @sable_process_change_directory(ptr {operands[0]})");
                    return;

                case "time_sleep":
                    Line($"call void @sable_time_sleep(i64 {operands[0]})");
                    return;

                case "io_argument_bytes":
                    SiteForRuntime();
                    Line($"{Result()} = call ptr @sable_io_argument_bytes(i64 {operands[0]}, ptr {module.ArrayMetadata(new ArrayType(SilType.U8))})");
                    return;

                case "env_set":
                    Line($"{Result()} = call zeroext i1 @sable_env_set(ptr {operands[0]}, ptr {operands[1]})");
                    return;

                case "env_remove":
                    Line($"{Result()} = call zeroext i1 @sable_env_remove(ptr {operands[0]})");
                    return;

                case "env_all":
                    Line($"{Result()} = call ptr @sable_env_all()");
                    return;

                case "system_text":
                    Line($"{Result()} = call ptr @sable_system_text(i64 {operands[0]})");
                    return;

                case "system_number" or "memory_statistic":
                    Line($"{Result()} = call i64 @sable_{builtin.Name}(i64 {operands[0]})");
                    return;

                case "io_argument":
                    SiteForRuntime();
                    Line($"{Result()} = call ptr @sable_io_argument(i64 {operands[0]})");
                    return;

                case "string_trimmed":
                    Line($"{Result()} = call ptr @sable_string_trimmed(ptr {operands[0]})");
                    return;

                case "string_split":
                    Line($"{Result()} = call ptr @sable_string_split(ptr {operands[0]}, i32 {operands[1]})");
                    return;

                case "string_from_Char":
                    Line($"{Result()} = call ptr @sable_string_from_char(i32 {operands[0]})");
                    return;

                case "string_from_Int":
                    Line($"{Result()} = call ptr @sable_string_from_int(i64 {operands[0]})");
                    return;

                case "string_from_F64":
                    Line($"{Result()} = call ptr @sable_string_from_f64(double {operands[0]})");
                    return;

                case "string_from_Bool":
                    Line($"{Result()} = call ptr @sable_string_from_bool(i1 zeroext {operands[0]})");
                    return;

                case "string_to_Int" or "string_to_F64" or "string_to_F32": {
                    var (target, function) = builtin.Name switch {
                        "string_to_Int" => ("i64", "sable_string_to_int"),
                        "string_to_F32" => ("float", "sable_string_to_f32"),
                        _ => ("double", "sable_string_to_f64"),
                    };
                    var slot = Scratch(target);
                    var ok = Fresh();
                    var value = Fresh();
                    var partial = Fresh();
                    Line($"store {target} zeroinitializer, ptr {slot}");
                    // A float is read from a range of the string's bytes.
                    var range = builtin.Name == "string_to_Int" ? "" : $", i64 {operands[1]}, i64 {operands[2]}";
                    Line($"{ok} = call zeroext i1 @{function}(ptr {operands[0]}{range}, ptr {slot})");
                    Line($"{value} = load {target}, ptr {slot}");
                    Line($"{partial} = insertvalue {{ i1, {target} }} poison, i1 {ok}, 0");
                    Line($"{Result()} = insertvalue {{ i1, {target} }} {partial}, {target} {value}, 1");
                    return;
                }

                case "char_to_Int":
                    Line($"{Result()} = sext i32 {operands[0]} to i64");
                    return;

                case "char_from_Int": {
                    var slot = Scratch("i32");
                    var ok = Fresh();
                    var value = Fresh();
                    var partial = Fresh();
                    Line($"{ok} = call zeroext i1 @sable_char_from_int(i64 {operands[0]}, ptr {slot})");
                    Line($"{value} = load i32, ptr {slot}");
                    Line($"{partial} = insertvalue {{ i1, i32 }} poison, i1 {ok}, 0");
                    Line($"{Result()} = insertvalue {{ i1, i32 }} {partial}, i32 {value}, 1");
                    return;
                }

                case "char_is_digit":
                    Line($"{Result()} = call zeroext i1 @sable_{builtin.Name}(i32 {operands[0]})");
                    return;

                case "abs_Int": {
                    var isMin = Fresh();
                    Line($"{isMin} = icmp eq i64 {operands[0]}, -9223372036854775808");
                    TrapIf(isMin, "integer overflow");
                    var negated = Fresh();
                    var negative = Fresh();
                    Line($"{negated} = sub i64 0, {operands[0]}");
                    Line($"{negative} = icmp slt i64 {operands[0]}, 0");
                    Line($"{Result()} = select i1 {negative}, i64 {negated}, i64 {operands[0]}");
                    return;
                }

                case "min_Int" or "max_Int": {
                    var less = Fresh();
                    Line($"{less} = icmp slt i64 {operands[0]}, {operands[1]}");
                    var (whenLess, otherwise) = builtin.Name == "min_Int" ? (operands[0], operands[1]) : (operands[1], operands[0]);
                    Line($"{Result()} = select i1 {less}, i64 {whenLess}, i64 {otherwise}");
                    return;
                }

                case "abs_F64":
                    Line($"{Result()} = call double @llvm.fabs.f64(double {operands[0]})");
                    return;

                case "min_F64" or "max_F64":
                    Line($"{Result()} = call double @llvm.{(builtin.Name == "min_F64" ? "minnum" : "maxnum")}.f64(double {operands[0]}, double {operands[1]})");
                    return;

                // The empty array is no buffer; the empty slice, no buffer and no elements.
                case "array_empty":
                    _names[builtin] = builtin.Type is SliceType ? "zeroinitializer" : "null";
                    return;

                case "no_context":
                    _names[builtin] = "null";
                    return;

                // An array of constants: the immortal one in read-only memory.
                case "array_literal" when SilBuiltins.IsConstantArray(builtin):
                    _names[builtin] = module.ArrayConstant((ArrayType)builtin.Type, [..operands]);
                    return;

                // An array literal that does not escape its frame: made there,
                // header and elements, each element stored where it goes.
                case "array_literal_stack" when !SilType.IsIndirect(((ArrayType)builtin.Type).Element): {
                    var arrayType = (ArrayType)builtin.Type;
                    var layout = $"{{ [48 x i8], [{operands.Length} x {Type(arrayType.Element)}] }}";
                    var memory = Scratch(layout, HeapAlign);
                    Line($"{Result()} = call ptr @sable_stack_array(ptr {memory}, ptr {module.ArrayMetadata(arrayType)}, i64 {operands.Length})");
                    for (var i = 0; i < operands.Length; i++) {
                        var element = Fresh();
                        Line($"{element} = getelementptr inbounds {layout}, ptr {memory}, i32 0, i32 1, i64 {i}");
                        Line($"store {Type(arrayType.Element)} {operands[i]}, ptr {element}, !tbaa {TbaaOf(Type(arrayType.Element))}");
                    }

                    return;
                }

                // Elements that are not all literals: appended, one by one;
                // an opaque type's always, its size known only at run time.
                case "array_literal" or "array_literal_stack": {
                    var arrayType = (ArrayType)builtin.Type;
                    var slot = Scratch("ptr");
                    Line($"store ptr null, ptr {slot}");
                    for (var i = 0; i < operands.Length; i++) {
                        var element = AppendSlot(slot, arrayType);
                        StoreElement(arrayType.Element, operands[i], element);
                    }

                    Line($"{Result()} = load ptr, ptr {slot}");
                    return;
                }

                // The array header is { object header (16), count, capacity, elements }:
                // the count and the elements are read in place, not through a
                // call. A slice's count is its own.
                case "array_count":
                    _names[builtin] = builtin.Operands[0].Type is SliceType counted ? SlicePart(operands[0], counted, 2) : Count(operands[0]);
                    return;

                case "array_get" or "array_get_borrowed": {
                    var element = builtin.Operands[0].Type is SliceType viewed
                        ? SliceElement(operands[0], operands[1], viewed, check: !builtin.InBounds)
                        : InlineElement(operands[0], operands[1], ((ArrayType)builtin.Operands[0].Type).Element, check: !builtin.InBounds);
                    // An opaque element: borrowed where it is, or copied into a buffer of its own.
                    if (builtin.Type is var opaqueElement && SilType.IsIndirect(opaqueElement)) {
                        if (builtin.Name == "array_get_borrowed") {
                            _names[builtin] = element;
                        } else {
                            var buffer = OpaqueBuffer(opaqueElement);
                            _names[builtin] = buffer;
                            OpaqueWitness(opaqueElement, CopyWitness, buffer, element);
                        }

                        return;
                    }

                    var value = Result();
                    Line($"{value} = load {Type(builtin.Type)}, ptr {element}, !tbaa {TbaaOf(Type(builtin.Type))}");
                    if (builtin.Name == "array_get") {
                        Own(true, builtin.Type, value);
                    }

                    return;
                }

                case "array_append": {
                    var arrayType = (ArrayType)((AddressType)builtin.Operands[0].Type).Pointee;
                    var element = AppendSlot(operands[0], arrayType);
                    ShareIntoArray(operands[0], arrayType.Element, operands[1]);
                    StoreElement(arrayType.Element, operands[1], element);
                    return;
                }

                // A slice made the only reference to its buffer: the runtime
                // copies its run alone when the buffer is shared.
                case "array_make_unique" when builtin.Operands[0].Type is AddressType { Pointee: SliceType unique }:
                    Line($"call void @sable_slice_make_unique(ptr {operands[0]}, ptr {ArrayMetadataOf(new ArrayType(unique.Element))})");
                    return;

                // Unique already — the loop's 'array_make_unique' made it so —
                // the element written where it is, its index checked against
                // the slice's count, as an array's is: no call per element.
                case "array_set_unique" when builtin.Operands[0].Type is AddressType { Pointee: SliceType unique }: {
                    var elementType = Type(unique.Element);
                    var slice = Fresh();
                    Line($"{slice} = load {Type(unique)}, ptr {operands[0]}");
                    var element = SliceElement(slice, operands[1], unique, check: !builtin.InBounds);
                    ShareIntoArray(operands[0], unique.Element, operands[2]);
                    if (SilType.IsIndirect(unique.Element)) {
                        OpaqueWitness(unique.Element, AssignWithTakeWitness, element, operands[2]);
                        return;
                    }

                    var old = Fresh();
                    Line($"{old} = load {elementType}, ptr {element}, !tbaa {TbaaOf(elementType)}");
                    Line($"store {elementType} {operands[2]}, ptr {element}, !tbaa {TbaaOf(elementType)}");
                    Own(false, unique.Element, old);
                    return;
                }

                case "array_set" or "array_set_unique" when builtin.Operands[0].Type is AddressType { Pointee: SliceType written }: {
                    var elementType = Type(written.Element);
                    var element = SliceMutableElement(operands[0], operands[1], written);
                    ShareIntoArray(operands[0], written.Element, operands[2]);
                    if (SilType.IsIndirect(written.Element)) {
                        OpaqueWitness(written.Element, AssignWithTakeWitness, element, operands[2]);
                        return;
                    }

                    var old = Fresh();
                    Line($"{old} = load {elementType}, ptr {element}, !tbaa {TbaaOf(elementType)}");
                    Line($"store {elementType} {operands[2]}, ptr {element}, !tbaa {TbaaOf(elementType)}");
                    Own(false, written.Element, old);
                    return;
                }

                // Unique already, the element's address is where it is.
                case "array_element_addr_unique" when builtin.Operands[0].Type is AddressType { Pointee: SliceType addressedUnique }: {
                    var slice = Fresh();
                    Line($"{slice} = load {Type(addressedUnique)}, ptr {operands[0]}");
                    _names[builtin] = SliceElement(slice, operands[1], addressedUnique, check: !builtin.InBounds);
                    return;
                }

                case "array_element_addr_unique": {
                    var arrayType = (ArrayType)((AddressType)builtin.Operands[0].Type).Pointee;
                    var array = Fresh();
                    Line($"{array} = load ptr, ptr {operands[0]}, !tbaa {TbaaReference}");
                    _names[builtin] = InlineElement(array, operands[1], arrayType.Element, check: !builtin.InBounds);
                    return;
                }

                case "array_element_addr" when builtin.Operands[0].Type is AddressType { Pointee: SliceType addressed }:
                    _names[builtin] = SliceMutableElement(operands[0], operands[1], addressed);
                    return;

                case "array_make_unique":
                    MakeUnique(operands[0], (ArrayType)((AddressType)builtin.Operands[0].Type).Pointee);
                    return;

                // Unique already: the element written where it is, its index checked.
                case "array_set_unique": {
                    var arrayType = (ArrayType)((AddressType)builtin.Operands[0].Type).Pointee;
                    var elementType = Type(arrayType.Element);
                    var array = Fresh();
                    Line($"{array} = load ptr, ptr {operands[0]}, !tbaa {TbaaReference}");
                    var element = InlineElement(array, operands[1], arrayType.Element, check: !builtin.InBounds);
                    ShareIntoArray(operands[0], arrayType.Element, operands[2]);
                    if (arrayType.Element is var replacedUnique && SilType.IsIndirect(replacedUnique)) {
                        OpaqueWitness(replacedUnique, AssignWithTakeWitness, element, operands[2]);
                        return;
                    }

                    var old = Fresh();
                    Line($"{old} = load {elementType}, ptr {element}, !tbaa {TbaaOf(elementType)}");
                    Line($"store {elementType} {operands[2]}, ptr {element}, !tbaa {TbaaOf(elementType)}");
                    Own(false, arrayType.Element, old);
                    return;
                }

                case "array_set": {
                    var arrayType = (ArrayType)((AddressType)builtin.Operands[0].Type).Pointee;
                    var elementType = Type(arrayType.Element);
                    var element = MutableElement(operands[0], operands[1], arrayType);
                    ShareIntoArray(operands[0], arrayType.Element, operands[2]);
                    if (arrayType.Element is var replaced && SilType.IsIndirect(replaced)) {
                        OpaqueWitness(replaced, AssignWithTakeWitness, element, operands[2]);
                        return;
                    }

                    var old = Fresh();
                    Line($"{old} = load {elementType}, ptr {element}, !tbaa {TbaaOf(elementType)}");
                    Line($"store {elementType} {operands[2]}, ptr {element}, !tbaa {TbaaOf(elementType)}");
                    Own(false, arrayType.Element, old);
                    return;
                }

                case "array_element_addr": {
                    var arrayType = (ArrayType)((AddressType)builtin.Operands[0].Type).Pointee;
                    _names[builtin] = MutableElement(operands[0], operands[1], arrayType);
                    return;
                }

                case "array_capacity":
                    Line($"{Result()} = call i64 @sable_array_capacity(ptr {operands[0]})");
                    return;

                // A type known to compare by its bits by the runtime's vector
                // loop; one known not to, -1, with no call; an opaque one as
                // its metadata says.
                case "array_mismatch": {
                    var element = ArrayType.ElementOf(builtin.Operands[0].Type)!;
                    var opaque = SilType.IsIndirect(element);
                    if (!opaque && !SilBuiltins.ComparesByBits(element)) {
                        _names[builtin] = "-1";
                        return;
                    }

                    // An array is its buffer, all of it.
                    string Run(int operand) => builtin.Operands[operand].Type is SliceType type
                        ? $"ptr {SlicePart(operands[operand], type, 0)}, i64 {SlicePart(operands[operand], type, 1)}, i64 {SlicePart(operands[operand], type, 2)}"
                        : $"ptr {operands[operand]}, i64 0, i64 {Count(operands[operand])}";

                    var runs = $"{Run(0)}, {Run(1)}";
                    Line(opaque
                        ? $"{Result()} = call i64 @sable_opaque_mismatch({runs}, ptr {OpaqueMetadata(element)})"
                        : $"{Result()} = call i64 @sable_array_mismatch({runs}, i64 {Size(element)})");
                    return;
                }

                case "array_rotate": {
                    var pointee = ((AddressType)builtin.Operands[0].Type).Pointee;
                    var element = pointee is SliceType rotated ? rotated.Element : ((ArrayType)pointee).Element;
                    var function = pointee is SliceType ? "sable_slice_rotate" : "sable_array_rotate";
                    SiteForRuntime();
                    Line($"call void @{function}(ptr {operands[0]}, ptr {ArrayMetadataOf(new ArrayType(element))}, i64 {operands[1]}, i64 {operands[2]}, i64 {operands[3]})");
                    return;
                }

                // A slice's elements stored over [from, to) of the array or
                // the slice at the address: the runtime checks the counts,
                // and stores nothing when the slice is still that range's view.
                case "array_store_slice": {
                    var pointee = ((AddressType)builtin.Operands[0].Type).Pointee;
                    var element = pointee is SliceType written ? written.Element : ((ArrayType)pointee).Element;
                    var function = pointee is SliceType ? "sable_slice_store_slice" : "sable_array_store_slice";
                    var source = (SliceType)builtin.Operands[3].Type;
                    var (buffer, start, count) = (SlicePart(operands[3], source, 0), SlicePart(operands[3], source, 1), SlicePart(operands[3], source, 2));
                    SiteForRuntime();
                    Line($"call void @{function}(ptr {operands[0]}, ptr {ArrayMetadataOf(new ArrayType(element))}, i64 {operands[1]}, i64 {operands[2]}, ptr {buffer}, i64 {start}, i64 {count})");
                    return;
                }

                case "array_truncate": {
                    var arrayType = (ArrayType)((AddressType)builtin.Operands[0].Type).Pointee;
                    Line($"call void @sable_array_truncate(ptr {operands[0]}, ptr {ArrayMetadataOf(arrayType)}, i64 {operands[1]})");
                    return;
                }

                // The element, borrowed, handed over where it is: in a slot
                // of the frame's own for a value, its buffer for an opaque one.
                case "array_append_all": {
                    var arrayType = (ArrayType)((AddressType)builtin.Operands[0].Type).Pointee;
                    var source = (SliceType)builtin.Operands[1].Type;
                    var (buffer, start, count) = (SlicePart(operands[1], source, 0), SlicePart(operands[1], source, 1), SlicePart(operands[1], source, 2));
                    Line($"call void @sable_array_append_all(ptr {operands[0]}, ptr {ArrayMetadataOf(arrayType)}, ptr {buffer}, i64 {start}, i64 {count})");
                    return;
                }

                case "array_append_repeating": {
                    var arrayType = (ArrayType)((AddressType)builtin.Operands[0].Type).Pointee;
                    var value = operands[1];
                    if (!SilType.IsIndirect(arrayType.Element)) {
                        value = Fresh();
                        _entryAllocas.Append($"  {value} = alloca {Type(arrayType.Element)}, align 16\n");
                        Line($"store {Type(arrayType.Element)} {operands[1]}, ptr {value}");
                    }

                    SiteForRuntime();
                    Line($"call void @sable_array_append_repeating(ptr {operands[0]}, ptr {ArrayMetadataOf(arrayType)}, ptr {value}, i64 {operands[2]})");
                    return;
                }

                case "array_append_holes": {
                    var arrayType = (ArrayType)((AddressType)builtin.Operands[0].Type).Pointee;
                    SiteForRuntime();
                    Line($"call void @sable_array_append_holes(ptr {operands[0]}, ptr {ArrayMetadataOf(arrayType)}, i64 {operands[1]})");
                    return;
                }

                // Where a trivial element was is left as it is: nothing
                // destroys one, and no one reads a hole.
                case "array_take": {
                    var arrayType = (ArrayType)((AddressType)builtin.Operands[0].Type).Pointee;
                    var element = MutableElement(operands[0], operands[1], arrayType);
                    if (arrayType.Element is var taken && SilType.IsIndirect(taken)) {
                        var buffer = OpaqueBuffer(taken);
                        _names[builtin] = buffer;
                        OpaqueMove(taken, buffer, element);
                        Line($"call void @llvm.memset.p0.i64(ptr {element}, i8 0, i64 {OpaqueSize(taken)}, i1 false)");
                        return;
                    }

                    Line($"{Result()} = load {Type(taken)}, ptr {element}");
                    if (!taken.IsTrivial) {
                        Line($"store {Type(taken)} zeroinitializer, ptr {element}");
                    }

                    return;
                }

                case "array_append_utf8": {
                    var arrayType = (ArrayType)((AddressType)builtin.Operands[0].Type).Pointee;
                    Line($"call void @sable_array_append_utf8(ptr {operands[0]}, ptr {ArrayMetadataOf(arrayType)}, ptr {operands[1]}, i64 {operands[2]}, i64 {operands[3]})");
                    return;
                }

                case "array_reserve": {
                    var arrayType = (ArrayType)((AddressType)builtin.Operands[0].Type).Pointee;
                    Line($"call void @sable_array_reserve(ptr {operands[0]}, ptr {ArrayMetadataOf(arrayType)}, i64 {operands[1]})");
                    return;
                }

                case "array_remove_last": {
                    var arrayType = (ArrayType)((AddressType)builtin.Operands[0].Type).Pointee;
                    var element = LastSlot(operands[0], arrayType);
                    if (arrayType.Element is var removed && SilType.IsIndirect(removed)) {
                        var buffer = OpaqueBuffer(removed);
                        _names[builtin] = buffer;
                        OpaqueMove(removed, buffer, element);
                        return;
                    }

                    Line($"{Result()} = load {Type(arrayType.Element)}, ptr {element}");
                    return;
                }

                case "region_create":
                    Line($"{Result()} = call ptr @sable_region_create()");
                    return;

                case "region_reset" or "region_push" or "region_pop":
                    Line($"call void @sable_{builtin.Name}(ptr {operands[0]})");
                    return;

                // Room in the region for the value, and how to destroy it when the region goes.
                case "region_alloc": {
                    var type = ((AddressType)builtin.Type).Pointee;
                    var size = $"ptrtoint (ptr getelementptr ({Type(type)}, ptr null, i32 1) to i64)";
                    Line($"{Result()} = call ptr @sable_region_alloc(ptr {operands[0]}, i64 {size}, ptr {module.ElementDestroyer(type)})");
                    return;
                }

                // The whole of an array, viewed: its buffer retained, or borrowed from
                // the array, from 0 for its count.
                case "slice_of" or "slice_of_borrowed": {
                    var count = Count(operands[0]);
                    if (builtin.Name == "slice_of") {
                        RetainBuffer(operands[0]);
                    }

                    _names[builtin] = MakeSlice((SliceType)builtin.Type, operands[0], "0", count);
                    return;
                }

                // A slice as an array again: its buffer, retained, when it is the
                // whole of it, else a copy of its run (the runtime decides).
                case "array_of_slice" when builtin.Operands[0].Type is ArrayType:
                    RetainBuffer(operands[0]);
                    _names[builtin] = operands[0];
                    return;

                // The array moved into a view of the whole of it: its buffer
                // neither retained nor released.
                case "slice_of_take":
                    _names[builtin] = MakeSlice((SliceType)builtin.Type, operands[0], "0", Count(operands[0]));
                    return;

                // The view moved back: its buffer as it is while it is the
                // whole of it, which it is unless the call replaced it; else
                // a copy of its run, and the view let go.
                case "array_of_slice_take": {
                    var slice = (SliceType)builtin.Operands[0].Type;
                    var (buffer, start, count) = (SlicePart(operands[0], slice, 0), SlicePart(operands[0], slice, 1), SlicePart(operands[0], slice, 2));
                    var fromStart = Fresh();
                    var same = Fresh();
                    var whole = Fresh();
                    Line($"{fromStart} = icmp eq i64 {start}, 0");
                    Line($"{same} = icmp eq i64 {count}, {Count(buffer)}");
                    Line($"{whole} = and i1 {fromStart}, {same}");
                    var kept = NewLabel("whole");
                    var copied = NewLabel("part");
                    var done = NewLabel("array");
                    Line($"br i1 {whole}, label %{kept}, label %{copied}");
                    Continue(kept);
                    Line($"br label %{done}");
                    Continue(copied);
                    var copy = Fresh();
                    Line($"{copy} = call ptr @sable_array_of_slice(ptr {buffer}, i64 {start}, i64 {count}, ptr {ArrayMetadataOf(new ArrayType(slice.Element))})");
                    Line($"call void @sable.release(ptr {buffer})");
                    Line($"br label %{done}");
                    Continue(done);
                    Line($"{Result()} = phi ptr [ {buffer}, %{kept} ], [ {copy}, %{copied} ]");
                    return;
                }

                case "array_of_slice": {
                    var slice = (SliceType)builtin.Operands[0].Type;
                    var (buffer, start, count) = (SlicePart(operands[0], slice, 0), SlicePart(operands[0], slice, 1), SlicePart(operands[0], slice, 2));
                    Line($"{Result()} = call ptr @sable_array_of_slice(ptr {buffer}, i64 {start}, i64 {count}, ptr {ArrayMetadataOf(new ArrayType(slice.Element))})");
                    return;
                }

                // Elements [from, to) of an array or a slice: a view of the same
                // buffer, retained, the range checked against the count. An
                // empty range holds no buffer.
                case "array_slice": {
                    var slice = (SliceType)builtin.Type;
                    string buffer, start, count;
                    if (builtin.Operands[0].Type is SliceType) {
                        (buffer, start, count) = (SlicePart(operands[0], slice, 0), SlicePart(operands[0], slice, 1), SlicePart(operands[0], slice, 2));
                    } else {
                        (buffer, start, count) = (operands[0], "0", Count(operands[0]));
                    }

                    var (from, to) = (operands[1], operands[2]);
                    var negative = Fresh();
                    var past = Fresh();
                    var reversed = Fresh();
                    var either = Fresh();
                    var outside = Fresh();
                    Line($"{negative} = icmp slt i64 {from}, 0");
                    Line($"{past} = icmp sgt i64 {to}, {count}");
                    Line($"{reversed} = icmp sgt i64 {from}, {to}");
                    Line($"{either} = or i1 {negative}, {past}");
                    Line($"{outside} = or i1 {either}, {reversed}");
                    TrapIf(outside, "array slice out of range");

                    var length = Fresh();
                    var empty = Fresh();
                    var held = Fresh();
                    var first = Fresh();
                    Line($"{length} = sub i64 {to}, {from}");
                    Line($"{empty} = icmp eq i64 {length}, 0");
                    Line($"{held} = select i1 {empty}, ptr null, ptr {buffer}");
                    RetainBuffer(held);
                    Line($"{first} = add i64 {start}, {from}");
                    _names[builtin] = MakeSlice(slice, held, first, length);
                    return;
                }

                case "array_clear": {
                    var old = Fresh();
                    Line($"{old} = load ptr, ptr {operands[0]}");
                    Line($"store ptr null, ptr {operands[0]}");
                    Line($"call void @sable.release(ptr {old})");
                    return;
                }

                case "convert_Int_F64":
                    Line($"{Result()} = sitofp i64 {operands[0]} to double");
                    return;

                case "convert_F64_Int": {
                    // Out of range (or NaN) has no Int; fptosi would give poison.
                    var low = Fresh();
                    var high = Fresh();
                    var inside = Fresh();
                    var outside = Fresh();
                    Line($"{low} = fcmp oge double {operands[0]}, -9.2233720368547758e18");
                    Line($"{high} = fcmp olt double {operands[0]}, 9.2233720368547758e18");
                    Line($"{inside} = and i1 {low}, {high}");
                    Line($"{outside} = xor i1 {inside}, true");
                    TrapIf(outside, "F64 does not fit in Int");
                    Line($"{Result()} = fptosi double {operands[0]} to i64");
                    return;
                }

                case "print_Int":
                    Line($"call void @sable_print_int(i64 {operands[0]})");
                    return;

                case "print_F64":
                    Line($"call void @sable_print_f64(double {operands[0]})");
                    return;

                case "print_Bool":
                    Line($"call void @sable_print_bool(i1 zeroext {operands[0]})");
                    return;

                case "print_Char":
                    Line($"call void @sable_print_char(i32 {operands[0]})");
                    return;
            }

            if (builtin.Name.StartsWith("cmp_", StringComparison.Ordinal)) {
                var separator = builtin.Name.LastIndexOf('_');
                var relation = builtin.Name[4..separator];
                var operandType = builtin.Name[(separator + 1)..];

                var (instruction, predicate, type) = operandType switch {
                    "F64" => ("fcmp", relation switch {
                        "eq" => "oeq",
                        "ne" => "une",
                        "lt" => "olt",
                        "le" => "ole",
                        "gt" => "ogt",
                        _ => "oge",
                    }, "double"),
                    _ => ("icmp", relation switch {
                        "eq" => "eq",
                        "ne" => "ne",
                        "lt" => "slt",
                        "le" => "sle",
                        "gt" => "sgt",
                        _ => "sge",
                    }, operandType switch { "Bool" => "i1", "Char" => "i32", _ => "i64" }),
                };

                Line($"{Result()} = {instruction} {predicate} {type} {operands[0]}, {operands[1]}");
                return;
            }

            throw new NotSupportedException($"no LLVM lowering for builtin \"{builtin.Name}\"");
        }

        /// <summary>
        ///     The inline-layout builtins: views are byte pointers; fixed
        ///     fields are reached by offset, the rest by the runtime's walk.
        /// </summary>
        void Inline(BuiltinInst builtin, string[] operands, Func<string> Result) {
            const int BytesOffset = 32; // a block's header, length and capacity
            static SilType Pointee(SilValue value) => ((InlinePointerType)value.Type).Pointee;
            StructType RootOf(SilValue slot) => (StructType)((AddressType)slot.Type).Pointee;

            // Where an element or a value to copy is: a view as it is, a block's bytes.
            string BytesOf(SilValue value, string name) {
                if (value.Type is InlinePointerType) {
                    return name;
                }

                var bytes = Fresh();
                Line($"{bytes} = getelementptr inbounds i8, ptr {name}, i64 {BytesOffset}");
                return bytes;
            }

            var name = builtin.Name;
            if (name.StartsWith("inline_field.", StringComparison.Ordinal)) {
                var owner = (StructType)Pointee(builtin.Operands[0]);
                var layout = InlineLayout.Of(owner);
                var slot = layout.SlotOf(int.Parse(name["inline_field.".Length..], CultureInfo.InvariantCulture));
                if (layout.Slots[slot].IsFixed) {
                    Line($"{Result()} = getelementptr inbounds i8, ptr {operands[0]}, i64 {layout.Slots[slot].Offset}");
                } else {
                    Line($"{Result()} = call ptr {module.FieldFunction(owner, slot)}(ptr {operands[0]})");
                }

                return;
            }

            switch (name) {
                case "inline_new":
                    Line($"{Result()} = call ptr @sable_inline_new(ptr {module.InlineLayoutGlobal((StructType)builtin.Type)})");
                    return;

                // A block in the frame, with room to grow before it must move to the heap.
                // Aligned as a heap block is: its header's counts are read
                // atomically, which arm64 refuses at an address less aligned
                // than the count, and an array of bytes is aligned to 1.
                case "inline_new_stack": {
                    const int Room = 96;
                    var memory = Scratch($"[{BytesOffset + Room} x i8]", 16);
                    Line($"{Result()} = call ptr @sable_inline_new_stack(ptr {memory}, ptr {module.InlineLayoutGlobal((StructType)builtin.Type)}, i64 {Room})");
                    return;
                }

                case "inline_base":
                    Line($"{Result()} = getelementptr inbounds i8, ptr {operands[0]}, i64 {BytesOffset}");
                    return;

                // Already the only reference, and not immortal: its bytes, with no call.
                case "inline_unique": {
                    var block = Fresh();
                    var strongField = Fresh();
                    var strong = Fresh();
                    var flagsField = Fresh();
                    var flags = Fresh();
                    var immortal = Fresh();
                    var isImmortal = Fresh();
                    var sole = Fresh();
                    var fast = Fresh();
                    var start = NewLabel("sole");
                    var slow = NewLabel("copy");
                    var done = NewLabel("unique");
                    Line($"{block} = load ptr, ptr {operands[0]}");
                    Line($"{strongField} = getelementptr inbounds %SableObjectHeader, ptr {block}, i32 0, i32 1");
                    Line($"{strong} = load atomic i32, ptr {strongField} acquire, align 4");
                    Line($"{flagsField} = getelementptr inbounds %SableObjectHeader, ptr {block}, i32 0, i32 2");
                    Line($"{flags} = load atomic i32, ptr {flagsField} monotonic, align 4");
                    Line($"{immortal} = and i32 {flags}, 1");
                    Line($"{isImmortal} = icmp ne i32 {immortal}, 0");
                    Line($"{sole} = icmp eq i32 {strong}, 1");
                    var ok = Fresh();
                    var notImmortal = Fresh();
                    Line($"{notImmortal} = xor i1 {isImmortal}, true");
                    Line($"{ok} = and i1 {sole}, {notImmortal}");
                    Line($"br i1 {ok}, label %{start}, label %{slow}");
                    Continue(start);
                    Line($"{fast} = getelementptr inbounds i8, ptr {block}, i64 {BytesOffset}");
                    Line($"br label %{done}");
                    Continue(slow);
                    var called = Fresh();
                    Line($"{called} = call ptr @sable_inline_unique(ptr {operands[0]})");
                    Line($"br label %{done}");
                    Continue(done);
                    Line($"{Result()} = phi ptr [ {fast}, %{start} ], [ {called}, %{slow} ]");
                    return;
                }

                case "inline_copy":
                    Line($"{Result()} = call ptr @sable_inline_copy(ptr {module.InlineLayoutGlobal((StructType)builtin.Type)}, ptr {operands[0]})");
                    return;

                case "inline_load": {
                    var type = builtin.Type;
                    if (type == SilType.Bool) {
                        var b = Fresh();
                        Line($"{b} = load i8, ptr {operands[0]}, align 1");
                        Line($"{Result()} = trunc i8 {b} to i1");
                    } else {
                        Line($"{Result()} = load {Type(type)}, ptr {operands[0]}, align 1");
                    }

                    return;
                }

                case "inline_store": {
                    var type = builtin.Operands[1].Type;
                    if (type == SilType.Bool) {
                        var b = Fresh();
                        Line($"{b} = zext i1 {operands[1]} to i8");
                        Line($"store i8 {b}, ptr {operands[0]}, align 1");
                    } else {
                        Line($"store {Type(type)} {operands[1]}, ptr {operands[0]}, align 1");
                    }

                    return;
                }

                case "inline_count" or "inline_first": {
                    var head = Fresh();
                    Line($"{head} = call {{ i64, ptr }} {module.InlineHead((InlineArrayType)Pointee(builtin.Operands[0]))}(ptr {operands[0]})");
                    Line($"{Result()} = extractvalue {{ i64, ptr }} {head}, {(name == "inline_count" ? 0 : 1)}");
                    return;
                }

                case "inline_next":
                    _names[builtin] = module.InlineNext((InlineArrayType)Pointee(builtin.Operands[0]), operands[1], Fresh, Line);
                    return;

                case "inline_at":
                    SiteForRuntime();
                    Line($"{Result()} = call ptr @sable_inline_at(ptr {module.InlineArrayGlobal((InlineArrayType)Pointee(builtin.Operands[0]))}, ptr {operands[0]}, i64 {operands[1]})");
                    return;

                case "inline_append": {
                    var array = (InlineArrayType)Pointee(builtin.Operands[1]);
                    var element = builtin.Operands[2];
                    string pointer;
                    long size;
                    if (InlineLayout.Scalar(element.Type) is var (_, width)) {
                        // A scalar is stored as its bytes: through memory.
                        pointer = Scratch(element.Type == SilType.Bool ? "i8" : Type(element.Type));
                        if (element.Type == SilType.Bool) {
                            var b = Fresh();
                            Line($"{b} = zext i1 {operands[2]} to i8");
                            Line($"store i8 {b}, ptr {pointer}, align 1");
                        } else {
                            Line($"store {Type(element.Type)} {operands[2]}, ptr {pointer}, align 1");
                        }

                        size = width;
                    } else {
                        pointer = BytesOf(element, operands[2]);
                        size = -1;
                    }

                    // The element's size, and where the array ends, found by the walkers made for these layouts.
                    if (size < 0) {
                        var after = Fresh();
                        var start = Fresh();
                        var end = Fresh();
                        Line($"{after} = call ptr {module.SkipFunction((StructType)array.Element)}(ptr {pointer})");
                        Line($"{start} = ptrtoint ptr {pointer} to i64");
                        Line($"{end} = ptrtoint ptr {after} to i64");
                        var measured = Fresh();
                        Line($"{measured} = sub i64 {end}, {start}");
                        var arrayEnd = Fresh();
                        Line($"{arrayEnd} = call ptr {module.ArraySkipFunction(array)}(ptr {operands[1]})");
                        SiteForRuntime();
                        Line($"call void @sable_inline_append_at(ptr {operands[0]}, ptr {module.InlineLayoutGlobal(RootOf(builtin.Operands[0]))}, ptr {module.InlineArrayGlobal(array)}, ptr {operands[1]}, ptr {arrayEnd}, ptr {pointer}, i64 {measured})");
                    } else {
                        var arrayEnd = Fresh();
                        Line($"{arrayEnd} = call ptr {module.ArraySkipFunction(array)}(ptr {operands[1]})");
                        SiteForRuntime();
                        Line($"call void @sable_inline_append_at(ptr {operands[0]}, ptr {module.InlineLayoutGlobal(RootOf(builtin.Operands[0]))}, ptr {module.InlineArrayGlobal(array)}, ptr {operands[1]}, ptr {arrayEnd}, ptr {pointer}, i64 {size})");
                    }
                    return;
                }

                case "inline_assign_string":
                    SiteForRuntime();
                    Line($"call void @sable_inline_assign_string(ptr {operands[0]}, ptr {module.InlineLayoutGlobal(RootOf(builtin.Operands[0]))}, ptr {module.InlineArrayGlobal((InlineArrayType)Pointee(builtin.Operands[1]))}, ptr {operands[1]}, ptr {operands[2]})");
                    return;

                case "inline_assign":
                    SiteForRuntime();
                    Line($"call void @sable_inline_assign(ptr {operands[0]}, ptr {module.InlineLayoutGlobal(RootOf(builtin.Operands[0]))}, ptr {module.InlineLayoutGlobal((StructType)Pointee(builtin.Operands[1]))}, ptr {operands[1]}, ptr {BytesOf(builtin.Operands[2], operands[2])})");
                    return;

                case "inline_ref_get":
                    Line($"{Result()} = call ptr @sable_inline_ref_get(ptr {operands[0]}, i32 {((RelativeReferenceType)Pointee(builtin.Operands[0])).Width})");
                    return;

                case "inline_is_null":
                    Line($"{Result()} = icmp eq ptr {operands[0]}, null");
                    return;

                case "inline_ref_set":
                    SiteForRuntime();
                    Line($"call void @sable_inline_ref_set(ptr {operands[0]}, ptr {operands[1]}, i32 {((RelativeReferenceType)Pointee(builtin.Operands[1])).Width}, ptr {operands[2]})");
                    return;

                case "inline_ref_clear":
                    Line($"store i{((RelativeReferenceType)Pointee(builtin.Operands[0])).Width * 8} 0, ptr {operands[0]}, align 1");
                    return;

                case "inline_to_string":
                    Line($"{Result()} = call ptr @sable_inline_to_string(ptr {module.InlineArrayGlobal((InlineArrayType)Pointee(builtin.Operands[0]))}, ptr {operands[0]})");
                    return;

                case "inline_bytes":
                    Line($"{Result()} = call ptr @sable_inline_bytes(ptr {module.InlineLayoutGlobal((StructType)Pointee(builtin.Operands[0]))}, ptr {operands[0]}, ptr {module.ArrayMetadata(new ArrayType(SilType.U8))})");
                    return;

                case "inline_from":
                    Line($"{Result()} = call ptr @sable_inline_from(ptr {operands[0]})");
                    return;

                case "inline_invalid":
                    Line($"{Result()} = call ptr @sable_inline_invalid(ptr {module.InlineLayoutGlobal((StructType)builtin.Operands[0].Type)}, ptr {operands[0]})");
                    return;
            }

            throw new NotSupportedException($"no LLVM lowering for builtin \"{name}\"");
        }

        /// <summary>
        ///     The numeric protocols' builtins (SilBuiltins.IsNumericIntrinsic),
        ///     at every width: each an LLVM intrinsic or a few instructions.
        ///     Also a number converted to its own type, which is itself.
        /// </summary>
        bool NumericIntrinsic(BuiltinInst builtin, string[] operands, Func<string> Result) {
            var name = builtin.Name;
            if ((name.StartsWith("wrap_", StringComparison.Ordinal) || name.StartsWith("convert_", StringComparison.Ordinal)) && builtin.Operands[0].Type == builtin.Type) {
                _names[builtin] = operands[0];
                return true;
            }

            if (!SilBuiltins.IsNumericIntrinsic(name)) {
                return false;
            }

            var separator = name.LastIndexOf('_');
            var op = name[..separator];
            var type = SilType.Numbers.First(n => n.ToString() == name[(separator + 1)..]);
            var t = Type(type);

            if (type == SilType.BF16) {
                return Bf16Intrinsic(op, builtin, operands);
            }

            if (SilType.FloatBits(type) is { } floatBits) {
                var suffix = $"f{floatBits}";
                var bitsType = $"i{floatBits}";
                switch (op) {
                    case "sqrt" or "floor" or "ceil" or "trunc" or "round":
                        Line($"{Result()} = call {t} @llvm.{op}.{suffix}({t} {operands[0]})");
                        return true;

                    // To nearest, ties to even: 'nearbyint', which rounds by
                    // the current mode, and Sable never changes it from that.
                    // Not 'roundeven': where the machine has no instruction for
                    // it (x86-64 before SSE4.1) it is a call to C's roundeven,
                    // which Microsoft's C runtime does not have; nearbyint it has.
                    case "round_even":
                        Line($"{Result()} = call {t} @llvm.nearbyint.{suffix}({t} {operands[0]})");
                        return true;

                    case "copysign" or "minimum" or "maximum":
                        Line($"{Result()} = call {t} @llvm.{op}.{suffix}({t} {operands[0]}, {t} {operands[1]})");
                        return true;

                    case "fma":
                        Line($"{Result()} = call {t} @llvm.fma.{suffix}({t} {operands[0]}, {t} {operands[1]}, {t} {operands[2]})");
                        return true;

                    // libm's, for a double or a float; a half's through the
                    // float it widens to exactly, the remainder exact in either.
                    case "remainder": {
                        if (floatBits == 64) {
                            Line($"{Result()} = call double @remainder(double {operands[0]}, double {operands[1]})");
                            return true;
                        }

                        var (left, right) = (operands[0], operands[1]);
                        if (floatBits == 16) {
                            left = Fresh();
                            right = Fresh();
                            Line($"{left} = fpext half {operands[0]} to float");
                            Line($"{right} = fpext half {operands[1]} to float");
                        }

                        var remainder = floatBits == 16 ? Fresh() : Result();
                        Line($"{remainder} = call float @remainderf(float {left}, float {right})");
                        if (floatBits == 16) {
                            Line($"{Result()} = fptrunc float {remainder} to half");
                        }

                        return true;
                    }

                    // The power is clamped to what moves any float from its
                    // least to beyond its greatest, so that it fits an i32.
                    //
                    // A half's is scaled as the float it widens to exactly,
                    // then narrowed: LLVM 18 expands a half's ldexp, where the
                    // machine has no half arithmetic (AArch64 without FP16),
                    // into a 16-bit shift of the exponent its instruction
                    // selection cannot select, as it does a half's frexp. The
                    // float's range holds any half scaled into the half's, so
                    // the one rounding is the narrowing's, as the half's own.
                    case "scale": {
                        var low = Fresh();
                        var clamped = Fresh();
                        var power = Fresh();
                        Line($"{low} = call i64 @llvm.smax.i64(i64 {operands[1]}, i64 -100000)");
                        Line($"{clamped} = call i64 @llvm.smin.i64(i64 {low}, i64 100000)");
                        Line($"{power} = trunc i64 {clamped} to i32");
                        if (floatBits == 16) {
                            var wide = Fresh();
                            var scaled = Fresh();
                            Line($"{wide} = fpext half {operands[0]} to float");
                            Line($"{scaled} = call float @llvm.ldexp.f32.i32(float {wide}, i32 {power})");
                            Line($"{Result()} = fptrunc float {scaled} to half");
                            return true;
                        }

                        Line($"{Result()} = call {t} @llvm.ldexp.{suffix}.i32({t} {operands[0]}, i32 {power})");
                        return true;
                    }

                    // frexp's significand is in [0.5, 1); its exponent, one
                    // less, is the one for [1, 2). A zero, an infinity or a
                    // NaN has none: Int.min for a zero and Int.max for the
                    // others, as C's 'ilogb' and .NET's 'ILogB' say.
                    //
                    // A half's is the float's it widens to exactly: LLVM 18
                    // expands a half's frexp, where the machine has no half
                    // arithmetic (AArch64 without FP16), into a 16-bit shift
                    // its instruction selection cannot then select.
                    case "exponent": {
                        var parts = Fresh();
                        var exponent = Fresh();
                        var less = Fresh();
                        var wide = Fresh();
                        var magnitude = Fresh();
                        var finite = Fresh();
                        var isZero = Fresh();
                        var unlessZero = Fresh();
                        var (frexpType, frexpSuffix, frexpOperand) = (t, suffix, operands[0]);
                        if (floatBits == 16) {
                            frexpOperand = Fresh();
                            (frexpType, frexpSuffix) = ("float", "f32");
                            Line($"{frexpOperand} = fpext half {operands[0]} to float");
                        }

                        Line($"{parts} = call {{ {frexpType}, i32 }} @llvm.frexp.{frexpSuffix}.i32({frexpType} {frexpOperand})");
                        Line($"{exponent} = extractvalue {{ {frexpType}, i32 }} {parts}, 1");
                        Line($"{less} = sub i32 {exponent}, 1");
                        Line($"{wide} = sext i32 {less} to i64");
                        Line($"{magnitude} = call {t} @llvm.fabs.{suffix}({t} {operands[0]})");
                        Line($"{finite} = fcmp olt {t} {magnitude}, {FloatInfinity}");
                        Line($"{isZero} = fcmp oeq {t} {magnitude}, {FloatZero}");
                        Line($"{unlessZero} = select i1 {finite}, i64 {wide}, i64 {long.MaxValue}");
                        Line($"{Result()} = select i1 {isZero}, i64 {long.MinValue}, i64 {unlessZero}");
                        return true;
                    }

                    // The next float up: a NaN or +infinity is itself, a zero
                    // of either sign the least positive float, and otherwise
                    // the bits counted one away from zero for a positive
                    // value, one toward it for a negative.
                    case "next_up": {
                        var bits = Fresh();
                        var up = Fresh();
                        var down = Fresh();
                        var positive = Fresh();
                        var stepped = Fresh();
                        var isZero = Fresh();
                        var fromZero = Fresh();
                        var isStuck = Fresh();
                        var value = Fresh();
                        Line($"{bits} = bitcast {t} {operands[0]} to {bitsType}");
                        Line($"{up} = add {bitsType} {bits}, 1");
                        Line($"{down} = sub {bitsType} {bits}, 1");
                        Line($"{positive} = fcmp ogt {t} {operands[0]}, {FloatZero}");
                        Line($"{stepped} = select i1 {positive}, {bitsType} {up}, {bitsType} {down}");
                        Line($"{isZero} = fcmp oeq {t} {operands[0]}, {FloatZero}");
                        Line($"{fromZero} = select i1 {isZero}, {bitsType} 1, {bitsType} {stepped}");
                        Line($"{value} = bitcast {bitsType} {fromZero} to {t}");
                        Line($"{isStuck} = fcmp ueq {t} {operands[0]}, {FloatInfinity}");
                        Line($"{Result()} = select i1 {isStuck}, {t} {operands[0]}, {t} {value}");
                        return true;
                    }
                }

                return false;
            }

            var (bitWidth, signed) = SilType.IntegerInfo(type)!.Value;
            var s = signed ? "s" : "u";
            switch (op) {
                case "count_leading_zeros" or "count_trailing_zeros" or "count_ones": {
                    var intrinsic = op switch { "count_leading_zeros" => "ctlz", "count_trailing_zeros" => "cttz", _ => "ctpop" };
                    var counted = bitWidth == 64 ? Result() : Fresh();
                    Line(intrinsic == "ctpop"
                        ? $"{counted} = call {t} @llvm.ctpop.{t}({t} {operands[0]})"
                        : $"{counted} = call {t} @llvm.{intrinsic}.{t}({t} {operands[0]}, i1 false)");
                    if (bitWidth != 64) {
                        Line($"{Result()} = {(bitWidth < 64 ? "zext" : "trunc")} {t} {counted} to i64");
                    }

                    return true;
                }

                // A byte has nothing to swap with.
                case "byte_swap" when bitWidth == 8:
                    _names[builtin] = operands[0];
                    return true;

                case "byte_swap" or "bit_reverse":
                    Line($"{Result()} = call {t} @llvm.{(op == "byte_swap" ? "bswap" : "bitreverse")}.{t}({t} {operands[0]})");
                    return true;

                // A funnel shift of the value with itself, which takes the
                // count modulo the width: the Int's low bits, as the width
                // is a power of two, whatever its sign.
                case "rotate_left" or "rotate_right": {
                    var count = operands[1];
                    if (bitWidth != 64) {
                        count = Fresh();
                        Line($"{count} = {(bitWidth < 64 ? "trunc" : "zext")} i64 {operands[1]} to {t}");
                    }

                    Line($"{Result()} = call {t} @llvm.{(op == "rotate_left" ? "fshl" : "fshr")}.{t}({t} {operands[0]}, {t} {operands[0]}, {t} {count})");
                    return true;
                }

                case "add_overflow" or "sub_overflow" or "mul_overflow": {
                    var pair = Fresh();
                    var value = Fresh();
                    var overflowed = Fresh();
                    Line($"{pair} = call {{ {t}, i1 }} {OverflowFunction($"{s}{op[..3]}", t)}({t} {operands[0]}, {t} {operands[1]})");
                    Line($"{value} = extractvalue {{ {t}, i1 }} {pair}, 0");
                    Line($"{overflowed} = extractvalue {{ {t}, i1 }} {pair}, 1");
                    var tuple = (TupleType)builtin.Type;
                    var layout = LayoutOf(tuple);
                    var first = Fresh();
                    Line($"{first} = insertvalue {Type(tuple)} {(layout.Explicit ? "zeroinitializer" : "poison")}, {t} {value}, {layout.Slots[0]}");
                    Line($"{Result()} = insertvalue {Type(tuple)} {first}, i1 {overflowed}, {layout.Slots[1]}");
                    return true;
                }

                case "add_saturating" or "sub_saturating":
                    Line($"{Result()} = call {t} @llvm.{s}{op[..3]}.sat.{t}({t} {operands[0]}, {t} {operands[1]})");
                    return true;

                // The product at twice the width, shifted down by one.
                case "mul_high": {
                    var wide = $"i{bitWidth * 2}";
                    var left = Fresh();
                    var right = Fresh();
                    var product = Fresh();
                    var high = Fresh();
                    var extend = signed ? "sext" : "zext";
                    Line($"{left} = {extend} {t} {operands[0]} to {wide}");
                    Line($"{right} = {extend} {t} {operands[1]} to {wide}");
                    Line($"{product} = mul {wide} {left}, {right}");
                    Line($"{high} = {(signed ? "ashr" : "lshr")} {wide} {product}, {bitWidth}");
                    Line($"{Result()} = trunc {wide} {high} to {t}");
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        ///     The sized integers and F32 (Int and F64 have their own cases):
        ///     arithmetic that traps on overflow, as Int's does, comparisons
        ///     by signedness, and conversions between any two numeric types
        ///     that trap when the value does not fit.
        /// </summary>
        bool Numeric(BuiltinInst builtin, string[] operands, Func<string> Result) {
            var name = builtin.Name;
            if (name.StartsWith("convert_", StringComparison.Ordinal)) {
                if (name is "convert_Int_F64" or "convert_F64_Int") {
                    return false;
                }

                Convert(builtin.Operands[0].Type, builtin.Type, operands[0], Result());
                return true;
            }

            if (name.StartsWith("wrap_", StringComparison.Ordinal)) {
                Wrap(builtin.Operands[0].Type, builtin.Type, operands[0], Result());
                return true;
            }

            // overflow-checks = false: LLVM's own arithmetic, which wraps.
            if (name.StartsWith(SilBuiltins.WrappingPrefix, StringComparison.Ordinal)) {
                var wt = Type(builtin.Type);
                var wresult = Result();
                switch (name[SilBuiltins.WrappingPrefix.Length..name.LastIndexOf('_')]) {
                    case "neg":
                        Line($"{wresult} = sub {wt} 0, {operands[0]}");
                        break;
                    case "abs": {
                        var negated = Fresh();
                        var negative = Fresh();
                        Line($"{negated} = sub {wt} 0, {operands[0]}");
                        Line($"{negative} = icmp slt {wt} {operands[0]}, 0");
                        Line($"{wresult} = select i1 {negative}, {wt} {negated}, {wt} {operands[0]}");
                        break;
                    }
                    case var arithmetic:
                        Line($"{wresult} = {arithmetic} {wt} {operands[0]}, {operands[1]}");
                        break;
                }

                return true;
            }

            var separator = name.LastIndexOf('_');
            if (separator < 0) {
                return false;
            }

            var op = name[..separator];
            var type = SilType.Numbers.FirstOrDefault(n => n.ToString() == name[(separator + 1)..]);
            if (type is null || type == SilType.Int || type == SilType.F64) {
                return false;
            }

            var t = Type(type);
            var result = Result();

            // A BF16 is computed as the float it widens to exactly and
            // narrowed back, as the interpreter computes it: a float has room
            // enough that the two roundings never disagree.
            if (type == SilType.BF16) {
                string Wide(string operand) => Bf16ToFloat(operand);

                switch (op) {
                    case "add" or "sub" or "mul" or "div" or "rem" or "min" or "max": {
                        var (a, b) = (Wide(operands[0]), Wide(operands[1]));
                        var computed = Fresh();
                        Line(op is "min" or "max"
                            ? $"{computed} = call float @llvm.{(op == "min" ? "minnum" : "maxnum")}.f32(float {a}, float {b})"
                            : $"{computed} = f{op} float {a}, {b}");
                        FloatToBf16(computed, result);
                        return true;
                    }

                    case "neg" or "abs": {
                        var a = Wide(operands[0]);
                        var computed = Fresh();
                        Line(op == "neg" ? $"{computed} = fneg float {a}" : $"{computed} = call float @llvm.fabs.f32(float {a})");
                        FloatToBf16(computed, result);
                        return true;
                    }

                    case "print" or "string_from": {
                        var a = Wide(operands[0]);
                        Line(op == "print"
                            ? $"call void @sable_print_bf16(float {a})"
                            : $"{result} = call ptr @sable_string_from_bf16(float {a})");
                        return true;
                    }
                }

                if (op.StartsWith("cmp_", StringComparison.Ordinal)) {
                    var (a, b) = (Wide(operands[0]), Wide(operands[1]));
                    var predicate = op[4..] switch { "eq" => "oeq", "ne" => "une", "lt" => "olt", "le" => "ole", "gt" => "ogt", _ => "oge" };
                    Line($"{result} = fcmp {predicate} float {a}, {b}");
                    return true;
                }

                return false;
            }

            if (SilType.FloatBits(type) is { } floatBits) {
                var suffix = $"f{floatBits}";
                switch (op) {
                    case "add" or "sub" or "mul" or "div" or "rem":
                        Line($"{result} = f{op} {t} {operands[0]}, {operands[1]}");
                        return true;
                    case "neg":
                        Line($"{result} = fneg {t} {operands[0]}");
                        return true;
                    case "abs":
                        Line($"{result} = call {t} @llvm.fabs.{suffix}({t} {operands[0]})");
                        return true;
                    case "min" or "max":
                        Line($"{result} = call {t} @llvm.{(op == "min" ? "minnum" : "maxnum")}.{suffix}({t} {operands[0]}, {t} {operands[1]})");
                        return true;

                    // The runtime takes an F16 as the float it widens to
                    // exactly, and writes the digits an F16 needs.
                    case "print" or "string_from": {
                        var value = operands[0];
                        if (type == SilType.F16) {
                            value = Fresh();
                            Line($"{value} = fpext half {operands[0]} to float");
                        }

                        Line(op == "print"
                            ? $"call void @sable_print_{suffix}(float {value})"
                            : $"{result} = call ptr @sable_string_from_{suffix}(float {value})");
                        return true;
                    }
                }

                if (op.StartsWith("cmp_", StringComparison.Ordinal)) {
                    var predicate = op[4..] switch { "eq" => "oeq", "ne" => "une", "lt" => "olt", "le" => "ole", "gt" => "ogt", _ => "oge" };
                    Line($"{result} = fcmp {predicate} {t} {operands[0]}, {operands[1]}");
                    return true;
                }

                return false;
            }

            var (bits, signed) = SilType.IntegerInfo(type)!.Value;
            var s = signed ? "s" : "u";
            var min = signed ? (BigInteger.One << (bits - 1)) * -1 : BigInteger.Zero;
            switch (op) {
                case "add" or "sub" or "mul":
                    Checked($"{s}{op}", t, operands[0], operands[1], result);
                    return true;

                case "neg":
                    Checked($"{s}sub", t, "0", operands[0], result);
                    return true;

                case "div" or "rem": {
                    var zero = Fresh();
                    Line($"{zero} = icmp eq {t} {operands[1]}, 0");
                    TrapIf(zero, "division by zero");
                    if (signed) {
                        var isMin = Fresh();
                        var isMinusOne = Fresh();
                        var both = Fresh();
                        Line($"{isMin} = icmp eq {t} {operands[0]}, {min}");
                        Line($"{isMinusOne} = icmp eq {t} {operands[1]}, -1");
                        Line($"{both} = and i1 {isMin}, {isMinusOne}");
                        TrapIf(both, "integer overflow");
                    }

                    Line($"{result} = {s}{op} {t} {operands[0]}, {operands[1]}");
                    return true;
                }

                case "shl" or "shr": {
                    var outside = Fresh();
                    Line($"{outside} = icmp uge {t} {operands[1]}, {bits}");
                    TrapIf(outside, "shift out of range");
                    var instruction = op == "shl" ? "shl" : signed ? "ashr" : "lshr";
                    Line($"{result} = {instruction} {t} {operands[0]}, {operands[1]}");
                    return true;
                }

                case "and" or "or" or "xor":
                    Line($"{result} = {op} {t} {operands[0]}, {operands[1]}");
                    return true;

                case "not":
                    Line($"{result} = xor {t} {operands[0]}, -1");
                    return true;

                case "abs": {
                    var isMin = Fresh();
                    Line($"{isMin} = icmp eq {t} {operands[0]}, {min}");
                    TrapIf(isMin, "integer overflow");
                    var negated = Fresh();
                    var negative = Fresh();
                    Line($"{negated} = sub {t} 0, {operands[0]}");
                    Line($"{negative} = icmp slt {t} {operands[0]}, 0");
                    Line($"{result} = select i1 {negative}, {t} {negated}, {t} {operands[0]}");
                    return true;
                }

                case "min" or "max": {
                    var less = Fresh();
                    Line($"{less} = icmp {s}lt {t} {operands[0]}, {operands[1]}");
                    var (whenLess, otherwise) = op == "min" ? (operands[0], operands[1]) : (operands[1], operands[0]);
                    Line($"{result} = select i1 {less}, {t} {whenLess}, {t} {otherwise}");
                    return true;
                }

                // 128 bits go to the runtime as two halves, low first.
                case "print" or "string_from" when bits == 128: {
                    var low = Fresh();
                    var shifted = Fresh();
                    var high = Fresh();
                    Line($"{low} = trunc i128 {operands[0]} to i64");
                    Line($"{shifted} = lshr i128 {operands[0]}, 64");
                    Line($"{high} = trunc i128 {shifted} to i64");
                    var function = $"{(op == "print" ? "sable_print" : "sable_string_from")}_{(signed ? "i128" : "u128")}";
                    Line(op == "print"
                        ? $"call void @{function}(i64 {low}, i64 {high})"
                        : $"{result} = call ptr @{function}(i64 {low}, i64 {high})");
                    return true;
                }

                case "print" or "string_from": {
                    var wide = Widen(t, bits, signed, operands[0]);
                    var unsignedWide = !signed && bits == 64;
                    if (op == "print") {
                        Line($"call void @sable_print_{(unsignedWide ? "uint" : "int")}(i64 {wide})");
                    } else {
                        Line($"{result} = call ptr @sable_string_from_{(unsignedWide ? "uint" : "int")}(i64 {wide})");
                    }

                    return true;
                }
            }

            if (op.StartsWith("cmp_", StringComparison.Ordinal)) {
                var relation = op[4..];
                var predicate = relation is "eq" or "ne" ? relation : s + relation[0] + relation[1..];
                Line($"{result} = icmp {predicate} {t} {operands[0]}, {operands[1]}");
                return true;
            }

            return false;
        }

        string Widen(string t, int bits, bool signed, string value) {
            if (bits == 64) {
                return value;
            }

            var wide = Fresh();
            Line($"{wide} = {(signed ? "sext" : "zext")} {t} {value} to i64");
            return wide;
        }

        /// <summary>
        ///     The function a checked operation calls: LLVM's intrinsic, except a
        ///     signed I128 multiply, which LLVM lowers to compiler-rt's
        ///     <c>__muloti4</c>, a function libgcc on AArch64 Linux does not
        ///     have; it calls <c>sable_smul_overflow_i128</c>, defined in every
        ///     module over an unsigned multiply of the sizes, which LLVM writes
        ///     out. (Not a multiply checked by a division back: LLVM sees what
        ///     that is, and calls <c>__muloti4</c> for it.)
        /// </summary>
        static string OverflowFunction(string intrinsic, string t) =>
            intrinsic == "smul" && t == "i128" ? "@sable_smul_overflow_i128" : $"@llvm.{intrinsic}.with.overflow.{t}";

        /// <summary>Checked arithmetic at any width.</summary>
        void Checked(string intrinsic, string t, string left, string right, string result) {
            var pair = Fresh();
            var overflowed = Fresh();
            Line($"{pair} = call {{ {t}, i1 }} {OverflowFunction(intrinsic, t)}({t} {left}, {t} {right})");
            Line($"{overflowed} = extractvalue {{ {t}, i1 }} {pair}, 1");
            TrapIf(overflowed, "integer overflow");
            Line($"{result} = extractvalue {{ {t}, i1 }} {pair}, 0");
        }

        /// <summary>A numeric conversion: trapping when the value does not fit the target.</summary>
        void Convert(SilType from, SilType to, string value, string result) {
            var fromType = Type(from);
            var toType = Type(to);
            var fromInteger = SilType.IntegerInfo(from);
            var toInteger = SilType.IntegerInfo(to);

            // A BF16 is converted as the double it widens to exactly, and
            // to one through a double, rounded once from there (an integer
            // past a double's 53 bits rounded twice, as the interpreter does):
            // nothing is asked of the machine's bfloat but the conversions.
            if (from == SilType.BF16 && to != SilType.BF16) {
                if (to == SilType.F64) {
                    Line($"{result} = fpext float {Bf16ToFloat(value)} to double");
                    return;
                }

                var wide = Bf16ToDouble(value);
                Convert(SilType.F64, to, wide, result);
                return;
            }

            if (to == SilType.BF16 && from != SilType.BF16) {
                var wide = value;
                if (from != SilType.F64) {
                    wide = Fresh();
                    Convert(from, SilType.F64, value, wide);
                }

                DoubleToBf16(wide, result);
                return;
            }

            if (fromInteger is var (fromBits, fromSigned) && toInteger is var (toBits, toSigned)) {
                // Through i129, which holds every value of every type here,
                // a U128's as well as an I128's.
                var wide = Fresh();
                Line($"{wide} = {(fromSigned ? "sext" : "zext")} {fromType} {value} to i129");
                var (low, high) = Bounds(toBits, toSigned);
                var under = Fresh();
                var over = Fresh();
                var outside = Fresh();
                Line($"{under} = icmp slt i129 {wide}, {low}");
                Line($"{over} = icmp sgt i129 {wide}, {high}");
                Line($"{outside} = or i1 {under}, {over}");
                TrapIf(outside, $"{from} does not fit in {to}");
                Line($"{result} = trunc i129 {wide} to {toType}");
                return;
            }

            // A 128-bit integer to a half is a call to compiler-rt's
            // '__floattihf', which not every C library has. An F16 overflows
            // past 65520, so a value of up to 2^17 either way goes through an
            // i32 and a float, both exact, and is rounded once; any other is
            // as far past the F16's greatest value, an infinity of its sign.
            if (fromInteger is (128, var signedWide) && to == SilType.F16) {
                var small = Fresh();
                var narrow = Fresh();
                var asFloat = Fresh();
                var rounded = Fresh();
                var negative = Fresh();
                var infinite = Fresh();
                if (signedWide) {
                    var biased = Fresh();
                    Line($"{biased} = add i128 {value}, 131072");
                    Line($"{small} = icmp ule i128 {biased}, 262144");
                } else {
                    Line($"{small} = icmp ule i128 {value}, 131072");
                }

                Line($"{narrow} = trunc i128 {value} to i32");
                Line($"{asFloat} = {(signedWide ? "sitofp" : "uitofp")} i32 {narrow} to float");
                Line($"{rounded} = fptrunc float {asFloat} to half");
                Line($"{negative} = icmp slt i128 {value}, 0");
                Line($"{infinite} = select i1 {(signedWide ? negative : "false")}, half 0xHFC00, half 0xH7C00");
                Line($"{result} = select i1 {small}, half {rounded}, half {infinite}");
                return;
            }

            if (fromInteger is var (_, signedSource) && toInteger is null) {
                Line($"{result} = {(signedSource ? "sitofp" : "uitofp")} {fromType} {value} to {toType}");
                return;
            }

            if (fromInteger is null && toInteger is var (bits, signed)) {
                // Out of range, or NaN, has no integer; fptosi would give poison.
                var (low, high) = Bounds(bits, signed);
                var lowFloat = FloatBound(low, from);
                var highFloat = FloatBound(high + 1, from);
                var aboveLow = Fresh();
                var belowHigh = Fresh();
                var inside = Fresh();
                var outside = Fresh();
                Line($"{aboveLow} = fcmp oge {fromType} {value}, {lowFloat}");
                Line($"{belowHigh} = fcmp olt {fromType} {value}, {highFloat}");
                Line($"{inside} = and i1 {aboveLow}, {belowHigh}");
                Line($"{outside} = xor i1 {inside}, true");
                TrapIf(outside, $"{from} does not fit in {to}");
                Line($"{result} = {(signed ? "fptosi" : "fptoui")} {fromType} {value} to {toType}");
                return;
            }

            Line($"{result} = {(SilType.FloatBits(from) < SilType.FloatBits(to) ? "fpext" : "fptrunc")} {fromType} {value} to {toType}");
        }

        /// <summary>
        ///     <c>unchecked as</c>: an integer keeps its low bits, extended by its
        ///     own sign when it widens; a float to an integer goes toward zero
        ///     and saturates at the target's bounds, NaN giving zero. The other
        ///     conversions cannot fail, and are the checked ones.
        /// </summary>
        void Wrap(SilType from, SilType to, string value, string result) {
            var fromType = Type(from);
            var toType = Type(to);
            var fromInteger = SilType.IntegerInfo(from);
            var toInteger = SilType.IntegerInfo(to);

            // A BF16 is converted as the double it widens to exactly, and
            // to one through a double, rounded once from there (an integer
            // past a double's 53 bits rounded twice, as the interpreter does):
            // nothing is asked of the machine's bfloat but the conversions.
            if (from == SilType.BF16 && to != SilType.BF16) {
                if (to == SilType.F64) {
                    Line($"{result} = fpext float {Bf16ToFloat(value)} to double");
                    return;
                }

                var wide = Bf16ToDouble(value);
                Wrap(SilType.F64, to, wide, result);
                return;
            }

            if (to == SilType.BF16 && from != SilType.BF16) {
                var wide = value;
                if (from != SilType.F64) {
                    wide = Fresh();
                    Wrap(from, SilType.F64, value, wide);
                }

                DoubleToBf16(wide, result);
                return;
            }

            if (fromInteger is var (fromBits, fromSigned) && toInteger is var (toBits, _)) {
                var op = toBits < fromBits ? "trunc" : toBits > fromBits ? (fromSigned ? "sext" : "zext") : "bitcast";
                Line($"{result} = {op} {fromType} {value} to {toType}");
                return;
            }

            if (fromInteger is null && toInteger is var (bits, signed)) {
                // fptosi of a value out of range is poison, but 'select'
                // passes on only the operand it picks. The bounds compared
                // are powers of two, which a float holds exactly.
                var (low, high) = Bounds(bits, signed);
                var lowFloat = FloatBound(low, from);
                var pastHighFloat = FloatBound(high + 1, from);

                // As an LLVM constant: the bits, read as signed.
                var highBits = high >= BigInteger.One << (bits - 1) ? high - (BigInteger.One << bits) : high;
                var truncated = Fresh();
                var isNaN = Fresh();
                var below = Fresh();
                var above = Fresh();
                var clampedHigh = Fresh();
                var clamped = Fresh();
                Line($"{truncated} = {(signed ? "fptosi" : "fptoui")} {fromType} {value} to {toType}");
                Line($"{isNaN} = fcmp uno {fromType} {value}, {value}");
                Line($"{below} = fcmp olt {fromType} {value}, {lowFloat}");
                Line($"{above} = fcmp oge {fromType} {value}, {pastHighFloat}");
                Line($"{clampedHigh} = select i1 {above}, {toType} {highBits}, {toType} {truncated}");
                Line($"{clamped} = select i1 {below}, {toType} {low}, {toType} {clampedHigh}");
                Line($"{result} = select i1 {isNaN}, {toType} 0, {toType} {clamped}");
                return;
            }

            Convert(from, to, value, result);
        }

        static (BigInteger Low, BigInteger High) Bounds(int bits, bool signed) =>
            signed
                ? (-(BigInteger.One << (bits - 1)), (BigInteger.One << (bits - 1)) - 1)
                : (BigInteger.Zero, (BigInteger.One << bits) - 1);

        static string Hex(double value) => $"0x{BitConverter.DoubleToInt64Bits(value):X16}";

        /// <summary>
        ///     An integer bound, a power of two, as a constant of the float
        ///     type compared with it: an infinity of its sign where the type
        ///     cannot reach it (an F16 past 65504, an F32 at 2^128), which
        ///     compares with every finite value of the type as the bound would.
        /// </summary>
        static string FloatZero => Hex(0.0);

        static string FloatInfinity => Hex(double.PositiveInfinity);

        static string FloatBound(BigInteger bound, SilType type) {
            var value = (double)bound;
            var largest = type == SilType.F16 ? (double)Half.MaxValue : type == SilType.F32 ? float.MaxValue : double.MaxValue;
            return Hex(Math.Abs(value) > largest ? value < 0 ? double.NegativeInfinity : double.PositiveInfinity : value);
        }

        /// <summary>An array's count: none for the empty array, which is null; else read from its header.</summary>
        string Count(string array) {
            var isNull = Fresh();
            var safe = Fresh();
            var field = Fresh();
            var stored = Fresh();
            var count = Fresh();
            Line($"{isNull} = icmp eq ptr {array}, null");
            Line($"{safe} = select i1 {isNull}, ptr @sable_empty_array_count, ptr {array}");
            Line($"{field} = getelementptr inbounds %SableArrayHeader, ptr {safe}, i32 0, i32 1");
            Line($"{stored} = load i64, ptr {field}, !tbaa {TbaaHeader}");
            Line($"{count} = select i1 {isNull}, i64 0, i64 {stored}");
            return count;
        }

        /// <summary>
        ///     Room for one more element: in place when the array is the only
        ///     reference to a buffer with room left, through the runtime —
        ///     which copies or grows it — otherwise.
        /// </summary>
        string AppendSlot(string slot, ArrayType arrayType) {
            var array = Fresh();
            var isNull = Fresh();
            Line($"{array} = load ptr, ptr {slot}, !tbaa {TbaaReference}");
            Line($"{isNull} = icmp eq ptr {array}, null");
            var check = NewLabel("room");
            var fast = NewLabel("append");
            var slow = NewLabel("grow");
            var done = NewLabel("appended");
            Line($"br i1 {isNull}, label %{slow}, label %{check}");

            Continue(check);
            var countField = Fresh();
            var count = Fresh();
            var capacityField = Fresh();
            var capacity = Fresh();
            var strongField = Fresh();
            var room = Fresh();
            Line($"{countField} = getelementptr inbounds %SableArrayHeader, ptr {array}, i32 0, i32 1");
            Line($"{count} = load i64, ptr {countField}, !tbaa {TbaaHeader}");
            Line($"{capacityField} = getelementptr inbounds %SableArrayHeader, ptr {array}, i32 0, i32 2");
            Line($"{capacity} = load i64, ptr {capacityField}, !tbaa {TbaaHeader}");
            Line($"{room} = icmp ult i64 {count}, {capacity}");
            Line($"{strongField} = getelementptr inbounds %SableObjectHeader, ptr {array}, i32 0, i32 1");
            BranchIfSole(room, strongField, fast, slow, shared: false);

            Continue(fast);
            var elementsField = Fresh();
            var elements = Fresh();
            var direct = Fresh();
            var grown = Fresh();
            Line($"{elementsField} = getelementptr inbounds %SableArrayHeader, ptr {array}, i32 0, i32 3");
            Line($"{elements} = load ptr, ptr {elementsField}, !tbaa {TbaaHeader}");
            Line($"{direct} = {ElementAt(arrayType.Element, elements, count)}");
            Line($"{grown} = add nuw i64 {count}, 1");
            Line($"store i64 {grown}, ptr {countField}, !tbaa {TbaaHeader}");
            Line($"br label %{done}");

            Continue(slow);
            var called = Fresh();
            Line($"{called} = call ptr @sable_array_append_slot(ptr {slot}, ptr {ArrayMetadataOf(arrayType)})");
            Line($"br label %{done}");

            Continue(done);
            var element = Fresh();
            Line($"{element} = phi ptr [ {direct}, %{fast} ], [ {called}, %{slow} ]");
            return element;
        }

        /// <summary>
        ///     The last element, moved out, for <c>removeLast</c>: the count
        ///     lowered in place when the array is the only reference to a
        ///     buffer that is not empty, through the runtime — which copies
        ///     it, or traps — otherwise. A stack's pop is then a few
        ///     instructions, as an append is.
        /// </summary>
        string LastSlot(string slot, ArrayType arrayType) {
            var array = Fresh();
            var isNull = Fresh();
            Line($"{array} = load ptr, ptr {slot}, !tbaa {TbaaReference}");
            Line($"{isNull} = icmp eq ptr {array}, null");
            var check = NewLabel("last");
            var fast = NewLabel("pop");
            var slow = NewLabel("popslow");
            var done = NewLabel("popped");
            Line($"br i1 {isNull}, label %{slow}, label %{check}");

            Continue(check);
            var countField = Fresh();
            var count = Fresh();
            var strongField = Fresh();
            var some = Fresh();
            Line($"{countField} = getelementptr inbounds %SableArrayHeader, ptr {array}, i32 0, i32 1");
            Line($"{count} = load i64, ptr {countField}, !tbaa {TbaaHeader}");
            Line($"{some} = icmp sgt i64 {count}, 0");
            Line($"{strongField} = getelementptr inbounds %SableObjectHeader, ptr {array}, i32 0, i32 1");
            BranchIfSole(some, strongField, fast, slow, shared: false);

            Continue(fast);
            var elementsField = Fresh();
            var elements = Fresh();
            var lowered = Fresh();
            var direct = Fresh();
            Line($"{elementsField} = getelementptr inbounds %SableArrayHeader, ptr {array}, i32 0, i32 3");
            Line($"{elements} = load ptr, ptr {elementsField}, !tbaa {TbaaHeader}");
            Line($"{lowered} = sub nsw i64 {count}, 1");
            Line($"store i64 {lowered}, ptr {countField}, !tbaa {TbaaHeader}");
            Line($"{direct} = {ElementAt(arrayType.Element, elements, lowered)}");
            Line($"br label %{done}");

            Continue(slow);
            var called = Fresh();
            SiteForRuntime();
            Line($"{called} = call ptr @sable_array_remove_last(ptr {slot}, ptr {ArrayMetadataOf(arrayType)})");
            Line($"br label %{done}");

            Continue(done);
            var element = Fresh();
            Line($"{element} = phi ptr [ {direct}, %{fast} ], [ {called}, %{slow} ]");
            return element;
        }

        /// <summary>A buffer retained, unless it is an array literal's, which is immortal and is never counted.</summary>
        void RetainBuffer(string buffer) {
            if (!buffer.StartsWith("@.array.", StringComparison.Ordinal)) {
                Line($"call void @sable_retain(ptr {buffer})");
            }
        }

        /// <summary>Part of a slice: 0 its buffer, 1 where its run starts, 2 its count.</summary>
        string SlicePart(string slice, SliceType type, int part) {
            var value = Fresh();
            Line($"{value} = extractvalue {Type(type)} {slice}, {part}");
            return value;
        }

        /// <summary>A slice of its parts.</summary>
        string MakeSlice(SliceType type, string buffer, string start, string count) {
            var llvmType = Type(type);
            var withBuffer = Fresh();
            var withStart = Fresh();
            var whole = Fresh();
            Line($"{withBuffer} = insertvalue {llvmType} undef, ptr {buffer}, 0");
            Line($"{withStart} = insertvalue {llvmType} {withBuffer}, i64 {start}, 1");
            Line($"{whole} = insertvalue {llvmType} {withStart}, i64 {count}, 2");
            return whole;
        }

        /// <summary>
        ///     A slice's element's address, the index checked against the
        ///     slice's count — not its buffer's — inline, with no call. A slice
        ///     with elements has a buffer.
        /// </summary>
        string SliceElement(string slice, string index, SliceType type, bool check = true, int elements = 1) {
            var buffer = SlicePart(slice, type, 0);
            var start = SlicePart(slice, type, 1);
            if (check) {
                IndexTraps(index, SlicePart(slice, type, 2), elements);
            }

            var elementsField = Fresh();
            var first = Fresh();
            var position = Fresh();
            var element = Fresh();
            Line($"{elementsField} = getelementptr inbounds %SableArrayHeader, ptr {buffer}, i32 0, i32 3");
            Line($"{first} = load ptr, ptr {elementsField}, !tbaa {TbaaHeader}");
            Line($"{position} = add i64 {start}, {index}");
            Line($"{element} = {ElementAt(type.Element, first, position)}");
            return element;
        }

        /// <summary>
        ///     A slice's element's address to write: the runtime makes the
        ///     slice the only reference to its run first, and checks the index.
        /// </summary>
        string VectorOperands(string x, string y, SliceType type) =>
            $"ptr {SlicePart(x, type, 0)}, i64 {SlicePart(x, type, 1)}, i64 {SlicePart(x, type, 2)}, ptr {SlicePart(y, type, 0)}, i64 {SlicePart(y, type, 1)}";

        string SliceMutableElement(string slot, string index, SliceType type) {
            var element = Fresh();
            SiteForRuntime();
            Line($"{element} = call ptr @sable_slice_mutable_element(ptr {slot}, ptr {ArrayMetadataOf(new ArrayType(type.Element))}, i64 {index})");
            return element;
        }

        /// <summary>
        ///     An array's place made the only reference to its buffer, the
        ///     check inline — the empty array, or an array of its own kind
        ///     whose buffer has one reference, is unique already — and the
        ///     call only for the rest: a shared buffer, which it copies, or a
        ///     mapped file's bytes, which are never the array's.
        /// </summary>
        void MakeUnique(string slot, ArrayType arrayType) {
            var metadata = ArrayMetadataOf(arrayType);
            var array = Fresh();
            var isNull = Fresh();
            Line($"{array} = load ptr, ptr {slot}, !tbaa {TbaaReference}");
            Line($"{isNull} = icmp eq ptr {array}, null");
            var check = NewLabel("shared");
            var copy = NewLabel("copy");
            var done = NewLabel("unique");
            Line($"br i1 {isNull}, label %{done}, label %{check}");
            Continue(check);
            var typeField = Fresh();
            var type = Fresh();
            var strongField = Fresh();
            var own = Fresh();
            Line($"{typeField} = getelementptr inbounds %SableObjectHeader, ptr {array}, i32 0, i32 0");
            Line($"{type} = load ptr, ptr {typeField}, !tbaa {TbaaHeader}");
            Line($"{own} = icmp eq ptr {type}, {metadata}");
            Line($"{strongField} = getelementptr inbounds %SableObjectHeader, ptr {array}, i32 0, i32 1");
            BranchIfSole(own, strongField, done, copy, shared: false);
            Continue(copy);
            Line($"call void @sable_array_make_unique(ptr {slot}, ptr {metadata})");
            Line($"br label %{done}");
            Continue(done);
        }

        /// <summary>An element's address, its index checked against the count — inline, with no call.</summary>
        string InlineElement(string array, string index, SilType elementType, bool check = true, int elements = 1) {
            // An index proved in range is not checked again: an array with an
            // element in range is not the empty one, null.
            if (check) {
                IndexTraps(index, Count(array), elements);
            }

            var elementsField = Fresh();
            var first = Fresh();
            var element = Fresh();
            Line($"{elementsField} = getelementptr inbounds %SableArrayHeader, ptr {array}, i32 0, i32 3");
            Line($"{first} = load ptr, ptr {elementsField}, !tbaa {TbaaHeader}");
            Line($"{element} = {ElementAt(elementType, first, index)}");
            return element;
        }

        /// <summary>
        ///     An element's address to write: in place when the array is the
        ///     only reference to its buffer and the index is in range, the
        ///     runtime's copy-on-write otherwise.
        /// </summary>
        string MutableElement(string slot, string index, ArrayType arrayType) {
            var array = Fresh();
            var isNull = Fresh();
            Line($"{array} = load ptr, ptr {slot}, !tbaa {TbaaReference}");
            Line($"{isNull} = icmp eq ptr {array}, null");
            var check = NewLabel("unique");
            var fast = NewLabel("inplace");
            var slow = NewLabel("cow");
            var done = NewLabel("element");
            Line($"br i1 {isNull}, label %{slow}, label %{check}");

            Continue(check);
            var countField = Fresh();
            var count = Fresh();
            var strongField = Fresh();
            var inRange = Fresh();
            Line($"{countField} = getelementptr inbounds %SableArrayHeader, ptr {array}, i32 0, i32 1");
            Line($"{count} = load i64, ptr {countField}, !tbaa {TbaaHeader}");
            Line($"{inRange} = icmp ult i64 {index}, {count}");
            Line($"{strongField} = getelementptr inbounds %SableObjectHeader, ptr {array}, i32 0, i32 1");
            BranchIfSole(inRange, strongField, fast, slow, shared: true);

            Continue(fast);
            var elementsField = Fresh();
            var elements = Fresh();
            var direct = Fresh();
            Line($"{elementsField} = getelementptr inbounds %SableArrayHeader, ptr {array}, i32 0, i32 3");
            Line($"{elements} = load ptr, ptr {elementsField}, !tbaa {TbaaHeader}");
            Line($"{direct} = {ElementAt(arrayType.Element, elements, index)}");
            Line($"br label %{done}");

            Continue(slow);
            var copied = Fresh();
            SiteForRuntime();
            Line($"{copied} = call ptr @sable_array_mutable_element(ptr {slot}, ptr {ArrayMetadataOf(arrayType)}, i64 {index})");
            Line($"br label %{done}");

            Continue(done);
            var element = Fresh();
            Line($"{element} = phi ptr [ {direct}, %{fast} ], [ {copied}, %{slow} ]");
            return element;
        }

        /// <summary>Checked arithmetic: the intrinsic, then a trap on its overflow flag.</summary>
        void Overflowing(string intrinsic, string left, string right, string result) {
            var pair = Fresh();
            var overflowed = Fresh();
            Line($"{pair} = call {{ i64, i1 }} @llvm.{intrinsic}.with.overflow.i64(i64 {left}, i64 {right})");
            Line($"{overflowed} = extractvalue {{ i64, i1 }} {pair}, 1");
            TrapIf(overflowed, "integer overflow");
            Line($"{result} = extractvalue {{ i64, i1 }} {pair}, 0");
        }
    }
}
