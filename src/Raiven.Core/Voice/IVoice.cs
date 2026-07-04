namespace Raiven.Core.Voice;

public interface IVoice
{
    /// <summary>
    /// Speak the given text aloud. A new call preempts (cuts off) any speech still
    /// playing - deliberate last-writer-wins: question announcements are time-sensitive
    /// and may interrupt a summary readout. The returned task completes when the speech
    /// finishes, is preempted, or is stopped. onPhase reports stages as they begin.
    /// </summary>
    Task SpeakAsync(string text, Action<VoicePhase>? onPhase = null);

    /// <summary>Stop the current playback, if any. The in-flight SpeakAsync task completes.</summary>
    void Stop();
}
