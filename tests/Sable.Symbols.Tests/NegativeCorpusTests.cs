using System.Text.RegularExpressions;

namespace Sable.Symbols.Tests;

/// <summary>
///     Programs that must fail, each saying exactly how.
/// </summary>
/// <remarks>
///     <para>
///         This is the negative corpus of docs/04 §1.4. A file in
///         <c>tests/negative</c> is an ordinary Sable program with its expectations
///         written beside the lines that carry them:
///     </para>
///     <code>
/// let x: Int = "s"   //! S3004 expected 'Int'
/// </code>
///     <para>
///         A <c>//!</c> comment claims a diagnostic on that line: its code, and
///         optionally a substring its message must contain. The match is exact in
///         both directions — a diagnostic with no expectation fails the file just as
///         an expectation with no diagnostic does — so a file cannot quietly start
///         reporting something else.
///     </para>
/// </remarks>
public sealed class NegativeCorpusTests {
    static string Directory {
        get {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Sable.slnx"))) {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "tests", "negative");
        }
    }

    public static TheoryData<string> Files {
        get {
            var data = new TheoryData<string>();
            foreach (var file in System.IO.Directory.GetFiles(Directory, "*.sb")) {
                data.Add(Path.GetFileName(file));
            }

            return data;
        }
    }

    private sealed record Expectation(int Line, string Code, string? Fragment) {
        public override string ToString() => Fragment is null ? $"{Line}: {Code}" : $"{Line}: {Code} \"{Fragment}\"";
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void EachFileReportsExactlyWhatItClaims(string fileName) {
        var path = Path.Combine(Directory, fileName);
        var text = File.ReadAllText(path);

        var expected = Parse(text);
        Assert.True(expected.Count > 0, $"{fileName} claims no diagnostics");

        var compilation = Compilation.Create(text);
        var source = compilation.SyntaxTrees[0].Text;

        var actual = compilation.GetAllDiagnostics()
            .Where(static d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(d => (
                    Line: source.GetLinePosition(d.Span.Start).Line,
                    d.Id,
                    d.Message)
            )
            .ToList();

        var unmatched = new List<Expectation>();
        var remaining = actual.ToList();

        foreach (var expectation in expected) {
            var index = remaining.FindIndex(d =>
                d.Line == expectation.Line
                && string.Equals(d.Id, expectation.Code, StringComparison.Ordinal)
                && (expectation.Fragment is null
                    || d.Message.Contains(expectation.Fragment, StringComparison.Ordinal))
            );

            if (index < 0) {
                unmatched.Add(expectation);
                continue;
            }

            remaining.RemoveAt(index);
        }

        var complaints = new List<string>();
        foreach (var expectation in unmatched) {
            complaints.Add($"  expected but not reported: {expectation}");
        }

        foreach (var diagnostic in remaining) {
            complaints.Add(
                $"  reported but not expected: {diagnostic.Line}: "
                + $"{diagnostic.Id}: {diagnostic.Message}"
            );
        }

        Assert.True(
            complaints.Count == 0,
            $"{fileName}\n{string.Join("\n", complaints)}"
        );
    }

    /// <summary>Reads the <c>//!</c> claims out of a file.</summary>
    static List<Expectation> Parse(string text) {
        var expectations = new List<Expectation>();
        var lines = text.Split('\n');

        for (var i = 0; i < lines.Length; i++) {
            foreach (Match match in ClaimPattern.Matches(lines[i])) {
                var fragment = match.Groups[2].Success ? match.Groups[2].Value.Trim() : null;
                expectations.Add(
                    new(
                        i + 1,
                        match.Groups[1].Value,
                        string.IsNullOrEmpty(fragment) ? null : fragment
                    )
                );
            }
        }

        return expectations;
    }

    static readonly Regex ClaimPattern =
        new(@"//!\s*(S\d{4})(?:\s+([^/\n]*))?", RegexOptions.Compiled);

    /// <summary>The corpus has to be big enough to be worth the name.</summary>
    [Fact]
    public void TheCorpusCoversEveryDiagnosticItCanReach() {
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var cases = 0;

        foreach (var file in System.IO.Directory.GetFiles(Directory, "*.sb")) {
            foreach (var expectation in Parse(File.ReadAllText(file))) {
                claimed.Add(expectation.Code);
                cases++;
            }
        }

        // docs/04 §1.4 asks for at least 600 programs that must fail, each
        // asserting an exact error code, span and message.
        Assert.True(cases >= 600, $"the negative corpus has only {cases} cases");
        Assert.True(
            claimed.Count >= 90,
            $"the negative corpus covers only {claimed.Count} distinct diagnostics"
        );
    }
}
