namespace Sable.Symbols.Tests;

/// <summary>
///     A module named in an expression or a type, in full or through the alias
///     an import gave it (spec/SYNTAX.md §1).
/// </summary>
public class ModuleQualificationTests {
    const string Library = """
        module Lib.Shapes
        public fn area(_ side: Int) -> Int { return side * side }
        public struct Square { public let side: Int }
        public struct Box<T> { public let item: T }
        fn hidden() -> Int { return 0 }
        """;

    static IReadOnlyList<Diagnostic> Errors(string app) =>
        [..Compilation.Create(Library, app).GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error)];

    [Fact]
    public void AnAliasReachesAFunction() =>
        Assert.Empty(Errors("module App\nimport Lib.Shapes as S\nfn f() -> Int { return S.area(3) }"));

    [Fact]
    public void AFullModuleNameReachesAFunctionWithoutAnImport() =>
        Assert.Empty(Errors("module App\nfn f() -> Int { return Lib.Shapes.area(3) }"));

    [Fact]
    public void AnAliasNamesATypeInAnExpressionAndInATypesPlace() =>
        Assert.Empty(Errors("module App\nimport Lib.Shapes as S\nfn f() -> Int { let s: S.Square = S.Square { side: 2 }\nreturn s.side }"));

    [Fact]
    public void AGenericTypeIsReachedWithItsArguments() =>
        Assert.Empty(Errors("module App\nimport Lib.Shapes as S\nfn f() -> Int { let b: S.Box<Int> = Lib.Shapes.Box<Int> { item: 2 }\nreturn b.item }"));

    const string Overloads = """
        module Lib.Calls
        public fn call<A: Equatable>(_ f: @escaping (A) -> Void) -> Int { return 1 }
        public fn call<A: Equatable, B: Equatable>(_ f: @escaping (A, B) throws -> Void) -> Int { return 2 }
        fn call() -> Int { return 3 }
        """;

    static IReadOnlyList<Diagnostic> OverloadErrors(string app) =>
        [..Compilation.Create(Overloads, app).GetAllDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error)];

    /// <summary>A name imported by itself is every overload of it that can be seen.</summary>
    [Fact]
    public void AnImportedNameIsEveryOverload() {
        const string uses = "fn one(_ a: Int) {}\nfn two(_ a: Int, by b: String) {}\nfn f() -> Int { return call(one) + call(two) }";
        Assert.Empty(OverloadErrors($"module App\nimport Lib.Calls.{{ call }}\n{uses}"));
        Assert.Empty(OverloadErrors($"module App\nimport Lib.Calls.{{ call as ring }}\n{uses.Replace("call(", "ring(", StringComparison.Ordinal)}"));
        Assert.Single(OverloadErrors("module App\nimport Lib.Calls.{ call }\nfn f() -> Int { return call() }"));
    }

    /// <summary>A function passed by its name is for the overload that takes as many arguments as it has.</summary>
    [Fact]
    public void AFunctionsNameChoosesAnOverloadByItsArguments() {
        var errors = OverloadErrors("module App\nimport Lib.Calls\nfn three(_ a: Int, _ b: Int, _ c: Int) {}\nfn f() -> Int { return call(three) }");
        Assert.Contains("takes 3 arguments", Assert.Single(errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALocalHidesAModuleOfTheSameName() =>
        Assert.Empty(Errors("module App\nimport Lib.Shapes as S\nstruct T { let area: Int }\nfn f(_ S: T) -> Int { return S.area }"));

    [Fact]
    public void AModuleIsNotAValue() {
        var errors = Errors("module App\nimport Lib.Shapes as S\nfn f() { let s = S }");
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NotAValue && e.Message.Contains("'Lib.Shapes'", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingMemberIsReportedAgainstTheModule() {
        var errors = Errors("module App\nimport Lib.Shapes as S\nfn f() -> Int { return S.volume(3) }");
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NameNotFound && e.Message.Contains("'Lib.Shapes' has no member named 'volume'", StringComparison.Ordinal));
    }

    [Fact]
    public void NamingTheModuleDoesNotGetPastWhatItKeepsToItself() {
        var errors = Errors("module App\nfn f() -> Int { return Lib.Shapes.hidden() }");
        Assert.Contains(errors, static e => e.Code == ErrorCode.ERR_NotAccessible);
    }
}
