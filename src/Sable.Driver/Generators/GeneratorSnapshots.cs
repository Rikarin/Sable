using System.Text;
using System.Text.RegularExpressions;
using Sable.Syntax;

namespace Sable.Driver.Generators;

/// <summary>What running a package's generator snapshot cases gave.</summary>
public sealed record SnapshotOutcome(int Passed, int Failed, int Blessed);

/// <summary>
///     Snapshot tests of a package's generators (docs/08 §8.8): each directory
///     under <c>generators/tests/</c> is a case, whose <c>.sb</c> files the
///     package's generators run over, compared with its <c>expected/</c>.
/// </summary>
public static partial class GeneratorSnapshots {
    const string Expected = "expected";
    const string InputsDirectory = "inputs";
    const string DiagnosticsFile = "diagnostics.txt";

    /// <summary>
    ///     Runs every case of the package in <paramref name="packageDirectory" />,
    ///     reporting each to <paramref name="output" />; with <paramref name="bless" />,
    ///     writes each case's <c>expected/</c> from what the generators produce.
    ///     Null when the package has no cases.
    /// </summary>
    public static SnapshotOutcome? Run(string package, string packageDirectory, bool bless, TextWriter output) {
        var tests = GeneratorSet.TestsDirectory(packageDirectory);
        if (!Directory.Exists(tests)) {
            return null;
        }

        var cases = Directory.GetDirectories(tests).Order(StringComparer.Ordinal).ToList();
        if (cases.Count == 0) {
            return null;
        }

        var set = GeneratorSet.Load(package, packageDirectory, f => Path.GetRelativePath(packageDirectory, f).Replace(Path.DirectorySeparatorChar, '/'));
        output.WriteLine($"running {cases.Count} generator snapshot case(s)");
        var (passed, failed, blessed) = (0, 0, 0);
        foreach (var directory in cases) {
            var name = $"generators/tests/{Path.GetFileName(directory)}";
            var actual = Produce(package, set, directory);
            if (bless) {
                Write(directory, actual);
                output.WriteLine($"test {name} ... blessed");
                blessed++;
                continue;
            }

            var differences = Compare(Read(directory), actual);
            if (differences.Count == 0) {
                output.WriteLine($"test {name} ... ok");
                passed++;
                continue;
            }

            output.WriteLine($"test {name} ... FAILED");
            foreach (var difference in differences) {
                output.WriteLine(difference);
            }

            failed++;
        }

        output.WriteLine();
        output.WriteLine(failed == 0
            ? $"generator snapshots: ok. {passed} passed{(blessed > 0 ? $", {blessed} blessed" : "")}"
            : $"generator snapshots: FAILED. {passed} passed; {failed} failed. 'sable test --bless' writes expected/ from what the generators produce now");
        output.WriteLine();
        return new(passed, failed, blessed);
    }

    /// <summary>What a case's <c>expected/</c> should hold: each generated file by its path within it, and the diagnostics.</summary>
    static SortedDictionary<string, string> Produce(string package, GeneratorSet? set, string directory) {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var options = ParseOptions.Default with {
            GeneratorAttributes = [..set?.Attributes.Types ?? []],
            GeneratorMemberAttributes = [..set?.Attributes.Members ?? []],
        };
        var trees = SourceFiles(directory)
            .Select(f => SyntaxTree.Parse(new SourceText(File.ReadAllText(f), Relative(directory, f)), options))
            .ToList();
        var inputs = Directory.Exists(Path.Combine(directory, InputsDirectory))
            ? Directory.GetFiles(Path.Combine(directory, InputsDirectory), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(f => new GeneratorInputFile(Relative(Path.Combine(directory, InputsDirectory), f), File.ReadAllText(f))).ToList()
            : [];

        var result = set is null ? GenerationResult.Empty : SourceGenerators.Run(new GenerationInput(package, [set], trees) { Inputs = inputs });
        var diagnostics = new StringBuilder();
        foreach (var (diagnostic, tree) in result.Diagnostics) {
            var at = tree.Text.GetLinePosition(diagnostic.Span.Start);
            diagnostics.Append($"{(diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning")} {diagnostic.Id} {tree.Text.FileName}:{at.Line}:{at.Column}: {diagnostic.Message}\n");
        }

        var generated = new List<SyntaxTree>();
        foreach (var file in result.Files) {
            var path = $"{file.Generator}/{file.FileName}";
            files[path] = file.Text;
            generated.Add(SyntaxTree.Parse(new SourceText(file.Text, $"{Expected}/{path}"), file.Origin.Options));
        }

        // What the generators wrote has to compile with the case: a snapshot
        // of code that does not is no test of it.
        if (!result.HasErrors && trees.Count > 0) {
            foreach (var error in Pipeline.Check([..trees, ..generated])) {
                diagnostics.Append(Summary(error)).Append('\n');
            }
        }

        if (diagnostics.Length > 0) {
            files[DiagnosticsFile] = diagnostics.ToString();
        }

        return files;
    }

    /// <summary>A case's own source: its <c>.sb</c> files, but those under <c>expected/</c> and <c>inputs/</c>.</summary>
    static IEnumerable<string> SourceFiles(string directory) {
        var skipped = new[] { Expected, InputsDirectory }.Select(d => Path.Combine(directory, d) + Path.DirectorySeparatorChar).ToList();
        return Directory.GetFiles(directory, "*.sb", SearchOption.AllDirectories)
            .Where(f => !skipped.Any(s => f.StartsWith(s, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal);
    }

    /// <summary>A compiler error as one line: <c>error S2002 model.sb:3:5: message</c>.</summary>
    static string Summary(string formatted) {
        var lines = formatted.Split('\n');
        var head = HeadLine().Match(lines[0]);
        var where = lines.Select(static l => WhereLine().Match(l)).FirstOrDefault(static m => m.Success);
        return head.Success
            ? $"{head.Groups[1].Value} {head.Groups[2].Value} {(where is null ? "" : where.Groups[1].Value + ": ")}{head.Groups[3].Value}"
            : lines[0];
    }

    [GeneratedRegex(@"^(error|warning)\[(S\d+)\][^:]*: (.*)$")]
    private static partial Regex HeadLine();

    [GeneratedRegex(@"^\s*-->\s*(\S+)")]
    private static partial Regex WhereLine();

    static SortedDictionary<string, string> Read(string directory) {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var expected = Path.Combine(directory, Expected);
        if (Directory.Exists(expected)) {
            foreach (var file in Directory.GetFiles(expected, "*", SearchOption.AllDirectories)) {
                files[Relative(expected, file)] = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal);
            }
        }

        return files;
    }

    static void Write(string directory, SortedDictionary<string, string> files) {
        var expected = Path.Combine(directory, Expected);
        if (Directory.Exists(expected)) {
            Directory.Delete(expected, true);
        }

        Directory.CreateDirectory(expected);
        foreach (var (path, text) in files) {
            var full = Path.Combine(expected, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
    }

    /// <summary>What differs between what a case expects and what the generators produced, each said with both versions.</summary>
    static List<string> Compare(SortedDictionary<string, string> expected, SortedDictionary<string, string> actual) {
        var differences = new List<string>();
        foreach (var path in expected.Keys.Union(actual.Keys).Order(StringComparer.Ordinal)) {
            var had = expected.GetValueOrDefault(path);
            var got = actual.GetValueOrDefault(path);
            if (had == got) {
                continue;
            }

            differences.Add(had is null ? $"    {path}: written, but not expected:\n{Indented(got!)}"
                : got is null ? $"    {path}: expected, but not written:\n{Indented(had)}"
                : $"    {path} differs at line {FirstDifference(had, got)}:\n    expected:\n{Indented(had)}\n    written:\n{Indented(got)}");
        }

        return differences;
    }

    static int FirstDifference(string a, string b) {
        var (left, right) = (a.Split('\n'), b.Split('\n'));
        var line = 0;
        while (line < left.Length && line < right.Length && left[line] == right[line]) {
            line++;
        }

        return line + 1;
    }

    static string Indented(string text) => string.Join("\n", text.TrimEnd('\n').Split('\n').Select(static l => "        " + l));

    static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
}
