namespace Raiven.Core.Voice;

/// <summary>Pipeline stage of a single SpeakAsync call, reported as each begins.</summary>
public enum VoicePhase
{
    /// <summary>First-ever call: the TTS model is being loaded (may include a one-time ~320 MB download).</summary>
    LoadingModel,
    /// <summary>Text is being synthesized into audio.</summary>
    Generating,
    /// <summary>Audio playback has started.</summary>
    Speaking,
}
