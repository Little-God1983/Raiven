# RAIVEN

**R**easoning **A**gent for **I**ntelligent **V**irtual **E**xecution and **N**avigation

A Windows tray companion for Claude Code: when Claude finishes a turn you get a
chime and a toast notification - click "Play summary" and RAIVEN asks Claude
Haiku for a short spoken-style summary and reads it aloud with a fully local
Kokoro-82M voice.

## How it works

Claude Code's `Stop` hook POSTs the turn's metadata to a loopback-only HTTP
listener (`http://127.0.0.1:9876/`). RAIVEN chimes and toasts immediately.
Only when you click the toast does it read the transcript, call Claude Haiku
(the only network call, using your existing Anthropic credentials), and speak
the summary via KokoroSharp - TTS never leaves your machine.

## Setup

1. **Build & run** (requires .NET 10 SDK on Windows):

       dotnet run --project src/Raiven.App

   A tray icon appears. First voice use downloads the Kokoro model (~320 MB).

2. **Register the hook**: merge `docs/hook-snippet.json` into
   `%USERPROFILE%\.claude\settings.json` (create the `hooks` section if it
   doesn't exist). Restart any running Claude Code sessions.

3. **Anthropic credentials**: RAIVEN uses the same resolution as the official
   SDK - `ANTHROPIC_API_KEY` env var, `ANTHROPIC_AUTH_TOKEN`, or an
   `ant auth login` profile. Without credentials, notifications still work;
   only "Play summary" fails (with an error toast).

4. Optional: tray menu -> "Start with Windows".

## Tray menu

Pause notifications * Test notification * Test voice * Start with Windows *
Open data folder * Quit

## Configuration

`%APPDATA%\Raiven\config.json` (created on first run):

| Key | Default | Meaning |
|---|---|---|
| `Port` | `9876` | Loopback listener port (keep in sync with the hook URL) |
| `Voice` | `af_heart` | Kokoro voice name (e.g. `am_michael` for a male voice) |
| `ChimeWavPath` | `null` | Custom chime WAV; default is the system Asterisk sound |
| `Model` | `claude-haiku-4-5` | Model used for summaries |
| `MaxTranscriptChars` | `30000` | Max transcript characters sent for summarizing |
| `SessionExpiryMinutes` | `240` | How long a finished session stays summarizable |

Logs: `%APPDATA%\Raiven\logs\raiven.log`.

## Testing without Claude Code

With RAIVEN running: `powershell -File scripts/send-test-event.ps1`

## Troubleshooting

- **No toast/chime**: is RAIVEN running? Is the hook registered? Check the log.
- **"Access denied" starting the listener**: rare on Win10/11 loopback; run
  `netsh http add urlacl url=http://127.0.0.1:9876/ user=%USERNAME%` once as
  admin, or change `Port`.
- **Port in use**: change `Port` in config.json AND in the hook snippet.
- **Summary fails**: usually missing Anthropic credentials - see Setup step 3.

## Roadmap

See `docs/superpowers/specs/2026-07-02-raiven-design.md` - MCP server adapter,
driving Claude Code sessions, voice confirmation, and a general local agent hub.
