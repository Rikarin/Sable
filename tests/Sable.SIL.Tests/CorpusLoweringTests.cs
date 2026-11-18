namespace Sable.SIL.Tests;

/// <summary>
///     docs/04 §2.5: "SIL verifier is clean on the corpus". Every corpus file
///     lowers, whole, and verifies before and after the passes.
/// </summary>
public sealed class CorpusLoweringTests {
    public static TheoryData<string> Files {
        get {
            var data = new TheoryData<string>();
            foreach (var directory in new[] { "tests/corpus", "examples", "tests/programs" }) {
                foreach (var file in Directory.GetFiles(Path.Combine(Root, directory), "*.sb").Order(StringComparer.Ordinal)) {
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

        // docs/04 §2.5: everything in the corpus lowers. The examples import
        // library modules this test does not bring (ExampleProgramTests builds
        // and runs them whole), so for them what lowers is all that is held.
        if (file.StartsWith("examples", StringComparison.Ordinal)) {
            Assert.All(diagnostics, static d => Assert.Equal(ErrorCode.ERR_NotLoweredYet, d.Code));
        } else {
            Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics.Select(static d => d.Message)));
        }
        Assert.Empty(SilVerifier.Verify(module));

        SilPassManager.Run(module, SilPassManager.Default);
        Assert.Empty(SilVerifier.Verify(module));
    }

    /// <summary>And what an optimised build makes of it: devirtualised, inlined, simplified.</summary>
    [Theory]
    [MemberData(nameof(Files))]
    public void WhatLowersVerifiesOptimised(string file) {
        var tree = SyntaxTree.Parse(new SourceText(File.ReadAllText(Path.Combine(Root, file)), Path.GetFileName(file)));
        var (module, _) = SilGen.Lower(Compilation.Create([tree]));
        SilPassManager.Run(module, SilPassManager.Optimising);
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
