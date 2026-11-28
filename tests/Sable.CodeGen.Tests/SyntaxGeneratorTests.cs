using Sable.Driver;
using Sable.Syntax;

namespace Sable.CodeGen.Tests;

/// <summary>
///     Sable.Syntax's <c>@syntax</c> generator: what it says of a union it
///     cannot write a tree from, and one it can. What the written views do is
///     tests/lib/Sable.Syntax's to test.
/// </summary>
public sealed class SyntaxGeneratorTests {
    /// <summary>The errors of one file with the library's generators' output, the first line of each with its place: <c>7:10 …</c>.</summary>
    static string Errors(string source) {
        var errors = Pipeline.Check([SyntaxTree.Parse(new SourceText("module App\n\nimport Sable.Syntax\n\n" + source, "app.sb"))]);
        return string.Join("\n", errors.Select(static e => {
            var lines = e.Split('\n');
            var place = lines[1].Trim().Replace("--> app.sb:", "", StringComparison.Ordinal);
            return $"{place} {lines[0][(lines[0].IndexOf(": ", StringComparison.Ordinal) + 2)..]}";
        }));
    }

    [Fact]
    public void ALanguageOfTokensNodesCategoriesAndListsIsWritten() =>
        Assert.Equal("", Errors("""
            @syntax
            public union Mini {
                Word
                Comma
                @trivia Space
                @category("Item") Name(word: SyntaxToken)
                @category("Item") Pair(first: ItemSyntax, comma: SyntaxToken?, second: ItemSyntax?)
                Items(`in`: SyntaxList<ItemSyntax>, all: SeparatedSyntaxList<NameSyntax>)
                @base @category("Item") Tagged(tags: SyntaxTokenList)
                @category("Tagged") @kinds("Loud", "Quiet") Note(tags: SyntaxTokenList, word: SyntaxToken)
            }

            fn use(_ node: SyntaxNode) -> Bool {
                let w = NameSyntax.make(word: GreenNode.token(MiniKind.Word.raw, "a"))
                let items = ItemsSyntax.make(`in`: GreenNode.list([w]), all: GreenNode.list([]))
                guard let pair = PairSyntax.cast(node) else {
                    return MiniKind.Space.isTrivia && items.isNode
                }
                let note = NoteSyntax.make(.Quiet, tags: GreenNode.list([]), word: GreenNode.token(MiniKind.Word.raw, "b"))
                return pair.comma == none && pair.second?.asName != none && pair.asItem.kind == .Pair
                    && note.isNode && pair.asItem.asTagged == none
            }
            """));

    [Theory]
    [InlineData("@syntax\nstruct S { }", "6:8 syntax: '@syntax' describes a language by a union of its kinds; 'S' is a struct")]
    [InlineData("@syntax\nunion U<T> { A(x: SyntaxToken) }", "'@syntax' is put on a union at the top of a file, with no generic parameters")]
    [InlineData("@syntax\nunion U { @category(\"E\") A }", "'@category' puts a kind of node in a category; 'A' is a token")]
    [InlineData("@syntax\nunion U { @category(e) A(x: SyntaxToken) }", "'@category' takes the categories' names as strings")]
    [InlineData("@syntax\nunion U { @category A(x: SyntaxToken) }", "'@category' names a category or more")]
    [InlineData("@syntax\nunion U { @trivia A(x: SyntaxToken) }", "'@trivia' is a kind of trivia, which has no slots; 'A' has them")]
    [InlineData("@syntax\nunion U { A(SyntaxToken) }", "each slot of 'A' has a label: its accessor's name")]
    [InlineData("@syntax\nunion U { A(x: Int) }", "the slot 'x' of 'A' is of 'Int', which is neither a kind of node of 'U' nor one of its categories")]
    [InlineData("@syntax\nunion U { A(x: SyntaxList<ASyntax>?) }", "the slot 'x' of 'A' is 'SyntaxList<ASyntax>?': a slot is 'SyntaxToken', a view, 'SyntaxList<View>', 'SeparatedSyntaxList<View>' or 'SyntaxTokenList', a token or a view optional")]
    [InlineData("@syntax\nunion U { A(x: SyntaxTokenList?) }", "the slot 'x' of 'A' is 'SyntaxTokenList?'")]
    [InlineData("@syntax\nunion U { @kinds(\"B\") A }", "'@kinds' names the kinds of a node that share its view; 'A' is a token")]
    [InlineData("@syntax\nunion U { @base @kinds(\"B\") A(x: SyntaxToken) }", "'@kinds' names the kinds of a node that share its view; 'A' is a '@base'")]
    [InlineData("@syntax\nunion U { @kinds(b) A(x: SyntaxToken) }", "'@kinds' takes the kinds' names as strings")]
    [InlineData("@syntax\nunion U { @kinds A(x: SyntaxToken) }", "'@kinds' names a kind or more")]
    [InlineData("@syntax\nunion U { @kinds(\"b c\") A(x: SyntaxToken) }", "'@kinds' names kinds: 'b c' is not a name")]
    [InlineData("@syntax\nunion U { B\n @kinds(\"B\", \"C\") A(x: SyntaxToken) }", "two kinds of 'U' are named 'B'")]
    [InlineData("@syntax\nunion U { @base @category(\"C\") B\n @base @category(\"B\") C\n A(x: SyntaxToken) }", "the category 'B' is in itself")]
    [InlineData("@syntax\nunion U { @base B(x: SyntaxToken)\n @category(\"B\") A(y: SyntaxToken) }", "'A' is a 'B', so it has the slot 'x: SyntaxToken'")]
    [InlineData("@syntax\nunion U { @base B(x: SyntaxToken)\n @category(\"B\") A(x: SyntaxToken?) }", "'A' is a 'B', so it has the slot 'x: SyntaxToken'")]
    [InlineData("@syntax\nunion U { @category(\"A\") A(x: SyntaxToken) }", "the category 'A' and a kind of node would both be 'ASyntax'")]
    public void WhatCannotBeWrittenIsSaid(string source, string message) => Assert.Contains(message, Errors(source));
}
