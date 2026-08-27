using Raiven.Core.Config;
using Raiven.Core.Events;
using Raiven.Core.Logging;

namespace Raiven.Core.Questions;

/// <summary>
/// Decides whether an incoming Claude Code Notification is worth announcing: it filters
/// housekeeping types, and drops the generic permission prompt Claude Code fires for an
/// AskUserQuestion dialog that RAIVEN already announced from the PreToolUse hook (see
/// <see cref="QuestionPipeline.IsDuplicateAskUserQuestionPrompt"/>).
///
/// Suppression is correlated, never blind: a permission copy is only dropped when this process
/// actually announced an AskUserQuestion for the same session inside
/// <see cref="DefaultCorrelationWindow"/> (the observed gap is ~6 seconds). Installs without the
/// PreToolUse hook - never merged, scoped away by a project settings.json, or RAIVEN not
/// listening when it fired - have nothing recorded, so their permission prompt is announced
/// rather than swallowed. One announcement excuses exactly one duplicate.
/// </summary>
public sealed class QuestionGate(RaivenConfig config, TimeSpan? correlationWindow = null)
{
    public static readonly TimeSpan DefaultCorrelationWindow = TimeSpan.FromSeconds(120);

    private readonly TimeSpan _window = correlationWindow ?? DefaultCorrelationWindow;
    private readonly Dictionary<string, DateTimeOffset> _announced = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>Records that the real question for this session was just announced from the PreToolUse hook.</summary>
    public void NoteAskUserQuestion(string sessionId, DateTimeOffset now)
    {
        lock (_lock)
        {
            // Sweep markers whose permission copy never arrived, so a long-running tray process
            // doesn't accumulate one entry per dead session.
            foreach (var stale in _announced.Where(kv => now - kv.Value > _window).Select(kv => kv.Key).ToList())
                _announced.Remove(stale);
            _announced[sessionId] = now;
        }
    }

    public bool ShouldAnnounce(ClaudeNotificationEvent notification, DateTimeOffset now)
    {
        if (!QuestionPipeline.IsQuestion(notification.NotificationType))
            return false;

        if (!config.SuppressDuplicateAskUserQuestionPrompt ||
            !QuestionPipeline.IsDuplicateAskUserQuestionPrompt(notification.NotificationType, notification.Message))
            return true;

        if (TryConsumeAnnouncement(notification.SessionId, now))
        {
            FileLog.Info($"Skipping duplicate AskUserQuestion permission prompt for {notification.SessionId}");
            return false;
        }

        FileLog.Info(
            $"Announcing AskUserQuestion permission prompt for {notification.SessionId}: no PreToolUse " +
            "announcement to duplicate - is the PreToolUse hook registered? See docs/hook-snippet.json");
        return true;
    }

    private bool TryConsumeAnnouncement(string sessionId, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_announced.TryGetValue(sessionId, out var announcedAt) || now - announcedAt > _window)
                return false;
            _announced.Remove(sessionId); // consumed: a later prompt in this session is a real one
            return true;
        }
    }
}
