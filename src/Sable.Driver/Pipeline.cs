using Sable.CodeGen;
using Sable.SIL;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Driver;

/// <summary>What compiling one file produced: the stages' output, or why it stopped.</summary>
public sealed record CompileResult(
    SilModule? Sil,
    string? LlvmIr,
    IReadOnlyList<string> Errors) {
    public bool Succeeded => Errors.Count == 0 && LlvmIr is not null;
}

/// <summary>
///     Source to LLVM IR: parse and type-check, lower to SIL, verify it, write
///     it as LLVM IR. Each stage stops the pipeline when it has something to
///     say, so a later stage never runs on a broken earlier one.
/// </summary>
public static class Pipeline {
    public static CompileResult Compile(SourceText source) {
        var tree = SyntaxTree.Parse(source);
        var compilation = Compilation.Create([tree]);

        var errors = compilation.GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Format(source))
            .ToList();
        if (errors.Count > 0) {
            return new(null, null, errors);
        }

        var (module, lowering) = SilGen.Lower(compilation);
        errors.AddRange(lowering.Select(d => d.Format(source)));
        if (errors.Count > 0) {
            return new(module, null, errors);
        }

        // The verifier runs on everything that leaves the front end: a SIL
        // problem is the compiler's bug, and it must not reach LLVM.
        var problems = SilVerifier.Verify(module);
        if (problems.Count > 0) {
            return new(module, null, [..problems.Select(static p => $"internal error: SIL verification failed: {p}")]);
        }

        if (module.EntryPoint is null) {
            return new(module, null, ["error: no entry point: mark a function '@main' or name one 'main'"]);
        }

        return new(module, LlvmEmitter.Emit(module), []);
    }
}
