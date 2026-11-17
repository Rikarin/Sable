namespace Sable.Symbols.Tests;

public sealed class TypeBindingTests {
    /// <summary>Declares a field of the given type and reports how it bound.</summary>
    static string Bind(string typeSyntax, string extraDeclarations = "") {
        var source = $"module M\n{extraDeclarations}\nstruct Holder {{ var value: {typeSyntax} }}";
        var compilation = Compilation.Create(source);

        var holder = compilation.GetTypeByQualifiedName("M.Holder");
        Assert.NotNull(holder);

        var field = holder!.GetMembers("value").OfType<FieldSymbol>().Single();
        return field.Type.ToDisplayString();
    }

    static IReadOnlyList<Diagnostic> ErrorsFor(string typeSyntax, string extra = "") {
        var source = $"module M\n{extra}\nstruct Holder {{ var value: {typeSyntax} }}";
        var compilation = Compilation.Create(source);
        return [..compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error)];
    }

    // ---- Built-ins ---------------------------------------------------------

    [Theory]
    [InlineData("Int", "Int")]
    [InlineData("Bool", "Bool")]
    [InlineData("String", "String")]
    [InlineData("F64", "F64")]
    [InlineData("U8", "U8")]
    [InlineData("Void", "Void")]
    [InlineData("Never", "Never")]
    public void BindsBuiltinTypes(string syntax, string expected) => Assert.Equal(expected, Bind(syntax));

    // ---- Structural forms --------------------------------------------------

    [Theory]
    [InlineData("Int?", "Int?")]
    [InlineData("Int??", "Int??")]
    [InlineData("Int[]", "Int[]")]
    [InlineData("Int[..]", "Int[..]")]
    [InlineData("Int[>..]", "Int[>..]")]
    [InlineData("Int[>..<]", "Int[>..<]")]
    [InlineData("Int[:]", "Int[:]")]
    [InlineData("Int[][]", "Int[][]")]
    [InlineData("Int&", "Int&")]
    [InlineData("Int&<u32>", "Int&<u32>")]
    [InlineData("Int&<u16 in pool>", "Int&<u16 in pool>")]
    [InlineData("Int&?", "Int&?")]
    [InlineData("ref Int", "Int&")]
    [InlineData("(Int, String)", "(Int, String)")]
    [InlineData("(q: Int, r: Int)", "(q: Int, r: Int)")]
    [InlineData("(Int)", "Int")]
    [InlineData("[String: Int]", "Dictionary<String, Int>")]
    [InlineData("(Int) -> Bool", "(Int) -> Bool")]
    [InlineData("async (Int) -> Bool", "async (Int) -> Bool")]
    [InlineData("Int | String", "Int | String")]
    [InlineData("Int[>..]?", "Int[>..]?")]
    public void BindsStructuralTypes(string syntax, string expected) => Assert.Equal(expected, Bind(syntax));

    [Fact]
    public void StructuralUnionsNormalise() {
        // Flattened, de-duplicated and sorted, so the same set is the same type.
        Assert.Equal(Bind("Int | String"), Bind("String | Int"));
        Assert.Equal(Bind("Int | String"), Bind("Int | String | Int"));
        Assert.Equal(Bind("Bool | Int | String"), Bind("(Bool | Int) | String"));
    }

    [Fact]
    public void BindsProtocolForms() {
        Assert.Equal("some P", Bind("some P", "protocol P { }"));
        Assert.Equal("any P", Bind("any P", "protocol P { }"));
        Assert.Equal("P & Q", Bind("P & Q", "protocol P { }\nprotocol Q { }"));
        Assert.Equal("any P & Q", Bind("any P & Q", "protocol P { }\nprotocol Q { }"));
    }

    // ---- Declared types ----------------------------------------------------

    [Fact]
    public void BindsADeclaredType() => Assert.Equal("Point", Bind("Point", "struct Point { }"));

    [Fact]
    public void BindsANestedType() => Assert.Equal("Inner", Bind("Outer.Inner", "struct Outer { struct Inner { } }"));

    [Fact]
    public void BindsGenericInstantiation() => Assert.Equal("Box<Int>", Bind("Box<Int>", "struct Box<T> { }"));

    [Fact]
    public void BindsNestedGenericInstantiation() =>
        Assert.Equal("Box<Box<Int>>", Bind("Box<Box<Int>>", "struct Box<T> { }"));

    [Fact]
    public void ConstructedTypesCompareByArguments() {
        var compilation = Compilation.Create(
            """
            module M
            struct Box<T> { }
            struct Holder {
                var a: Box<Int>
                var b: Box<Int>
                var c: Box<String>
            }
            """
        );

        var holder = compilation.GetTypeByQualifiedName("M.Holder")!;
        TypeSymbol TypeOf(string name) => holder.GetMembers(name).OfType<FieldSymbol>().Single().Type;

        Assert.Equal(TypeOf("a"), TypeOf("b"));
        Assert.NotEqual(TypeOf("a"), TypeOf("c"));
    }

    [Fact]
    public void BindsATypeParameterInScope() {
        var compilation = Compilation.Create(
            """
            module M
            struct Box<T> { var item: T }
            """
        );

        var box = compilation.GetTypeByQualifiedName("M.Box")!;
        var item = box.GetMembers("item").OfType<FieldSymbol>().Single();

        Assert.Equal("T", item.Type.ToDisplayString());
        Assert.IsType<TypeParameterSymbol>(item.Type);
    }

    [Fact]
    public void BindsSelf() {
        var compilation = Compilation.Create("module M\nstruct S { var me: Self }");
        var s = compilation.GetTypeByQualifiedName("M.S")!;
        Assert.Equal("S", s.GetMembers("me").OfType<FieldSymbol>().Single().Type.ToDisplayString());
    }

    [Fact]
    public void BindsAliases() {
        Assert.Equal("Int", Bind("Id", "alias Id = Int"));
        Assert.Equal("Int[>..]", Bind("Buffer", "alias Buffer = Int[>..]"));
        Assert.Equal("(Int) -> Bool", Bind("Pred", "alias Pred = (Int) -> Bool"));
    }

    [Fact]
    public void BindsQualifiedModuleNames() {
        var compilation = Compilation.Create(
            "module Lib.Collections\npublic struct List<T> { }",
            "module App\nstruct S { var xs: Lib.Collections.List<Int> }"
        );

        var errors = compilation.GetDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(static e => e.Message)));

        var s = compilation.GetTypeByQualifiedName("App.S")!;
        Assert.Equal(
            "List<Int>",
            s.GetMembers("xs").OfType<FieldSymbol>().Single().Type.ToDisplayString()
        );
    }

    // ---- Signatures --------------------------------------------------------

    [Fact]
    public void BindsFunctionSignatures() {
        var compilation = Compilation.Create(
            """
            module M
            struct S {
                fn add(_ a: Int, b: Int) -> Int { }
                async fn fetch(url: String) throws(IoError) -> String { }
                fn nothing() { }
            }
            union IoError { NotFound }
            """
        );

        var s = compilation.GetTypeByQualifiedName("M.S")!;

        var add = s.GetMembers("add").OfType<FunctionSymbol>().Single();
        Assert.Equal("Int", add.ReturnType.ToDisplayString());
        Assert.Equal(["Int", "Int"], add.Parameters.Select(static p => p.Type.ToDisplayString()));
        Assert.Equal("b", add.Parameters[1].Name);

        var fetch = s.GetMembers("fetch").OfType<FunctionSymbol>().Single();
        Assert.True(fetch.IsAsync);
        Assert.Equal("IoError", fetch.ThrownType!.ToDisplayString());

        var nothing = s.GetMembers("nothing").OfType<FunctionSymbol>().Single();
        Assert.Equal("Void", nothing.ReturnType.ToDisplayString());
    }

    [Fact]
    public void RecordsParameterModifiersAndLabels() {
        var compilation = Compilation.Create(
            """
            module M
            struct S {
                fn f(from source: Int, to dest: inout Int, _ hidden: Int, rest: Int...) { }
            }
            """
        );

        var f = compilation.GetTypeByQualifiedName("M.S")!
            .GetMembers("f")
            .OfType<FunctionSymbol>()
            .Single();

        Assert.Equal("from", f.Parameters[0].Label);
        Assert.Equal("source", f.Parameters[0].Name);
        Assert.Equal(ParameterModifier.InOut, f.Parameters[1].Modifier);
        Assert.Equal("_", f.Parameters[2].Label);
        Assert.True(f.Parameters[3].IsVariadic);
    }

    [Fact]
    public void BindsBaseTypes() {
        var compilation = Compilation.Create(
            """
            module M
            protocol Drawable { }
            protocol Serializable { }
            class Base { }
            class Shape: Base, Drawable, Serializable { }
            """
        );

        compilation.GetDiagnostics();

        var shape = compilation.GetTypeByQualifiedName("M.Shape")!;
        Assert.Equal(
            ["Base", "Drawable", "Serializable"],
            shape.BaseTypes.Select(static b => b.ToDisplayString())
        );
    }

    [Fact]
    public void BindsTypeParameterConstraints() {
        var compilation = Compilation.Create(
            """
            module M
            protocol Comparable { }
            struct Sorted<T: Comparable> { }
            """
        );

        compilation.GetDiagnostics();

        var sorted = compilation.GetTypeByQualifiedName("M.Sorted")!;
        Assert.Equal(
            "Comparable",
            Assert.Single(sorted.TypeParameters[0].Constraints).ToDisplayString()
        );
    }

    // ---- Errors ------------------------------------------------------------

    [Fact]
    public void AnUnknownTypeIsReported() {
        var error = Assert.Single(ErrorsFor("Nope"));
        Assert.Equal(ErrorCode.ERR_TypeNotFound, error.Code);
    }

    [Fact]
    public void WrongTypeArgumentCountIsReported() {
        Assert.Contains(
            ErrorsFor("Box<Int, Int>", "struct Box<T> { }"),
            static e => e.Code == ErrorCode.ERR_WrongTypeArgumentCount
        );

        Assert.Contains(
            ErrorsFor("Box", "struct Box<T> { }"),
            static e => e.Code == ErrorCode.ERR_MissingTypeArguments
        );

        Assert.Contains(
            ErrorsFor("Int<Bool>"),
            static e => e.Code == ErrorCode.ERR_TypeArgumentsOnNonGeneric
                || e.Code == ErrorCode.ERR_WrongTypeArgumentCount
        );
    }

    [Fact]
    public void AnErrorTypeDoesNotCascade() {
        // One unknown name, one diagnostic -- not one per use.
        var compilation = Compilation.Create(
            """
            module M
            struct S {
                var a: Missing
                var b: Missing
            }
            """
        );

        var errors = compilation.GetDiagnostics()
            .Where(static d => d.Code == ErrorCode.ERR_TypeNotFound)
            .ToList();

        Assert.Equal(2, errors.Count); // one per occurrence, none derived
        Assert.All(errors, static e => Assert.Equal(ErrorCode.ERR_TypeNotFound, e.Code));
    }

    [Fact]
    public void SelfOutsideATypeIsReported() {
        var compilation = Compilation.Create("module M\nfn f(x: Self) { }");
        Assert.Contains(compilation.GetDiagnostics(), static e => e.Code == ErrorCode.ERR_NotAType);
    }

    [Fact]
    public void AmbiguousImportsAreReported() {
        var compilation = Compilation.Create(
            "module A\npublic struct Widget { }",
            "module B\npublic struct Widget { }",
            "module App\nimport A\nimport B\nstruct S { w: Widget }"
        );

        Assert.Contains(compilation.GetDiagnostics(), static e => e.Code == ErrorCode.ERR_AmbiguousName);
    }

    [Fact]
    public void AnInternalTypeIsNotVisibleThroughAnImport() {
        var compilation = Compilation.Create(
            "module Lib\nstruct Hidden { }",
            "module App\nimport Lib\nstruct S { h: Hidden }"
        );

        Assert.Contains(compilation.GetDiagnostics(), static e => e.Code == ErrorCode.ERR_TypeNotFound);
    }
}
