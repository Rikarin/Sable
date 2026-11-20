using System.Diagnostics;
using System.Runtime.CompilerServices;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Which tests that hold native code and the interpreter to the same
///     answer (<see cref="Programs.AssertAgree" />) build the program
///     natively as well: <c>SABLE_TEST_NATIVE</c>.
/// </summary>
/// <remarks>
///     <para>
///         <c>representative</c>, the default, builds natively only for the
///         tests marked <see cref="NativeAttribute" /> — those about code
///         generation, layout, the ABI, reference counting, the runtime and
///         the platform — and runs the rest in the interpreter alone, which
///         compiles the program once rather than twice and starts no clang.
///         <c>all</c>, which CI sets, builds every one natively too.
///     </para>
///     <para>
///         Tests that only build natively (<see cref="Programs.BuildAndRun" />,
///         <see cref="Harnesses.Native" />) always do.
///     </para>
/// </remarks>
internal static class NativeMode {
    public const string Variable = "SABLE_TEST_NATIVE";

    /// <summary>True when every agreeing test builds natively: <c>SABLE_TEST_NATIVE=all</c>.</summary>
    public static bool All { get; } = Environment.GetEnvironmentVariable(Variable) switch {
        null or "" or "representative" => false,
        "all" => true,
        var other => throw new InvalidOperationException($"{Variable} is '{other}': it is 'all' or 'representative' (the default)"),
    };

    /// <summary>Whether the test running now builds its programs natively as well as interpreting them.</summary>
    public static bool Wanted => All || CalledFromNativeTest();

    /// <summary>
    ///     Whether a test method or class marked <see cref="NativeAttribute" />
    ///     is on the stack: the test's own frame is always there, called by
    ///     xunit through reflection, and a lambda's or an iterator's class is
    ///     nested in the test's.
    /// </summary>
    static bool CalledFromNativeTest() {
        foreach (var frame in new StackTrace().GetFrames()) {
            if (frame.GetMethod() is not { DeclaringType: { } type } method) {
                continue;
            }

            while (type.DeclaringType is { } outer && type.IsDefined(typeof(CompilerGeneratedAttribute), false)) {
                type = outer;
            }

            if (type.Assembly == typeof(NativeMode).Assembly
                && (method.IsDefined(typeof(NativeAttribute), false) || type.IsDefined(typeof(NativeAttribute), false))) {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
///     A test, or a class of them, whose programs are built natively as well
///     as interpreted whatever <see cref="NativeMode" /> says: what it checks
///     is native code's — layout, the ABI, reference counting, the runtime,
///     the platform. Its tests have the trait <c>Category=Native</c>.
/// </summary>
[TraitDiscoverer("Sable.CodeGen.Tests.NativeTraitDiscoverer", "Sable.CodeGen.Tests")]
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class NativeAttribute : Attribute, ITraitAttribute;

/// <summary>Gives a test marked <see cref="NativeAttribute" /> the trait <c>Category=Native</c>.</summary>
public sealed class NativeTraitDiscoverer : ITraitDiscoverer {
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute) => [new("Category", "Native")];
}
