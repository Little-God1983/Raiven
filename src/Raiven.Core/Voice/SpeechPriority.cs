namespace Raiven.Core.Voice;

/// <summary>
/// Relative urgency of a spoken utterance. An <see cref="Urgent"/> line (a question
/// announcement - Claude Code is blocked waiting on the user) interrupts a
/// <see cref="Normal"/> line (a finished-turn summary) that is currently playing; the
/// interrupted summary resumes afterward. Normal lines never interrupt anything - they
/// queue and play in order.
/// </summary>
public enum SpeechPriority
{
    Normal,
    Urgent,
}
