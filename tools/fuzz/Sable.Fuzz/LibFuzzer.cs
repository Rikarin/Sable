using System.Runtime.InteropServices;

namespace Sable.Fuzz;

/// <summary>
///     A target under libFuzzer, through libfuzzer-dotnet: libFuzzer runs in a
///     process of its own and starts this one, handing it each input and
///     reading back the coverage map SharpFuzz's instrumentation fills.
/// </summary>
/// <remarks>
///     A failure — anything the target throws — is reported to libFuzzer,
///     which keeps the input as <c>crash-&lt;sha1&gt;</c>; one that takes this
///     process down (a stack overflow) is kept the same way, as libFuzzer
///     sees its child go. A hang is libFuzzer's <c>-timeout</c>, after which
///     libFuzzer exits and leaves this process running the hung input, so a
///     watchdog ends it once its parent has gone.
/// </remarks>
static partial class LibFuzzer {
    [LibraryImport("libc", EntryPoint = "getppid")]
    private static partial int ParentProcess();

    public static int Run(Target target) {
        var parent = ParentProcess();
        var watchdog = new Thread(() => {
            while (true) {
                Thread.Sleep(1000);
                if (ParentProcess() != parent) {
                    Environment.Exit(1);
                }
            }
        }) { IsBackground = true, Name = "libFuzzer watchdog" };
        watchdog.Start();

        SharpFuzz.Fuzzer.LibFuzzer.Run(data => target.Run(data));
        return 0;
    }
}
