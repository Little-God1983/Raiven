# Audio keep-alive, live playback status toast, and summary menu labels

Date: 2026-07-04
Status: Approved

## Goal

Three quality-of-life improvements from daily Bluetooth-headphone use:

1. **Audio keep-alive**: Bluetooth devices drop their audio link when idle;
   re-establishing it swallows RAIVEN's first ~second of speech. An optional
   continuous silent stream keeps the output device (and the BT link) awake.
2. **Recent summaries labels**: entries become
   `04.07.2026 14:32 — RAIVEN — Fix login bug`
   (datetime — project folder — chat topic) instead of topic-first.
3. **Live playback status** (two requests merged): the finished-turn toast
   stays on screen for the whole pipeline and shows what RAIVEN is doing -
   `Summarizing with Haiku…` → `Loading voice model…` → `Generating voice…`
   → `Speaking…` - then dismisses itself when speech ends. Today the toast
   vanishes at play time and nothing indicates whether Haiku or Kokoro is
   still working.

## 1. Audio keep-alive

New `AudioKeepAlive` class (Raiven.App, `IDisposable`), built on NAudio -
already available transitively through KokoroSharp, no new package.

- `Start()`: plays endless digital silence (`WaveOutEvent` +
  `SilenceProvider`, 44.1 kHz 16-bit stereo) on the default output device.
  The open stream keeps the Windows audio endpoint and the Bluetooth A2DP
  link active, so speech starts without the wake-up gap. `Stop()` tears the
  stream down. Both idempotent; `Dispose` stops.
- **Default-device change** (e.g. BT headphones connect after RAIVEN
  started): an `MMDeviceEnumerator` endpoint-notification callback
  (`IMMNotificationClient.OnDefaultDeviceChanged`, Render/Multimedia,
  debounced - the callback fires once per role) restarts the stream so the
  keep-alive follows the new device.
- **Device removal / playback error**: `WaveOutEvent.PlaybackStopped` with an
  error triggers a delayed restart (a few seconds); failures log and retry on
  the next trigger rather than looping hot. Nothing here may crash the app -
  all best-effort with FileLog, like ChimePlayer.
- Wiring: created in `Program.RunTray`; `Start()` only when
  `KeepAudioAlive` is true. The tray Settings checkbox calls Start/Stop
  immediately (no restart needed) and saves config. Disposed on shutdown.
- Known limitation (documented in README): the stream is digital zeros. That
  keeps the *link* open - which is what swallows the first second - but a few
  speaker models additionally power-save on silent *content*. If such a
  device turns up, a future "inaudible fluctuation" mode is the fix; not
  built now.

## 2. Recent summaries menu labels

- `SummaryHistoryEntry` gains a `MenuLabel` computed property (Raiven.Core,
  pure, testable):
  `$"{GeneratedAt.LocalDateTime:dd.MM.yyyy HH:mm} — {Folder} — {Headline}"`.
  The format is fixed (not culture-dependent) - it is the rendering the user
  approved.
- `TrayContext.RebuildRecentSummaries` uses `entry.MenuLabel` and keeps the
  existing `&` → `&&` mnemonic escaping (WinForms concern, stays in the App
  layer).

## 3. Live playback status toast

### Principle: one toast, one lifecycle, no re-popping

The countdown toast already carries a progress bar with bindable value and
status text; progress-data updates (`UpdateAsync`) change it **without**
re-showing the toast. Stage feedback therefore rides the progress bar of the
toast that is already on screen. The `Reminder` scenario keeps the toast on
screen until dismissed (Windows otherwise slides toasts away after ~25 s),
which is what lets it "stay open as long as RAIVEN speaks".

### Toast lifecycle when `ShowPlaybackStatus` is on (default)

- `ShowFinishedCountdown` builds the toast with
  `SetScenario(Reminder)` and buttons **Play now · Stop · Hide** (instead of
  Play now · Abort).
- Countdown ticks fill the bar as today. When playback triggers (expiry or
  Play now), the same bar walks the stages via `UpdateAsync` - fixed
  fractions, no toast re-show:

  | Stage | Status text | Fraction |
  |---|---|---|
  | Haiku call (summary mode only) | `Summarizing with Haiku…` | 0.25 |
  | First-ever Kokoro init (incl. one-time ~320 MB download) | `Loading voice model…` | 0.45 |
  | Kokoro synthesis | `Generating voice…` | 0.65 |
  | Audio playing | `Speaking…` | 0.9 |

  (After the countdown reaches full, the bar drops back to the first stage
  fraction with the new label - accepted, it visibly marks the phase change.)
- When speech completes, is preempted, or is stopped, the toast is removed.
  On any pipeline failure the toast is removed before the existing error
  toast shows - a status toast may never outlive its pipeline run.
- **Buttons**: *Stop* during the countdown cancels auto-play (today's Abort);
  during playback it stops the voice and dismisses the toast. *Hide*
  dismisses the toast and nothing else - countdown and playback continue.
  Swiping the toast away counts as Hide.
- **Paths without a live toast** get a fresh status toast (same tag =
  sessionId, `Reminder` scenario, **Stop · Hide** buttons, `MuteAudio()` - no
  second ding): click-to-play (`AutoPlaySummary: false` - Windows dismisses
  the clicked toast), the re-show after a Play-now click (also dismissed by
  Windows), and **Recent-summaries replays** (which still run Kokoro
  synthesis and can hit the model-load delay; replays route through the
  pipeline instead of calling the voice directly).
- Toggle off (`ShowPlaybackStatus: false`): exactly today's behavior - toast
  removed at play time, background work invisible, Play now · Abort buttons.

### Toast liveness tracking (AppSdkNotifier)

The AppNotifications API has no dismissed-event, so liveness is tracked
optimistically: shown → live; any activation that dismisses (Play now, Stop,
Hide, legacy actions) or `RemoveNotification` → dead; a progress update
returning `AppNotificationNotFoundError` (user swiped it away) → dead and no
further updates.

Whether a dead tag gets a fresh status toast depends on the trigger:
**user-initiated** triggers (Play now click, click-to-play click, a
Recent-summaries replay) show one; **continuation** triggers (the countdown
expiring on its own) never do - they update the toast if it is still live and
otherwise stay silent. So a Play-now click gets its status back, while a
toast the user swiped away or hid keeps auto-play working without ever being
resurrected. A later finished turn for the same session starts a new toast
lifecycle as usual.

### Interface changes

- `IVoice` (breaking, all call sites migrate):

  ```csharp
  public enum VoicePhase { LoadingModel, Generating, Speaking }
  public interface IVoice
  {
      // Completes when speech finishes, is preempted by a newer Speak,
      // or is stopped. onPhase reports pipeline stages as they begin.
      Task SpeakAsync(string text, Action<VoicePhase>? onPhase = null);
      void Stop(); // stop current playback; no-op when idle
  }
  ```

  `VoiceService` implements it on KokoroSharp's `SynthesisHandle` callbacks:
  `OnSpeechStarted` → `Speaking`; `OnSpeechCompleted` / `OnSpeechCanceled`
  complete the task. A defensive cap (60 s + one second per two words)
  completes the task even if a callback never fires - a stuck toast is worse
  than a slightly early cleanup. `Stop()` = `KokoroTTS.StopPlayback()`.
  `LoadingModel` is reported only on the first call (before model init).
  QuestionPipeline and the tray test-voice keep fire-and-forget semantics.

- `INotifier` gains:

  ```csharp
  event Action<string>? StopRequested;  // stop speech and dismiss
  event Action<string>? HideRequested;  // dismiss only, work continues
  void ShowPlaybackStatus(string sessionId, string folderName, string? headline);
  void UpdatePlaybackStatus(string sessionId, string status, double fraction);
  ```

  `AppSdkNotifier` receives the shared `RaivenConfig` so
  `ShowFinishedCountdown` can pick the button set / scenario by the current
  `ShowPlaybackStatus` value.

- `AutoPlayCountdown.Cancel(string)` returns `bool` (whether an active
  countdown was actually canceled) so Program can tell a countdown-phase Stop
  from a playback-phase Stop.

- `SummaryPipeline` drives the stages (it already owns config, notifier,
  voice, history): ensures a status toast exists when enabled, emits stage
  updates (skipping the Haiku stage in `message` mode and on cache hits),
  awaits `SpeakAsync`, and always removes the toast in a `finally`. New
  `PlayCachedAsync(SummaryHistoryEntry)` for tray replays (stages:
  `Generating voice…` → `Speaking…`, plus `Loading voice model…` when the
  replay is the first speech of the run). It tracks the currently-speaking
  session id so Program's Stop handler only stops the voice when the Stop
  came from the session that is actually speaking - a Stop on session B's
  countdown must not kill session A's speech.

- `Program.RunTray` wiring: `StopRequested` → cancel countdown; if nothing
  was canceled and the session is the one speaking, `voice.Stop()`; remove
  toast. `HideRequested` → remove toast only. The pre-play
  `RemoveNotification` calls happen only when `ShowPlaybackStatus` is off.
  On shutdown, best-effort removal of any live status toasts.

## 4. Configuration and docs

| Key | Type | Default | Meaning |
|---|---|---|---|
| `KeepAudioAlive` | bool | `false` | Continuous silent stream keeps the audio device / Bluetooth link awake while RAIVEN runs. |
| `ShowPlaybackStatus` | bool | `true` | Finished-turn toast stays open through the pipeline showing stage status; off = today's behavior. |

Tray Settings submenu gains two checkboxes (same pattern as existing ones,
saved immediately): **Keep audio device awake** (off by default) and
**Show playback status** (on by default).

README: config table rows, Settings list, Recent-summaries format, status
toast behavior (incl. Stop/Hide and swipe-=-Hide), and a Bluetooth
troubleshooting note (first-second swallowed → enable keep-alive; headphone
battery trade-off; digital-zeros limitation).

## 5. Error handling

- Keep-alive: all failures best-effort + FileLog; device errors retry
  delayed; the feature degrades to today's behavior, never crashes.
- Status toast: every pipeline exit path (success, preemption, Stop,
  exception) removes the toast; update failures mark the toast dead and are
  otherwise ignored. Toast display failures already never throw past
  AppSdkNotifier.
- `SpeakAsync` can never hang its awaiter (defensive completion cap above).

## 6. Testing

Unit tests (Raiven.Core.Tests):

- `SummaryHistoryEntry.MenuLabel`: exact `dd.MM.yyyy HH:mm — folder — topic`
  rendering.
- `SummaryPipeline` stage sequences with a recording fake notifier and a fake
  phase-emitting voice: summary mode emits all stages in order then removes;
  `message` mode and cache hits skip the Haiku stage; `ShowPlaybackStatus:
  false` emits no status calls; failures remove the toast and show the error;
  the toast is removed on preempted/canceled speech too.
- `PlayCachedAsync`: replay stages + no Claude call.
- `AutoPlayCountdown.Cancel` return value: true only when a countdown was
  active.
- `RaivenConfig`: defaults and round-trip for the two new keys.
- Existing `IVoice` fakes migrate from `Speak` to `SpeakAsync`.

Manual verification (hardware/UI-bound):

- Keep-alive: enable toggle with BT headphones → first word no longer
  swallowed; toggle off → stream stops (audio endpoint goes idle); connect
  BT device while RAIVEN runs → keep-alive follows it.
- Status toast: full stage walk on a real finished turn; `Loading voice
  model…` visible after deleting `%APPDATA%\Raiven\kokoro.onnx`; Stop during
  countdown vs during speech; Hide leaves audio running; swipe-away stops
  updates without resurrection; replay from Recent summaries shows the small
  status toast; toggle off restores today's flow.
