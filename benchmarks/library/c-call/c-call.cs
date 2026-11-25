using System.Runtime.InteropServices;

public static unsafe class CCallBench {
    [DllImport("libc", EntryPoint = "labs"), SuppressGCTransition]
    static extern nint Labs(nint n);

    [DllImport("libc", EntryPoint = "strlen", CharSet = CharSet.Ansi), SuppressGCTransition]
    static extern nuint Length(byte* text);

    [Benchmark("library/c-call")]
    public static string Run() {
        nint total = 0;
        for (nint i = 0; i < 20_000_000; i++) {
            total += Labs(i - 10_000_000);
        }

        // Looked up as the program runs: a call that says nothing of the collector.
        var absolute = (delegate* unmanaged[Cdecl, SuppressGCTransition]<nint, nint>)NativeLibrary.GetExport(NativeLibrary.Load("libc", typeof(CCallBench).Assembly, null), "labs");
        for (nint i = 0; i < 20_000_000; i++) {
            total += absolute(i - 10_000_000);
        }

        nuint length = 0;
        const string text = "a name, as long as many are";
        Span<byte> bytes = stackalloc byte[128];
        for (var i = 0; i < 500_000; i++) {
            var n = System.Text.Encoding.UTF8.GetBytes(text, bytes);
            bytes[n] = 0;
            fixed (byte* start = bytes) {
                length += Length(start);
            }
        }

        return $"{total} {length}";
    }
}
