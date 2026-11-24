using System.Text;
using Sable.SIL;

namespace Sable.CodeGen;

/// <summary>
///     C's calling convention (SYNTAX.md §24): how an <c>@extern</c> function
///     is called, and how an <c>@export</c> one is called by C.
/// </summary>
/// <remarks>
///     <para>
///         Sable's own functions pass a struct as an LLVM aggregate, which is
///         not how C passes one: the platform's ABI classifies it, and passes
///         it in registers of the class it fits, coerced to an integer or a
///         float array, or in memory. So each C function is called through a
///         thunk of Sable's signature, <c>sable.M.f</c>, which passes what it
///         is given as C would, and each exported function is reached
///         through a wrapper of C's signature that calls it. LLVM inlines the
///         thunks; a call costs what the C call costs.
///     </para>
///     <para>
///         The classification is the target's — the one the program is built
///         for, the machine building it unless a target says otherwise:
///         AAPCS64 (Apple's and Linux's arm64), the System V x86-64 ABI, on
///         Windows Microsoft's x64 convention, or WebAssembly's (clang's for
///         WASI: a struct of one scalar as that scalar, any other through
///         memory). An integer narrower than 32 bits, and <c>Bool</c>, is
///         widened by the side the ABI says (<c>signext</c>, <c>zeroext</c>).
///     </para>
///     <para>
///         AAPCS64: a struct of one to four floats or doubles of one kind
///         (a homogeneous floating-point aggregate) is passed in float
///         registers, as <c>[n x double]</c>; another struct of up to 16
///         bytes in one or two integer registers, as <c>i64</c> or
///         <c>[2 x i64]</c>; a larger one as a pointer to a copy, and
///         returned through <c>sret</c>.
///     </para>
///     <para>
///         System V x86-64: a struct of up to 16 bytes is split into
///         eightbytes, each passed in an SSE register when it holds only
///         floats, in an integer register otherwise; a larger one is passed
///         <c>byval</c> on the stack and returned through <c>sret</c>. A
///         struct whose eightbytes no longer all fit in the registers left
///         goes whole on the stack too, as C passes it (<see cref="CArguments" />).
///     </para>
/// </remarks>
public sealed partial class LlvmEmitter {
    enum CPassing {
        /// <summary>In registers, as <see cref="CType.Coerced" />: the value itself for a scalar.</summary>
        Direct,

        /// <summary>A pointer to a copy the caller makes (AAPCS64's large struct).</summary>
        Indirect,

        /// <summary>On the stack, the pointer marked <c>byval</c> (x86-64's large struct).</summary>
        ByValue,
    }

    /// <summary>How C passes one value of a Sable type: how, as what LLVM type, with what attributes.</summary>
    sealed record CType(CPassing Passing, string Coerced, string Attributes);

    /// <summary>The target follows AAPCS64; otherwise the System V x86-64 ABI, or on Windows Microsoft's.</summary>
    bool IsArm64 => _arch == "arm64";

    /// <summary>
    ///     Microsoft's x64 convention: a struct of 1, 2, 4 or 8 bytes is
    ///     passed and returned in an integer register, as an integer of its
    ///     size — floats or not; any other as a pointer to a copy the caller
    ///     makes, and returned through <c>sret</c>.
    /// </summary>
    bool IsWin64 => _windows && _arch == "x86_64";

    /// <summary>
    ///     WebAssembly's C ABI (clang's, for WASI): a struct of one scalar,
    ///     however nested, is passed and returned as that scalar; any other
    ///     is passed <c>byval</c> at its own alignment and returned through
    ///     <c>sret</c>.
    /// </summary>
    bool IsWasm => _arch == "wasm32";

    /// <summary>The alignment of a struct C passes or returns through memory: its own on WebAssembly, at least 8 elsewhere.</summary>
    int MemoryAlign(SilType type) => IsWasm ? Align(type) : Math.Max(8, Align(type));

    /// <summary>
    ///     How C passes each of a function's arguments. On System V x86-64 a
    ///     struct goes in registers only if all of its eightbytes still fit
    ///     in those left — six integer and eight SSE registers, one integer
    ///     register fewer when the result is returned through memory —
    ///     otherwise the whole struct goes on the stack, <c>byval</c>; LLVM
    ///     would split it, part in registers and part on the stack.
    /// </summary>
    List<CType> CArguments(IReadOnlyList<SilType> parameters, bool indirectResult) {
        var arguments = parameters.Select(CArgument).ToList();
        if (IsWin64 || IsArm64 || IsWasm) {
            return arguments;
        }

        var integers = indirectResult ? 5 : 6;
        var sse = 8;
        for (var i = 0; i < arguments.Count; i++) {
            if (arguments[i].Passing != CPassing.Direct) {
                continue;
            }

            if (parameters[i] is StructType structure) {
                var classes = EightByteClasses(structure);
                var (wantedIntegers, wantedSse) = (classes.Count(static c => !c), classes.Count(static c => c));
                if (wantedIntegers > integers || wantedSse > sse) {
                    arguments[i] = new(CPassing.ByValue, "ptr", $"byval({Type(structure)}) align {MemoryAlign(structure)}");
                    continue;
                }

                integers -= wantedIntegers;
                sse -= wantedSse;
            } else if (SilType.IsFloat(parameters[i])) {
                sse = Math.Max(0, sse - 1);
            } else {
                integers = Math.Max(0, integers - (SilType.IntegerInfo(parameters[i]) is (128, _) ? 2 : 1));
            }
        }

        return arguments;
    }

    /// <summary>How C passes a value of the type as an argument, were the registers never to run out.</summary>
    CType CArgument(SilType type) {
        if (type is not StructType structure) {
            return new(CPassing.Direct, Type(type), Extension(type));
        }

        var size = Size(structure);
        if (IsWin64) {
            return size is 1 or 2 or 4 or 8 ? new(CPassing.Direct, $"i{size * 8}", "") : new(CPassing.Indirect, "ptr", "noundef");
        }

        if (IsWasm) {
            return SingleScalar(structure) is { } scalar
                ? new(CPassing.Direct, Type(scalar), "")
                : new(CPassing.ByValue, "ptr", $"byval({Type(structure)}) align {MemoryAlign(structure)}");
        }

        if (IsArm64) {
            if (FloatAggregate(structure) is var (element, count)) {
                return new(CPassing.Direct, $"[{count} x {element}]", "");
            }

            // A struct aligned to 16 goes in an even pair of registers, as an i128.
            return size <= 16 && Align(structure) == 16 ? new(CPassing.Direct, "i128", "")
                : size <= 8 ? new(CPassing.Direct, "i64", "")
                : size <= 16 ? new(CPassing.Direct, "[2 x i64]", "")
                : new(CPassing.Indirect, "ptr", "noundef");
        }

        return size <= 16 && !HasUnalignedFields(structure)
            ? new(CPassing.Direct, EightBytes(structure), "")
            : new(CPassing.ByValue, "ptr", $"byval({Type(structure)}) align {Math.Max(8, Align(structure))}");
    }

    /// <summary>How C returns a value of the type: null when through memory the caller gives (<c>sret</c>).</summary>
    CType? CResult(SilType type) {
        if (type is not StructType structure) {
            return new(CPassing.Direct, Type(type), Extension(type));
        }

        var size = Size(structure);
        if (IsWin64) {
            return size is 1 or 2 or 4 or 8 ? new(CPassing.Direct, $"i{size * 8}", "") : null;
        }

        if (IsWasm) {
            return SingleScalar(structure) is { } scalar ? new(CPassing.Direct, Type(scalar), "") : null;
        }

        if (size > 16 || !IsArm64 && HasUnalignedFields(structure)) {
            return null;
        }

        if (IsArm64) {
            return FloatAggregate(structure) is var (element, count) ? new(CPassing.Direct, $"[{count} x {element}]", "")
                : new(CPassing.Direct, size <= 8 ? "i64" : Align(structure) == 16 ? "i128" : "[2 x i64]", "");
        }

        return new(CPassing.Direct, EightBytes(structure), "");
    }

    /// <summary>The widening C expects of a narrow integer: by its sign; a Bool's by zeros.</summary>
    static string Extension(SilType type) =>
        type == SilType.Bool ? "zeroext"
        : SilType.IntegerInfo(type) is var (bits, signed) && bits < 32 ? signed ? "signext" : "zeroext"
        : "";

    /// <summary>The struct's scalars, in order, each with where it is: what an ABI classifies.</summary>
    static IEnumerable<(SilType Type, int Offset)> Flatten(StructType structure, int at = 0) {
        // At the offsets the layout gives: packed, or padded for '@align'.
        var layout = LayoutOf(structure);
        var parts = StructParts(structure).ToList();
        for (var i = 0; i < parts.Count; i++) {
            var offset = layout.Offsets[i];
            if (parts[i] is StructType { IsInline: false } inner) {
                foreach (var scalar in Flatten(inner, at + offset)) {
                    yield return scalar;
                }
            } else {
                yield return (parts[i], at + offset);
            }
        }
    }

    /// <summary>
    ///     Whether a scalar of the struct is at an offset its type is not
    ///     aligned to — which only <c>@packed</c> does. System V x86-64
    ///     passes such a struct in memory.
    /// </summary>
    static bool HasUnalignedFields(StructType structure) =>
        Flatten(structure).Any(static s => s.Offset % Align(s.Type) != 0);

    /// <summary>AAPCS64's homogeneous floating-point aggregate: one to four of one float type.</summary>
    static (string Element, int Count)? FloatAggregate(StructType structure) {
        var scalars = Flatten(structure).Select(static s => s.Type).ToList();
        // With no padding: an '@align' struct that rounds its floats up is not one.
        return scalars.Count is >= 1 and <= 4 && SilType.IsFloat(scalars[0]) && scalars.TrueForAll(s => s == scalars[0])
               && Size(structure) == scalars.Count * Size(scalars[0])
            ? (Type(scalars[0]), scalars.Count)
            : null;
    }

    /// <summary>WebAssembly: the one scalar a struct holds, however nested; null when it holds more, or none.</summary>
    static SilType? SingleScalar(StructType structure) =>
        Flatten(structure).ToList() is [var (scalar, _)] && Size(scalar) == Size(structure) ? scalar : null;

    /// <summary>System V x86-64: whether each eightbyte of a struct of up to 16 bytes goes in an SSE register (true) or an integer one.</summary>
    static List<bool> EightByteClasses(StructType structure) {
        var size = Size(structure);
        var scalars = Flatten(structure).ToList();
        var classes = new List<bool>();
        for (var start = 0; start < size; start += 8) {
            var end = Math.Min(start + 8, size);
            var inside = scalars.Where(s => s.Offset >= start && s.Offset < end).Select(static s => s.Type).ToList();
            if (start > 0 && !scalars.Any(s => s.Offset < end && s.Offset + Size(s.Type) > start)) {
                break; // padding alone, which an '@align' struct may end with: no class, not passed
            }

            classes.Add(inside.Count > 0 && inside.TrueForAll(static t => SilType.IsFloat(t)));
        }

        return classes;
    }

    /// <summary>System V x86-64: each eightbyte of a struct of up to 16 bytes, as the register class it goes in.</summary>
    static string EightBytes(StructType structure) {
        var size = Size(structure);
        var scalars = Flatten(structure).ToList();
        var parts = new List<string>();
        for (var start = 0; start < size; start += 8) {
            var end = Math.Min(start + 8, size);
            var inside = scalars.Where(s => s.Offset >= start && s.Offset < end).Select(static s => s.Type).ToList();
            if (start > 0 && !scalars.Any(s => s.Offset < end && s.Offset + Size(s.Type) > start)) {
                break;
            }

            if (inside.Count > 0 && inside.TrueForAll(static t => SilType.IsFloat(t))) {
                parts.Add(inside.Count == 2 && inside[0] == SilType.F32 ? "<2 x float>"
                    : inside[0] == SilType.F32 && end - start <= 4 ? "float"
                    : "double");
            } else {
                parts.Add(end - start >= 8 ? "i64" : $"i{(end - start) * 8}");
            }
        }

        return parts.Count == 1 ? parts[0] : $"{{ {string.Join(", ", parts)} }}";
    }

    /// <summary>
    ///     The C functions the module calls: each declared by its C name,
    ///     with a thunk of Sable's signature that calls it as C would.
    /// </summary>
    string ForeignFunctions(SilModule module) {
        var text = new StringBuilder();
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, (symbol, type)) in module.Foreign) {
            var result = CResult(type.Result);

            // A variadic function is C's with its fixed parameters and '...',
            // whatever this call passes after them.
            var fixedCount = module.ForeignVariadic.TryGetValue(name, out var count) ? count : type.Parameters.Length;
            var variadic = fixedCount < type.Parameters.Length || module.ForeignVariadic.ContainsKey(name);
            var arguments = CArguments([..type.Parameters.Take(fixedCount)], indirectResult: result is null);
            var cParameters = new List<string>();
            if (result is null) {
                cParameters.Add($"ptr sret({Type(type.Result)}) align {MemoryAlign(type.Result)}");
            }

            cParameters.AddRange(arguments.Select(static a => $"{a.Coerced}{(a.Attributes.Length > 0 ? " " + a.Attributes : "")}"));
            if (variadic) {
                cParameters.Add("...");
            }

            var cResult = result is null ? "void" : $"{(result.Attributes.Length > 0 ? result.Attributes + " " : "")}{result.Coerced}";

            // The runtime's own functions are declared already, one the module
            // exports is defined here, and a C symbol is declared once.
            if (!Declarations.Contains($"@{symbol}(", StringComparison.Ordinal) && !MathDeclarations.Contains($"@{symbol}(", StringComparison.Ordinal) && !module.Exports.ContainsValue(symbol) && declared.Add(symbol)) {
                text.Append($"declare {cResult} @\"{symbol}\"({string.Join(", ", cParameters)})\n");
            }

            text.Append($"define internal {Type(type.Result)} @{Global(name)}(")
                .Append(string.Join(", ", type.Parameters.Select(static (p, i) => $"{Type(p)} %a{i}")))
                .Append(") alwaysinline {\nentry:\n");

            var passed = new List<string>();
            if (result is null) {
                text.Append($"  %out = alloca {Type(type.Result)}, align {MemoryAlign(type.Result)}\n");
                passed.Add($"ptr sret({Type(type.Result)}) align {MemoryAlign(type.Result)} %out");
            }

            for (var i = 0; i < arguments.Count; i++) {
                var argument = arguments[i];
                var parameter = type.Parameters[i];
                switch (argument.Passing) {
                    case CPassing.Direct when parameter is StructType:
                        text.Append(Spill($"%s{i}", parameter, $"%a{i}"));
                        text.Append($"  %c{i} = load {argument.Coerced}, ptr %s{i}\n");
                        passed.Add($"{argument.Coerced} %c{i}");
                        break;
                    case CPassing.Direct:
                        passed.Add($"{argument.Coerced}{(argument.Attributes.Length > 0 ? " " + argument.Attributes : "")} %a{i}");
                        break;
                    default:
                        text.Append(Spill($"%s{i}", parameter, $"%a{i}"));
                        passed.Add($"ptr {argument.Attributes} %s{i}");
                        break;
                }
            }

            // After the fixed arguments, C's default promotions: an integer
            // narrower than an int, and Bool, as an int; a float as a double.
            for (var i = fixedCount; i < type.Parameters.Length; i++) {
                passed.Add(Promoted(text, type.Parameters[i], $"%a{i}", $"%v{i}"));
            }

            // A variadic callee is called by its type, '...' and all.
            var callee = variadic
                ? $"{result?.Coerced ?? "void"} ({string.Join(", ", (result is null ? new[] { "ptr" } : []).Concat(arguments.Select(static a => a.Coerced)).Append("..."))}) @\"{symbol}\""
                : $"{cResult} @\"{symbol}\"";
            var call = $"call {callee}({string.Join(", ", passed)})";
            if (result is null) {
                text.Append($"  {call}\n  %r = load {Type(type.Result)}, ptr %out\n  ret {Type(type.Result)} %r\n}}\n\n");
            } else if (type.Result == SilType.Unit) {
                text.Append($"  {call}\n  ret void\n}}\n\n");
            } else if (type.Result is StructType) {
                text.Append($"  %c = {call}\n").Append(Spill("%r.s", type.Result, "%c", result.Coerced))
                    .Append($"  %r = load {Type(type.Result)}, ptr %r.s\n  ret {Type(type.Result)} %r\n}}\n\n");
            } else {
                text.Append($"  %r = {call}\n  ret {Type(type.Result)} %r\n}}\n\n");
            }
        }

        return text.ToString();
    }

    /// <summary>An argument to C's '...', as its default promotions pass it, with the instructions that promote it.</summary>
    static string Promoted(StringBuilder text, SilType type, string value, string promoted) {
        if (type == SilType.Bool) {
            text.Append($"  {promoted} = zext i1 {value} to i32\n");
            return $"i32 {promoted}";
        }

        if (SilType.IntegerInfo(type) is var (bits, signed) && bits < 32) {
            text.Append($"  {promoted} = {(signed ? "sext" : "zext")} {Type(type)} {value} to i32\n");
            return $"i32 {promoted}";
        }

        if (type == SilType.BF16) {
            var wide = promoted + ".wide";
            var shifted = promoted + ".shifted";
            var single = promoted + ".single";
            text.Append($"  {wide} = zext i16 {value} to i32\n");
            text.Append($"  {shifted} = shl i32 {wide}, 16\n");
            text.Append($"  {single} = bitcast i32 {shifted} to float\n");
            text.Append($"  {promoted} = fpext float {single} to double\n");
            return $"double {promoted}";
        }

        if (type == SilType.F32 || type == SilType.F16) {
            text.Append($"  {promoted} = fpext {Type(type)} {value} to double\n");
            return $"double {promoted}";
        }

        return $"{Type(type)} {value}";
    }

    /// <summary>
    ///     The module's functions C calls: each a wrapper of C's signature,
    ///     by the C name, that takes what C passes and calls the function.
    /// </summary>
    string ExportedFunctions(SilModule module) {
        var text = new StringBuilder();
        foreach (var (name, symbol) in module.Exports) {
            if (module.Find(name) is not { } function || !(_reachable?.Contains(name) ?? true)) {
                continue;
            }

            var type = function.Type;
            var result = CResult(type.Result);
            var arguments = CArguments(type.Parameters, indirectResult: result is null);
            var cParameters = new List<string>();
            if (result is null) {
                cParameters.Add($"ptr sret({Type(type.Result)}) align {MemoryAlign(type.Result)} %out");
            }

            cParameters.AddRange(arguments.Select(static (a, i) => $"{a.Coerced}{(a.Attributes.Length > 0 ? " " + a.Attributes : "")} %c{i}"));
            var cResult = result is null ? "void" : $"{(result.Attributes.Length > 0 ? result.Attributes + " " : "")}{result.Coerced}";
            // A Windows DLL exports only what is marked so.
            var export = _windows && module.IsLibrary ? "dllexport " : "";
            text.Append($"define {export}{cResult} @\"{symbol}\"({string.Join(", ", cParameters)}) {{\nentry:\n");

            var passed = new List<string>();
            for (var i = 0; i < arguments.Count; i++) {
                var parameter = type.Parameters[i];
                switch (arguments[i].Passing) {
                    case CPassing.Direct when parameter is StructType:
                        text.Append(Spill($"%s{i}", parameter, $"%c{i}", arguments[i].Coerced));
                        text.Append($"  %a{i} = load {Type(parameter)}, ptr %s{i}\n");
                        break;
                    case CPassing.Direct:
                        passed.Add($"{Type(parameter)} %c{i}");
                        continue;
                    default:
                        text.Append($"  %a{i} = load {Type(parameter)}, ptr %c{i}\n");
                        break;
                }

                passed.Add($"{Type(parameter)} %a{i}");
            }

            var call = $"call {Type(type.Result)} @{Global(name)}({string.Join(", ", passed)})";
            if (type.Result == SilType.Unit) {
                text.Append($"  {call}\n  ret void\n}}\n\n");
            } else if (result is null) {
                text.Append($"  %r = {call}\n  store {Type(type.Result)} %r, ptr %out\n  ret void\n}}\n\n");
            } else if (type.Result is StructType) {
                text.Append($"  %r = {call}\n").Append(Spill("%r.s", type.Result, "%r"))
                    .Append($"  %c = load {result.Coerced}, ptr %r.s\n  ret {result.Coerced} %c\n}}\n\n");
            } else {
                text.Append($"  %r = {call}\n  ret {Type(type.Result)} %r\n}}\n\n");
            }
        }

        return text.ToString();
    }

    /// <summary>
    ///     A value stored in a slot of its own, large enough for it and for
    ///     what it is reread as: how a struct becomes the integers or floats
    ///     C passes it in, and back.
    /// </summary>
    static string Spill(string slot, SilType type, string value, string? storedAs = null) {
        var bytes = Math.Max(Size(type), 16);
        var align = Math.Max(Align(type), 16);
        return $"  {slot} = alloca [{bytes} x i8], align {align}\n  store {storedAs ?? Type(type)} {value}, ptr {slot}\n";
    }
}
