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
}
