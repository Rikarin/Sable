namespace Sable.Symbols.Tests;

/// <summary>
///     <c>where A == B</c> same-type requirements, per docs/03 §3.4: inside the
///     declaration the two are one type, and a call must make them the same.
/// </summary>
public sealed class SameTypeTests {
    const string Container = """
                             protocol Container {
                                 associatedtype Item
                                 fn first() -> Item?
                                 fn holds(_ item: Item) -> Bool
                             }
                             struct WordBag: Container {
                                 alias Item = String
                                 fn first() -> String? { return none }
                                 fn holds(_ item: String) -> Bool { return true }
                             }
                             struct Sentence: Container {
                                 alias Item = String
                                 fn first() -> String? { return none }
                                 fn holds(_ item: String) -> Bool { return true }
                             }
                             struct Numbers: Container {
                                 alias Item = Int
                                 fn first() -> Int? { return none }
                                 fn holds(_ item: Int) -> Bool { return true }
                             }
                             """;

    const string Shares = """
                          fn shares<A: Container, B: Container>(_ a: A, _ b: B) -> Bool
                              where A.Item == B.Item {
                              guard let x = a.first() else { return false }
                              return b.holds(x)
                          }
                          """;

    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compilation.Create($"module M\n{Container}\n{source}")
            .GetAllDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static void AssertRejected(string source) =>
        Assert.Contains(ErrorsIn(source), static e => e.Code == ErrorCode.ERR_NoMatchingOverload);

    static void AssertClean(string source) {
        var errors = ErrorsIn(source);
        Assert.True(
            errors.Count == 0,
            string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}"))
        );
    }

    [Fact]
    public void InsideTheBodyTheTwoAreOneType() => AssertClean(Shares);

    [Fact]
    public void WithoutTheRequirementTheyAreNot() =>
        AssertRejected(Shares.Replace("    where A.Item == B.Item ", " ", StringComparison.Ordinal));

    [Fact]
    public void ACallThatMakesThemTheSameIsAccepted() =>
        AssertClean($"{Shares}\nfn probe(_ w: WordBag, _ s: Sentence) -> Bool {{ return shares(w, s) }}");

    [Fact]
    public void ACallThatDoesNotIsRejected() =>
        AssertRejected($"{Shares}\nfn probe(_ w: WordBag, _ n: Numbers) -> Bool {{ return shares(w, n) }}");

    [Fact]
    public void AGenericCallerMustCarryTheRequirementToo() {
        AssertRejected(
            $"{Shares}\nfn relay<C: Container, D: Container>(_ c: C, _ d: D) -> Bool {{ return shares(c, d) }}"
        );

        AssertClean(
            $"{Shares}\nfn relay<C: Container, D: Container>(_ c: C, _ d: D) -> Bool where C.Item == D.Item {{ return shares(c, d) }}"
        );
    }

    [Fact]
    public void AConcreteRequirementMakesTheParameterThatType() =>
        AssertClean(
            """
            fn count<C: Container>(_ c: C) -> Int where C.Item == Int {
                guard let n = c.first() else { return 0 }
                return n + 1
            }
            fn probe(_ n: Numbers) -> Int { return count(n) }
            """
        );

    [Fact]
    public void AConcreteRequirementIsCheckedAtTheCall() =>
        AssertRejected(
            """
            fn count<C: Container>(_ c: C) -> Int where C.Item == Int { return 0 }
            fn probe(_ w: WordBag) -> Int { return count(w) }
            """
        );

    [Fact]
    public void ARequirementOnAParameterIsNotAConformance() {
        // 'T == Int' used to be recorded as 'T: Int'.
        var compilation = Compilation.Create(
            $"module M\n{Container}\nfn f<T>(_ x: T) -> Int where T == Int {{ return x + 1 }}"
        );
        var f = compilation.GetModule("M")!.Members.OfType<FunctionSymbol>().Single(static m => m.Name == "f");

        Assert.DoesNotContain(compilation.GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
        Assert.Empty(f.TypeParameters[0].Constraints);
        Assert.Single(f.SameTypeRequirements);
    }

    // ---- Conformance requirements on associated types ----------------------

    const string Ranked = """
                          protocol Ranked { fn rank() -> Int }
                          struct Card: Ranked { fn rank() -> Int { return 1 } }
                          struct Hand: Container {
                              alias Item = Card
                              fn first() -> Card? { return none }
                              fn holds(_ item: Card) -> Bool { return true }
                          }
                          fn best<C: Container>(_ c: C) -> Int where C.Item: Ranked {
                              guard let top = c.first() else { return 0 }
                              return top.rank()
                          }
                          """;

    [Fact]
    public void ARequirementOnAnAssociatedTypeGivesItsMembers() => AssertClean(Ranked);

    [Fact]
    public void WithoutItTheMemberIsNotThere() =>
        Assert.NotEmpty(ErrorsIn(Ranked.Replace(" where C.Item: Ranked", "", StringComparison.Ordinal)));

    [Fact]
    public void ACallMeetsItThroughTheConformer() =>
        AssertClean($"{Ranked}\nfn probe(_ h: Hand) -> Int {{ return best(h) }}");

    [Fact]
    public void ACallThatDoesNotMeetItIsRejected() =>
        AssertRejected($"{Ranked}\nfn probe(_ w: WordBag) -> Int {{ return best(w) }}");

    [Fact]
    public void AGenericCallerMustRequireItToo() {
        AssertRejected($"{Ranked}\nfn relay<D: Container>(_ d: D) -> Int {{ return best(d) }}");
        AssertClean($"{Ranked}\nfn relay<D: Container>(_ d: D) -> Int where D.Item: Ranked {{ return best(d) }}");
    }
}
