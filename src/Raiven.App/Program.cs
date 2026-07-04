using Raiven.Core.Config;
using Raiven.Core.Events;
using Raiven.Core.Http;
using Raiven.Core.Logging;
using Raiven.Core.Notifications;
using Raiven.Core.Questions;
using Raiven.Core.Sessions;
using Raiven.Core.Summaries;
using Raiven.Core.Transcripts;

namespace Raiven.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        FileLog.Configure(AppPaths.LogFile);
        var config = RaivenConfig.LoadOrCreate(AppPaths.ConfigFile);

        try
        {
            Run(args, config);
        }
        catch (Exception ex)
        {
            FileLog.Error("RAIVEN failed to start", ex);
            MessageBox.Show(
                $"RAIVEN failed to start:\n\n{ex.Message}\n\nIf this mentions a COM or class-not-registered error, repair the Windows App Runtime:\nwinget install --id Microsoft.WindowsAppRuntime.2.2 --force\n\nDetails are in the log: {AppPaths.LogFile}",
                "RAIVEN",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void Run(string[] args, RaivenConfig config)
    {
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

        var activatedArgs = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activatedArgs.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.AppNotification)
        {
            // Toast clicked from a previous run; the in-memory session registry is gone.
            if (activatedArgs.Data is Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs toast &&
                toast.Arguments.TryGetValue("action", out var act) && act == "abort")
            {
                FileLog.Info("Stale toast aborted; nothing to do.");
                return;
            }
            new AppSdkNotifier().ShowError("RAIVEN wasn't running - that session's summary is no longer available.");
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
        var notifier = new AppSdkNotifier();
        var voice = new VoiceService(config);

        Raiven.Core.Summaries.IClaudeClient claude = config.SummaryBackend.Equals("api", StringComparison.OrdinalIgnoreCase)
            ? new Raiven.Core.Summaries.AnthropicClaudeClient(config.Model)
            : new Raiven.Core.Summaries.ClaudeCliClient(config.CliModelAlias);
        var history = Raiven.Core.Summaries.SummaryHistory.Load(AppPaths.HistoryFile);
        var pipeline = new SummaryPipeline(registry, config, claude, notifier, voice, history);
        var questions = new QuestionPipeline(config, claude, voice);

        var delaySeconds = Math.Max(1, config.AutoPlayDelaySeconds);
        using var countdown = new AutoPlayCountdown(
            TimeSpan.FromSeconds(delaySeconds),
            TimeSpan.FromMilliseconds(500));

        // Guards against a "Play now" click and the expiry timer firing near-simultaneously
        // (before the toast disappears), which would otherwise launch two Claude calls and
        // speak the summary twice. Stale Action Center clicks with no countdown running still
        // play from cache — this only dedupes concurrent triggers for the same session.
        var playing = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        async Task PlayOnce(string id)
        {
            if (!playing.TryAdd(id, 0))
            {
                FileLog.Info($"Summary already in flight for {id}; ignoring duplicate trigger.");
                return;
            }
            try { await pipeline.PlaySummaryAsync(id); }
            finally { playing.TryRemove(id, out _); }
        }

        countdown.Progress += (id, fraction) => notifier.UpdateCountdownProgress(id, fraction);
        countdown.Expired += id =>
        {
            if (state.Paused) return;
            notifier.RemoveNotification(id);
            _ = Task.Run(() => PlayOnce(id));
        };
        notifier.PlaySummaryRequested += id =>
        {
            countdown.Cancel(id);
            notifier.RemoveNotification(id);
            _ = Task.Run(() => PlayOnce(id));
        };
        notifier.AbortRequested += id =>
        {
            countdown.Cancel(id);
            notifier.RemoveNotification(id);
            FileLog.Info($"Auto-play aborted for {id}");
        };

        Task HandleEvent(RaivenEvent evt)
        {
            if (EventParser.TryParseClaudeStop(evt, out var stop))
            {
                registry.Upsert(stop.SessionId, stop.TranscriptPath, stop.Cwd, DateTimeOffset.Now);
                FileLog.Info($"Stop event for session {stop.SessionId} in {stop.Cwd}");
                if (!state.Paused && config.NotifyOnFinishedTurn)
                {
                    ChimePlayer.Play(config);
                    var folder = Path.GetFileName(stop.Cwd.TrimEnd('\\', '/'));
                    var folderName = folder.Length > 0 ? folder : stop.Cwd;
                    string? headline = null;
                    try { headline = TranscriptReader.ReadFirstPrompt(stop.TranscriptPath); }
                    catch (Exception ex) { FileLog.Error("Could not read chat headline", ex); }

                    if (config.AutoPlaySummary)
                    {
                        // Cancel any prior countdown for this session first so a restarted
                        // session's stale timer can't tick/expire onto the fresh toast.
                        countdown.Cancel(stop.SessionId);
                        notifier.ShowFinishedCountdown(stop.SessionId, folderName, headline, delaySeconds);
                        countdown.Start(stop.SessionId);
                    }
                    else
                    {
                        notifier.ShowFinished(stop.SessionId, folderName, headline);
                    }
                }
            }
            else if (EventParser.TryParseClaudeNotification(evt, out var question))
            {
                FileLog.Info($"Notification event for session {question.SessionId}: [{question.NotificationType ?? "unknown"}] {question.Message}");
                if (QuestionPipeline.IsQuestion(question.NotificationType) && config.NotifyOnQuestion && !state.Paused)
                {
                    ChimePlayer.Play(config);
                    var folder = Path.GetFileName(question.Cwd.TrimEnd('\\', '/'));
                    var folderName = folder.Length > 0 ? folder : question.Cwd;
                    string? headline = null;
                    try { headline = TranscriptReader.ReadFirstPrompt(question.TranscriptPath); }
                    catch (Exception ex) { FileLog.Error("Could not read chat headline", ex); }
                    notifier.ShowQuestion(folderName, headline, question.Message);
                    _ = Task.Run(() => questions.AnnounceAsync(question));
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
            notifier.ShowError($"RAIVEN couldn't listen on port {config.Port}. Is another instance or app using it?");
            return;
        }

        try
        {
            Application.Run(new TrayContext(
                state,
                config,
                saveConfig: () => config.Save(AppPaths.ConfigFile),
                history,
                replaySummary: entry => Task.Run(() => voice.SpeakAsync(entry.SummaryText)),
                testToast: () => { ChimePlayer.Play(config); notifier.ShowFinished("test-session-001", "RAIVEN", "This is a test notification"); },
                testVoice: () => Task.Run(() => voice.SpeakAsync("RAIVEN online. All systems operational.")),
                onPauseChanged: paused => { if (paused) countdown.CancelAll(); }));
        }
        finally
        {
            listener.DisposeAsync().AsTask().GetAwaiter().GetResult();
            FileLog.Info("RAIVEN stopped.");
        }
    }

    private static void RunToastTest(RaivenConfig config)
    {
        var notifier = new AppSdkNotifier();
        notifier.PlaySummaryRequested += id => FileLog.Info($"TEST: play now requested for {id}");
        notifier.AbortRequested += id => FileLog.Info($"TEST: abort requested for {id}");
        ChimePlayer.Play(config);
        notifier.ShowFinishedCountdown("test-session-001", "RAIVEN", "Testing the countdown toast", 5);
        for (var step = 1; step <= 10; step++)
        {
            Thread.Sleep(500);
            notifier.UpdateCountdownProgress("test-session-001", step / 10.0);
        }
        FileLog.Info("Countdown complete; waiting 10s for clicks...");
        Thread.Sleep(TimeSpan.FromSeconds(10));
    }

    private static void RunVoiceTest(RaivenConfig config)
    {
        var voice = new VoiceService(config);
        FileLog.Info("Speaking test phrase...");
        voice.SpeakAsync("RAIVEN online. All systems operational.").GetAwaiter().GetResult();
    }
}
