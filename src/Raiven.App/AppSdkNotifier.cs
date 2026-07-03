using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Raiven.Core.Logging;
using Raiven.Core.Notifications;

namespace Raiven.App;

public sealed class AppSdkNotifier : INotifier
{
    public event Action<string>? PlaySummaryRequested;
    public event Action<string>? AbortRequested;

    private readonly Dictionary<string, uint> _progressSequences = [];
    private readonly Lock _sequenceLock = new();

    public AppSdkNotifier()
    {
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
            }
        }
        catch (Exception ex)
        {
            FileLog.Error("Toast activation handling failed", ex);
        }
    }

    public void ShowFinished(string sessionId, string folderName, string? headline)
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
        AppNotificationManager.Default.Show(notification);
    }

    public void ShowFinishedCountdown(string sessionId, string folderName, string? headline, int totalSeconds)
    {
        var builder = new AppNotificationBuilder()
            .AddArgument("action", "playNow")
            .AddArgument("sessionId", sessionId);
        AddHeadline(builder, folderName, headline);
        builder
            .AddProgressBar(new AppNotificationProgressBar()
                .BindValue()
                .BindStatus())
            .AddButton(new AppNotificationButton("Play now")
                .AddArgument("action", "playNow")
                .AddArgument("sessionId", sessionId))
            .AddButton(new AppNotificationButton("Abort")
                .AddArgument("action", "abort")
                .AddArgument("sessionId", sessionId))
            .SetDuration(AppNotificationDuration.Long);
        TrySetLogo(builder);

        var notification = builder.BuildNotification();
        notification.Tag = sessionId;
        notification.Progress = new AppNotificationProgressData(sequenceNumber: 1)
        {
            Value = 0,
            Status = $"Auto-playing in {totalSeconds}s…",
        };
        lock (_sequenceLock) _progressSequences[sessionId] = 1;
        AppNotificationManager.Default.Show(notification);
    }

    public void UpdateCountdownProgress(string sessionId, double fraction)
    {
        try
        {
            uint sequence;
            lock (_sequenceLock)
            {
                sequence = _progressSequences.TryGetValue(sessionId, out var current) ? current + 1 : 2;
                _progressSequences[sessionId] = sequence;
            }

            var data = new AppNotificationProgressData(sequence)
            {
                Value = Math.Clamp(fraction, 0, 1),
                Status = "Auto-playing summary…",
            };
            _ = AppNotificationManager.Default.UpdateAsync(data, sessionId);
        }
        catch (Exception ex)
        {
            FileLog.Error($"Countdown progress update failed for {sessionId}", ex);
        }
    }

    public void RemoveNotification(string sessionId)
    {
        try
        {
            lock (_sequenceLock) _progressSequences.Remove(sessionId);
            _ = AppNotificationManager.Default.RemoveByTagAsync(sessionId);
        }
        catch (Exception ex)
        {
            FileLog.Error($"Removing notification failed for {sessionId}", ex);
        }
    }

    public void ShowError(string message)
    {
        var notification = new AppNotificationBuilder()
            .AddText("RAIVEN")
            .AddText(message)
            .BuildNotification();

        AppNotificationManager.Default.Show(notification);
    }

    private static void AddHeadline(AppNotificationBuilder builder, string folderName, string? headline)
    {
        if (headline is not null)
        {
            builder.AddText(headline);
            builder.AddText($"Claude finished in {folderName}");
        }
        else
        {
            builder.AddText($"Claude finished in {folderName}");
        }
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
