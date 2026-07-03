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
        public List<string> Errors { get; } = [];
        public List<(string SessionId, string Folder)> Finished { get; } = [];
        public void ShowFinished(string sessionId, string folderName) => Finished.Add((sessionId, folderName));
        public void ShowError(string message) => Errors.Add(message);
        public void RaisePlaySummary(string sessionId) => PlaySummaryRequested?.Invoke(sessionId);
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
        public Task<string> CompleteAsync(string systemPrompt, string userContent, CancellationToken ct = default) =>
            Throws is null ? Task.FromResult(Response) : Task.FromException<string>(Throws);
    }

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
            new SessionRegistry(TimeSpan.FromHours(4)), new RaivenConfig(), new FakeClaudeClient(), notifier, voice);

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
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), new FakeClaudeClient(), notifier, voice);

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
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), claude, notifier, voice);

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
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), new FakeClaudeClient(), notifier, voice);

        await pipeline.PlaySummaryAsync("s1");

        Assert.Contains(notifier.Errors, e => e.Contains("Couldn't get the summary"));
        Assert.Empty(voice.Spoken);
    }
}
