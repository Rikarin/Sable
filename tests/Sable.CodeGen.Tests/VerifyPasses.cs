using System.Runtime.CompilerServices;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Every compilation the tests make verifies its SIL after every pass
///     (docs/04 §2.1), as a debug build of the compiler does: in process,
///     and in the <c>sable</c> processes they start, which inherit it.
/// </summary>
static class VerifyPasses {
    [ModuleInitializer]
    internal static void AfterEach() => Environment.SetEnvironmentVariable("SABLE_VERIFY_PASSES", "1");
}
