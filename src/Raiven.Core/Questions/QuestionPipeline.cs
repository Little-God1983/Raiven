using System.Text;
using Raiven.Core.Config;
using Raiven.Core.Events;
using Raiven.Core.Logging;
using Raiven.Core.Summaries;
using Raiven.Core.Transcripts;
using Raiven.Core.Voice;

namespace Raiven.Core.Questions;

/// <summary>
/// Speaks an immediate voice notification when Claude Code has a question
/// (permission prompt, waiting for input). No countdown, no history entry.
/// </summary>
public sealed class QuestionPipeline(RaivenConfig config, IClaudeClient claude, IVoice voice)
{
    public const string SystemPrompt =
        "You are RAIVEN, a voice assistant. Claude Code is waiting for the user's attention. " +
        "Reply with ONE short spoken-style sentence telling the user what Claude Code is asking or " +
        "waiting for, in plain conversational language. No markdown, no emojis or special symbols, no preamble.";

    private static readonly HashSet<string> IgnoredTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "auth_success", "agent_completed", "elicitation_complete", "elicitation_response",
    };

    /// <summary>Housekeeping notification types are not questions; unknown/missing types are (fail open).</summary>
    public static bool IsQuestion(string? notificationType) =>
        notificationType is null || !IgnoredTypes.Contains(notificationType);

    public async Task AnnounceAsync(ClaudeNotificationEvent evt)
    {
        var announceLine = "Claude Code has a question.";
        try
        {
            var folder = Path.GetFileName(evt.Cwd.TrimEnd('\\', '/'));
            if (folder.Length == 0) folder = evt.Cwd;
            announceLine = $"Claude Code has a question in {folder}.";
            switch (config.QuestionVoice.ToLowerInvariant())
            {
                case "message" when !string.IsNullOrWhiteSpace(evt.Message):
                    await voice.SpeakAsync(SpeechText.LimitWords(evt.Message, config.QuestionWordLimit), priority: SpeechPriority.Urgent);
                    return;
                case "summary":
                    await voice.SpeakAsync(await SummarizeQuestionAsync(evt), priority: SpeechPriority.Urgent);
                    return;
                default: // "announce", "message" with empty message, and unknown values
                    if (!config.QuestionVoice.Equals("announce", StringComparison.OrdinalIgnoreCase) &&
                        !config.QuestionVoice.Equals("message", StringComparison.OrdinalIgnoreCase))
                        FileLog.Info($"Unknown QuestionVoice '{config.QuestionVoice}'; using announce");
                    await voice.SpeakAsync(announceLine, priority: SpeechPriority.Urgent);
                    return;
            }
        }
        catch (Exception ex)
        {
            FileLog.Error($"Question announcement failed for {evt.SessionId}; falling back to announce line", ex);
            try { await voice.SpeakAsync(announceLine); }
            catch (Exception voiceEx) { FileLog.Error("Fallback announcement also failed", voiceEx); }
        }
    }

    private async Task<string> SummarizeQuestionAsync(ClaudeNotificationEvent evt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Claude Code sent this notification:");
        sb.AppendLine(evt.Message.Length > 0 ? evt.Message : "(no message text)");
        try
        {
            var slice = TranscriptReader.ReadLastTurn(evt.TranscriptPath, config.MaxTranscriptChars);
            sb.AppendLine();
            sb.AppendLine("The user's last request was:");
            sb.AppendLine(slice.UserPrompt);
        }
        catch (Exception ex)
        {
            FileLog.Info($"Question summary proceeding without transcript context: {ex.Message}");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        return await claude.CompleteAsync(SystemPrompt, sb.ToString(), cts.Token);
    }
}
