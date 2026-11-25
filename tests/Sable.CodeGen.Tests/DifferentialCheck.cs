using Sable.Driver;
using Sable.SIL;
using Sable.Syntax;

namespace Sable.CodeGen.Tests;

/// <summary>
///     One program run several ways that must agree: what each way of
///     compiling it makes of it, in the SIL interpreter. Shared by the
///     differential tests (<see cref="DifferentialFuzzTests" />, which run it
///     natively too) and the coverage-guided fuzzer (tools/fuzz/Sable.Fuzz).
/// </summary>
public static class DifferentialCheck {
    /// <summary>What a run printed and, if it trapped, what the trap said; or that it ran too long to be told.</summary>
    public sealed record Outcome(string Output, string? Trap, bool TooLong = false) {
        public override string ToString() => TooLong ? "ran too long" : $"{(Trap is null ? "ended" : $"trapped: {Trap}")}, having printed\n{Output}";
    }

    const int StepLimit = 20_000_000;

    static readonly Lazy<LibraryInterface> Core = new(static () =>
        LibraryInterface.Read(Pipeline.CoreInterface(ParseOptions.Default).Write(), Pipeline.CompilerIdentity)
        ?? throw new InvalidOperationException("the core's interface did not read back"));

    static SyntaxTree Parse(string source) => SyntaxTree.Parse(new SourceText(source, "generated.sb"));

    /// <summary>The program as it is lowered, unoptimised: what the others are held to.</summary>
    public static Outcome Reference(string source) => Run(Pipeline.Compile([Parse(source)]), "as it is lowered");

    /// <summary>The other ways, each with its name.</summary>
    public static readonly IReadOnlyList<(string Name, Func<string, Outcome> Run)> Ways = [
        ("unspecialised", static source => {
            using var _ = SilGen.UseGenerics(GenericsMode.Unspecialised);
            return Run(Pipeline.Compile([Parse(source)]), "unspecialised");
        }),
        ("the optimising passes", static source => Run(Pipeline.Compile([Parse(source)], optimised: true), "through the optimising passes")),
        ("the library's interface", static source => Run(Pipeline.CompileAgainst([Parse(source)], [Core.Value]), "against the library's interface")),
    ];

    /// <summary>The program interpreted: an outcome, or an exception when it does not compile, which every generated program should.</summary>
    static Outcome Run(CompileResult result, string how) {
        if (!result.Succeeded) {
            throw new InvalidOperationException($"a generated program does not compile {how}:\n{string.Join("\n", result.Errors)}");
        }

        var interpreter = new SilInterpreter(result.Sil!, StepLimit) { Input = TextReader.Null };
        try {
            interpreter.Run(result.Sil!.EntryPoint!);
        } catch (SilTrap trap) {
            return trap.Message.Contains("step limit", StringComparison.OrdinalIgnoreCase) ? new("", null, true) : new(interpreter.Output.ToString(), trap.Message);
        }

        if (interpreter.LiveObjects != 0) {
            throw new InvalidOperationException($"{interpreter.LiveObjects} object(s) leaked {how}");
        }

        return new(interpreter.Output.ToString(), null);
    }

    /// <summary>Runs a program every way here, and throws when one disagrees with the first.</summary>
    /// <returns>The outcome; null for a program that ran too long to compare.</returns>
    public static Outcome? Check(string source) {
        var reference = Reference(source);
        if (reference.TooLong) {
            return null;
        }

        foreach (var (name, run) in Ways) {
            if (run(source) is { TooLong: false } other && other != reference) {
                throw new InvalidOperationException($"{name} disagrees: the reference {reference}\n{name} {other}\nthe program:\n{source}");
            }
        }

        return reference;
    }
}
