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
    private string? _speakingSessionId;

    /// <summary>Whether this session's text is the one currently being generated/spoken.</summary>
    public bool IsSpeaking(string sessionId) => Volatile.Read(ref _speakingSessionId) == sessionId;

    public Task PlaySummaryAsync(string sessionId) => PlaySummaryAsync(sessionId, userInitiated: true);

    /// <summary>
    /// userInitiated: true for explicit clicks and replays (may show a fresh status
    /// toast), false for a countdown expiring on its own (may only update a toast
    /// that is still live - one the user hid or swiped away is never resurrected).
    /// </summary>
    public async Task PlaySummaryAsync(string sessionId, bool userInitiated)
    {
        try
        {
            if (!registry.TryGet(sessionId, DateTimeOffset.Now, out var info))
            {
                notifier.ShowError("That session's details are no longer available.");
                return;
            }

            var folder = Path.GetFileName(info.Cwd.TrimEnd('\\', '/'));
            if (folder.Length == 0) folder = info.Cwd;
            var headline = TranscriptReader.ReadFirstPrompt(info.TranscriptPath) ?? folder;

            EnsureStatusToast(sessionId, folder, headline, userInitiated);
            try
            {
                var lastWriteUtc = File.GetLastWriteTimeUtc(info.TranscriptPath);
                if (history.TryGetCached(sessionId, lastWriteUtc, out var cached))
                {
                    FileLog.Info($"Replaying cached summary for {sessionId}");
                    await SpeakWithPhasesAsync(sessionId, cached.SummaryText);
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
                    UpdateStatus(sessionId, "Summarizing with Haiku…", 0.25);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    spokenText = await _summaries.SummarizeAsync(slice, cts.Token);
                    FileLog.Info($"Summary for {sessionId}: {spokenText}");
                }

                history.Add(new SummaryHistoryEntry(
                    sessionId, headline, folder, DateTimeOffset.Now, info.TranscriptPath, lastWriteUtc, spokenText));

                await SpeakWithPhasesAsync(sessionId, spokenText);
            }
            finally
            {
                // A status toast may never outlive its playback run.
                if (config.ShowPlaybackStatus)
                    notifier.RemoveNotification(sessionId);
            }
        }
        catch (Exception ex)
        {
            FileLog.Error($"Summary failed for session {sessionId}", ex);
            notifier.ShowError("Couldn't get the summary - check the RAIVEN log for details.");
        }
    }

    /// <summary>Replay a cached history entry (tray menu): no Claude call, no history write.</summary>
    public async Task PlayCachedAsync(SummaryHistoryEntry entry)
    {
        try
        {
            EnsureStatusToast(entry.SessionId, entry.Folder, entry.Headline, userInitiated: true);
            try
            {
                await SpeakWithPhasesAsync(entry.SessionId, entry.SummaryText);
            }
            finally
            {
                if (config.ShowPlaybackStatus)
                    notifier.RemoveNotification(entry.SessionId);
            }
        }
        catch (Exception ex)
        {
            FileLog.Error($"Replay failed for session {entry.SessionId}", ex);
        }
    }

    private void EnsureStatusToast(string sessionId, string folder, string? headline, bool userInitiated)
    {
        if (!config.ShowPlaybackStatus) return;
        if (notifier.IsToastLive(sessionId)) return; // countdown toast still up: its bar carries the stages
        if (!userInitiated) return;                  // expiry after Hide/swipe: stay silent
        notifier.ShowPlaybackStatus(sessionId, folder, headline);
    }

    private void UpdateStatus(string sessionId, string status, double fraction)
    {
        if (config.ShowPlaybackStatus)
            notifier.UpdatePlaybackStatus(sessionId, status, fraction);
    }

    private async Task SpeakWithPhasesAsync(string sessionId, string text)
    {
        Volatile.Write(ref _speakingSessionId, sessionId);
        try
        {
            await voice.SpeakAsync(text, phase => UpdateStatus(sessionId, PhaseStatus(phase), PhaseFraction(phase)));
        }
        finally
        {
            // Only clear our own claim: a preempting session may have overwritten it.
            Interlocked.CompareExchange(ref _speakingSessionId, null, sessionId);
        }
    }

    private static string PhaseStatus(VoicePhase phase) => phase switch
    {
        VoicePhase.LoadingModel => "Loading voice model…",
        VoicePhase.Generating => "Generating voice…",
        _ => "Speaking…",
    };

    private static double PhaseFraction(VoicePhase phase) => phase switch
    {
        VoicePhase.LoadingModel => 0.45,
        VoicePhase.Generating => 0.65,
        _ => 0.9,
    };
}
