namespace Sable.Symbols.Tests;

/// <summary>
///     Who can see what, per spec/SYNTAX.md §2.
/// </summary>
public sealed class AccessibilityTests {
    const string Account = """
                           public struct Account {
                               public var id: Int
                               private var secret: String
                               internal var note: String
                               private fn audit() -> String { return secret }
                               public fn visible() -> String { return audit() }
                           }
                           """;

    static IReadOnlyList<Diagnostic> ErrorsIn(string source, string module = "M") => [
        ..Compilation.Create($"module {module}\n{Account}\n{source}")
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static void AssertClean(string source) {
        var errors = ErrorsIn(source);
        Assert.True(
            errors.Count == 0,
            string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}"))
        );
    }

    static void AssertHidden(string source) =>
        Assert.Contains(ErrorsIn(source), static e => e.Code == ErrorCode.ERR_NotAccessible);

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
    public void AnExtensionSeesThePrivateMembersOfWhatItExtends() =>
        AssertClean(
            """
            extension Account {
                fn alsoVisible() -> String { return audit() }
            }
            """
        );

    [Fact]
    public void ANestedTypeSeesTheEnclosingDeclarationsPrivateMembers() =>
        AssertClean(
            """
            struct Outer {
                private var hidden: Int
                struct Inner {
                    fn reach(_ o: Outer) -> Int { return o.hidden }
                }
            }
            """
        );

    [Fact]
    public void AnInternalMemberIsVisibleInTheSameModule() =>
        AssertClean("fn probe(_ a: Account) -> String { return a.note }");

    [Fact]
    public void APublicMemberIsAlwaysVisible() => AssertClean("fn probe(_ a: Account) -> Int { return a.id }");

    [Fact]
    public void AnInternalMemberIsHiddenFromAnotherModule() {
        var first = SyntaxTree.Parse(new("module First\npublic struct Box { internal var n: Int }", "a.sb"));
        var second = SyntaxTree.Parse(
            new("module Second\nimport First\nfn probe(_ b: Box) -> Int { return b.n }", "b.sb")
        );

        var compilation = Compilation.Create([first, second]);

        Assert.Contains(
            compilation.GetAllDiagnostics(),
            static d => d.Code == ErrorCode.ERR_NotAccessible
        );
    }

    [Fact]
    public void APublicMemberCrossesModules() {
        var first = SyntaxTree.Parse(new("module First\npublic struct Box { public var n: Int }", "a.sb"));
        var second = SyntaxTree.Parse(
            new("module Second\nimport First\nfn probe(_ b: Box) -> Int { return b.n }", "b.sb")
        );

        var errors = Compilation.Create([first, second])
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.True(
            errors.Count == 0,
            string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}"))
        );
    }

    // ---- Across files, modules and workspaces ------------------------------

    static IReadOnlyList<Diagnostic> ErrorsAcross(params (string Text, string File, string? Workspace)[] files) => [
        ..Compilation.Create(
                files.Select(static f => SyntaxTree.Parse(new(f.Text, f.File), ParseOptions.Default with { Workspace = f.Workspace }))
            )
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static IReadOnlyList<Diagnostic> ErrorsAcross(params (string Text, string File)[] files) =>
        ErrorsAcross([..files.Select(static f => (f.Text, f.File, (string?)null))]);

    static void AssertNone(IReadOnlyList<Diagnostic> errors) =>
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));

    /// <summary>An extension's internal member is its own module's, whoever declared the type.</summary>
    [Fact]
    public void AnInternalMemberOfAnExtensionIsVisibleInItsOwnModule() =>
        AssertClean(
            """
            extension String { fn twice() -> String { return self + self } }
            fn probe() -> String { return "a".twice() }
            """
        );

    [Fact]
    public void AnInternalMemberOfAnExtensionIsHiddenFromOtherModules() {
        var errors = ErrorsAcross(
            ("module First\npublic struct Box { public var n: Int }\nextension Box { fn doubled() -> Int { return n * 2 } }", "a.sb"),
            ("module Second\nimport First\nfn probe(_ b: Box) -> Int { return b.doubled() }", "b.sb")
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NotAccessible);
    }

    /// <summary>
    ///     An internal overload in a type's body does not hide a public one
    ///     in an extension from another module, nor the other way round (#351).
    /// </summary>
    [Theory]
    [InlineData(
        "public struct V {\n    var n: Int\n    static fn parse(_ n: Int, _ m: Int) -> V { return V(n: n + m) }\n}\nextension V {\n    public static fn parse(_ text: String) -> V { return V(n: text.count) }\n}"
    )]
    [InlineData(
        "public struct V {\n    var n: Int\n    public static fn parse(_ text: String) -> V { return V(n: text.count) }\n}\nextension V {\n    static fn parse(_ n: Int, _ m: Int) -> V { return V(n: n + m) }\n}"
    )]
    public void AnInternalOverloadDoesNotHideAPublicOneFromAnotherModule(string first) {
        var trees = new[] {
            SyntaxTree.Parse(new($"module First\n{first}", "a.sb")),
            SyntaxTree.Parse(new("module Second\nimport First\nfn probe() -> V { return V.parse(\"1.2\") }", "b.sb")),
        };
        var compilation = Compilation.Create(trees);
        AssertNone([..compilation.GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error)]);

        var call = trees[1].Root.DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var bound = Assert.IsType<BoundCall>(SemanticModel.Unconverted(compilation.GetSemanticModel(trees[1]).GetBoundNode(call)!));
        Assert.Equal(Accessibility.Public, bound.Function.DeclaredAccessibility);
    }

    /// <summary>Where every overload is hidden, that is still the error.</summary>
    [Fact]
    public void InternalOverloadsAloneAreReportedAsHidden() {
        var errors = ErrorsAcross(
            ("module First\npublic struct V {\n    var n: Int\n    static fn parse(_ n: Int) -> V { return V(n: n) }\n}\nextension V {\n    static fn parse(_ t: String) -> V { return V(n: t.count) }\n}", "a.sb"),
            ("module Second\nimport First\nfn probe() -> V { return V.parse(\"1.2\") }", "b.sb")
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NotAccessible);
    }

    [Fact]
    public void AnExtensionInAnotherModuleDoesNotSeePrivateMembers() {
        var errors = ErrorsAcross(
            ("module First\npublic struct Box { private var secret: Int }", "a.sb"),
            ("module Second\nimport First\nextension Box { fn peek() -> Int { return secret } }", "b.sb")
        );
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NotAccessible);
    }

    /// <summary>A private member written in an extension belongs to the type, and its extensions in the module.</summary>
    [Fact]
    public void APrivateMemberOfAnExtensionIsTheTypes() =>
        AssertClean(
            """
            extension Account { private fn tag() -> String { return "t" } }
            extension Account { fn tagged() -> String { return tag() } }
            """
        );

    /// <summary>At the top level, the enclosing declaration of a private one is its file.</summary>
    [Fact]
    public void ATopLevelPrivateDeclarationIsHiddenFromOtherFiles() {
        var errors = ErrorsAcross(
            ("module App\nprivate fn helper() -> Int { return 1 }\nprivate struct Local { var x: Int }\nfn here() -> Int { return helper() }", "a.sb"),
            ("module App\nfn probe() -> Int { return helper() }\nfn make() -> Int { return Local(x: 1).x }", "b.sb")
        );
        Assert.Equal(2, errors.Count(static e => e.Code == ErrorCode.ERR_NotAccessible));
        Assert.DoesNotContain(errors, static e => e.Code == ErrorCode.ERR_NameNotFound);
    }

    /// <summary>'package' reaches the workspace: its other modules, not the packages it depends on.</summary>
    [Fact]
    public void APackageMemberIsVisibleAcrossTheWorkspaceOnly() {
        const string library = "module Lib\npublic struct Box { package var n: Int }\npackage fn shared() -> Int { return 1 }";
        const string user = "module App\nimport Lib\nfn probe(_ b: Box) -> Int { return b.n + shared() }";

        AssertNone(ErrorsAcross((library, "lib.sb", null), (user, "app.sb", null)));

        var outside = ErrorsAcross((library, "lib.sb", "lib-1.0.0"), (user, "app.sb", null));
        Assert.Equal(2, outside.Count(static e => e.Code == ErrorCode.ERR_NotAccessible));
    }

    /// <summary>A name another module keeps to itself is there, and says so.</summary>
    [Fact]
    public void AnotherModulesInternalNameIsReportedAsHidden() {
        var errors = ErrorsAcross(
            ("module First\nfn hidden() -> Int { return 1 }\nstruct Kept { var x: Int }", "a.sb"),
            ("module Second\nimport First\nfn probe() -> Int { return hidden() }\nfn make(_ k: Kept) { }", "b.sb")
        );
        Assert.Equal(2, errors.Count(static e => e.Code == ErrorCode.ERR_NotAccessible));
        Assert.DoesNotContain(errors, static e => e.Code is ErrorCode.ERR_NameNotFound or ErrorCode.ERR_TypeNotFound);
    }

    // ---- What a signature exposes -------------------------------------------

    static void AssertExposes(string source) =>
        Assert.Contains(ErrorsIn(source), static e => e.Code == ErrorCode.ERR_ExposesHiddenType);

    [Fact]
    public void APublicFunctionCannotReturnAnInternalType() =>
        AssertExposes("struct Hidden { var x: Int }\npublic fn leak() -> Hidden { return Hidden(x: 1) }");

    [Fact]
    public void APublicFunctionCannotTakeAnInternalType() =>
        AssertExposes("struct Hidden { var x: Int }\npublic fn take(_ h: Hidden?) { }");

    [Fact]
    public void APublicFieldCannotHaveAnInternalType() =>
        AssertExposes("struct Hidden { var x: Int }\npublic struct Pub { public var h: Hidden }");

    [Fact]
    public void APublicSignatureCannotNameAnInternalProtocol() =>
        AssertExposes("protocol Secret { }\npublic fn take(_ s: any Secret) { }");

    [Fact]
    public void AnInternalSignatureMayUseAnInternalType() =>
        AssertClean("struct Hidden { var x: Int }\nfn keep() -> Hidden { return Hidden(x: 1) }\npublic struct Pub { var h: Hidden }");

    [Fact]
    public void APublicSignatureMayUseItsOwnTypeParameters() =>
        AssertClean("public struct Wrap<T> { public var value: T }\npublic fn wrap<T>(_ v: T) -> Wrap<T> { return Wrap(value: v) }");
}
