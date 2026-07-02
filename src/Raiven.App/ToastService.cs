using Microsoft.Toolkit.Uwp.Notifications;
using Raiven.Core.Logging;

namespace Raiven.App;

public sealed class ToastService
{
    public event Action<string>? PlaySummaryRequested;

    public ToastService()
    {
        ToastNotificationManagerCompat.OnActivated += toastArgs =>
        {
            try
            {
                var args = ToastArguments.Parse(toastArgs.Argument);
                if (args.TryGetValue("action", out var action) && action == "playSummary" &&
                    args.TryGetValue("sessionId", out var sessionId))
                {
                    FileLog.Info($"Toast activated: playSummary for {sessionId}");
                    PlaySummaryRequested?.Invoke(sessionId);
                }
            }
            catch (Exception ex)
            {
                FileLog.Error("Toast activation handling failed", ex);
            }
        };
    }

    public void ShowFinished(string sessionId, string folderName)
    {
        new ToastContentBuilder()
            .AddArgument("action", "playSummary")
            .AddArgument("sessionId", sessionId)
            .AddText($"Claude finished in {folderName}")
            .AddText("Click to hear a summary.")
            .AddButton(new ToastButton()
                .SetContent("Play summary")
                .AddArgument("action", "playSummary")
                .AddArgument("sessionId", sessionId))
            .Show();
    }

    public void ShowError(string message)
    {
        new ToastContentBuilder()
            .AddText("RAIVEN")
            .AddText(message)
            .Show();
    }
}
