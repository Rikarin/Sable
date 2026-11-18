namespace Sable.Symbols.Tests;

/// <summary>
///     A property written <c>var x: T =&gt; e</c>, and one kept in
///     <c>field</c>, per spec/SYNTAX.md §13.
/// </summary>
public sealed class PropertyTests {
    static Compilation Compile(string source) => Compilation.Create($"module M\n{source}");

    static IReadOnlyList<Diagnostic> ErrorsIn(string source) => [
        ..Compile(source).GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error)
    ];

    static void AssertClean(string source) {
        var errors = ErrorsIn(source);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => $"{e.Id}: {e.Message}")));
    }

    static FieldSymbol Field(Compilation compilation, string type, string name) =>
        compilation.GetTypeByQualifiedName($"M.{type}")!.Members.OfType<FieldSymbol>().Single(f => f.Name == name);

    [Fact]
    public void AnArrowPropertyIsAGetterAlone() {
        var compilation = Compile("struct R { var w: Int\n    var twice: Int => w * 2 }");
        var twice = Field(compilation, "R", "twice");
        Assert.True(twice.IsComputed);
        Assert.False(twice.HasSetter);
        Assert.DoesNotContain(compilation.GetAllDiagnostics(), static d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void AnArrowPropertyCannotBeAssigned() =>
        Assert.NotEmpty(ErrorsIn("struct R { var w: Int\n    var twice: Int => w * 2 }\nfn f() { var r = R { w: 1 }\n    r.twice = 4 }"));

    [Fact]
    public void FieldIsThePropertysOwnStorage() {
        const string source = """
                              struct Account {
                                  var owner: String = "" {
                                      get => field
                                      set => field = newValue.trimmed()
                                  }
                              }
                              fn f() -> String {
                                  var a = Account { }
                                  a.owner = " ada "
                                  return a.owner
                              }
                              """;
        AssertClean(source);
        var compilation = Compile(source);
        var owner = Field(compilation, "Account", "owner");
        Assert.True(owner.IsComputed);
        var backing = Assert.IsType<FieldSymbol>(owner.BackingField);
        Assert.False(backing.IsComputed);
        Assert.Same(owner, backing.BackedProperty);
        Assert.Equal("String", backing.Type.ToDisplayString());
    }

    [Fact]
    public void FieldNeedsAnInitialValue() =>
        Assert.Contains(ErrorsIn("struct S { var n: Int { get => field } }"), static e => e.Code == ErrorCode.ERR_BackedPropertyNeedsInitializer);

    [Fact]
    public void AnExtensionCannotKeepAPropertyInField() {
        var errors = ErrorsIn("struct P { var x: Int }\nextension P { var y: Int { get => field } }");
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_ExtensionStoredProperty);
        Assert.DoesNotContain(errors, static e => e.Code == ErrorCode.ERR_NameNotFound);
    }

    [Fact]
    public void AMemberCalledFieldIsNotTheStorage() {
        const string source = "struct Cell { var field: Int }\nstruct Row { var cell: Cell\n    var value: Int { get => cell.field } }";
        AssertClean(source);
        Assert.Null(Field(Compile(source), "Row", "value").BackingField);
    }

    [Fact]
    public void FieldOutsideAnAccessorIsJustAName() =>
        Assert.Contains(ErrorsIn("fn f() -> Int { return field }"), static e => e.Code == ErrorCode.ERR_NameNotFound);

    [Theory]
    [InlineData("struct S { var n: Int { get } }")]
    [InlineData("struct S { var n: Int { get => 1\n    set } }")]
    [InlineData("struct S { fn f() -> Int }")]
    [InlineData("class C { fn g() }")]
    [InlineData("struct S { subscript(i: Int) -> Int { get } }")]
    [InlineData("struct S { }\nextension S { fn f() }")]
    [InlineData("fn free() -> Int")]
    public void AMemberWithNoBodyIsReported(string source) =>
        Assert.Contains(ErrorsIn(source), static e => e.Code == ErrorCode.ERR_MissingBody);

    [Theory]
    [InlineData("protocol P { var n: Int { get }\n    fn f() -> Int\n    subscript(i: Int) -> Int { get } }")]
    [InlineData("abstract class Shape { abstract fn area() -> Int }")]
    [InlineData("struct S { var n: Int = 0 { get\n    set => field = newValue } }")]
    public void ARequirementOrSuppliedBodyIsNot(string source) =>
        Assert.DoesNotContain(ErrorsIn(source), static e => e.Code == ErrorCode.ERR_MissingBody);

    [Theory]
    [InlineData("struct S { lazy let a: Int = 1 }")]
    [InlineData("struct S { lazy var b: Int }")]
    [InlineData("struct S { lazy var c: Int = 1 { get => 2 } }")]
    [InlineData("struct S { static lazy var d: Int = 1 }")]
    [InlineData("lazy var global: Int = 1")]
    public void ALazyPropertyThatCannotBeOneIsReported(string source) =>
        Assert.Contains(ErrorsIn(source), static e => e.Code == ErrorCode.ERR_LazyProperty);

    [Fact]
    public void ALazyPropertyIsStorageOfAnOptionalAndAccessors() {
        const string source = "class Report { var base: Int = 21\n    lazy var total: Int = self.base * 2\n    init() { } }";
        AssertClean(source);
        var compilation = Compile(source);
        var total = Field(compilation, "Report", "total");
        Assert.True(total is { IsLazy: true, IsComputed: true, HasSetter: true });
        Assert.Equal("Int?", total.BackingField!.Type.ToDisplayString());
        Assert.True(compilation.LazyProperties.ContainsKey(total));
    }

    [Theory]
    [InlineData("fn f() -> Int { let d = Doc { n: 1 }\n    return d.words }")]
    [InlineData("extension Doc { fn peek() -> Int { return words } }")]
    public void ALazyPropertyOfAValueIsReadOnlyWhereItCanChange(string use) =>
        Assert.Contains(
            ErrorsIn("struct Doc { var n: Int\n    lazy var words: Int = n * 2 }\n" + use),
            static e => e.Code == ErrorCode.ERR_NotMutable && e.Message.Contains("lazy", StringComparison.Ordinal));

    [Fact]
    public void ALazyPropertyOfAValueIsReadFromAVarOrAMutatingMethod() =>
        AssertClean("""
                    struct Doc {
                        var n: Int
                        lazy var words: Int = n * 2
                        mutating fn peek() -> Int { return words }
                    }
                    fn f() -> Int {
                        var d = Doc { n: 1 }
                        return d.words + d.peek()
                    }
                    """);

    [Fact]
    public void AnObservedPropertyIsStoredAndCanBeAssigned() {
        const string source = """
                              struct S {
                                  var tracked: Int = 0 {
                                      willSet { print("to \(newValue)") }
                                      didSet { print("from \(oldValue)") }
                                  }
                              }
                              fn f() {
                                  var s = S()
                                  s.tracked = 4
                                  s.tracked += 1
                                  let t = S(tracked: 2)
                                  let u = S { tracked: 3 }
                              }
                              """;
        AssertClean(source);
        var compilation = Compile(source);
        var tracked = Field(compilation, "S", "tracked");
        Assert.True(tracked is { HasSetter: true, BackingField.HasInitializer: true });
        Assert.True(compilation.ObservedProperties.ContainsKey(tracked));
    }

    [Fact]
    public void AnObservedPropertyWithoutAValueIsSetByTheInitialiser() =>
        AssertClean("""
                    class C {
                        var count: Int { didSet { if count > 10 { count = 10 } } }
                        init(count: Int) { self.count = count }
                    }
                    struct S { var log: String { willSet(next) { print(next) } } }
                    fn f() -> S { return S(log: "a") }
                    """);

    [Fact]
    public void AnObservedPropertyNamesItsStorageInItsObserversAndInitialisers() {
        const string source = """
                              class C {
                                  var count: Int = 0 { didSet { count = 10 } }
                                  init() { count = 3 }
                                  fn reset() { count = 0 }
                              }
                              """;
        var compilation = Compile(source);
        var model = compilation.GetSemanticModel(compilation.SyntaxTrees[0]);
        var stores = compilation.SyntaxTrees[0].Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Select(a => ((BoundAssignment)model.GetBoundNode(a)!).Target)
            .Cast<BoundFieldAccess>()
            .Select(static t => t.Field.Name)
            .ToList();
        Assert.Equal(["count$field", "count$field", "count"], stores);
    }

    [Theory]
    [InlineData("struct S { let a: Int = 0 { didSet { } } }")]
    [InlineData("struct S { var b: Int = 0 { get => 1\n    didSet { } } }")]
    [InlineData("struct S { lazy var c: Int = 0 { didSet { } } }")]
    [InlineData("struct S { }\nextension S { var d: Int = 0 { didSet { } } }")]
    [InlineData("var global: Int = 0 { didSet { } }")]
    public void AnObservedPropertyThatCannotBeOneIsReported(string source) =>
        Assert.Contains(ErrorsIn(source), static e => e.Code is ErrorCode.ERR_PropertyObservers or ErrorCode.ERR_LazyProperty);
}
