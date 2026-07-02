using System.Text.Json;

namespace Raiven.Core.Events;

public static class EventParser
{
    public static RaivenEvent Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Body is not valid JSON.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("Event body must be a JSON object.");

            if (TryGetString(root, "source", out var source) && TryGetString(root, "type", out var type))
            {
                var payload = root.TryGetProperty("payload", out var p) ? p : root;
                return new RaivenEvent(source, type, payload.Clone());
            }

            if (TryGetString(root, "hook_event_name", out var hookEvent))
                return new RaivenEvent("claude-code", hookEvent, root.Clone());

            throw new FormatException("Not a RAIVEN envelope or Claude Code hook payload.");
        }
    }

    public static bool TryParseClaudeStop(RaivenEvent e, out ClaudeStopEvent stop)
    {
        stop = null!;
        if (e.Source != "claude-code" || e.Type != "Stop")
            return false;
        if (!TryGetString(e.Payload, "session_id", out var sessionId) ||
            !TryGetString(e.Payload, "transcript_path", out var transcriptPath) ||
            !TryGetString(e.Payload, "cwd", out var cwd))
            return false;

        stop = new ClaudeStopEvent(sessionId, transcriptPath, cwd);
        return true;
    }

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        value = null!;
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(name, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString()!;
            return true;
        }
        return false;
    }
}
