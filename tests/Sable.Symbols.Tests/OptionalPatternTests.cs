namespace Sable.Symbols.Tests;

/// <summary>
///     An optional's cases spelled out, SYNTAX.md §4 and §15: <c>.Some(p)</c>,
///     <c>.None</c> and <c>p?</c> as patterns, <c>.Some(x)</c> and <c>.None</c>
///     as values; and what a bare binding binds after earlier arms took some
///     of the subject's values.
/// </summary>
public sealed class OptionalPatternTests {
    const string Types = """
                         union Outcome { Ok(Int), Failed(String) }
                         """;

    static IReadOnlyList<Diagnostic> DiagnosticsIn(string source) => [
        ..Compilation.Create($"module M\n{Types}\n{source}").GetAllDiagnostics()
    ];

    static void AssertClean(string source) {
        var diagnostics = DiagnosticsIn(source);
        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics.Select(static d => $"{d.Id}: {d.Message}")));
    }

    /// <summary>The type of the first local named <paramref name="name" />.</summary>
    static string TypeOfLocal(string source, string name) {
        var compilation = Compilation.Create($"module M\n{Types}\n{source}");
        var errors = compilation.GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));

        var tree = compilation.SyntaxTrees[0];
        var model = compilation.GetSemanticModel(tree);
        var binding = tree.Root.DescendantNodes().OfType<BindingPatternSyntax>().First(b => b.Identifier.Text == name);
        return ((LocalSymbol)model.GetSymbolOfToken(binding.Identifier)!).Type.ToDisplayString();
    }

    // ---- Patterns -----------------------------------------------------------

    [Fact]
    public void SomeAndNoneCoverAnOptional() =>
        AssertClean(
            """
            fn f(_ o: Int?) -> Int {
                return match o {
                    case .Some(3) => 30
                    case .Some(let v) => v
                    case .None => 0
                }
            }
            """
        );

    [Fact]
    public void SomeNestsAUnionCase() =>
        AssertClean(
            """
            fn f(_ o: Outcome?) -> Int {
                return match o {
                    case .Some(.Ok(let v)) => v
                    case .Some(.Failed(_)) => -1
                    case .None => 0
                }
            }
            """
        );

    [Fact]
    public void AMissingNestedCaseIsNamedInsideSome() {
        var error = Assert.Single(
            DiagnosticsIn(
                """
                fn f(_ o: Outcome?) -> Int {
                    return match o {
                        case .Some(.Ok(let v)) => v
                        case .None => 0
                    }
                }
                """
            ),
            static d => d.Code == ErrorCode.ERR_MatchNotExhaustive
        );
        Assert.Contains("'.Some(.Failed(_))'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuestionMarkPatternIsSome() =>
        AssertClean(
            """
            fn f(_ o: Int?) -> Int {
                if case let v? = o {
                    return v
                }
                return match o {
                    case let v? => v
                    case none => 0
                }
            }
            """
        );

    [Fact]
    public void AQuestionMarkOnAPlainValueIsAnError() =>
        Assert.Contains(
            DiagnosticsIn(
                """
                fn f(_ n: Int) -> Int {
                    return match n {
                        case let v? => v
                        default => 0
                    }
                }
                """
            ),
            static d => d.Code == ErrorCode.ERR_PatternTypeMismatch
        );

    [Fact]
    public void AnOptionalHasOnlyTwoCases() =>
        Assert.Contains(
            DiagnosticsIn(
                """
                fn f(_ o: Int?) -> Int {
                    return match o {
                        case .Other => 1
                        default => 0
                    }
                }
                """
            ),
            static d => d.Code == ErrorCode.ERR_UnknownUnionCase
        );

    [Fact]
    public void SomeTakesOneValue() =>
        Assert.Contains(
            DiagnosticsIn(
                """
                fn f(_ o: Int?) -> Int {
                    return match o {
                        case .Some => 1
                        default => 0
                    }
                }
                """
            ),
            static d => d.Code == ErrorCode.ERR_WrongPayloadCount
        );

    [Fact]
    public void AnArmAfterSomeWithAWildcardIsCovered() =>
        Assert.Contains(
            DiagnosticsIn(
                """
                fn f(_ o: Int?) -> Int {
                    return match o {
                        case .Some(_) => 1
                        case .None => 0
                        case let v? => v
                    }
                }
                """
            ),
            static d => d.Code == ErrorCode.WRN_PatternAlreadyCovered
        );

    // ---- Values -------------------------------------------------------------

    [Fact]
    public void SomeAndNoneAreValues() =>
        AssertClean(
            """
            fn take(_ o: Int?) -> Int? { return o }

            fn f() -> Int? {
                let three: Int? = .Some(3)
                let nothing: Int? = .None
                take(.Some(4))
                take(.None)
                take(nothing)
                return three
            }
            """
        );

    [Fact]
    public void AWrappedTypesOwnSomeIsTheOneMeant() =>
        // 'Outcome?' asks for an Outcome; '.Ok' is Outcome's, not the optional's.
        AssertClean(
            """
            fn f() -> Outcome? {
                return .Ok(1)
            }
            """
        );

    // ---- What a bare binding binds -----------------------------------------

    [Fact]
    public void ABindingAfterNoneIsTheValue() =>
        Assert.Equal(
            "Int",
            TypeOfLocal(
                """
                fn f(_ o: Int?) -> Int {
                    return match o {
                        case none => 0
                        case let v => v + 1
                    }
                }
                """,
                "v"
            )
        );

    [Fact]
    public void AGuardedNoneLeavesTheOptional() =>
        Assert.Equal(
            "Int?",
            TypeOfLocal(
                """
                fn f(_ o: Int?, _ go: Bool) -> Int? {
                    return match o {
                        case none where go => 0
                        case let v => v
                    }
                }
                """,
                "v"
            )
        );

    [Fact]
    public void ABindingAfterTheOtherCasesOfAUnionIsTheLast() =>
        Assert.Equal(
            "Int",
            TypeOfLocal(
                """
                fn f(_ o: Int | String | Bool) -> Int {
                    return match o {
                        case let s as String => 1
                        case is Bool => 2
                        case let rest => rest
                    }
                }
                """,
                "rest"
            )
        );

    [Fact]
    public void ABindingWithTwoCasesLeftKeepsTheUnion() =>
        Assert.Equal(
            "Bool | Int | String",
            TypeOfLocal(
                """
                fn count(_ v: Int | String | Bool) -> Int { return 0 }

                fn f(_ o: Int | String | Bool) -> Int {
                    return match o {
                        case is Bool => 2
                        case let rest => count(rest)
                    }
                }
                """,
                "rest"
            )
        );
}
