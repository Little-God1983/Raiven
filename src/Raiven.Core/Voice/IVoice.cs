namespace Raiven.Core.Voice;

public interface IVoice
{
    /// <summary>
    /// Speak the given text aloud. The wired implementation (<c>SpeechCoordinator</c>) queues
    /// utterances and plays them one at a time, so overlapping summaries never cut each other
    /// off; an <see cref="SpeechPriority.Urgent"/> line (a question - Claude is waiting on the
    /// user) interrupts a <see cref="SpeechPriority.Normal"/> summary that is playing, and the
    /// summary resumes from the top afterward. The returned task completes when this utterance
    /// has finished (including any resume replay), was stopped, or was superseded. onPhase
    /// reports stages as they begin. (The raw backend, <c>VoiceService</c>, speaks immediately
    /// and preempts; the coordinator only ever drives it one utterance at a time.)
    /// </summary>
    Task SpeakAsync(string text, Action<VoicePhase>? onPhase = null, SpeechPriority priority = SpeechPriority.Normal);

    /// <summary>Stop the current playback, if any. The in-flight SpeakAsync task completes.</summary>
    void Stop();
}
