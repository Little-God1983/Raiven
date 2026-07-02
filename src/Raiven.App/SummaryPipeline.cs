using Raiven.Core.Config;
using Raiven.Core.Logging;
using Raiven.Core.Sessions;
using Raiven.Core.Summaries;
using Raiven.Core.Transcripts;

namespace Raiven.App;

public sealed class SummaryPipeline(
    SessionRegistry registry,
    RaivenConfig config,
    IClaudeClient claude,
    ToastService toasts,
    VoiceService voice)
{
    private readonly SummaryService _summaries = new(claude);

    public async Task PlaySummaryAsync(string sessionId)
    {
        try
        {
            if (!registry.TryGet(sessionId, DateTimeOffset.Now, out var info))
            {
                toasts.ShowError("That session's details are no longer available.");
                return;
            }

            var slice = TranscriptReader.ReadLastTurn(info.TranscriptPath, config.MaxTranscriptChars);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var summary = await _summaries.SummarizeAsync(slice, cts.Token);
            FileLog.Info($"Summary for {sessionId}: {summary}");

            voice.Speak(summary);
        }
        catch (Exception ex)
        {
            FileLog.Error($"Summary failed for session {sessionId}", ex);
            toasts.ShowError("Couldn't get the summary - check the RAIVEN log for details.");
        }
    }
}
