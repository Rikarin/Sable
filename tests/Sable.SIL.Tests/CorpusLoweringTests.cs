namespace Sable.SIL.Tests;

/// <summary>
///     docs/04 §2.5: "SIL verifier is clean on the corpus". Every corpus file
///     is lowered; what lowers must verify, before and after the passes, and
///     what does not yet is reported by construct — never lowered wrong.
/// </summary>
public sealed class CorpusLoweringTests {
    public static TheoryData<string> Files {
        get {
            var data = new TheoryData<string>();
            foreach (var directory in new[] { "tests/corpus", "examples" }) {
                foreach (var file in Directory.GetFiles(Path.Combine(Root, directory), "*.sab").Order(StringComparer.Ordinal)) {
                    data.Add(Path.GetRelativePath(Root, file));
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void WhatLowersVerifies(string file) {
        var tree = SyntaxTree.Parse(new SourceText(File.ReadAllText(Path.Combine(Root, file)), Path.GetFileName(file)));
        var compilation = Compilation.Create([tree]);
        var (module, diagnostics) = SilGen.Lower(compilation);

        Assert.All(diagnostics, static d => Assert.Equal(ErrorCode.ERR_NotLoweredYet, d.Code));
        Assert.Empty(SilVerifier.Verify(module));

        SilPassManager.Run(module, SilPassManager.Default);
        Assert.Empty(SilVerifier.Verify(module));
    }

    static string Root {
        get {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sable.slnx"))) {
                directory = directory.Parent;
            }

            return directory!.FullName;
        }
    }
}
