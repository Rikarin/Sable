using Xunit;

namespace Sable.LanguageServer.Tests;

/// <summary>Offsets to protocol positions and back: lines, line breaks, and UTF-16 columns.</summary>
public sealed class LineIndexTests {
    [Theory]
    [InlineData("a\nb", 2, 1, 0)]
    [InlineData("a\r\nb", 3, 1, 0)]
    [InlineData("a\rb", 2, 1, 0)]
    [InlineData("ab\n", 3, 1, 0)]
    [InlineData("😀x", 2, 0, 2)]
    public void OffsetsMapToPositions(string text, int offset, int line, int character) {
        var lines = LineIndex.Of(text);
        Assert.Equal(new LspPosition(line, character), lines.PositionOf(offset));
        Assert.Equal(offset, lines.OffsetOf(new LspPosition(line, character)));
    }

    [Fact]
    public void AColumnPastTheEndOfALineIsItsEnd() {
        var lines = LineIndex.Of("ab\r\ncd");
        Assert.Equal(2, lines.OffsetOf(new LspPosition(0, 99)));
        Assert.Equal(6, lines.OffsetOf(new LspPosition(1, 99)));
        Assert.Equal(6, lines.OffsetOf(new LspPosition(7, 0)));
    }

    [Fact]
    public void AnEmptyTextHasOneLine() {
        var lines = LineIndex.Of("");
        Assert.Equal(1, lines.LineCount);
        Assert.Equal(new LspPosition(0, 0), lines.PositionOf(0));
    }

    [Theory]
    [InlineData("lS", "lengthSquared", true)]
    [InlineData("len", "lengthSquared", true)]
    [InlineData("sq", "lengthSquared", false)]
    [InlineData("", "anything", true)]
    [InlineData("TOT", "total", true)]
    public void CompletionMatchesLettersInOrder(string typed, string name, bool matches) =>
        Assert.Equal(matches, LanguageServer.Matches(typed, name));
}
