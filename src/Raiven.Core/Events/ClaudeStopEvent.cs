namespace Raiven.Core.Events;

public sealed record ClaudeStopEvent(string SessionId, string TranscriptPath, string Cwd);
