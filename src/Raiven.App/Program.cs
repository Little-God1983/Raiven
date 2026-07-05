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
            new AppSdkNotifier(config).ShowError("RAIVEN wasn't running - that session's summary is no longer available.");
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
        var notifier = new AppSdkNotifier(config);
        var voice = new VoiceService(config);
        using var keepAlive = new AudioKeepAlive();
        if (config.KeepAudioAlive) keepAlive.Start();

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

        countdown.Progress += (id, fraction) => notifier.UpdateCountdownProgress(id, fraction);
        countdown.Expired += id =>
        {
            if (state.Paused) return;
            if (!config.ShowPlaybackStatus) notifier.RemoveNotification(id);
            _ = Task.Run(() => pipeline.PlaySummaryAsync(id, userInitiated: false));
        };
        notifier.PlaySummaryRequested += id =>
        {
            countdown.Cancel(id);
            if (!config.ShowPlaybackStatus) notifier.RemoveNotification(id);
            _ = Task.Run(() => pipeline.PlaySummaryAsync(id, userInitiated: true));
        };
        notifier.AbortRequested += id =>
        {
            countdown.Cancel(id);
            notifier.RemoveNotification(id);
            FileLog.Info($"Auto-play aborted for {id}");
        };
        notifier.StopRequested += id =>
        {
            var hadCountdown = countdown.Cancel(id);
            notifier.RemoveNotification(id);
            // Countdown-phase Stop is an abort; playback-phase Stop halts the voice -
            // but only when THIS session is the one speaking, so stopping session B's
            // toast can never kill session A's speech. A Stop landing while the run is
            // still preparing (summarizing/generating) marks the session instead, so
            // the pipeline skips the speech when it gets there.
            if (!hadCountdown)
            {
                if (pipeline.IsSpeaking(id)) voice.Stop();
                else pipeline.RequestStop(id);
            }
            FileLog.Info($"Stop requested for {id} (countdown canceled: {hadCountdown})");
        };
        notifier.HideRequested += id =>
        {
            notifier.RemoveNotification(id);
            FileLog.Info($"Status toast hidden for {id}");
        };

        // Chime + toast + immediate voice for a question (permission prompt, idle, or an
        // AskUserQuestion dialog). Shared by the Notification and AskUserQuestion branches.
        void AnnounceQuestion(ClaudeNotificationEvent q)
        {
            if (!config.NotifyOnQuestion || state.Paused) return;
            ChimePlayer.Play(config);
            var folder = Path.GetFileName(q.Cwd.TrimEnd('\\', '/'));
            var folderName = folder.Length > 0 ? folder : q.Cwd;
            string? headline = null;
            try { headline = TranscriptReader.ReadFirstPrompt(q.TranscriptPath); }
            catch (Exception ex) { FileLog.Error("Could not read chat headline", ex); }
            notifier.ShowQuestion(folderName, headline, q.Message);
            _ = Task.Run(() => questions.AnnounceAsync(q));
        }

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

                    // This turn's toast supersedes any in-flight run for the session:
                    // the old run must not remove or scribble on the new toast, and its
                    // not-yet-started speech is obsolete. Audible speech keeps playing
                    // until the new turn's own playback preempts it.
                    pipeline.ObsoleteRun(stop.SessionId);
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
                if (QuestionPipeline.IsQuestion(question.NotificationType))
                    AnnounceQuestion(question);
            }
            else if (EventParser.TryParseClaudeAskUserQuestion(evt, out var askQuestion))
            {
                // AskUserQuestion dialogs fire no Notification/Stop hook, so without this
                // branch RAIVEN stays silent on them. Always a question - no ignore-list filter.
                FileLog.Info($"AskUserQuestion event for session {askQuestion.SessionId}: {askQuestion.Message}");
                AnnounceQuestion(askQuestion);
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
                replaySummary: entry => Task.Run(() => pipeline.PlayCachedAsync(entry)),
                testToast: () => { ChimePlayer.Play(config); notifier.ShowFinished("test-session-001", "RAIVEN", "This is a test notification"); },
                testVoice: () => Task.Run(() => voice.SpeakAsync("RAIVEN online. All systems operational.")),
                onPauseChanged: paused =>
                {
                    if (!paused) return;
                    foreach (var id in countdown.CancelAll())
                        notifier.RemoveNotification(id);
                },
                onKeepAudioAliveChanged: on => { if (on) keepAlive.Start(); else keepAlive.Stop(); }));
        }
        finally
        {
            notifier.RemoveLiveNotifications();
            listener.DisposeAsync().AsTask().GetAwaiter().GetResult();
            FileLog.Info("RAIVEN stopped.");
        }
    }

    private static void RunToastTest(RaivenConfig config)
    {
        var notifier = new AppSdkNotifier(config);
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
