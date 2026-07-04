# Audio Keep-Alive, Live Status Toast, and Summary Labels Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bluetooth keep-alive silent stream, `datetime — project — topic` Recent-summaries labels, and a finished-turn toast that stays open showing pipeline stages (Summarizing → Generating voice → Speaking) until speech ends.

**Architecture:** Three additive features on the existing tray app. Keep-alive is a new `AudioKeepAlive` class (NAudio, already transitive via KokoroSharp). The status toast reuses the countdown toast's bindable progress bar: `SummaryPipeline` (Core, testable) drives stage updates through two new `INotifier` methods, and `IVoice` becomes awaitable (`SpeakAsync` completes when speech ends) using KokoroSharp `SynthesisHandle` callbacks. Toast liveness is tracked optimistically in `AppSdkNotifier` because the AppNotifications API has no dismissed event.

**Tech Stack:** .NET 10 / WinForms tray app, Windows App SDK 2.2 (`AppNotification*`), KokoroSharp.CPU 0.6.7 (brings NAudio), xUnit.

**Spec:** `docs/superpowers/specs/2026-07-04-keepalive-status-toast-labels-design.md`

## Global Constraints

- **No new NuGet packages.** NAudio comes transitively from `KokoroSharp.CPU 0.6.7`; reference its namespaces directly.
- Target frameworks stay as-is: Core `net10.0`, App `net10.0-windows10.0.17763.0`.
- New config keys and defaults, exactly: `KeepAudioAlive` = `false`, `ShowPlaybackStatus` = `true`.
- Status strings, exactly (note the `…` is U+2026, one character): `Summarizing with Haiku…` (0.25), `Loading voice model…` (0.45), `Generating voice…` (0.65), `Speaking…` (0.9).
- Menu label format, exactly: `dd.MM.yyyy HH:mm — Folder — Headline` with `CultureInfo.InvariantCulture` (the `—` is U+2014 with a space either side).
- Toast buttons when `ShowPlaybackStatus` is on: `Play now · Stop · Hide` (countdown toast) / `Stop · Hide` (standalone status toast). When off: today's `Play now · Abort`.
- The spec's `AutoPlayCountdown.Cancel` bool return **already exists** (with tests in `AutoPlayCountdownTests.cs:37-41`) — no task needed.
- Run tests with `dotnet test tests/Raiven.Core.Tests`; build the app with `dotnet build src/Raiven.App`.
- Commit style: `feat:` / `refactor:` / `docs:` prefixes; every commit ends with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. Never `--no-verify`.
- House style: file-scoped namespaces, `System.Threading.Lock` for locks, best-effort try/catch + `FileLog` around all Windows-API surface (never crash the tray app).

---

### Task 1: Config keys `KeepAudioAlive` and `ShowPlaybackStatus`

**Files:**
- Modify: `src/Raiven.Core/Config/RaivenConfig.cs`
- Test: `tests/Raiven.Core.Tests/RaivenConfigTests.cs`

**Interfaces:**
- Consumes: existing `RaivenConfig` (mutable properties + `Save`).
- Produces: `bool RaivenConfig.KeepAudioAlive` (default `false`), `bool RaivenConfig.ShowPlaybackStatus` (default `true`). Tasks 4, 5, 7 read these.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Raiven.Core.Tests/RaivenConfigTests.cs` (inside the class):

```csharp
    [Fact]
    public void LoadOrCreate_MissingFile_HasKeepAliveAndPlaybackStatusDefaults()
    {
        var config = RaivenConfig.LoadOrCreate(TempConfigPath());

        Assert.False(config.KeepAudioAlive);
        Assert.True(config.ShowPlaybackStatus);
    }

    [Fact]
    public void LoadOrCreate_ExistingFile_ReadsKeepAliveAndPlaybackStatusValues()
    {
        var path = TempConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"KeepAudioAlive":true,"ShowPlaybackStatus":false}""");

        var config = RaivenConfig.LoadOrCreate(path);

        Assert.True(config.KeepAudioAlive);
        Assert.False(config.ShowPlaybackStatus);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Raiven.Core.Tests`
Expected: build FAILS with CS1061 (`RaivenConfig` has no `KeepAudioAlive`).

- [ ] **Step 3: Add the properties**

In `src/Raiven.Core/Config/RaivenConfig.cs`, after the `QuestionWordLimit` property (line 25), add:

```csharp
    public bool KeepAudioAlive { get; set; } // continuous silent stream keeps the audio device / Bluetooth link awake
    public bool ShowPlaybackStatus { get; set; } = true; // finished-turn toast stays open showing summarize/generate/speak stages
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Raiven.Core.Tests`
Expected: PASS (all tests).

- [ ] **Step 5: Commit**

```bash
git add src/Raiven.Core/Config/RaivenConfig.cs tests/Raiven.Core.Tests/RaivenConfigTests.cs
git commit -m "feat: KeepAudioAlive and ShowPlaybackStatus config keys

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: Recent-summaries `MenuLabel`

**Files:**
- Modify: `src/Raiven.Core/Summaries/SummaryHistory.cs` (the `SummaryHistoryEntry` record, lines 6-13)
- Modify: `src/Raiven.App/TrayContext.cs:88-95` (`RebuildRecentSummaries`)
- Test: `tests/Raiven.Core.Tests/SummaryHistoryTests.cs`

**Interfaces:**
- Consumes: `SummaryHistoryEntry` record.
- Produces: `string SummaryHistoryEntry.MenuLabel` (computed, `[JsonIgnore]`). Task 7's docs and the tray menu rely on the exact format `04.07.2026 14:32 — RAIVEN — Fix login bug`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Raiven.Core.Tests/SummaryHistoryTests.cs` (inside the class):

```csharp
    [Fact]
    public void MenuLabel_FormatsDatetimeFolderHeadline()
    {
        // Local-kind DateTime -> DateTimeOffset assumes the local offset, so
        // LocalDateTime round-trips the same wall time on any machine/timezone.
        var generatedAt = new DateTimeOffset(new DateTime(2026, 7, 4, 14, 32, 0));
        var entry = new SummaryHistoryEntry(
            "s1", "Fix login bug", "RAIVEN", generatedAt,
            @"C:\transcripts\s1.jsonl", DateTime.UtcNow, "text");

        Assert.Equal("04.07.2026 14:32 — RAIVEN — Fix login bug", entry.MenuLabel);
    }

    [Fact]
    public void MenuLabel_IsNotPersistedToJson()
    {
        var path = TempHistoryPath();
        var history = SummaryHistory.Load(path);
        history.Add(Entry("s1"));

        Assert.DoesNotContain("MenuLabel", File.ReadAllText(path));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Raiven.Core.Tests`
Expected: build FAILS with CS1061 (`SummaryHistoryEntry` has no `MenuLabel`).

- [ ] **Step 3: Implement `MenuLabel`**

In `src/Raiven.Core/Summaries/SummaryHistory.cs`, replace the record declaration (lines 6-13) with:

```csharp
public sealed record SummaryHistoryEntry(
    string SessionId,
    string Headline,
    string Folder,
    DateTimeOffset GeneratedAt,
    string TranscriptPath,
    DateTime TranscriptLastWriteUtc,
    string SummaryText)
{
    /// <summary>Tray menu label, e.g. "04.07.2026 14:32 — RAIVEN — Fix login bug".</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string MenuLabel =>
        $"{GeneratedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture)} — {Folder} — {Headline}";
}
```

(`[JsonIgnore]` matters: System.Text.Json serializes read-only properties by default, which would write a redundant `MenuLabel` field into every `history.json` entry.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Raiven.Core.Tests`
Expected: PASS.

- [ ] **Step 5: Use it in the tray menu**

In `src/Raiven.App/TrayContext.cs`, inside `RebuildRecentSummaries`, replace:

```csharp
            // Escape ampersands so headlines with '&' don't render as menu mnemonics.
            var label = $"{captured.Headline} ({captured.Folder}, {captured.GeneratedAt.LocalDateTime:HH:mm})"
                .Replace("&", "&&");
```

with:

```csharp
            // Escape ampersands so headlines with '&' don't render as menu mnemonics.
            var label = captured.MenuLabel.Replace("&", "&&");
```

- [ ] **Step 6: Build the app and run all tests**

Run: `dotnet build src/Raiven.App` then `dotnet test tests/Raiven.Core.Tests`
Expected: build succeeds, all tests PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Raiven.Core/Summaries/SummaryHistory.cs src/Raiven.App/TrayContext.cs tests/Raiven.Core.Tests/SummaryHistoryTests.cs
git commit -m "feat: datetime-first Recent summaries menu labels

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: `IVoice.SpeakAsync` with phases and completion

**Files:**
- Create: `src/Raiven.Core/Voice/VoicePhase.cs`
- Modify: `src/Raiven.Core/Voice/IVoice.cs`
- Modify: `src/Raiven.App/VoiceService.cs`
- Modify: `src/Raiven.Core/Questions/QuestionPipeline.cs` (3 `voice.Speak` calls + catch fallback)
- Modify: `src/Raiven.Core/Summaries/SummaryPipeline.cs` (2 `voice.Speak` calls)
- Modify: `src/Raiven.App/Program.cs` (`replaySummary`, `testVoice`, `RunVoiceTest`)
- Test: `tests/Raiven.Core.Tests/SummaryPipelineTests.cs`, `tests/Raiven.Core.Tests/QuestionPipelineTests.cs` (fake updates only)

This task is a behavior-preserving migration: everything speaks exactly as before, but callers can now await completion and observe phases. No new tests — the existing suites must stay green.

**Interfaces:**
- Consumes: KokoroSharp `KokoroTTS.SpeakFast(text, voice)` returning `SynthesisHandle` with delegate **fields** `OnSpeechStarted` (`Action<SpeechStartPacket>`), `OnSpeechCompleted` (`Action<SpeechCompletionPacket>`), `OnSpeechCanceled` (`Action<SpeechCancellationPacket>`); `KokoroTTS.StopPlayback()`.
- Produces (Tasks 5 and 7 depend on these exact signatures):
  - `enum VoicePhase { LoadingModel, Generating, Speaking }` (namespace `Raiven.Core.Voice`)
  - `Task IVoice.SpeakAsync(string text, Action<VoicePhase>? onPhase = null)` — completes when speech finishes, is preempted, or is stopped.
  - `void IVoice.Stop()`

- [ ] **Step 1: Create `src/Raiven.Core/Voice/VoicePhase.cs`**

```csharp
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
```

- [ ] **Step 2: Replace `src/Raiven.Core/Voice/IVoice.cs`**

```csharp
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
```

- [ ] **Step 3: Replace `src/Raiven.App/VoiceService.cs`**

```csharp
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

    // KokoroSharp stops any in-flight playback before speaking - see IVoice.SpeakAsync.
    public async Task SpeakAsync(string text, Action<VoicePhase>? onPhase = null)
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
```

- [ ] **Step 4: Migrate `QuestionPipeline`**

In `src/Raiven.Core/Questions/QuestionPipeline.cs`, inside `AnnounceAsync`, replace the three `voice.Speak(...)` switch-case calls and the catch fallback:

```csharp
                case "message" when !string.IsNullOrWhiteSpace(evt.Message):
                    await voice.SpeakAsync(SpeechText.LimitWords(evt.Message, config.QuestionWordLimit));
                    return;
                case "summary":
                    await voice.SpeakAsync(await SummarizeQuestionAsync(evt));
                    return;
                default: // "announce", "message" with empty message, and unknown values
                    if (!config.QuestionVoice.Equals("announce", StringComparison.OrdinalIgnoreCase) &&
                        !config.QuestionVoice.Equals("message", StringComparison.OrdinalIgnoreCase))
                        FileLog.Info($"Unknown QuestionVoice '{config.QuestionVoice}'; using announce");
                    await voice.SpeakAsync(announceLine);
                    return;
```

and in the catch block:

```csharp
            try { await voice.SpeakAsync(announceLine); }
            catch (Exception voiceEx) { FileLog.Error("Fallback announcement also failed", voiceEx); }
```

- [ ] **Step 5: Migrate `SummaryPipeline` (minimal — stages come in Task 5)**

In `src/Raiven.Core/Summaries/SummaryPipeline.cs`, replace `voice.Speak(cached.SummaryText);` with `await voice.SpeakAsync(cached.SummaryText);` and `voice.Speak(spokenText);` with `await voice.SpeakAsync(spokenText);`.

- [ ] **Step 6: Migrate `Program.cs` call sites**

In `src/Raiven.App/Program.cs`:

- `replaySummary` argument: `replaySummary: entry => Task.Run(() => voice.SpeakAsync(entry.SummaryText)),`
- `testVoice` argument: `testVoice: () => Task.Run(() => voice.SpeakAsync("RAIVEN online. All systems operational.")),`
- `RunVoiceTest` body (SpeakAsync completes when playback ends, so the keep-alive sleep goes away):

```csharp
    private static void RunVoiceTest(RaivenConfig config)
    {
        var voice = new VoiceService(config);
        FileLog.Info("Speaking test phrase...");
        voice.SpeakAsync("RAIVEN online. All systems operational.").GetAwaiter().GetResult();
    }
```

- [ ] **Step 7: Update the two test fakes**

In `tests/Raiven.Core.Tests/SummaryPipelineTests.cs` AND `tests/Raiven.Core.Tests/QuestionPipelineTests.cs`, replace the `FakeVoice` class (identical in both) with:

```csharp
    private sealed class FakeVoice : IVoice
    {
        public List<string> Spoken { get; } = [];
        public Task SpeakAsync(string text, Action<VoicePhase>? onPhase = null)
        {
            Spoken.Add(text);
            return Task.CompletedTask;
        }
        public void Stop() { }
    }
```

- [ ] **Step 8: Build and run all tests**

Run: `dotnet build src/Raiven.App` then `dotnet test tests/Raiven.Core.Tests`
Expected: build succeeds, all existing tests PASS (no behavior change).

- [ ] **Step 9: Commit**

```bash
git add src/Raiven.Core/Voice/ src/Raiven.App/VoiceService.cs src/Raiven.Core/Questions/QuestionPipeline.cs src/Raiven.Core/Summaries/SummaryPipeline.cs src/Raiven.App/Program.cs tests/Raiven.Core.Tests/SummaryPipelineTests.cs tests/Raiven.Core.Tests/QuestionPipelineTests.cs
git commit -m "refactor: IVoice.SpeakAsync with phase reporting and completion

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: `INotifier` status-toast surface and `AppSdkNotifier` implementation

**Files:**
- Modify: `src/Raiven.Core/Notifications/INotifier.cs`
- Modify: `src/Raiven.App/AppSdkNotifier.cs` (full replacement below)
- Modify: `src/Raiven.App/Program.cs` (3 `new AppSdkNotifier()` call sites gain the config argument)
- Test: `tests/Raiven.Core.Tests/SummaryPipelineTests.cs` (FakeNotifier grows the new members)

No unit tests possible for `AppSdkNotifier` (Windows App SDK); the deliverable is a clean build, green existing tests, and the fake ready for Task 5.

**Interfaces:**
- Consumes: `RaivenConfig.ShowPlaybackStatus` (Task 1), Windows App SDK `AppNotificationBuilder` (`SetScenario(AppNotificationScenario.Reminder)`, `MuteAudio()`), `AppNotificationManager.UpdateAsync` returning `AppNotificationProgressResult`.
- Produces (Task 5 and 7 depend on these exact signatures):
  - `event Action<string>? INotifier.StopRequested`
  - `event Action<string>? INotifier.HideRequested`
  - `void INotifier.ShowPlaybackStatus(string sessionId, string folderName, string? headline)`
  - `void INotifier.UpdatePlaybackStatus(string sessionId, string status, double fraction)`
  - `bool INotifier.IsToastLive(string sessionId)`
  - `void AppSdkNotifier.RemoveLiveNotifications()` (concrete class only, for shutdown)
  - `AppSdkNotifier(RaivenConfig config)` constructor.

- [ ] **Step 1: Replace `src/Raiven.Core/Notifications/INotifier.cs`**

```csharp
namespace Raiven.Core.Notifications;

public interface INotifier
{
    /// <summary>Raised with the session id when the user asks to hear a summary (toast body, "Play summary", or "Play now").</summary>
    event Action<string>? PlaySummaryRequested;

    /// <summary>Raised with the session id when the user aborts a pending auto-play (legacy Abort button).</summary>
    event Action<string>? AbortRequested;

    /// <summary>Raised when the user clicks Stop: cancel a pending countdown, or stop speech if this session is speaking.</summary>
    event Action<string>? StopRequested;

    /// <summary>Raised when the user clicks Hide: dismiss the toast only - countdown and playback continue.</summary>
    event Action<string>? HideRequested;

    /// <summary>Static finished-turn toast (no countdown). Headline is the chat's first prompt, or null to fall back to the folder line.</summary>
    void ShowFinished(string sessionId, string folderName, string? headline);

    /// <summary>Finished-turn toast with an auto-play progress bar. Buttons depend on ShowPlaybackStatus config.</summary>
    void ShowFinishedCountdown(string sessionId, string folderName, string? headline, int totalSeconds);

    /// <summary>Toast for a Claude Code question / attention request. No actions; informational only.</summary>
    void ShowQuestion(string folderName, string? headline, string message);

    /// <summary>Advance the countdown toast's progress bar (fraction 0..1). Safe to call for a dismissed toast.</summary>
    void UpdateCountdownProgress(string sessionId, double fraction);

    /// <summary>Standalone playback-status toast (Stop/Hide buttons, progress bar) for a run without a live countdown toast.</summary>
    void ShowPlaybackStatus(string sessionId, string folderName, string? headline);

    /// <summary>Update the live toast's progress bar to a pipeline stage. No-op once the toast is gone.</summary>
    void UpdatePlaybackStatus(string sessionId, string status, double fraction);

    /// <summary>Whether a toast for this session is believed to still be un-dismissed.</summary>
    bool IsToastLive(string sessionId);

    /// <summary>Remove the toast for a session (e.g. once playback finishes).</summary>
    void RemoveNotification(string sessionId);

    /// <summary>Show a non-fatal error to the user.</summary>
    void ShowError(string message);
}
```

- [ ] **Step 2: Replace `src/Raiven.App/AppSdkNotifier.cs`**

```csharp
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Raiven.Core.Config;
using Raiven.Core.Logging;
using Raiven.Core.Notifications;

namespace Raiven.App;

public sealed class AppSdkNotifier : INotifier
{
    public event Action<string>? PlaySummaryRequested;
    public event Action<string>? AbortRequested;
    public event Action<string>? StopRequested;
    public event Action<string>? HideRequested;

    private readonly RaivenConfig _config;
    private readonly Dictionary<string, uint> _progressSequences = [];
    // The AppNotifications API has no dismissed event, so liveness is optimistic:
    // shown -> live; any activation, RemoveNotification, or a NotFound progress
    // update (user swiped it away) -> dead. A dead toast is never updated again.
    private readonly HashSet<string> _liveTags = [];
    private readonly Lock _lock = new();

    public AppSdkNotifier(RaivenConfig config)
    {
        _config = config;
        // Subscribe BEFORE Register, per the Windows App SDK contract, so activations
        // that arrive during registration are not lost.
        AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
        AppNotificationManager.Default.Register();
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try
        {
            if (!args.Arguments.TryGetValue("action", out var action) ||
                !args.Arguments.TryGetValue("sessionId", out var sessionId))
                return;

            // Windows dismisses a toast on any activation, body click or button.
            lock (_lock) _liveTags.Remove(sessionId);

            switch (action)
            {
                case "playSummary" or "playNow":
                    FileLog.Info($"Toast activated: {action} for {sessionId}");
                    PlaySummaryRequested?.Invoke(sessionId);
                    break;
                case "abort":
                    FileLog.Info($"Toast activated: abort for {sessionId}");
                    AbortRequested?.Invoke(sessionId);
                    break;
                case "stop":
                    FileLog.Info($"Toast activated: stop for {sessionId}");
                    StopRequested?.Invoke(sessionId);
                    break;
                case "hide":
                    FileLog.Info($"Toast activated: hide for {sessionId}");
                    HideRequested?.Invoke(sessionId);
                    break;
            }
        }
        catch (Exception ex)
        {
            FileLog.Error("Toast activation handling failed", ex);
        }
    }

    public void ShowFinished(string sessionId, string folderName, string? headline)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "playSummary")
                .AddArgument("sessionId", sessionId);
            AddHeadline(builder, folderName, headline);
            builder
                .AddText("Click to hear a summary.")
                .AddButton(new AppNotificationButton("Play summary")
                    .AddArgument("action", "playSummary")
                    .AddArgument("sessionId", sessionId))
                .SetDuration(AppNotificationDuration.Long);
            TrySetLogo(builder);

            var notification = builder.BuildNotification();
            notification.Tag = sessionId;
            lock (_lock) _liveTags.Add(sessionId);
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            FileLog.Error($"Showing finished toast failed for {sessionId}", ex);
        }
    }

    public void ShowFinishedCountdown(string sessionId, string folderName, string? headline, int totalSeconds)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("action", "playNow")
                .AddArgument("sessionId", sessionId);
            AddHeadline(builder, folderName, headline);
            builder.AddProgressBar(new AppNotificationProgressBar()
                .BindValue()
                .BindStatus());
            if (_config.ShowPlaybackStatus)
            {
                // Reminder keeps the toast on screen through countdown AND playback;
                // it is removed programmatically when speech ends.
                builder
                    .SetScenario(AppNotificationScenario.Reminder)
                    .AddButton(new AppNotificationButton("Play now")
                        .AddArgument("action", "playNow")
                        .AddArgument("sessionId", sessionId))
                    .AddButton(new AppNotificationButton("Stop")
                        .AddArgument("action", "stop")
                        .AddArgument("sessionId", sessionId))
                    .AddButton(new AppNotificationButton("Hide")
                        .AddArgument("action", "hide")
                        .AddArgument("sessionId", sessionId));
            }
            else
            {
                builder
                    .AddButton(new AppNotificationButton("Play now")
                        .AddArgument("action", "playNow")
                        .AddArgument("sessionId", sessionId))
                    .AddButton(new AppNotificationButton("Abort")
                        .AddArgument("action", "abort")
                        .AddArgument("sessionId", sessionId))
                    .SetDuration(AppNotificationDuration.Long);
            }
            TrySetLogo(builder);

            var notification = builder.BuildNotification();
            notification.Tag = sessionId;
            notification.Progress = new AppNotificationProgressData(sequenceNumber: 1)
            {
                Value = 0,
                Status = $"Auto-playing in {totalSeconds}s…",
            };
            lock (_lock)
            {
                _progressSequences[sessionId] = 1;
                _liveTags.Add(sessionId);
            }
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            FileLog.Error($"Showing countdown toast failed for {sessionId}", ex);
        }
    }

    public void ShowPlaybackStatus(string sessionId, string folderName, string? headline)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                // Body click = Hide: dismiss the toast, playback continues.
                .AddArgument("action", "hide")
                .AddArgument("sessionId", sessionId)
                .SetScenario(AppNotificationScenario.Reminder)
                .MuteAudio();
            if (headline is not null)
                builder.AddText(headline);
            builder
                .AddText($"Playing summary from {folderName}")
                .AddProgressBar(new AppNotificationProgressBar()
                    .BindValue()
                    .BindStatus())
                .AddButton(new AppNotificationButton("Stop")
                    .AddArgument("action", "stop")
                    .AddArgument("sessionId", sessionId))
                .AddButton(new AppNotificationButton("Hide")
                    .AddArgument("action", "hide")
                    .AddArgument("sessionId", sessionId));
            TrySetLogo(builder);

            var notification = builder.BuildNotification();
            notification.Tag = sessionId;
            notification.Progress = new AppNotificationProgressData(sequenceNumber: 1)
            {
                Value = 0.1,
                Status = "Working…",
            };
            lock (_lock)
            {
                _progressSequences[sessionId] = 1;
                _liveTags.Add(sessionId);
            }
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            FileLog.Error($"Showing status toast failed for {sessionId}", ex);
        }
    }

    public void ShowQuestion(string folderName, string? headline, string message)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddText("Claude Code has a question")
                .AddText(headline ?? folderName);
            if (!string.IsNullOrWhiteSpace(message))
                builder.AddText(message);
            TrySetLogo(builder);
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            FileLog.Error("Showing question toast failed", ex);
        }
    }

    public void UpdateCountdownProgress(string sessionId, double fraction) =>
        PostProgressUpdate(sessionId, "Auto-playing summary…", fraction);

    public void UpdatePlaybackStatus(string sessionId, string status, double fraction) =>
        PostProgressUpdate(sessionId, status, fraction);

    public bool IsToastLive(string sessionId)
    {
        lock (_lock) return _liveTags.Contains(sessionId);
    }

    public void RemoveNotification(string sessionId)
    {
        try
        {
            lock (_lock)
            {
                _progressSequences.Remove(sessionId);
                _liveTags.Remove(sessionId);
            }
            _ = AppNotificationManager.Default.RemoveByTagAsync(sessionId);
        }
        catch (Exception ex)
        {
            FileLog.Error($"Removing notification failed for {sessionId}", ex);
        }
    }

    /// <summary>Best-effort cleanup of toasts we still consider live (app shutdown).</summary>
    public void RemoveLiveNotifications()
    {
        List<string> tags;
        lock (_lock) tags = [.. _liveTags];
        foreach (var tag in tags)
            RemoveNotification(tag);
    }

    public void ShowError(string message)
    {
        try
        {
            var notification = new AppNotificationBuilder()
                .AddText("RAIVEN")
                .AddText(message)
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            FileLog.Error("Showing error toast failed", ex);
        }
    }

    private void PostProgressUpdate(string sessionId, string status, double fraction)
    {
        try
        {
            uint sequence;
            lock (_lock)
            {
                if (!_liveTags.Contains(sessionId))
                    return;
                sequence = _progressSequences.TryGetValue(sessionId, out var current) ? current + 1 : 2;
                _progressSequences[sessionId] = sequence;
            }

            var data = new AppNotificationProgressData(sequence)
            {
                Value = Math.Clamp(fraction, 0, 1),
                Status = status,
            };
            _ = ApplyUpdateAsync(sessionId, data);
        }
        catch (Exception ex)
        {
            FileLog.Error($"Progress update failed for {sessionId}", ex);
        }
    }

    private async Task ApplyUpdateAsync(string sessionId, AppNotificationProgressData data)
    {
        try
        {
            var result = await AppNotificationManager.Default.UpdateAsync(data, sessionId);
            if (result == AppNotificationProgressResult.AppNotificationNotFoundError)
            {
                // User swiped the toast away: treat as Hide - stop updating, never resurrect.
                lock (_lock) _liveTags.Remove(sessionId);
            }
        }
        catch (Exception ex)
        {
            FileLog.Error($"Progress update failed for {sessionId}", ex);
        }
    }

    private static void AddHeadline(AppNotificationBuilder builder, string folderName, string? headline)
    {
        if (headline is not null)
            builder.AddText(headline);
        builder.AddText($"Claude finished in {folderName}");
    }

    private static void TrySetLogo(AppNotificationBuilder builder)
    {
        try
        {
            var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "raiven-logo.png");
            if (File.Exists(logoPath))
                builder.SetAppLogoOverride(new Uri(logoPath));
        }
        catch (Exception ex)
        {
            FileLog.Error("Setting toast logo failed", ex);
        }
    }
}
```

- [ ] **Step 3: Fix the three `AppSdkNotifier` constructor call sites in `src/Raiven.App/Program.cs`**

- In `Run` (stale-activation path): `new AppSdkNotifier(config).ShowError("RAIVEN wasn't running - that session's summary is no longer available.");`
- In `RunTray`: `var notifier = new AppSdkNotifier(config);`
- In `RunToastTest`: `var notifier = new AppSdkNotifier(config);`

- [ ] **Step 4: Grow the `FakeNotifier` in `tests/Raiven.Core.Tests/SummaryPipelineTests.cs`**

Replace the `FakeNotifier` class with:

```csharp
    private sealed class FakeNotifier : INotifier
    {
        public event Action<string>? PlaySummaryRequested;
        public event Action<string>? AbortRequested;
        public event Action<string>? StopRequested;
        public event Action<string>? HideRequested;
        public List<string> Errors { get; } = [];
        public List<(string SessionId, string Folder)> Finished { get; } = [];
        public List<(string Folder, string? Headline, string Message)> Questions { get; } = [];
        public List<(string SessionId, string Folder, string? Headline)> StatusShown { get; } = [];
        public List<(string SessionId, string Status, double Fraction)> StatusUpdates { get; } = [];
        public List<string> Removed { get; } = [];
        public bool ToastLive { get; set; }
        public void ShowFinished(string sessionId, string folderName, string? headline) => Finished.Add((sessionId, folderName));
        public void ShowFinishedCountdown(string sessionId, string folderName, string? headline, int totalSeconds) => Finished.Add((sessionId, folderName));
        public void ShowQuestion(string folderName, string? headline, string message) => Questions.Add((folderName, headline, message));
        public void ShowError(string message) => Errors.Add(message);
        public void UpdateCountdownProgress(string sessionId, double fraction) { }
        public void ShowPlaybackStatus(string sessionId, string folderName, string? headline)
        {
            StatusShown.Add((sessionId, folderName, headline));
            ToastLive = true;
        }
        public void UpdatePlaybackStatus(string sessionId, string status, double fraction) => StatusUpdates.Add((sessionId, status, fraction));
        public bool IsToastLive(string sessionId) => ToastLive;
        public void RemoveNotification(string sessionId)
        {
            Removed.Add(sessionId);
            ToastLive = false;
        }
        public void RaisePlaySummary(string sessionId) => PlaySummaryRequested?.Invoke(sessionId);
        public void RaiseAbort(string sessionId) => AbortRequested?.Invoke(sessionId);
        public void RaiseStop(string sessionId) => StopRequested?.Invoke(sessionId);
        public void RaiseHide(string sessionId) => HideRequested?.Invoke(sessionId);
    }
```

- [ ] **Step 5: Build and run all tests**

Run: `dotnet build src/Raiven.App` then `dotnet test tests/Raiven.Core.Tests`
Expected: build succeeds, all existing tests PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Raiven.Core/Notifications/INotifier.cs src/Raiven.App/AppSdkNotifier.cs src/Raiven.App/Program.cs tests/Raiven.Core.Tests/SummaryPipelineTests.cs
git commit -m "feat: status-toast surface in INotifier and AppSdkNotifier

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: `SummaryPipeline` stage updates and `PlayCachedAsync` (TDD)

**Files:**
- Modify: `src/Raiven.Core/Summaries/SummaryPipeline.cs` (full replacement below)
- Test: `tests/Raiven.Core.Tests/SummaryPipelineTests.cs`

**Interfaces:**
- Consumes: Task 1 config keys, Task 3 `IVoice.SpeakAsync`/`VoicePhase`, Task 4 `INotifier` status methods.
- Produces (Task 7 depends on these exact signatures):
  - `Task SummaryPipeline.PlaySummaryAsync(string sessionId)` (unchanged, = userInitiated: true)
  - `Task SummaryPipeline.PlaySummaryAsync(string sessionId, bool userInitiated)`
  - `Task SummaryPipeline.PlayCachedAsync(SummaryHistoryEntry entry)`
  - `bool SummaryPipeline.IsSpeaking(string sessionId)`

- [ ] **Step 1: Extend `FakeVoice` in `tests/Raiven.Core.Tests/SummaryPipelineTests.cs` (this file's copy only)**

Replace the `FakeVoice` class with:

```csharp
    private sealed class FakeVoice : IVoice
    {
        public List<string> Spoken { get; } = [];
        public List<VoicePhase> PhasesToEmit { get; set; } = [VoicePhase.Generating, VoicePhase.Speaking];
        public Task? Blocker;
        public TaskCompletionSource SpeakEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Stopped;
        public async Task SpeakAsync(string text, Action<VoicePhase>? onPhase = null)
        {
            foreach (var phase in PhasesToEmit)
                onPhase?.Invoke(phase);
            Spoken.Add(text);
            SpeakEntered.TrySetResult();
            if (Blocker is not null)
                await Blocker;
        }
        public void Stop() => Stopped = true;
    }
```

- [ ] **Step 2: Write the failing stage tests**

Append to `tests/Raiven.Core.Tests/SummaryPipelineTests.cs`:

```csharp
    [Fact]
    public async Task PlaySummaryAsync_SummaryMode_EmitsStagesInOrderAndRemovesToast()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var notifier = new FakeNotifier();
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), new FakeClaudeClient(), notifier, new FakeVoice(), NewHistory());

        await pipeline.PlaySummaryAsync("s1");

        Assert.Equal(["Summarizing with Haiku…", "Generating voice…", "Speaking…"],
            notifier.StatusUpdates.Select(u => u.Status));
        Assert.Equal([0.25, 0.65, 0.9], notifier.StatusUpdates.Select(u => u.Fraction));
        Assert.Contains("s1", notifier.Removed);
    }

    [Fact]
    public async Task PlaySummaryAsync_LoadingModelPhase_ReportsLoadingStatus()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var notifier = new FakeNotifier();
        var voice = new FakeVoice
        {
            PhasesToEmit = [VoicePhase.LoadingModel, VoicePhase.Generating, VoicePhase.Speaking],
        };
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), new FakeClaudeClient(), notifier, voice, NewHistory());

        await pipeline.PlaySummaryAsync("s1");

        Assert.Contains(notifier.StatusUpdates, u => u.Status == "Loading voice model…" && u.Fraction == 0.45);
    }

    [Fact]
    public async Task PlaySummaryAsync_MessageMode_SkipsHaikuStage()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var notifier = new FakeNotifier();
        var config = new RaivenConfig { FinishedTurnVoice = "message" };
        var pipeline = new SummaryPipeline(registry, config, new FakeClaudeClient(), notifier, new FakeVoice(), NewHistory());

        await pipeline.PlaySummaryAsync("s1");

        Assert.Equal(["Generating voice…", "Speaking…"], notifier.StatusUpdates.Select(u => u.Status));
    }

    [Fact]
    public async Task PlaySummaryAsync_CacheHit_SkipsHaikuStage()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var notifier = new FakeNotifier();
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), new FakeClaudeClient(), notifier, new FakeVoice(), NewHistory());

        await pipeline.PlaySummaryAsync("s1");
        notifier.StatusUpdates.Clear();
        await pipeline.PlaySummaryAsync("s1"); // cached now

        Assert.Equal(["Generating voice…", "Speaking…"], notifier.StatusUpdates.Select(u => u.Status));
    }

    [Fact]
    public async Task PlaySummaryAsync_StatusOff_MakesNoStatusCalls()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var notifier = new FakeNotifier();
        var config = new RaivenConfig { ShowPlaybackStatus = false };
        var voice = new FakeVoice();
        var pipeline = new SummaryPipeline(registry, config, new FakeClaudeClient(), notifier, voice, NewHistory());

        await pipeline.PlaySummaryAsync("s1");

        Assert.Empty(notifier.StatusShown);
        Assert.Empty(notifier.StatusUpdates);
        Assert.Empty(notifier.Removed);
        Assert.Single(voice.Spoken); // playback itself still happens
    }

    [Fact]
    public async Task PlaySummaryAsync_DeadToastUserInitiated_ShowsFreshStatusToast()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var notifier = new FakeNotifier { ToastLive = false };
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), new FakeClaudeClient(), notifier, new FakeVoice(), NewHistory());

        await pipeline.PlaySummaryAsync("s1", userInitiated: true);

        Assert.Single(notifier.StatusShown);
    }

    [Fact]
    public async Task PlaySummaryAsync_DeadToastContinuation_NeverResurrectsToast()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var notifier = new FakeNotifier { ToastLive = false };
        var voice = new FakeVoice();
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), new FakeClaudeClient(), notifier, voice, NewHistory());

        await pipeline.PlaySummaryAsync("s1", userInitiated: false);

        Assert.Empty(notifier.StatusShown);
        Assert.Single(voice.Spoken); // playback still happens, just without a toast
    }

    [Fact]
    public async Task PlaySummaryAsync_LiveToast_IsReusedNotReShown()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var notifier = new FakeNotifier { ToastLive = true };
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), new FakeClaudeClient(), notifier, new FakeVoice(), NewHistory());

        await pipeline.PlaySummaryAsync("s1", userInitiated: false);

        Assert.Empty(notifier.StatusShown);
        Assert.NotEmpty(notifier.StatusUpdates); // stages ride the existing toast
    }

    [Fact]
    public async Task PlaySummaryAsync_ClaudeFails_StillRemovesToast()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var notifier = new FakeNotifier();
        var claude = new FakeClaudeClient { Throws = new InvalidOperationException("boom") };
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), claude, notifier, new FakeVoice(), NewHistory());

        await pipeline.PlaySummaryAsync("s1");

        Assert.Contains("s1", notifier.Removed);
        Assert.Contains(notifier.Errors, e => e.Contains("Couldn't get the summary"));
    }

    [Fact]
    public async Task PlayCachedAsync_SpeaksEntryWithStagesAndNoClaudeCall()
    {
        var notifier = new FakeNotifier();
        var claude = new FakeClaudeClient();
        var voice = new FakeVoice();
        var pipeline = new SummaryPipeline(
            new SessionRegistry(TimeSpan.FromHours(4)), new RaivenConfig(), claude, notifier, voice, NewHistory());
        var entry = new SummaryHistoryEntry(
            "sX", "Fix login bug", "RAIVEN", DateTimeOffset.Now, @"C:\t.jsonl", DateTime.UtcNow, "cached text");

        await pipeline.PlayCachedAsync(entry);

        Assert.Equal(["cached text"], voice.Spoken);
        Assert.Equal(0, claude.Calls);
        Assert.Single(notifier.StatusShown);
        Assert.Equal(["Generating voice…", "Speaking…"], notifier.StatusUpdates.Select(u => u.Status));
        Assert.Contains("sX", notifier.Removed);
    }

    [Fact]
    public async Task IsSpeaking_TrueWhileSpeechPending_FalseAfter()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var voice = new FakeVoice { Blocker = gate.Task };
        var pipeline = new SummaryPipeline(
            registry, new RaivenConfig(), new FakeClaudeClient(), new FakeNotifier(), voice, NewHistory());

        var play = pipeline.PlaySummaryAsync("s1");
        await voice.SpeakEntered.Task;

        Assert.True(pipeline.IsSpeaking("s1"));
        Assert.False(pipeline.IsSpeaking("other"));

        gate.SetResult();
        await play;

        Assert.False(pipeline.IsSpeaking("s1"));
    }
```

- [ ] **Step 3: Run tests to verify the new ones fail**

Run: `dotnet test tests/Raiven.Core.Tests`
Expected: build FAILS with CS1501/CS1061 (`PlaySummaryAsync` has no 2-arg overload, no `PlayCachedAsync`, no `IsSpeaking`).

- [ ] **Step 4: Replace `src/Raiven.Core/Summaries/SummaryPipeline.cs`**

```csharp
using Raiven.Core.Config;
using Raiven.Core.Logging;
using Raiven.Core.Notifications;
using Raiven.Core.Sessions;
using Raiven.Core.Transcripts;
using Raiven.Core.Voice;

namespace Raiven.Core.Summaries;

public sealed class SummaryPipeline(
    SessionRegistry registry,
    RaivenConfig config,
    IClaudeClient claude,
    INotifier notifier,
    IVoice voice,
    SummaryHistory history)
{
    private readonly SummaryService _summaries = new(claude);
    private string? _speakingSessionId;

    /// <summary>Whether this session's text is the one currently being generated/spoken.</summary>
    public bool IsSpeaking(string sessionId) => Volatile.Read(ref _speakingSessionId) == sessionId;

    public Task PlaySummaryAsync(string sessionId) => PlaySummaryAsync(sessionId, userInitiated: true);

    /// <summary>
    /// userInitiated: true for explicit clicks and replays (may show a fresh status
    /// toast), false for a countdown expiring on its own (may only update a toast
    /// that is still live - one the user hid or swiped away is never resurrected).
    /// </summary>
    public async Task PlaySummaryAsync(string sessionId, bool userInitiated)
    {
        try
        {
            if (!registry.TryGet(sessionId, DateTimeOffset.Now, out var info))
            {
                notifier.ShowError("That session's details are no longer available.");
                return;
            }

            var folder = Path.GetFileName(info.Cwd.TrimEnd('\\', '/'));
            if (folder.Length == 0) folder = info.Cwd;
            var headline = TranscriptReader.ReadFirstPrompt(info.TranscriptPath) ?? folder;

            EnsureStatusToast(sessionId, folder, headline, userInitiated);
            try
            {
                var lastWriteUtc = File.GetLastWriteTimeUtc(info.TranscriptPath);
                if (history.TryGetCached(sessionId, lastWriteUtc, out var cached))
                {
                    FileLog.Info($"Replaying cached summary for {sessionId}");
                    await SpeakWithPhasesAsync(sessionId, cached.SummaryText);
                    return;
                }

                var slice = TranscriptReader.ReadLastTurn(info.TranscriptPath, config.MaxTranscriptChars);

                string spokenText;
                if (config.FinishedTurnVoice.Equals("message", StringComparison.OrdinalIgnoreCase))
                {
                    spokenText = string.IsNullOrWhiteSpace(slice.AssistantText)
                        ? "Claude finished, but there was no message to read."
                        : SpeechText.LimitWords(slice.AssistantText, config.FinishedTurnWordLimit);
                    FileLog.Info($"Reading last message for {sessionId}");
                }
                else
                {
                    if (!config.FinishedTurnVoice.Equals("summary", StringComparison.OrdinalIgnoreCase))
                        FileLog.Info($"Unknown FinishedTurnVoice '{config.FinishedTurnVoice}'; using summary");
                    UpdateStatus(sessionId, "Summarizing with Haiku…", 0.25);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    spokenText = await _summaries.SummarizeAsync(slice, cts.Token);
                    FileLog.Info($"Summary for {sessionId}: {spokenText}");
                }

                history.Add(new SummaryHistoryEntry(
                    sessionId, headline, folder, DateTimeOffset.Now, info.TranscriptPath, lastWriteUtc, spokenText));

                await SpeakWithPhasesAsync(sessionId, spokenText);
            }
            finally
            {
                // A status toast may never outlive its playback run.
                if (config.ShowPlaybackStatus)
                    notifier.RemoveNotification(sessionId);
            }
        }
        catch (Exception ex)
        {
            FileLog.Error($"Summary failed for session {sessionId}", ex);
            notifier.ShowError("Couldn't get the summary - check the RAIVEN log for details.");
        }
    }

    /// <summary>Replay a cached history entry (tray menu): no Claude call, no history write.</summary>
    public async Task PlayCachedAsync(SummaryHistoryEntry entry)
    {
        try
        {
            EnsureStatusToast(entry.SessionId, entry.Folder, entry.Headline, userInitiated: true);
            try
            {
                await SpeakWithPhasesAsync(entry.SessionId, entry.SummaryText);
            }
            finally
            {
                if (config.ShowPlaybackStatus)
                    notifier.RemoveNotification(entry.SessionId);
            }
        }
        catch (Exception ex)
        {
            FileLog.Error($"Replay failed for session {entry.SessionId}", ex);
        }
    }

    private void EnsureStatusToast(string sessionId, string folder, string? headline, bool userInitiated)
    {
        if (!config.ShowPlaybackStatus) return;
        if (notifier.IsToastLive(sessionId)) return; // countdown toast still up: its bar carries the stages
        if (!userInitiated) return;                  // expiry after Hide/swipe: stay silent
        notifier.ShowPlaybackStatus(sessionId, folder, headline);
    }

    private void UpdateStatus(string sessionId, string status, double fraction)
    {
        if (config.ShowPlaybackStatus)
            notifier.UpdatePlaybackStatus(sessionId, status, fraction);
    }

    private async Task SpeakWithPhasesAsync(string sessionId, string text)
    {
        Volatile.Write(ref _speakingSessionId, sessionId);
        try
        {
            await voice.SpeakAsync(text, phase => UpdateStatus(sessionId, PhaseStatus(phase), PhaseFraction(phase)));
        }
        finally
        {
            // Only clear our own claim: a preempting session may have overwritten it.
            Interlocked.CompareExchange(ref _speakingSessionId, null, sessionId);
        }
    }

    private static string PhaseStatus(VoicePhase phase) => phase switch
    {
        VoicePhase.LoadingModel => "Loading voice model…",
        VoicePhase.Generating => "Generating voice…",
        _ => "Speaking…",
    };

    private static double PhaseFraction(VoicePhase phase) => phase switch
    {
        VoicePhase.LoadingModel => 0.45,
        VoicePhase.Generating => 0.65,
        _ => 0.9,
    };
}
```

- [ ] **Step 5: Run all tests to verify they pass**

Run: `dotnet test tests/Raiven.Core.Tests`
Expected: PASS — all new stage tests AND every pre-existing pipeline test (cache, history, message mode, error paths).

- [ ] **Step 6: Commit**

```bash
git add src/Raiven.Core/Summaries/SummaryPipeline.cs tests/Raiven.Core.Tests/SummaryPipelineTests.cs
git commit -m "feat: pipeline stage status updates and cached replay via status toast

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 6: `AudioKeepAlive` silent stream

**Files:**
- Create: `src/Raiven.App/AudioKeepAlive.cs`

Hardware-bound — no unit tests; deliverable is a clean build (manual verification in Task 8). NAudio namespaces come from KokoroSharp's transitive NAudio dependency; add **no** package references.

**Interfaces:**
- Consumes: `NAudio.Wave.WaveOutEvent`, `NAudio.Wave.SilenceProvider`, `NAudio.CoreAudioApi.MMDeviceEnumerator`, `NAudio.CoreAudioApi.Interfaces.IMMNotificationClient`.
- Produces (Task 7 depends on): `AudioKeepAlive` : `IDisposable` with `void Start()` and `void Stop()`, both idempotent.

- [ ] **Step 1: Create `src/Raiven.App/AudioKeepAlive.cs`**

```csharp
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using Raiven.Core.Logging;

namespace Raiven.App;

/// <summary>
/// Plays endless digital silence to keep the default output device - and a
/// Bluetooth audio link - awake, so speech doesn't lose its first second to
/// device wake-up. Follows default-device changes and recovers from device
/// errors. Everything is best-effort: this must never crash the tray app.
/// </summary>
public sealed class AudioKeepAlive : IDisposable
{
    private readonly Lock _lock = new();
    private MMDeviceEnumerator? _enumerator;
    private DeviceChangeListener? _listener;
    private WaveOutEvent? _output;
    private bool _shouldRun;
    private int _restartPending;

    public void Start()
    {
        lock (_lock)
        {
            _shouldRun = true;
            if (_enumerator is null)
            {
                try
                {
                    _enumerator = new MMDeviceEnumerator();
                    _listener = new DeviceChangeListener(this);
                    _enumerator.RegisterEndpointNotificationCallback(_listener);
                }
                catch (Exception ex)
                {
                    FileLog.Error("Audio keep-alive: device-change watcher unavailable; continuing without it", ex);
                }
            }
            StartStreamLocked();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _shouldRun = false;
            StopStreamLocked();
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_lock)
        {
            if (_enumerator is not null && _listener is not null)
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(_listener); }
                catch (Exception ex) { FileLog.Error("Audio keep-alive: unregister failed", ex); }
            }
            _enumerator?.Dispose();
            _enumerator = null;
            _listener = null;
        }
    }

    private void StartStreamLocked()
    {
        if (!_shouldRun || _output is not null) return;
        try
        {
            var output = new WaveOutEvent();
            output.Init(new SilenceProvider(new WaveFormat(44100, 16, 2)));
            output.PlaybackStopped += OnPlaybackStopped;
            output.Play();
            _output = output;
            FileLog.Info("Audio keep-alive stream started.");
        }
        catch (Exception ex)
        {
            FileLog.Error("Audio keep-alive could not start (will retry on the next device change)", ex);
            _output = null;
        }
    }

    private void StopStreamLocked()
    {
        if (_output is null) return;
        try
        {
            _output.PlaybackStopped -= OnPlaybackStopped; // manual stop must not trigger the restart path
            _output.Stop();
            _output.Dispose();
        }
        catch (Exception ex)
        {
            FileLog.Error("Audio keep-alive stop failed", ex);
        }
        _output = null;
        FileLog.Info("Audio keep-alive stream stopped.");
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // The device vanished or errored; retry shortly on whatever is default by then.
        if (e.Exception is not null)
            FileLog.Error("Audio keep-alive playback stopped unexpectedly", e.Exception);
        RestartSoon();
    }

    // Debounced: OnDefaultDeviceChanged fires once per role, and errors can burst.
    private void RestartSoon()
    {
        if (Interlocked.Exchange(ref _restartPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Interlocked.Exchange(ref _restartPending, 0);
            lock (_lock)
            {
                if (!_shouldRun) return;
                StopStreamLocked();
                StartStreamLocked();
            }
        });
    }

    private sealed class DeviceChangeListener(AudioKeepAlive owner) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            // WaveOutEvent binds the device at Init and never migrates; restart on the
            // new default so the keep-alive follows e.g. freshly connected BT headphones.
            if (flow == DataFlow.Render && role == Role.Multimedia)
                owner.RestartSoon();
        }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Raiven.App`
Expected: build succeeds with no new package references (verify `src/Raiven.App/Raiven.App.csproj` is untouched by `git status`).

- [ ] **Step 3: Commit**

```bash
git add src/Raiven.App/AudioKeepAlive.cs
git commit -m "feat: AudioKeepAlive silent stream keeps Bluetooth audio awake

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 7: Wire everything into `Program` and the tray Settings menu

**Files:**
- Modify: `src/Raiven.App/Program.cs` (`RunTray`)
- Modify: `src/Raiven.App/TrayContext.cs` (constructor + `BuildSettingsMenu`)

**Interfaces:**
- Consumes: Task 5 `PlaySummaryAsync(id, userInitiated)` / `PlayCachedAsync(entry)` / `IsSpeaking(id)`, Task 4 events + `RemoveLiveNotifications()`, Task 6 `AudioKeepAlive`, Task 3 `voice.Stop()`, existing `AutoPlayCountdown.Cancel(id)` → `bool`.
- Produces: `TrayContext` constructor gains a final optional parameter `Action<bool>? onKeepAudioAliveChanged = null`.

- [ ] **Step 1: Add the two Settings checkboxes in `src/Raiven.App/TrayContext.cs`**

Change the constructor signature (add the new final parameter):

```csharp
    public TrayContext(
        AppState state,
        RaivenConfig config,
        Action saveConfig,
        SummaryHistory history,
        Action<SummaryHistoryEntry> replaySummary,
        Action testToast,
        Action testVoice,
        Action<bool>? onPauseChanged = null,
        Action<bool>? onKeepAudioAliveChanged = null)
```

Change the `BuildSettingsMenu` call inside the constructor to pass it through:

```csharp
        var settingsItem = BuildSettingsMenu(config, saveConfig, onKeepAudioAliveChanged);
```

Replace the `BuildSettingsMenu` method signature and add the checkboxes before `return settings;`:

```csharp
    private static ToolStripMenuItem BuildSettingsMenu(
        RaivenConfig config, Action saveConfig, Action<bool>? onKeepAudioAliveChanged)
```

and after the `settings.DropDownItems.Add(questionVoice);` line:

```csharp
        var keepAlive = new ToolStripMenuItem("Keep audio device awake")
        {
            CheckOnClick = true,
            Checked = config.KeepAudioAlive,
        };
        keepAlive.CheckedChanged += (_, _) =>
        {
            config.KeepAudioAlive = keepAlive.Checked;
            saveConfig();
            onKeepAudioAliveChanged?.Invoke(keepAlive.Checked);
        };

        var showStatus = new ToolStripMenuItem("Show playback status")
        {
            CheckOnClick = true,
            Checked = config.ShowPlaybackStatus,
        };
        showStatus.CheckedChanged += (_, _) => { config.ShowPlaybackStatus = showStatus.Checked; saveConfig(); };

        settings.DropDownItems.Add(new ToolStripSeparator());
        settings.DropDownItems.Add(keepAlive);
        settings.DropDownItems.Add(showStatus);
```

- [ ] **Step 2: Wire `RunTray` in `src/Raiven.App/Program.cs`**

After `var voice = new VoiceService(config);` add:

```csharp
        using var keepAlive = new AudioKeepAlive();
        if (config.KeepAudioAlive) keepAlive.Start();
```

Replace `PlayOnce` and the countdown/notifier event handlers (the block from `async Task PlayOnce...` through the `notifier.AbortRequested += ...;` handler) with:

```csharp
        var playing = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        async Task PlayOnce(string id, bool userInitiated)
        {
            if (!playing.TryAdd(id, 0))
            {
                FileLog.Info($"Summary already in flight for {id}; ignoring duplicate trigger.");
                return;
            }
            try { await pipeline.PlaySummaryAsync(id, userInitiated); }
            finally { playing.TryRemove(id, out _); }
        }

        countdown.Progress += (id, fraction) => notifier.UpdateCountdownProgress(id, fraction);
        countdown.Expired += id =>
        {
            if (state.Paused) return;
            // With the status toast on, the countdown toast stays up and its progress
            // bar carries the pipeline stages; the pipeline removes it when speech ends.
            if (!config.ShowPlaybackStatus) notifier.RemoveNotification(id);
            _ = Task.Run(() => PlayOnce(id, userInitiated: false));
        };
        notifier.PlaySummaryRequested += id =>
        {
            countdown.Cancel(id);
            if (!config.ShowPlaybackStatus) notifier.RemoveNotification(id);
            _ = Task.Run(() => PlayOnce(id, userInitiated: true));
        };
        notifier.AbortRequested += id =>
        {
            countdown.Cancel(id);
            notifier.RemoveNotification(id);
            FileLog.Info($"Auto-play aborted for {id}");
        };
        notifier.StopRequested += id =>
        {
            var hadCountdown = countdown.Cancel(id);
            notifier.RemoveNotification(id);
            // Countdown-phase Stop is an abort; playback-phase Stop halts the voice -
            // but only when THIS session is the one speaking, so stopping session B's
            // toast can never kill session A's speech.
            if (!hadCountdown && pipeline.IsSpeaking(id))
                voice.Stop();
            FileLog.Info($"Stop requested for {id} (countdown canceled: {hadCountdown})");
        };
        notifier.HideRequested += id =>
        {
            notifier.RemoveNotification(id);
            FileLog.Info($"Status toast hidden for {id}");
        };
```

Update the `Application.Run(new TrayContext(...))` call:

```csharp
            Application.Run(new TrayContext(
                state,
                config,
                saveConfig: () => config.Save(AppPaths.ConfigFile),
                history,
                replaySummary: entry => Task.Run(() => pipeline.PlayCachedAsync(entry)),
                testToast: () => { ChimePlayer.Play(config); notifier.ShowFinished("test-session-001", "RAIVEN", "This is a test notification"); },
                testVoice: () => Task.Run(() => voice.SpeakAsync("RAIVEN online. All systems operational.")),
                onPauseChanged: paused => { if (paused) countdown.CancelAll(); },
                onKeepAudioAliveChanged: on => { if (on) keepAlive.Start(); else keepAlive.Stop(); }));
```

And in the `finally` block, before `listener.DisposeAsync()...`, add:

```csharp
            notifier.RemoveLiveNotifications();
```

- [ ] **Step 3: Build and run all tests**

Run: `dotnet build src/Raiven.App` then `dotnet test tests/Raiven.Core.Tests`
Expected: build succeeds, all tests PASS.

- [ ] **Step 4: Smoke test the flow end to end**

Run in one terminal: `dotnet run --project src/Raiven.App`
Run in another: `powershell -File scripts\send-test-event.ps1`

Expected: chime + countdown toast with **Play now / Stop / Hide** buttons; after the 5 s countdown the same toast's status walks `Summarizing with Haiku…` → (`Loading voice model…` on a cold run) → `Generating voice…` → `Speaking…`; the toast disappears when the voice finishes. Right-click tray → Settings shows **Keep audio device awake** (unchecked) and **Show playback status** (checked). Recent summaries entry reads like `04.07.2026 14:32 — RAIVEN — <headline>`. Quit the app.

- [ ] **Step 5: Commit**

```bash
git add src/Raiven.App/Program.cs src/Raiven.App/TrayContext.cs
git commit -m "feat: wire keep-alive and playback status toast into tray and event flow

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 8: README and manual verification

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Add the config rows**

In the Configuration table, after the `QuestionWordLimit` row, add:

```markdown
| `KeepAudioAlive` | `false` | Plays a continuous silent stream so the audio device - and a Bluetooth link - never sleeps, preventing the first ~second of speech being swallowed. Toggled by tray **Settings -> Keep audio device awake**. Costs Bluetooth-headphone battery while on |
| `ShowPlaybackStatus` | `true` | Finished-turn and replay toasts stay on screen through the whole pipeline and show what RAIVEN is doing (Summarizing with Haiku / Generating voice / Speaking), then dismiss when speech ends. `false` restores the old disappear-at-play behavior |
```

- [ ] **Step 2: Document the status toast and keep-alive behavior**

After the "Auto-play, Play now/Abort, and Recent summaries" section's existing paragraphs, add:

```markdown
### Playback status and the Stop/Hide buttons

With `ShowPlaybackStatus` on (the default), the finished-turn toast stays on
screen for the whole playback run: its progress bar text walks through
"Summarizing with Haiku", "Loading voice model" (first run only - this makes
the one-time ~320 MB Kokoro download visible), "Generating voice", and
"Speaking", and the toast dismisses itself when the voice finishes. The
buttons are **Play now**, **Stop** (during the countdown: cancel auto-play;
during playback: stop the voice), and **Hide** (dismiss the toast - the
countdown and playback carry on). Swiping the toast away counts as Hide:
RAIVEN keeps working but won't bring the toast back. Replaying from **Recent
summaries** shows the same status toast, muted. Recent-summaries entries are
labelled `04.07.2026 14:32 — project — chat topic`.

### Bluetooth: first second of speech cut off

Bluetooth devices drop their audio link when idle and take up to a second to
re-open it - swallowing the start of RAIVEN's speech. Enable tray
**Settings -> Keep audio device awake** (`KeepAudioAlive`) to play a
continuous silent stream that keeps the link open. It follows the default
output device if you connect headphones later. Trade-off: the headphones
never auto-sleep, which costs battery. Note the stream is digital silence;
it keeps the *link* awake, but a few speaker models additionally power-save
on silent *content* - if yours still clips, open an issue.
```

- [ ] **Step 3: Update the tray menu line**

Replace the tray menu listing under "### Tray menu":

```markdown
Pause notifications * Recent summaries * Settings * Test notification *
Test voice * Start with Windows * Open data folder * Quit
```

- [ ] **Step 4: Full manual verification pass**

With `dotnet run --project src/Raiven.App` running, verify each; use `powershell -File scripts\send-test-event.ps1` to trigger events:

1. Countdown toast shows Play now / Stop / Hide; stages walk; toast auto-dismisses after speech.
2. **Play now** mid-countdown: toast is dismissed by the click, a muted status toast reappears with Stop / Hide and stages.
3. **Hide** mid-countdown: toast gone; speech still auto-plays; no toast comes back.
4. **Stop** mid-countdown: nothing plays. **Stop** mid-speech: voice halts, toast gone.
5. Replay from **Recent summaries**: muted status toast, `Generating voice…` → `Speaking…`; label reads `dd.MM.yyyy HH:mm — folder — topic`.
6. Settings → **Show playback status** off: toast vanishes at play time (old behavior).
7. Settings → **Keep audio device awake** on: log shows `Audio keep-alive stream started.`; off: `... stopped.`
8. Bluetooth (hardware permitting): keep-alive on, let headphones idle a minute, trigger a test event - the first word is not swallowed; switch default output device and confirm a restart pair in the log.
9. `dotnet test tests/Raiven.Core.Tests` - everything green.

- [ ] **Step 5: Commit**

```bash
git add README.md
git commit -m "docs: README for keep-alive, playback status toast, and summary labels

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```
