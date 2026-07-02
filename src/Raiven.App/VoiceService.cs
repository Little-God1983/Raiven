using KokoroSharp;
using KokoroSharp.Core;
using Raiven.Core.Config;
using Raiven.Core.Logging;

namespace Raiven.App;

public sealed class VoiceService(RaivenConfig config)
{
    private readonly Lock _lock = new();
    private KokoroTTS? _tts;
    private KokoroVoice? _voice;

    public void Speak(string text)
    {
        lock (_lock)
        {
            if (_tts is null)
            {
                FileLog.Info("Loading Kokoro model (downloads ~320 MB on first ever run)...");
                _tts = KokoroTTS.LoadModel();
                _voice = KokoroVoiceManager.GetVoice(config.Voice);
                FileLog.Info($"Kokoro ready with voice '{config.Voice}'.");
            }
            _tts.SpeakFast(text, _voice!);
        }
    }
}
