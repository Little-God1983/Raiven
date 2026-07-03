# Auto-play summary, richer toasts, and summary history

Date: 2026-07-03
Status: Approved

## Goal

Three user-facing improvements to RAIVEN's finished-turn flow:

1. **Auto-play summary** - the toast counts down 5 seconds with a visible
   filling progress bar; if the user does nothing, the summary generates and
   speaks automatically. Two buttons: "Play now" and "Abort".
2. **Richer toast** - the raven logo appears inside the popup body, and the
   chat's headline (first user prompt) appears as the title line.
3. **Summary history** - the last 5 generated summaries are cached (persisted
   across restarts) and replayable from the tray menu without calling Claude
   Haiku again.

## 1. Auto-play countdown toast

### Behavior

- On a Stop event (and not paused), RAIVEN shows a toast containing:
  - Title: chat headline (see section 3), fallback "Claude finished in {folder}".
  - Second line: "Claude finished in {folder}" (when headline is shown).
  - A progress bar that fills from 0% to 100% over `AutoPlayDelaySeconds`.
  - Buttons: **Play now** and **Abort**.
- Progress bar updates via `AppNotificationManager.Default.UpdateAsync` with
  `Tag = sessionId`, sequence-numbered, roughly every 500 ms.
- When the bar completes without user action, the summary pipeline runs and
  the summary is spoken (identical path to clicking "Play now").
- **Play now** or clicking the toast body: cancel countdown, play immediately.
- **Abort**: cancel countdown, remove the notification
  (`RemoveByTagAsync(sessionId)`), play nothing.
- Countdowns are per-session (keyed by sessionId); concurrent sessions each
  get an independent countdown and toast (distinct tags).
- If a new Stop event arrives for a session that already has a countdown
  running, the old countdown is cancelled and replaced.

### Configuration

New `RaivenConfig` keys:

- `AutoPlaySummary` (bool, default `true`) - when `false`, the toast is the
  current static one ("Play summary" button, no progress bar, no countdown).
- `AutoPlayDelaySeconds` (int, default `5`).

### Components

- `AutoPlayCountdown` (Raiven.App or Core; owns no UI): manages per-session
  `CancellationTokenSource` + timer, raises a callback on expiry, exposes
  `Start(sessionId)`, `Cancel(sessionId)`, `CancelAll()`. Progress reporting
  is a callback so the toast-update logic stays in `AppSdkNotifier`.
- `AppSdkNotifier` gains:
  - `ShowFinishedWithCountdown(sessionId, headline, folder, totalSeconds)`.
  - `UpdateProgress(sessionId, fraction)` and `Remove(sessionId)`.
  - New activation actions: `playNow`, `abort` (in addition to existing
    `playSummary` for the static/no-autoplay toast).
- `INotifier` interface extended accordingly (App SDK implementation is the
  only concrete one; keep the interface minimal - the countdown logic itself
  lives outside the notifier).

### Races and edge cases

- Abort arriving after expiry already fired: countdown's CTS cancel is a
  no-op; if generation already started it completes (acceptable).
- Play-now and expiry racing: guarded so the pipeline runs at most once per
  countdown (e.g. `Interlocked.Exchange` flag inside `AutoPlayCountdown`).
- Toast dismissed by user swiping it away: Windows does not notify reliably;
  the countdown continues and auto-plays. This is accepted v1 behavior.
- Failures updating the progress bar are logged and ignored (countdown still
  completes).

## 2. App icon in the popup body

- `AppNotificationBuilder.SetAppLogoOverride(new Uri(file:///...png))`
  pointing at a raven logo PNG shipped to the output directory (reuse
  existing logo artwork, copied via the csproj as Content).
- Applied to finished-turn toasts (both countdown and static variants).
- If the file is missing at runtime, skip the override (toast still shows).

## 3. Chat headline in the popup

- New `TranscriptReader.ReadFirstPrompt(transcriptPath, maxChars = 60)`:
  scans the transcript JSONL from the top, returns the first user prompt
  (same extraction rules as `TryExtractPrompt` - skips meta and tool_result
  lines), truncated to ~60 chars with an ellipsis. Early-exits after the
  first match; does not read the whole file.
- Called at Stop-event time in the event handler; failures (missing file, no
  prompt yet) fall back to the current "Claude finished in {folder}" title.
- The AI-generated chat titles shown in the VSCode UI are not stored anywhere
  RAIVEN can read; the first user prompt is the chosen proxy (user-approved).

## 4. Summary history (last 5, no regeneration)

### Storage

- `SummaryHistory` (Raiven.Core): holds up to 5 entries, newest first.
  Entry: `SessionId`, `Headline`, `Folder`, `GeneratedAt`,
  `TranscriptPath`, `TranscriptLastWriteUtc`, `SummaryText`.
- Persisted as JSON to RAIVEN's app-data folder (alongside existing config /
  log paths in `AppPaths`), written after each new entry. Corrupt or missing
  file loads as empty history. Thread-safe (lock around mutation + save).

### Replay UI

- Tray menu gains a **"Recent summaries"** submenu, rebuilt when the menu
  opens, one item per entry: "{headline} ({folder}, {time})".
- Clicking an item speaks `SummaryText` via `IVoice` directly - no transcript
  read, no Claude call.
- Empty history shows a disabled "(none yet)" item.

### Cache-before-generate

- `SummaryPipeline.PlaySummaryAsync` first checks history for an entry with
  the same `SessionId` whose `TranscriptLastWriteUtc` matches the transcript
  file's current last-write time; on a hit it speaks the cached text and
  skips Haiku. On a miss it generates, speaks, and records a new entry.

## Error handling

- Any countdown/toast failure degrades to the existing static toast.
- History persistence failures log and continue (history is best-effort).
- Existing pipeline error handling (error toast + log) unchanged.

## Testing

- Unit tests (Raiven.Core / countdown logic):
  - `AutoPlayCountdown`: expiry fires once; abort prevents fire; play-now
    prevents expiry double-fire; per-session independence; restart replaces.
  - `TranscriptReader.ReadFirstPrompt`: normal, meta-skip, tool_result-skip,
    empty file, truncation.
  - `SummaryHistory`: trims to 5, newest-first, persistence round-trip,
    corrupt-file recovery, cache-hit matching on transcript timestamp.
- Manual verification via `--test-toast` (extended to show the countdown
  variant) for visuals: logo, headline, progress fill, both buttons.
