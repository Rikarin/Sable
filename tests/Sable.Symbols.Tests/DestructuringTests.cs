namespace Sable.Symbols.Tests;

/// <summary>
///     <c>let (a, b) = pair</c> and <c>let { name, age } = person</c>, per
///     spec/SYNTAX.md §5.
/// </summary>
public sealed class DestructuringTests {
    const string Preamble = """
                            module M
                            struct Person { var name: String, var age: Int }
                            union Shape { Circle(r: Int), Square(side: Int) }
                            fn pair() -> (Int, String) { return (1, "a") }
                            """;

    static IReadOnlyList<Diagnostic> ErrorsIn(string body) => [
        ..Compilation.Create(
                $$"""
                  {{Preamble}}
                  fn probe(_ p: Person, _ s: Shape) -> Int {
                  {{body}}
                  }
                  """
            )
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static void AssertClean(string body) {
        var errors = ErrorsIn(body);
        Assert.True(
            errors.Count == 0,
            string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}"))
        );
    }

    static void AssertError(ErrorCode code, string body) => Assert.Contains(ErrorsIn(body), e => e.Code == code);

    [Fact]
    public void ATupleIsTakenApart() =>
        AssertClean(
            """
            let (n, label) = pair()
            let text: String = label
            return n
            """
        );

    [Fact]
    public void AWildcardDiscardsAnElement() =>
        AssertClean(
            """
            let (n, _) = pair()
            return n
            """
        );

    [Fact]
    public void ANestedTupleIsTakenApartToo() =>
        AssertClean(
            """
            let ((a, b), c) = ((1, 2), 3)
            return a + b + c
            """
        );

    [Fact]
    public void ARecordIsTakenApartByFieldName() =>
        AssertClean(
            """
            let { name, age } = p
            let text: String = name
            return age
            """
        );

    [Fact]
    public void ARecordFieldMayBeRenamed() =>
        AssertClean(
            """
            let { age: years } = p
            return years
            """
        );

    [Fact]
    public void TheNamesHaveTheElementTypes() =>
        AssertError(
            ErrorCode.ERR_TypeMismatch,
            """
            let (n, label) = pair()
            return label
            """
        );

    [Fact]
    public void LetNamesAreImmutable() =>
        AssertError(
            ErrorCode.ERR_NotAssignable,
            """
            let (n, _) = pair()
            n = 2
            return n
            """
        );

    [Fact]
    public void VarNamesAreMutable() =>
        AssertClean(
            """
            var (n, _) = pair()
            n = 2
            return n
            """
        );

    [Fact]
    public void VarRecordNamesAreMutable() =>
        AssertClean(
            """
            var { age } = p
            age += 1
            return age
            """
        );

    [Fact]
    public void AnAnnotationAppliesToTheValue() =>
        AssertClean(
            """
            let (a, b): (I64, I64) = (1, 2)
            return 0
            """
        );

    [Fact]
    public void ARefutablePatternIsReported() =>
        AssertError(
            ErrorCode.ERR_RefutablePatternInDeclaration,
            """
            let (1, label) = pair()
            return 0
            """
        );

    [Fact]
    public void ATupleOfTheWrongArityIsReported() =>
        Assert.NotEmpty(
            ErrorsIn(
                """
                let (a, b, c) = pair()
                return 0
                """
            )
        );

    [Fact]
    public void AForTakesEachElementApart() =>
        AssertClean(
            """
            let ages: [String: Int] = ["ada": 36]
            var sum = 0
            for (name, age) in ages where !name.isEmpty {
                let text: String = name
                sum = sum + age
            }
            let pairs: (Int, String)[>..] = [pair()]
            for (n, _) in pairs {
                sum = sum + n
            }
            return sum
            """
        );

    [Fact]
    public void AForWhosePatternMightNotMatchIsReported() =>
        AssertError(
            ErrorCode.ERR_RefutablePatternInDeclaration,
            """
            let pairs: (Int, String)[>..] = [pair()]
            for (1, label) in pairs {
            }
            return 0
            """
        );

    [Fact]
    public void ADictionaryHasItsMembers() =>
        AssertClean(
            """
            var ages: [String: Int] = ["ada": 36]
            let removed: Int? = ages.remove("ada")
            let keys: String[>..] = ages.keys()
            let values: Int[>..] = ages.values()
            ages.removeAll()
            return ages.count + keys.count + values.count + (ages.contains("ada") || ages.isEmpty ? 1 : 0)
            """
        );

    [Fact]
    public void AnUnknownFieldIsReported() =>
        AssertError(
            ErrorCode.ERR_NameNotFound,
            """
            let { height } = p
            return 0
            """
        );

    [Fact]
    public void ADestructuredNameIsDefinitelyAssigned() =>
        AssertClean(
            """
            let (n, _) = pair()
            let copy = n
            return copy
            """
        );

    [Fact]
    public void TheDataFlowSeesTheDeclaredNames() {
        var compilation = Compilation.Create(
            $$"""
              {{Preamble}}
              fn probe() -> Int {
                  let (n, label) = pair()
                  return n
              }
              """
        );

        var tree = compilation.SyntaxTrees[0];
        var body = tree.Root.DescendantNodes()
            .OfType<FunctionDeclarationSyntax>()
            .First(static f => f.Identifier.Text == "probe")
            .Body!;

        var flow = compilation.GetSemanticModel(tree).AnalyzeDataFlow(body.Statements[0]);
        Assert.Equal(
            ["label", "n"],
            flow.VariablesDeclared.Select(static s => s.Name).Order(StringComparer.Ordinal)
        );
        Assert.Equal(
            ["label", "n"],
            flow.WrittenInside.Select(static s => s.Name).Order(StringComparer.Ordinal)
        );
        Assert.Empty(flow.DataFlowsIn);
    }
}
