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

        foreach (var line in ReadLinesShared(transcriptPath))
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

        // Anchor on the last assistant entry, not the last prompt: the user may already
        // have queued the next message by the time we read the transcript (#15), and a
        // prompt with no reply after it would otherwise erase the turn that just finished.
        var lastAssistant = entries.FindLastIndex(e => !e.IsPrompt);
        var lastPrompt = lastAssistant >= 0
            ? entries.FindLastIndex(lastAssistant, e => e.IsPrompt)
            : entries.FindLastIndex(e => e.IsPrompt);
        if (lastPrompt < 0)
            lastPrompt = entries.FindLastIndex(e => e.IsPrompt);
        if (lastPrompt < 0)
            throw new InvalidDataException("No user prompt found in transcript.");

        var userPrompt = entries[lastPrompt].Prompt!;
        if (userPrompt.Length > MaxPromptChars)
            userPrompt = userPrompt[..MaxPromptChars];

        var textBuilder = new StringBuilder();
        var tools = new List<string>();
        foreach (var entry in entries.Skip(lastPrompt + 1).TakeWhile(e => !e.IsPrompt))
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
            if (IsSyntheticRecord(text)) return false;
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
            if (IsSyntheticRecord(sb.ToString())) return false;
            prompt = sb.ToString();
            return true;
        }

        return false;
    }

    /// <summary>Synthetic records (slash commands, bash mode, hook output) are stored as
    /// user lines of metadata XML; they are not prompts the user actually typed.</summary>
    private static bool IsSyntheticRecord(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text.TrimStart(), "^<[a-z][a-z0-9-]*>");

    /// <summary>
    /// Returns the chat's first user prompt as a single-line headline, truncated to
    /// <paramref name="maxChars"/> with an ellipsis; null when the file is missing or
    /// contains no user prompt. Stops reading at the first match.
    /// </summary>
    public static string? ReadFirstPrompt(string transcriptPath, int maxChars = 60)
    {
        if (!File.Exists(transcriptPath))
            return null;

        foreach (var line in ReadLinesShared(transcriptPath))
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

    /// <summary>
    /// Reads a file line by line while another process (Claude Code) still has it open
    /// for appending. Plain <see cref="File.ReadLines(string)"/> opens with
    /// <see cref="FileShare.Read"/>, which conflicts with the writer's open handle and
    /// throws a sharing violation - the "Raiven stuck at working" bug. Sharing write and
    /// delete makes RAIVEN's read handle compatible with the live writer.
    /// </summary>
    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
            yield return line;
    }
}
