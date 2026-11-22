using System.Runtime.CompilerServices;

namespace Sable.SIL.Tests;

/// <summary>
///     Every compilation the tests make verifies its SIL after every pass
///     (docs/04 §2.1), as a debug build of the compiler does, even when the
///     suite runs against a release build of it, as CI's does.
/// </summary>
static class VerifyPasses {
    [ModuleInitializer]
    internal static void AfterEach() => Environment.SetEnvironmentVariable("SABLE_VERIFY_PASSES", "1");
}
