using System.Text;

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

    // Sentence punctuation Kokoro reads naturally; everything outside this (plus letters,
    // digits and whitespace) is treated as a non-speech symbol and dropped.
    private const string AllowedPunctuation = ".,!?;:'\"()-";

    /// <summary>
    /// Strips the emojis, markdown and stray symbols that Kokoro mispronounces (#12) while
    /// keeping real words and sentence punctuation. Uses a keep-list, so emojis (including
    /// multi-code-point ones) and unknown symbols fall away without enumerating them; letters
    /// of any language, including German umlauts, are preserved. Smart quotes and ellipses are
    /// normalized to plain equivalents, and a couple of common signs are spoken as words.
    /// </summary>
    public static string CleanForSpeech(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '‘' or '’' or '‚' or '‛': sb.Append('\''); break; // smart single quotes
                case '“' or '”' or '„' or '‟': sb.Append('"'); break;  // smart double quotes
                case '…': sb.Append("..."); break;                                    // ellipsis
                case '&': sb.Append(" and "); break;
                case '%': sb.Append(" percent "); break;
                default:
                    if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) || AllowedPunctuation.IndexOf(ch) >= 0)
                        sb.Append(ch);
                    else
                        sb.Append(' '); // emoji / markdown / other symbol -> gap, collapsed below
                    break;
            }
        }

        // Collapse the gaps left by stripped symbols (and any original run of whitespace) and trim.
        return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
