namespace Raiven.Core.Notifications;

public interface INotifier
{
    /// <summary>Raised with the session id when the user asks to hear a summary (toast body, "Play summary", or "Play now").</summary>
    event Action<string>? PlaySummaryRequested;

    /// <summary>Raised with the session id when the user aborts a pending auto-play (legacy Abort button).</summary>
    event Action<string>? AbortRequested;

    /// <summary>Raised when the user clicks Stop: cancel a pending countdown, or stop speech if this session is speaking.</summary>
    event Action<string>? StopRequested;

    /// <summary>Raised when the user clicks Hide: dismiss the toast only - countdown and playback continue.</summary>
    event Action<string>? HideRequested;

    /// <summary>Static finished-turn toast (no countdown). Headline is the chat's first prompt, or null to fall back to the folder line.</summary>
    void ShowFinished(string sessionId, string folderName, string? headline);

    /// <summary>Finished-turn toast with an auto-play progress bar. Buttons depend on ShowPlaybackStatus config.</summary>
    void ShowFinishedCountdown(string sessionId, string folderName, string? headline, int totalSeconds);

    /// <summary>Toast for a Claude Code question / attention request. No actions; informational only.</summary>
    void ShowQuestion(string folderName, string? headline, string message);

    /// <summary>Advance the countdown toast's progress bar (fraction 0..1). Safe to call for a dismissed toast.</summary>
    void UpdateCountdownProgress(string sessionId, double fraction);

    /// <summary>Standalone playback-status toast (Stop/Hide buttons, progress bar) for a run without a live countdown toast.</summary>
    void ShowPlaybackStatus(string sessionId, string folderName, string? headline);

    /// <summary>Update the live toast's progress bar to a pipeline stage. No-op once the toast is gone.</summary>
    void UpdatePlaybackStatus(string sessionId, string status, double fraction);

    /// <summary>Whether a toast for this session is believed to still be un-dismissed.</summary>
    bool IsToastLive(string sessionId);

    /// <summary>Remove the toast for a session (e.g. once playback finishes).</summary>
    void RemoveNotification(string sessionId);

    /// <summary>Show a non-fatal error to the user.</summary>
    void ShowError(string message);
}
