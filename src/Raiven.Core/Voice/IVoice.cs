namespace Raiven.Core.Voice;

public interface IVoice
{
    /// <summary>
    /// Speak the given text aloud. Playback is fire-and-forget, and a new call
    /// preempts (cuts off) any speech still playing - deliberate last-writer-wins:
    /// question announcements are time-sensitive and may interrupt a summary readout.
    /// </summary>
    void Speak(string text);
}
