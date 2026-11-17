using Sable.Symbols;

namespace Sable.Symbols.Tests;

public sealed class DeclarationTests
{
    private static Compilation Compile(params string[] sources) => Compilation.Create(sources);

    private static IReadOnlyList<Diagnostic> Errors(Compilation compilation) =>
        [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];

    private static void AssertNoErrors(Compilation compilation)
    {
        var errors = Errors(compilation);
        Assert.True(errors.Count == 0,
            string.Join("\n", errors.Select(e => $"{e.Id}: {e.Message} {e.Span}")));
    }

    // ---- Modules -----------------------------------------------------------

    [Fact]
    public void DeclaresAModule()
    {
        var compilation = Compile("module App.Core\nstruct Point { x: Int, y: Int }");
        AssertNoErrors(compilation);

        var module = compilation.GetModule("App.Core");
        Assert.NotNull(module);
        Assert.Equal("Point", Assert.Single(module!.Members).Name);
    }

    [Fact]
    public void TwoFilesShareOneModule()
    {
        var compilation = Compile(
            "module App\nstruct A { }",
            "module App\nstruct B { }");

        AssertNoErrors(compilation);
        var module = compilation.GetModule("App");
        Assert.Equal(2, module!.Members.Count);
    }

    [Fact]
    public void AFileWithoutAModuleStillDeclaresItsTypes()
    {
        // spec/SYNTAX.md §1 wants a module declaration, and says so, but the
        // file is still bound: a missing one must not take the rest with it.
        var compilation = Compile("struct A { }");

        Assert.Contains(compilation.GetDiagnostics(),
            d => d.Code == ErrorCode.ERR_NoModuleDeclaration);
        Assert.NotNull(compilation.GetModule(""));
        Assert.NotNull(compilation.GetTypeByQualifiedName("A"));
    }

    // ---- Types -------------------------------------------------------------

    [Fact]
    public void DeclaresEveryTypeKind()
    {
        var compilation = Compile("""
            module M
            struct S { }
            class C { }
            actor A { }
            protocol P { }
            union U { Case }
            enum E { One }
            """);

        AssertNoErrors(compilation);

        Assert.Equal(TypeKind.Struct, compilation.GetTypeByQualifiedName("M.S")!.TypeKind);
        Assert.Equal(TypeKind.Class, compilation.GetTypeByQualifiedName("M.C")!.TypeKind);
        Assert.Equal(TypeKind.Actor, compilation.GetTypeByQualifiedName("M.A")!.TypeKind);
        Assert.Equal(TypeKind.Protocol, compilation.GetTypeByQualifiedName("M.P")!.TypeKind);
        Assert.Equal(TypeKind.Union, compilation.GetTypeByQualifiedName("M.U")!.TypeKind);
        Assert.Equal(TypeKind.Enum, compilation.GetTypeByQualifiedName("M.E")!.TypeKind);
    }

    [Fact]
    public void DeclaresNestedTypes()
    {
        var compilation = Compile("module M\nstruct Outer { struct Inner { } }");
        AssertNoErrors(compilation);

        var inner = compilation.GetTypeByQualifiedName("M.Outer.Inner");
        Assert.NotNull(inner);
        Assert.Equal("M.Outer.Inner", inner!.QualifiedName);
    }

    [Fact]
    public void RecordsTypeParametersAndVariance()
    {
        var compilation = Compile("module M\nprotocol Producer<out T> { }\nstruct Pair<A, B> { }");
        AssertNoErrors(compilation);

        var producer = compilation.GetTypeByQualifiedName("M.Producer")!;
        Assert.Equal(VarianceKind.Out, Assert.Single(producer.TypeParameters).Variance);

        Assert.Equal(2, compilation.GetTypeByQualifiedName("M.Pair")!.TypeParameters.Length);
    }

    [Fact]
    public void RecordsVariableSizedUnions()
    {
        var compilation = Compile("module M\nunion Node.. { Leaf }");
        AssertNoErrors(compilation);
        Assert.True(compilation.GetTypeByQualifiedName("M.Node")!.IsVariableSize);
    }

    [Fact]
    public void RecordsAccessibility()
    {
        var compilation = Compile("""
            module M
            public struct Open { }
            private struct Hidden { }
            struct Default { }
            """);

        AssertNoErrors(compilation);
        Assert.Equal(Accessibility.Public,
            compilation.GetTypeByQualifiedName("M.Open")!.DeclaredAccessibility);
        Assert.Equal(Accessibility.Private,
            compilation.GetTypeByQualifiedName("M.Hidden")!.DeclaredAccessibility);
        Assert.Equal(Accessibility.Internal,
            compilation.GetTypeByQualifiedName("M.Default")!.DeclaredAccessibility);
    }

    // ---- Members -----------------------------------------------------------

    [Fact]
    public void DeclaresMembers()
    {
        var compilation = Compile("""
            module M
            struct S {
                public var a: Int
                let b: String
                c: F64
                public fn f(_ x: Int) -> Bool { }
                init(a: Int) { }
                deinit { }
            }
            """);

        AssertNoErrors(compilation);

        var type = compilation.GetTypeByQualifiedName("M.S")!;
        Assert.Equal(3, type.Members.OfType<FieldSymbol>().Count());
        Assert.Equal(3, type.Members.OfType<FunctionSymbol>().Count());

        var a = type.GetMembers("a").OfType<FieldSymbol>().Single();
        Assert.True(a.IsMutable);
        Assert.Equal(Accessibility.Public, a.DeclaredAccessibility);

        var c = type.GetMembers("c").OfType<FieldSymbol>().Single();
        Assert.False(c.IsMutable);
    }

    [Fact]
    public void DeclaresUnionCasesWithPayloads()
    {
        var compilation = Compile("""
            module M
            union Shape {
                Circle(radius: F64)
                Rect(width: F64, height: F64)
                Empty
            }
            """);

        AssertNoErrors(compilation);

        var shape = compilation.GetTypeByQualifiedName("M.Shape")!;
        var cases = shape.Members.OfType<UnionCaseSymbol>().ToList();
        Assert.Equal(3, cases.Count);

        Assert.Equal("radius", cases[0].Payload[0].Name);
        Assert.Equal(2, cases[1].Payload.Length);
        Assert.False(cases[2].HasPayload);
    }

    [Fact]
    public void DeclaresEnumCases()
    {
        var compilation = Compile("module M\nenum E : U8 { A = 1, B, C }");
        AssertNoErrors(compilation);

        var cases = compilation.GetTypeByQualifiedName("M.E")!
            .Members.OfType<EnumCaseSymbol>().ToList();

        Assert.Equal(3, cases.Count);
        Assert.True(cases[0].HasExplicitValue);
        Assert.False(cases[1].HasExplicitValue);
    }

    [Fact]
    public void FunctionsMayBeOverloaded()
    {
        var compilation = Compile("""
            module M
            struct S {
                fn f(_ a: Int) { }
                fn f(_ a: String) { }
            }
            """);

        AssertNoErrors(compilation);
        Assert.Equal(2, compilation.GetTypeByQualifiedName("M.S")!.GetMembers("f").Count());
    }

    [Fact]
    public void RecordsFunctionModifiers()
    {
        var compilation = Compile("""
            module M
            struct S {
                public static fn a() { }
                mutating fn b() { }
                async fn c() { }
            }
            """);

        AssertNoErrors(compilation);
        var type = compilation.GetTypeByQualifiedName("M.S")!;

        Assert.True(type.GetMembers("a").OfType<FunctionSymbol>().Single().IsStatic);
        Assert.True(type.GetMembers("b").OfType<FunctionSymbol>().Single().IsMutating);
        Assert.True(type.GetMembers("c").OfType<FunctionSymbol>().Single().IsAsync);
    }

    // ---- Duplicates --------------------------------------------------------

    [Fact]
    public void DuplicateTypeIsReported()
    {
        var compilation = Compile("module M\nstruct A { }\nstruct A { }");
        var error = Assert.Single(Errors(compilation));
        Assert.Equal(ErrorCode.ERR_DuplicateDeclaration, error.Code);
    }

    [Fact]
    public void DuplicateMemberIsReported()
    {
        var compilation = Compile("module M\nstruct S { var a: Int\n var a: Int }");
        Assert.Contains(Errors(compilation), e => e.Code == ErrorCode.ERR_DuplicateDeclaration);
    }

    [Fact]
    public void DuplicateAcrossFilesInOneModuleIsReported()
    {
        var compilation = Compile("module M\nstruct A { }", "module M\nstruct A { }");
        Assert.Contains(Errors(compilation), e => e.Code == ErrorCode.ERR_DuplicateDeclaration);
    }

    [Fact]
    public void TheSameNameInTwoModulesIsFine()
    {
        AssertNoErrors(Compile("module A\nstruct S { }", "module B\nstruct S { }"));
    }

    // ---- Imports -----------------------------------------------------------

    [Fact]
    public void ResolvesImports()
    {
        var compilation = Compile(
            "module Lib\npublic struct Widget { }",
            "module App\nimport Lib\nstruct Screen { w: Widget }");

        AssertNoErrors(compilation);
    }

    [Fact]
    public void AnUnknownImportIsReported()
    {
        var compilation = Compile("module App\nimport Missing\nstruct S { }");
        Assert.Contains(Errors(compilation), e => e.Code == ErrorCode.ERR_ModuleNotFound);
    }

    [Fact]
    public void SelectiveImportBringsInOnlyWhatIsNamed()
    {
        var compilation = Compile(
            "module Lib\npublic struct A { }\npublic struct B { }",
            "module App\nimport Lib.{ A }\nstruct S { a: A }");

        AssertNoErrors(compilation);

        var hidden = Compile(
            "module Lib\npublic struct A { }\npublic struct B { }",
            "module App\nimport Lib.{ A }\nstruct S { b: B }");

        Assert.Contains(Errors(hidden), e => e.Code == ErrorCode.ERR_TypeNotFound);
    }

    [Fact]
    public void ImportingAnUnknownMemberIsReported()
    {
        var compilation = Compile(
            "module Lib\npublic struct A { }",
            "module App\nimport Lib.{ Nope }");

        Assert.Contains(Errors(compilation), e => e.Code == ErrorCode.ERR_NameNotFound);
    }
}
