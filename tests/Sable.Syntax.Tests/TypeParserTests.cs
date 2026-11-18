namespace Sable.Syntax.Tests;

public sealed class TypeParserTests {
    static string Parse(string source) {
        var tree = SyntaxTree.ParseType(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(source, tree.ToFullString());
        return TreeDump.Dump(tree);
    }

    // ---- Names ------------------------------------------------------------

    [Theory]
    [InlineData("Int", "(SimpleNameType Int)")]
    [InlineData("Self", "(SelfType Self)")]
    [InlineData(
        "List<Int>",
        "(SimpleNameType List (TypeArgumentList < (SimpleNameType Int) >))"
    )]
    [InlineData(
        "Cache<Key, Value>",
        "(SimpleNameType Cache (TypeArgumentList < (SimpleNameType Key) (SimpleNameType Value) >))"
    )]
    [InlineData(
        "A.B",
        "(QualifiedName (SimpleNameType A) . (SimpleNameType B))"
    )]
    [InlineData(
        "Sable.Collections.List<T>",
        "(QualifiedName (QualifiedName (SimpleNameType Sable) . (SimpleNameType Collections)) "
        + ". (SimpleNameType List (TypeArgumentList < (SimpleNameType T) >)))"
    )]
    public void ParsesNames(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Fact]
    public void SplitsTheShiftTokenToCloseNestedGenerics() {
        // 'List<List<Int>>' ends on a single '>>' token, which must be split.
        Assert.Equal(
            "(SimpleNameType List (TypeArgumentList < "
            + "(SimpleNameType List (TypeArgumentList < (SimpleNameType Int) >)) >))",
            Parse("List<List<Int>>")
        );
    }

    [Fact]
    public void SplittingTheShiftTokenPreservesRoundTrip() {
        const string source = "  List<List<List<Int>>>  ";
        var tree = SyntaxTree.ParseType(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(source, tree.ToFullString());
    }

    // ---- Suffixes ---------------------------------------------------------

    [Theory]
    [InlineData("Int?", "(OptionalType (SimpleNameType Int) ?)")]
    [InlineData("Int??", "(OptionalType (OptionalType (SimpleNameType Int) ?) ?)")]
    [InlineData("Int?.Type", "(QualifiedName (OptionalType (SimpleNameType Int) ?) . (SimpleNameType Type))")]
    [InlineData("Widget!", "(ImplicitlyUnwrappedType (SimpleNameType Widget) !)")]
    [InlineData("Order&", "(ReferenceType (SimpleNameType Order) &)")]
    [InlineData("Order&?", "(ReferenceType (SimpleNameType Order) & ?)")]
    [InlineData(
        "Order&<u32>",
        "(ReferenceType (SimpleNameType Order) & (RelativeReferenceSpec < u32 >))"
    )]
    [InlineData(
        "Order&<u32>?",
        "(ReferenceType (SimpleNameType Order) & (RelativeReferenceSpec < u32 >) ?)"
    )]
    [InlineData(
        "Order&<u16 in pool>?",
        "(ReferenceType (SimpleNameType Order) & (RelativeReferenceSpec < u16 in pool >) ?)"
    )]
    public void ParsesSuffixes(string source, string expected) => Assert.Equal(expected, Parse(source));

    // ---- The array family, per docs/02-memory-model.md §2.5 ---------------

    [Theory]
    [InlineData("T[]", "(FixedSizeArrayType (SimpleNameType T))")]
    [InlineData("T[8]", "(FixedSizeArrayType (SimpleNameType T) (Literal 8))")]
    [InlineData("T[..]", "(InlineArrayType (SimpleNameType T) ..)")]
    [InlineData("T[..64]", "(InlineArrayType (SimpleNameType T) .. (Literal 64))")]
    [InlineData("T[>..]", "(GrowArrayType (SimpleNameType T) > ..)")]
    [InlineData("T[>..<]", "(GrowArrayType (SimpleNameType T) > ..<)")]
    [InlineData("T[:]", "(SliceType (SimpleNameType T))")]
    [InlineData(
        "T[][]",
        "(FixedSizeArrayType (FixedSizeArrayType (SimpleNameType T)))"
    )]
    [InlineData("Line[>..]", "(GrowArrayType (SimpleNameType Line) > ..)")]
    public void ParsesArrayFamily(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Fact]
    public void ParsesDictionaryType() =>
        Assert.Equal(
            "(DictionaryType (SimpleNameType String) (SimpleNameType Int))",
            Parse("[String: Int]")
        );

    [Fact]
    public void ParsesNestedInlineLayoutType() =>
        Assert.Equal(
            "(GrowArrayType (SimpleNameType OrderLine) > ..)",
            Parse("OrderLine[>..]")
        );

    // ---- Tuples, functions, groups ----------------------------------------

    [Theory]
    [InlineData(
        "(A, B)",
        "(TupleType (TupleTypeElement (SimpleNameType A)) (TupleTypeElement (SimpleNameType B)))"
    )]
    [InlineData(
        "(q: A, r: B)",
        "(TupleType (TupleTypeElement q (SimpleNameType A)) (TupleTypeElement r (SimpleNameType B)))"
    )]
    [InlineData("(T)", "(ParenthesizedType (SimpleNameType T))")]
    [InlineData("()", "(TupleType)")]
    public void ParsesTupleAndParenthesizedTypes(string source, string expected) =>
        Assert.Equal(expected, Parse(source));

    [Theory]
    [InlineData(
        "(A) -> B",
        "(FunctionType (TupleType (TupleTypeElement (SimpleNameType A))) -> (SimpleNameType B))"
    )]
    [InlineData(
        "() -> Void",
        "(FunctionType (TupleType) -> (SimpleNameType Void))"
    )]
    [InlineData(
        "(A) throws -> B",
        "(FunctionType (TupleType (TupleTypeElement (SimpleNameType A))) "
        + "(ThrowsSpec throws) -> (SimpleNameType B))"
    )]
    [InlineData(
        "(A) throws(IoError) -> B",
        "(FunctionType (TupleType (TupleTypeElement (SimpleNameType A))) "
        + "(ThrowsSpec throws (SimpleNameType IoError)) -> (SimpleNameType B))"
    )]
    [InlineData(
        "(A) rethrows -> B",
        "(FunctionType (TupleType (TupleTypeElement (SimpleNameType A))) "
        + "(ThrowsSpec rethrows) -> (SimpleNameType B))"
    )]
    [InlineData(
        "async (Request) throws(HttpError) -> Response",
        "(FunctionType async (TupleType (TupleTypeElement (SimpleNameType Request))) "
        + "(ThrowsSpec throws (SimpleNameType HttpError)) -> (SimpleNameType Response))"
    )]
    public void ParsesFunctionTypes(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Fact]
    public void FunctionTypeReturnIsRightAssociative() =>
        Assert.Equal(
            "(FunctionType (TupleType (TupleTypeElement (SimpleNameType A))) -> "
            + "(FunctionType (TupleType (TupleTypeElement (SimpleNameType B))) -> (SimpleNameType C)))",
            Parse("(A) -> (B) -> C")
        );

    // ---- some / any / ref / composition / union ---------------------------

    [Theory]
    [InlineData("some Drawable", "(OpaqueType some (SimpleNameType Drawable))")]
    [InlineData("any Drawable", "(ExistentialType any (SimpleNameType Drawable))")]
    [InlineData("ref Node", "(RefType ref (SimpleNameType Node))")]
    [InlineData(
        "P & Q",
        "(CompositionType (SimpleNameType P) & (SimpleNameType Q))"
    )]
    [InlineData(
        "P & Q & R",
        "(CompositionType (CompositionType (SimpleNameType P) & (SimpleNameType Q)) & (SimpleNameType R))"
    )]
    [InlineData(
        "any Hashable & Serializable",
        "(ExistentialType any (CompositionType (SimpleNameType Hashable) & (SimpleNameType Serializable)))"
    )]
    public void ParsesConstraintForms(string source, string expected) => Assert.Equal(expected, Parse(source));

    [Fact]
    public void ParsesStructuralUnion() =>
        Assert.Equal(
            "(UnionType (UnionType (SimpleNameType Int) | (SimpleNameType String)) "
            + "| (SimpleNameType Vec3))",
            Parse("Int | String | Vec3")
        );

    [Fact]
    public void CompositionBindsTighterThanUnion() =>
        Assert.Equal(
            "(UnionType (CompositionType (SimpleNameType A) & (SimpleNameType B)) "
            + "| (SimpleNameType C))",
            Parse("A & B | C")
        );

    [Fact]
    public void AmpersandIsAReferenceSuffixWhenNoTypeFollows() {
        // 'Order&' then ',' — a reference, not a composition.
        Assert.Equal(
            "(TupleType (TupleTypeElement (ReferenceType (SimpleNameType Order) &)) "
            + "(TupleTypeElement (SimpleNameType Int)))",
            Parse("(Order&, Int)")
        );
    }

    [Fact]
    public void ParsesAGenericSignatureShapedType() =>
        Assert.Equal(
            "(SimpleNameType Outcome (TypeArgumentList < "
            + "(GrowArrayType (SimpleNameType User) > ..) "
            + "(OptionalType (SimpleNameType ServiceError) ?) >))",
            Parse("Outcome<User[>..], ServiceError?>")
        );

    [Fact]
    public void TriviaIsPreservedInsideTypes() {
        const string source = "List< /* key */ Int >";
        var tree = SyntaxTree.ParseType(source);
        Assert.False(tree.HasErrors, tree.FormatDiagnostics());
        Assert.Equal(source, tree.ToFullString());
    }
}
