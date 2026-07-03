using Raiven.Core.Config;
using Raiven.Core.Logging;
using Raiven.Core.Notifications;
using Raiven.Core.Sessions;
using Raiven.Core.Transcripts;
using Raiven.Core.Voice;

namespace Raiven.Core.Summaries;

public sealed class SummaryPipeline(
    SessionRegistry registry,
    RaivenConfig config,
    IClaudeClient claude,
    INotifier notifier,
    IVoice voice,
    SummaryHistory history)
{
    private readonly SummaryService _summaries = new(claude);

    public async Task PlaySummaryAsync(string sessionId)
    {
        try
        {
            if (!registry.TryGet(sessionId, DateTimeOffset.Now, out var info))
            {
                notifier.ShowError("That session's details are no longer available.");
                return;
            }

            var lastWriteUtc = File.GetLastWriteTimeUtc(info.TranscriptPath);
            if (history.TryGetCached(sessionId, lastWriteUtc, out var cached))
            {
                FileLog.Info($"Replaying cached summary for {sessionId}");
                voice.Speak(cached.SummaryText);
                return;
            }

            var slice = TranscriptReader.ReadLastTurn(info.TranscriptPath, config.MaxTranscriptChars);

            string spokenText;
            if (config.FinishedTurnVoice.Equals("message", StringComparison.OrdinalIgnoreCase))
            {
                spokenText = string.IsNullOrWhiteSpace(slice.AssistantText)
                    ? "Claude finished, but there was no message to read."
                    : SpeechText.LimitWords(slice.AssistantText, config.FinishedTurnWordLimit);
                FileLog.Info($"Reading last message for {sessionId}");
            }
            else
            {
                if (!config.FinishedTurnVoice.Equals("summary", StringComparison.OrdinalIgnoreCase))
                    FileLog.Info($"Unknown FinishedTurnVoice '{config.FinishedTurnVoice}'; using summary");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                spokenText = await _summaries.SummarizeAsync(slice, cts.Token);
                FileLog.Info($"Summary for {sessionId}: {spokenText}");
            }

            var folder = Path.GetFileName(info.Cwd.TrimEnd('\\', '/'));
            if (folder.Length == 0) folder = info.Cwd;
            var headline = TranscriptReader.ReadFirstPrompt(info.TranscriptPath) ?? folder;
            history.Add(new SummaryHistoryEntry(
                sessionId, headline, folder, DateTimeOffset.Now, info.TranscriptPath, lastWriteUtc, spokenText));

            voice.Speak(spokenText);
        }
        catch (Exception ex)
        {
            FileLog.Error($"Summary failed for session {sessionId}", ex);
            notifier.ShowError("Couldn't get the summary - check the RAIVEN log for details.");
        }
    }
}
