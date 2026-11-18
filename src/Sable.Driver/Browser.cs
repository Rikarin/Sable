using System.Diagnostics;

namespace Sable.Driver;

/// <summary>Opens a file the way <c>sable doc --open</c> wants it: in the user's browser.</summary>
public static class Browser {
    /// <summary>
    ///     Opens <paramref name="path" /> with <c>$BROWSER</c> when it is set
    ///     (as Cargo does), else with the system's opener: <c>open</c> on
    ///     macOS, <c>xdg-open</c> on Linux, the shell on Windows. Returns the
    ///     exit code to end with: 0, or 1 having said why it could not.
    /// </summary>
    public static int Open(string path) {
        var start = Environment.GetEnvironmentVariable("BROWSER") is { Length: > 0 } browser ? new ProcessStartInfo(browser)
            : OperatingSystem.IsMacOS() ? new ProcessStartInfo("open")
            : OperatingSystem.IsWindows() ? new ProcessStartInfo(path) { UseShellExecute = true }
            : new ProcessStartInfo("xdg-open");
        if (!start.UseShellExecute) {
            start.ArgumentList.Add(path);
        }

        try {
            using var process = Process.Start(start);
            process?.WaitForExit();
            if (process is { ExitCode: not 0 }) {
                Console.Error.WriteLine($"sable: '{start.FileName}' could not open {path}");
                return 1;
            }

            return 0;
        } catch (System.ComponentModel.Win32Exception) {
            Console.Error.WriteLine($"sable: cannot run '{start.FileName}' to open {path}; set BROWSER to a program that opens it");
            return 1;
        }
    }
}
