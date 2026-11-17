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
///     <c>Sable.Testing</c>'s <c>runTests</c> (or <c>runBenchmarks</c>).
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
    public static CompileResult Compile(
        IReadOnlyList<SyntaxTree> trees,
        TestKind kind,
        out IReadOnlyList<TestFunction> tests,
        bool docExamples = false,
        string? debugDirectory = null,
        bool checkExclusivity = true
    ) {
        tests = [];
        var program = trees.ToList();
        if (docExamples) {
            program.AddRange(program.ToList().SelectMany(DocExamples.Extract));
        }

        // Every test build has Sable.Testing, whether or not a file imports it.
        var testing = SyntaxTree.Parse(new SourceText($"module {HarnessModule}\nimport Sable.Testing\n", "<harness>"));
        var primary = program[0];

        var checkedAlone = Compilation.Create([..program, ..LibraryFor([..program, testing])]);
        if (Errors(checkedAlone, primary) is { Count: > 0 } errors) {
            return new(null, null, errors);
        }

        tests = checkedAlone.GetTestFunctions(kind);
        var harness = Write(tests, kind);
        var compilation = Compilation.Create([..program, ..harness, ..LibraryFor([..program, ..harness])]);
        if (Errors(compilation, primary) is { Count: > 0 } harnessErrors) {
            return new(null, null, [..harnessErrors.Select(static e => $"internal error: the {HarnessModule} harness did not compile:\n{e}")]);
        }

        var entry = harness[^1].Root.DescendantNodes().OfType<FunctionDeclarationSyntax>().First(static f => f.Identifier.Text == EntryName);
        var (module, lowering) = SilGen.Lower(compilation, (FunctionSymbol)compilation.GetDeclaredSymbol(entry)!);
        module.CheckExclusivity = checkExclusivity;
        if (lowering.Count > 0) {
            return new(module, null, [..lowering.Select(d => Format(d, primary.Text))]);
        }

        var problems = SilVerifier.Verify(module);
        if (problems.Count > 0) {
            return new(module, null, [..problems.Select(static p => $"internal error: SIL verification failed: {p}")]);
        }

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

    /// <summary>The library modules the files import, each once.</summary>
    static IReadOnlyList<SyntaxTree> LibraryFor(IEnumerable<SyntaxTree> trees) {
        var seen = new HashSet<SyntaxTree>();
        var all = new List<SyntaxTree>();
        foreach (var tree in trees) {
            foreach (var library in Library.For(tree)) {
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
        var files = new List<SyntaxTree>();
        var main = new StringBuilder();
        main.Append(CultureInfo.InvariantCulture, $"module {HarnessModule}\n\nimport Sable.Testing\n");

        var modules = tests.GroupBy(static t => t.Module).ToList();
        for (var m = 0; m < modules.Count; m++) {
            var module = modules[m];
            var list = $"sableHarnessList{m}";
            var thunks = new StringBuilder();
            thunks.Append(CultureInfo.InvariantCulture, $"module {module.Key}\n\n");
            var closure = "() -> Bool";
            if (kind == TestKind.Bench) {
                // Each is run with a Bencher: given it, if it takes one, or
                // timed whole by it, if not.
                thunks.Append("import Sable.Testing\n\n");
                closure = "(Bencher) -> Void";
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
        main.Append(CultureInfo.InvariantCulture, $"\n@main\nfn {EntryName}() -> Int {{\n    var all: {caseType}[>..] = []\n");
        for (var m = 0; m < modules.Count; m++) {
            main.Append(CultureInfo.InvariantCulture, $"    let list{m} = sableHarnessList{m}()\n");
            var i = 0;
            foreach (var test in modules[m]) {
                main.Append(CultureInfo.InvariantCulture, $"    all.append({caseType} {{ name: {Quote(test.QualifiedName)}, location: {Quote(test.Location)}, run: list{m}[{i++}] }})\n");
            }
        }

        main.Append(CultureInfo.InvariantCulture, $"    return {run}(all)\n}}\n");
        files.Add(SyntaxTree.Parse(new SourceText(main.ToString(), "<harness>")));
        return files;
    }

    /// <summary>The closure that runs one test from inside its module.</summary>
    static string Thunk(TestFunction test) => test switch {
        { Kind: TestKind.Test, Throws: false } => $"() => {{ {test.CallPath}(); return true }}",
        { Kind: TestKind.Test, Throws: true } => $"() => {{\n            do {{ try {test.CallPath}() }} catch {{ return false }}\n            return true\n        }}",
        { Kind: TestKind.Bench, TakesBencher: true } => $"(b: Bencher) => {test.CallPath}(b)",
        _ => $"(b: Bencher) => b.iterate(() => {test.CallPath}())",
    };

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
