using System.Globalization;
using System.Text;
using Sable.CodeGen;
using Sable.Symbols;
using Sable.Syntax;

namespace Sable.Driver;

/// <summary>
///     The fuzz targets' part of a harness: the whole of a fuzz build's, and
///     what a test build replays their saved inputs with.
/// </summary>
public static partial class TestHarness {
    /// <summary>
    ///     A fuzz build's harness: in each module with targets, a function
    ///     listing a closure per target, as a test build's; then what C calls
    ///     (<see cref="FuzzBuild.Shim" />) — <c>sable_harness_fuzz_begin</c>, then
    ///     <c>sable_harness_fuzz_push</c> for each byte of an input, since only
    ///     integers cross to C, then <c>sable_harness_fuzz_run</c> with the number of
    ///     the target, in <paramref name="targets" />' order, to run on it. When a
    ///     target has a mutator, <c>sable_harness_fuzz_mutate</c> makes another
    ///     input of the one pushed, and gives its length (-1 for a target with
    ///     none), and <c>sable_harness_fuzz_mutated</c> its bytes.
    /// </summary>
    static IReadOnlyList<SyntaxTree> WriteFuzz(IReadOnlyList<TestFunction> targets, ParseOptions options) {
        var files = new List<SyntaxTree>();
        var main = new StringBuilder();
        main.Append(CultureInfo.InvariantCulture, $"module {HarnessModule}\n\n");
        var run = new StringBuilder();
        var mutate = new StringBuilder();
        foreach (var (list, module) in FuzzModules(targets, files, options)) {
            main.Append(CultureInfo.InvariantCulture, $"import {module[0].Target.Module}.{{ {list}{(module.Any(static t => t.Target.Mutator is not null) ? $", {list}Mutators" : "")} }}\n");
            var mutators = 0;
            foreach (var ((target, number), i) in module.Select(static (t, i) => (t, i))) {
                run.Append(CultureInfo.InvariantCulture, $"    if target == {number} {{\n        {list}()[{i}](sableFuzzInput)\n        return\n    }}\n");
                if (target.Mutator is not null) {
                    mutate.Append(CultureInfo.InvariantCulture, $"    if target == {number} {{\n        sableFuzzMutated = {list}Mutators()[{mutators++}](sableFuzzInput, seed)\n        return sableFuzzMutated.count\n    }}\n");
                }
            }
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
        if (mutate.Length > 0) {
            main.Append("""

                // Another input made of the one given: how long it is, and its bytes.
                var sableFuzzMutated: U8[>..] = []

                @export("c", "sable_harness_fuzz_mutated")
                fn sableFuzzMutatedByte(_ at: Int) -> U8 {
                    return sableFuzzMutated[at]
                }

                @export("c", "sable_harness_fuzz_mutate")
                fn sableFuzzMutate(_ target: Int, _ seed: U64) -> Int {

                """).Append(mutate).Append("    return -1\n}\n");
        }

        files.Add(SyntaxTree.Parse(new SourceText(main.ToString(), "<harness>"), options));
        return files;
    }

    /// <summary>
    ///     What a test build replays the targets' saved inputs with: the file
    ///     of each module's targets, and for each target a <c>TestCase</c> whose
    ///     cases are the inputs <c>sable test</c> lists (Sable.Testing's <c>fuzzReplays</c>).
    /// </summary>
    /// <returns>The harness's imports, and the statements adding the tests to <c>all</c>.</returns>
    static (string Imports, string Statements) WriteReplays(IReadOnlyList<TestFunction> targets, List<SyntaxTree> files, ParseOptions options) {
        var imports = new StringBuilder();
        var statements = new StringBuilder();
        foreach (var (list, module) in FuzzModules(targets, files, options)) {
            imports.Append(CultureInfo.InvariantCulture, $"import {module[0].Target.Module}.{{ {list} }}\n");
            foreach (var ((target, _), i) in module.Select(static (t, i) => (t, i))) {
                var name = Quote(target.QualifiedName);
                statements.Append(CultureInfo.InvariantCulture,
                    $"    if wantsTest({name}) {{\n        all.append(TestCase(name: {name}, location: {Quote(target.Location)}, run: {{ scope in }}, tags: [\"fuzz\"], timeLimit: {ReplayLimit.Ticks * 100}, cases: () => fuzzReplays({name}, {list}()[{i}]), suite: {Quote(target.Module)}))\n    }}\n");
            }
        }

        return (imports.ToString(), statements.ToString());
    }

    /// <summary>How long a target's saved failure, or its corpus, may take in <c>sable test</c>: one that hangs fails.</summary>
    static readonly TimeSpan ReplayLimit = TimeSpan.FromSeconds(120);

    /// <summary>
    ///     Adds to <paramref name="files" />, for each module with targets, a
    ///     file of it that lists a closure per target taking the input's
    ///     bytes, and one per mutator; gives the function that lists them and
    ///     the targets, each with its number.
    /// </summary>
    static IReadOnlyList<(string List, IReadOnlyList<(TestFunction Target, int Number)> Targets)> FuzzModules(IReadOnlyList<TestFunction> targets, List<SyntaxTree> files, ParseOptions options) {
        var modules = new List<(string, IReadOnlyList<(TestFunction, int)>)>();
        foreach (var module in targets.Select(static (t, i) => (Target: t, Number: i)).GroupBy(static t => t.Target.Module)) {
            var list = string.Create(CultureInfo.InvariantCulture, $"sableHarnessFuzz{modules.Count}");
            var thunks = new StringBuilder();
            thunks.Append(CultureInfo.InvariantCulture, $"module {module.Key}\n\nimport Sable.Testing.{{ fuzzCall }}\n\n// Written by the compiler for 'sable fuzz'.\npublic fn {list}() -> ((U8[:]) -> Void)[>..] {{\n    return [\n");
            foreach (var (target, _) in module) {
                thunks.Append("        ").Append(FuzzThunk(target)).Append(",\n");
            }

            thunks.Append("    ]\n}\n");
            var mutators = module.Select(static t => t.Target.Mutator).OfType<string>().ToList();
            if (mutators.Count > 0) {
                thunks.Append(CultureInfo.InvariantCulture, $"\npublic fn {list}Mutators() -> ((U8[:], U64) -> U8[>..])[>..] {{\n    return [\n");
                foreach (var mutator in mutators) {
                    thunks.Append(CultureInfo.InvariantCulture, $"        (input: U8[:], seed: U64) => {mutator}(input, seed),\n");
                }

                thunks.Append("    ]\n}\n");
            }

            files.Add(SyntaxTree.Parse(new SourceText(thunks.ToString(), $"<harness:fuzz:{module.Key}>"), options));
            modules.Add((list, [..module]));
        }

        return modules;
    }

    /// <summary>The closure that runs a target on an input: what it throws fails it, as a trap does.</summary>
    static string FuzzThunk(TestFunction target) {
        var name = Escape(target.QualifiedName);
        var parameters = target.Symbol.Parameters;
        if (!target.TakesBytes) {
            return $"(data: U8[:]) => {{ fuzzCall(\"{name}\", data, {target.CallPath}) }}";
        }

        var data = parameters[0] is { Label: "_" } ? "data" : $"{parameters[0].Label ?? parameters[0].Name}: data";
        return target.Throws
            ? $"(data: U8[:]) => {{\n            do {{ try {target.CallPath}({data}) }} catch {{ fatalError(\"{name} threw: \\(error)\") }}\n        }}"
            : $"(data: U8[:]) => {{ {target.CallPath}({data}) }}";
    }
}
