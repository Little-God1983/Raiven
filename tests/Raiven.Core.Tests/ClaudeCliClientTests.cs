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

    [Fact]
    public void ResolveExecutablePath_FindsRealExecutableOnPath()
    {
        // "dotnet" is guaranteed to be on PATH in this test environment (it's how the
        // test runner itself launched) and has a real .exe, unlike claude's .cmd shim.
        var path = ClaudeCliClient.ResolveExecutablePath("dotnet");

        Assert.True(File.Exists(path));
        Assert.EndsWith("dotnet.exe", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveExecutablePath_UnknownCommand_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => ClaudeCliClient.ResolveExecutablePath("this-command-does-not-exist-anywhere-raiven"));
    }

    [Fact]
    public void BuildArguments_LoadsNoSettingSources_SoUserHooksCannotFire()
    {
        var args = ClaudeCliClient.BuildArguments("system prompt", "haiku");

        var idx = args.ToList().IndexOf("--setting-sources");
        Assert.True(idx >= 0, "args must contain --setting-sources");
        Assert.Equal("", args[idx + 1]);
        Assert.Contains("--max-turns", args);
        Assert.Equal("haiku", args[args.ToList().IndexOf("--model") + 1]);
    }

    [Fact]
    public void ScrubChildEnvironment_RemovesClaudeCodeAndAnthropicVars()
    {
        // CLAUDECODE is set inside every Claude Code session; if RAIVEN inherits it
        // (e.g. launched from a Claude Code terminal, or by a Stop hook's own process),
        // the child `claude` CLI refuses to launch ("cannot be launched inside another
        // Claude Code session"). The ANTHROPIC_* keys, if present, would override the
        // /login subscription credential. All three must be stripped from the child env.
        var env = new Dictionary<string, string?>
        {
            ["CLAUDECODE"] = "1",
            ["ANTHROPIC_API_KEY"] = "sk-test",
            ["ANTHROPIC_AUTH_TOKEN"] = "tok-test",
            ["PATH"] = "keep-me",
            ["CLAUDE_CODE_SESSION_ID"] = "keep-me-too",
        };

        ClaudeCliClient.ScrubChildEnvironment(env);

        Assert.False(env.ContainsKey("CLAUDECODE"));
        Assert.False(env.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.False(env.ContainsKey("ANTHROPIC_AUTH_TOKEN"));
        // Unrelated vars (incl. other CLAUDE_CODE_* that the CLI tolerates) are preserved.
        Assert.Equal("keep-me", env["PATH"]);
        Assert.Equal("keep-me-too", env["CLAUDE_CODE_SESSION_ID"]);
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
