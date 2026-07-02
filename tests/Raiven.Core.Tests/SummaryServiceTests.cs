using Raiven.Core.Summaries;
using Raiven.Core.Transcripts;

namespace Raiven.Core.Tests;

public class SummaryServiceTests
{
    private sealed class FakeClaudeClient : IClaudeClient
    {
        public string? LastSystemPrompt;
        public string? LastUserContent;
        public string Response = "I fixed the login bug and all tests pass.";

        public Task<string> CompleteAsync(string systemPrompt, string userContent, CancellationToken ct = default)
        {
            LastSystemPrompt = systemPrompt;
            LastUserContent = userContent;
            return Task.FromResult(Response);
        }
    }

    private static readonly TurnSlice Slice = new(
        "Fix the login bug",
        "Fixed the null check. All tests pass.",
        ["Edit", "Bash"]);

    [Fact]
    public void BuildUserContent_IncludesPromptToolsAndTranscript()
    {
        var content = SummaryService.BuildUserContent(Slice);

        Assert.Contains("Fix the login bug", content);
        Assert.Contains("Edit, Bash", content);
        Assert.Contains("Fixed the null check. All tests pass.", content);
    }

    [Fact]
    public void BuildUserContent_NoTools_SaysNone()
    {
        var content = SummaryService.BuildUserContent(Slice with { ToolsUsed = [] });

        Assert.Contains("none", content);
    }

    [Fact]
    public async Task SummarizeAsync_SendsSystemPromptAndReturnsResponse()
    {
        var fake = new FakeClaudeClient();
        var service = new SummaryService(fake);

        var summary = await service.SummarizeAsync(Slice);

        Assert.Equal("I fixed the login bug and all tests pass.", summary);
        Assert.Equal(SummaryService.SystemPrompt, fake.LastSystemPrompt);
        Assert.Contains("Fix the login bug", fake.LastUserContent);
    }
}
