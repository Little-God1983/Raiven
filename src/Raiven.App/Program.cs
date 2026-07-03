using Raiven.Core.Config;
using Raiven.Core.Events;
using Raiven.Core.Http;
using Raiven.Core.Logging;
using Raiven.Core.Sessions;
using Raiven.Core.Summaries;

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
        if (args.Contains("--test-voice"))
        {
            RunVoiceTest(config);
            return;
        }

        using var mutex = new Mutex(initiallyOwned: true, "RAIVEN_SINGLE_INSTANCE", out var isPrimary);
        if (!isPrimary)
        {
            // A toast click can relaunch the exe when we're already running; the
            // primary instance receives the activation, so this one just exits.
            FileLog.Info("Another RAIVEN instance is running; exiting.");
            return;
        }

        if (args.Any(a => a.Contains("-ToastActivated")))
        {
            // Clicked a toast from a previous run; nothing to summarize anymore.
            new ToastService().ShowError("RAIVEN wasn't running - that session's summary is no longer available.");
            Thread.Sleep(TimeSpan.FromSeconds(3));
            return;
        }

        RunTray(config);
    }

    private static void RunTray(RaivenConfig config)
    {
        ApplicationConfiguration.Initialize();

        var state = new AppState();
        var registry = new SessionRegistry(TimeSpan.FromMinutes(config.SessionExpiryMinutes));
        var toasts = new ToastService();
        var voice = new VoiceService(config);

        Raiven.Core.Summaries.IClaudeClient claude = config.SummaryBackend.Equals("api", StringComparison.OrdinalIgnoreCase)
            ? new Raiven.Core.Summaries.AnthropicClaudeClient(config.Model)
            : new Raiven.Core.Summaries.ClaudeCliClient(config.CliModelAlias);
        var pipeline = new SummaryPipeline(registry, config, claude, toasts, voice);
        toasts.PlaySummaryRequested += id => _ = Task.Run(() => pipeline.PlaySummaryAsync(id));

        Task HandleEvent(RaivenEvent evt)
        {
            if (EventParser.TryParseClaudeStop(evt, out var stop))
            {
                registry.Upsert(stop.SessionId, stop.TranscriptPath, stop.Cwd, DateTimeOffset.Now);
                FileLog.Info($"Stop event for session {stop.SessionId} in {stop.Cwd}");
                if (!state.Paused)
                {
                    ChimePlayer.Play(config);
                    var folder = Path.GetFileName(stop.Cwd.TrimEnd('\\', '/'));
                    toasts.ShowFinished(stop.SessionId, folder.Length > 0 ? folder : stop.Cwd);
                }
            }
            else
            {
                FileLog.Info($"Ignoring event {evt.Source}/{evt.Type}");
            }
            return Task.CompletedTask;
        }

        var listener = new HttpEventListener(config.Port, HandleEvent);
        try
        {
            listener.Start();
            FileLog.Info($"RAIVEN listening on http://127.0.0.1:{config.Port}/");
        }
        catch (Exception ex)
        {
            FileLog.Error($"Could not start listener on port {config.Port}", ex);
            toasts.ShowError($"RAIVEN couldn't listen on port {config.Port}. Is another instance or app using it?");
            return;
        }

        try
        {
            Application.Run(new TrayContext(
                state,
                testToast: () => { ChimePlayer.Play(config); toasts.ShowFinished("test-session-001", "RAIVEN"); },
                testVoice: () => Task.Run(() => voice.Speak("RAIVEN online. All systems operational."))));
        }
        finally
        {
            listener.DisposeAsync().AsTask().GetAwaiter().GetResult();
            FileLog.Info("RAIVEN stopped.");
        }
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

    private static void RunVoiceTest(RaivenConfig config)
    {
        var voice = new VoiceService(config);
        FileLog.Info("Speaking test phrase...");
        voice.Speak("RAIVEN online. All systems operational.");
        Thread.Sleep(TimeSpan.FromSeconds(20)); // keep process alive while audio plays
    }
}
