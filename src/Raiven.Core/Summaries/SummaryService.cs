using System.Text;
using Raiven.Core.Transcripts;

namespace Raiven.Core.Summaries;

public sealed class SummaryService(IClaudeClient client)
{
    public const string SystemPrompt =
        "You are RAIVEN, a voice assistant that reports what the coding agent Claude Code just finished doing. " +
        "Reply with a 1-3 sentence spoken-style summary of what was accomplished and the outcome, in plain " +
        "conversational language, speaking in first person as the agent (for example: 'I fixed the login bug " +
        "and all tests are passing now.'). No markdown, no code, no emojis or special symbols, no file paths " +
        "unless essential, no preamble.";

    public static string BuildUserContent(TurnSlice slice)
    {
        var sb = new StringBuilder();
        sb.AppendLine("The user's request:");
        sb.AppendLine(slice.UserPrompt);
        sb.AppendLine();
        sb.Append("Tools the agent used: ");
        sb.AppendLine(slice.ToolsUsed.Count > 0 ? string.Join(", ", slice.ToolsUsed) : "none");
        sb.AppendLine();
        sb.AppendLine("What the agent said while working (may be truncated):");
        sb.Append(slice.AssistantText);
        return sb.ToString();
    }

    public Task<string> SummarizeAsync(TurnSlice slice, CancellationToken ct = default) =>
        client.CompleteAsync(SystemPrompt, BuildUserContent(slice), ct);
}
