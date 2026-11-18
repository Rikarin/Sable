namespace Sable.Symbols.Tests;

/// <summary>
///     The lints added for #204 — shadowed locals, self-assignment, unused
///     private declarations, constant arithmetic that traps — the fixes
///     lints carry, and plugins. (A 'catch' with nothing to catch needs no
///     lint: lowering already reports its body unreachable, S6005.)
/// </summary>
public sealed class MoreLintTests {
    static IReadOnlyList<Diagnostic> Lint(string source, LintConfiguration? configuration = null) {
        var compilation = Compilation.Create(source);
        Assert.DoesNotContain(compilation.GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
        return Lints.Run(compilation, configuration ?? LintConfiguration.Default);
    }

    static List<string> Messages(string source, ErrorCode code) =>
        [..Lint(source).Where(d => d.Code == code).Select(static d => d.Message)];

    /// <summary>The source with every fix the lints offer applied, as <c>sable fix</c> applies them.</summary>
    static string Fixed(string source) {
        var changes = Lint(source).SelectMany(static d => d.Fixes.Take(1)).SelectMany(static f => f.Changes).OrderByDescending(static c => c.Span.Start);
        foreach (var change in changes) {
            source = source[..change.Span.Start] + change.NewText + source[change.Span.End..];
        }

        return source;
    }

    [Fact]
    public void ALocalHidingOneFromAnOuterBlock() {
        const string source = """
            module M
            fn f(_ n: Int) -> Int {
                let x = n
                if x > 0 {
                    let x = 1
                    return x
                }
                for i in [1, 2] {
                    if i > 1 {
                        let i = 3
                        return i
                    }
                }
                return x
            }
            """;
        var found = Messages(source, ErrorCode.LNT_ShadowedLocal);
        Assert.Equal(2, found.Count);
        Assert.StartsWith("'x' hides the local of that name declared on line 3", found[0], StringComparison.Ordinal);
        Assert.StartsWith("'i' hides", found[1], StringComparison.Ordinal);
    }

    [Fact]
    public void RebindingInTheSameBlockOrInASiblingIsNotShadowing() {
        const string source = """
            module M
            fn f(_ n: Int?) -> Int {
                if true {
                    let y = 1
                    print(y)
                }
                let y = 2
                if let v = n {
                    print(v)
                }
                if true {
                    let v = 3
                    print(v)
                }
                return y
            }
            """;
        Assert.Empty(Messages(source, ErrorCode.LNT_ShadowedLocal));
    }

    [Fact]
    public void AssigningAPlaceItsOwnValue() {
        const string source = """
            module M
            struct Counter {
                var count: Int
                mutating fn reset(_ count: Int) {
                    self.count = self.count
                    self.count = count
                }
            }
            fn f(_ n: Int) -> Int {
                var x = n
                x = x
                x = n
                return x
            }
            """;
        var found = Lint(source).Where(static d => d.Code == ErrorCode.LNT_SelfAssignment).ToList();
        Assert.Equal(["'self.count'", "'x'"], found.Select(static d => d.Message[..d.Message.IndexOf(" is", StringComparison.Ordinal)]));

        // The fix takes the line away.
        var fixedSource = Fixed(source);
        Assert.DoesNotContain("self.count = self.count", fixedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("x = x\n", fixedSource, StringComparison.Ordinal);
        Assert.Contains("        self.count = count\n", fixedSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivateFunctionsAndTypesNothingUses() {
        const string source = """
            module M
            private fn helper() -> Int { return 1 }
            private fn unused() -> Int { return 2 }
            private fn recursive(_ n: Int) -> Int { return n == 0 ? 0 : recursive(n - 1) }
            private struct Hidden {
                var x: Int
            }
            private struct Shown {
                var x: Int
            }
            public struct Box {
                private fn twice() -> Int { return 2 }
                private fn spare() -> Int { return 3 }
                public fn value() -> Int { return twice() }
            }
            public fn run() -> Int {
                let s = Shown(x: 1)
                return helper() + s.x
            }
            """;
        var found = Messages(source, ErrorCode.LNT_UnusedPrivate);
        Assert.Equal(
            ["the private function 'unused' is never used [unused_private]",
                "the private function 'recursive' is never used [unused_private]",
                "the private type 'Hidden' is never used [unused_private]",
                "the private function 'spare' is never used [unused_private]"],
            found);
    }

    [Fact]
    public void APrivateFunctionUsedFromAnotherFileIsUsed() {
        var a = SyntaxTree.Parse(new SourceText("module M\npublic struct S {\n    private fn helper() -> Int { return 1 }\n}\n", "a.sb"));
        var b = SyntaxTree.Parse(new SourceText("module M\nextension S {\n    public fn run() -> Int { return helper() }\n}\n", "b.sb"));
        var compilation = Compilation.Create([a, b]);
        Assert.DoesNotContain(compilation.GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(Lints.Run(compilation, LintConfiguration.Default), static d => d.Code == ErrorCode.LNT_UnusedPrivate);
    }

    [Fact]
    public void ArithmeticThatTrapsWheneverItRuns() {
        const string source = """
            module M
            fn f(_ n: Int) -> Int {
                let b = n / 0
                let c = n % (2 - 2)
                let d = 9223372036854775807 + 1
                let fine = n / 2 + (3 * 4)
                return b + c + d + fine
            }
            """;
        var found = Messages(source, ErrorCode.LNT_ConstantArithmetic);
        Assert.Equal(3, found.Count);
        Assert.Contains("'n / 0' divides by zero", found[0], StringComparison.Ordinal);
        Assert.Contains("'n % (2 - 2)' divides by zero", found[1], StringComparison.Ordinal);
        Assert.Contains("is 9223372036854775808, which does not fit in 'Int'", found[2], StringComparison.Ordinal);
    }

    [Fact]
    public void TheFixesOfTheOlderLints() {
        const string source = """
            module M
            fn area(width: Int, _ height: Int) -> Int {
                let perimeter = 2
                var unchanged = 3
                twice(unchanged)
                return 4
            }
            fn twice(_ n: Int) -> Int { return n * 2 }
            """;
        var fixedSource = Fixed(source);
        Assert.Contains("fn area(width _width: Int, _ _height: Int)", fixedSource, StringComparison.Ordinal);
        Assert.Contains("let _perimeter = 2", fixedSource, StringComparison.Ordinal);
        Assert.Contains("let unchanged = 3", fixedSource, StringComparison.Ordinal);
        Assert.Contains("let _ = twice(unchanged)", fixedSource, StringComparison.Ordinal);

        // Fixed, the file lints clean.
        Assert.Empty(Lint(fixedSource));
    }

    [Fact]
    public void AFixDoesNotMakeDiagnosticsUnequal() {
        var plain = Diagnostic.Warning(ErrorCode.LNT_UnusedLocal, new TextSpan(1, 2), "m");
        var withFix = plain with { Fixes = [LintContext.Fix("f", TextChange.Insert(1, "_"))] };
        Assert.Equal(plain, withFix);
        Assert.Equal(plain.GetHashCode(), withFix.GetHashCode());
    }

    [Fact]
    public void APluginsLintsRunBesideTheBuiltInOnes() {
        var plugin = Lints.LoadPlugin(typeof(NoPrintLint).Assembly.Location);
        Assert.Contains(plugin.Lints, static l => l.Name == "no_print");

        const string source = "module M\nfn f() {\n    print(1)\n}\n@allow(no_print)\nfn g() {\n    print(2)\n}\n";
        var configuration = LintConfiguration.Default.WithPlugin(plugin);
        var found = Lint(source, configuration);
        var report = Assert.Single(found, static d => d.Message.EndsWith("[no_print]", StringComparison.Ordinal));
        Assert.Equal(ErrorCode.LNT_PluginLint, report.Code);
        Assert.Equal(DiagnosticSeverity.Warning, report.Severity);

        // Its level is configured like any lint's, and its name is known to '@allow'.
        var table = new[] { new KeyValuePair<string, string>("no_print", "error") };
        var levels = LintConfiguration.FromTable(table, out var errors, configuration);
        Assert.Empty(errors);
        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(Lint(source, levels), static d => d.Message.EndsWith("[no_print]", StringComparison.Ordinal)).Severity);
        Assert.DoesNotContain(found, static d => d.Code == ErrorCode.LNT_UnknownLint);

        // Without the plugin, its name is unknown.
        LintConfiguration.FromTable(table, out errors);
        Assert.Contains("no lint named 'no_print'", Assert.Single(errors), StringComparison.Ordinal);
    }

    [Fact]
    public void APluginLintThatThrowsIsReportedAndTheOthersRun() {
        var configuration = LintConfiguration.Default.WithPlugin(new("test", [new ThrowingLint()]));
        var found = Lint("module M\nfn f() {\n    let x = 1\n}\n", configuration);
        Assert.Contains(found, static d => d.Code == ErrorCode.LNT_PluginFailed && d.Message.Contains("'explodes' failed", StringComparison.Ordinal));
        Assert.Contains(found, static d => d.Code == ErrorCode.LNT_UnusedLocal);
    }

    [Fact]
    public void APluginCannotTakeABuiltInName() {
        var error = Assert.Throws<ArgumentException>(() => LintConfiguration.Default.WithPlugin(new("clash.dll", [new ClashingLint()])));
        Assert.Contains("'unused_local'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatIsNotAPluginIsSaid() {
        Assert.Contains("no lint plugin", Assert.Throws<LintPluginException>(() => Lints.LoadPlugin("/no/such/plugin.dll")).Message, StringComparison.Ordinal);
        var text = Path.Combine(Path.GetTempPath(), $"not-a-plugin-{Guid.NewGuid():N}.dll");
        File.WriteAllText(text, "not an assembly");
        try {
            Assert.Contains("cannot load", Assert.Throws<LintPluginException>(() => Lints.LoadPlugin(text)).Message, StringComparison.Ordinal);
        } finally {
            File.Delete(text);
        }
    }
}

/// <summary>A lint as a plugin would write it: only the public API.</summary>
public sealed class NoPrintLint : Lint {
    public override string Name => "no_print";
    public override LintLevel DefaultLevel => LintLevel.Warn;
    public override string Description => "a call to 'print'";

    public override void Check(LintContext context) {
        foreach (var body in context.Bodies) {
            foreach (var call in body.Body.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>()) {
                if (call.Expression.ToString().Trim() == "print") {
                    context.Report(call, "'print' left in");
                }
            }
        }
    }
}

/// <summary>Not public, so a plugin's loader does not find it: it is handed to the configuration directly.</summary>
sealed class ThrowingLint : Lint {
    public override string Name => "explodes";
    public override LintLevel DefaultLevel => LintLevel.Warn;
    public override string Description => "throws";
    public override void Check(LintContext context) => throw new InvalidOperationException("boom");
}

sealed class ClashingLint : Lint {
    public override string Name => "unused_local";
    public override LintLevel DefaultLevel => LintLevel.Warn;
    public override string Description => "clashes";
    public override void Check(LintContext context) { }
}
