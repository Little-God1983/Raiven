using Raiven.Core.Summaries;

namespace Raiven.Core.Tests;

public class ClaudeCliClientTests
{
    [Fact]
    public void ExtractResultText_ValidJson_ReturnsTrimmedResult()
    {
        var json = """{"result":"  Hello there.  ","session_id":"abc"}""";

        var text = ClaudeCliClient.ExtractResultText(json);

        Assert.Equal("Hello there.", text);
    }

    [Fact]
    public void ExtractResultText_MissingResultField_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => ClaudeCliClient.ExtractResultText("""{"session_id":"abc"}"""));
    }

    [Fact]
    public void ExtractResultText_EmptyResult_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => ClaudeCliClient.ExtractResultText("""{"result":"   "}"""));
    }

    [SkippableFact]
    public async Task CompleteAsync_LiveCall_ReturnsNonEmptyText()
    {
        Skip.If(Environment.GetEnvironmentVariable("RAIVEN_LIVE_TESTS") != "1",
            "Set RAIVEN_LIVE_TESTS=1 to run live CLI tests (requires `claude` on PATH and a logged-in subscription).");

        var client = new ClaudeCliClient("haiku");

        var text = await client.CompleteAsync("Reply with exactly one short sentence.", "Say hello.");

        Assert.False(string.IsNullOrWhiteSpace(text));
    }
}
