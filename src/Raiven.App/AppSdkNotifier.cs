using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Raiven.Core.Logging;
using Raiven.Core.Notifications;

namespace Raiven.App;

public sealed class AppSdkNotifier : INotifier
{
    public event Action<string>? PlaySummaryRequested;

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
            if (args.Arguments.TryGetValue("action", out var action) && action == "playSummary" &&
                args.Arguments.TryGetValue("sessionId", out var sessionId))
            {
                FileLog.Info($"Toast activated: playSummary for {sessionId}");
                PlaySummaryRequested?.Invoke(sessionId);
            }
        }
        catch (Exception ex)
        {
            FileLog.Error("Toast activation handling failed", ex);
        }
    }

    public void ShowFinished(string sessionId, string folderName)
    {
        var notification = new AppNotificationBuilder()
            .AddArgument("action", "playSummary")
            .AddArgument("sessionId", sessionId)
            .AddText($"Claude finished in {folderName}")
            .AddText("Click to hear a summary.")
            .AddButton(new AppNotificationButton("Play summary")
                .AddArgument("action", "playSummary")
                .AddArgument("sessionId", sessionId))
            .SetDuration(AppNotificationDuration.Long)
            .BuildNotification();

        AppNotificationManager.Default.Show(notification);
    }

    public void ShowError(string message)
    {
        var notification = new AppNotificationBuilder()
            .AddText("RAIVEN")
            .AddText(message)
            .BuildNotification();

        AppNotificationManager.Default.Show(notification);
    }
}
