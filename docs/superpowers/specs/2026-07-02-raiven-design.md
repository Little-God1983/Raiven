# RAIVEN — Design Spec

**R**easoning **A**gent for **I**ntelligent **V**irtual **E**xecution and **N**avigation

Date: 2026-07-02
Status: Approved for planning

## Problem

Claude Code runs inside VS Code's terminal with no way to know it has finished a task without checking the window. The goal is a Jarvis-style local companion: when Claude Code finishes a turn, get an audible chime and a notification, and optionally hear a short spoken summary of what happened — narrated by a small model and a local, offline text-to-speech voice.

## Prior art considered

- **[claude-voice-mcp](https://github.com/nathanphelps/claude-voice-mcp)** (Nathan Phelps) — an MCP server exposing a `speak` tool. Claude decides to call it after every response, using Kokoro-82M (ONNX) for local TTS. Not usable as-is: it narrates *every* response (not a finish notification), and relies on the model remembering to call a tool rather than firing deterministically.
- **[claude-code-notify](https://github.com/BenWilles/claude-code-notify)** (Ben Willes) — a VS Code extension that installs a Claude Code hook (`Notification`/`Stop`) to play a sound or say a canned phrase via macOS's `say`. Deterministic and reliable, but macOS-only and has no AI-generated summary.

RAIVEN borrows the hook-driven, deterministic trigger mechanism from claude-code-notify and the local-Kokoro-TTS approach from claude-voice-mcp, reimplemented from scratch as a Windows-native background service, since neither project runs on Windows or combines notification + on-demand AI summary + local voice.

## Non-goals (v1)

- Not cross-platform — Windows only.
- Not a VS Code extension — a standalone background service (works regardless of which terminal/editor hosts Claude Code).
- No speech-to-text / voice-activated "yes" — confirmation is a toast button click. Voice confirmation is an explicit possible future phase, not built now.
- No filtering of which turns notify — every `Stop` event triggers a chime + toast in v1.

## Architecture

RAIVEN is a single C#/.NET Windows tray application that starts at login and runs continuously.

Claude Code's `Stop` hook (fires natively at the end of every turn) is configured with `type: "http"`, posting its event JSON directly to a RAIVEN-owned local HTTP endpoint bound to `127.0.0.1` only. No intermediate script is needed — this is a first-class, documented Claude Code hook type.

On receiving a `Stop` event, RAIVEN immediately plays a chime and raises a Windows toast notification with a "Play summary" action, then returns HTTP 200 without blocking Claude Code. Nothing further happens unless the user clicks the toast.

On click, RAIVEN reads the transcript referenced by the stored event, asks Claude Haiku (via the Anthropic API, using the user's existing API access) for a short narrative-style spoken summary, and synthesizes that text locally with Kokoro-82M via the [KokoroSharp](https://github.com/Lyrcaxis/KokoroSharp) NuGet package, then plays the audio.

Kokoro/TTS is local-only in all cases — this is a hard requirement, not configurable. Haiku is the only component that calls the network, and only in response to a click.

## Components

| Component | Responsibility |
|---|---|
| Tray shell | System tray icon, right-click menu (pause notifications, quit, settings), start-at-login registration |
| Local listener | Loopback-only HTTP server receiving `Stop` event payloads from Claude Code |
| Notifier | Plays the chime, raises the Windows toast, tags/keys each by session ID so concurrent Claude Code sessions don't cross-talk |
| Summarizer | On toast click: extracts the relevant slice of the referenced transcript, calls Claude Haiku for a short narrative summary |
| Voice | Feeds the summary text to KokoroSharp (Kokoro-82M) and plays the resulting audio locally |
| Config | Local settings file: HTTP port, Kokoro voice selection, chime sound |
| Hook snippet | One-time addition to the user's Claude Code settings registering the `Stop` hook against RAIVEN's endpoint |

## Data flow

1. Claude Code fires `Stop` at the end of a turn → POSTs the event (session ID, transcript path, working directory) to RAIVEN.
2. RAIVEN caches the event in memory, keyed by session ID, and immediately plays the chime + shows a toast ("Claude finished in \<folder>", with a "Play summary" button). RAIVEN responds 200 immediately; it never blocks Claude Code waiting on the user.
3. If the user clicks "Play summary": RAIVEN looks up the cached event, pulls the relevant transcript slice, and asks Haiku for a 1–3 sentence narrative summary (e.g. "I fixed the login bug and all tests are passing now.").
4. That summary is synthesized and spoken locally via Kokoro.
5. If the toast is never clicked, no Haiku call and no audio are ever produced for that turn.
6. If clicked after the cached event has expired or RAIVEN has restarted, RAIVEN shows a short "that session's details are no longer available" notification instead of failing silently.

## Error handling

- RAIVEN not running when `Stop` fires → the HTTP POST simply fails to connect; this is a non-blocking hook type, so Claude Code is unaffected, the user just misses that one notification. Mitigated by auto-start at login.
- Haiku call fails (network/rate limit/timeout) → a short error toast, no retry storm.
- Kokoro/audio playback fails → error toast, logged locally, no crash.
- Concurrent Claude Code sessions finishing simultaneously → kept separate via session ID, so toasts and summaries never mix.
- The HTTP listener binds to `127.0.0.1` only and is never reachable from the network.

## Testing

Personal tool — no CI investment. Verification is:
- A smoke-test script that POSTs a synthetic `Stop`-shaped payload directly to the local endpoint, to verify chime → toast → summary → voice each work without needing a live Claude Code session.
- A manual end-to-end pass with a real Claude Code session and the hook configured, confirming the full chime → toast → click → summary → voice path.

## Open items for the future (explicitly out of scope for v1)

- Voice-activated confirmation ("say yes") via local speech-to-text, replacing or supplementing the toast button.
- Filtering which turns notify (e.g. skip trivial replies).
- Cross-platform support.
