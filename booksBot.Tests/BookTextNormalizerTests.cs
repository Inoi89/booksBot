using booksBot.Core.Services;

namespace booksBot.Tests;

public sealed class BookTextNormalizerTests
{
    [Theory]
    [InlineData("  Ёжик — в ТУМАНЕ! ", "ежик в тумане")]
    [InlineData("Касс,   Маркус", "касс маркус")]
    [InlineData("L'école", "l ecole")]
    public void Normalize_ProducesSearchFriendlyText(string source, string expected)
    {
        Assert.Equal(expected, BookTextNormalizer.Normalize(source));
    }

    [Fact]
    public void Tokens_DeduplicatesWords()
    {
        Assert.Equal(new[] { "маркус", "касс" }, BookTextNormalizer.Tokens("Маркус Маркус Касс"));
    }
}
