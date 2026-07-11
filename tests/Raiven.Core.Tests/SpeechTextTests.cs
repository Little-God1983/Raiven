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

    // CleanForSpeech - strip what Kokoro mispronounces (#12), keep real words/punctuation.

    [Fact]
    public void CleanForSpeech_PlainText_Unchanged()
    {
        Assert.Equal("I fixed the bug and all tests pass.",
            SpeechText.CleanForSpeech("I fixed the bug and all tests pass."));
    }

    [Fact]
    public void CleanForSpeech_RemovesEmojis()
    {
        Assert.Equal("All tests pass", SpeechText.CleanForSpeech("All tests pass ✅🎉"));
    }

    [Fact]
    public void CleanForSpeech_StripsMarkdownAndCodeDecoration()
    {
        Assert.Equal("Fixed the login bug in auth.cs",
            SpeechText.CleanForSpeech("**Fixed** the `login` bug in `auth.cs`"));
    }

    [Fact]
    public void CleanForSpeech_StripsAssortedSymbols()
    {
        Assert.Equal("a b c", SpeechText.CleanForSpeech("a | b < c >"));
    }

    [Fact]
    public void CleanForSpeech_PreservesGermanLettersAndPunctuation()
    {
        Assert.Equal("Grüße, alles klar!", SpeechText.CleanForSpeech("Grüße, alles klar!"));
    }

    [Fact]
    public void CleanForSpeech_NormalizesSmartQuotes()
    {
        Assert.Equal("\"don't\"", SpeechText.CleanForSpeech("“don’t”"));
    }

    [Fact]
    public void CleanForSpeech_NormalizesEllipsis()
    {
        Assert.Equal("Where was I...", SpeechText.CleanForSpeech("Where was I…"));
    }

    [Fact]
    public void CleanForSpeech_MapsAmpersandAndPercentToWords()
    {
        Assert.Equal("cats and dogs, 50 percent done",
            SpeechText.CleanForSpeech("cats & dogs, 50% done"));
    }

    [Fact]
    public void CleanForSpeech_CollapsesWhitespaceAndTrims()
    {
        Assert.Equal("hello world", SpeechText.CleanForSpeech("  hello   world  "));
    }

    [Fact]
    public void CleanForSpeech_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Equal("", SpeechText.CleanForSpeech(""));
        Assert.Equal("", SpeechText.CleanForSpeech("   "));
    }
}
