using System.Text;
using System.Text.Json;

namespace Raiven.Core.Transcripts;

public static class TranscriptReader
{
    private const int MaxPromptChars = 2000;

    public static TurnSlice ReadLastTurn(string transcriptPath, int maxChars = 30000)
    {
        if (!File.Exists(transcriptPath))
            throw new FileNotFoundException("Transcript not found.", transcriptPath);

        // (index, isPrompt, promptText) for user lines; (texts, tools) for assistant lines.
        var entries = new List<(bool IsPrompt, string? Prompt, List<string> Texts, List<string> Tools)>();

        foreach (var line in File.ReadLines(transcriptPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String) continue;
                var type = typeProp.GetString();

                if (type == "user")
                {
                    if (TryExtractPrompt(root, out var prompt))
                        entries.Add((true, prompt, [], []));
                }
                else if (type == "assistant")
                {
                    var (texts, toolNames) = ExtractAssistant(root);
                    if (texts.Count > 0 || toolNames.Count > 0)
                        entries.Add((false, null, texts, toolNames));
                }
            }
        }

        var lastPrompt = entries.FindLastIndex(e => e.IsPrompt);
        if (lastPrompt < 0)
            throw new InvalidDataException("No user prompt found in transcript.");

        var userPrompt = entries[lastPrompt].Prompt!;
        if (userPrompt.Length > MaxPromptChars)
            userPrompt = userPrompt[..MaxPromptChars];

        var textBuilder = new StringBuilder();
        var tools = new List<string>();
        foreach (var entry in entries.Skip(lastPrompt + 1).Where(e => !e.IsPrompt))
        {
            foreach (var t in entry.Texts)
            {
                if (textBuilder.Length > 0) textBuilder.Append("\n\n");
                textBuilder.Append(t);
            }
            foreach (var tool in entry.Tools)
            {
                if (!tools.Contains(tool)) tools.Add(tool);
            }
        }

        var assistantText = textBuilder.ToString();
        if (assistantText.Length > maxChars)
            assistantText = assistantText[^maxChars..];

        return new TurnSlice(userPrompt, assistantText, tools);
    }

    private static bool TryExtractPrompt(JsonElement root, out string prompt)
    {
        prompt = null!;
        if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True)
            return false;
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return false;
        if (!message.TryGetProperty("content", out var content))
            return false;

        if (content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString()!;
            if (string.IsNullOrWhiteSpace(text)) return false;
            prompt = text;
            return true;
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("type", out var itemType) || itemType.ValueKind != JsonValueKind.String) continue;
                var kind = itemType.GetString();
                if (kind == "tool_result") return false;
                if (kind == "text" && item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    sb.Append(t.GetString());
            }
            if (sb.Length == 0) return false;
            prompt = sb.ToString();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the chat's first user prompt as a single-line headline, truncated to
    /// <paramref name="maxChars"/> with an ellipsis; null when the file is missing or
    /// contains no user prompt. Stops reading at the first match.
    /// </summary>
    public static string? ReadFirstPrompt(string transcriptPath, int maxChars = 60)
    {
        if (!File.Exists(transcriptPath))
            return null;

        foreach (var line in File.ReadLines(transcriptPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String) continue;
                if (typeProp.GetString() != "user") continue;
                if (!TryExtractPrompt(root, out var prompt)) continue;

                var headline = prompt.ReplaceLineEndings(" ").Trim();
                if (headline.Length == 0) continue;
                return headline.Length <= maxChars ? headline : headline[..maxChars].TrimEnd() + "…";
            }
        }

        return null;
    }

    private static (List<string> Texts, List<string> Tools) ExtractAssistant(JsonElement root)
    {
        var texts = new List<string>();
        var tools = new List<string>();
        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("type", out var itemType) || itemType.ValueKind != JsonValueKind.String) continue;
                switch (itemType.GetString())
                {
                    case "text" when item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String:
                        var text = t.GetString()!;
                        if (!string.IsNullOrWhiteSpace(text)) texts.Add(text);
                        break;
                    case "tool_use" when item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String:
                        tools.Add(n.GetString()!);
                        break;
                }
            }
        }
        return (texts, tools);
    }
}
