using Sable.Symbols;

namespace Sable.Symbols.Tests;

/// <summary>
/// Who can see what, per spec/SYNTAX.md §2.
/// </summary>
public sealed class AccessibilityTests
{
    private const string Account = """
        public struct Account {
            public var id: Int
            private var secret: String
            internal var note: String
            private fn audit() -> String { return secret }
            public fn visible() -> String { return audit() }
        }
        """;

    private static IReadOnlyList<Diagnostic> ErrorsIn(string source, string module = "M") =>
        [.. Compilation.Create($"module {module}\n{Account}\n{source}").GetAllDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)];

    private static void AssertClean(string source)
    {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 0,
            string.Join("\n", errors.Select(e => $"{e.Id}: {e.Message}")));
    }

    private static void AssertHidden(string source) =>
        Assert.Contains(ErrorsIn(source), e => e.Code == ErrorCode.ERR_NotAccessible);

    [Fact]
    public void APrivateFieldIsHiddenOutsideItsType() =>
        AssertHidden("fn probe(_ a: Account) -> String { return a.secret }");

    [Fact]
    public void APrivateMethodIsHiddenOutsideItsType() =>
        AssertHidden("fn probe(_ a: Account) -> String { return a.audit() }");

    [Fact]
    public void APrivateMemberIsVisibleInside() =>
        AssertClean("fn probe(_ a: Account) -> String { return a.visible() }");

    [Fact]
    public void AnExtensionSeesThePrivateMembersOfWhatItExtends() => AssertClean("""
        extension Account {
            fn alsoVisible() -> String { return audit() }
        }
        """);

    [Fact]
    public void ANestedTypeSeesTheEnclosingDeclarationsPrivateMembers() => AssertClean("""
        struct Outer {
            private var hidden: Int
            struct Inner {
                fn reach(_ o: Outer) -> Int { return o.hidden }
            }
        }
        """);

    [Fact]
    public void AnInternalMemberIsVisibleInTheSameModule() =>
        AssertClean("fn probe(_ a: Account) -> String { return a.note }");

    [Fact]
    public void APublicMemberIsAlwaysVisible() =>
        AssertClean("fn probe(_ a: Account) -> Int { return a.id }");

    [Fact]
    public void AnInternalMemberIsHiddenFromAnotherModule()
    {
        var first = SyntaxTree.Parse(new SourceText(
            "module First\npublic struct Box { internal var n: Int }", "a.sab"));
        var second = SyntaxTree.Parse(new SourceText(
            "module Second\nimport First\nfn probe(_ b: Box) -> Int { return b.n }", "b.sab"));

        var compilation = Compilation.Create([first, second]);

        Assert.Contains(compilation.GetAllDiagnostics(),
            d => d.Code == ErrorCode.ERR_NotAccessible);
    }

    [Fact]
    public void APublicMemberCrossesModules()
    {
        var first = SyntaxTree.Parse(new SourceText(
            "module First\npublic struct Box { public var n: Int }", "a.sab"));
        var second = SyntaxTree.Parse(new SourceText(
            "module Second\nimport First\nfn probe(_ b: Box) -> Int { return b.n }", "b.sab"));

        var errors = Compilation.Create([first, second]).GetAllDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

        Assert.True(errors.Count == 0,
            string.Join("\n", errors.Select(e => $"{e.Id}: {e.Message}")));
    }
}
