# RAIVEN

**R**easoning **A**gent for **I**ntelligent **V**irtual **E**xecution and **N**avigation

A Windows tray companion for Claude Code: when Claude finishes a turn you get a
chime and a toast notification - click "Play summary" and RAIVEN asks Claude
Haiku for a short spoken-style summary and reads it aloud with a fully local
Kokoro-82M voice.

Notifications use the Windows App SDK (`Microsoft.Windows.AppNotifications`),
so the toast header shows "RAIVEN" with the raven icon (pulled straight from
the exe's own version info and embedded icon - no AUMID or shortcut needed),
and finished-turn toasts stay on screen for about 25 seconds instead of the
usual ~7.

## How it works

Claude Code's `Stop` hook POSTs the turn's metadata to a loopback-only HTTP
listener (`http://127.0.0.1:9876/`). RAIVEN chimes and toasts immediately.
Only when you click the toast does it read the transcript, call Claude Haiku
(the only network call, using your Claude subscription via the `claude` CLI
by default), and speak the summary via KokoroSharp - TTS never leaves your
machine.

## Setup

1. **Build & run** (requires .NET 10 SDK on Windows):

       dotnet run --project src/Raiven.App

   A tray icon appears. First voice use downloads the Kokoro model (~320 MB).

2. **Register the hook**: merge `docs/hook-snippet.json` into
   `%USERPROFILE%\.claude\settings.json` (create the `hooks` section if it
   doesn't exist). Restart any running Claude Code sessions.

3. **Anthropic credentials**: by default RAIVEN uses your Claude Code
   subscription login (`claude /login`) via the `claude` CLI - no API key
   needed. Set `SummaryBackend: "api"` in config and export
   `ANTHROPIC_API_KEY` only if you want pay-per-token API billing instead.
   Without either, notifications still work; only "Play summary" fails
   (with an error toast) - RAIVEN never silently falls back from `cli` to
   `api` to avoid surprise charges.

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
| `Model` | `claude-haiku-4-5` | Model used for summaries (when `SummaryBackend` is `api`) |
| `MaxTranscriptChars` | `30000` | Max transcript characters sent for summarizing |
| `SessionExpiryMinutes` | `240` | How long a finished session stays summarizable |
| `SummaryBackend` | `cli` | `cli` uses your Claude subscription via the `claude` CLI (no per-token cost); `api` uses the Anthropic API directly (pay-per-token, needs `ANTHROPIC_API_KEY`) |
| `CliModelAlias` | `haiku` | CLI model alias used for summaries when `SummaryBackend` is `cli` - one of `sonnet`, `opus`, `haiku`, `fable` |

Logs: `%APPDATA%\Raiven\logs\raiven.log`.

## Testing without Claude Code

With RAIVEN running: `powershell -File scripts/send-test-event.ps1`

## Troubleshooting

- **No toast/chime**: is RAIVEN running? Is the hook registered? Check the log.
- **App fails to start with a "Windows App Runtime" error**: install the
  Windows App Runtime -
  https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads
  If it's already installed and RAIVEN still fails at startup with a COM or
  "class not registered" error, the install can be present but not correctly
  registered - repair it with
  `winget install --id Microsoft.WindowsAppRuntime.2.2 --force` (this exact
  situation occurred during development).
- **"Access denied" starting the listener**: rare on Win10/11 loopback; run
  `netsh http add urlacl url=http://127.0.0.1:9876/ user=%USERNAME%` once as
  admin, or change `Port`.
- **Port in use**: change `Port` in config.json AND in the hook snippet.
- **Summary fails**: usually missing Anthropic credentials - see Setup step 3.
- **Summary fails with "Could not launch the claude CLI"**: make sure Claude
  Code is installed and `claude` is on PATH, and that you're logged in
  (`claude /login`).

## Roadmap

See `docs/superpowers/specs/2026-07-02-raiven-design.md` - MCP server adapter,
driving Claude Code sessions, voice confirmation, and a general local agent hub.
