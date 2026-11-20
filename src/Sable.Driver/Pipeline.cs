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

    /// <summary>The front end's warnings about the program's own files, whether or not it compiled.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>For a build measured for coverage, the counters it was given.</summary>
    public CoverageMap? Coverage { get; init; }
}

/// <summary>
///     Source to LLVM IR: parse and type-check, lower to SIL, verify it, write
///     it as LLVM IR. Each stage stops the pipeline when it has something to
///     say, so a later stage never runs on a broken earlier one.
/// </summary>
public static partial class Pipeline {
    /// <param name="debugDirectory">
    ///     When set, emit DWARF debug information, with source file names
    ///     relative to this directory.
    /// </param>
    /// <param name="checkExclusivity">
    ///     Keep the dynamic exclusivity checks (docs/02 §2.3): on unless a
    ///     release build turns them off.
    /// </param>
    public static CompileResult Compile(SourceText source, string? debugDirectory = null, bool checkExclusivity = true, bool optimised = false) =>
        Compile([SyntaxTree.Parse(source)], debugDirectory, checkExclusivity, optimised: optimised);

    /// <summary>
    ///     What a program's files are parsed with: <c>DEBUG</c> defined for an
    ///     unoptimised build, <c>RELEASE</c> for an optimised one (SYNTAX.md
    ///     §21), and the package's features.
    /// </summary>
    public static ParseOptions ParseOptionsFor(bool optimise, IEnumerable<string>? features = null) =>
        ParseOptionsFor(optimise, ParseOptions.Default with { Features = [..features ?? []] });

    /// <summary>The options a file already has — its features and workspace — with <c>DEBUG</c> or <c>RELEASE</c> defined.</summary>
    public static ParseOptions ParseOptionsFor(bool optimise, ParseOptions options) =>
        options with { Symbols = [optimise ? "RELEASE" : "DEBUG"] };

    /// <summary>A program's files' options for <paramref name="target" />: <c>#if os(...)</c> and <c>#if arch(...)</c> test it.</summary>
    public static ParseOptions ParseOptionsFor(bool optimise, NativeTarget target) =>
        ParseOptionsFor(optimise, ParseOptions.Default.ForTarget(target.Os, target.Arch));

    /// <summary>
    ///     A program of many files — a package and its dependencies — compiled
    ///     together. The first tree is the one that should hold the entry
    ///     point: '@main', or a function called main, is looked for there first.
    /// </summary>
    /// <param name="optimised">
    ///     An optimised build: the SIL passes that move code between functions
    ///     — devirtualising, inlining — run too, which an unoptimised build
    ///     leaves out so that a trap's backtrace names every frame. With debug
    ///     information, the SIL passes run anyway, and the debugger sees what
    ///     they leave; without it, a build with debug information keeps every
    ///     local in its slot.
    /// </param>
    /// <param name="optLevel">
    ///     The profile's <c>opt-level</c>, which picks the SIL passes
    ///     (<see cref="SilPassManager.ForLevel" />); when null, <c>2</c> for an
    ///     optimised build and <c>0</c> for another.
    /// </param>
    /// <param name="checkArc">
    ///     A profile's <c>arc-checks</c>: compiled code's retains and releases
    ///     trap on an object whose deinit has run (<see cref="SilModule.CheckArc" />).
    /// </param>
    /// <param name="checkOverflow">
    ///     Trap on integer overflow; a profile's <c>overflow-checks = false</c>
    ///     turns it off, and the arithmetic wraps (<see cref="WrappingArithmetic" />).
    /// </param>
    /// <param name="target">
    ///     What the program is built for, whose C calling convention its
    ///     <c>@extern</c> calls follow; the host when null. (What its files'
    ///     <c>#if os(...)</c> says is the parse's: <see cref="ParseOptionsFor(bool, NativeTarget)" />.)
    /// </param>
    /// <param name="library">
    ///     Compile a library, which C calls through its <c>@export</c>
    ///     functions: no entry point is looked for, and its globals are set up
    ///     as it is loaded (<see cref="SilGen.LowerLibraryByFile" />).
    /// </param>
    public static CompileResult Compile(
        IReadOnlyList<SyntaxTree> program,
        string? debugDirectory = null,
        bool checkExclusivity = true,
        BuildTimings? timings = null,
        bool optimised = false,
        NativeTarget? target = null,
        bool checkOverflow = true,
        bool library = false,
        bool denyReflection = false,
        string? optLevel = null,
        bool checkArc = false
    ) {
        var level = optLevel ?? (optimised ? "2" : "0");
        Compilation compilation;
        List<string> errors;
        List<string> warnings;
        LibraryCheck? check;
        Func<SyntaxTree, bool>? standard;
        using (BuildTimings.Of(timings, "bind")) {
            compilation = CompilationOf(program);
            // The standard library's bodies, once it is known to check
            // cleanly with a program that cannot change that, are neither
            // checked again nor lowered beyond what the program reaches. A
            // library C links starts from its exports, and is lowered whole.
            check = library ? null : Library.CheckFor(compilation, program);
            standard = check?.Skipped;
            // The SIL checks run as the program is lowered, below.
            errors = Formatted(standard is null ? compilation.GetAllDiagnosticsByFile() : compilation.GetAllDiagnosticsByFile(t => !standard(t)), program);
            warnings = Warnings(compilation, program);
        }

        // The warnings go with whatever comes back, an error or the IR.
        var result = CompileChecked(compilation, program, errors, debugDirectory, checkExclusivity, timings, level, target, checkOverflow, library, denyReflection, checkArc, standard) with { Warnings = warnings };
        if (standard is null && result.Succeeded) {
            check?.Passed();
        }

        return result;
    }

    /// <summary>The rest of <see cref="Compile(IReadOnlyList{SyntaxTree}, string?, bool, BuildTimings?, bool)" />, from a checked program.</summary>
    static CompileResult CompileChecked(
        Compilation compilation,
        IReadOnlyList<SyntaxTree> program,
        List<string> errors,
        string? debugDirectory,
        bool checkExclusivity,
        BuildTimings? timings,
        string optLevel,
        NativeTarget? target,
        bool checkOverflow,
        bool library,
        bool denyReflection,
        bool checkArc,
        Func<SyntaxTree, bool>? onDemand,
        Func<Compilation, (SilModule, IReadOnlyList<(Diagnostic Diagnostic, SyntaxTree? Tree)>)>? lower = null
    ) {
        if (errors.Count > 0) {
            return new(null, null, errors);
        }

        SilModule module;
        IReadOnlyList<(Diagnostic Diagnostic, SyntaxTree? Tree)> lowering;
        using (BuildTimings.Of(timings, "sil")) {
            (module, lowering) = lower is not null ? lower(compilation)
                : library ? SilGen.LowerLibraryByFile(compilation, denyReflection) : SilGen.LowerByFile(compilation, denyReflection, onDemand);
        }

        module.CheckExclusivity = checkExclusivity;
        module.CheckArc = checkArc;
        if (!checkOverflow) {
            WrappingArithmetic.Apply(module);
        }

        errors.AddRange(Formatted(lowering, program));
        if (errors.Count > 0) {
            return new(module, null, errors);
        }

        // The verifier runs on everything that leaves the front end: a SIL
        // problem is the compiler's bug, and it must not reach LLVM.
        var problems = timings is null ? SilVerifier.Verify(module) : timings.Measure("verify", () => SilVerifier.Verify(module));
        if (problems.Count > 0) {
            return new(module, null, [..problems.Select(static p => $"internal error: SIL verification failed: {p}")]);
        }

        if (module.EntryPoint is null && !library) {
            return new(module, null, ["error: no entry point: mark a function '@main' or name one 'main'"]);
        }

        // The SIL passes the opt-level asks for, each followed by the
        // verifier, over what the program can reach: nothing else is
        // emitted, and what the inliner and the others decide of a function
        // does not then depend on callers no program runs (nor on whether
        // the library's were lowered: LibraryCheck). An unoptimised debug
        // build keeps every local in its slot, where the debug information
        // says it is; an optimised one runs them as any other, and the
        // debugger sees what they leave.
        if (debugDirectory is null || optLevel != "0") {
            try {
                using var _ = BuildTimings.Of(timings, "passes");
                SilPassManager.RemoveUnreachable(module);
                SilPassManager.Run(module, SilPassManager.ForLevel(optLevel));
            } catch (SilPassException failure) {
                return new(module, null, [$"internal error: {failure.Message}"]);
            }
        }

        string ir;
        using (BuildTimings.Of(timings, "emit")) {
            ir = debugDirectory is null
                ? LlvmEmitter.Emit(module, target)
                : LlvmEmitter.EmitWithDebugInfo(module, debugDirectory, target, optimised: optLevel != "0", sources: EmbeddedSources(compilation.SyntaxTrees, program));
        }

        return new(module, ir, []);
    }

    /// <summary>
    ///     The text of each file of the compilation that is not one of the
    ///     program's — the standard library's, embedded in the compiler — by
    ///     its name: what the debug information carries, since no debugger
    ///     could find those files on disk.
    /// </summary>
    public static IReadOnlyDictionary<string, string> EmbeddedSources(IEnumerable<SyntaxTree> compiled, IEnumerable<SyntaxTree> program) {
        var own = program.Select(static t => t.Text.FileName).ToHashSet(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tree in compiled) {
            if (!own.Contains(tree.Text.FileName)) {
                sources.TryAdd(tree.Text.FileName, tree.Text.Text);
            }
        }

        return sources;
    }

    /// <summary>
    ///     Type-checks a program without generating code (<c>sable check</c>):
    ///     the errors, each formatted against the file it is in, the program's
    ///     own files first.
    /// </summary>
    public static IReadOnlyList<string> Check(IReadOnlyList<SyntaxTree> program) => Diagnose(program).Errors;

    /// <summary>Type-checks a program: its errors, and the warnings about its own files.</summary>
    public static (IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings) Diagnose(IReadOnlyList<SyntaxTree> program) {
        var compilation = CompilationOf(program);
        var errors = Errors(compilation, program);
        return (errors.Count > 0 ? errors : ConstantErrors(compilation, program), Warnings(compilation, program));
    }

    /// <summary>
    ///     What computing the constants and running the comptime blocks
    ///     reports (SYNTAX.md §21): part of type-checking, since a program
    ///     whose constant divides by zero is not a program.
    /// </summary>
    static List<string> ConstantErrors(Compilation compilation, IReadOnlyList<SyntaxTree> program) {
        var order = program.Select(static (tree, index) => (tree, index)).ToDictionary(static p => p.tree, static p => p.index);
        return [
            ..SilGen.EvaluateConstants(compilation)
                .Where(static d => d.Diagnostic.Severity == DiagnosticSeverity.Error)
                .OrderBy(d => order.TryGetValue(d.Tree, out var index) ? index : int.MaxValue)
                .ThenBy(static d => d.Diagnostic.Span.Start)
                .Select(d => order.ContainsKey(d.Tree) && d.Diagnostic.Span.End <= d.Tree.Text.Length
                    ? d.Diagnostic.Format(d.Tree.Text)
                    : $"error[{d.Diagnostic.Id}] in the standard library: {d.Diagnostic.Message}"),
        ];
    }

    /// <summary>
    ///     <c>sable check</c> with the lint set (docs/05 §5.7): the errors, the
    ///     compiler's warnings, and what the lints report over
    ///     <paramref name="linted" /> — a lint at the error level among the
    ///     errors, the rest as warnings. The lints run only on a program that
    ///     type-checks; the compiler's warnings are reported either way.
    /// </summary>
    public static (IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings) CheckAndLint(
        IReadOnlyList<SyntaxTree> program,
        LintConfiguration configuration,
        IReadOnlyList<SyntaxTree> linted
    ) {
        var compilation = CompilationOf(program);
        var errors = Errors(compilation, program);
        if (errors.Count == 0) {
            errors = ConstantErrors(compilation, program);
        }

        var warnings = Warnings(compilation, program);
        if (errors.Count > 0) {
            return (errors, warnings);
        }

        foreach (var (diagnostic, tree) in Lints.RunByFile(compilation, configuration, linted)) {
            (diagnostic.Severity == DiagnosticSeverity.Error ? errors : warnings).Add(diagnostic.Format(tree.Text));
        }

        return (errors, warnings);
    }

    /// <summary>
    ///     The type errors; and for a program without any, what the checks
    ///     that run on SIL say — use after move, overlapping accesses, a
    ///     reference that outlives its root — so that <c>sable check</c>, like
    ///     a build, reports them.
    /// </summary>
    static List<string> Errors(Compilation compilation, IReadOnlyList<SyntaxTree> program) {
        var errors = Formatted(compilation.GetAllDiagnosticsByFile(), program);
        if (errors.Count > 0) {
            return errors;
        }

        IReadOnlyList<(Diagnostic, SyntaxTree?)> checks;
        try {
            checks = SilGen.Check(compilation);
        } catch (Exception e) when (e is not OutOfMemoryException) {
            // Lowering's own failure is the build's to report, as an internal error.
            return errors;
        }

        return Formatted(checks, program);
    }

    /// <summary>
    ///     <c>--deny-unsafe</c> (SYNTAX.md §23): each <c>unsafe</c> block and
    ///     <c>unsafe fn</c> in the files given — the program's own; the
    ///     standard library and the dependencies may use <c>unsafe</c> behind
    ///     safe APIs — as an error (S3472), formatted against its file.
    /// </summary>
    public static List<string> UnsafeDenied(IEnumerable<SyntaxTree> own) {
        var errors = new List<string>();
        foreach (var tree in own) {
            foreach (var node in tree.Root.DescendantNodes()) {
                var (keyword, what) = node switch {
                    UnsafeStatementSyntax block => (block.UnsafeKeyword, "an 'unsafe' block"),
                    FunctionDeclarationSyntax function when function.Modifiers.Any(SyntaxKind.UnsafeKeyword) =>
                        (function.Modifiers.First(static m => m.Kind == SyntaxKind.UnsafeKeyword), $"the 'unsafe fn' '{function.Identifier.Text}'"),
                    _ => (default(SyntaxToken), null),
                };
                if (what is not null) {
                    errors.Add(Diagnostic.Error(ErrorCode.ERR_UnsafeDenied, keyword.Span, $"this program is built with --deny-unsafe, and this is {what}").Format(tree.Text));
                }
            }
        }

        return errors;
    }

    static List<string> Formatted(IEnumerable<(Diagnostic Diagnostic, SyntaxTree? Tree)> diagnostics, IReadOnlyList<SyntaxTree> program) {
        var order = program.Select(static (tree, index) => (tree, index)).ToDictionary(static p => p.tree, static p => p.index);
        return [
            ..diagnostics
                .Where(static d => d.Diagnostic.Severity == DiagnosticSeverity.Error)
                .OrderBy(d => d.Tree is { } t && order.TryGetValue(t, out var index) ? index : int.MaxValue)
                .ThenBy(static d => d.Diagnostic.Span.Start)
                .Select(d => d.Tree is { } file ? d.Diagnostic.Format(file.Text)
                    : program.Count == 1 ? Format(d.Diagnostic, program[0].Text)
                    : $"error[{d.Diagnostic.Id}]: {d.Diagnostic.Message}"),
        ];
    }

    /// <summary>
    ///     The warnings — unreachable code, a 'try' on nothing that throws,
    ///     '#warning' — in the program's own files, each formatted against its
    ///     file. The standard library's are not the program's to act on.
    /// </summary>
    public static List<string> Warnings(Compilation compilation, IReadOnlyList<SyntaxTree> program) {
        var order = program.Select(static (tree, index) => (tree, index)).ToDictionary(static p => p.tree, static p => p.index);
        // Only the program's bodies need binding for them.
        return [
            ..compilation.GetAllDiagnosticsByFile(order.ContainsKey)
                .Where(static d => d.Diagnostic.Severity == DiagnosticSeverity.Warning)
                .Where(d => d.Tree is { } t && order.ContainsKey(t))
                .OrderBy(d => order[d.Tree!])
                .ThenBy(static d => d.Diagnostic.Span.Start)
                .Select(static d => d.Diagnostic.Format(d.Tree!.Text)),
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
