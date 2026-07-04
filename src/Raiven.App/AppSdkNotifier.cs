using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Raiven.Core.Config;
using Raiven.Core.Logging;
using Raiven.Core.Notifications;

namespace Raiven.App;

public sealed class AppSdkNotifier : INotifier
{
    public event Action<string>? PlaySummaryRequested;
    public event Action<string>? AbortRequested;
    public event Action<string>? StopRequested;
    public event Action<string>? HideRequested;

    private readonly RaivenConfig _config;
    private readonly Dictionary<string, uint> _progressSequences = [];
    // The AppNotifications API has no dismissed event, so liveness is optimistic:
    // shown -> live; any activation, RemoveNotification, or a NotFound progress
    // update (user swiped it away) -> dead. A dead toast is never updated again.
    private readonly HashSet<string> _liveTags = [];
    private readonly Lock _lock = new();

    public AppSdkNotifier(RaivenConfig config)
    {
        _config = config;
        // Subscribe BEFORE Register, per the Windows App SDK contract, so activations
        // that arrive during registration are not lost.
        AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
        AppNotificationManager.Default.Register();
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try
        {
            if (!args.Arguments.TryGetValue("action", out var action) ||
                !args.Arguments.TryGetValue("sessionId", out var sessionId))
                return;

            // Windows dismisses a toast on any activation, body click or button.
            lock (_lock) _liveTags.Remove(sessionId);

            switch (action)
            {
                case "playSummary" or "playNow":
                    FileLog.Info($"Toast activated: {action} for {sessionId}");
                    PlaySummaryRequested?.Invoke(sessionId);
                    break;
                case "abort":
                    FileLog.Info($"Toast activated: abort for {sessionId}");
                    AbortRequested?.Invoke(sessionId);
                    break;
                case "stop":
                    FileLog.Info($"Toast activated: stop for {sessionId}");
                    StopRequested?.Invoke(sessionId);
                    break;
                case "hide":
                    FileLog.Info($"Toast activated: hide for {sessionId}");
                    HideRequested?.Invoke(sessionId);
                    break;
            }
        }
        catch (Exception ex)
        {
            FileLog.Error("Toast activation handling failed", ex);
        }
    }

    public void ShowFinished(string sessionId, string folderName, string? headline)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "playSummary")
                .AddArgument("sessionId", sessionId);
            AddHeadline(builder, folderName, headline);
            builder
                .AddText("Click to hear a summary.")
                .AddButton(new AppNotificationButton("Play summary")
                    .AddArgument("action", "playSummary")
                    .AddArgument("sessionId", sessionId))
                .SetDuration(AppNotificationDuration.Long);
            TrySetLogo(builder);

            var notification = builder.BuildNotification();
            notification.Tag = sessionId;
            lock (_lock) _liveTags.Add(sessionId);
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            FileLog.Error($"Showing finished toast failed for {sessionId}", ex);
        }
    }

    public void ShowFinishedCountdown(string sessionId, string folderName, string? headline, int totalSeconds)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "playNow")
                .AddArgument("sessionId", sessionId);
            AddHeadline(builder, folderName, headline);
            builder.AddProgressBar(new AppNotificationProgressBar()
                .BindValue()
                .BindStatus());
            if (_config.ShowPlaybackStatus)
            {
                // Reminder keeps the toast on screen through countdown AND playback;
                // it is removed programmatically when speech ends.
                builder
                    .SetScenario(AppNotificationScenario.Reminder)
                    .AddButton(new AppNotificationButton("Play now")
                        .AddArgument("action", "playNow")
                        .AddArgument("sessionId", sessionId))
                    .AddButton(new AppNotificationButton("Stop")
                        .AddArgument("action", "stop")
                        .AddArgument("sessionId", sessionId))
                    .AddButton(new AppNotificationButton("Hide")
                        .AddArgument("action", "hide")
                        .AddArgument("sessionId", sessionId));
            }
            else
            {
                builder
                    .AddButton(new AppNotificationButton("Play now")
                        .AddArgument("action", "playNow")
                        .AddArgument("sessionId", sessionId))
                    .AddButton(new AppNotificationButton("Abort")
                        .AddArgument("action", "abort")
                        .AddArgument("sessionId", sessionId))
                    .SetDuration(AppNotificationDuration.Long);
            }
            TrySetLogo(builder);

            var notification = builder.BuildNotification();
            notification.Tag = sessionId;
            notification.Progress = new AppNotificationProgressData(sequenceNumber: 1)
            {
                Value = 0,
                Status = $"Auto-playing in {totalSeconds}s…",
            };
            lock (_lock)
            {
                _progressSequences[sessionId] = 1;
                _liveTags.Add(sessionId);
            }
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            FileLog.Error($"Showing countdown toast failed for {sessionId}", ex);
        }
    }

    public void ShowPlaybackStatus(string sessionId, string folderName, string? headline)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                // Body click = Hide: dismiss the toast, playback continues.
                .AddArgument("action", "hide")
                .AddArgument("sessionId", sessionId)
                .SetScenario(AppNotificationScenario.Reminder)
                .MuteAudio();
            if (headline is not null)
                builder.AddText(headline);
            builder
                .AddText($"Playing summary from {folderName}")
                .AddProgressBar(new AppNotificationProgressBar()
                    .BindValue()
                    .BindStatus())
                .AddButton(new AppNotificationButton("Stop")
                    .AddArgument("action", "stop")
                    .AddArgument("sessionId", sessionId))
                .AddButton(new AppNotificationButton("Hide")
                    .AddArgument("action", "hide")
                    .AddArgument("sessionId", sessionId));
            TrySetLogo(builder);

            var notification = builder.BuildNotification();
            notification.Tag = sessionId;
            notification.Progress = new AppNotificationProgressData(sequenceNumber: 1)
            {
                Value = 0.1,
                Status = "Working…",
            };
            lock (_lock)
            {
                _progressSequences[sessionId] = 1;
                _liveTags.Add(sessionId);
            }
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            FileLog.Error($"Showing status toast failed for {sessionId}", ex);
        }
    }

    public void ShowQuestion(string folderName, string? headline, string message)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddText("Claude Code has a question")
                .AddText(headline ?? folderName);
            if (!string.IsNullOrWhiteSpace(message))
                builder.AddText(message);
            TrySetLogo(builder);
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            FileLog.Error("Showing question toast failed", ex);
        }
    }

    public void UpdateCountdownProgress(string sessionId, double fraction) =>
        PostProgressUpdate(sessionId, "Auto-playing summary…", fraction);

    public void UpdatePlaybackStatus(string sessionId, string status, double fraction) =>
        PostProgressUpdate(sessionId, status, fraction);

    public bool IsToastLive(string sessionId)
    {
        lock (_lock) return _liveTags.Contains(sessionId);
    }

    public void RemoveNotification(string sessionId)
    {
        try
        {
            lock (_lock)
            {
                _progressSequences.Remove(sessionId);
                _liveTags.Remove(sessionId);
            }
            _ = AppNotificationManager.Default.RemoveByTagAsync(sessionId);
        }
        catch (Exception ex)
        {
            FileLog.Error($"Removing notification failed for {sessionId}", ex);
        }
    }

    /// <summary>Best-effort cleanup of toasts we still consider live (app shutdown).</summary>
    public void RemoveLiveNotifications()
    {
        List<string> tags;
        lock (_lock) tags = [.. _liveTags];
        foreach (var tag in tags)
            RemoveNotification(tag);
    }

    public void ShowError(string message)
    {
        try
        {
            var notification = new AppNotificationBuilder()
                .AddText("RAIVEN")
                .AddText(message)
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            FileLog.Error("Showing error toast failed", ex);
        }
    }

    private void PostProgressUpdate(string sessionId, string status, double fraction)
    {
        try
        {
            uint sequence;
            lock (_lock)
            {
                if (!_liveTags.Contains(sessionId))
                    return;
                sequence = _progressSequences.TryGetValue(sessionId, out var current) ? current + 1 : 2;
                _progressSequences[sessionId] = sequence;
            }

            var data = new AppNotificationProgressData(sequence)
            {
                Value = Math.Clamp(fraction, 0, 1),
                Status = status,
            };
            _ = ApplyUpdateAsync(sessionId, data);
        }
        catch (Exception ex)
        {
            FileLog.Error($"Progress update failed for {sessionId}", ex);
        }
    }

    private async Task ApplyUpdateAsync(string sessionId, AppNotificationProgressData data)
    {
        try
        {
            var result = await AppNotificationManager.Default.UpdateAsync(data, sessionId);
            if (result == AppNotificationProgressResult.AppNotificationNotFound)
            {
                // User swiped the toast away: treat as Hide - stop updating, never resurrect.
                lock (_lock) _liveTags.Remove(sessionId);
            }
        }
        catch (Exception ex)
        {
            FileLog.Error($"Progress update failed for {sessionId}", ex);
        }
    }

    private static void AddHeadline(AppNotificationBuilder builder, string folderName, string? headline)
    {
        if (headline is not null)
            builder.AddText(headline);
        builder.AddText($"Claude finished in {folderName}");
    }

    private static void TrySetLogo(AppNotificationBuilder builder)
    {
        try
        {
            var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "raiven-logo.png");
            if (File.Exists(logoPath))
                builder.SetAppLogoOverride(new Uri(logoPath));
        }
        catch (Exception ex)
        {
            FileLog.Error("Setting toast logo failed", ex);
        }
    }
}
