<p align="center">
  <img src="Images/Raiven-logo.png" alt="RAIVEN logo" width="220">
</p>

<h1 align="center">RAIVEN</h1>

<p align="center"><strong>R</strong>easoning <strong>A</strong>gent for <strong>I</strong>ntelligent <strong>V</strong>irtual <strong>E</strong>xecution and <strong>N</strong>avigation</p>

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

## Installation

### Prerequisites

- Windows 10 (build 17763+) or Windows 11.
- [.NET 10 SDK](https://dotnet.microsoft.com/download) - `dotnet --version` should print `10.x`.
- [Claude Code](https://code.claude.com) installed and on `PATH`, logged in
  via `claude /login` (your Claude Pro/Max/Team subscription). This is the
  default credential RAIVEN uses for summaries - no separate API key needed.
  (An `ANTHROPIC_API_KEY` works too as an opt-in alternative; see
  [Configuration](#configuration).)
- The Windows App Runtime **2.2** is required for notifications. Most current
  Windows 11 installs already have it; if RAIVEN fails to start, see
  [Troubleshooting](#troubleshooting).
- `git`, to clone the repo.

### 1. Get the code

```
git clone https://github.com/Little-God1983/Raiven.git
cd Raiven
```

### 2. Build and install it somewhere permanent

`dotnet run` (used during development) builds into a `bin\Debug\...` folder
that can be wiped by a rebuild - fine for trying it out, not for something
you want running every day. For a real install, publish a Release build into
a stable folder instead:

```
dotnet publish src/Raiven.App -c Release -r win-x64 --self-contained false -o "%LOCALAPPDATA%\Programs\RAIVEN"
```

This produces `Raiven.App.exe` and everything it needs in
`%LOCALAPPDATA%\Programs\RAIVEN`. Run it once to confirm the tray icon
appears (right-click it to see the menu):

```
"%LOCALAPPDATA%\Programs\RAIVEN\Raiven.App.exe"
```

(If you'd rather just try RAIVEN without a permanent install, skip this step
and use `dotnet run --project src/Raiven.App` instead everywhere below - it
behaves identically, it just won't survive a rebuild.)

### 3. Register the Claude Code hook

Merge `docs/hook-snippet.json` into `%USERPROFILE%\.claude\settings.json`
(create the file, or the `hooks` section within it, if it doesn't exist
yet). This tells Claude Code to notify RAIVEN's listener whenever a turn
finishes - it's a one-time, global change that applies to every Claude Code
session on the machine, in any editor or terminal. Restart any Claude Code
sessions that were already running so they pick up the change.

The snippet registers **three** hooks, all pointing at RAIVEN's listener:
`Stop` (finished-turn notifications), `Notification` (permission prompts and
idle/input waits), and `PreToolUse` scoped by `matcher` to the
`AskUserQuestion` tool - Claude Code's interactive multiple-choice dialog,
which fires neither a `Stop` (the turn continues once you answer) nor a
`Notification` hook of its own. If you set RAIVEN up before any of these
entries existed, re-merge the updated `docs/hook-snippet.json` so all three
are present - otherwise that notification type silently never fires (e.g.
finished turns notify, but the `AskUserQuestion` popup stays silent).

### 4. Confirm Claude credentials are in place

By default RAIVEN summarizes via your Claude subscription through the
`claude` CLI - if `claude /login` already works in a terminal, you're done,
no further setup needed. See [Configuration](#configuration) if you'd rather
use a pay-per-token API key instead.

### 5. Make it permanent (optional)

Right-click the tray icon -> **Start with Windows**. This registers whichever
exe is currently running, so do this only after launching RAIVEN from its
permanent install folder (step 2), not from a `dotnet run` build.

### 6. Try it

With RAIVEN running, in another terminal:

```
powershell -File scripts\send-test-event.ps1
```

You should hear a chime, see a "Claude finished in RAIVEN" toast, and hearing
"Play summary" speak a summary aloud confirms the whole pipeline - chime,
toast, transcript read, Claude Haiku call, and local voice - is working.
From then on, it fires automatically whenever any Claude Code session
finishes a turn.

## Usage

### Tray menu

Right-click the tray icon:

Pause notifications * Recent summaries * Settings * Test notification *
Test voice * Start with Windows * Open data folder * Quit

**Pause notifications** is worth knowing about: Claude Code's `Stop` hook
fires at the end of *every* turn, including a live back-and-forth chat. If
you're actively conversing with Claude in one window, pause notifications so
you're not chimed on every reply; unpause when you hand off a longer task and
tab away - that's the scenario RAIVEN is built for.

### Testing without a live Claude Code session

With RAIVEN running: `powershell -File scripts\send-test-event.ps1` - posts a
synthetic finished-turn event with a fake transcript, so you can exercise the
whole chime -> toast -> click -> summary -> voice path on demand.

## Configuration

`%APPDATA%\Raiven\config.json` (created on first run):

| Key | Default | Meaning |
|---|---|---|
| `Port` | `9876` | Loopback listener port (keep in sync with the hook URL) |
| `Voice` | `af_heart` | Kokoro voice name (e.g. `am_michael` for a male voice) |
| `ChimeWavPath` | `null` | Custom chime WAV; default is the system Asterisk sound |
| `Model` | `claude-haiku-4-5` | Model used for summaries (when `SummaryBackend` is `api`) |
| `MaxTranscriptChars` | `30000` | Max transcript characters sent for summarizing |
| `SummaryTimeoutSeconds` | `120` | Max seconds to wait for the summary Claude call before giving up (clamped `1-600`). Raise it on slower machines or very long sessions; config.json-only |
| `SessionExpiryMinutes` | `240` | How long a finished session stays summarizable |
| `SummaryBackend` | `cli` | `cli` uses your Claude subscription via the `claude` CLI (no per-token cost); `api` uses the Anthropic API directly (pay-per-token, needs `ANTHROPIC_API_KEY`) |
| `CliModelAlias` | `haiku` | CLI model alias used for summaries when `SummaryBackend` is `cli` - one of `sonnet`, `opus`, `haiku`, `fable` |
| `AutoPlaySummary` | `true` | When on, a finished-turn toast auto-plays the summary after `AutoPlayDelaySeconds`; with a non-zero delay the toast carries a countdown plus **Play now** and **Abort** buttons. Set `false` for the old click-to-play behavior |
| `AutoPlayDelaySeconds` | `0` | Seconds the auto-play countdown runs before speaking (clamped to `0-60`). `0` (the default) plays immediately with no countdown or **Abort** window; set `1-60` for a reaction window. Also on the tray **Settings → Auto-play delay** submenu |
| `HistorySize` | `10` | How many spoken summaries **Recent summaries** keeps (clamped to `1-50`). Also on the tray **Settings → Recent summaries kept** submenu; lowering it trims immediately |
| `NotifyOnFinishedTurn` | `true` | Master on/off switch for finished-turn chime/toast/voice; also toggled by the tray **Settings** submenu |
| `FinishedTurnVoice` | `summary` | `summary` asks Claude Haiku for a short spoken summary; `message` reads the last assistant message verbatim (no Claude call), capped by `FinishedTurnWordLimit`. Unknown values behave as `summary` |
| `FinishedTurnWordLimit` | `0` | Spoken word cap for `FinishedTurnVoice: message` mode; `0` = unlimited. config.json-only - no tray control |
| `NotifyOnQuestion` | `true` | Master on/off switch for question notifications (Claude Code waiting on a permission prompt or input); also toggled by the tray **Settings** submenu |
| `QuestionVoice` | `announce` | `announce` speaks a fixed "Claude Code has a question in {folder}." line; `message` reads the hook's message text verbatim, capped by `QuestionWordLimit`; `summary` asks Claude Haiku to phrase what's being asked. Unknown values (and `message` with an empty message) behave as `announce` |
| `QuestionWordLimit` | `0` | Spoken word cap for `QuestionVoice: message` mode; `0` = unlimited. config.json-only - no tray control |
| `SuppressDuplicateQuestionPrompts` | `true` | Drops Claude Code's generic "Claude needs your permission to use AskUserQuestion" notification, which newer builds fire a few seconds after the `PreToolUse` hook already announced the same dialog with its real question text. Toggled by tray **Settings -> Skip duplicate permission prompts**. Turn it off only if you have not registered the `PreToolUse` hook - permission prompts for every other tool are unaffected either way |
| `KeepAudioAlive` | `false` | Plays a continuous silent stream so the audio device - and a Bluetooth link - never sleeps, preventing the first ~second of speech being swallowed. Toggled by tray **Settings -> Keep audio device awake**. Costs Bluetooth-headphone battery while on |
| `ShowPlaybackStatus` | `true` | Finished-turn and replay toasts stay on screen through the whole pipeline and show what RAIVEN is doing (Summarizing with Haiku / Generating voice / Speaking), then dismiss when speech ends; also toggled by the tray **Settings** submenu. `false` restores the old disappear-at-play behavior |

### Auto-play, Play now/Abort, and Recent summaries

By default (`AutoPlaySummary: true`, `AutoPlayDelaySeconds: 0`) each finished-turn
toast speaks the summary immediately - no countdown, no reaction window. Set
`AutoPlayDelaySeconds` to `1-60` (config.json or the tray **Settings → Auto-play
delay** submenu) to get a countdown progress bar first: while it counts down you
can click **Play now** to speak it immediately or **Abort** to skip it, and
pausing notifications during a countdown cancels the pending auto-play. If you
prefer the click-to-play toast, set `"AutoPlaySummary": false` in `config.json`.

The tray menu's **Recent summaries** submenu keeps the last `HistorySize`
(default 10) spoken summaries and replays any of them straight from cache - no
new Claude call. Change the count from the tray **Settings → Recent summaries
kept** submenu. This history is persisted to `%APPDATA%\Raiven\history.json`.

### Playback status and the Stop/Hide buttons

With `ShowPlaybackStatus` on (the default), the finished-turn toast stays on
screen for the whole playback run: its progress bar text walks through
"Summarizing with Haiku", "Loading voice model" (first playback after each
start of RAIVEN; on the very first run this makes the one-time ~320 MB Kokoro
download visible), "Generating voice", and
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

### Question notifications

When Claude Code has a question - a permission prompt, an interactive
multiple-choice dialog (`AskUserQuestion`), or it's waiting on input - RAIVEN
chimes, shows a toast, and speaks immediately (no countdown,
no history entry - unlike finished-turn summaries). Because a new voice line
always preempts whatever is currently playing, a question announcement can
interrupt a summary that is still being read aloud; an interrupted summary
can be replayed afterward from **Recent summaries**.

Both finished-turn and question notifications can be switched on/off, and
their voice mode changed, from the tray's **Settings** submenu, which saves
every change straight to `config.json`. The spoken word limits
(`FinishedTurnWordLimit`, `QuestionWordLimit`) are config.json-only - there's
no tray control for them. RAIVEN rewrites the whole file on every
Settings-menu change while it's running, so quit RAIVEN before hand-editing
config.json, or the next Settings-menu change may overwrite your edit.

Finished-turn summaries are cached per session (see above): if you change
`FinishedTurnVoice` after a turn's summary has already been generated,
replaying that same turn - via **Play now** on a lingering toast, or via
**Recent summaries** - still speaks the original cached text. The new mode
takes effect starting with that session's next finished turn.

Logs: `%APPDATA%\Raiven\logs\raiven-log-YYYYMMDD.log` - a new file per day
(e.g. `raiven-log-20260705.log`); RAIVEN keeps the newest 30 and deletes older
ones on startup. Check today's file first whenever something doesn't work as
expected.

## Troubleshooting

- **No toast/chime**: is RAIVEN running? Is the hook registered? Check the log.
- **A question is announced twice** (the real question, then "Claude needs your permission to use AskUserQuestion"): you are running with `SuppressDuplicateQuestionPrompts: false`. Re-enable it in tray **Settings -> Skip duplicate permission prompts**.
- **App fails to start with a COM / "class not registered" error
  (0x80040154)**: the Windows App Runtime 2.2 is missing or not registered on
  this machine. Download and run Microsoft's **standalone installer**
  (`windowsappruntimeinstall-x64.exe`, latest 2.2.x release) from
  https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads
  and start RAIVEN again. Do **not** use winget for this: its catalog has no
  `Microsoft.WindowsAppRuntime.2.2` package, and the 2.1 package it does have
  installs only the Framework package - not the Main/Singleton packages that
  register the notification COM classes - so it cannot fix this error.
  RAIVEN shows an error dialog naming this fix if it hits this at startup,
  rather than failing silently.
- **"Access denied" starting the listener**: rare on Win10/11 loopback; run
  `netsh http add urlacl url=http://127.0.0.1:9876/ user=%USERNAME%` once as
  admin, or change `Port`.
- **Port in use**: change `Port` in config.json AND in the hook snippet.
- **Summary fails**: usually missing Anthropic credentials - see Installation
  step 4.
- **Summary fails with "Could not launch the claude CLI"**: make sure Claude
  Code is installed and `claude` is on PATH, and that you're logged in
  (`claude /login`).
- **"Start with Windows" launches an old/wrong build**: it registers
  whatever exe was running when you toggled it on. Re-toggle it off and on
  again after publishing a new build to the same install folder.

## Roadmap

See `docs/superpowers/specs/2026-07-02-raiven-design.md` - MCP server adapter,
driving Claude Code sessions, voice confirmation, and a general local agent hub.
