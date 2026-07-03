# Question notifications and configurable notification behavior

Date: 2026-07-03
Status: Approved

## Goal

RAIVEN currently notifies only when a turn finishes (Stop hook). Users get no
notification when Claude Code needs them - a permission prompt, an unanswered
question, waiting for input. This feature:

1. Notifies (chime + toast + voice) when Claude Code has a question.
2. Makes each notification type configurable: on/off, and what the voice
   does - read a raw message string verbatim or ask Claude Haiku for a
   summary - with an optional spoken word limit for the raw-message modes.
3. Adds a tray "Settings" submenu persisting these choices to config.json.

## 1. Hook registration and event parsing

- `docs/hook-snippet.json` gains a `Notification` entry with the same
  http-POST hook as `Stop` (same listener URL, timeout 10). Users re-merge
  the snippet into `%USERPROFILE%\.claude\settings.json`; README documents
  this (both for new installs and as an upgrade note).
- `EventParser.TryParseClaudeNotification(RaivenEvent, out ClaudeNotificationEvent)`:
  matches `Source == "claude-code" && Type == "Notification"`; requires
  `session_id`, `transcript_path`, `cwd`; reads optional `message` (default
  `""`) and optional `notification_type` (default `null`).
- `ClaudeNotificationEvent(string SessionId, string TranscriptPath, string Cwd,
  string Message, string? NotificationType)` record in Raiven.Core.Events.
- Housekeeping notification types are NOT questions and are ignored by the
  handler: `auth_success`, `agent_completed`, `elicitation_complete`,
  `elicitation_response`. A missing/unknown `notification_type` counts as a
  question (fail-open: better a spurious notification than a missed one).

## 2. Configuration

New `RaivenConfig` keys (flat, JSON, following existing style):

| Key | Type | Default | Meaning |
|---|---|---|---|
| `NotifyOnFinishedTurn` | bool | `true` | Chime/toast/auto-play on Stop events at all. |
| `FinishedTurnVoice` | string | `"summary"` | `"summary"` = Claude Haiku summary (current behavior); `"message"` = read the last assistant message verbatim, no Haiku call. |
| `FinishedTurnWordLimit` | int | `0` | Spoken word cap when `FinishedTurnVoice` is `"message"`. `0` = unlimited. |
| `NotifyOnQuestion` | bool | `true` | Chime/toast/voice on Notification events. |
| `QuestionVoice` | string | `"announce"` | `"announce"` = speak "Claude Code has a question in {project}."; `"message"` = speak the hook's message text; `"summary"` = Haiku one-liner. |
| `QuestionWordLimit` | int | `0` | Spoken word cap when `QuestionVoice` is `"message"`. `0` = unlimited. |

- Unknown string values fall back to the default at point of use (log once).
- `RaivenConfig` becomes mutable (`get; set;`) and gains `Save(string path)`
  (indented JSON, same serializer options as LoadOrCreate; best-effort with
  FileLog on failure). The single shared instance is mutated by the tray
  thread and read by listener/countdown threads - all reads are single
  bool/string/int property reads (atomic enough for this use; no torn state
  risk worth locking over).
- Word limits are config.json-only (no tray UI - numeric entry does not fit
  a menu).

`SpeechText.LimitWords(string text, int limit)` static helper (Raiven.Core):
`limit <= 0` returns text unchanged; otherwise returns the first `limit`
whitespace-separated words joined by single spaces.

## 3. Question flow (new)

On a parsed Notification event that passes the ignore-list, when
`NotifyOnQuestion` and not paused:

- Chime (same chime as finished turns).
- Toast: title "Claude Code has a question", second line = chat headline
  (`ReadFirstPrompt`, fallback folder name), third line = the hook `message`
  text (omitted when empty). Logo override like other toasts. No buttons,
  no arguments (clicking has no action), default duration.
- Voice speaks immediately - no countdown (questions are time-sensitive):
  - `announce`: "Claude Code has a question in {folder}."
  - `message`: the hook `message` text, word-limited by `QuestionWordLimit`;
    empty message falls back to the announce line.
  - `summary`: Haiku call with a question-specific system prompt (spoken-style
    one-liner: what Claude is asking/waiting for), user content = the hook
    message plus the last-turn slice when the transcript is readable. On any
    failure (or 15s timeout) falls back to the announce line.
- New `QuestionPipeline` (Raiven.Core, testable) owns this behavior:
  constructor `(RaivenConfig config, IClaudeClient claude, IVoice voice)`,
  method `Task AnnounceAsync(ClaudeNotificationEvent evt)`. The
  chime/toast/paused gating stays in Program.cs's event handler (same
  layering as finished turns). Questions are NOT recorded in the summary
  history.
- `INotifier` gains `void ShowQuestion(string folderName, string? headline,
  string message)`; AppSdkNotifier implements it (try/catch + FileLog like
  the other Show methods).

## 4. Finished-turn playback modes

- `NotifyOnFinishedTurn == false` suppresses chime/toast/countdown for Stop
  events entirely (registry upsert still happens, so stale-toast clicks from
  before the setting change still resolve).
- `SummaryPipeline.PlaySummaryAsync` honors `FinishedTurnVoice`:
  - `"summary"`: current behavior (Haiku, cache-before-generate, history).
  - `"message"`: skip the Haiku call; spoken text = the turn's
    `TurnSlice.AssistantText` (already extracted by `ReadLastTurn`),
    word-limited by `FinishedTurnWordLimit`. Empty assistant text falls back
    to "Claude finished, but there was no message to read."
  - Both modes record the spoken text as the history entry (same key:
    sessionId + transcript last-write) and hit the cache identically. A turn
    cached under one mode replays its cached text even after the mode
    changes - accepted behavior, documented in README.

## 5. Tray Settings submenu

`TrayContext` gains a "Settings" submenu (between "Recent summaries" and the
separator):

- ☑ Notify on finished turns  (checkbox -> `NotifyOnFinishedTurn`)
- ☑ Notify on questions       (checkbox -> `NotifyOnQuestion`)
- Finished turn voice ▸  ( ) Haiku summary / ( ) Read last message
  (radio -> `FinishedTurnVoice`)
- Question voice ▸  ( ) Announce only / ( ) Read message / ( ) Haiku summary
  (radio -> `QuestionVoice`)

Each change mutates the shared `RaivenConfig` and calls `Save` immediately.
Radio groups are plain checked menu items where checking one unchecks its
siblings. Menu state is initialized from config at startup; unknown config
strings display as the default option checked.

## 6. Error handling

- Question-flow failures: log + degrade (voice falls back to the announce
  line; toast failures already never crash). A failed config Save logs and
  keeps the in-memory value (setting applies until restart).
- Malformed Notification payloads (missing required fields) are ignored with
  an info log, like other unparseable events.

## 7. Testing

Unit tests (Raiven.Core.Tests):
- `EventParser.TryParseClaudeNotification`: happy path, missing fields,
  missing message defaults to "", notification_type passthrough.
- `SpeechText.LimitWords`: 0/negative = unchanged, limit < words truncates,
  limit >= words unchanged, whitespace normalization.
- `QuestionPipeline.AnnounceAsync`: announce mode speaks the fixed line with
  folder name; message mode speaks word-limited message, empty message falls
  back to announce; summary mode uses Claude and falls back to announce on
  failure; ignore-list types never reach the pipeline (tested at the
  callsite-logic level via a pure helper `IsQuestion(notificationType)`).
- `SummaryPipeline` message mode: no Claude call, speaks word-limited
  assistant text, records history, cache replay works.
- `RaivenConfig`: new-key defaults, round-trip, `Save` writes readable JSON.

Live verification: POST a fake Notification event to the listener; confirm
chime + toast + spoken announcement, config toggles change behavior, and
Stop-event behavior is unchanged. Re-register the updated hook snippet and
confirm a real permission prompt notifies.
