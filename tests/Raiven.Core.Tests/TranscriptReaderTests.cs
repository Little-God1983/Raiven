using Raiven.Core.Transcripts;

namespace Raiven.Core.Tests;

public class TranscriptReaderTests
{
    private static string WriteTranscript(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"raiven-test-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    // Realistic line shapes, modeled on a real Claude Code transcript.
    private const string QueueLine = """{"type":"queue-operation","operation":"enqueue","timestamp":"2026-07-02T19:03:01.695Z","sessionId":"s1"}""";
    private const string AttachmentLine = """{"parentUuid":null,"attachment":{"type":"hook_success"},"type":"attachment","sessionId":"s1"}""";
    private const string OldUserLine = """{"type":"user","message":{"role":"user","content":"an earlier request"},"sessionId":"s1","cwd":"E:\\Repos\\RAIVEN"}""";
    private const string UserStringLine = """{"type":"user","message":{"role":"user","content":"Fix the login bug"},"sessionId":"s1","cwd":"E:\\Repos\\RAIVEN"}""";
    private const string MetaUserLine = """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"Injected skill content"}]},"isMeta":true,"sessionId":"s1"}""";
    private const string AssistantToolLine = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Edit","input":{"file_path":"auth.cs"}}]},"sessionId":"s1"}""";
    private const string ToolResultLine = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]},"sessionId":"s1"}""";
    private const string AssistantTextLine = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Fixed the null check. All tests pass."}]},"sessionId":"s1"}""";

    [Fact]
    public void ReadLastTurn_ExtractsPromptToolsAndText()
    {
        var path = WriteTranscript(QueueLine, AttachmentLine, OldUserLine, UserStringLine,
            MetaUserLine, AssistantToolLine, ToolResultLine, AssistantTextLine);

        var slice = TranscriptReader.ReadLastTurn(path);

        Assert.Equal("Fix the login bug", slice.UserPrompt);
        Assert.Equal("Fixed the null check. All tests pass.", slice.AssistantText);
        Assert.Equal(["Edit"], slice.ToolsUsed);
    }

    [Fact]
    public void ReadLastTurn_MetaAndToolResultLines_AreNotPrompts()
    {
        // Last real prompt is UserStringLine even though meta/tool_result user lines come after.
        var path = WriteTranscript(UserStringLine, AssistantToolLine, ToolResultLine, MetaUserLine, AssistantTextLine);

        var slice = TranscriptReader.ReadLastTurn(path);

        Assert.Equal("Fix the login bug", slice.UserPrompt);
    }

    [Fact]
    public void ReadLastTurn_ArrayContentPrompt_ConcatenatesTextItems()
    {
        var arrayPrompt = """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"Part one. "},{"type":"text","text":"Part two."}]},"sessionId":"s1"}""";
        var path = WriteTranscript(arrayPrompt, AssistantTextLine);

        var slice = TranscriptReader.ReadLastTurn(path);

        Assert.Equal("Part one. Part two.", slice.UserPrompt);
    }

    [Fact]
    public void ReadLastTurn_UnparseableLines_AreSkipped()
    {
        var path = WriteTranscript("not json {{{", UserStringLine, AssistantTextLine);

        var slice = TranscriptReader.ReadLastTurn(path);

        Assert.Equal("Fix the login bug", slice.UserPrompt);
    }

    [Fact]
    public void ReadLastTurn_AssistantTextOverMaxChars_KeepsTail()
    {
        var longText = new string('a', 100) + "THE END";
        var assistantLong = $$"""{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"{{longText}}"}]},"sessionId":"s1"}""";
        var path = WriteTranscript(UserStringLine, assistantLong);

        var slice = TranscriptReader.ReadLastTurn(path, maxChars: 50);

        Assert.Equal(50, slice.AssistantText.Length);
        Assert.EndsWith("THE END", slice.AssistantText);
    }

    [Fact]
    public void ReadLastTurn_NoUserPrompt_Throws()
    {
        var path = WriteTranscript(QueueLine, AssistantTextLine);

        Assert.Throws<InvalidDataException>(() => TranscriptReader.ReadLastTurn(path));
    }

    [Fact]
    public void ReadLastTurn_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(
            () => TranscriptReader.ReadLastTurn(Path.Combine(Path.GetTempPath(), "raiven-does-not-exist.jsonl")));
    }

    [Fact]
    public void ReadFirstPrompt_ReturnsFirstUserPrompt()
    {
        var path = WriteTranscript(
            """{"type":"user","message":{"role":"user","content":"Fix the login bug"}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Done."}]}}""",
            """{"type":"user","message":{"role":"user","content":"Now add tests"}}""");

        Assert.Equal("Fix the login bug", TranscriptReader.ReadFirstPrompt(path));
    }

    [Fact]
    public void ReadFirstPrompt_SkipsMetaAndToolResultLines()
    {
        var path = WriteTranscript(
            """{"type":"queue-operation","operation":"enqueue"}""",
            """{"type":"user","isMeta":true,"message":{"role":"user","content":"meta noise"}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"tool output"}]}}""",
            """{"type":"user","message":{"role":"user","content":"The real prompt"}}""");

        Assert.Equal("The real prompt", TranscriptReader.ReadFirstPrompt(path));
    }

    [Fact]
    public void ReadFirstPrompt_TruncatesAndFlattensNewlines()
    {
        var longPrompt = "I want you to extend the software\nby adding auto play summary and lots more text beyond sixty characters";
        var serialized = System.Text.Json.JsonSerializer.Serialize(longPrompt);
        var json = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":" + serialized + "}}";
        var path = WriteTranscript(json);

        var result = TranscriptReader.ReadFirstPrompt(path);

        Assert.NotNull(result);
        Assert.True(result!.Length <= 61); // 60 chars + ellipsis
        Assert.EndsWith("…", result);
        Assert.DoesNotContain("\n", result);
    }

    [Fact]
    public void ReadFirstPrompt_MissingFileOrNoPrompt_ReturnsNull()
    {
        Assert.Null(TranscriptReader.ReadFirstPrompt(Path.Combine(Path.GetTempPath(), "raiven-nope.jsonl")));

        var emptyish = WriteTranscript("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"hi"}]}}""");
        Assert.Null(TranscriptReader.ReadFirstPrompt(emptyish));
    }

    [Fact]
    public void ReadFirstPrompt_SkipsSlashCommandRecords()
    {
        var path = WriteTranscript(
            """{"type":"user","message":{"role":"user","content":"<command-name>/model</command-name>\n<command-message>model</command-message>"}}""",
            """{"type":"user","message":{"role":"user","content":"<local-command-stdout>Set model</local-command-stdout>"}}""",
            """{"type":"user","message":{"role":"user","content":"Fix the login bug"}}""");

        Assert.Equal("Fix the login bug", TranscriptReader.ReadFirstPrompt(path));
    }

    [Fact]
    public void ReadFirstPrompt_SkipsBashModeRecords()
    {
        var path = WriteTranscript(
            """{"type":"user","message":{"role":"user","content":"<bash-input>ls -la</bash-input>"}}""",
            """{"type":"user","message":{"role":"user","content":"<bash-stdout>total 4</bash-stdout>"}}""",
            """{"type":"user","message":{"role":"user","content":"Fix the login bug"}}""");

        Assert.Equal("Fix the login bug", TranscriptReader.ReadFirstPrompt(path));
    }

    // Regression for the "Raiven stuck at working" bug (#11): Claude Code holds the
    // transcript open for appending (more so with multiple agents), and RAIVEN must
    // still be able to read it instead of failing with a sharing violation.
    [Fact]
    public void ReadLastTurn_TranscriptHeldOpenForAppending_StillReads()
    {
        var path = WriteTranscript(UserStringLine, AssistantTextLine);
        // Mimic Claude Code's own append handle (FILE_SHARE_READ|WRITE|DELETE).
        using var writerHandle = new FileStream(
            path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        var slice = TranscriptReader.ReadLastTurn(path);

        Assert.Equal("Fix the login bug", slice.UserPrompt);
        Assert.Equal("Fixed the null check. All tests pass.", slice.AssistantText);
    }

    [Fact]
    public void ReadFirstPrompt_TranscriptHeldOpenForAppending_StillReads()
    {
        var path = WriteTranscript(UserStringLine, AssistantTextLine);
        using var writerHandle = new FileStream(
            path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        Assert.Equal("Fix the login bug", TranscriptReader.ReadFirstPrompt(path));
    }
}
