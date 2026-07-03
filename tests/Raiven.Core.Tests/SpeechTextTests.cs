using Raiven.Core.Voice;

namespace Raiven.Core.Tests;

public class SpeechTextTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void LimitWords_ZeroOrNegative_ReturnsUnchanged(int limit)
    {
        Assert.Equal("one two  three", SpeechText.LimitWords("one two  three", limit));
    }

    [Fact]
    public void LimitWords_BelowWordCount_TruncatesJoinedBySingleSpaces()
    {
        Assert.Equal("one two three", SpeechText.LimitWords("one  two\nthree four five", 3));
    }

    [Fact]
    public void LimitWords_AtOrAboveWordCount_ReturnsUnchanged()
    {
        Assert.Equal("one two three", SpeechText.LimitWords("one two three", 3));
        Assert.Equal("one two three", SpeechText.LimitWords("one two three", 10));
    }
}
