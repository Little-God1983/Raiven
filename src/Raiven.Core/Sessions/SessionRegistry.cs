namespace Raiven.Core.Sessions;

public sealed class SessionRegistry(TimeSpan expiry)
{
    private readonly Dictionary<string, SessionInfo> _sessions = [];
    private readonly Lock _lock = new();

    public void Upsert(string sessionId, string transcriptPath, string cwd, DateTimeOffset now)
    {
        lock (_lock)
        {
            _sessions[sessionId] = new SessionInfo(sessionId, transcriptPath, cwd, now);
        }
    }

    public bool TryGet(string sessionId, DateTimeOffset now, out SessionInfo info)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out info!) && now - info.LastSeen <= expiry)
                return true;
            info = null!;
            return false;
        }
    }
}
