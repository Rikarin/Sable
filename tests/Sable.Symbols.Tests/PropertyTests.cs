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
}
