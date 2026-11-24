namespace Sable.Symbols.Tests;

/// <summary>
///     Members of one name from more than one of a type's protocols: overloads,
///     told apart by their labels, none of them hiding another's.
/// </summary>
public sealed class ProtocolOverloadTests {
    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compilation.Create($"module M\n{source}")
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    const string Declarations = """
                                protocol Fixed { init() }
                                protocol Extended { init() }
                                extension Fixed {
                                    static fn hash(_ text: String) -> Int { return 1 }
                                    fn same() -> Int { return 1 }
                                }
                                extension Extended {
                                    static fn hash(_ text: String, count: Int) -> String { return text }
                                    fn same() -> String { return "" }
                                }
                                struct Both: Fixed, Extended { init() { } }
                                struct One: Fixed { init() { } }
                                """;

    [Fact]
    public void ATypeHasTheOverloadsOfEachOfItsProtocols() =>
        Assert.Empty(ErrorsIn(Declarations + """

            fn f() {
                let a: Int = Both.hash("x")
                let b: String = Both.hash("x", count: 1)
            }
            """));

    /// <summary>Of the same labels, the first protocol's is the one there is, as it was.</summary>
    [Fact]
    public void TheFirstProtocolsMemberHidesTheOthersOfItsLabels() =>
        Assert.Empty(ErrorsIn(Declarations + "\nfn f() { let a: Int = Both().same() }"));

    [Fact]
    public void AProtocolATypeIsNotGivesItNothing() =>
        Assert.NotEmpty(ErrorsIn(Declarations + "\nfn f() { let b: String = One.hash(\"x\", count: 1) }"));
}
