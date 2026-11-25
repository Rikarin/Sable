namespace Sable.Symbols.Tests;

/// <summary>The lint set of docs/05 §5.7: each lint, the levels, and the attributes that override them.</summary>
public sealed class LintTests {
    static IReadOnlyList<Diagnostic> Lint(string source, LintConfiguration? configuration = null) {
        var compilation = Compilation.Create(source);
        Assert.DoesNotContain(compilation.GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
        return Lints.Run(compilation, configuration ?? LintConfiguration.Default);
    }

    static List<string> Codes(string source, LintConfiguration? configuration = null) =>
        [..Lint(source, configuration).Select(static d => d.Id)];

    [Fact]
    public void UnusedLocals() {
        const string source = """
            module M
            fn f(_ xs: Int[]) -> Int {
                let unused = 1
                let _ignored = 2
                var written = 0
                written = 3
                for x in xs { }
                for _ in xs { }
                let used = 4
                return used
            }
            """;
        var found = Lint(source);
        Assert.Equal(
            ["constant 'unused'", "variable 'written'", "loop variable 'x'"],
            found.Where(static d => d.Code == ErrorCode.LNT_UnusedLocal).Select(static d => d.Message[..d.Message.IndexOf(" is", StringComparison.Ordinal)]));
        Assert.All(found, static d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.Contains("[unused_local]", found[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABindingReadOnlyInAClosureIsUsed() {
        const string source = """
            module M
            fn f() -> Int {
                let n = 2
                let g = (x: Int) => x * n
                return g(3)
            }
            """;
        Assert.Empty(Codes(source));
    }

    [Fact]
    public void UnusedParametersButNotWhereTheSignatureIsDictated() {
        const string source = """
            module M
            protocol Shape {
                fn area(scale: Int) -> Int
            }
            struct Square: Shape {
                var side: Int
                fn area(scale: Int) -> Int { return side * side }
                fn grow(by amount: Int, _ unusedToo: Int) -> Int { return side + amount }
            }
            fn ignore(_ _value: Int) { }
            fn first(_ a: Int, _ b: Int) -> Int { return a }
            """;
        var found = Lint(source).Where(static d => d.Code == ErrorCode.LNT_UnusedParameter).Select(static d => d.Message).ToList();
        Assert.Equal(2, found.Count);
        Assert.Contains(found, static m => m.Contains("'unusedToo'", StringComparison.Ordinal));
        Assert.Contains(found, static m => m.Contains("'b'", StringComparison.Ordinal));
    }

    [Fact]
    public void CodeNotCompiledUsesWhatItNames() {
        const string source = """
            module M
            fn pick(here: Int, there: Int, nowhere: Int) -> Int {
                let local = 1
                var changed = 2
                #if NEVER_DEFINED
                changed += 1
                return there + local + helper()
                #else
                return here + changed
                #endif
            }
            private fn helper() -> Int { return 1 }
            fn named<T>(of type: T.Type) -> Int { return 1 }
            """;
        var found = Assert.Single(Lint(source));
        Assert.Equal(ErrorCode.LNT_UnusedParameter, found.Code);
        Assert.Contains("'nowhere'", found.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADiscardNeedsNoLet() {
        const string source = """
            module M
            fn make() -> Int { return 1 }
            async fn f(_ xs: Int?) {
                let _ = make()
                _ = make()
                let _: I8 = 5
                async let _ = make()
                if let _ = xs { }
            }
            """;
        var found = Assert.Single(Lint(source));
        Assert.Equal(ErrorCode.LNT_LetDiscard, found.Code);
    }

    [Fact]
    public void ADiscardIsOnlyAnAssignmentsTarget() {
        var compilation = Compilation.Create("module M\nfn f() {\n    _ = none\n}\n");
        Assert.Contains(compilation.GetAllDiagnostics(), static d => d.Code == ErrorCode.ERR_CannotInferType);
        Assert.NotEmpty(SyntaxTree.Parse("module M\nfn f() {\n    let x = _\n}\n").Diagnostics);
        Assert.NotEmpty(SyntaxTree.Parse("module M\nfn f() {\n    _ += 1\n}\n").Diagnostics);
        Assert.Empty(SyntaxTree.Parse("module M\nfn f() {\n    _ = 1\n    let g = (n: Int) => _ = n\n}\n").Diagnostics);
    }

    [Fact]
    public void VarNeverMutated() {
        const string source = """
            module M
            struct P {
                var x: Int
                mutating fn bump() { x += 1 }
            }
            fn f() -> Int {
                var never = 1
                var assigned = 1
                assigned = 2
                var compound = 1
                compound += 1
                var p = P { x: 1 }
                p.x = 3
                var q = P { x: 1 }
                q.bump()
                var later: Int
                later = 4
                var passed = 1
                reset(out passed)
                var referenced = 1
                bump(&referenced)
                return never + assigned + compound + p.x + q.x + later + passed + referenced
            }
            fn bump(_ n: inout Int) { n += 1 }
            fn reset(out n: Int) { n = 0 }
            """;
        var found = Lint(source).Where(static d => d.Code == ErrorCode.LNT_VarNeverMutated).ToList();
        var single = Assert.Single(found);
        Assert.Contains("'never'", single.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShadowingAParameter() {
        const string source = """
            module M
            fn f(_ value: Int?, _ limit: Int) -> Int {
                guard let value = value else { return 0 }
                let limit = limit * 2
                return value + limit
            }
            """;
        var found = Lint(source).Where(static d => d.Code == ErrorCode.LNT_ShadowedParameter).ToList();
        Assert.Contains("'limit'", Assert.Single(found).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnusedImport() {
        var compilation = Compilation.Create(
            "module A\npublic fn helper() -> Int { return 1 }\n",
            "module B\npublic fn other() -> Int { return 2 }\n",
            "module C\nimport A\nimport B\nfn f() -> Int { return helper() }\n");
        var found = Lints.Run(compilation, LintConfiguration.Default, [compilation.SyntaxTrees[2]]);
        var unused = Assert.Single(found);
        Assert.Equal(ErrorCode.LNT_UnusedImport, unused.Code);
        Assert.Contains("'B'", unused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationSetsTheLevel() {
        const string source = "module M\nfn f() {\n    let x = 1\n}\n";
        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(Lint(source, LintConfiguration.Default.With("unused_local", LintLevel.Error))).Severity);
        Assert.Empty(Lint(source, LintConfiguration.Default.With("unused_local", LintLevel.Allow)));
        Assert.Throws<ArgumentException>(() => LintConfiguration.Default.With("nonsense", LintLevel.Warn));
    }

    [Fact]
    public void ReadsALintsTable() {
        var configuration = LintConfiguration.FromTable(
            [new("unused_local", "allow"), new("shadowed_parameter", "error"), new("nonsense", "warn"), new("unused_import", "loud")],
            out var errors);
        Assert.Equal(2, errors.Count);
        Assert.Equal(LintLevel.Allow, configuration.LevelOf(Lints.Find("unused_local")!));
        Assert.Equal(LintLevel.Error, configuration.LevelOf(Lints.Find("shadowed_parameter")!));
        Assert.Equal(LintLevel.Warn, configuration.LevelOf(Lints.Find("unused_import")!));
    }

    [Fact]
    public void AttributesOverrideTheLevelInsideTheirDeclaration() {
        const string source = """
            module M
            @allow(unused_local)
            fn quiet() {
                let x = 1
            }
            fn loud() {
                let y = 1
            }
            @allow(unused_local)
            struct S {
                @warn(unused_local)
                fn f() {
                    let z = 1
                }
                fn g() {
                    let w = 1
                }
            }
            """;
        var found = Lint(source, LintConfiguration.Default).Select(static d => d.Message).ToList();
        Assert.Equal(2, found.Count);
        Assert.Contains(found, static m => m.Contains("'y'", StringComparison.Ordinal));
        Assert.Contains(found, static m => m.Contains("'z'", StringComparison.Ordinal));

        // '@warn' turns on a lint the configuration has off.
        var configured = Lint(source, LintConfiguration.Default.With("unused_local", LintLevel.Allow)).Select(static d => d.Message).ToList();
        Assert.Contains("'z'", Assert.Single(configured), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownLintNameInAnAttributeIsReported() {
        const string source = "module M\n@allow(unused_locals, unused_local)\nfn f() {\n    let x = 1\n}\n";
        var found = Lint(source);
        var unknown = Assert.Single(found);
        Assert.Equal(ErrorCode.LNT_UnknownLint, unknown.Code);
        Assert.Contains("'unused_locals'", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryLintHasACodeInItsRangeAndAUniqueName() {
        Assert.Equal(Lints.All.Count, Lints.All.Select(static l => l.Name).Distinct().Count());
        Assert.All(Lints.All, static l => Assert.InRange((int)l.Code, 3300, 3398));
    }
}
