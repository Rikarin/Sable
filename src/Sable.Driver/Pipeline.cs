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
    /// <param name="debugDirectory">
    ///     When set, emit DWARF debug information, with source file names
    ///     relative to this directory.
    /// </param>
    /// <param name="checkExclusivity">
    ///     Keep the dynamic exclusivity checks (docs/02 §2.3): on unless a
    ///     release build turns them off.
    /// </param>
    public static CompileResult Compile(SourceText source, string? debugDirectory = null, bool checkExclusivity = true) {
        var tree = SyntaxTree.Parse(source);

        // The standard library the program uses comes with it (docs/04 §4.3).
        var compilation = Compilation.Create([tree, ..Library.For(tree)]);

        var errors = compilation.GetAllDiagnosticsByFile()
            .Where(static d => d.Diagnostic.Severity == DiagnosticSeverity.Error)
            .OrderBy(d => d.Tree == tree ? 0 : 1)
            .ThenBy(static d => d.Diagnostic.Span.Start)
            .Select(d => d.Tree is { } file && file != tree ? d.Diagnostic.Format(file.Text) : Format(d.Diagnostic, source))
            .ToList();
        if (errors.Count > 0) {
            return new(null, null, errors);
        }

        var (module, lowering) = SilGen.Lower(compilation);
        module.CheckExclusivity = checkExclusivity;
        errors.AddRange(lowering.Select(d => Format(d, source)));
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

        // The SIL passes, each followed by the verifier. A debug build keeps
        // every local in its slot, where the debug information says it is.
        if (debugDirectory is null) {
            try {
                SilPassManager.Run(module, SilPassManager.Default);
            } catch (SilPassException failure) {
                return new(module, null, [$"internal error: {failure.Message}"]);
            }
        }

        var ir = debugDirectory is null ? LlvmEmitter.Emit(module) : LlvmEmitter.EmitWithDebugInfo(module, debugDirectory);
        return new(module, ir, []);
    }

    /// <summary>A diagnostic, against the program's text — or the library's, when it falls outside the program.</summary>
    static string Format(Diagnostic diagnostic, SourceText source) =>
        diagnostic.Span.End <= source.Length
            ? diagnostic.Format(source)
            : $"error[{diagnostic.Id}] in the standard library: {diagnostic.Message}";
}
