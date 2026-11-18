namespace Sable.Syntax.Tests;

public sealed class DeclarationParserTests {
    static CompilationUnitSyntax Parse(string source) {
        var tree = SyntaxTree.Parse(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(source, tree.ToFullString());
        return Assert.IsType<CompilationUnitSyntax>(tree.Root);
    }

    static T ParseMember<T>(string source) where T : SyntaxNode =>
        Assert.IsType<T>(Assert.Single(Parse(source).Members));

    // ---- Module and imports -----------------------------------------------

    [Fact]
    public void ParsesModuleAndImports() {
        var unit = Parse(
            """
            module App.Rendering

            import Sable.Collections
            import Sable.Math as M
            import Sable.IO.{ File, Directory }
            import Sable.Text.{ * }
            public import Sable.Math.Vectors
            """
        );

        Assert.NotNull(unit.Module);
        Assert.Equal(5, unit.Imports.Count);
        Assert.Equal("M", unit.Imports[1].Alias.Text);
        Assert.Equal(2, unit.Imports[2].Items!.Items.Count);
        Assert.Equal("*", unit.Imports[3].Items!.Items[0].NameToken.Text);
        Assert.False(unit.Imports[4].PublicKeyword.IsNone);
    }

    [Fact]
    public void AFileNeedsNoModuleDeclaration() {
        var unit = Parse("fn main() { }");
        Assert.Null(unit.Module);
        Assert.Single(unit.Members);
    }

    // ---- Functions ---------------------------------------------------------

    [Fact]
    public void ParsesAFullFunctionSignature() {
        var fn = ParseMember<FunctionDeclarationSyntax>(
            "public static fn add(_ a: Int, b: Int = 0) throws(MathError) -> Int => a + b"
        );

        Assert.Equal(2, fn.Modifiers.Count);
        Assert.Equal("add", fn.Identifier.Text);
        Assert.Equal(2, fn.Parameters.Parameters.Count);
        Assert.NotNull(fn.ThrowsSpec);
        Assert.NotNull(fn.ReturnClause);
        Assert.NotNull(fn.ExpressionBody);
        Assert.Null(fn.Body);
    }

    [Fact]
    public void ParsesParameterLabelsAndModifiers() {
        var fn = ParseMember<FunctionDeclarationSyntax>(
            "fn relocate(from source: Point, to dest: inout Point, values: Int...) { }"
        );

        var parameters = fn.Parameters.Parameters;
        Assert.Equal("from", parameters[0].Label.Text);
        Assert.Equal("source", parameters[0].Identifier.Text);
        Assert.Equal("inout", parameters[1].TypeModifierToken.Text);
        Assert.False(parameters[2].EllipsisToken.IsNone);
    }

    // Before the parameter or on its type (#125); on a type, the parenthesis
    // after an attribute is the function type's, not the attribute's.
    [Theory]
    [InlineData("fn keep(_ f: @escaping () -> Int) -> () -> Int { }")]
    [InlineData("fn keep(@escaping _ f: () -> Int) -> () -> Int { }")]
    public void ParsesEscapingOnAParameterOrItsType(string source) {
        var tree = SyntaxTree.Parse(new SourceText(source, "test.sb"));
        Assert.Empty(tree.Diagnostics);
        var parameter = Assert.Single(Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(((CompilationUnitSyntax)tree.Root).Members)).Parameters.Parameters);
        var attributes = parameter.Type is AttributedTypeSyntax attributed ? attributed.Attributes : parameter.Attributes;
        Assert.Equal("escaping", Assert.Single(attributes).Name.ToString().Trim());
        Assert.IsType<FunctionTypeSyntax>(parameter.Type is AttributedTypeSyntax { Type: var inner } ? inner : parameter.Type);
        Assert.Equal(source, tree.Root.ToFullString());
    }

    [Fact]
    public void ParsesAKeywordParameterLabel() {
        var fn = ParseMember<FunctionDeclarationSyntax>("fn first(where p: (Element) -> Bool) -> Element? { }");
        Assert.Equal("where", fn.Parameters.Parameters[0].Label.Text);
    }

    [Fact]
    public void ParsesUnderscoreLabelSuppression() {
        var fn = ParseMember<FunctionDeclarationSyntax>("fn insert(_ item: T, at index: Int) { }");
        Assert.Equal("_", fn.Parameters.Parameters[0].Label.Text);
    }

    [Fact]
    public void ParsesGenericsAndWhereClause() {
        var fn = ParseMember<FunctionDeclarationSyntax>(
            """
            fn zip<A, B, S1, S2>(_ a: S1, _ b: S2) -> some Sequence<(A, B)>
                where S1: Sequence<A>,
                      S2: Sequence<B>,
                      A: Sendable { }
            """
        );

        Assert.Equal(4, fn.TypeParameters!.Parameters.Count);
        Assert.Equal(3, fn.WhereClause!.Constraints.Count);
    }

    [Fact]
    public void ParsesSameTypeConstraint() {
        var fn = ParseMember<FunctionDeclarationSyntax>("fn f<S, T>(_ s: S) where S.Element == T { }");
        Assert.Equal("==", fn.WhereClause!.Constraints[0].OperatorToken.Text);
    }

    [Fact]
    public void ParsesAsyncAndRethrows() {
        var fn = ParseMember<FunctionDeclarationSyntax>(
            "public async fn fetch(_ url: Url) throws(HttpError) -> Response { }"
        );
        Assert.Equal(2, fn.Modifiers.Count);
        Assert.NotNull(fn.ThrowsSpec);

        var rethrowing = ParseMember<FunctionDeclarationSyntax>(
            "fn withLock<T>(_ l: Lock, _ body: () throws -> T) rethrows -> T { }"
        );
        Assert.Equal("rethrows", rethrowing.ThrowsSpec!.ThrowsKeyword.Text);
    }

    [Fact]
    public void ParsesExplicitProtocolImplementation() {
        var unit = Parse("class Shape { fn Drawable.render() -> Image { } }");
        var declaration = Assert.IsType<ClassDeclarationSyntax>(unit.Members[0]);
        var fn = Assert.IsType<FunctionDeclarationSyntax>(declaration.Members[0]);
        Assert.NotNull(fn.ExplicitProtocol);
        Assert.Equal("render", fn.Identifier.Text);
    }

    [Fact]
    public void ParsesANestedFunction() {
        var fn = ParseMember<FunctionDeclarationSyntax>(
            """
            fn outer() {
                fn helper(x: Int) -> Int => x + 1
                let f = (x: Int) => x + offset
            }
            """
        );

        Assert.Equal(SyntaxKind.DeclarationStatement, fn.Body!.Statements[0].Kind);
    }

    // ---- Types -------------------------------------------------------------

    [Fact]
    public void ParsesStructWithTerseFields() {
        var declaration = ParseMember<StructDeclarationSyntax>("struct Vec3 { x: F32, y: F32, z: F32 }");
        Assert.Equal(3, declaration.Members.Count);

        var first = Assert.IsType<FieldDeclarationSyntax>(declaration.Members[0]);
        Assert.True(first.LetOrVarKeyword.IsNone);
        Assert.Equal(",", first.SeparatorToken.Text);
    }

    [Fact]
    public void ParsesClassWithBasesAndMembers() {
        var declaration = ParseMember<ClassDeclarationSyntax>(
            """
            public open class Document : Base, Serializable, Equatable {
                public var title: String
                private var revisions: Revision[>..] = []
                public weak var owner: Workspace?
                public init(title: String) { self.title = title }
                deinit { store.unregister(self) }
                public open fn render() -> String { return title }
            }
            """
        );

        Assert.Equal(3, declaration.BaseTypes!.Types.Count);
        Assert.Equal(6, declaration.Members.Count);
        Assert.Equal(SyntaxKind.InitDeclaration, declaration.Members[3].Kind);
        Assert.Equal(SyntaxKind.DeinitDeclaration, declaration.Members[4].Kind);
    }

    [Fact]
    public void ParsesProtocolWithAssociatedTypes() {
        var declaration = ParseMember<ProtocolDeclarationSyntax>(
            """
            public protocol Collection {
                associatedtype Element
                associatedtype Index: Comparable = Int

                var count: Int { get }
                subscript(i: Index) -> Element { get }
                fn makeIterator() -> some Iterator<Element>
                var isEmpty: Bool { get => count == 0 }
            }
            """
        );

        Assert.Equal(6, declaration.Members.Count);
        var indexType = Assert.IsType<AssociatedTypeDeclarationSyntax>(declaration.Members[1]);
        Assert.NotNull(indexType.BaseTypes);
        Assert.NotNull(indexType.DefaultType);
    }

    [Fact]
    public void ParsesProtocolWithSelfConstraint() {
        var declaration = ParseMember<ProtocolDeclarationSyntax>(
            "protocol Drawable where Self: Sized { fn draw(into ctx: inout Context) }"
        );
        Assert.NotNull(declaration.WhereClause);
    }

    [Fact]
    public void ParsesUnionWithPayloads() {
        var declaration = ParseMember<UnionDeclarationSyntax>(
            """
            public union Shape {
                Circle(radius: F64)
                Rect(width: F64, height: F64)
                Empty
            }
            """
        );

        Assert.Equal(3, declaration.Members.Count);
        var circle = Assert.IsType<UnionCaseDeclarationSyntax>(declaration.Members[0]);
        Assert.Equal("radius", circle.Payload!.Fields[0].NameToken.Text);
        Assert.Null(Assert.IsType<UnionCaseDeclarationSyntax>(declaration.Members[2]).Payload);
    }

    [Fact]
    public void ParsesUnionOnOneLineWithCommas() {
        var declaration = ParseMember<UnionDeclarationSyntax>(
            "union IoError { NotFound(path: String), Denied, Interrupted }"
        );
        Assert.Equal(3, declaration.Members.Count);
    }

    [Fact]
    public void ParsesVariantSizedUnion() {
        var declaration = ParseMember<UnionDeclarationSyntax>(
            "public union Node.. { Leaf(value: I32) Branch(left: Node&<u32>, right: Node&<u32>) }"
        );
        Assert.False(declaration.VariableSizeToken.IsNone);
        Assert.Equal(2, declaration.Members.Count);
    }

    [Fact]
    public void ParsesGenericUnionWithMethods() {
        var declaration = ParseMember<UnionDeclarationSyntax>(
            """
            public union Outcome<T, E> {
                Ok(T)
                Err(E)

                fn map<U>(_ f: (T) -> U) -> Outcome<U, E> {
                    return match self {
                        case .Ok(let v) => .Ok(f(v))
                        case .Err(let e) => .Err(e)
                    }
                }
            }
            """
        );

        Assert.Equal(2, declaration.TypeParameters!.Parameters.Count);
        Assert.Equal(3, declaration.Members.Count);
        Assert.Equal(SyntaxKind.FunctionDeclaration, declaration.Members[2].Kind);
    }

    [Fact]
    public void ParsesEnums() {
        var inline = ParseMember<EnumDeclarationSyntax>("public enum Direction { North, South, East, West }");
        Assert.Equal(4, inline.Members.Count);

        var valued = ParseMember<EnumDeclarationSyntax>(
            """
            @flags
            public enum Permissions : U32 {
                None    = 0
                Read    = 1
                All     = Read | Write
            }
            """
        );
        Assert.Single(valued.Attributes);
        Assert.NotNull(valued.BaseTypes);
        Assert.Equal(3, valued.Members.Count);
        Assert.NotNull(Assert.IsType<EnumCaseDeclarationSyntax>(valued.Members[2]).Value);
    }

    [Fact]
    public void ParsesExtensions() {
        Assert.NotNull(
            ParseMember<ExtensionDeclarationSyntax>(
                    "extension Vec3 : Serializable { fn encode(to e: inout Encoder) { } }"
                )
                .BaseTypes
        );

        Assert.NotNull(
            ParseMember<ExtensionDeclarationSyntax>(
                    "extension Array<T> where T: Comparable { fn sorted() -> Self { } }"
                )
                .WhereClause
        );
    }

    [Theory]
    [InlineData("extension<Element> Element[>..] { }", typeof(GrowArrayTypeSyntax))]
    [InlineData("extension<Element> Element[>..<] { }", typeof(GrowArrayTypeSyntax))]
    [InlineData("extension<Element> Element[:] { }", typeof(SliceTypeSyntax))]
    [InlineData("extension<Element> Element[] { }", typeof(FixedSizeArrayTypeSyntax))]
    [InlineData("extension<Element> Element[..] { }", typeof(InlineArrayTypeSyntax))]
    [InlineData("extension<K, V> (K, V)[>..] { }", typeof(GrowArrayTypeSyntax))]
    public void ParsesArrayExtensions(string source, Type arrayType) {
        var extension = ParseMember<ExtensionDeclarationSyntax>(source);
        Assert.NotNull(extension.TypeParameters);
        Assert.IsType(arrayType, extension.Type);
        Assert.Null(extension.WhereClause);
    }

    [Fact]
    public void ParsesAConstrainedArrayExtension() {
        var inline = ParseMember<ExtensionDeclarationSyntax>(
            "extension<Element: Equatable> Element[:] { fn contains(element: Element) -> Bool { return false } }"
        );
        var parameter = Assert.Single(inline.TypeParameters!.Parameters);
        Assert.Equal("Element", parameter.Identifier.Text);
        Assert.NotNull(parameter.Constraint);
        Assert.Single(inline.Members);

        var where = ParseMember<ExtensionDeclarationSyntax>("extension<T> T[>..] where T: Hashable { }");
        Assert.NotNull(where.WhereClause);
        Assert.IsType<GrowArrayTypeSyntax>(where.Type);
    }

    [Fact]
    public void AnExtensionWithoutParametersHasNone() {
        Assert.Null(ParseMember<ExtensionDeclarationSyntax>("extension U8[:] { }").TypeParameters);
        Assert.Null(ParseMember<ExtensionDeclarationSyntax>("extension Point { }").TypeParameters);
    }

    [Fact]
    public void AnUnclosedParameterListIsReportedNotLost() {
        var tree = SyntaxTree.Parse("module M\nextension<Element Element[>..] { }\n");
        Assert.NotEmpty(tree.Diagnostics);
        Assert.Equal("module M\nextension<Element Element[>..] { }\n", tree.Root.ToFullString());
    }

    [Fact]
    public void ParsesActor() {
        var declaration = ParseMember<ActorDeclarationSyntax>(
            """
            public actor Cache<Key: Hashable & Sendable, Value: Sendable> {
                private var entries: [Key: Value] = [:]
                public fn get(_ key: Key) -> Value? { return entries[key] }
                nonisolated let id: Uuid
            }
            """
        );

        Assert.Equal(2, declaration.TypeParameters!.Parameters.Count);
        Assert.Equal(3, declaration.Members.Count);
    }

    // ---- Members -----------------------------------------------------------

    [Fact]
    public void ParsesAccessors() {
        var declaration = ParseMember<ClassDeclarationSyntax>(
            """
            class Temperature {
                var fahrenheit: F64 {
                    get => celsius * 9/5 + 32
                    set { celsius = (newValue - 32) * 5/9 }
                }
                private(set) var updates: Int = 0
                var tracked: Int = 0 {
                    willSet { log(newValue) }
                    didSet  { log(oldValue) }
                }
                lazy var expensive: Report = computeReport()
            }
            """
        );

        var fahrenheit = Assert.IsType<FieldDeclarationSyntax>(declaration.Members[0]);
        Assert.Equal(2, fahrenheit.Accessors!.Accessors.Count);

        var updates = Assert.IsType<FieldDeclarationSyntax>(declaration.Members[1]);
        Assert.Equal(4, updates.Modifiers.Count); // private ( set )

        var tracked = Assert.IsType<FieldDeclarationSyntax>(declaration.Members[2]);
        Assert.NotNull(tracked.Declarator.Initializer);
        Assert.Equal(2, tracked.Accessors!.Accessors.Count);
    }

    [Fact]
    public void ParsesAPropertyWrittenWithAnArrowAsItsGetter() {
        const string source = "struct R { var w: F64, var h: F64\n    var area: F64 => w * h\n    static var one: Int => 1 }";
        var declaration = ParseMember<StructDeclarationSyntax>(source);
        var area = Assert.IsType<FieldDeclarationSyntax>(declaration.Members[2]);
        var getter = Assert.Single(area.Accessors!.Accessors);
        Assert.Equal(SyntaxKind.GetKeyword, getter.Keyword.Kind);
        Assert.True(getter.Keyword.IsMissing);
        Assert.True(area.Accessors.OpenBraceToken.IsMissing);
        Assert.Equal("w * h", getter.ExpressionBody!.ToString().Trim());
        Assert.Equal(source, declaration.ToFullString());
        Assert.IsType<FieldDeclarationSyntax>(declaration.Members[3]);
    }

    [Fact]
    public void TypealiasIsReadAsTheAliasItMeantAndSaysSo() {
        const string source = "module M\nstruct S {\n    typealias Element = Int\n}\n";
        var tree = SyntaxTree.Parse(source);
        var error = Assert.Single(tree.Diagnostics);
        Assert.Equal(ErrorCode.ERR_TypealiasIsAlias, error.Code);
        var type = Assert.IsType<StructDeclarationSyntax>(Assert.Single(((CompilationUnitSyntax)tree.Root).Members));
        var alias = Assert.IsType<AliasDeclarationSyntax>(Assert.Single(type.Members));
        Assert.Equal("Element", alias.Identifier.Text);
        Assert.Equal(source, tree.ToFullString());
    }

    [Fact]
    public void ParsesSetterWithNamedParameter() {
        var declaration = ParseMember<ClassDeclarationSyntax>("class C { var x: Int { get => y  set(v) { y = v } } }");
        var field = Assert.IsType<FieldDeclarationSyntax>(declaration.Members[0]);
        Assert.Equal("v", field.Accessors!.Accessors[1].ParameterName.Text);
    }

    [Fact]
    public void ParsesSubscripts() {
        var declaration = ParseMember<StructDeclarationSyntax>(
            """
            struct Grid<T> {
                subscript(x: Int, y: Int) -> T {
                    get => storage[y * width + x]
                    set { storage[y * width + x] = newValue }
                }
                subscript(row r: Int) -> T[:] { get => storage[r..<width] }
            }
            """
        );

        Assert.Equal(2, declaration.Members.Count);
        var first = Assert.IsType<SubscriptDeclarationSyntax>(declaration.Members[0]);
        Assert.Equal(2, first.Parameters.Parameters.Count);
    }

    [Fact]
    public void ParsesOperators() {
        var declaration = ParseMember<StructDeclarationSyntax>(
            """
            struct Money {
                operator + (a: Money, b: Money) -> Money => a
                operator == (a: Money, b: Money) -> Bool => true
                operator [] (m: Money, k: String) -> F64 => 0
                prefix operator - (m: Money) -> Money => m
            }
            """
        );

        Assert.Equal(4, declaration.Members.Count);
        var index = Assert.IsType<OperatorDeclarationSyntax>(declaration.Members[2]);
        Assert.Equal("[", index.OperatorToken.Text);
        Assert.Equal("]", index.SecondOperatorToken.Text);

        var negate = Assert.IsType<OperatorDeclarationSyntax>(declaration.Members[3]);
        Assert.Equal("prefix", negate.FixityToken.Text);
    }

    [Fact]
    public void ParsesAliasesAndConstants() {
        Assert.NotNull(
            ParseMember<AliasDeclarationSyntax>("alias Handler = async (Request) throws(HttpError) -> Response")
        );

        var generic = ParseMember<AliasDeclarationSyntax>("public alias Result<T> = Outcome<T, AppError>");
        Assert.Single(generic.TypeParameters!.Parameters);

        var constant = ParseMember<ConstDeclarationSyntax>("const Pi: F64 = 3.14159265358979");
        Assert.NotNull(constant.TypeAnnotation);

        var constFn = ParseMember<FunctionDeclarationSyntax>(
            "const fn factorial(_ n: Int) -> Int => n <= 1 ? 1 : n * factorial(n - 1)"
        );
        Assert.Equal("const", constFn.Modifiers[0].Text);
    }

    [Fact]
    public void ParsesAttributes() {
        var declaration = ParseMember<StructDeclarationSyntax>(
            """
            @inline_layout
            @derive(Equatable, Hashable)
            @align(16)
            public struct OrderLine {
                public var sku: U8[..16]
            }
            """
        );

        Assert.Equal(3, declaration.Attributes.Count);
        Assert.Equal(2, declaration.Attributes[1].ArgumentList!.Arguments.Count);
        Assert.Null(declaration.Attributes[0].ArgumentList);
    }

    [Fact]
    public void ParsesGenericVariance() {
        var declaration = ParseMember<ProtocolDeclarationSyntax>("protocol Producer<out T> { fn next() -> T? }");
        Assert.Equal("out", declaration.TypeParameters!.Parameters[0].VarianceToken.Text);
    }

    [Fact]
    public void ParsesTryCatchExpressionForm() {
        var fn = ParseMember<FunctionDeclarationSyntax>("""fn f() { let text = try read("c.txt") catch { "" } }""");

        var declaration = Assert.IsType<LocalDeclarationStatementSyntax>(fn.Body!.Statements[0]);
        var tryExpression = Assert.IsType<TryExpressionSyntax>(declaration.Declarator.Initializer!.Value);
        Assert.NotNull(tryExpression.CatchBody);
    }

    [Fact]
    public void ParsesAsyncLet() {
        var fn = ParseMember<FunctionDeclarationSyntax>(
            "async fn main() { async let a = fetch(1)\n async let b = fetch(2) }"
        );

        var first = Assert.IsType<LocalDeclarationStatementSyntax>(fn.Body!.Statements[0]);
        Assert.False(first.AsyncKeyword.IsNone);
    }

    // ---- Recovery ----------------------------------------------------------

    [Theory]
    [InlineData("fn")]
    [InlineData("fn f")]
    [InlineData("fn f(")]
    [InlineData("fn f()")]
    [InlineData("fn f() ->")]
    [InlineData("struct")]
    [InlineData("struct S")]
    [InlineData("struct S {")]
    [InlineData("class C : {")]
    [InlineData("union U { Case(")]
    [InlineData("enum E { A = }")]
    [InlineData("protocol P { associatedtype }")]
    [InlineData("extension")]
    [InlineData("import")]
    [InlineData("module")]
    [InlineData("@")]
    [InlineData("@attr")]
    [InlineData("public")]
    [InlineData("public public public")]
    [InlineData("alias = Int")]
    [InlineData("var x: Int { get")]
    [InlineData("operator")]
    [InlineData("subscript")]
    [InlineData("}}}")]
    [InlineData("let x = 1")]
    public void MalformedFilesStillRoundTrip(string source) {
        var tree = SyntaxTree.Parse(source);
        Assert.True(
            source == tree.ToFullString(),
            $"round-trip failed\n  source: {source}\n  tree:   {tree.ToFullString()}"
        );
    }

    [Fact]
    public void AttributesWithNoDeclarationBecomeAnIncompleteDeclaration() {
        var tree = SyntaxTree.Parse("@reflectable @main");
        var unit = Assert.IsType<CompilationUnitSyntax>(tree.Root);
        Assert.Equal(SyntaxKind.IncompleteDeclaration, unit.Members[0].Kind);
        Assert.Equal("@reflectable @main", tree.ToFullString());
    }
}
