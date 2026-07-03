using Raiven.Core.Config;
using Raiven.Core.Events;
using Raiven.Core.Questions;
using Raiven.Core.Summaries;
using Raiven.Core.Voice;

namespace Raiven.Core.Tests;

public class QuestionPipelineTests
{
    private sealed class FakeVoice : IVoice
    {
        public List<string> Spoken { get; } = [];
        public void Speak(string text) => Spoken.Add(text);
    }

    private sealed class FakeClaudeClient : IClaudeClient
    {
        public string Response = "Claude wants permission to run the tests.";
        public Exception? Throws;
        public int Calls;
        public string? LastUserContent;
        public Task<string> CompleteAsync(string systemPrompt, string userContent, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            LastUserContent = userContent;
            return Throws is null ? Task.FromResult(Response) : Task.FromException<string>(Throws);
        }
    }

    private static ClaudeNotificationEvent Evt(string message = "Claude needs your permission to use Bash") =>
        new("s1", Path.Combine(Path.GetTempPath(), "raiven-does-not-exist.jsonl"), @"E:\Repos\RAIVEN", message, "permission_prompt");

    [Theory]
    [InlineData(null, true)]
    [InlineData("permission_prompt", true)]
    [InlineData("idle_prompt", true)]
    [InlineData("something_new", true)]
    [InlineData("auth_success", false)]
    [InlineData("AGENT_COMPLETED", false)]
    [InlineData("elicitation_complete", false)]
    [InlineData("elicitation_response", false)]
    public void IsQuestion_FiltersHousekeepingTypes(string? type, bool expected)
    {
        Assert.Equal(expected, QuestionPipeline.IsQuestion(type));
    }

    [Fact]
    public async Task AnnounceAsync_AnnounceMode_SpeaksFixedLineWithFolder()
    {
        var voice = new FakeVoice();
        var claude = new FakeClaudeClient();
        var pipeline = new QuestionPipeline(new RaivenConfig { QuestionVoice = "announce" }, claude, voice);

        await pipeline.AnnounceAsync(Evt());

        Assert.Equal(["Claude Code has a question in RAIVEN."], voice.Spoken);
        Assert.Equal(0, claude.Calls);
    }

    [Fact]
    public async Task AnnounceAsync_MessageMode_SpeaksWordLimitedMessage()
    {
        var voice = new FakeVoice();
        var config = new RaivenConfig { QuestionVoice = "message", QuestionWordLimit = 4 };
        var pipeline = new QuestionPipeline(config, new FakeClaudeClient(), voice);

        await pipeline.AnnounceAsync(Evt("Claude needs your permission to use Bash"));

        Assert.Equal(["Claude needs your permission"], voice.Spoken);
    }

    [Fact]
    public async Task AnnounceAsync_MessageMode_EmptyMessage_FallsBackToAnnounce()
    {
        var voice = new FakeVoice();
        var pipeline = new QuestionPipeline(new RaivenConfig { QuestionVoice = "message" }, new FakeClaudeClient(), voice);

        await pipeline.AnnounceAsync(Evt(""));

        Assert.Equal(["Claude Code has a question in RAIVEN."], voice.Spoken);
    }

    [Fact]
    public async Task AnnounceAsync_SummaryMode_SpeaksClaudeResponse()
    {
        var voice = new FakeVoice();
        var claude = new FakeClaudeClient();
        var pipeline = new QuestionPipeline(new RaivenConfig { QuestionVoice = "summary" }, claude, voice);

        await pipeline.AnnounceAsync(Evt());

        Assert.Equal(["Claude wants permission to run the tests."], voice.Spoken);
        Assert.Equal(1, claude.Calls);
    }

    [Fact]
    public async Task AnnounceAsync_SummaryMode_ClaudeFails_FallsBackToAnnounce()
    {
        var voice = new FakeVoice();
        var claude = new FakeClaudeClient { Throws = new InvalidOperationException("boom") };
        var pipeline = new QuestionPipeline(new RaivenConfig { QuestionVoice = "summary" }, claude, voice);

        await pipeline.AnnounceAsync(Evt());

        Assert.Equal(["Claude Code has a question in RAIVEN."], voice.Spoken);
    }

    [Fact]
    public async Task AnnounceAsync_UnknownMode_BehavesAsAnnounce()
    {
        var voice = new FakeVoice();
        var pipeline = new QuestionPipeline(new RaivenConfig { QuestionVoice = "yodel" }, new FakeClaudeClient(), voice);

        await pipeline.AnnounceAsync(Evt());

        Assert.Equal(["Claude Code has a question in RAIVEN."], voice.Spoken);
    }

    [Fact]
    public async Task AnnounceAsync_SummaryMode_SendsHookMessageToClaude()
    {
        var claude = new FakeClaudeClient();
        var pipeline = new QuestionPipeline(new RaivenConfig { QuestionVoice = "summary" }, claude, new FakeVoice());

        await pipeline.AnnounceAsync(Evt("Claude needs your permission to use Bash"));

        Assert.NotNull(claude.LastUserContent);
        Assert.Contains("Claude needs your permission to use Bash", claude.LastUserContent);
    }
}
