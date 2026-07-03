namespace Raiven.Core.Events;

public sealed record ClaudeNotificationEvent(
    string SessionId,
    string TranscriptPath,
    string Cwd,
    string Message,
    string? NotificationType);
