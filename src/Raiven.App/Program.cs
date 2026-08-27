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
        FileLog.Configure(AppPaths.LogDir);
        FileLog.PruneOldLogs(keep: 30); // keep the newest 30 daily log files
        var config = RaivenConfig.LoadOrCreate(AppPaths.ConfigFile);

        try
        {
            Run(args, config);
        }
        catch (Exception ex)
        {
            FileLog.Error("RAIVEN failed to start", ex);
            MessageBox.Show(
                $"RAIVEN failed to start:\n\n{Raiven.Core.Diagnostics.StartupFailureMessage.Describe(ex)}\n\nDetails are in the logs folder: {AppPaths.LogDir}",
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
        // All speech funnels through the coordinator so overlapping notifications queue instead
        // of cutting each other off (#13); it drives the raw VoiceService one utterance at a time.
        var rawVoice = new VoiceService(config);
        using var voice = new Raiven.Core.Voice.SpeechCoordinator(
            rawVoice, new Raiven.Core.Voice.RandomTransitionNarrator());
        using var keepAlive = new AudioKeepAlive();
        if (config.KeepAudioAlive) keepAlive.Start();

        Raiven.Core.Summaries.IClaudeClient claude = config.SummaryBackend.Equals("api", StringComparison.OrdinalIgnoreCase)
            ? new Raiven.Core.Summaries.AnthropicClaudeClient(config.Model)
            : new Raiven.Core.Summaries.ClaudeCliClient(config.CliModelAlias);
        var history = Raiven.Core.Summaries.SummaryHistory.Load(
            AppPaths.HistoryFile, Math.Clamp(config.HistorySize, 1, 50));
        var pipeline = new SummaryPipeline(registry, config, claude, notifier, voice, history);
        var questions = new QuestionPipeline(config, claude, voice);
        var gate = new QuestionGate(config);

        // The auto-play delay is read per finished turn (see BeginFinishedPlayback) so a
        // tray change takes effect on the next turn; this default only backs Start() calls
        // that pass no explicit total, which none currently do.
        using var countdown = new AutoPlayCountdown(
            TimeSpan.FromSeconds(5),
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

        // Begin a finished-turn toast + playback for a session. Shared by real Stop events
        // and the Test notification. Reads the delay from config each time so a tray change
        // applies on the next turn: 0 = play immediately (no countdown/Abort window),
        // 1-60 = countdown then auto-play, or (AutoPlaySummary off) a click-to-play toast.
        void BeginFinishedPlayback(string sessionId, string folderName, string? headline)
        {
            pipeline.ObsoleteRun(sessionId);
            if (!config.AutoPlaySummary)
            {
                notifier.ShowFinished(sessionId, folderName, headline);
                return;
            }

            // Cancel any prior countdown first so a restarted session's stale timer can't
            // tick/expire onto the fresh toast.
            countdown.Cancel(sessionId);
            var delay = Math.Clamp(config.AutoPlayDelaySeconds, 0, 60);
            if (delay <= 0)
            {
                // Immediate: show a toast so the finished turn is visible (the pipeline
                // reuses a live status toast for its stages), then play straight away.
                if (config.ShowPlaybackStatus)
                    notifier.ShowPlaybackStatus(sessionId, folderName, headline);
                else
                    notifier.ShowFinished(sessionId, folderName, headline);
                _ = Task.Run(() => pipeline.PlaySummaryAsync(sessionId, userInitiated: false));
            }
            else
            {
                notifier.ShowFinishedCountdown(sessionId, folderName, headline, delay);
                countdown.Start(sessionId, TimeSpan.FromSeconds(delay));
            }
        }

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

                    // This turn's toast supersedes any in-flight run for the session: the old
                    // run must not remove or scribble on the new toast, and its not-yet-started
                    // speech is obsolete (BeginFinishedPlayback calls ObsoleteRun). Audible
                    // speech keeps playing until the new turn's own playback preempts it.
                    BeginFinishedPlayback(stop.SessionId, folderName, headline);
                }
            }
            else if (EventParser.TryParseClaudeNotification(evt, out var question))
            {
                FileLog.Info($"Notification event for session {question.SessionId}: [{question.NotificationType ?? "unknown"}] {question.Message}");
                // Housekeeping filter plus the duplicate-AskUserQuestion check, which the gate only
                // applies to a dialog the PreToolUse branch below actually announced.
                if (gate.ShouldAnnounce(question, DateTimeOffset.Now))
                    AnnounceQuestion(question);
            }
            else if (EventParser.TryParseClaudeAskUserQuestion(evt, out var askQuestion))
            {
                // Only this hook carries the question text (the Notification copy is generic and
                // gets dropped above), so without this branch RAIVEN never reads the actual
                // question. Always a question - no ignore-list filter.
                FileLog.Info($"AskUserQuestion event for session {askQuestion.SessionId}: {askQuestion.Message}");
                gate.NoteAskUserQuestion(askQuestion.SessionId, DateTimeOffset.Now);
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

        // Reconstruct the complete history from the daily logs into one analyzable JSON file (#10).
        void ExportHistory(bool full)
        {
            try
            {
                var scope = full
                    ? Raiven.Core.Export.HistoryExporter.Scope.Full
                    : Raiven.Core.Export.HistoryExporter.Scope.Signal;
                var export = Raiven.Core.Export.HistoryExporter.ExportFromLogs(
                    AppPaths.LogDir, AppVersion.Display, DateTimeOffset.Now, scope);
                using var dialog = new SaveFileDialog
                {
                    Title = "Export RAIVEN history",
                    Filter = "JSON file (*.json)|*.json",
                    FileName = $"raiven-history-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                };
                if (dialog.ShowDialog() != DialogResult.OK) return;
                File.WriteAllText(dialog.FileName, Raiven.Core.Export.HistoryExporter.ToJson(export));
                FileLog.Info($"Exported history ({scope}) to {dialog.FileName}: {export.Summaries.Count} summaries, " +
                    $"{export.Errors.Count} errors, {export.Questions.Count} questions");
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\"");
            }
            catch (Exception ex)
            {
                FileLog.Error("History export failed", ex);
                notifier.ShowError("History export failed - check the RAIVEN log for details.");
            }
        }

        try
        {
            Application.Run(new TrayContext(
                state,
                config,
                saveConfig: () => config.Save(AppPaths.ConfigFile),
                history,
                replaySummary: entry => Task.Run(() => pipeline.PlayCachedAsync(entry)),
                testToast: () =>
                {
                    const string testId = "test-session-001";
                    pipeline.RegisterCanned(testId, "RAIVEN", "Test notification", "This is a summary test by RAIVEN.");
                    ChimePlayer.Play(config);
                    BeginFinishedPlayback(testId, "RAIVEN", "Test notification");
                },
                testVoice: () => Task.Run(() => voice.SpeakAsync("RAIVEN online. All systems operational.")),
                onPauseChanged: paused =>
                {
                    if (!paused) return;
                    foreach (var id in countdown.CancelAll())
                        notifier.RemoveNotification(id);
                },
                onKeepAudioAliveChanged: on => { if (on) keepAlive.Start(); else keepAlive.Stop(); },
                exportHistory: ExportHistory));
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
