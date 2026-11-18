namespace Sable.Symbols.Tests;

/// <summary>
///     Attributes (spec/SYNTAX.md §22): only those there are, where they
///     apply, with the arguments they take — and what <c>@deprecated</c>,
///     <c>@available</c>, <c>@frozen</c>, <c>@immutable</c>,
///     <c>@discardable</c> and <c>@derive(Copy)</c> do.
/// </summary>
public sealed class AttributeTests {
    static IReadOnlyList<Diagnostic> Diagnostics(params string[] sources) =>
        [..Compilation.Create(sources).GetAllDiagnostics().Where(static d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)];

    static IReadOnlyList<Diagnostic> Errors(params string[] sources) =>
        [..Diagnostics(sources).Where(static d => d.Severity == DiagnosticSeverity.Error)];

    static void AssertClean(params string[] sources) {
        var found = Diagnostics(sources);
        Assert.True(found.Count == 0, string.Join("\n", found.Select(static e => $"{e.Id}: {e.Message}")));
    }

    [Fact]
    public void AnAttributeThereIsNotIsAnError() {
        var error = Assert.Single(Errors("module M\n@frobnicate\nstruct S { }"));
        Assert.Equal(ErrorCode.ERR_UnknownAttribute, error.Code);
        Assert.Contains("'@frobnicate'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("@noncopy\nfn f() { }", "'@noncopy' applies to a struct or a union, not to a function")]
    [InlineData("@test\nstruct S { }", "'@test' applies to a function, not to a struct")]
    [InlineData("fn f(@frozen _ x: Int) { }", "'@frozen' applies to a struct, a union or an enum, not to a parameter")]
    [InlineData("struct S { @immortal var count: Int = 0 }", "'@immortal' applies to a 'static let', not to a property")]
    [InlineData("@flags\nstruct S { }", "'@flags' applies to an enum, not to a struct")]
    public void AnAttributeOnlyGoesWhereItApplies(string source, string message) {
        var error = Assert.Single(Errors($"module M\n{source}"));
        Assert.Equal(ErrorCode.ERR_AttributeDoesNotApply, error.Code);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("@inline(sometimes)\nfn f() { }")]
    [InlineData("@deprecated(3)\nfn f() { }")]
    [InlineData("@available\nfn f() { }")]
    [InlineData("@align(12)\nstruct S { }")]
    [InlineData("@frozen(true)\nstruct S { }")]
    public void AnAttributeTakesTheArgumentsItReads(string source) =>
        Assert.Equal(ErrorCode.ERR_MalformedAttribute, Assert.Single(Errors($"module M\n{source}")).Code);

    [Fact]
    public void TheAttributesThereAreAreAccepted() =>
        AssertClean("""
                    module M
                    @inline(always) fn a() { }
                    @deprecated fn b() { }
                    @deprecated("use a") fn c() { }
                    @available(macos: "14.0", linux) fn d() { }
                    @align(16) @frozen struct E { }
                    @discardable fn f() -> Int { return 1 }
                    @immutable class G { let n: Int = 0 }
                    struct H { @immortal static let shared: Int = 1 }
                    fn i(@escaping _ k: () -> Int) -> Int { return k() }
                    @derive(Equatable, Copy) struct J { var n: Int }
                    """);

    [Fact]
    public void ADeprecatedDeclarationIsWarnedOfWhereItIsUsed() {
        var found = Diagnostics("""
                                module M
                                @deprecated("use 'area'")
                                fn size() -> Int { return 1 }
                                @deprecated
                                struct Old { var n: Int }
                                fn use() -> Int {
                                    let o: Old = Old { n: size() }
                                    return o.n
                                }
                                @deprecated
                                fn alsoOld() -> Int { return size() }
                                """);

        Assert.All(found, static d => Assert.Equal(ErrorCode.WRN_Deprecated, d.Code));
        Assert.Contains(found, static d => d.Message == "'size' is deprecated: use 'area'");
        Assert.Contains(found, static d => d.Message == "'Old' is deprecated");

        // Inside something deprecated itself, the use is not reported.
        Assert.Equal(1, found.Count(static d => d.Message.StartsWith("'size'", StringComparison.Ordinal)));
    }

    [Fact]
    public void ADeclarationIsAvailableOnlyOnThePlatformsItNames() {
        var error = Assert.Single(Errors("module M\n@available(windows)\nfn f() { }\nfn g() { f() }"));
        Assert.Equal(ErrorCode.ERR_NotAvailable, error.Code);
        Assert.Contains("available only on windows", error.Message, StringComparison.Ordinal);

        AssertClean("module M\n@available(macos)\nfn f() { }\nfn g() { f() }");
        AssertClean("module M\n@available(windows)\nfn f() { }\n@available(windows)\nfn g() { f() }");
    }

    const string Library = """
                           module Lib
                           public union Shape { Circle(r: Int), Square(s: Int) }
                           @frozen public union Answer { Yes, No }
                           """;

    /// <summary>A public union of another module may gain cases unless it is '@frozen' (docs/03 §3.3).</summary>
    [Fact]
    public void ANonFrozenUnionOfAnotherModuleNeedsADefault() {
        var error = Assert.Single(Errors(Library, """
                                                  module App
                                                  import Lib
                                                  fn f(_ s: Shape) -> Int {
                                                      return match s {
                                                          case .Circle(_) => 1
                                                          case .Square(_) => 2
                                                      }
                                                  }
                                                  """));
        Assert.Equal(ErrorCode.ERR_MatchNotExhaustive, error.Code);
        Assert.Contains("not '@frozen'", error.Message, StringComparison.Ordinal);

        AssertClean(Library, """
                              module App
                              import Lib
                              fn f(_ s: Shape, _ a: Answer) -> Int {
                                  let n = match s {
                                      case .Circle(_) => 1
                                      case .Square(_) => 2
                                      default => 0
                                  }
                                  return match a {
                                      case .Yes => n
                                      case .No => 0
                                  }
                              }
                              """);

        // In its own module, every case is every case.
        AssertClean(Library + """

                              fn g(_ s: Shape) -> Int {
                                  return match s {
                                      case .Circle(_) => 1
                                      case .Square(_) => 2
                                  }
                              }
                              """);
    }

    [Fact]
    public void AnImmutableClassCannotChange() {
        var errors = Errors("""
                            module M
                            class Box { var n: Int = 0 }
                            @immutable class Frozen { let n: Int = 0 }
                            @immutable class C {
                                var a: Int = 0
                                let b: Box? = none
                                let c: Frozen[] = []
                                let d: String = ""
                            }
                            """);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, static e => Assert.Equal(ErrorCode.ERR_NotDeeplyImmutable, e.Code));
        Assert.Contains(errors, static e => e.Message.Contains("'a' cannot be a 'var'", StringComparison.Ordinal));
        Assert.Contains(errors, static e => e.Message.Contains("'b' cannot hold 'Box'", StringComparison.Ordinal));
    }

    [Fact]
    public void DeriveCopyHoldsOnlyOfACopiedType() {
        var error = Assert.Single(Errors("module M\n@noncopy @derive(Copy) struct Lock { var fd: Int }"));
        Assert.Equal(ErrorCode.ERR_CannotDerive, error.Code);
    }

    [Fact]
    public void AnUnusedResultIsLintedUnlessDiscardable() {
        var compilation = Compilation.Create("""
                                             module M
                                             fn value() -> Int { return 1 }
                                             @discardable fn counted() -> Int { return 1 }
                                             fn nothing() { }
                                             fn f() {
                                                 value()
                                                 counted()
                                                 nothing()
                                                 _ = value()
                                             }
                                             """);
        var found = Lints.Run(compilation, LintConfiguration.Default);
        var unused = Assert.Single(found, static d => d.Code == ErrorCode.LNT_UnusedResult);
        Assert.Contains("the result of 'value' is unused", unused.Message, StringComparison.Ordinal);
    }
}
