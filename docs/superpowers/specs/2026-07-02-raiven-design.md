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

RAIVEN is a single C#/.NET Windows tray application that starts at login and runs continuously. Internally it is built as **Core + Adapters**, so that today's single notification flow is one instance of a general pattern rather than a one-off: an inbound adapter turns something that happened into an event, Core routes the event to a service, and the service produces one or more outbound actions. This costs nothing extra to build for v1, but means the future capabilities in the [Roadmap](#roadmap) are additions to Core rather than rewrites of it.

- **Core** — an internal event pipeline (`Event in → handled by a service → Action(s) out`) plus a small session registry (`session_id → transcript_path, cwd, last-seen`) that outlives any single event.
- **Inbound adapters** — things that feed typed events into Core. v1 ships exactly one: an HTTP listener bound to `127.0.0.1` that accepts a generic event envelope (`source`, `type`, `payload`), fed by Claude Code's `Stop` hook (configured with `type: "http"`, posting directly — no intermediate script needed, since `http` is a first-class documented Claude Code hook type).
- **Outbound actions** — things Core can trigger. v1 ships: play chime, raise toast, summarize via Haiku, speak via Kokoro.

For v1 specifically: on a `Stop` event, Core plays a chime and raises a Windows toast with a "Play summary" action, then the adapter returns HTTP 200 without blocking Claude Code. Nothing further happens unless the user clicks the toast. On click, Core reads the transcript referenced by the registered session, asks Claude Haiku (via the Anthropic API, using the user's existing API access) for a short narrative-style spoken summary, and synthesizes that text locally with Kokoro-82M via the [KokoroSharp](https://github.com/Lyrcaxis/KokoroSharp) NuGet package, then plays the audio.

Kokoro/TTS is local-only in all cases — this is a hard requirement, not configurable. Haiku is the only component that calls the network, and only in response to a click.

## Components

| Component | Responsibility |
|---|---|
| Tray shell | System tray icon, right-click menu (pause notifications, quit, settings), start-at-login registration |
| Local listener (inbound adapter) | Loopback-only HTTP server accepting a generic `{source, type, payload}` event envelope; v1's only registered source is Claude Code's `Stop` hook |
| Session registry (Core) | Durable-for-the-process store of `session_id → transcript_path, cwd, last-seen`, populated by inbound events and read by any outbound action that needs session context |
| Notifier (outbound action) | Plays the chime, raises the Windows toast, tags/keys each by session ID so concurrent Claude Code sessions don't cross-talk |
| Summarizer (outbound action) | On toast click: extracts the relevant slice of the referenced transcript, calls Claude Haiku for a short narrative summary |
| Voice (outbound action) | Feeds the summary text to KokoroSharp (Kokoro-82M) and plays the resulting audio locally |
| Config | Local settings file: HTTP port, Kokoro voice selection, chime sound |
| Hook snippet | One-time addition to the user's Claude Code settings registering the `Stop` hook against RAIVEN's local listener |

## Data flow

1. Claude Code fires `Stop` at the end of a turn → the local listener adapter receives it and hands Core a typed event (session ID, transcript path, working directory).
2. Core writes/updates the session registry entry for that session ID, and triggers the Notifier action: play the chime + show a toast ("Claude finished in \<folder>", with a "Play summary" button). RAIVEN responds 200 immediately; it never blocks Claude Code waiting on the user.
3. If the user clicks "Play summary": Core looks up the session registry entry, triggers the Summarizer action, which pulls the relevant transcript slice and asks Haiku for a 1–3 sentence narrative summary (e.g. "I fixed the login bug and all tests are passing now.").
4. Core triggers the Voice action: that summary is synthesized and spoken locally via Kokoro.
5. If the toast is never clicked, no Haiku call and no audio are ever produced for that turn.
6. If clicked after the session registry entry has expired or RAIVEN has restarted, RAIVEN shows a short "that session's details are no longer available" notification instead of failing silently.

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

## Roadmap (explicitly out of scope for v1)

RAIVEN's longer-term goal is a local event/control hub for AI-assisted work, with Claude Code as its first integration. None of the following is built in v1 — they're named here so the Core+Adapters architecture above is deliberately shaped to accept them as additions:

- **Claude calls RAIVEN as tools.** RAIVEN also runs as an MCP server — a second inbound adapter alongside the HTTP listener. This lets Claude proactively call something like `raiven.speak(...)` or `raiven.ask(...)` mid-task, instead of RAIVEN only reacting after a `Stop` event.
- **RAIVEN drives Claude Code sessions.** RAIVEN gains an outbound action that starts or resumes a Claude Code session with a new prompt (via `claude -p --resume <session_id>`, spawned as a subprocess). Constraint worth flagging now: there is no way to inject a prompt into a still-running interactive session's stdin — "driving" a session means starting a new turn via `--resume`, not talking into a live one. This is what would let a voice command ("Claude, add tests for that") become a new Claude Code turn.
- **RAIVEN coordinates other tools/agents.** The event envelope (`source`, `type`, `payload`) and action model are intentionally not Claude-Code-specific, so other local tools/agents can register as additional inbound adapters or outbound action targets later, making RAIVEN a general local hub rather than a single-integration notifier.
- Voice-activated confirmation ("say yes") via local speech-to-text, replacing or supplementing the toast button — this is what would carry a spoken "yes, and also add tests" into the session-driving capability above.
- Filtering which turns notify (e.g. skip trivial replies).
- Cross-platform support.

Design update 2026-07-03: SummaryPipeline's IClaudeClient now defaults to ClaudeCliClient (shells to 'claude -p', subscription billing via Claude Code CLI /login), with AnthropicClaudeClient (pay-per-token API) as opt-in via config.SummaryBackend='api'. No silent fallback on CLI failure by user's explicit choice - CLI errors surface as toast.
