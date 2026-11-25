using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Port of c-callback.sb.
[Benchmark("library/c-callback")]
static unsafe class LibraryCCallback {
    [DllImport("libc", EntryPoint = "qsort")]
    static extern void Sort(int* start, nuint count, nuint size, delegate* unmanaged[Cdecl]<int*, int*, int> compare);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int ByValue(int* a, int* b) => *a < *b ? -1 : *a > *b ? 1 : 0;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int Step(int n, nint context);

    public static string Run() {
        var numbers = new int[300_000];
        var seed = 12345u;
        for (var i = 0; i < numbers.Length; i++) {
            seed = unchecked(seed * 1664525 + 1013904223);
            numbers[i] = (int)(seed >> 1);
        }

        fixed (int* start = numbers) {
            Sort(start, (nuint)numbers.Length, 4, &ByValue);
        }

        // A closure with what it captures, as C has it: a delegate's pointer.
        long total = 0;
        Step step = (n, context) => {
            total += n;
            return n + 1;
        };
        var call = (delegate* unmanaged[Cdecl]<int, nint, int>)Marshal.GetFunctionPointerForDelegate(step);
        var k = 0;
        while (k < 1_000_000) {
            k = call(k, 0);
        }

        GC.KeepAlive(step);
        return $"{numbers[0]} {numbers[150_000]} {numbers[299_999]} {total}";
    }
}
