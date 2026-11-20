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
    const string TbaaRoot = "!9000000", TbaaHeaderType = "!9000001", TbaaElementType = "!9000002";
    const string TbaaHeader = "!9000003", TbaaElement = "!9000004";

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
        _inherited = null;
        _pointerBytes = _arch == "wasm32" ? 4 : 8;
        _checkExclusivity = module.CheckExclusivity;
        _debug?.Begin(module);

        // Only what the program reaches is built: the library's other functions
        // are not. One unit of a separately compiled program cannot know what
        // the rest reaches, so it builds everything it has.
        _reachable = module.Unit is null ? SilPassManager.Reachable(module) : null;
        var functions = new StringBuilder();
        foreach (var function in module.Functions.Where(f => _reachable?.Contains(f.Name) ?? true)) {
            functions.Append(new FunctionEmitter(this, function).Emit()).Append('\n');
        }

        if (module.EntryPoint is { } entry && module.Find(entry) is { } main) {
            functions.Append(MainShim(main, keepEntry: _debug is not null, wasm: _arch == "wasm32"));
        }

        if (module.IsLibrary) {
            functions.Append(LibraryLifetime(module));
        }

        var output = new StringBuilder();
        output.Append("; Generated by the Sable compiler from SIL.\n\n");
        output.Append("%SableMetadata = type { ptr, ptr, i64, ptr, ptr, i64, i64, ptr, ptr }\n");
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

        output.Append(Declarations);
        output.Append(IntrinsicDeclarations);
        output.Append(MathDeclarations);
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

        // Type-based alias information (docs/04 §3.8): an array's header —
        // its count, its buffer, its strong count — is never an element, so a
        // store to an element leaves them where LLVM last read them.
        output.Append($"\n{TbaaRoot} = !{{!\"Sable TBAA\"}}\n")
            .Append($"{TbaaHeaderType} = !{{!\"array header\", {TbaaRoot}, i64 0}}\n")
            .Append($"{TbaaElementType} = !{{!\"array element\", {TbaaRoot}, i64 0}}\n")
            .Append($"{TbaaHeader} = !{{{TbaaHeaderType}, {TbaaHeaderType}, i64 0}}\n")
            .Append($"{TbaaElement} = !{{{TbaaElementType}, {TbaaElementType}, i64 0}}\n");

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

    static readonly string Counting = BuildCounting(false);
    static readonly string CheckedCounting = BuildCounting(true);

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
                declare void @sable_release_last(ptr) cold
                {(check ? "declare void @sable_arc_dead(ptr, i32) cold noreturn" : "")}

                define internal void @sable.retain(ptr %object) alwaysinline nounwind {"{"}
                entry:
                  %none = icmp eq ptr %object, null
                  br i1 %none, label %done, label %present
                present:
                  %flagsField = getelementptr inbounds {"{"} ptr, i32, i32 {"}"}, ptr %object, i32 0, i32 2
                  %flags = load atomic i32, ptr %flagsField monotonic, align 4
                  %special = and i32 %flags, 513
                  %ordinary = icmp eq i32 %special, 0
                  %strongField = getelementptr inbounds {"{"} ptr, i32, i32 {"}"}, ptr %object, i32 0, i32 1
                  br i1 %ordinary, label %local, label %unusual, !prof !{"{"}!"branch_weights", i32 1000, i32 1{"}"}
                local:
                  %strong = load i32, ptr %strongField
                {Dead(0)}  %more = add i32 %strong, 1
                  store i32 %more, ptr %strongField
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

                define internal void @sable.release(ptr %object) alwaysinline nounwind {"{"}
                entry:
                  %none = icmp eq ptr %object, null
                  br i1 %none, label %done, label %present
                present:
                  %flagsField = getelementptr inbounds {"{"} ptr, i32, i32 {"}"}, ptr %object, i32 0, i32 2
                  %flags = load atomic i32, ptr %flagsField monotonic, align 4
                  %special = and i32 %flags, 513
                  %ordinary = icmp eq i32 %special, 0
                  %strongField = getelementptr inbounds {"{"} ptr, i32, i32 {"}"}, ptr %object, i32 0, i32 1
                  %counting = load i8, ptr @sable_counting
                  %counts = icmp ne i8 %counting, 0
                  br i1 %ordinary, label %local, label %unusual, !prof !{"{"}!"branch_weights", i32 1000, i32 1{"}"}
                local:
                  %strong = load i32, ptr %strongField
                {Dead(1)}  %less = sub i32 %strong, 1
                  store i32 %less, ptr %strongField
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
                                declare void @sable_retain(ptr)
                                declare void @sable_release(ptr)
                                declare void @sable_destroy_uncounted(ptr)
                                declare void @sable_heap_visit(ptr)
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
                                declare zeroext i1 @sable_io_file_exists(ptr)
                                declare zeroext i1 @sable_io_is_directory(ptr)
                                declare zeroext i1 @sable_io_remove_file(ptr)
                                declare zeroext i1 @sable_io_make_directory(ptr)
                                declare ptr @sable_io_list_directory(ptr)
                                declare ptr @sable_io_environment(ptr)
                                declare i64 @sable_io_argument_count()
                                declare ptr @sable_io_argument(i64)
                                declare zeroext i1 @sable_io_succeeded()
                                declare void @sable_set_arguments(i32, ptr)
                                declare i64 @sable_time_monotonic()
                                declare i64 @sable_time_wall_clock()
                                declare void @sable_time_sleep(i64)
                                declare ptr @sable_process_run(ptr, ptr, ptr)
                                declare i64 @sable_process_status()
                                declare ptr @sable_process_error_output()
                                declare ptr @sable_process_current_directory()
                                declare ptr @sable_process_executable_path()
                                declare zeroext i1 @sable_process_change_directory(ptr)
                                declare i64 @sable_process_id()
                                declare ptr @sable_string_split(ptr, i32)
                                declare i64 @sable_string_hash(ptr, i64)
                                @sable_hash_seed = external global i64
                                declare i32 @getentropy(ptr, i64)
                                declare i32 @rand_s(ptr)
                                declare ptr @sable_string_join(ptr, ptr)
                                declare i64 @sable_string_compare(ptr, ptr)
                                declare i64 @sable_string_find(ptr, ptr, i64)
                                declare ptr @sable_string_from_char(i32)
                                declare ptr @sable_string_from_int(i64)
                                declare ptr @sable_string_from_f64(double)
                                declare ptr @sable_string_from_bool(i1 zeroext)
                                declare zeroext i1 @sable_string_to_int(ptr, ptr)
                                declare zeroext i1 @sable_string_to_f64(ptr, ptr)
                                declare zeroext i1 @sable_string_to_f32(ptr, ptr)
                                declare zeroext i1 @sable_char_is_digit(i32)
                                declare zeroext i1 @sable_char_from_int(i64, ptr)
                                declare i64 @sable_array_count(ptr)
                                declare ptr @sable_array_element(ptr, i64)
                                declare ptr @sable_array_append_slot(ptr nocapture, ptr)
                                declare ptr @sable_array_mutable_element(ptr nocapture, ptr, i64)
                                declare ptr @sable_array_remove_last(ptr nocapture, ptr)
                                declare i64 @sable_array_capacity(ptr)
                                declare void @sable_array_reserve(ptr nocapture, ptr, i64)
                                declare void @sable_array_rotate(ptr nocapture, ptr, i64, i64, i64)
                                declare void @sable_slice_rotate(ptr nocapture, ptr, i64, i64, i64)
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
                                declare void @sable_share(ptr)
                                declare void @sable_task_spawn_cancelled()
                                declare zeroext i1 @sable_actor_hop(ptr)
                                declare ptr @sable_actor_current()
                                declare ptr @sable_waitlist_new()
                                declare void @sable_waitlist_free(ptr)
                                declare void @sable_waitlist_wake(ptr)
                                declare zeroext i1 @sable_task_park(ptr)
                                declare zeroext i1 @sable_task_io_wait(i64, i1 zeroext)
                                declare zeroext i1 @sable_task_blocking(ptr, ptr)
                                declare i64 @sable_net_status()
                                declare i64 @sable_net_listen(ptr, i64)
                                declare i64 @sable_net_port(i64)
                                declare i64 @sable_net_accept(i64)
                                declare i64 @sable_net_connect(ptr, i64)
                                declare zeroext i1 @sable_net_connected(i64)
                                declare ptr @sable_net_read(i64, i64, ptr)
                                declare i64 @sable_net_write(i64, ptr, i64, i64)
                                declare void @sable_net_shutdown(i64)
                                declare void @sable_net_close(i64)
                                declare void @sable_tasks_drain()
                                declare void @sable_task_run_until_main()
                                declare ptr @sable_frame_alloc(i64)
                                declare void @sable_frame_free(ptr)
                                declare void @sable_coverage_hit(i64)
                                declare void @sable_array_make_unique(ptr nocapture, ptr)
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
                                declare { i128, i1 } @llvm.smul.with.overflow.i128(i128, i128)
                                declare { i128, i1 } @llvm.uadd.with.overflow.i128(i128, i128)
                                declare { i128, i1 } @llvm.usub.with.overflow.i128(i128, i128)
                                declare { i128, i1 } @llvm.umul.with.overflow.i128(i128, i128)
                                declare half @llvm.fabs.f16(half)
                                declare half @llvm.minnum.f16(half, half)
                                declare half @llvm.maxnum.f16(half, half)
                                declare void @sable_print_i128(i64, i64)
                                declare void @sable_print_u128(i64, i64)
                                declare void @sable_print_f16(float)
                                declare void @sable_print_char(i32)
                                declare ptr @sable_string_from_i128(i64, i64)
                                declare ptr @sable_string_from_u128(i64, i64)
                                declare ptr @sable_string_from_f16(float)
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
                                declare ptr @sable_string_from_utf8_unchecked(ptr, i64, i64)
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
        return entry.Type.Result == SilType.Int
            ? $"define i32 @{name}(i32 %argc, ptr %argv) {{\n  call void @sable_set_arguments(i32 %argc, ptr %argv)\n  %r = call i64 {call}\n  %c = trunc i64 %r to i32\n  ret i32 %c\n}}\n"
            : $"define i32 @{name}(i32 %argc, ptr %argv) {{\n  call void @sable_set_arguments(i32 %argc, ptr %argv)\n  call void {call}\n  ret i32 0\n}}\n";
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
            .Append("define internal void @\"sable.library.start\"(i32 %argc, ptr %argv) {\nentry:\n  call void @sable_runtime_start()\n")
            .Append(takesArguments ? "  call void @sable_set_arguments(i32 %argc, ptr %argv)\n" : "  call void @sable_set_arguments(i32 0, ptr null)\n");
        if (module.Find(init) is not null && (_reachable?.Contains(init) ?? true)) {
            text.Append($"  call void @{Global(init)}()\n");
        }

        if (module.Find(deinit) is not null && (_reachable?.Contains(deinit) ?? true)) {
            text.Append($"  call void @sable_runtime_at_exit(ptr @{Global(deinit)})\n");
        }

        text.Append("  ret void\n}\n\n")
            .Append("@llvm.global_ctors = appending global [1 x { i32, ptr, ptr }] [{ i32, ptr, ptr } { i32 65535, ptr @\"sable.library.start\", ptr null }]\n");
        return text.Append('\n').ToString();
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
            _ when SilType.IntegerInfo(type) is var (bits, _) => $"i{bits}",
            _ when type == SilType.Char => "i32",
            _ when type == SilType.String => "ptr",
            _ when type == SilType.Unit => "void",
            AddressType or FunctionType or ClassType or ArrayType => "ptr",
            StructType { IsInline: true } or InlinePointerType => "ptr",
            _ when type == SilType.Object || type == SilType.Region => "ptr",
            ThickFunctionType => "{ ptr, ptr }",
            WeakStorageType or UnownedStorageType => "ptr",
            StructType structType => LayoutOf(structType).Llvm,
            TupleType tuple => LayoutOf(tuple).Llvm,
            SliceType slice => Type(slice.Parts),
            EnumType enumType when IsNullable(enumType) => "ptr",
            EnumType enumType when Niche(enumType) is { } niche => niche.Llvm,
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
    static IEnumerable<SilType> StructParts(StructType type) =>
        type.Deinit is null ? type.Fields.Select(static f => f.Type) : [SilType.Bool, ..type.Fields.Select(static f => f.Type)];

    /// <summary>Where a struct's field is in its LLVM type: one further along when there is a live byte.</summary>
    static int FieldSlot(StructType type, int field) => LayoutOf(type).Slots[PartIndex(type, field)];

    /// <summary>Where a struct's field is among its parts: one further along when there is a live byte.</summary>
    static int PartIndex(StructType type, int field) => type.Deinit is null ? field : field + 1;

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

    // ---- Enum layout --------------------------------------------------------
    //
    // An enum is laid out in the least room its cases allow:
    //
    // - An optional reference — one case holding a class reference or a
    //   string, one holding nothing — is the reference itself, null for
    //   none, as C's nullable pointer is: neither is ever null.
    // - One case holding a Bool or a Char and the rest nothing is that
    //   scalar, each other case one of the values it never takes — its
    //   niche: a Bool is a byte, 0 or 1, so 'Bool?' is a byte with none 2;
    //   a Char is a scalar value, never past U+10FFFF, so 'Char?' is four
    //   bytes with none U+110000.
    // - Anything else is a tag — a byte, unless there are more than 256
    //   cases — and room for the largest payload, in units of the most
    //   aligned one, after the tag. An enum of cases alone is its tag.

    static bool IsNullable(EnumType type) =>
        type.Cases.Count == 2
        && type.Cases[1 - SomeCase(type)].Payload is null
        && type.Cases[SomeCase(type)].Payload is { } payload && (payload is ClassType || payload == SilType.String);

    static int SomeCase(EnumType type) => type.Cases[0].Payload is null ? 1 : 0;

    /// <summary>A scalar with values to spare, holding an enum: its LLVM type, its size, and the first value no payload takes.</summary>
    sealed record NicheLayout(int Some, SilType Payload, string Llvm, int Bytes, long Spare) {
        /// <summary>The value a case without a payload is: the spare values, in case order.</summary>
        public long ValueOf(EnumType type, int @case) => Spare + type.Cases.Take(@case).Count(static c => c.Payload is null);
    }

    static NicheLayout? Niche(EnumType type) {
        var payloads = type.Cases.Select(static (c, i) => (c.Payload, i)).Where(static c => c.Payload is not null).ToList();
        if (payloads is not [var (payload, some)] || payload!.IsTrivial is false) {
            return null;
        }

        var empty = type.Cases.Count - 1;
        return payload == SilType.Bool && empty <= 254 ? new(some, payload, "i8", 1, 2)
            : payload == SilType.Char && empty <= 0x7FFFFFFF - 0x110000 ? new(some, payload, "i32", 4, 0x110000)
            : null;
    }

    /// <summary>A tagged enum's tag: a byte, or four for more than 256 cases.</summary>
    static string TagType(EnumType type) => type.Cases.Count <= 256 ? "i8" : "i32";

    static int TagBytes(EnumType type) => type.Cases.Count <= 256 ? 1 : 4;

    /// <summary>A tagged enum's payload area: so many units of the most aligned payload's alignment, room for the largest.</summary>
    static (int Count, int Unit) Area(EnumType type) {
        var payloads = type.Cases.Where(static c => c.Payload is not null).Select(static c => c.Payload!).ToList();
        var size = payloads.Select(Size).DefaultIfEmpty(0).Max();
        if (size == 0) {
            return (0, 1);
        }

        var unit = payloads.Select(Align).Max();
        return ((size + unit - 1) / unit, unit);
    }

    /// <summary>Where a tagged enum's payload area starts: after the tag, aligned for the area.</summary>
    static int AreaOffset(EnumType type) {
        var (_, unit) = Area(type);
        return (TagBytes(type) + unit - 1) / unit * unit;
    }

    static int TaggedSize(EnumType type) {
        var (count, unit) = Area(type);
        var align = Math.Max(unit, TagBytes(type));
        var end = count == 0 ? TagBytes(type) : AreaOffset(type) + count * unit;
        return (end + align - 1) / align * align;
    }

    /// <summary>Bytes a value of the type occupies in memory, as LLVM lays it out.</summary>
    static int Size(SilType type) {
        return type switch {
            _ when type == SilType.Bool => 1,
            _ when type == SilType.Int || type == SilType.F64 => 8,
            _ when type == SilType.F32 => 4,
            _ when type == SilType.F16 => 2,
            _ when SilType.IntegerInfo(type) is var (bits, _) => bits / 8,
            _ when type == SilType.Char => 4,
            _ when type == SilType.String => PointerBytes,
            AddressType or FunctionType or ClassType or ArrayType => PointerBytes,
            StructType { IsInline: true } or InlinePointerType => PointerBytes,
            _ when type == SilType.Object || type == SilType.Region => PointerBytes,
            ThickFunctionType => 2 * PointerBytes,
            WeakStorageType or UnownedStorageType => PointerBytes,
            EnumType enumType when IsNullable(enumType) => PointerBytes,
            EnumType enumType when Niche(enumType) is { } niche => niche.Bytes,
            EnumType enumType => TaggedSize(enumType),
            StructType structType => LayoutOf(structType).Size,
            TupleType tuple => LayoutOf(tuple).Size,
            SliceType slice => Size(slice.Parts),
            _ => 0,
        };
    }

    static int Align(SilType type) =>
        type switch {
            _ when type == SilType.Bool => 1,
            _ when type == SilType.Char => 4,
            _ when type == SilType.F32 => 4,
            _ when type == SilType.F16 => 2,
            _ when SilType.IntegerInfo(type) is var (bits, _) => bits / 8,
            _ when type == SilType.Int || type == SilType.F64 => 8,
            _ when type == SilType.Unit => 1,
            StructType { IsInline: true } => PointerBytes,
            StructType structType => LayoutOf(structType).Align,
            TupleType tuple => LayoutOf(tuple).Align,
            EnumType enumType when IsNullable(enumType) => PointerBytes,
            EnumType enumType when Niche(enumType) is { } niche => niche.Bytes,
            EnumType enumType => Math.Max(Area(enumType).Unit, TagBytes(enumType)),
            _ => PointerBytes,
        };

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
                Witnessing.Share => "sable_share",
                _ => "sable_heap_visit",
            };
            output.Append(indent).Append($"call void @{call}(ptr {value}){dbg}\n");
            return;
        }

        // A weak or unowned reference keeps nothing alive.
        if (type is WeakStorageType or UnownedStorageType) {
            if (what is Witnessing.Visit or Witnessing.Immortal or Witnessing.Share) {
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

        var destroy = $"@\"sable.deinit.{type.Name}\"";
        var objectType = ObjectType(type);
        var body = new StringBuilder($"define internal void {destroy}(ptr %o) {{\nentry:\n");
        for (var level = type; level is not null; level = level.Superclass) {
            if (level.Deinit is { } deinit) {
                body.Append($"  call void @{Global(deinit)}(ptr %o)\n");
            }
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
        // object's size and alignment, its visitor and its type information
        // — and the vtable follows them.
        // A class has no elements to copy: that slot holds its superclass's
        // metadata, which 'is' and 'as?' walk.
        var typeName = TrapMessage(type.Name);
        var superclass = type.Superclass is { } parent ? ClassMetadata(parent) : "null";
        var info = ClassInfo(type, objectType);
        // A method no reachable code can call was not built: its slot is empty.
        var vtable = string.Join(", ", type.VTable.Select(m => _reachable?.Contains(m.Function) == false ? "ptr null" : $"ptr @{Global(m.Function)}"));
        _globals.Append($"{name} = {SharedLinkage} constant {MetadataType(type)} {{ ptr {destroy}, ptr {typeName}, i64 0, ptr {superclass}, ptr null, ")
            .Append($"i64 {SizeOf(objectType)}, i64 {(ObjectLayout(type).Explicit ? ObjectLayout(type).Align.ToString(System.Globalization.CultureInfo.InvariantCulture) : AlignmentOf(objectType))}, ptr {visit}, ptr {info}, [{type.VTable.Count} x ptr] [{vtable}] }}{Comdat(name, SharedLinkage)}\n");
        return name;
    }

    /// <summary>
    ///     A class's type information (docs/06 §6.2, <c>SableTypeInfo</c> in
    ///     the runtime): its kind; a field descriptor per stored field — its
    ///     name, its type's name, its offset in an instance and what it holds;
    ///     and a conformance record per protocol it declares (its
    ///     superclass's records are its superclass's). Built with
    ///     <c>--deny-reflection</c>, the descriptors carry no names.
    /// </summary>
    string ClassInfo(ClassType type, string objectType) {
        var name = $"@\"sable.info.{type.Name}\"";
        var denied = _module?.DenyReflection == true;
        var fields = "null";
        if (type.Fields.Count > 0) {
            fields = $"@\"sable.fields.{type.Name}\"";
            // Made before the array is written: a name adds a global of its own.
            var descriptors = type.Fields.Select((f, i) =>
                $"{FieldDescriptorType} {{ {(denied ? "ptr null, ptr null" : $"ptr {TrapMessage(f.Name)}, ptr {TrapMessage(FieldTypeName(f.Type))}")}, "
                + $"i64 ptrtoint (ptr getelementptr ({objectType}, ptr null, i32 0, i32 {ObjectSlot(type, i)}) to i64), i64 {FieldKind(f.Type)} }}").ToList();
            _globals.Append($"{fields} = {SharedLinkage} constant [{type.Fields.Count} x {FieldDescriptorType}] [{string.Join(", ", descriptors)}]{Comdat(fields, SharedLinkage)}\n");
        }

        var conformances = "null";
        if (type.Conformances.Count > 0) {
            conformances = $"@\"sable.conformances.{type.Name}\"";
            // Made before the record is written: each adds its own globals.
            var records = type.Conformances.Select(p => $"{{ ptr, ptr }} {{ ptr {ProtocolDescriptor(p)}, ptr null }}").ToList();
            _globals.Append($"{conformances} = {SharedLinkage} constant [{type.Conformances.Count} x {{ ptr, ptr }}] [{string.Join(", ", records)}]{Comdat(conformances, SharedLinkage)}\n");
        }

        _globals.Append($"{name} = {SharedLinkage} constant {{ i64, i64, ptr, i64, ptr }} {{ i64 {ClassKind}, i64 {type.Fields.Count}, ptr {fields}, i64 {type.Conformances.Count}, ptr {conformances} }}{Comdat(name, SharedLinkage)}\n");
        return name;
    }

    /// <summary>The runtime's <c>SableField</c>: name, type name, offset, kind.</summary>
    const string FieldDescriptorType = "{ ptr, ptr, i64, i64 }";

    /// <summary>The runtime's <c>SABLE_KIND_CLASS</c>.</summary>
    const int ClassKind = 1;

    /// <summary>
    ///     A protocol's descriptor, <c>SableProtocol</c>: its name. Every unit
    ///     that needs one makes it, and the linker keeps one, so it is the
    ///     protocol's identity to <c>sable_conformance</c>; across images,
    ///     which each have their own, the name is.
    /// </summary>
    internal string ProtocolDescriptor(string protocol) {
        var name = $"@\"sable.protocol.{protocol}\"";
        if (_emitted.Add(name)) {
            var text = TrapMessage(protocol);
            _globals.Append($"{name} = {SharedLinkage} constant {{ ptr }} {{ ptr {text} }}{Comdat(name, SharedLinkage)}\n");
        }

        return name;
    }

    /// <summary>What a field holds, as the runtime's <c>SABLE_FIELD_*</c> says: what the leak report follows.</summary>
    static int FieldKind(SilType type) => type switch {
        WeakStorageType or UnownedStorageType => 3,
        ClassType or ArrayType => 1,
        EnumType enumType when IsNullable(enumType) => 1,
        _ when type == SilType.String || type == SilType.Object => 1,
        _ when type.IsTrivial => 0,
        _ => 2,
    };

    /// <summary>A field's type as a descriptor names it: as Sable writes it, as the debugger shows it.</summary>
    static string FieldTypeName(SilType type) => DebugInfo.DisplayName(type);

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

    sealed class FunctionEmitter(LlvmEmitter module, SilFunction function) {
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
                parameters.Add(argument.Type is AddressType ? $"ptr noalias %p{i}" : $"{Type(argument.Type)} %p{i}");
            }

            foreach (var block in function.Blocks.Skip(1)) {
                foreach (var argument in block.Arguments) {
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
            text.Append("define ").Append(linkage).Append(ReturnType(function.Type)).Append(" @").Append(Global(function.Name))
                .Append('(').Append(string.Join(", ", parameters)).Append(") nounwind")
                .Append(function.Inline switch {
                    SilInline.Always => " alwaysinline",
                    SilInline.Never => " noinline",
                    _ => "",
                })
                .Append(module.Comdat("@" + Global(function.Name), linkage) is { Length: > 0 } ? " comdat" : "");
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
                            if (fieldName.StartsWith('$') || fieldType == SilType.Unit) {
                                continue;
                            }

                            var cut = fieldName.LastIndexOf('$');
                            var shown = cut > 0 && fieldName[(cut + 1)..].All(char.IsAsciiDigit) ? fieldName[..cut] : fieldName;
                            declarations.Add(BoxedType(fieldType) is { } inner
                                ? (Debug.Variable(shown, _scope, file, line, inner), $"DW_OP_deref, DW_OP_plus_uconst, {offset}, DW_OP_deref, DW_OP_plus_uconst, {ObjectHeaderSize}")
                                : (Debug.Variable(shown, _scope, file, line, fieldType), $"DW_OP_deref, DW_OP_plus_uconst, {offset}"));
                        }
                    } else if (i < function.ParameterNames.Count) {
                        declarations.Add((Debug.Variable(function.ParameterNames[i], _scope, file, line, argument.Type, i + 1), ""));
                    } else if (argument.Type == SilType.Object && ContextType(argument) is { } context) {
                        if (BoxedType(context) is { } boxed) {
                            // A protocol witness's context is the box an
                            // existential keeps its value in: that value is its self.
                            declarations.Add((Debug.Variable("self", _scope, file, line, boxed, i + 1), $"DW_OP_deref, DW_OP_plus_uconst, {ObjectHeaderSize}"));
                        } else if (context.Name.EndsWith("$context", StringComparison.Ordinal)) {
                            // A closure's: what it captured, each by its own
                            // name, as if a local — through the box of one
                            // captured by reference.
                            foreach (var (fieldName, fieldType, offset) in ObjectFields(context)) {
                                declarations.Add(BoxedType(fieldType) is { } inner
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
        void NegativeOffsetTraps(string offset) {
            var negative = Fresh();
            Line($"{negative} = icmp slt i64 {offset}, 0");
            TrapIf(negative, "array index out of range");
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

                case BuiltinInst builtin:
                    Builtin(builtin);
                    return;

                case AllocStackInst alloc: {
                    var name = Fresh();
                    _names[alloc] = name;

                    // With debug information, a slot of the entry block is
                    // made before anything else, at the function's own line:
                    // the code that makes it is no statement to stop at, and
                    // a breakpoint on its variable's line must find the
                    // statement, not this.
                    var output = _out;
                    var savedDbg = _dbg;
                    if (_scope is not null && alloc.Block == function.Entry) {
                        _out = _entryAllocas;
                        _dbg = EntryLocation;
                    }

                    Line($"{name} = alloca {PartType(alloc.ElementType)}{StorageAlign(alloc.ElementType)}");

                    // Storage for something with references starts empty: all zeros.
                    if (!alloc.ElementType.IsTrivial) {
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
                        if (alloc.Location is { Scope: > 0 } declared) {
                            _dbg = $", !dbg {Debug!.Location(declared.Line, declared.Column, scope)}";
                        }

                        if (BoxedType(alloc.ElementType) is { } boxed) {
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

                case LoadInst load: {
                    var name = Fresh();
                    _names[load] = name;
                    Line($"{name} = load {Type(load.Type)}, ptr {Name(load.Address)}{AccessAlign(load.Address, load.Type)}");
                    switch (load.Kind) {
                        case LoadKind.Copy:
                            Own(true, load.Type, name);
                            break;
                        case LoadKind.Take:
                            Line($"store {Type(load.Type)} zeroinitializer, ptr {Name(load.Address)}{AccessAlign(load.Address, load.Type)}");
                            break;
                    }

                    return;
                }

                case StoreInst store: {
                    // Assigning destroys what was there, after the new value is in.
                    string? old = null;
                    if (store.Kind == StoreKind.Assign && !store.Value.Type.IsTrivial && store.Address is not AllocStackInst { Unmanaged: true }) {
                        old = Fresh();
                        Line($"{old} = load {Type(store.Value.Type)}, ptr {Name(store.Address)}{AccessAlign(store.Address, store.Value.Type)}");
                    }

                    Line($"store {Type(store.Value.Type)} {Name(store.Value)}, ptr {Name(store.Address)}{AccessAlign(store.Address, store.Value.Type)}");
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

                case LoadBorrowInst borrow: {
                    var name = Fresh();
                    _names[borrow] = name;
                    Line($"{name} = load {Type(borrow.Type)}, ptr {Name(borrow.Address)}{AccessAlign(borrow.Address, borrow.Type)}");
                    return;
                }

                case EndBorrowInst:
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
                    Line($"{old} = load {Type(type)}, ptr {Name(destroy.Address)}{AccessAlign(destroy.Address, type)}");
                    Line($"store {Type(type)} zeroinitializer, ptr {Name(destroy.Address)}{AccessAlign(destroy.Address, type)}");
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
                        Line($"{name} = call ptr @sable_frame_alloc(i64 {size})");
                        return;
                    }

                    if (alloc.OnStack) {
                        // In the frame: the header made there, freed with it.
                        // Aligned as the heap's blocks are, which the layout assumes.
                        var memory = Scratch(ObjectType(alloc.ClassType), HeapAlign);
                        Line($"{name} = call ptr @sable_stack_object(ptr {memory}, ptr {module.ClassMetadata(alloc.ClassType)}, i64 {size})");
                        return;
                    }

                    Line($"{name} = call ptr @sable_alloc(ptr {module.ClassMetadata(alloc.ClassType)}, i64 {size})");
                    return;
                }

                case RefElementAddrInst element: {
                    var name = Fresh();
                    _names[element] = name;
                    var classType = (ClassType)element.Reference.Type;
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
                    // A payload of no bytes, an empty struct's, has no area to go in.
                    if (enumInst.Payload is { } payload && Area(enumInst.EnumType).Count > 0) {
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

            if (split) {
                var edge = NewLabel("edge");
                _extra.Append(edge).Append(":\n  br label %").Append(label).Append('\n');
                from = edge;
            }

            for (var i = 0; i < arguments.Count; i++) {
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

        void SwitchEnum(SwitchEnumInst switchEnum) {
            var enumType = (EnumType)switchEnum.Value.Type;
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
        void SwitchNiche(SwitchEnumInst switchEnum, NicheLayout niche) {
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
                    arms.Add($"{niche.Llvm} {niche.ValueOf(enumType, caseIndex)}, label %{_labels[target]}");
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
            Line($"{isPayload} = icmp ult {niche.Llvm} {value}, {niche.Spare}");
            Line($"br i1 {isPayload}, label %{payloadTarget}, label %{spare}");
            _extra.Append(spare).Append(":\n").Append($"  switch {niche.Llvm} {value}, label %{fallback} [ {string.Join(" ", arms)} ]\n");
        }

        /// <summary>
        ///     What a call calls, and with what: a function as it is; a closure
        ///     by its code, with its context passed after the arguments.
        /// </summary>
        (string Target, string Arguments) Callee(SilValue callee, IEnumerable<SilValue> arguments) {
            var passed = arguments.Select(a => $"{Type(a.Type)} {Aligned(a)}").ToList();
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
            if (argument.Type is not AddressType { Pointee: var pointee } || AddressAlign(argument) >= Align(pointee)) {
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

            if (FloatFunctions.Find(builtin.Name) is var (elementary, floatType)) {
                Line($"{Result()} = {MathCall(elementary, floatType, operands)}");
                return;
            }

            // Bytes read as an integer: the last byte's index checked as an
            // element's would be, and a negative offset refused, so that all
            // of them are in the slice; then one load, aligned or not.
            if (builtin.Name.StartsWith("bytes_load_", StringComparison.Ordinal)) {
                var integer = Type(builtin.Type);
                var width = SilType.IntegerInfo(builtin.Type)!.Value.Bits / 8;
                NegativeOffsetTraps(operands[1]);
                var lastIndex = Fresh();
                Line($"{lastIndex} = add i64 {operands[1]}, {width - 1}");
                var last = SliceElement(operands[0], lastIndex, (SliceType)builtin.Operands[0].Type);
                var first = Fresh();
                Line($"{first} = getelementptr inbounds i8, ptr {last}, i64 {1 - width}");
                Line($"{Result()} = load {integer}, ptr {first}, align 1");
                return;
            }

            // An integer written as bytes: the slice made the only reference to
            // its run and the last byte's index checked, as writing that byte
            // would, then one store.
            if (builtin.Name.StartsWith("bytes_store_", StringComparison.Ordinal)) {
                var integer = Type(builtin.Operands[2].Type);
                var width = SilType.IntegerInfo(builtin.Operands[2].Type)!.Value.Bits / 8;
                NegativeOffsetTraps(operands[1]);
                var lastIndex = Fresh();
                Line($"{lastIndex} = add i64 {operands[1]}, {width - 1}");
                var last = SliceMutableElement(operands[0], lastIndex, (SliceType)((AddressType)builtin.Operands[0].Type).Pointee);
                var first = Fresh();
                Line($"{first} = getelementptr inbounds i8, ptr {last}, i64 {1 - width}");
                Line($"store {integer} {operands[2]}, ptr {first}, align 1");
                return;
            }

            if (builtin.Name.StartsWith("bitcast_", StringComparison.Ordinal)) {
                Line($"{Result()} = bitcast {Type(builtin.Operands[0].Type)} {operands[0]} to {Type(builtin.Type)}");
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

                case "task_run_until_main":
                    Line("call void @sable_task_run_until_main()");
                    return;

                // A value crossing from one task to another: its objects shared.
                case "share":
                    module.Ownership(_out, "  ", Witnessing.Share, builtin.Operands[0].Type, operands[0], _dbg);
                    return;

                // A value stored into an object: shared too, if the object
                // is — its header's flag word, at 12, has SABLE_SHARED (512).
                case "share_into": {
                    var at = Fresh();
                    var flags = Fresh();
                    var bit = Fresh();
                    var shared = Fresh();
                    Line($"{at} = getelementptr inbounds i8, ptr {operands[0]}, i64 12");
                    Line($"{flags} = load atomic i32, ptr {at} monotonic, align 4");
                    Line($"{bit} = and i32 {flags}, 512");
                    Line($"{shared} = icmp ne i32 {bit}, 0");
                    var share = NewLabel("share");
                    var after = NewLabel("shared");
                    Line($"br i1 {shared}, label %{share}, label %{after}");
                    Continue(share);
                    module.Ownership(_out, "  ", Witnessing.Share, builtin.Operands[1].Type, operands[1], _dbg);
                    Line($"br label %{after}");
                    Continue(after);
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

                // Wait lists, by handle: what a channel's receivers park in.
                case "task_waitlist_new": {
                    var list = Fresh();
                    Line($"{list} = call ptr @sable_waitlist_new()");
                    Line($"{Result()} = ptrtoint ptr {list} to i64");
                    return;
                }

                case "task_waitlist_free" or "task_waitlist_wake": {
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

                // Sockets (runtime/net.c), by handle.
                case "net_status":
                    Line($"{Result()} = call i64 @sable_net_status()");
                    return;

                case "net_listen" or "net_connect":
                    Line($"{Result()} = call i64 @sable_{builtin.Name}(ptr {operands[0]}, i64 {operands[1]})");
                    return;

                case "net_port" or "net_accept":
                    Line($"{Result()} = call i64 @sable_{builtin.Name}(i64 {operands[0]})");
                    return;

                case "net_connected":
                    Line($"{Result()} = call zeroext i1 @sable_net_connected(i64 {operands[0]})");
                    return;

                case "net_read":
                    Line($"{Result()} = call ptr @sable_net_read(i64 {operands[0]}, i64 {operands[1]}, ptr {module.ArrayMetadata(new ArrayType(SilType.U8))})");
                    return;

                case "net_write": {
                    var type = (SliceType)builtin.Operands[1].Type;
                    var buffer = SlicePart(operands[1], type, 0);
                    var start = SlicePart(operands[1], type, 1);
                    var count = SlicePart(operands[1], type, 2);
                    Line($"{Result()} = call i64 @sable_net_write(i64 {operands[0]}, ptr {buffer}, i64 {start}, i64 {count})");
                    return;
                }

                case "net_shutdown" or "net_close":
                    Line($"call void @sable_{builtin.Name}(i64 {operands[0]})");
                    return;

                case "async_park": {
                    var list = Fresh();
                    Line($"{list} = inttoptr i64 {operands[0]} to ptr");
                    Line($"{Result()} = call zeroext i1 @sable_task_park(ptr {list})");
                    return;
                }

                case Coverage.Builtin:
                    Line($"call void @sable_coverage_hit(i64 {operands[0]})");
                    return;
            }

            // Asked of the registry: each protocol's descriptor, looked up
            // in the records of the object's class and its superclasses.
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
                    Line($"{strong} = load i32, ptr {strongField}");
                    Line($"{flagsField} = getelementptr inbounds %SableObjectHeader, ptr {objectPointer}, i32 0, i32 2");
                    Line($"{flags} = load i32, ptr {flagsField}");
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

                // 64 bits from the operating system's random source: C's
                // getentropy, or, on Windows, two of the CRT's rand_s.
                case "random_bits": {
                    var bits = Scratch("i64", 8);
                    if (module._windows) {
                        var high = Fresh();
                        Line($"{Fresh()} = call i32 @rand_s(ptr {bits})");
                        Line($"{high} = getelementptr i8, ptr {bits}, i64 4");
                        Line($"{Fresh()} = call i32 @rand_s(ptr {high})");
                    } else {
                        Line($"{Fresh()} = call i32 @getentropy(ptr {bits}, i64 8)");
                    }

                    Line($"{Result()} = load i64, ptr {bits}");
                    return;
                }

                case "string_find":
                    Line($"{Result()} = call i64 @sable_string_find(ptr {operands[0]}, ptr {operands[1]}, i64 {operands[2]})");
                    return;

                case "string_compare":
                    Line($"{Result()} = call i64 @sable_string_compare(ptr {operands[0]}, ptr {operands[1]})");
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

                case "string_cursor_scalar":
                    Line($"{Result()} = call i32 @sable_string_cursor_scalar(ptr {operands[0]}, i64 {operands[1]})");
                    return;

                case "string_cursor_next":
                    Line($"{Result()} = call i64 @sable_string_cursor_next(ptr {operands[0]}, i64 {operands[1]})");
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

                case "string_index_scalar":
                    SiteForRuntime();
                    Line($"{Result()} = call i32 @sable_string_index_scalar(ptr {operands[0]}, i64 {operands[1]})");
                    return;

                case "string_index_after" or "string_index_before":
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

                case "time_monotonic" or "time_wall_clock":
                    Line($"{Result()} = call i64 @sable_{builtin.Name}()");
                    return;

                case "process_run":
                    Line($"{Result()} = call ptr @sable_process_run(ptr {operands[0]}, ptr {operands[1]}, ptr {operands[2]})");
                    return;

                case "process_status" or "process_id":
                    Line($"{Result()} = call i64 @sable_{builtin.Name}()");
                    return;

                case "process_error_output" or "process_current_directory" or "process_executable_path":
                    Line($"{Result()} = call ptr @sable_{builtin.Name}()");
                    return;

                case "process_change_directory":
                    Line($"{Result()} = call zeroext i1 @sable_process_change_directory(ptr {operands[0]})");
                    return;

                case "time_sleep":
                    Line($"call void @sable_time_sleep(i64 {operands[0]})");
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
                    Line($"{ok} = call zeroext i1 @{function}(ptr {operands[0]}, ptr {slot})");
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
                case "array_literal_stack": {
                    var arrayType = (ArrayType)builtin.Type;
                    var layout = $"{{ [48 x i8], [{operands.Length} x {Type(arrayType.Element)}] }}";
                    var memory = Scratch(layout, HeapAlign);
                    Line($"{Result()} = call ptr @sable_stack_array(ptr {memory}, ptr {module.ArrayMetadata(arrayType)}, i64 {operands.Length})");
                    for (var i = 0; i < operands.Length; i++) {
                        var element = Fresh();
                        Line($"{element} = getelementptr inbounds {layout}, ptr {memory}, i32 0, i32 1, i64 {i}");
                        Line($"store {Type(arrayType.Element)} {operands[i]}, ptr {element}, !tbaa {TbaaElement}");
                    }

                    return;
                }

                // Elements that are not all literals: appended, one by one.
                case "array_literal": {
                    var arrayType = (ArrayType)builtin.Type;
                    var slot = Scratch("ptr");
                    Line($"store ptr null, ptr {slot}");
                    for (var i = 0; i < operands.Length; i++) {
                        var element = AppendSlot(slot, arrayType);
                        Line($"store {Type(arrayType.Element)} {operands[i]}, ptr {element}, !tbaa {TbaaElement}");
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
                    var value = Result();
                    Line($"{value} = load {Type(builtin.Type)}, ptr {element}, !tbaa {TbaaElement}");
                    if (builtin.Name == "array_get") {
                        Own(true, builtin.Type, value);
                    }

                    return;
                }

                case "array_append": {
                    var arrayType = (ArrayType)((AddressType)builtin.Operands[0].Type).Pointee;
                    var element = AppendSlot(operands[0], arrayType);
                    Line($"store {Type(arrayType.Element)} {operands[1]}, ptr {element}, !tbaa {TbaaElement}");
                    return;
                }

                // A slice made the only reference to its buffer: the runtime
                // copies its run alone when the buffer is shared.
                case "array_make_unique" when builtin.Operands[0].Type is AddressType { Pointee: SliceType unique }:
                    Line($"call void @sable_slice_make_unique(ptr {operands[0]}, ptr {module.ArrayMetadata(new ArrayType(unique.Element))})");
                    return;

                // Unique already — the loop's 'array_make_unique' made it so —
                // the element written where it is, its index checked against
                // the slice's count, as an array's is: no call per element.
                case "array_set_unique" when builtin.Operands[0].Type is AddressType { Pointee: SliceType unique }: {
                    var elementType = Type(unique.Element);
                    var slice = Fresh();
                    Line($"{slice} = load {Type(unique)}, ptr {operands[0]}");
                    var element = SliceElement(slice, operands[1], unique, check: !builtin.InBounds);
                    var old = Fresh();
                    Line($"{old} = load {elementType}, ptr {element}, !tbaa {TbaaElement}");
                    Line($"store {elementType} {operands[2]}, ptr {element}, !tbaa {TbaaElement}");
                    Own(false, unique.Element, old);
                    return;
                }

                case "array_set" or "array_set_unique" when builtin.Operands[0].Type is AddressType { Pointee: SliceType written }: {
                    var elementType = Type(written.Element);
                    var element = SliceMutableElement(operands[0], operands[1], written);
                    var old = Fresh();
                    Line($"{old} = load {elementType}, ptr {element}");
                    Line($"store {elementType} {operands[2]}, ptr {element}");
                    Own(false, written.Element, old);
                    return;
                }

                case "array_element_addr" when builtin.Operands[0].Type is AddressType { Pointee: SliceType addressed }:
                    _names[builtin] = SliceMutableElement(operands[0], operands[1], addressed);
                    return;

                case "array_make_unique": {
                    var arrayType = (ArrayType)((AddressType)builtin.Operands[0].Type).Pointee;
                    Line($"call void @sable_array_make_unique(ptr {operands[0]}, ptr {module.ArrayMetadata(arrayType)})");
                    return;
                }

                // Unique already: the element written where it is, its index checked.
                case "array_set_unique": {
                    var arrayType = (ArrayType)((AddressType)builtin.Operands[0].Type).Pointee;
                    var elementType = Type(arrayType.Element);
                    var array = Fresh();
                    Line($"{array} = load ptr, ptr {operands[0]}");
                    var element = InlineElement(array, operands[1], arrayType.Element, check: !builtin.InBounds);
                    var old = Fresh();
                    Line($"{old} = load {elementType}, ptr {element}, !tbaa {TbaaElement}");
                    Line($"store {elementType} {operands[2]}, ptr {element}, !tbaa {TbaaElement}");
                    Own(false, arrayType.Element, old);
                    return;
                }

                case "array_set": {
                    var arrayType = (ArrayType)((AddressType)builtin.Operands[0].Type).Pointee;
                    var elementType = Type(arrayType.Element);
                    var element = MutableElement(operands[0], operands[1], arrayType);
                    var old = Fresh();
                    Line($"{old} = load {elementType}, ptr {element}");
                    Line($"store {elementType} {operands[2]}, ptr {element}");
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

                case "array_rotate": {
                    var pointee = ((AddressType)builtin.Operands[0].Type).Pointee;
                    var element = pointee is SliceType rotated ? rotated.Element : ((ArrayType)pointee).Element;
                    var function = pointee is SliceType ? "sable_slice_rotate" : "sable_array_rotate";
                    SiteForRuntime();
                    Line($"call void @{function}(ptr {operands[0]}, ptr {module.ArrayMetadata(new ArrayType(element))}, i64 {operands[1]}, i64 {operands[2]}, i64 {operands[3]})");
                    return;
                }

                case "array_reserve": {
                    var arrayType = (ArrayType)((AddressType)builtin.Operands[0].Type).Pointee;
                    Line($"call void @sable_array_reserve(ptr {operands[0]}, ptr {module.ArrayMetadata(arrayType)}, i64 {operands[1]})");
                    return;
                }

                case "array_remove_last": {
                    var arrayType = (ArrayType)((AddressType)builtin.Operands[0].Type).Pointee;
                    var element = LastSlot(operands[0], arrayType);
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

                // The whole of an array, viewed: its buffer retained, from 0 for its count.
                case "slice_of": {
                    var count = Count(operands[0]);
                    Line($"call void @sable_retain(ptr {operands[0]})");
                    _names[builtin] = MakeSlice((SliceType)builtin.Type, operands[0], "0", count);
                    return;
                }

                // A slice as an array again: its buffer, retained, when it is the
                // whole of it, else a copy of its run (the runtime decides).
                case "array_of_slice" when builtin.Operands[0].Type is ArrayType:
                    Line($"call void @sable_retain(ptr {operands[0]})");
                    _names[builtin] = operands[0];
                    return;

                case "array_of_slice": {
                    var slice = (SliceType)builtin.Operands[0].Type;
                    var (buffer, start, count) = (SlicePart(operands[0], slice, 0), SlicePart(operands[0], slice, 1), SlicePart(operands[0], slice, 2));
                    Line($"{Result()} = call ptr @sable_array_of_slice(ptr {buffer}, i64 {start}, i64 {count}, ptr {module.ArrayMetadata(new ArrayType(slice.Element))})");
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
                    Line($"call void @sable_retain(ptr {held})");
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
                    Line($"{strong} = load i32, ptr {strongField}");
                    Line($"{flagsField} = getelementptr inbounds %SableObjectHeader, ptr {block}, i32 0, i32 2");
                    Line($"{flags} = load i32, ptr {flagsField}");
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
                    Line($"{pair} = call {{ {t}, i1 }} @llvm.{s}{op[..3]}.with.overflow.{t}({t} {operands[0]}, {t} {operands[1]})");
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

        /// <summary>Checked arithmetic at any width.</summary>
        void Checked(string intrinsic, string t, string left, string right, string result) {
            var pair = Fresh();
            var overflowed = Fresh();
            Line($"{pair} = call {{ {t}, i1 }} @llvm.{intrinsic}.with.overflow.{t}({t} {left}, {t} {right})");
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
            Line($"{array} = load ptr, ptr {slot}");
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
            var strong = Fresh();
            var room = Fresh();
            var unique = Fresh();
            var both = Fresh();
            Line($"{countField} = getelementptr inbounds %SableArrayHeader, ptr {array}, i32 0, i32 1");
            Line($"{count} = load i64, ptr {countField}, !tbaa {TbaaHeader}");
            Line($"{capacityField} = getelementptr inbounds %SableArrayHeader, ptr {array}, i32 0, i32 2");
            Line($"{capacity} = load i64, ptr {capacityField}, !tbaa {TbaaHeader}");
            Line($"{room} = icmp ult i64 {count}, {capacity}");
            Line($"{strongField} = getelementptr inbounds %SableObjectHeader, ptr {array}, i32 0, i32 1");
            Line($"{strong} = load i32, ptr {strongField}, !tbaa {TbaaHeader}");
            Line($"{unique} = icmp eq i32 {strong}, 1");
            Line($"{both} = and i1 {room}, {unique}");
            Line($"br i1 {both}, label %{fast}, label %{slow}");

            Continue(fast);
            var elementsField = Fresh();
            var elements = Fresh();
            var direct = Fresh();
            var grown = Fresh();
            Line($"{elementsField} = getelementptr inbounds %SableArrayHeader, ptr {array}, i32 0, i32 3");
            Line($"{elements} = load ptr, ptr {elementsField}, !tbaa {TbaaHeader}");
            Line($"{direct} = getelementptr inbounds {Type(arrayType.Element)}, ptr {elements}, i64 {count}");
            Line($"{grown} = add nuw i64 {count}, 1");
            Line($"store i64 {grown}, ptr {countField}, !tbaa {TbaaHeader}");
            Line($"br label %{done}");

            Continue(slow);
            var called = Fresh();
            Line($"{called} = call ptr @sable_array_append_slot(ptr {slot}, ptr {module.ArrayMetadata(arrayType)})");
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
            Line($"{array} = load ptr, ptr {slot}");
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
            var strong = Fresh();
            var some = Fresh();
            var unique = Fresh();
            var both = Fresh();
            Line($"{countField} = getelementptr inbounds %SableArrayHeader, ptr {array}, i32 0, i32 1");
            Line($"{count} = load i64, ptr {countField}, !tbaa {TbaaHeader}");
            Line($"{some} = icmp sgt i64 {count}, 0");
            Line($"{strongField} = getelementptr inbounds %SableObjectHeader, ptr {array}, i32 0, i32 1");
            Line($"{strong} = load i32, ptr {strongField}, !tbaa {TbaaHeader}");
            Line($"{unique} = icmp eq i32 {strong}, 1");
            Line($"{both} = and i1 {some}, {unique}");
            Line($"br i1 {both}, label %{fast}, label %{slow}");

            Continue(fast);
            var elementsField = Fresh();
            var elements = Fresh();
            var lowered = Fresh();
            var direct = Fresh();
            Line($"{elementsField} = getelementptr inbounds %SableArrayHeader, ptr {array}, i32 0, i32 3");
            Line($"{elements} = load ptr, ptr {elementsField}, !tbaa {TbaaHeader}");
            Line($"{lowered} = sub nsw i64 {count}, 1");
            Line($"store i64 {lowered}, ptr {countField}, !tbaa {TbaaHeader}");
            Line($"{direct} = getelementptr inbounds {Type(arrayType.Element)}, ptr {elements}, i64 {lowered}");
            Line($"br label %{done}");

            Continue(slow);
            var called = Fresh();
            SiteForRuntime();
            Line($"{called} = call ptr @sable_array_remove_last(ptr {slot}, ptr {module.ArrayMetadata(arrayType)})");
            Line($"br label %{done}");

            Continue(done);
            var element = Fresh();
            Line($"{element} = phi ptr [ {direct}, %{fast} ], [ {called}, %{slow} ]");
            return element;
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
        string SliceElement(string slice, string index, SliceType type, bool check = true) {
            var buffer = SlicePart(slice, type, 0);
            var start = SlicePart(slice, type, 1);
            if (check) {
                var count = SlicePart(slice, type, 2);
                var outside = Fresh();
                Line($"{outside} = icmp uge i64 {index}, {count}");
                TrapIf(outside, "array index out of range");
            }

            var elementsField = Fresh();
            var elements = Fresh();
            var position = Fresh();
            var element = Fresh();
            Line($"{elementsField} = getelementptr inbounds %SableArrayHeader, ptr {buffer}, i32 0, i32 3");
            Line($"{elements} = load ptr, ptr {elementsField}, !tbaa {TbaaHeader}");
            Line($"{position} = add i64 {start}, {index}");
            Line($"{element} = getelementptr inbounds {Type(type.Element)}, ptr {elements}, i64 {position}");
            return element;
        }

        /// <summary>
        ///     A slice's element's address to write: the runtime makes the
        ///     slice the only reference to its run first, and checks the index.
        /// </summary>
        string SliceMutableElement(string slot, string index, SliceType type) {
            var element = Fresh();
            SiteForRuntime();
            Line($"{element} = call ptr @sable_slice_mutable_element(ptr {slot}, ptr {module.ArrayMetadata(new ArrayType(type.Element))}, i64 {index})");
            return element;
        }

        /// <summary>An element's address, its index checked against the count — inline, with no call.</summary>
        string InlineElement(string array, string index, SilType elementType, bool check = true) {
            // An index proved in range is not checked again: an array with an
            // element in range is not the empty one, null.
            if (check) {
                var count = Count(array);
                var outside = Fresh();
                Line($"{outside} = icmp uge i64 {index}, {count}");
                TrapIf(outside, "array index out of range");
            }

            var elementsField = Fresh();
            var elements = Fresh();
            var element = Fresh();
            Line($"{elementsField} = getelementptr inbounds %SableArrayHeader, ptr {array}, i32 0, i32 3");
            Line($"{elements} = load ptr, ptr {elementsField}, !tbaa {TbaaHeader}");
            Line($"{element} = getelementptr inbounds {Type(elementType)}, ptr {elements}, i64 {index}");
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
            Line($"{array} = load ptr, ptr {slot}");
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
            var strong = Fresh();
            var inRange = Fresh();
            var unique = Fresh();
            var both = Fresh();
            Line($"{countField} = getelementptr inbounds %SableArrayHeader, ptr {array}, i32 0, i32 1");
            Line($"{count} = load i64, ptr {countField}, !tbaa {TbaaHeader}");
            Line($"{inRange} = icmp ult i64 {index}, {count}");
            Line($"{strongField} = getelementptr inbounds %SableObjectHeader, ptr {array}, i32 0, i32 1");
            Line($"{strong} = load i32, ptr {strongField}, !tbaa {TbaaHeader}");
            Line($"{unique} = icmp eq i32 {strong}, 1");
            Line($"{both} = and i1 {inRange}, {unique}");
            Line($"br i1 {both}, label %{fast}, label %{slow}");

            Continue(fast);
            var elementsField = Fresh();
            var elements = Fresh();
            var direct = Fresh();
            Line($"{elementsField} = getelementptr inbounds %SableArrayHeader, ptr {array}, i32 0, i32 3");
            Line($"{elements} = load ptr, ptr {elementsField}, !tbaa {TbaaHeader}");
            Line($"{direct} = getelementptr inbounds {Type(arrayType.Element)}, ptr {elements}, i64 {index}");
            Line($"br label %{done}");

            Continue(slow);
            var copied = Fresh();
            SiteForRuntime();
            Line($"{copied} = call ptr @sable_array_mutable_element(ptr {slot}, ptr {module.ArrayMetadata(arrayType)}, i64 {index})");
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
