using KokoroSharp;
using KokoroSharp.Core;
using Raiven.Core.Config;
using Raiven.Core.Logging;
using Raiven.Core.Voice;

namespace Raiven.App;

public sealed class VoiceService(RaivenConfig config) : IVoice
{
    private readonly Lock _lock = new();
    private KokoroTTS? _tts;
    private KokoroVoice? _voice;

    // Raw backend: KokoroSharp stops any in-flight playback before speaking. The SpeechCoordinator
    // wraps this and only ever calls it one utterance at a time, so priority is not used here.
    public async Task SpeakAsync(string text, Action<VoicePhase>? onPhase = null, SpeechPriority priority = SpeechPriority.Normal)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SynthesisHandle handle;
        lock (_lock)
        {
            if (_tts is null)
            {
                onPhase?.Invoke(VoicePhase.LoadingModel);
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
                    var tts = KokoroTTS.LoadModel();
                    var voice = KokoroVoiceManager.GetVoice(config.Voice);
                    _tts = tts;
                    _voice = voice;
                }
                finally
                {
                    Directory.SetCurrentDirectory(originalCwd);
                }
                FileLog.Info($"Kokoro ready with voice '{config.Voice}'.");
            }
            onPhase?.Invoke(VoicePhase.Generating);
            handle = _tts.SpeakFast(text, _voice!);
        }

        // Handle callbacks are plain delegate fields; += preserves any library-installed
        // ones. They attach just after SpeakFast returns, so an (unrealistically) instant
        // completion could slip past them - the cap below completes the task even then.
        handle.OnSpeechStarted += _ => onPhase?.Invoke(VoicePhase.Speaking);
        handle.OnSpeechCompleted += _ => tcs.TrySetResult();
        handle.OnSpeechCanceled += _ => tcs.TrySetResult();

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var cap = TimeSpan.FromSeconds(60 + words / 2.0); // ~2x real speech duration; a stuck awaiter is worse than an early cleanup
        await Task.WhenAny(tcs.Task, Task.Delay(cap)).ConfigureAwait(false);
    }

    public void Stop()
    {
        lock (_lock) _tts?.StopPlayback();
    }
}
