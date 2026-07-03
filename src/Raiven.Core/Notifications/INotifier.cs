namespace Raiven.Core.Notifications;

public interface INotifier
{
    /// <summary>Raised with the session id when the user asks to hear a summary (toast body, "Play summary", or "Play now").</summary>
    event Action<string>? PlaySummaryRequested;

    /// <summary>Raised with the session id when the user aborts a pending auto-play.</summary>
    event Action<string>? AbortRequested;

    /// <summary>Static finished-turn toast (no countdown). Headline is the chat's first prompt, or null to fall back to the folder line.</summary>
    void ShowFinished(string sessionId, string folderName, string? headline);

    /// <summary>Finished-turn toast with an auto-play progress bar and Play now / Abort buttons.</summary>
    void ShowFinishedCountdown(string sessionId, string folderName, string? headline, int totalSeconds);

    /// <summary>Advance the countdown toast's progress bar (fraction 0..1). Safe to call for a dismissed toast.</summary>
    void UpdateCountdownProgress(string sessionId, double fraction);

    /// <summary>Remove the toast for a session (e.g. once auto-play fires).</summary>
    void RemoveNotification(string sessionId);

    /// <summary>Show a non-fatal error to the user.</summary>
    void ShowError(string message);
}
