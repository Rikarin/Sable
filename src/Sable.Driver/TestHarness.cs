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
            // A test's closure is async, so an async test can be awaited, and
            // is given the fixtures there are.
            thunks.Append("import Sable.Testing.{ FixtureScope }\n\n");
            var closure = "async (FixtureScope) throws -> Void";
            if (kind == TestKind.Bench) {
                thunks.Clear();
                thunks.Append(CultureInfo.InvariantCulture, $"module {module.Key}\n\n");
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

            // What is written with a test's own names — its cases, the fixtures
            // it takes, when it is skipped — is in a file with its imports.
            foreach (var (test, i) in module.Select(static (t, i) => (t, i)).Where(static t => NeedsFile(t.t))) {
                files.Add(SyntaxTree.Parse(new SourceText(WriteTest(test, Suffix(m, i)), $"<harness:{test.QualifiedName}>"), options));
                foreach (var name in new[] { (test.Arguments.Count > 0, CasesName), (test.Fixtures.Count > 0 && test.Arguments.Count == 0, RunName), (test.Fixtures.Count > 0 || test.Suite is { Parameters.Count: > 0 }, LifetimesName), (HasCondition(test), SkipsName) }.Where(static n => n.Item1)) {
                    main.Append(CultureInfo.InvariantCulture, $"import {module.Key}.{{ {name.Item2}{Suffix(m, i)} }}\n");
                }
            }
        }

        // How each suite is made, and each fixture that is made from others.
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var making in kind == TestKind.Test ? tests.SelectMany(static t => t.Fixtures.Select(static f => f.Making).Concat(t.Suite is { } suite ? [suite] : [])).ToList() : []) {
            WriteMaking(making, isSuite: tests.Any(t => t.Suite?.Type == making.Type), written, files, options);
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
                if (kind == TestKind.Test) {
                    main.Append(CultureInfo.InvariantCulture, $"    if wantsTest({Quote(test.QualifiedName)}) {{\n        all.append(TestCase({Case(test, test.Fixtures.Count > 0 && test.Arguments.Count == 0 ? RunName + Suffix(m, i) : $"list{m}[{i}]", Suffix(m, i))}))\n    }}\n");
                } else {
                    var cases = test.Arguments.Count > 0 ? CasesName + Suffix(m, i) : "none";
                    main.Append(CultureInfo.InvariantCulture, $"    all.append({caseType} {{ name: {Quote(test.QualifiedName)}, location: {Quote(test.Location)}, run: list{m}[{i}], tags: [{string.Join(", ", test.Tags.Select(Quote))}], cases: {cases} }})\n");
                }

                i++;
            }
        }

        main.Append(replays);
        main.Append(CultureInfo.InvariantCulture, $"    return await {run}(all)\n}}\n");
        files.Add(SyntaxTree.Parse(new SourceText(main.ToString(), "<harness>"), options));
        return files;
    }

    const string CasesName = "sableHarnessCases", RunName = "sableHarnessRun", LifetimesName = "sableHarnessLifetimes", SkipsName = "sableHarnessSkips", MakeName = "sableHarnessMake";

    static string Suffix(int module, int test) => string.Create(CultureInfo.InvariantCulture, $"{module}_{test}");

    static bool HasCondition(TestFunction test) => test.Skips.Any(static s => s.Condition is not null);

    static bool NeedsFile(TestFunction test) => test.Arguments.Count > 0 || test.Kind == TestKind.Test && (test.Fixtures.Count > 0 || HasCondition(test) || test.Suite is { Parameters.Count: > 0 });

    /// <summary>A test as the entry point lists it: what it is, and what it says of itself where it says anything.</summary>
    static string Case(TestFunction test, string run, string suffix) {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"name: {Quote(test.QualifiedName)}, location: {Quote(test.Location)}, run: {run}");
        if (test.ExpectedTrap is { } trap) {
            text.Append(CultureInfo.InvariantCulture, $", traps: {Quote(trap)}");
        }

        if (test.Tags.Count > 0) {
            text.Append(CultureInfo.InvariantCulture, $", tags: [{string.Join(", ", test.Tags.Select(Quote))}]");
        }

        if (test.Timeout is { } timeout) {
            text.Append(CultureInfo.InvariantCulture, $", timeLimit: {timeout.Ticks * 100}");
        }

        if (test.Arguments.Count > 0) {
            text.Append(CultureInfo.InvariantCulture, $", cases: {CasesName}{suffix}");
        }

        text.Append(CultureInfo.InvariantCulture, $", suite: {Quote(test.SuiteName)}");
        if (test.Collection.Length > 0) {
            text.Append(CultureInfo.InvariantCulture, $", collection: {Quote(test.Collection)}");
        }

        text.Append(test.IsSerialized ? ", isSerialized: true" : "").Append(test.IsExplicit ? ", isExplicit: true" : "");
        if (test.Retries > 0) {
            text.Append(CultureInfo.InvariantCulture, $", retries: {test.Retries}");
        }

        if (test.Skips.Count > 0) {
            text.Append(", skips: ").Append(test.Skips.Any(static s => s.Condition is not null) ? SkipsName + suffix : $"{{ {Quote(test.Skips[0].Reason)} }}");
        }

        if (test.Fixtures.Count > 0 || test.Suite is { Parameters.Count: > 0 }) {
            text.Append(CultureInfo.InvariantCulture, $", fixtures: {LifetimesName}{suffix}");
        }

        return text.ToString();
    }

    /// <summary>The expression that gives a fixture, in a file that may name its type as it is written there.</summary>
    static string Give(TestFixture fixture) =>
        fixture.Making.Parameters.Count > 0
            ? $"try await {fixture.Written}.{MakeName}(scope)"
            : $"try await scope.fixture({fixture.Written}.self, named: {Quote(fixture.Making.QualifiedName)}, {{ (scope: FixtureScope) in {Make(fixture.Making, fixture.Written)} }})";

    /// <summary>A call of the initialiser, with its fixtures given.</summary>
    static string Make(TestMaking making, string type) =>
        $"{(making.Throws || making.Parameters.Count > 0 ? "try " : "")}{(making.IsAsync || making.Parameters.Count > 0 ? "await " : "")}{type}({Passed(making.Parameters)})";

    static string Passed(IReadOnlyList<TestFixture> fixtures) =>
        string.Join(", ", fixtures.Select(static f => $"{(f.Label is { } label ? $"{label}: " : "")}{Give(f)}"));

    /// <summary>A body that says how long each of the fixtures lives, with those they are made from.</summary>
    /// <param name="own">What it starts with, an array of its own, if anything.</param>
    static string Lifetimes(string? own, IReadOnlyList<TestFixture> fixtures) {
        var body = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"    var all: FixtureLifetime[>..] = {own ?? "[]"}\n"));
        foreach (var fixture in fixtures) {
            body.Append(
                fixture.Making.Parameters.Count > 0
                    ? $"    all.append(contentsOf: {fixture.Written}.{LifetimesName}())\n"
                    : $"    all.append({fixture.Written}.lifetime)\n"
            );
        }

        return body.Append("    return all\n").ToString();
    }

    /// <summary>The file that makes a suite, or a fixture, from the fixtures its initialiser takes: an extension of it, with its file's imports.</summary>
    static void WriteMaking(TestMaking making, bool isSuite, HashSet<string> written, List<SyntaxTree> files, ParseOptions options) {
        foreach (var parameter in making.Parameters) {
            WriteMaking(parameter.Making, false, written, files, options);
        }

        if (making.Tree is null || (!isSuite && making.Parameters.Count == 0) || !written.Add(making.QualifiedName)) {
            return;
        }

        var access = making.IsPublic ? "public " : "";
        var made = isSuite ? Make(making, making.Path) : $"try await scope.fixture({making.Path}.self, named: {Quote(making.QualifiedName)}, {{ (scope: FixtureScope) in {Make(making, making.Path)} }})";
        var members = $$"""
                {{access}}static async fn {{MakeName}}(_ scope: FixtureScope) throws -> {{making.Path}} {
                    return {{made}}
                }

                {{access}}static fn {{LifetimesName}}() -> FixtureLifetime[>..] {
                {{Lifetimes(isSuite ? null : $"[{making.Path}.lifetime]", making.Parameters)}}
                }

            """;
        files.Add(SyntaxTree.Parse(new SourceText($"{Header(making.Module, making.Tree)}import Sable.Testing.{{ FixtureScope, FixtureLifetime, Fixture }}\n\nextension {making.Path} {{\n{members}}}\n", $"<harness:{making.QualifiedName}>"), options));
    }

    /// <summary>The module, and the imports of the file.</summary>
    static string Header(string module, SyntaxTree tree) {
        var imports = ((CompilationUnitSyntax)tree.Root).Imports.Select(i => tree.Text.Text.Substring(i.Span.Start, i.Span.Length).Trim());
        return $"module {module}\n\n{string.Join('\n', imports)}\n";
    }

    /// <summary>
    ///     What is written with a test's own names, in a file with the
    ///     imports of the one it is in — and, for a method's, in an extension
    ///     of its type: the cases of a <c>@test(arguments:)</c> test, a closure
    ///     calling the test for each of what its arrays hold; the fixtures a
    ///     function takes, given to it; and when it is skipped.
    /// </summary>
    static string WriteTest(TestFunction test, string suffix) {
        var type = test.CallPath.LastIndexOf('.') is var dot and >= 0 ? test.CallPath[..dot] : null;
        var functions = new List<(string Signature, string Name, string Pass, string Body)>();
        if (test.Arguments.Count > 0) {
            functions.Add(($"fn {{0}}() -> {(test.Kind == TestKind.Bench ? "BenchArguments" : "TestArguments")}[>..]", CasesName, "()", WriteCases(test)));
        }

        if (test.Fixtures.Count > 0 && test.Arguments.Count == 0) {
            functions.Add(("async fn {0}(_ scope: FixtureScope) throws", RunName, "(scope)", $"    {Call(test, Passed(test.Fixtures))}\n"));
        }

        if (test.Fixtures.Count > 0 || test.Suite is { Parameters.Count: > 0 }) {
            var own = test.Suite is { Parameters.Count: > 0 } suite ? $"{suite.Path}.{LifetimesName}()" : null;
            functions.Add(("fn {0}() -> FixtureLifetime[>..]", LifetimesName, "()", Lifetimes(own, test.Fixtures)));
        }

        if (test.Skips.Any(static s => s.Condition is not null)) {
            var body = new StringBuilder();
            foreach (var skip in test.Skips) {
                body.Append(CultureInfo.InvariantCulture, $"    if {skip.Condition?.ToString().Trim() ?? "true"} {{\n        return {Quote(skip.Reason)}\n    }}\n");
            }

            functions.Add(("fn {0}() -> String?", SkipsName, "()", body.Append("    return none\n").ToString()));
        }

        var text = new StringBuilder();
        text.Append(Header(test.Module, test.Tree)).Append(test.Kind == TestKind.Bench
            ? "import Sable.Testing.{ BenchArguments, Bencher, describeArgument }\n\n// Written by the compiler for 'sable bench'.\n"
            : "import Sable.Testing.{ TestArguments, describeArgument, FixtureScope, FixtureLifetime, Fixture }\n\n// Written by the compiler for 'sable test'.\n");
        foreach (var (signature, name, pass, body) in functions) {
            var function = string.Format(CultureInfo.InvariantCulture, signature, name + suffix);
            if (type is null) {
                text.Append(CultureInfo.InvariantCulture, $"public {function} {{\n{body}}}\n");
            } else {
                var waits = signature.StartsWith("async", StringComparison.Ordinal) ? "try await " : signature.Contains("->", StringComparison.Ordinal) ? "return " : "";
                text.Append(CultureInfo.InvariantCulture, $"public {function} {{\n    {waits}{type}.{name}{suffix}{pass}\n}}\n\nextension {type} {{\n    static {function} {{\n{body}    }}\n}}\n");
            }
        }

        return text.ToString();
    }

    /// <summary>The body that gives a <c>@test(arguments:)</c> test's cases.</summary>
    static string WriteCases(TestFunction test) {
        var parameters = test.ArgumentParameters;
        var type = test.Kind == TestKind.Bench ? "BenchArguments" : "TestArguments";
        var types = test.Syntax.Parameters.Parameters.Skip(test.TakesBencher ? 1 : 0).Take(parameters.Count).Select(static p => p.Type.ToString().Trim()).ToList();
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

        var call = string.Join(", ", parameters.Select((p, i) => p.Label == "_" ? values[i] : $"{p.Label ?? p.Name}: {values[i]}").Append(Passed(test.Fixtures)).Where(static a => a.Length > 0));
        var described = string.Join(", ", values.Select(static v => $"\\(describeArgument({v}))"));
        body.Append(CultureInfo.InvariantCulture, $"        sableCases.append({type}(\"{described}\", {(test.Kind == TestKind.Bench ? BenchThunk(test, call) : $"(scope: FixtureScope) => {{ {Call(test, call)} }}")}))\n");
        return body.Append(string.Concat(Enumerable.Repeat("    }\n", arrays.Count))).Append("    return sableCases\n").ToString();
    }

    /// <summary>The test called: of a suite made for it, for a method of one.</summary>
    static string Call(TestFunction test, string arguments) {
        var call = $"{(test.Throws ? "try " : "")}{Await(test)}";
        if (test.Suite is not { } suite) {
            return $"{call}{test.CallPath}({arguments})";
        }

        return $"var sableSuite = try await scope.suite(try await {suite.Path}.{MakeName}(scope)); {call}sableSuite.{test.Symbol.Name}({arguments})";
    }

    /// <summary>The closure that runs one test from inside its module.</summary>
    static string Thunk(TestFunction test) => test switch {
        // One whose cases are run instead.
        { Kind: TestKind.Test, Arguments.Count: > 0 } => "(scope: FixtureScope) => { }",
        // One whose fixtures are given in a file that can name them.
        { Kind: TestKind.Test, Fixtures.Count: > 0 } => "(scope: FixtureScope) => { }",
        { Kind: TestKind.Test } => $"(scope: FixtureScope) => {{ {Call(test, "")} }}",
        { Arguments.Count: > 0 } => "(b: Bencher) => { }",
        _ => BenchThunk(test, ""),
    };

    /// <summary>The closure that measures one benchmark.</summary>
    static string BenchThunk(TestFunction test, string arguments) => test switch {
        { TakesBencher: true } => $"(sableBencher: Bencher) => {{ {Await(test)}{test.CallPath}({BencherArgument(test)}{(arguments.Length > 0 ? ", " : "")}{arguments}) }}",
        { IsAsync: true } => $"(b: Bencher) => {{ await b.iterateAsync(() => {{ await {test.CallPath}({arguments}) }}) }}",
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
