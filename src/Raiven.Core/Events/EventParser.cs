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

    public static bool TryParseClaudeNotification(RaivenEvent e, out ClaudeNotificationEvent notification)
    {
        notification = null!;
        if (e.Source != "claude-code" || e.Type != "Notification")
            return false;
        if (!TryGetString(e.Payload, "session_id", out var sessionId) ||
            !TryGetString(e.Payload, "transcript_path", out var transcriptPath) ||
            !TryGetString(e.Payload, "cwd", out var cwd))
            return false;

        TryGetString(e.Payload, "message", out var message);
        var notificationType = TryGetString(e.Payload, "notification_type", out var t) ? t : null;
        notification = new ClaudeNotificationEvent(sessionId, transcriptPath, cwd, message ?? "", notificationType);
        return true;
    }

    /// <summary>
    /// The interactive AskUserQuestion dialog fires no Notification hook (and no Stop -
    /// the turn continues once answered), only a PreToolUse hook with
    /// tool_name "AskUserQuestion". We surface it from that hook as a question so RAIVEN
    /// still chimes/announces. The question text is pulled from tool_input.questions[].question.
    /// </summary>
    public static bool TryParseClaudeAskUserQuestion(RaivenEvent e, out ClaudeNotificationEvent question)
    {
        question = null!;
        if (e.Source != "claude-code" || e.Type != "PreToolUse")
            return false;
        // Matcher-based hook registration should already scope this to AskUserQuestion,
        // but re-check so a broader PreToolUse registration can never misfire.
        if (!TryGetString(e.Payload, "tool_name", out var toolName) ||
            !string.Equals(toolName, "AskUserQuestion", StringComparison.Ordinal))
            return false;
        if (!TryGetString(e.Payload, "session_id", out var sessionId) ||
            !TryGetString(e.Payload, "transcript_path", out var transcriptPath) ||
            !TryGetString(e.Payload, "cwd", out var cwd))
            return false;

        var message = ExtractAskUserQuestionText(e.Payload);
        question = new ClaudeNotificationEvent(sessionId, transcriptPath, cwd, message, "ask_user_question");
        return true;
    }

    /// <summary>Joins the question prompts from tool_input.questions[]; "" when absent/malformed.</summary>
    private static string ExtractAskUserQuestionText(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("tool_input", out var toolInput) ||
            toolInput.ValueKind != JsonValueKind.Object ||
            !toolInput.TryGetProperty("questions", out var questions) ||
            questions.ValueKind != JsonValueKind.Array)
            return "";

        var texts = new List<string>();
        foreach (var q in questions.EnumerateArray())
        {
            if (q.ValueKind == JsonValueKind.Object &&
                q.TryGetProperty("question", out var qt) &&
                qt.ValueKind == JsonValueKind.String)
            {
                var text = qt.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    texts.Add(text.Trim());
            }
        }
        return string.Join(" ", texts);
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
