namespace Raiven.Core.Sessions;

public sealed record SessionInfo(string SessionId, string TranscriptPath, string Cwd, DateTimeOffset LastSeen);
