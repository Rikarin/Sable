namespace Sable.Symbols.Tests;

/// <summary>
///     Attributes (spec/SYNTAX.md §22): only those there are, where they
///     apply, once, with the arguments they take — and what <c>@deprecated</c>,
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
    [InlineData("fn f(@frozen _ x: Int) { }", "'@frozen' applies to a union, not to a parameter")]
    [InlineData("@frozen\nstruct S { }", "'@frozen' applies to a union, not to a struct")]
    [InlineData("@main\nstruct S { }", "'@main' applies to a function, not to a struct")]
    [InlineData("fn f(@escaping _ x: Int) { }", "'@escaping' applies to a closure parameter, and 'x' is of type 'Int'")]
    [InlineData("fn f(_ x: @escaping Int?) { }", "'@escaping' applies to a closure parameter, and 'x' is of type 'Int?'")]
    [InlineData("fn f(_ x: @discardable Int) { }", "'@discardable' applies to a function, not to a parameter")]
    [InlineData("struct S { @immortal var count: Int = 0 }", "'@immortal' applies to a 'static let', not to a property")]
    [InlineData("@flags\nstruct S { }", "'@flags' applies to an enum, not to a struct")]
    public void AnAttributeOnlyGoesWhereItApplies(string source, string message) {
        var error = Assert.Single(Errors($"module M\n{source}"));
        Assert.Equal(ErrorCode.ERR_AttributeDoesNotApply, error.Code);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    // A generator is run by the program made of a package's generators/
    // directory (docs/08 §8.1); anywhere else nothing would run it.
    [Theory]
    [InlineData("src/main.sb", false)]
    [InlineData("main.sb", false)]
    [InlineData("app/generators/tests/case/main.sb", false)]
    [InlineData("app/generators/json.sb", true)]
    [InlineData("generators/sub/json.sb", true)]
    [InlineData("lib/Sable.Testing/generators/Mockable.sb", true)]
    [InlineData(@"C:\app\generators\json.sb", true)]
    public void AGeneratorIsDeclaredInAGeneratorsDirectoryAlone(string file, bool accepted) {
        var tree = SyntaxTree.Parse(new SourceText("module M\n\n@generator(\"x\")\nfn g() { }\n", file));
        var errors = Compilation.Create([tree]).GetAllDiagnostics().Where(static d => d.Code == ErrorCode.ERR_AttributeDoesNotApply).ToList();
        if (accepted) {
            Assert.Empty(errors);
        } else {
            Assert.Contains($"'{file}' is not in one: nothing would run it", Assert.Single(errors).Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("@inline(sometimes)\nfn f() { }")]
    [InlineData("@deprecated(3)\nfn f() { }")]
    [InlineData("@available\nfn f() { }")]
    [InlineData("@align(12)\nstruct S { }")]
    [InlineData("@frozen(true)\nunion U { A, B }")]
    [InlineData("@main(1)\nfn f() { }")]
    [InlineData("@reflectable(true)\nstruct S { }")]
    [InlineData("@inline_layout(1)\nstruct S { }")]
    [InlineData("@available(macos: \"14.0\")\nfn f() { }")]
    [InlineData("@available(macOS)\nfn f() { }")]
    public void AnAttributeTakesTheArgumentsItReads(string source) =>
        Assert.Equal(ErrorCode.ERR_MalformedAttribute, Assert.Single(Errors($"module M\n{source}")).Code);

    [Theory]
    [InlineData("@inline(always)\n@inline(never)\nfn f() { }", "'@inline'")]
    [InlineData("@deprecated\n@deprecated(\"again\")\nfn f() { }", "'@deprecated'")]
    [InlineData("@noncopy @noncopy struct S { }", "'@noncopy'")]
    public void AnAttributeIsWrittenOnce(string source, string name) {
        var error = Assert.Single(Errors($"module M\n{source}"));
        Assert.Equal(ErrorCode.ERR_DuplicateAttribute, error.Code);
        Assert.Equal($"{name} is written twice on one declaration", error.Message);
    }

    [Fact]
    public void OnlySableCoreDeclaresAnIntrinsic() =>
        Assert.Equal(ErrorCode.ERR_BuiltinOutsideCore, Assert.Single(Errors("module M\nstruct S {\n    @intrinsic\n    static fn name() -> String\n}")).Code);

    /// <summary>'@main' marks a function the process can call with nothing, one per module.</summary>
    [Theory]
    [InlineData("struct S {\n    @main\n    fn start() { }\n}", "'start' is a function of the module, not a method")]
    [InlineData("fn outer() {\n    @main\n    fn start() { }\n}", "'start' is a function of the module, not a method or a local function")]
    [InlineData("@main\nfn start(_ n: Int) { }", "'start' takes no parameters")]
    [InlineData("@main\nfn start<T>() { }", "'start' is not generic")]
    [InlineData("@main\nfn start() -> String { return \"\" }", "returns nothing, or an 'Int' that is the exit status, not 'String'")]
    [InlineData("@main\nfn start() { }\n@main\nfn again() { }", "a module has one, and 'start' is it")]
    public void AMainIsWhereAProgramCanStart(string source, string why) {
        var error = Assert.Single(Errors($"module M\n{source}"), static e => e.Code == ErrorCode.ERR_InvalidEntryPoint);
        Assert.Contains(why, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMainReturnsNothingOrAnExitStatus() {
        AssertClean("module M\n@main\nfn start() { }");
        AssertClean("module M\n@main\nfn start() -> Int { return 0 }");
        AssertClean("module M\n@main\nasync fn start() { }");
    }

    [Fact]
    public void AnAlignmentIsAtMostWhatTheHeapGives() {
        var error = Assert.Single(Errors("module M\n@align(32)\nstruct S { var n: Int }"));
        Assert.Equal(ErrorCode.ERR_AlignTooLarge, error.Code);
        Assert.Contains("'@align(32)' is more than 16", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStructConformingToAttributeIsOne() =>
        AssertClean("""
                    module M
                    struct Route: Attribute {
                        var path: String
                        var method: String = "GET"
                    }
                    @Route("/users") @Route("/users", method: "POST") struct Users { }
                    @Route(path: "/f") fn f() { }
                    """);

    const string Attributes = """
                              module M
                              struct Route: Attribute {
                                  var path: String
                              }
                              struct Plain {
                                  var n: Int
                              }
                              fn base() -> String { return "/" }

                              """;

    /// <summary>The arguments are checked as a call to the struct's initialisers would be.</summary>
    [Theory]
    [InlineData("@Route(3)\nstruct S { }")]
    [InlineData("@Route(\"/a\", verb: \"GET\")\nstruct S { }")]
    [InlineData("@Route\nstruct S { }")]
    public void AnAttributesArgumentsAreAnInitialisers(string source) =>
        Assert.NotEmpty(Errors(Attributes + source));

    [Fact]
    public void AnAttributesArgumentsAreConstants() =>
        Assert.Equal(ErrorCode.ERR_AttributeArgumentNotConstant, Assert.Single(Errors(Attributes + "@Route(base())\nstruct S { }")).Code);

    [Fact]
    public void AStructNotConformingToAttributeIsNone() {
        var error = Assert.Single(Errors(Attributes + "@Plain(n: 1)\nstruct S { }"));
        Assert.Equal(ErrorCode.ERR_UnknownAttribute, error.Code);
        Assert.Contains("'Plain' is not an attribute", error.Message, StringComparison.Ordinal);
    }

    const string Shapes = """
                          module M
                          protocol Shape { fn area() -> Int }
                          class Base { }

                          """;

    [Theory]
    [InlineData("@polymorphic fn f(_ n: Int) -> Int { return n }", "it has no type parameters")]
    [InlineData("@polymorphic fn f<T: Base>(_ t: T) { }", "'T: Base' is not a protocol")]
    [InlineData("@polymorphic fn f<T: Equatable>(_ t: T) { }", "requires '==', which mentions Self")]
    [InlineData("@polymorphic fn f<T: Shape>(_ ts: T[>..]) { }", "'ts: T[>..]' holds a type parameter inside it")]
    [InlineData("@polymorphic fn f<T: Shape>() -> T? { return none }", "its result 'T?' holds a type parameter inside it")]
    [InlineData("@polymorphic fn f<T: Shape>(_ t: inout T) { }", "'t' is 'inout'")]
    [InlineData("struct G<U> { @polymorphic fn f<T: Shape>(_ t: T) { } }", "'G' around it is generic")]
    public void APolymorphicFunctionPassesItsValuesInBoxes(string source, string why) {
        var error = Assert.Single(Errors(Shapes + source), static e => e.Code == ErrorCode.ERR_NotPolymorphic);
        Assert.Contains(why, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APolymorphicFunctionWithBoxableValuesIsOne() =>
        AssertClean(Shapes + "@polymorphic fn larger<A: Shape, B: Shape>(_ a: A, _ b: B, _ n: Int) -> A { return a }");

    [Fact]
    public void TheAttributesThereAreAreAccepted() =>
        AssertClean("""
                    module M
                    @inline(always) fn a() { }
                    @deprecated fn b() { }
                    @deprecated("use a") fn c() { }
                    @available(macos, linux) fn d() { }
                    @align(16) struct E { }
                    @frozen union K { A, B }
                    @discardable fn f() -> Int { return 1 }
                    @immutable class G { let n: Int = 0 }
                    struct H { @immortal static let shared: Int = 1 }
                    fn i(@escaping _ k: () -> Int) -> Int { return k() }
                    fn l(_ k: @escaping (() -> Int)?) -> Int { return 0 }
                    @derive(Equatable, Copy) struct J { var n: Int }
                    @derive(Equatable) @derive(Hashable) struct N { var n: Int }
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
        // A compilation builds for the machine running it unless told
        // otherwise: one platform it is, and one it is not.
        var here = ParseOptions.Default.TargetOs;
        var elsewhere = here == "windows" ? "linux" : "windows";

        var error = Assert.Single(Errors($"module M\n@available({elsewhere})\nfn f() {{ }}\nfn g() {{ f() }}"));
        Assert.Equal(ErrorCode.ERR_NotAvailable, error.Code);
        Assert.Contains($"available only on {elsewhere}", error.Message, StringComparison.Ordinal);

        AssertClean($"module M\n@available({here})\nfn f() {{ }}\nfn g() {{ f() }}");
        AssertClean($"module M\n@available({elsewhere})\nfn f() {{ }}\n@available({elsewhere})\nfn g() {{ f() }}");
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
