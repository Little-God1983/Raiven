using System.Text.Json;
using Raiven.Core.Events;

namespace Raiven.Core.Tests;

public class EventParserTests
{
    private const string StopHookJson = """
        {"session_id":"abc123","transcript_path":"C:\\Users\\x\\.claude\\projects\\p\\abc123.jsonl","cwd":"E:\\Repos\\RAIVEN","permission_mode":"default","hook_event_name":"Stop"}
        """;

    [Fact]
    public void Parse_ClaudeHookPayload_WrapsAsClaudeCodeEvent()
    {
        var evt = EventParser.Parse(StopHookJson);

        Assert.Equal("claude-code", evt.Source);
        Assert.Equal("Stop", evt.Type);
        Assert.Equal("abc123", evt.Payload.GetProperty("session_id").GetString());
    }

    [Fact]
    public void Parse_GenericEnvelope_PassesThrough()
    {
        var evt = EventParser.Parse("""{"source":"my-tool","type":"ping","payload":{"n":1}}""");

        Assert.Equal("my-tool", evt.Source);
        Assert.Equal("ping", evt.Type);
        Assert.Equal(1, evt.Payload.GetProperty("n").GetInt32());
    }

    [Fact]
    public void Parse_EnvelopeWithoutPayloadProperty_UsesWholeRoot()
    {
        var evt = EventParser.Parse("""{"source":"my-tool","type":"ping","extra":true}""");

        Assert.True(evt.Payload.GetProperty("extra").GetBoolean());
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"neither":"envelope","nor":"hook"}""")]
    [InlineData("[1,2,3]")]
    public void Parse_Unrecognized_Throws(string json)
    {
        Assert.Throws<FormatException>(() => EventParser.Parse(json));
    }

    [Fact]
    public void TryParseClaudeStop_StopEvent_ExtractsFields()
    {
        var evt = EventParser.Parse(StopHookJson);

        Assert.True(EventParser.TryParseClaudeStop(evt, out var stop));
        Assert.Equal("abc123", stop.SessionId);
        Assert.Equal(@"C:\Users\x\.claude\projects\p\abc123.jsonl", stop.TranscriptPath);
        Assert.Equal(@"E:\Repos\RAIVEN", stop.Cwd);
    }

    [Fact]
    public void TryParseClaudeStop_OtherEventType_ReturnsFalse()
    {
        var evt = EventParser.Parse("""{"session_id":"abc","transcript_path":"t","cwd":"c","hook_event_name":"Notification"}""");

        Assert.False(EventParser.TryParseClaudeStop(evt, out _));
    }

    [Fact]
    public void TryParseClaudeStop_MissingField_ReturnsFalse()
    {
        var evt = EventParser.Parse("""{"session_id":"abc","hook_event_name":"Stop"}""");

        Assert.False(EventParser.TryParseClaudeStop(evt, out _));
    }

    [Fact]
    public void TryParseClaudeNotification_FullPayload_ParsesAllFields()
    {
        var evt = EventParser.Parse(
            """{"hook_event_name":"Notification","session_id":"s1","transcript_path":"C:\\t.jsonl","cwd":"E:\\Repos\\RAIVEN","message":"Claude needs your permission to use Bash","notification_type":"permission_prompt"}""");

        Assert.True(EventParser.TryParseClaudeNotification(evt, out var n));
        Assert.Equal("s1", n.SessionId);
        Assert.Equal(@"C:\t.jsonl", n.TranscriptPath);
        Assert.Equal(@"E:\Repos\RAIVEN", n.Cwd);
        Assert.Equal("Claude needs your permission to use Bash", n.Message);
        Assert.Equal("permission_prompt", n.NotificationType);
    }

    [Fact]
    public void TryParseClaudeNotification_MissingOptionalFields_DefaultsApplied()
    {
        var evt = EventParser.Parse(
            """{"hook_event_name":"Notification","session_id":"s1","transcript_path":"C:\\t.jsonl","cwd":"E:\\Repos\\RAIVEN"}""");

        Assert.True(EventParser.TryParseClaudeNotification(evt, out var n));
        Assert.Equal("", n.Message);
        Assert.Null(n.NotificationType);
    }

    [Fact]
    public void TryParseClaudeNotification_MissingRequiredField_ReturnsFalse()
    {
        var evt = EventParser.Parse(
            """{"hook_event_name":"Notification","session_id":"s1","cwd":"E:\\Repos\\RAIVEN"}""");

        Assert.False(EventParser.TryParseClaudeNotification(evt, out _));
    }

    [Fact]
    public void TryParseClaudeNotification_StopEvent_ReturnsFalse()
    {
        var evt = EventParser.Parse(
            """{"hook_event_name":"Stop","session_id":"s1","transcript_path":"C:\\t.jsonl","cwd":"E:\\Repos\\RAIVEN"}""");

        Assert.False(EventParser.TryParseClaudeNotification(evt, out _));
    }

    // AskUserQuestion is the interactive multiple-choice dialog. It fires no
    // Notification hook (and no Stop, since the turn continues), only a
    // PreToolUse hook with tool_name "AskUserQuestion" - so we surface it from
    // that hook and treat it like a question notification.
    private const string AskUserQuestionHookJson = """
        {"hook_event_name":"PreToolUse","session_id":"s1","transcript_path":"C:\\t.jsonl","cwd":"E:\\Repos\\RAIVEN","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"How do you want to sequence this?","header":"Sequencing","options":[{"label":"Publish SDK first","description":"aa"},{"label":"UI shell now","description":"bb"}]}]}}
        """;

    [Fact]
    public void TryParseClaudeAskUserQuestion_FullPayload_ExtractsFieldsAndQuestionText()
    {
        var evt = EventParser.Parse(AskUserQuestionHookJson);

        Assert.True(EventParser.TryParseClaudeAskUserQuestion(evt, out var q));
        Assert.Equal("s1", q.SessionId);
        Assert.Equal(@"C:\t.jsonl", q.TranscriptPath);
        Assert.Equal(@"E:\Repos\RAIVEN", q.Cwd);
        Assert.Equal("How do you want to sequence this?", q.Message);
        Assert.Equal("ask_user_question", q.NotificationType);
    }

    [Fact]
    public void TryParseClaudeAskUserQuestion_MultipleQuestions_JoinsText()
    {
        var evt = EventParser.Parse(
            """{"hook_event_name":"PreToolUse","session_id":"s1","transcript_path":"C:\\t.jsonl","cwd":"E:\\Repos\\RAIVEN","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"First question?","header":"One"},{"question":"Second question?","header":"Two"}]}}""");

        Assert.True(EventParser.TryParseClaudeAskUserQuestion(evt, out var q));
        Assert.Contains("First question?", q.Message);
        Assert.Contains("Second question?", q.Message);
    }

    [Fact]
    public void TryParseClaudeAskUserQuestion_MissingQuestions_ParsesWithEmptyMessage()
    {
        var evt = EventParser.Parse(
            """{"hook_event_name":"PreToolUse","session_id":"s1","transcript_path":"C:\\t.jsonl","cwd":"E:\\Repos\\RAIVEN","tool_name":"AskUserQuestion","tool_input":{}}""");

        Assert.True(EventParser.TryParseClaudeAskUserQuestion(evt, out var q));
        Assert.Equal("", q.Message);
    }

    [Fact]
    public void TryParseClaudeAskUserQuestion_OtherTool_ReturnsFalse()
    {
        var evt = EventParser.Parse(
            """{"hook_event_name":"PreToolUse","session_id":"s1","transcript_path":"C:\\t.jsonl","cwd":"E:\\Repos\\RAIVEN","tool_name":"Bash","tool_input":{"command":"npm test"}}""");

        Assert.False(EventParser.TryParseClaudeAskUserQuestion(evt, out _));
    }

    [Fact]
    public void TryParseClaudeAskUserQuestion_NotPreToolUse_ReturnsFalse()
    {
        var evt = EventParser.Parse(
            """{"hook_event_name":"Notification","session_id":"s1","transcript_path":"C:\\t.jsonl","cwd":"E:\\Repos\\RAIVEN"}""");

        Assert.False(EventParser.TryParseClaudeAskUserQuestion(evt, out _));
    }

    [Fact]
    public void TryParseClaudeAskUserQuestion_MissingRequiredField_ReturnsFalse()
    {
        var evt = EventParser.Parse(
            """{"hook_event_name":"PreToolUse","session_id":"s1","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"Q?"}]}}""");

        Assert.False(EventParser.TryParseClaudeAskUserQuestion(evt, out _));
    }
}
