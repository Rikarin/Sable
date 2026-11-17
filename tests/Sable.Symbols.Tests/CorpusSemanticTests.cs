namespace Sable.Symbols.Tests;

/// <summary>
///     Programs that use only the built-in types and what they declare themselves,
///     which must type-check with nothing to say.
/// </summary>
/// <remarks>
///     This is the small version of the go/no-go in docs/04 §1.4: real code
///     written against the front end, before any backend exists, because that is
///     what finds the gaps.
/// </remarks>
public sealed class CorpusSemanticTests {
    static string CorpusDirectory {
        get {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Sable.slnx"))) {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "tests", "corpus");
        }
    }

    public static TheoryData<string> CorpusFiles {
        get {
            var data = new TheoryData<string>();
            foreach (var file in Directory.GetFiles(CorpusDirectory, "*.sab")) {
                data.Add(Path.GetFileName(file));
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void CorpusFileTypeChecks(string fileName) {
        var path = Path.Combine(CorpusDirectory, fileName);
        var source = new SourceText(File.ReadAllText(path), fileName);

        var compilation = Compilation.Create([SyntaxTree.Parse(source)]);
        var diagnostics = compilation.GetAllDiagnostics();

        var complaints = diagnostics
            .Where(static d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .ToList();

        Assert.True(
            complaints.Count == 0,
            $"{fileName} did not type-check:\n"
            + string.Join("\n\n", complaints.Take(8).Select(d => d.Format(source)))
        );
    }

    [Fact]
    public void TheWholeCorpusCompilesTogether() {
        var trees = Directory.GetFiles(CorpusDirectory, "*.sab")
            .Select(static file => SyntaxTree.Parse(new(File.ReadAllText(file), file)))
            .ToList();

        var compilation = Compilation.Create(trees);

        var errors = compilation.GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.True(
            errors.Count == 0,
            string.Join("\n", errors.Take(8).Select(static e => $"{e.Id}: {e.Message}"))
        );
    }
}
