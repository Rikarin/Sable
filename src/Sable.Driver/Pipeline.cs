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
    public static CompileResult Compile(SourceText source, string? debugDirectory = null, bool checkExclusivity = true) =>
        Compile([SyntaxTree.Parse(source)], debugDirectory, checkExclusivity);

    /// <summary>
    ///     A program of many files — a package and its dependencies — compiled
    ///     together. The first tree is the one that should hold the entry
    ///     point: '@main', or a function called main, is looked for there first.
    /// </summary>
    /// <param name="optimised">
    ///     An optimised build with debug information: the SIL passes run
    ///     anyway, and the debugger sees what they leave. Without it, a build
    ///     with debug information keeps every local in its slot.
    /// </param>
    public static CompileResult Compile(
        IReadOnlyList<SyntaxTree> program,
        string? debugDirectory = null,
        bool checkExclusivity = true,
        BuildTimings? timings = null,
        bool optimised = false
    ) {
        Compilation compilation;
        List<string> errors;
        using (BuildTimings.Of(timings, "bind")) {
            compilation = CompilationOf(program);
            errors = Errors(compilation, program);
        }

        if (errors.Count > 0) {
            return new(null, null, errors);
        }

        SilModule module;
        IReadOnlyList<Diagnostic> lowering;
        using (BuildTimings.Of(timings, "sil")) {
            (module, lowering) = SilGen.Lower(compilation);
        }

        module.CheckExclusivity = checkExclusivity;
        errors.AddRange(lowering.Select(d => program.Count == 1 ? Format(d, program[0].Text) : $"error[{d.Id}]: {d.Message}"));
        if (errors.Count > 0) {
            return new(module, null, errors);
        }

        // The verifier runs on everything that leaves the front end: a SIL
        // problem is the compiler's bug, and it must not reach LLVM.
        var problems = timings is null ? SilVerifier.Verify(module) : timings.Measure("verify", () => SilVerifier.Verify(module));
        if (problems.Count > 0) {
            return new(module, null, [..problems.Select(static p => $"internal error: SIL verification failed: {p}")]);
        }

        if (module.EntryPoint is null) {
            return new(module, null, ["error: no entry point: mark a function '@main' or name one 'main'"]);
        }

        // The SIL passes, each followed by the verifier. An unoptimised debug
        // build keeps every local in its slot, where the debug information
        // says it is.
        if (debugDirectory is null || optimised) {
            try {
                using var _ = BuildTimings.Of(timings, "passes");
                SilPassManager.Run(module, SilPassManager.Default);
            } catch (SilPassException failure) {
                return new(module, null, [$"internal error: {failure.Message}"]);
            }
        }

        string ir;
        using (BuildTimings.Of(timings, "emit")) {
            ir = debugDirectory is null ? LlvmEmitter.Emit(module) : LlvmEmitter.EmitWithDebugInfo(module, debugDirectory);
        }

        return new(module, ir, []);
    }

    /// <summary>
    ///     Type-checks a program without generating code (<c>sable check</c>):
    ///     the errors, each formatted against the file it is in, the program's
    ///     own files first.
    /// </summary>
    public static IReadOnlyList<string> Check(IReadOnlyList<SyntaxTree> program) => Errors(CompilationOf(program), program);

    /// <summary>
    ///     <c>sable check</c> with the lint set (docs/05 §5.7): the errors, and
    ///     what the lints report over <paramref name="linted" /> — a lint at
    ///     the error level among the errors, the rest as warnings. The lints run
    ///     only on a program that type-checks.
    /// </summary>
    public static (IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings) CheckAndLint(
        IReadOnlyList<SyntaxTree> program,
        LintConfiguration configuration,
        IReadOnlyList<SyntaxTree> linted
    ) {
        var compilation = CompilationOf(program);
        var errors = Errors(compilation, program);
        if (errors.Count > 0) {
            return (errors, []);
        }

        var warnings = new List<string>();
        foreach (var (diagnostic, tree) in Lints.RunByFile(compilation, configuration, linted)) {
            (diagnostic.Severity == DiagnosticSeverity.Error ? errors : warnings).Add(diagnostic.Format(tree.Text));
        }

        return (errors, warnings);
    }

    static List<string> Errors(Compilation compilation, IReadOnlyList<SyntaxTree> program) {
        var order = program.Select(static (tree, index) => (tree, index)).ToDictionary(static p => p.tree, static p => p.index);
        return [
            ..compilation.GetAllDiagnosticsByFile()
                .Where(static d => d.Diagnostic.Severity == DiagnosticSeverity.Error)
                .OrderBy(d => d.Tree is { } t && order.TryGetValue(t, out var index) ? index : int.MaxValue)
                .ThenBy(static d => d.Diagnostic.Span.Start)
                .Select(d => d.Tree is { } file ? d.Diagnostic.Format(file.Text)
                    : program.Count == 1 ? Format(d.Diagnostic, program[0].Text)
                    : $"error[{d.Diagnostic.Id}]: {d.Diagnostic.Message}"),
        ];
    }

    // The standard library the program uses comes with it (docs/04 §4.3).
    static Compilation CompilationOf(IReadOnlyList<SyntaxTree> program) => Compilation.Create([..program, ..Library.For(program)]);

    /// <summary>A diagnostic, against the program's text — or the library's, when it falls outside the program.</summary>
    static string Format(Diagnostic diagnostic, SourceText source) =>
        diagnostic.Span.End <= source.Length
            ? diagnostic.Format(source)
            : $"error[{diagnostic.Id}] in the standard library: {diagnostic.Message}";
}
