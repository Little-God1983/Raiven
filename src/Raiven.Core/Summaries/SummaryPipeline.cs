using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, byte> _stopRequested = new();
    // Guards against a "Play now" click and the expiry timer firing near-simultaneously,
    // which would otherwise launch two Claude calls for the same turn. Held only through
    // generation: once speech starts, a newer trigger for the session may run (it replays
    // from cache or generates the next turn) and simply preempts the audio.
    private readonly ConcurrentDictionary<string, byte> _generating = new();
    // Identity of the session's current run. A new finished-turn toast obsoletes the
    // in-flight run (ObsoleteRun): an obsolete run stops updating the toast, never
    // starts speaking, and leaves toast removal to its successor.
    private readonly ConcurrentDictionary<string, Guid> _currentRun = new();

    /// <summary>Whether this session's text is the one currently being generated/spoken.</summary>
    public bool IsSpeaking(string sessionId) => Volatile.Read(ref _speakingSessionId) == sessionId;

    /// <summary>Skip the upcoming speech of this session's in-flight run (Stop clicked while
    /// it was still summarizing/generating). A fresh run clears any stale request.</summary>
    public void RequestStop(string sessionId) => _stopRequested[sessionId] = 0;

    /// <summary>Called when a new finished-turn toast is shown for the session: any in-flight
    /// run becomes obsolete - it stops touching the (new) toast and won't start speaking.</summary>
    public void ObsoleteRun(string sessionId) => _currentRun.TryRemove(sessionId, out _);

    private bool IsCurrentRun(string sessionId, Guid token) =>
        _currentRun.TryGetValue(sessionId, out var current) && current == token;

    public Task PlaySummaryAsync(string sessionId) => PlaySummaryAsync(sessionId, userInitiated: true);

    /// <summary>
    /// userInitiated: true for explicit clicks and replays (may show a fresh status
    /// toast), false for a countdown expiring on its own (may only update a toast
    /// that is still live - one the user hid or swiped away is never resurrected).
    /// </summary>
    public async Task PlaySummaryAsync(string sessionId, bool userInitiated)
    {
        if (!_generating.TryAdd(sessionId, 0))
        {
            FileLog.Info($"Summary already being prepared for {sessionId}; ignoring duplicate trigger.");
            return;
        }
        var generatingReleased = false;
        _stopRequested.TryRemove(sessionId, out _); // a stale Stop from a previous run must not cancel this one
        var runToken = Guid.NewGuid();
        _currentRun[sessionId] = runToken;
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
                    _generating.TryRemove(sessionId, out _); generatingReleased = true;
                    await SpeakWithPhasesAsync(sessionId, runToken, cached.SummaryText);
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
                    UpdateStatus(sessionId, runToken, "Summarizing with Haiku…", 0.25);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    spokenText = await _summaries.SummarizeAsync(slice, cts.Token);
                    FileLog.Info($"Summary for {sessionId}: {spokenText}");
                }

                history.Add(new SummaryHistoryEntry(
                    sessionId, headline, folder, DateTimeOffset.Now, info.TranscriptPath, lastWriteUtc, spokenText));

                _generating.TryRemove(sessionId, out _); generatingReleased = true;
                await SpeakWithPhasesAsync(sessionId, runToken, spokenText);
            }
            finally
            {
                // A status toast may never outlive its run - but an obsolete run's toast
                // belongs to its successor now, so only the current run removes it.
                if (config.ShowPlaybackStatus && IsCurrentRun(sessionId, runToken))
                    notifier.RemoveNotification(sessionId);
            }
        }
        catch (Exception ex)
        {
            FileLog.Error($"Summary failed for session {sessionId}", ex);
            notifier.ShowError("Couldn't get the summary - check the RAIVEN log for details.");
        }
        finally
        {
            if (!generatingReleased) _generating.TryRemove(sessionId, out _);
            _currentRun.TryRemove(new KeyValuePair<string, Guid>(sessionId, runToken));
        }
    }

    /// <summary>Replay a cached history entry (tray menu): no Claude call, no history write.</summary>
    public async Task PlayCachedAsync(SummaryHistoryEntry entry)
    {
        _stopRequested.TryRemove(entry.SessionId, out _);
        var runToken = Guid.NewGuid();
        _currentRun[entry.SessionId] = runToken;
        try
        {
            EnsureStatusToast(entry.SessionId, entry.Folder, entry.Headline, userInitiated: true);
            try
            {
                await SpeakWithPhasesAsync(entry.SessionId, runToken, entry.SummaryText);
            }
            finally
            {
                if (config.ShowPlaybackStatus && IsCurrentRun(entry.SessionId, runToken))
                    notifier.RemoveNotification(entry.SessionId);
            }
        }
        catch (Exception ex)
        {
            FileLog.Error($"Replay failed for session {entry.SessionId}", ex);
        }
        finally
        {
            _currentRun.TryRemove(new KeyValuePair<string, Guid>(entry.SessionId, runToken));
        }
    }

    private void EnsureStatusToast(string sessionId, string folder, string? headline, bool userInitiated)
    {
        if (!config.ShowPlaybackStatus) return;
        if (notifier.IsToastLive(sessionId)) return; // countdown toast still up: its bar carries the stages
        if (!userInitiated) return;                  // expiry after Hide/swipe: stay silent
        notifier.ShowPlaybackStatus(sessionId, folder, headline);
    }

    private void UpdateStatus(string sessionId, Guid runToken, string status, double fraction)
    {
        if (config.ShowPlaybackStatus && IsCurrentRun(sessionId, runToken))
            notifier.UpdatePlaybackStatus(sessionId, status, fraction);
    }

    private async Task SpeakWithPhasesAsync(string sessionId, Guid runToken, string text)
    {
        if (_stopRequested.TryRemove(sessionId, out _))
        {
            FileLog.Info($"Speech skipped for {sessionId}: Stop was clicked during preparation");
            return;
        }
        if (!IsCurrentRun(sessionId, runToken))
        {
            FileLog.Info($"Speech skipped for {sessionId}: a newer notification superseded this run");
            return;
        }
        Volatile.Write(ref _speakingSessionId, sessionId);
        try
        {
            await voice.SpeakAsync(text, phase => UpdateStatus(sessionId, runToken, PhaseStatus(phase), PhaseFraction(phase)));
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
