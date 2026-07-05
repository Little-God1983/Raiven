using Raiven.Core.Config;
using Raiven.Core.Notifications;
using Raiven.Core.Sessions;
using Raiven.Core.Summaries;
using Raiven.Core.Voice;

namespace Raiven.Core.Tests;

public class SummaryPipelineTests
{
    private sealed class FakeNotifier : INotifier
    {
        public event Action<string>? PlaySummaryRequested;
        public event Action<string>? AbortRequested;
        public event Action<string>? StopRequested;
        public event Action<string>? HideRequested;
        public List<string> Errors { get; } = [];
        public List<(string SessionId, string Folder)> Finished { get; } = [];
        public List<(string Folder, string? Headline, string Message)> Questions { get; } = [];
        public List<(string SessionId, string Folder, string? Headline)> StatusShown { get; } = [];
        public List<(string SessionId, string Status, double Fraction)> StatusUpdates { get; } = [];
        public List<string> Removed { get; } = [];
        public bool ToastLive { get; set; }
        public void ShowFinished(string sessionId, string folderName, string? headline) => Finished.Add((sessionId, folderName));
        public void ShowFinishedCountdown(string sessionId, string folderName, string? headline, int totalSeconds) => Finished.Add((sessionId, folderName));
        public void ShowQuestion(string folderName, string? headline, string message) => Questions.Add((folderName, headline, message));
        public void ShowError(string message) => Errors.Add(message);
        public void UpdateCountdownProgress(string sessionId, double fraction) { }
        public void ShowPlaybackStatus(string sessionId, string folderName, string? headline)
        {
            StatusShown.Add((sessionId, folderName, headline));
            ToastLive = true;
        }
        public void UpdatePlaybackStatus(string sessionId, string status, double fraction) => StatusUpdates.Add((sessionId, status, fraction));
        public bool IsToastLive(string sessionId) => ToastLive;
        public void RemoveNotification(string sessionId)
        {
            Removed.Add(sessionId);
            ToastLive = false;
        }
        public void RaisePlaySummary(string sessionId) => PlaySummaryRequested?.Invoke(sessionId);
        public void RaiseAbort(string sessionId) => AbortRequested?.Invoke(sessionId);
        public void RaiseStop(string sessionId) => StopRequested?.Invoke(sessionId);
        public void RaiseHide(string sessionId) => HideRequested?.Invoke(sessionId);
    }

    private sealed class FakeVoice : IVoice
    {
        public List<string> Spoken { get; } = [];
        public List<VoicePhase> PhasesToEmit { get; set; } = [VoicePhase.Generating, VoicePhase.Speaking];
        public Task? Blocker;
        public TaskCompletionSource SpeakEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Stopped;
        public async Task SpeakAsync(string text, Action<VoicePhase>? onPhase = null)
        {
            foreach (var phase in PhasesToEmit)
                onPhase?.Invoke(phase);
            Spoken.Add(text);
            SpeakEntered.TrySetResult();
            if (Blocker is not null)
                await Blocker;
        }
        public void Stop() => Stopped = true;
    }

    private sealed class FakeClaudeClient : IClaudeClient
    {
        public string Response = "I fixed the login bug.";
        public Exception? Throws;
        public int Calls;
        public Task? Blocker;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> CompleteAsync(string systemPrompt, string userContent, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            if (Blocker is not null) await Blocker;
            if (Throws is not null) throw Throws;
            return Response;
        }
    }

    private static SummaryHistory NewHistory() =>
        SummaryHistory.Load(Path.Combine(Path.GetTempPath(), $"raiven-hist-{Guid.NewGuid():N}", "history.json"));

    private static string WriteTranscript()
    {
        var path = Path.Combine(Path.GetTempPath(), $"raiven-pipeline-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path,
        [
            """{"type":"user","message":{"role":"user","content":"Fix the login bug"},"sessionId":"s1"}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Fixed it. Tests pass."}]},"sessionId":"s1"}""",
        ]);
        return path;
    }

    [Fact]
    public async Task PlaySummaryAsync_UnknownSession_ShowsInfoErrorAndDoesNotSpeak()
    {
        var notifier = new FakeNotifier();
        var voice = new FakeVoice();
        var pipeline = new SummaryPipeline(
            new SessionRegistry(TimeSpan.FromHours(4)), new RaivenConfig(), new FakeClaudeClient(), notifier, voice, NewHistory());

        await pipeline.PlaySummaryAsync("nope");

        Assert.Contains(notifier.Errors, e => e.Contains("no longer available"));
        Assert.Empty(voice.Spoken);
    }

    [Fact]
    public async Task PlaySummaryAsync_HappyPath_SpeaksSummary()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var notifier = new FakeNotifier();
        var voice = new FakeVoice();
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), new FakeClaudeClient(), notifier, voice, NewHistory());

        await pipeline.PlaySummaryAsync("s1");

        Assert.Equal(["I fixed the login bug."], voice.Spoken);
        Assert.Empty(notifier.Errors);
    }

    [Fact]
    public async Task PlaySummaryAsync_ClaudeFails_ShowsErrorAndDoesNotSpeak()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var notifier = new FakeNotifier();
        var voice = new FakeVoice();
        var claude = new FakeClaudeClient { Throws = new InvalidOperationException("boom") };
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), claude, notifier, voice, NewHistory());

        await pipeline.PlaySummaryAsync("s1");

        Assert.Contains(notifier.Errors, e => e.Contains("Couldn't get the summary"));
        Assert.Empty(voice.Spoken);
    }

    [Fact]
    public async Task PlaySummaryAsync_MissingTranscript_ShowsError()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", Path.Combine(Path.GetTempPath(), "raiven-does-not-exist.jsonl"), "c", DateTimeOffset.Now);
        var notifier = new FakeNotifier();
        var voice = new FakeVoice();
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), new FakeClaudeClient(), notifier, voice, NewHistory());

        await pipeline.PlaySummaryAsync("s1");

        Assert.Contains(notifier.Errors, e => e.Contains("Couldn't get the summary"));
        Assert.Empty(voice.Spoken);
    }

    [Fact]
    public async Task PlaySummaryAsync_SecondCallSameTranscript_UsesCacheAndSkipsClaude()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var claude = new FakeClaudeClient();
        var voice = new FakeVoice();
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), claude, new FakeNotifier(), voice, NewHistory());

        await pipeline.PlaySummaryAsync("s1");
        await pipeline.PlaySummaryAsync("s1");

        Assert.Equal(1, claude.Calls);
        Assert.Equal(["I fixed the login bug.", "I fixed the login bug."], voice.Spoken);
    }

    [Fact]
    public async Task PlaySummaryAsync_RecordsHistoryEntryWithHeadline()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var history = NewHistory();
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), new FakeClaudeClient(), new FakeNotifier(), new FakeVoice(), history);

        await pipeline.PlaySummaryAsync("s1");

        var entry = Assert.Single(history.Entries);
        Assert.Equal("s1", entry.SessionId);
        Assert.Equal("Fix the login bug", entry.Headline);
        Assert.Equal("RAIVEN", entry.Folder);
        Assert.Equal("I fixed the login bug.", entry.SummaryText);
    }

    [Fact]
    public async Task PlaySummaryAsync_ChangedTranscript_RegeneratesInsteadOfCaching()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        var path = WriteTranscript();
        registry.Upsert("s1", path, @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var claude = new FakeClaudeClient();
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), claude, new FakeNotifier(), new FakeVoice(), NewHistory());

        await pipeline.PlaySummaryAsync("s1");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1)); // simulate a new turn
        await pipeline.PlaySummaryAsync("s1");

        Assert.Equal(2, claude.Calls);
    }

    [Fact]
    public async Task PlaySummaryAsync_ClaudeFails_RecordsNothing()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var history = NewHistory();
        var claude = new FakeClaudeClient { Throws = new InvalidOperationException("boom") };
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), claude, new FakeNotifier(), new FakeVoice(), history);

        await pipeline.PlaySummaryAsync("s1");

        Assert.Empty(history.Entries);
    }

    [Fact]
    public async Task PlaySummaryAsync_MessageMode_SpeaksLastMessageWithoutClaude()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var claude = new FakeClaudeClient();
        var voice = new FakeVoice();
        var config = new RaivenConfig { FinishedTurnVoice = "message" };
        var pipeline = new SummaryPipeline(registry, config, claude, new FakeNotifier(), voice, NewHistory());

        await pipeline.PlaySummaryAsync("s1");

        Assert.Equal(["Fixed it. Tests pass."], voice.Spoken);
        Assert.Equal(0, claude.Calls);
    }

    [Fact]
    public async Task PlaySummaryAsync_MessageMode_AppliesWordLimitAndCaches()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var voice = new FakeVoice();
        var history = NewHistory();
        var config = new RaivenConfig { FinishedTurnVoice = "message", FinishedTurnWordLimit = 2 };
        var pipeline = new SummaryPipeline(registry, config, new FakeClaudeClient(), new FakeNotifier(), voice, history);

        await pipeline.PlaySummaryAsync("s1");
        await pipeline.PlaySummaryAsync("s1"); // second call must replay from cache

        Assert.Equal(["Fixed it.", "Fixed it."], voice.Spoken);
        var entry = Assert.Single(history.Entries);
        Assert.Equal("Fixed it.", entry.SummaryText);
    }

    [Fact]
    public async Task PlaySummaryAsync_MessageMode_EmptyAssistantText_SpeaksFallback()
    {
        var path = Path.Combine(Path.GetTempPath(), $"raiven-pipeline-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path,
        [
            """{"type":"user","message":{"role":"user","content":"Do the thing"},"sessionId":"s1"}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","name":"Bash"}]},"sessionId":"s1"}""",
        ]);
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", path, @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var voice = new FakeVoice();
        var config = new RaivenConfig { FinishedTurnVoice = "message" };
        var pipeline = new SummaryPipeline(registry, config, new FakeClaudeClient(), new FakeNotifier(), voice, NewHistory());

        await pipeline.PlaySummaryAsync("s1");

        Assert.Equal(["Claude finished, but there was no message to read."], voice.Spoken);
    }

    [Fact]
    public async Task PlaySummaryAsync_SummaryMode_EmitsStagesInOrderAndRemovesToast()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var notifier = new FakeNotifier();
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), new FakeClaudeClient(), notifier, new FakeVoice(), NewHistory());

        await pipeline.PlaySummaryAsync("s1");

        Assert.Equal(["Summarizing with Haiku…", "Generating voice…", "Speaking…"],
            notifier.StatusUpdates.Select(u => u.Status));
        Assert.Equal([0.25, 0.65, 0.9], notifier.StatusUpdates.Select(u => u.Fraction));
        Assert.Contains("s1", notifier.Removed);
    }

    [Fact]
    public async Task PlaySummaryAsync_LoadingModelPhase_ReportsLoadingStatus()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var notifier = new FakeNotifier();
        var voice = new FakeVoice
        {
            PhasesToEmit = [VoicePhase.LoadingModel, VoicePhase.Generating, VoicePhase.Speaking],
        };
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), new FakeClaudeClient(), notifier, voice, NewHistory());

        await pipeline.PlaySummaryAsync("s1");

        Assert.Contains(notifier.StatusUpdates, u => u.Status == "Loading voice model…" && u.Fraction == 0.45);
    }

    [Fact]
    public async Task PlaySummaryAsync_MessageMode_SkipsHaikuStage()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var notifier = new FakeNotifier();
        var config = new RaivenConfig { FinishedTurnVoice = "message" };
        var pipeline = new SummaryPipeline(registry, config, new FakeClaudeClient(), notifier, new FakeVoice(), NewHistory());

        await pipeline.PlaySummaryAsync("s1");

        Assert.Equal(["Generating voice…", "Speaking…"], notifier.StatusUpdates.Select(u => u.Status));
    }

    [Fact]
    public async Task PlaySummaryAsync_CacheHit_SkipsHaikuStage()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var notifier = new FakeNotifier();
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), new FakeClaudeClient(), notifier, new FakeVoice(), NewHistory());

        await pipeline.PlaySummaryAsync("s1");
        notifier.StatusUpdates.Clear();
        await pipeline.PlaySummaryAsync("s1"); // cached now

        Assert.Equal(["Generating voice…", "Speaking…"], notifier.StatusUpdates.Select(u => u.Status));
    }

    [Fact]
    public async Task PlaySummaryAsync_StatusOff_MakesNoStatusCalls()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var notifier = new FakeNotifier();
        var config = new RaivenConfig { ShowPlaybackStatus = false };
        var voice = new FakeVoice();
        var pipeline = new SummaryPipeline(registry, config, new FakeClaudeClient(), notifier, voice, NewHistory());

        await pipeline.PlaySummaryAsync("s1");

        Assert.Empty(notifier.StatusShown);
        Assert.Empty(notifier.StatusUpdates);
        Assert.Empty(notifier.Removed);
        Assert.Single(voice.Spoken); // playback itself still happens
    }

    [Fact]
    public async Task PlaySummaryAsync_DeadToastUserInitiated_ShowsFreshStatusToast()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var notifier = new FakeNotifier { ToastLive = false };
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), new FakeClaudeClient(), notifier, new FakeVoice(), NewHistory());

        await pipeline.PlaySummaryAsync("s1", userInitiated: true);

        Assert.Single(notifier.StatusShown);
    }

    [Fact]
    public async Task PlaySummaryAsync_DeadToastContinuation_NeverResurrectsToast()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var notifier = new FakeNotifier { ToastLive = false };
        var voice = new FakeVoice();
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), new FakeClaudeClient(), notifier, voice, NewHistory());

        await pipeline.PlaySummaryAsync("s1", userInitiated: false);

        Assert.Empty(notifier.StatusShown);
        Assert.Single(voice.Spoken); // playback still happens, just without a toast
    }

    [Fact]
    public async Task PlaySummaryAsync_LiveToast_IsReusedNotReShown()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var notifier = new FakeNotifier { ToastLive = true };
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), new FakeClaudeClient(), notifier, new FakeVoice(), NewHistory());

        await pipeline.PlaySummaryAsync("s1", userInitiated: false);

        Assert.Empty(notifier.StatusShown);
        Assert.NotEmpty(notifier.StatusUpdates); // stages ride the existing toast
    }

    [Fact]
    public async Task PlaySummaryAsync_ClaudeFails_StillRemovesToast()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var notifier = new FakeNotifier();
        var claude = new FakeClaudeClient { Throws = new InvalidOperationException("boom") };
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), claude, notifier, new FakeVoice(), NewHistory());

        await pipeline.PlaySummaryAsync("s1");

        Assert.Contains("s1", notifier.Removed);
        Assert.Contains(notifier.Errors, e => e.Contains("Couldn't get the summary"));
    }

    [Fact]
    public async Task PlayCachedAsync_SpeaksEntryWithStagesAndNoClaudeCall()
    {
        var notifier = new FakeNotifier();
        var claude = new FakeClaudeClient();
        var voice = new FakeVoice();
        var pipeline = new SummaryPipeline(
            new SessionRegistry(TimeSpan.FromHours(4)), new RaivenConfig(), claude, notifier, voice, NewHistory());
        var entry = new SummaryHistoryEntry(
            "sX", "Fix login bug", "RAIVEN", DateTimeOffset.Now, @"C:\t.jsonl", DateTime.UtcNow, "cached text");

        await pipeline.PlayCachedAsync(entry);

        Assert.Equal(["cached text"], voice.Spoken);
        Assert.Equal(0, claude.Calls);
        Assert.Single(notifier.StatusShown);
        Assert.Equal(["Generating voice…", "Speaking…"], notifier.StatusUpdates.Select(u => u.Status));
        Assert.Contains("sX", notifier.Removed);
    }

    [Fact]
    public async Task PlaySummaryAsync_CannedSession_SpeaksTextWithoutRegistryClaudeOrHistory()
    {
        var claude = new FakeClaudeClient();
        var voice = new FakeVoice();
        var history = NewHistory();
        var pipeline = new SummaryPipeline(
            new SessionRegistry(TimeSpan.FromHours(4)), new RaivenConfig(), claude, new FakeNotifier(), voice, history);
        pipeline.RegisterCanned("test-session-001", "RAIVEN", "Test notification", "This is a summary test by RAIVEN.");

        await pipeline.PlaySummaryAsync("test-session-001");

        Assert.Equal(["This is a summary test by RAIVEN."], voice.Spoken);
        Assert.Equal(0, claude.Calls);      // no Claude call for canned text
        Assert.Empty(history.Entries);      // canned playback is never persisted to Recent summaries
    }

    [Fact]
    public async Task PlaySummaryAsync_CannedSession_ShowsAndRemovesStatusToast()
    {
        var notifier = new FakeNotifier();
        var pipeline = new SummaryPipeline(
            new SessionRegistry(TimeSpan.FromHours(4)), new RaivenConfig(), new FakeClaudeClient(), notifier, new FakeVoice(), NewHistory());
        pipeline.RegisterCanned("test-session-001", "RAIVEN", "Test notification", "This is a summary test by RAIVEN.");

        await pipeline.PlaySummaryAsync("test-session-001", userInitiated: true);

        Assert.Single(notifier.StatusShown);
        Assert.Empty(notifier.Errors); // never hits the "no longer available" path
        Assert.Contains("test-session-001", notifier.Removed);
    }

    [Fact]
    public async Task IsSpeaking_TrueWhileSpeechPending_FalseAfter()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var voice = new FakeVoice { Blocker = gate.Task };
        var pipeline = new SummaryPipeline(
            registry, new RaivenConfig(), new FakeClaudeClient(), new FakeNotifier(), voice, NewHistory());

        var play = pipeline.PlaySummaryAsync("s1");
        await voice.SpeakEntered.Task;

        Assert.True(pipeline.IsSpeaking("s1"));
        Assert.False(pipeline.IsSpeaking("other"));

        gate.SetResult();
        await play;

        Assert.False(pipeline.IsSpeaking("s1"));
    }

    [Fact]
    public async Task RequestStop_DuringSummarize_SkipsSpeechButKeepsHistoryAndRemovesToast()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claude = new FakeClaudeClient { Blocker = gate.Task };
        var notifier = new FakeNotifier();
        var voice = new FakeVoice();
        var history = NewHistory();
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), claude, notifier, voice, history);

        var play = pipeline.PlaySummaryAsync("s1");
        await claude.Entered.Task;
        pipeline.RequestStop("s1");
        gate.SetResult();
        await play;

        Assert.Empty(voice.Spoken);
        Assert.Single(history.Entries);           // the generated summary is still cached for replay
        Assert.Contains("s1", notifier.Removed);  // the toast never outlives the run
    }

    [Fact]
    public async Task RequestStop_StaleMark_DoesNotAffectNextRun()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var voice = new FakeVoice();
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), new FakeClaudeClient(), new FakeNotifier(), voice, NewHistory());

        pipeline.RequestStop("s1"); // stale: no run in flight
        await pipeline.PlaySummaryAsync("s1");

        Assert.Single(voice.Spoken);
    }

    [Fact]
    public async Task ObsoleteRun_InFlightRun_NeitherSpeaksNorRemovesToast()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claude = new FakeClaudeClient { Blocker = gate.Task };
        var notifier = new FakeNotifier();
        var voice = new FakeVoice();
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), claude, notifier, voice, NewHistory());

        var play = pipeline.PlaySummaryAsync("s1");
        await claude.Entered.Task;
        pipeline.ObsoleteRun("s1"); // a new finished-turn toast arrived for this session
        gate.SetResult();
        await play;

        Assert.Empty(voice.Spoken);                    // the obsolete run never speaks
        Assert.DoesNotContain("s1", notifier.Removed); // and leaves the new toast alone
    }

    [Fact]
    public async Task PlaySummaryAsync_DuplicateTriggerDuringGeneration_IsDropped()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claude = new FakeClaudeClient { Blocker = gate.Task };
        var voice = new FakeVoice();
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), claude, new FakeNotifier(), voice, NewHistory());

        var first = pipeline.PlaySummaryAsync("s1");
        await claude.Entered.Task;
        await pipeline.PlaySummaryAsync("s1"); // duplicate while the first is still summarizing
        gate.SetResult();
        await first;

        Assert.Equal(1, claude.Calls);
        Assert.Single(voice.Spoken);
    }

    [Fact]
    public async Task PlaySummaryAsync_TriggerDuringSpeech_RunsInsteadOfBeingDropped()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var voice = new FakeVoice { Blocker = gate.Task };
        var claude = new FakeClaudeClient();
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), claude, new FakeNotifier(), voice, NewHistory());

        var first = pipeline.PlaySummaryAsync("s1");
        await voice.SpeakEntered.Task; // first run is speaking; generation slot already released
        var second = pipeline.PlaySummaryAsync("s1"); // a trigger during speech must not be dropped
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(2, voice.Spoken.Count); // replayed from cache instead of dropped
        Assert.Equal(1, claude.Calls);
    }
}
