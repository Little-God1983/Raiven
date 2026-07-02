using Raiven.Core.Summaries;

namespace Raiven.Core.Tests;

public class AnthropicClaudeClientTests
{
    [SkippableFact]
    public async Task CompleteAsync_LiveCall_ReturnsNonEmptyText()
    {
        Skip.If(Environment.GetEnvironmentVariable("RAIVEN_LIVE_TESTS") != "1",
            "Set RAIVEN_LIVE_TESTS=1 to run live API tests.");

        var client = new AnthropicClaudeClient("claude-haiku-4-5");

        var text = await client.CompleteAsync("Reply with exactly one short sentence.", "Say hello.");

        Assert.False(string.IsNullOrWhiteSpace(text));
    }
}
