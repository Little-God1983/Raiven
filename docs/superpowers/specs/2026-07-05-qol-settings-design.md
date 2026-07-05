# RAIVEN quality-of-life settings — design

Date: 2026-07-05

Four small, independent improvements: an optional (off-by-default) auto-play
delay, a Test-notification path that actually speaks, date-stamped daily logs
with count-based retention, and a configurable Recent-summaries history size.

## 1. Optional auto-play delay (default off)

**Problem.** The built-in countdown before a summary auto-plays is meant as a
reaction window, but in practice it is more annoying than useful.

**Config.** `AutoPlayDelaySeconds` default changes `5 → 0`. Valid range is
clamped to `0–60` (today it is clamped to a minimum of 1 with no maximum).

**Behavior.**
- **Delay = 0 (new default):** a finished turn shows the toast and speaks the
  summary immediately — no countdown bar, no Play now / Abort. The user can
  still **Stop** mid-speech and replay from **Recent summaries**.
- **Delay 1–60:** the current countdown behavior, unchanged.

**Implementation.** The finished-turn branch in `Program.cs` (`HandleEvent`,
the `Stop` event) and the Test-notification handler share one helper that
decides between: immediate play (delay 0), countdown (delay 1–60), or
click-to-play (`AutoPlaySummary: false`). For delay 0, RAIVEN shows the
finished-turn toast — a playback-status toast when `ShowPlaybackStatus` is on,
otherwise the standard finished toast — and begins playback immediately; a
status toast walks the stages and dismisses at the end as it does today.

**Tray control.** New **Settings → Auto-play delay** radio submenu:
`Off (0s) · 1s · 3s · 5s · 10s · 30s`. Selecting one saves to `config.json`
and applies to the next finished turn. A hand-edited value outside the presets
(e.g. `45`) is honored and simply shows no radio item checked — matching how
the existing voice-mode radios treat unknown values.

## 2. Test notification speaks a canned summary

**Problem.** The **Test notification** toast is shown for `test-session-001`,
which has no registry entry, so clicking **Play** fails with *"That session's
details are no longer available."*

**Fix.** The Test-notification menu item drives the real finished-turn path
(so it also exercises the configured delay), backed by an in-memory **canned
summary** with text `"This is a summary test by RAIVEN."`. The pipeline
resolves this canned text without a registry entry, Claude call, or transcript
read, and **without writing to `history.json`** — so it does not appear under
Recent summaries. Result: chime → toast → (immediate or countdown per the
delay) → the voice speaks the canned line.

**Implementation.** `SummaryPipeline` gains a small canned-text registry
(`RegisterCanned(sessionId, folder, headline, text)` storing into a
`ConcurrentDictionary`). `PlaySummaryAsync` checks it first, before
`registry.TryGet`, and when present reuses the existing status-toast +
`SpeakWithPhasesAsync` path (no history write). All existing routing
(countdown expiry, Play now) already flows through `PlaySummaryAsync`, so the
canned path needs no special handling in the notifier.

## 3. Date-stamped daily logs, keep last 30

**Problem.** A single ever-growing `logs/raiven.log`.

**Filenames.** `%APPDATA%\Raiven\logs\raiven-log-YYYYMMDD.log` (e.g.
`raiven-log-20260705.log`), one file per day. `FileLog` computes the target
path per write from the current date, so an instance left running rolls to a
new file automatically at midnight.

**Retention.** Count-based, **not** age-based: keep the newest **30** daily log
files and hard-delete older ones. This is deliberately not "older than N days" —
a 30-day gap in usage must never wipe the history; retention counts days RAIVEN
actually ran. Pruning runs once at startup: enumerate `raiven-log-*.log`, order
by name descending (`YYYYMMDD` sorts chronologically), keep the first 30, delete
the rest. All failures are best-effort and never take the app down.

**Implementation.** `FileLog.Configure` takes a **log directory** instead of a
fixed file path; a new `FileLog.PruneOldLogs(keep: 30)` performs the sweep,
called once at startup. `AppPaths` exposes `LogDir`; the startup error dialog
points at the logs folder. Any pre-existing `raiven.log` is left in place
(harmless; it does not match the dated pattern and is never pruned).

## 4. Configurable Recent-summaries history size (default 10)

**Config.** New `HistorySize`, default `10`, clamped `1–50`. Replaces the
hardcoded `capacity = 5` in `SummaryHistory.Load`.

**Tray control.** New **Settings → Recent summaries kept** radio submenu:
`5 · 10 · 20`. Changing it applies live: `SummaryHistory` gains a settable
capacity that trims immediately when lowered, persists the trim, and saves the
new value to `config.json`.

## Config summary

| Key | Old | New default | Range |
|---|---|---|---|
| `AutoPlayDelaySeconds` | `5` (min 1) | `0` | `0–60` |
| `HistorySize` | — (hardcoded 5) | `10` | `1–50` |

## Testing

- **RaivenConfig:** new defaults (`AutoPlayDelaySeconds = 0`, `HistorySize = 10`).
- **FileLog:** directory-based `Configure`; writes land in a
  `raiven-log-YYYYMMDD.log` file; `PruneOldLogs` keeps the newest N and deletes
  the rest.
- **SummaryHistory:** lowering capacity trims and persists; default is 10.
- **SummaryPipeline:** a registered canned session speaks its text and writes
  nothing to history.
- **UI layer** (`Program.cs` 0-delay branch, `TrayContext` submenus): manual
  verification, no unit tests.

## Docs

Update the README config table (both new/changed keys), the "last 5" Recent
summaries line, and the logs-path line (`raiven-log-YYYYMMDD.log`, keep 30).
