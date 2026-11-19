using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Sable.SIL;

/// <summary>
///     The platform's C math library, as the interpreter calls it: the very
///     functions native code links to, so that <c>sin(x)</c> run by the
///     interpreter (a constant, the REPL, a manifest) and compiled give the
///     same bits.
/// </summary>
/// <remarks>
///     .NET's <c>Math</c> is no substitute: it calls libm for some functions
///     but computes others itself (<c>Exp2</c> as a power, <c>ExpM1</c> as
///     <c>Exp(x) - 1</c>, which loses the precision the function is for) and
///     has no gamma or error function at all. The library is libSystem on
///     macOS, libm on Linux and the universal C runtime on Windows; each
///     function is looked up once, by its C name.
/// </remarks>
static class Libm {
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate double Unary64(double x);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate double Binary64(double x, double y);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate float Unary32(float x);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate float Binary32(float x, float y);

    static readonly Lazy<nint> Library = new(Load);
    static readonly ConcurrentDictionary<string, Delegate> Functions = new(StringComparer.Ordinal);

    static nint Load() {
        string[] candidates = OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ? ["/usr/lib/libSystem.B.dylib", "libm.dylib"]
            : OperatingSystem.IsWindows() ? ["ucrtbase.dll", "msvcrt.dll"]
            : ["libm.so.6", "libm.so"];
        foreach (var candidate in candidates) {
            if (NativeLibrary.TryLoad(candidate, out var handle)) {
                return handle;
            }
        }

        throw new SilTrap("the platform's C math library could not be loaded");
    }

    static T Find<T>(string name) where T : Delegate =>
        (T)Functions.GetOrAdd(name, static n => Marshal.GetDelegateForFunctionPointer<T>(
            NativeLibrary.TryGetExport(Library.Value, n, out var address)
                ? address
                : throw new SilTrap($"the C math library has no '{n}'")));

    /// <summary>The builtin applied to its operands: a double for an F64, a float for an F32.</summary>
    public static object Call(FloatFunctions.Function function, SilType type, object?[] operands) {
        var name = FloatFunctions.CName(function, type);
        if (type == SilType.F32) {
            return function.Arity == 1
                ? Find<Unary32>(name)((float)operands[0]!)
                : Find<Binary32>(name)((float)operands[0]!, (float)operands[1]!);
        }

        return function.Arity == 1
            ? Find<Unary64>(name)((double)operands[0]!)
            : Find<Binary64>(name)((double)operands[0]!, (double)operands[1]!);
    }
}
