namespace Raiven.Core.Transcripts;

public sealed record TurnSlice(string UserPrompt, string AssistantText, IReadOnlyList<string> ToolsUsed)
{
    /// <summary>One-line stats for the log, so an empty or truncated slice is visible
    /// at a glance when a summary comes back with "nothing to report" (#15).</summary>
    public string Describe() =>
        $"prompt {UserPrompt.Length} chars, assistant text {AssistantText.Length} chars, " +
        $"tools: {(ToolsUsed.Count > 0 ? string.Join(", ", ToolsUsed) : "none")}";
}
