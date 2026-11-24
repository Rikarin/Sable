using System.Runtime.InteropServices;
using System.Text;

namespace Sable.SIL;

/// <summary>
///     The locale the system is set to, asked as the runtime's user_locale asks: not .NET's
///     current culture, which is always invariant in the compiler.
/// </summary>
static class UserLocale {
    const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    internal static string Find() {
        if (OperatingSystem.IsWindows()) {
            var name = new char[85]; // LOCALE_NAME_MAX_LENGTH
            var length = GetUserDefaultLocaleName(name, name.Length);
            return length > 1 ? new string(name, 0, length - 1) : "";
        }

        if (!OperatingSystem.IsMacOS()) {
            return "";
        }

        var locale = CFLocaleCopyCurrent();
        if (locale == 0) {
            return "";
        }

        try {
            var text = new byte[256];
            return CFStringGetCString(CFLocaleGetIdentifier(locale), text, text.Length, 0x08000100) != 0
                ? Encoding.UTF8.GetString(text, 0, Array.IndexOf(text, (byte)0))
                : "";
        } finally {
            CFRelease(locale);
        }
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    static extern int GetUserDefaultLocaleName([Out] char[] name, int length);

    [DllImport(CoreFoundation)]
    static extern nint CFLocaleCopyCurrent();

    [DllImport(CoreFoundation)]
    static extern nint CFLocaleGetIdentifier(nint locale);

    [DllImport(CoreFoundation)]
    static extern byte CFStringGetCString(nint text, byte[] buffer, nint size, uint encoding);

    [DllImport(CoreFoundation)]
    static extern void CFRelease(nint value);
}
