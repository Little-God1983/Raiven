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
        public List<string> Errors { get; } = [];
        public List<(string SessionId, string Folder)> Finished { get; } = [];
        public void ShowFinished(string sessionId, string folderName, string? headline) => Finished.Add((sessionId, folderName));
        public void ShowFinishedCountdown(string sessionId, string folderName, string? headline, int totalSeconds) => Finished.Add((sessionId, folderName));
        public void ShowError(string message) => Errors.Add(message);
        public void UpdateCountdownProgress(string sessionId, double fraction) { }
        public void RemoveNotification(string sessionId) { }
        public void RaisePlaySummary(string sessionId) => PlaySummaryRequested?.Invoke(sessionId);
        public void RaiseAbort(string sessionId) => AbortRequested?.Invoke(sessionId);
    }

    private sealed class FakeVoice : IVoice
    {
        public List<string> Spoken { get; } = [];
        public void Speak(string text) => Spoken.Add(text);
    }

    private sealed class FakeClaudeClient : IClaudeClient
    {
        public string Response = "I fixed the login bug.";
        public Exception? Throws;
        public int Calls;
        public Task<string> CompleteAsync(string systemPrompt, string userContent, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Throws is null ? Task.FromResult(Response) : Task.FromException<string>(Throws);
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
}
