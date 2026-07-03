namespace Raiven.Core.Voice;

public static class SpeechText
{
    /// <summary>
    /// Caps spoken text at <paramref name="limit"/> whitespace-separated words;
    /// limit &lt;= 0 means unlimited. Truncated output is joined by single spaces.
    /// </summary>
    public static string LimitWords(string text, int limit)
    {
        if (limit <= 0) return text;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= limit ? text : string.Join(' ', words.Take(limit));
    }
}
