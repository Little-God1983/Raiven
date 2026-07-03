namespace Raiven.Core.Notifications;

public interface INotifier
{
    /// <summary>Raised with the session id when the user asks to hear a summary (e.g. clicks a notification).</summary>
    event Action<string>? PlaySummaryRequested;

    /// <summary>Notify that a Claude Code session finished a turn.</summary>
    void ShowFinished(string sessionId, string folderName);

    /// <summary>Show a non-fatal error to the user.</summary>
    void ShowError(string message);
}
