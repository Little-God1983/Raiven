namespace Raiven.Core.Voice;

public interface IVoice
{
    /// <summary>Speak the given text aloud. Playback is fire-and-forget.</summary>
    void Speak(string text);
}
