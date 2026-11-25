using Sable.Driver.Generators;
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
public static partial class TestHarness {
    /// <summary>The module the entry point is written in.</summary>
    public const string HarnessModule = "SableHarness";

    const string EntryName = "sableHarnessMain";

    /// <summary>
    ///     Compiles the files for <paramref name="kind" />'s harness, with doc
    ///     examples as tests if asked: parsed as a test build is, unoptimised,
    ///     with <c>DEBUG</c> defined, as <c>sable test</c> parses them.
    /// </summary>
    /// <param name="tests">What the harness lists, in its order.</param>
    public static CompileResult Compile(
        IReadOnlyList<SourceText> sources,
        TestKind kind,
        out IReadOnlyList<TestFunction> tests,
        bool docExamples = false,
        string? debugDirectory = null,
        bool checkExclusivity = true
    ) => Compile([..sources.Select(static source => SyntaxTree.Parse(source, Pipeline.ParseOptionsFor(optimise: false)))], kind, out tests, docExamples, debugDirectory, checkExclusivity);

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
        Func<string, bool>? coverLibrary = null,
        string? optLevel = null,
        bool apart = false
    ) {
        var warnings = new List<string>();
        CoverageMap? map = null;
        var result = Compile(trees, kind, out tests, docExamples, debugDirectory, checkExclusivity, warnings,
            coverage is null && coverLibrary is null ? null : (coverage ?? (static _ => false), coverLibrary, m => map = m), optLevel, apart);
        return result with { Warnings = warnings, Coverage = map };
    }

    /// <param name="optLevel">The level whose SIL passes are run; every one of them when null.</param>
    /// <param name="apart">Keep each test a function of its own, inlined nowhere: to look at what it became.</param>
    /// <param name="warnings">Filled with the program's warnings, once it has been checked.</param>
    static CompileResult Compile(
        IReadOnlyList<SyntaxTree> trees,
        TestKind kind,
        out IReadOnlyList<TestFunction> tests,
        bool docExamples,
        string? debugDirectory,
        bool checkExclusivity,
        List<string> warnings,
        (Func<string, bool> Measured, Func<string, bool>? Library, Action<CoverageMap> Made)? coverage = null,
        string? optLevel = null,
        bool apart = false
    ) {
        tests = [];
        var program = trees.ToList();
        if (docExamples) {
            program.AddRange(DocExamples.Extract(program.ToList()));
        }

        var expanded = LibraryGenerators.Expand(program);
        warnings.AddRange(expanded.Warnings);
        if (expanded.Errors.Count > 0) {
            return new(null, null, expanded.Errors);
        }

        program = [..expanded.Program];

        // Every test build has Sable.Testing, whether or not a file imports
        // it. What the compiler writes is parsed as the program is: the
        // library it imports is otherwise another profile's, beside the
        // program's own.
        var primary = program[0];
        var options = ParseOptions.Default.ForTarget(primary.Options.TargetOs, primary.Options.TargetArch) with { Symbols = primary.Options.Symbols };
        var testing = SyntaxTree.Parse(new SourceText($"module {HarnessModule}\nimport Sable.Testing\n", "<harness>"), options);

        // The standard library's bodies, once it is known to check cleanly
        // with a program that cannot change that, are neither checked again
        // nor lowered beyond what the program reaches (LibraryCheck) — unless
        // its coverage is measured, which counts what was never lowered. A
        // fuzz harness is a library, lowered whole.
        var wholeLibrary = coverage is { Library: not null };
        var checkedAlone = Compilation.Create([..program, ..LibraryFor([..program, testing])]);
        warnings.AddRange(Pipeline.Warnings(checkedAlone, program));
        if (Errors(checkedAlone, primary, wholeLibrary ? null : Library.CheckFor(checkedAlone, program)?.Skipped) is { Count: > 0 } errors) {
            return new(null, null, errors);
        }

        tests = checkedAlone.GetTestFunctions(kind);
        // A test build replays the fuzz targets' saved inputs too.
        var harness = Write(tests, kind, options, kind == TestKind.Test ? checkedAlone.GetTestFunctions(TestKind.Fuzz) : []);
        var compilation = Compilation.Create([..program, ..harness, ..LibraryFor([..program, ..harness])]);
        var check = wholeLibrary || kind == TestKind.Fuzz ? null : Library.CheckFor(compilation, [..program, ..harness]);
        var standard = check?.Skipped;
        if (Errors(compilation, primary, standard) is { Count: > 0 } harnessErrors) {
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
            (module, lowering) = SilGen.Lower(compilation, (FunctionSymbol)compilation.GetDeclaredSymbol(entry)!, markDecisions: coverage is not null, onDemand: standard);
        }

        module.CheckExclusivity = checkExclusivity;
        if (lowering.Count > 0) {
            return new(module, null, [..lowering.Select(d => d.Anchor is { } anchor && compilation.TreeOf(anchor) is { } file ? d.Format(file.Text) : Format(d, primary.Text))]);
        }

        var problems = SilVerifier.Verify(module);
        if (problems.Count > 0) {
            return new(module, null, [..problems.Select(static p => $"internal error: SIL verification failed: {p}")]);
        }

        if (apart) {
            var names = tests.Select(static t => t.QualifiedName).ToHashSet(StringComparer.Ordinal);
            foreach (var function in module.Functions.Where(f => names.Contains(f.Name))) {
                function.Inline = SilInline.Never;
            }
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

        if (debugDirectory is null || optLevel is not (null or "0")) {
            try {
                // Over what the harness can reach, as a build's (Pipeline.Compile).
                SilPassManager.RemoveUnreachable(module);
                SilPassManager.Run(module, optLevel is null ? SilPassManager.Default : SilPassManager.ForLevel(optLevel));
            } catch (SilPassException failure) {
                return new(module, null, [$"internal error: {failure.Message}"]);
            }
        }

        var ir = debugDirectory is null ? LlvmEmitter.Emit(module) : LlvmEmitter.EmitWithDebugInfo(module, debugDirectory, optimised: optLevel is not (null or "0"), sources: Pipeline.EmbeddedSources(compilation.SyntaxTrees, program));
        if (standard is null) {
            check?.Passed();
        }

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

    /// <summary>The compilation's errors, each against its own file; with the standard library's bodies left out when it was checked once already (<see cref="LibraryCheck" />).</summary>
    static List<string> Errors(Compilation compilation, SyntaxTree primary, Func<SyntaxTree, bool>? standard) =>
        (standard is null ? compilation.GetAllDiagnosticsByFile() : compilation.GetAllDiagnosticsByFile(t => !standard(t)))
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
    public static IReadOnlyList<SyntaxTree> Write(IReadOnlyList<TestFunction> tests, TestKind kind, ParseOptions? options = null, IReadOnlyList<TestFunction>? replayed = null) {
        options ??= ParseOptions.Default;
        if (kind == TestKind.Fuzz) {
            return WriteFuzz(tests, options);
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
            files.Add(SyntaxTree.Parse(new SourceText(thunks.ToString(), $"<harness:{module.Key}>"), options));
            main.Append(CultureInfo.InvariantCulture, $"import {module.Key}.{{ {list} }}\n");

            // The cases of each test that has any: a file of its own for each.
            foreach (var (test, i) in module.Select(static (t, i) => (t, i)).Where(static t => t.t.Arguments.Count > 0)) {
                var cases = CasesName(m, i);
                files.Add(SyntaxTree.Parse(new SourceText(WriteCases(test, cases), $"<harness:{test.QualifiedName}>"), options));
                main.Append(CultureInfo.InvariantCulture, $"import {module.Key}.{{ {cases} }}\n");
            }
        }

        var (replayImports, replays) = WriteReplays(replayed ?? [], files, options);
        main.Append(replayImports);
        var (caseType, run) = kind == TestKind.Test ? ("TestCase", "runTests") : ("BenchCase", "runBenchmarks");
        // The entry point is async: 'runTests' and 'runBenchmarks' await each one.
        main.Append(CultureInfo.InvariantCulture, $"\n@main\nasync fn {EntryName}() -> Int {{\n    var all: {caseType}[>..] = []\n");
        for (var m = 0; m < modules.Count; m++) {
            main.Append(CultureInfo.InvariantCulture, $"    let list{m} = sableHarnessList{m}()\n");
            var i = 0;
            foreach (var test in modules[m]) {
                var cases = test.Arguments.Count > 0 ? CasesName(m, i) : "none";
                var tags = string.Join(", ", test.Tags.Select(Quote));
                var more = kind != TestKind.Test ? $", tags: [{tags}], cases: {cases}"
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $", traps: {(test.ExpectedTrap is { } trap ? Quote(trap) : "none")}, tags: [{tags}], timeLimit: {(test.Timeout ?? TimeSpan.Zero).Ticks * 100}, cases: {cases}"
                    );
                main.Append(CultureInfo.InvariantCulture, $"    all.append({caseType} {{ name: {Quote(test.QualifiedName)}, location: {Quote(test.Location)}, run: list{m}[{i++}]{more} }})\n");
            }
        }

        main.Append(replays);
        main.Append(CultureInfo.InvariantCulture, $"    return await {run}(all)\n}}\n");
        files.Add(SyntaxTree.Parse(new SourceText(main.ToString(), "<harness>"), options));
        return files;
    }

    static string CasesName(int module, int test) => string.Create(CultureInfo.InvariantCulture, $"sableHarnessCases{module}_{test}");

    /// <summary>
    ///     The file that gives the cases of a <c>@test(arguments:)</c> test, or a benchmark: its
    ///     arrays, as they are written, in a file with the imports of the one
    ///     they are in — and, for a static method's, in an extension of its
    ///     type — and a closure calling the test for each of what they hold.
    /// </summary>
    static string WriteCases(TestFunction test, string name) {
        var parameters = test.ArgumentParameters;
        var type = test.Kind == TestKind.Bench ? "BenchArguments" : "TestArguments";
        var types = test.Syntax.Parameters.Parameters.Skip(test.TakesBencher ? 1 : 0).Select(static p => p.Type.ToString().Trim()).ToList();
        var arrays = test.Arguments.Select(static a => a.ToString().Trim()).ToList();
        var values = Enumerable.Range(0, parameters.Count).Select(static i => string.Create(CultureInfo.InvariantCulture, $"sableValue{i}")).ToList();
        var body = new StringBuilder();
        body.Append(CultureInfo.InvariantCulture, $"    var sableCases: {type}[>..] = []\n");
        if (arrays.Count == 1 && parameters.Count > 1) {
            body.Append(CultureInfo.InvariantCulture, $"    let sableValues: ({string.Join(", ", types)})[>..] = {arrays[0]}\n    for ({string.Join(", ", values)}) in sableValues {{\n");
        } else {
            for (var i = 0; i < arrays.Count; i++) {
                body.Append(CultureInfo.InvariantCulture, $"    let sableValues{i}: ({types[i]})[>..] = {arrays[i]}\n");
            }

            for (var i = 0; i < arrays.Count; i++) {
                body.Append(CultureInfo.InvariantCulture, $"    for {values[i]} in sableValues{i} {{\n");
            }
        }

        var call = string.Join(", ", parameters.Select((p, i) => p.Label == "_" ? values[i] : $"{p.Label ?? p.Name}: {values[i]}"));
        var described = string.Join(", ", values.Select(static v => $"\\(describeArgument({v}))"));
        body.Append(CultureInfo.InvariantCulture, $"        sableCases.append({type}(\"{described}\", {Thunk(test, call)}))\n");
        body.Append(string.Concat(Enumerable.Repeat("    }\n", arrays.Count))).Append("    return sableCases\n");

        var unit = (CompilationUnitSyntax)test.Tree.Root;
        var imports = unit.Imports.Select(i => test.Tree.Text.Text.Substring(i.Span.Start, i.Span.Length).Trim());
        var owner = test.CallPath.LastIndexOf('.') is var dot and >= 0 ? test.CallPath[..dot] : null;
        var code = owner is null
            ? $"public fn {name}() -> {type}[>..] {{\n{body}}}\n"
            : $"public fn {name}() -> {type}[>..] {{\n    return {owner}.{name}()\n}}\n\nextension {owner} {{\n    static fn {name}() -> {type}[>..] {{\n{body}    }}\n}}\n";
        return $"module {test.Module}\n\n{string.Join('\n', imports)}\nimport Sable.Testing.{{ {type}, describeArgument{(test.Kind == TestKind.Bench ? ", Bencher" : "")} }}\n\n// Written by the compiler for 'sable {(test.Kind == TestKind.Bench ? "bench" : "test")}'.\n{code}";
    }

    /// <summary>The closure that runs one test from inside its module.</summary>
    /// <remarks>What a test throws is described as interpolation describes it, for its failure's report.</remarks>
    static string Thunk(TestFunction test, string arguments = "") => test switch {
        // One whose cases are run instead.
        { Kind: TestKind.Test, Arguments.Count: > 0 } when arguments.Length == 0 => "() => { return none }",
        { Kind: TestKind.Test, Throws: false } => $"() => {{ {Await(test)}{test.CallPath}({arguments}); return none }}",
        { Kind: TestKind.Test, Throws: true } => $"() => {{\n            do {{ try {Await(test)}{test.CallPath}({arguments}) }} catch {{ return \"\\(error)\" }}\n            return none\n        }}",
        { Kind: TestKind.Bench, Arguments.Count: > 0 } when arguments.Length == 0 => "(b: Bencher) => { }",
        { Kind: TestKind.Bench, TakesBencher: true } => $"(sableBencher: Bencher) => {{ {Await(test)}{test.CallPath}({BencherArgument(test)}{(arguments.Length > 0 ? ", " : "")}{arguments}) }}",
        { Kind: TestKind.Bench, IsAsync: true } => $"(b: Bencher) => {{ await b.iterateAsync(() => {{ await {test.CallPath}({arguments}) }}) }}",
        _ => $"(b: Bencher) => {{ b.iterate(() => {test.CallPath}({arguments})) }}",
    };

    static string BencherArgument(TestFunction test) =>
        test.Symbol.Parameters[0] is { Label: not "_" } named ? $"{named.Label ?? named.Name}: sableBencher" : "sableBencher";

    static string Await(TestFunction test) => test.IsAsync ? "await " : "";

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
