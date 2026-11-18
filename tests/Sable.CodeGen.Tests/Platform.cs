using System.Diagnostics;
using System.Text;

namespace Sable.CodeGen.Tests;

/// <summary>What a test needs to know about the machine it runs on.</summary>
internal static class Platform {
    /// <summary>The path an executable built here is written to: on Windows, with <c>.exe</c>, which starting it needs.</summary>
    public static string Executable(string path) => path + NativeTarget.Host.ExecutableSuffix;

    /// <summary>
    ///     An executable, and what its link wrote beside it: on Windows its
    ///     PDB (the link always writes one, for a trap's backtrace).
    /// </summary>
    public static void DeleteExecutable(string path) {
        File.Delete(path);
        if (OperatingSystem.IsWindows()) {
            File.Delete(Path.ChangeExtension(path, ".pdb"));
        }
    }

    /// <summary>
    ///     A process whose redirected streams are read and written as UTF-8,
    ///     which is what a Sable program (and the driver) write whatever the
    ///     console's code page. Left to .NET, Windows reads them in the
    ///     console's code page, and 'é' comes back as two other characters.
    /// </summary>
    public static ProcessStartInfo Utf8(this ProcessStartInfo start) {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (start.RedirectStandardInput) {
            start.StandardInputEncoding = utf8;
        }

        if (start.RedirectStandardOutput) {
            start.StandardOutputEncoding = utf8;
        }

        if (start.RedirectStandardError) {
            start.StandardErrorEncoding = utf8;
        }

        return start;
    }

    /// <summary>A path as a Sable string literal holds it: Windows' backslashes escaped.</summary>
    public static string Literal(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);
}

/// <summary>
///     A fact about something Windows does not have — lldb's Python
///     formatters, a POSIX shell, bytes for arguments — skipped there with why.
/// </summary>
public sealed class PosixFactAttribute : FactAttribute {
    public PosixFactAttribute(string whyNotWindows) {
        if (OperatingSystem.IsWindows()) {
            Skip = "Not on Windows: " + whyNotWindows;
        }
    }
}

/// <summary>A theory skipped on Windows, with why (<see cref="PosixFactAttribute" />).</summary>
public sealed class PosixTheoryAttribute : TheoryAttribute {
    public PosixTheoryAttribute(string whyNotWindows) {
        if (OperatingSystem.IsWindows()) {
            Skip = "Not on Windows: " + whyNotWindows;
        }
    }
}
