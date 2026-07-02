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
                // KokoroSharp has no overload that both accepts an explicit path AND
                // auto-downloads: LoadModel(string path) only loads an existing file
                // (it throws if missing, it never downloads), while the download-capable
                // overloads (the parameterless LoadModel()/LoadModelAsync()) always
                // resolve/download the ~320 MB "kokoro.onnx" relative to the process's
                // current working directory, with no parameter to redirect the target
                // directory. RAIVEN is launched from varying CWDs (repo root in dev, the
                // exe folder, or System32 under Windows autostart), so left alone this
                // would re-download 325 MB repeatedly or fail depending on launch context.
                // Point the CWD at the stable AppPaths.DataDir before calling the
                // parameterless overload so the model downloads once and is always
                // reloaded from the same absolute location afterward. The CWD switch is
                // only so KokoroSharp can resolve/download its model in that stable
                // directory; it is restored immediately afterward (in a finally) so no
                // process-global side effect leaks out and silently breaks relative paths
                // used elsewhere in the app (e.g. a relative ChimeWavPath in config).
                var originalCwd = Directory.GetCurrentDirectory();
                Directory.CreateDirectory(AppPaths.DataDir);
                Directory.SetCurrentDirectory(AppPaths.DataDir);

                var modelPath = Path.Combine(AppPaths.DataDir, "kokoro.onnx");
                FileLog.Info($"Loading Kokoro model (downloads ~320 MB on first ever run) at '{modelPath}'...");
                try
                {
                    _tts = KokoroTTS.LoadModel();
                    _voice = KokoroVoiceManager.GetVoice(config.Voice);
                }
                finally
                {
                    Directory.SetCurrentDirectory(originalCwd);
                }
                FileLog.Info($"Kokoro ready with voice '{config.Voice}'.");
            }
            _tts.SpeakFast(text, _voice!);
        }
    }
}
