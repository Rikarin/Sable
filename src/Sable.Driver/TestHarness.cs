using System.Globalization;
using System.Text;
using Sable.CodeGen;
using Sable.SIL;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Driver;

/// <summary>
///     A test or bench build (docs/05 §5.1): the program's files, with an
///     entry point the compiler writes in place of the program's own — a
///     table of the <c>@test</c> (or <c>@bench</c>) functions, handed to
///     <c>Sable.Testing</c>'s <c>runTests</c> (or <c>runBenchmarks</c>). A
///     fuzz build has no entry point: it is a library, whose <c>@export</c>
///     functions C calls once an input (<see cref="FuzzBuild" />).
/// </summary>
/// <remarks>
///     <para>
///         The harness is Sable source, compiled with the rest. For each
///         module with tests, a file of that module gives a function listing
///         a closure per test — inside the module, so that a test need not
///         be public. A file of a module of its own, <see cref="HarnessModule" />,
///         imports just those functions, pairs each closure with the test's
///         name and place, and starts the program. Nothing else of the
///         program's modules is imported there, so no name of theirs can
///         clash with Sable.Testing's.
///     </para>
///     <para>
///         The program is checked first on its own: an error in it is
///         reported against its own files, and the harness is written only
///         for a program that compiles. Then the harness is added, and the
///         whole is compiled again, starting at the harness.
///     </para>
/// </remarks>
public static class TestHarness {
    /// <summary>The module the entry point is written in.</summary>
    public const string HarnessModule = "SableHarness";

    const string EntryName = "sableHarnessMain";

    /// <summary>Compiles the files for <paramref name="kind" />'s harness, with doc examples as tests if asked.</summary>
    /// <param name="tests">What the harness lists, in its order.</param>
    public static CompileResult Compile(
        IReadOnlyList<SourceText> sources,
        TestKind kind,
        out IReadOnlyList<TestFunction> tests,
        bool docExamples = false,
        string? debugDirectory = null,
        bool checkExclusivity = true
    ) => Compile([..sources.Select(SyntaxTree.Parse)], kind, out tests, docExamples, debugDirectory, checkExclusivity);

    /// <summary>The same, for files already parsed: a package's, each with its own package's features.</summary>
    /// <param name="coverage">Which of the program's own files to count coverage in (never its tests').</param>
    /// <param name="coverLibrary">Which of the standard library's files to count coverage in, by the names they are embedded with: <c>lib/Sable.Collections/Stack.sb</c>.</param>
    public static CompileResult Compile(
        IReadOnlyList<SyntaxTree> trees,
        TestKind kind,
        out IReadOnlyList<TestFunction> tests,
        bool docExamples = false,
        string? debugDirectory = null,
        bool checkExclusivity = true,
        Func<string, bool>? coverage = null,
        Func<string, bool>? coverLibrary = null
    ) {
        var warnings = new List<string>();
        CoverageMap? map = null;
        var result = Compile(trees, kind, out tests, docExamples, debugDirectory, checkExclusivity, warnings,
            coverage is null && coverLibrary is null ? null : (coverage ?? (static _ => false), coverLibrary, m => map = m));
        return result with { Warnings = warnings, Coverage = map };
    }

    /// <param name="warnings">Filled with the program's warnings, once it has been checked.</param>
    static CompileResult Compile(
        IReadOnlyList<SyntaxTree> trees,
        TestKind kind,
        out IReadOnlyList<TestFunction> tests,
        bool docExamples,
        string? debugDirectory,
        bool checkExclusivity,
        List<string> warnings,
        (Func<string, bool> Measured, Func<string, bool>? Library, Action<CoverageMap> Made)? coverage = null
    ) {
        tests = [];
        var program = trees.ToList();
        if (docExamples) {
            program.AddRange(DocExamples.Extract(program.ToList()));
        }

        // Every test build has Sable.Testing, whether or not a file imports it.
        var testing = SyntaxTree.Parse(new SourceText($"module {HarnessModule}\nimport Sable.Testing\n", "<harness>"));
        var primary = program[0];

        var checkedAlone = Compilation.Create([..program, ..LibraryFor([..program, testing])]);
        warnings.AddRange(Pipeline.Warnings(checkedAlone, program));
        if (Errors(checkedAlone, primary) is { Count: > 0 } errors) {
            return new(null, null, errors);
        }

        tests = checkedAlone.GetTestFunctions(kind);
        var harness = Write(tests, kind);
        var compilation = Compilation.Create([..program, ..harness, ..LibraryFor([..program, ..harness])]);
        if (Errors(compilation, primary) is { Count: > 0 } harnessErrors) {
            return new(null, null, [..harnessErrors.Select(static e => $"internal error: the {HarnessModule} harness did not compile:\n{e}")]);
        }

        SilModule module;
        IReadOnlyList<Diagnostic> lowering;
        if (kind == TestKind.Fuzz) {
            // A library: libFuzzer's main, or the replay one, is the C side's.
            var (lowered, placed) = SilGen.LowerLibraryByFile(compilation);
            (module, lowering) = (lowered, [..placed.Select(static d => d.Diagnostic)]);
        } else {
            var entry = harness[^1].Root.DescendantNodes().OfType<FunctionDeclarationSyntax>().First(static f => f.Identifier.Text == EntryName);
            // Measured, the decisions are marked as they are lowered, for branch coverage.
            (module, lowering) = SilGen.Lower(compilation, (FunctionSymbol)compilation.GetDeclaredSymbol(entry)!, markDecisions: coverage is not null);
        }

        module.CheckExclusivity = checkExclusivity;
        if (lowering.Count > 0) {
            return new(module, null, [..lowering.Select(d => Format(d, primary.Text))]);
        }

        var problems = SilVerifier.Verify(module);
        if (problems.Count > 0) {
            return new(module, null, [..problems.Select(static p => $"internal error: SIL verification failed: {p}")]);
        }

        // Counted where the program's own code is, before any pass: the
        // tests' files are not measured, nor the library's — unless the
        // library's are what was asked for ('--cover'), by the names they
        // are embedded with (lib/<Module>/<File>.sb).
        if (coverage is var (wanted, library, made)) {
            var own = trees.Select(static t => t.Text.FileName).ToHashSet(StringComparer.Ordinal);
            var measured = own.Where(f => wanted(f) && !IsTestFile(f)).ToHashSet(StringComparer.Ordinal);
            bool Measured(string f) => measured.Contains(f) || (library is not null && !own.Contains(f) && library(f));
            var map = Coverage.Instrument(module, Measured);
            made(map with { Counters = [..map.Counters, ..NeverLowered(compilation, Measured, map)] });
        }

        if (debugDirectory is null) {
            try {
                SilPassManager.Run(module, SilPassManager.Default);
            } catch (SilPassException failure) {
                return new(module, null, [$"internal error: {failure.Message}"]);
            }
        }

        var ir = debugDirectory is null ? LlvmEmitter.Emit(module) : LlvmEmitter.EmitWithDebugInfo(module, debugDirectory, sources: Pipeline.EmbeddedSources(compilation.SyntaxTrees, program));
        return new(module, ir, []);
    }

    /// <summary>
    ///     Counters, never counted, for the code of measured files that was
    ///     not lowered at all: a generic function is lowered only for the
    ///     types it is used with, so a method of a generic type no test calls
    ///     has no SIL to put a counter in. Without these its lines would be
    ///     missing from the report rather than reported as not run. A
    ///     function lowered has a counter on a line within it; one with none
    ///     contributes the first line of each of its statements (or of its
    ///     expression body), which is where lowering would have put them.
    /// </summary>
    static IEnumerable<CoverageCounter> NeverLowered(Compilation compilation, Func<string, bool> measured, CoverageMap map) {
        var lowered = map.Counters.GroupBy(static c => c.File)
            .ToDictionary(static g => g.Key, static g => g.SelectMany(static c => c.Lines).ToHashSet());
        foreach (var tree in compilation.SyntaxTrees.Where(t => measured(t.Text.FileName))) {
            var text = tree.Text;
            int LineOf(SyntaxNode node) => text.GetLinePosition(node.Span.Start).Line;
            var ran = lowered.GetValueOrDefault(text.FileName) ?? [];
            foreach (var declaration in tree.Root.DescendantNodes()) {
                SyntaxNode? body = declaration switch {
                    FunctionDeclarationSyntax f => (SyntaxNode?)f.Body ?? f.ExpressionBody,
                    InitDeclarationSyntax i => i.Body,
                    DeinitDeclarationSyntax d => d.Body,
                    OperatorDeclarationSyntax o => (SyntaxNode?)o.Body ?? o.ExpressionBody,
                    AccessorSyntax a => (SyntaxNode?)a.Body ?? a.ExpressionBody,
                    SubscriptDeclarationSyntax s => s.ExpressionBody,
                    _ => null,
                };

                // A function inside another's body is that one's code.
                if (body is null || InsideABody(declaration)) {
                    continue;
                }

                var (first, last) = (LineOf(declaration), text.GetLinePosition(declaration.Span.End).Line);
                if (ran.Any(l => l >= first && l <= last)) {
                    continue;
                }

                List<int> lines = body is BlockSyntax block
                    ? [..block.DescendantNodes().OfType<StatementSyntax>().Where(static s => s is not BlockSyntax).Select(LineOf).Distinct().Order()]
                    : [LineOf(body)];
                if (lines.Count > 0) {
                    yield return new(text.FileName, lines);
                }
            }
        }
    }

    static bool InsideABody(SyntaxNode node) {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent) {
            if (parent is BlockSyntax) {
                return true;
            }
        }

        return false;
    }

    /// <summary>A file of a package's tests/ directory.</summary>
    static bool IsTestFile(string path) =>
        path.Replace('\\', '/').Split('/').SkipLast(1).Contains("tests", StringComparer.Ordinal);

    /// <summary>The library modules the files import, each once.</summary>
    static IReadOnlyList<SyntaxTree> LibraryFor(IEnumerable<SyntaxTree> trees) {
        var seen = new HashSet<SyntaxTree>();
        var all = new List<SyntaxTree>();
        var program = trees.ToList();
        // Each file for its own target (a package's files may differ), but
        // knowing the whole program: a library file it has its own of is its.
        var brought = Library.Brought(program);
        foreach (var tree in program) {
            foreach (var library in Library.For(tree).Where(l => !CoreLibrary.IsBrought(l, brought))) {
                if (seen.Add(library)) {
                    all.Add(library);
                }
            }
        }

        return all;
    }

    /// <summary>The compilation's errors, each against its own file.</summary>
    static List<string> Errors(Compilation compilation, SyntaxTree primary) =>
        compilation.GetAllDiagnosticsByFile()
            .Where(static d => d.Diagnostic.Severity == DiagnosticSeverity.Error)
            .OrderBy(d => d.Tree == primary ? 0 : 1)
            .ThenBy(static d => d.Diagnostic.Span.Start)
            .Select(d => d.Tree is { } file ? d.Diagnostic.Format(file.Text) : Format(d.Diagnostic, primary.Text))
            .ToList();

    static string Format(Diagnostic diagnostic, SourceText source) =>
        diagnostic.Span.End <= source.Length
            ? diagnostic.Format(source)
            : $"error[{diagnostic.Id}] in the standard library: {diagnostic.Message}";

    /// <summary>
    ///     The harness's files: one per module with tests, then the entry
    ///     point's. Public so that a test can see what the compiler writes.
    /// </summary>
    public static IReadOnlyList<SyntaxTree> Write(IReadOnlyList<TestFunction> tests, TestKind kind) {
        if (kind == TestKind.Fuzz) {
            return WriteFuzz(tests);
        }

        var files = new List<SyntaxTree>();
        var main = new StringBuilder();
        main.Append(CultureInfo.InvariantCulture, $"module {HarnessModule}\n\nimport Sable.Testing\n");

        var modules = tests.GroupBy(static t => t.Module).ToList();
        for (var m = 0; m < modules.Count; m++) {
            var module = modules[m];
            var list = $"sableHarnessList{m}";
            var thunks = new StringBuilder();
            thunks.Append(CultureInfo.InvariantCulture, $"module {module.Key}\n\n");
            // A test's closure is async, so an async test can be awaited:
            // none when it returned, what it threw when it threw.
            var closure = "async () -> String?";
            if (kind == TestKind.Bench) {
                // Each is run with a Bencher: given it, if it takes one, or
                // timed whole by it, if not; awaited, as an async one may be.
                thunks.Append("import Sable.Testing\n\n");
                closure = "async (Bencher) -> Void";
            }

            thunks.Append(CultureInfo.InvariantCulture, $"// Written by the compiler for '{(kind == TestKind.Test ? "sable test" : "sable bench")}'.\npublic fn {list}() -> ({closure})[>..] {{\n    return [\n");
            foreach (var test in module) {
                thunks.Append("        ").Append(Thunk(test)).Append(",\n");
            }

            thunks.Append("    ]\n}\n");
            files.Add(SyntaxTree.Parse(new SourceText(thunks.ToString(), $"<harness:{module.Key}>")));
            main.Append(CultureInfo.InvariantCulture, $"import {module.Key}.{{ {list} }}\n");
        }

        var (caseType, run) = kind == TestKind.Test ? ("TestCase", "runTests") : ("BenchCase", "runBenchmarks");
        // The entry point is async: 'runTests' and 'runBenchmarks' await each one.
        main.Append(CultureInfo.InvariantCulture, $"\n@main\nasync fn {EntryName}() -> Int {{\n    var all: {caseType}[>..] = []\n");
        for (var m = 0; m < modules.Count; m++) {
            main.Append(CultureInfo.InvariantCulture, $"    let list{m} = sableHarnessList{m}()\n");
            var i = 0;
            foreach (var test in modules[m]) {
                var traps = kind == TestKind.Test ? $", traps: {(test.ExpectedTrap is { } trap ? Quote(trap) : "none")}" : "";
                main.Append(CultureInfo.InvariantCulture, $"    all.append({caseType} {{ name: {Quote(test.QualifiedName)}, location: {Quote(test.Location)}, run: list{m}[{i++}]{traps} }})\n");
            }
        }

        main.Append(CultureInfo.InvariantCulture, $"    return await {run}(all)\n}}\n");
        files.Add(SyntaxTree.Parse(new SourceText(main.ToString(), "<harness>")));
        return files;
    }

    /// <summary>The closure that runs one test from inside its module.</summary>
    /// <remarks>What a test throws is described as interpolation describes it, for its failure's report.</remarks>
    static string Thunk(TestFunction test) => test switch {
        { Kind: TestKind.Test, Throws: false } => $"() => {{ {Await(test)}{test.CallPath}(); return none }}",
        { Kind: TestKind.Test, Throws: true } => $"() => {{\n            do {{ try {Await(test)}{test.CallPath}() }} catch {{ return \"\\(error)\" }}\n            return none\n        }}",
        { Kind: TestKind.Bench, TakesBencher: true } => $"(b: Bencher) => {{ {Await(test)}{test.CallPath}(b) }}",
        { Kind: TestKind.Bench, IsAsync: true } => $"(b: Bencher) => {{ await b.iterateAsync(() => {{ await {test.CallPath}() }}) }}",
        _ => $"(b: Bencher) => {{ b.iterate(() => {test.CallPath}()) }}",
    };

    static string Await(TestFunction test) => test.IsAsync ? "await " : "";

    /// <summary>
    ///     A fuzz build's harness: in each module with targets, a function
    ///     listing a closure per target, as a test build's; then what C calls
    ///     (<see cref="FuzzBuild.Shim" />) — <c>sable_harness_fuzz_begin</c>, then
    ///     <c>sable_harness_fuzz_push</c> for each byte of an input, since only
    ///     integers cross to C, then <c>sable_harness_fuzz_run</c> with the number of
    ///     the target, in <paramref name="targets" />' order, to run on it.
    /// </summary>
    static IReadOnlyList<SyntaxTree> WriteFuzz(IReadOnlyList<TestFunction> targets) {
        var files = new List<SyntaxTree>();
        var main = new StringBuilder();
        main.Append(CultureInfo.InvariantCulture, $"module {HarnessModule}\n\n");
        var run = new StringBuilder();
        var index = 0;
        var modules = targets.GroupBy(static t => t.Module).ToList();
        for (var m = 0; m < modules.Count; m++) {
            var list = $"sableHarnessList{m}";
            var thunks = new StringBuilder();
            thunks.Append(CultureInfo.InvariantCulture, $"module {modules[m].Key}\n\n// Written by the compiler for 'sable fuzz'.\npublic fn {list}() -> ((U8[:]) -> Void)[>..] {{\n    return [\n");
            var i = 0;
            foreach (var target in modules[m]) {
                // What a target throws fails it, as a trap does.
                var call = target.Throws
                    ? $"(data: U8[:]) => {{\n            do {{ try {target.CallPath}(data) }} catch {{ fatalError(\"{Escape(target.QualifiedName)} threw: \\(error)\") }}\n        }}"
                    : $"(data: U8[:]) => {{ {target.CallPath}(data) }}";
                thunks.Append("        ").Append(call).Append(",\n");
                run.Append(CultureInfo.InvariantCulture, $"    if target == {index++} {{\n        {list}()[{i++}](sableFuzzInput)\n        return\n    }}\n");
            }

            thunks.Append("    ]\n}\n");
            files.Add(SyntaxTree.Parse(new SourceText(thunks.ToString(), $"<harness:{modules[m].Key}>")));
            main.Append(CultureInfo.InvariantCulture, $"import {modules[m].Key}.{{ {list} }}\n");
        }

        main.Append("""

            // Written by the compiler for 'sable fuzz': the input, a byte at a time, and the target to run on it.
            var sableFuzzInput: U8[>..] = []

            @export("c", "sable_harness_fuzz_begin")
            fn sableFuzzBegin() {
                sableFuzzInput = []
            }

            @export("c", "sable_harness_fuzz_push")
            fn sableFuzzPush(_ byte: U8) {
                sableFuzzInput.append(byte)
            }

            @export("c", "sable_harness_fuzz_run")
            fn sableFuzzRun(_ target: Int) {

            """).Append(run).Append("}\n");
        files.Add(SyntaxTree.Parse(new SourceText(main.ToString(), "<harness>")));
        return files;
    }

    /// <summary>Text for inside a string literal: its quotes and backslashes escaped.</summary>
    static string Escape(string text) => Quote(text)[1..^1];

    static string Quote(string text) {
        var quoted = new StringBuilder("\"");
        foreach (var c in text) {
            quoted.Append(c switch {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\t' => "\\t",
                _ => c.ToString(),
            });
        }

        return quoted.Append('"').ToString();
    }
}
