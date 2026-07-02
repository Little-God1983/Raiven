using Raiven.Core.Config;
using Raiven.Core.Logging;

namespace Raiven.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        FileLog.Configure(AppPaths.LogFile);
        var config = RaivenConfig.LoadOrCreate(AppPaths.ConfigFile);

        if (args.Contains("--test-toast"))
        {
            RunToastTest(config);
            return;
        }

        // Tray mode arrives in a later task.
        FileLog.Info("Tray mode not implemented yet.");
    }

    private static void RunToastTest(RaivenConfig config)
    {
        var toasts = new ToastService();
        toasts.PlaySummaryRequested += id => FileLog.Info($"TEST: play summary requested for {id}");
        ChimePlayer.Play(config);
        toasts.ShowFinished("test-session-001", "RAIVEN");
        FileLog.Info("Test toast shown; waiting 15s for clicks...");
        Thread.Sleep(TimeSpan.FromSeconds(15));
    }
}