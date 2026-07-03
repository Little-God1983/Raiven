# Question Notifications & Configurable Voice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Notify (chime + toast + immediate voice) when Claude Code has a question, and make every notification type configurable — on/off, voice mode (raw message vs. Haiku summary), spoken word limits — via a tray Settings submenu persisted to config.json.

**Architecture:** The Claude Code `Notification` hook is registered alongside `Stop` and parsed by a new `EventParser.TryParseClaudeNotification`. A new `QuestionPipeline` (Raiven.Core, testable) owns question voice behavior (announce / message / Haiku summary, immediate, no countdown); `SummaryPipeline` gains a "message" mode that reads the last assistant message verbatim without a Haiku call. `RaivenConfig` becomes mutable + saveable so a new tray "Settings" submenu can persist toggles.

**Tech Stack:** .NET 10, Windows App SDK 2.2 notifications, WinForms tray, xUnit.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-03-question-notifications-design.md`.
- New config keys and defaults (exact): `NotifyOnFinishedTurn` = `true`, `FinishedTurnVoice` = `"summary"` (`"summary"`|`"message"`), `FinishedTurnWordLimit` = `0` (0 = unlimited), `NotifyOnQuestion` = `true`, `QuestionVoice` = `"announce"` (`"announce"`|`"message"`|`"summary"`), `QuestionWordLimit` = `0`. Unknown string values behave as the default at point of use.
- Question announce line (exact): `Claude Code has a question in {folder}.`
- Finished-turn empty-message fallback (exact): `Claude finished, but there was no message to read.`
- Ignored notification types (exact, case-insensitive): `auth_success`, `agent_completed`, `elicitation_complete`, `elicitation_response`. Missing/unknown `notification_type` counts as a question.
- Questions speak immediately — no countdown; questions are never recorded in the summary history.
- No new NuGet packages; target frameworks unchanged.
- All failure paths log via `FileLog` and degrade gracefully — never crash the tray app.
- Run tests with: `dotnet test tests/Raiven.Core.Tests` (repo root `e:\Repos\RAIVEN`). Suite currently: 63 passed, 2 skipped.
- Commit style: `feat:`/`fix:`/`docs:` prefix + Claude co-author trailer.

---

### Task 1: Config — new keys, mutability, Save

**Files:**
- Modify: `src/Raiven.Core/Config/RaivenConfig.cs`
- Test: `tests/Raiven.Core.Tests/RaivenConfigTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces (used by Tasks 4-7): properties `bool NotifyOnFinishedTurn`, `string FinishedTurnVoice`, `int FinishedTurnWordLimit`, `bool NotifyOnQuestion`, `string QuestionVoice`, `int QuestionWordLimit` — all `{ get; set; }`; instance method `void Save(string path)`. ALL existing properties change from `init` to `set` so the tray can mutate and Save can round-trip.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Raiven.Core.Tests/RaivenConfigTests.cs`:

```csharp
[Fact]
public void LoadOrCreate_MissingFile_HasNotificationDefaults()
{
    var config = RaivenConfig.LoadOrCreate(TempConfigPath());

    Assert.True(config.NotifyOnFinishedTurn);
    Assert.Equal("summary", config.FinishedTurnVoice);
    Assert.Equal(0, config.FinishedTurnWordLimit);
    Assert.True(config.NotifyOnQuestion);
    Assert.Equal("announce", config.QuestionVoice);
    Assert.Equal(0, config.QuestionWordLimit);
}

[Fact]
public void Save_ThenLoad_RoundTripsMutatedValues()
{
    var path = TempConfigPath();
    var config = RaivenConfig.LoadOrCreate(path);
    config.NotifyOnQuestion = false;
    config.QuestionVoice = "message";
    config.QuestionWordLimit = 25;

    config.Save(path);
    var reloaded = RaivenConfig.LoadOrCreate(path);

    Assert.False(reloaded.NotifyOnQuestion);
    Assert.Equal("message", reloaded.QuestionVoice);
    Assert.Equal(25, reloaded.QuestionWordLimit);
    Assert.Equal(9876, reloaded.Port); // untouched keys survive the round trip
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Raiven.Core.Tests --filter "Notification | RoundTrips"`
Expected: build FAILURE — `'RaivenConfig' does not contain a definition for 'NotifyOnFinishedTurn'`.

- [ ] **Step 3: Implement**

In `src/Raiven.Core/Config/RaivenConfig.cs`:

1. Add `using Raiven.Core.Logging;` to the usings.
2. Change every existing property's `init` to `set` (Port, Voice, ChimeWavPath, Model, MaxTranscriptChars, SessionExpiryMinutes, SummaryBackend, CliModelAlias, AutoPlaySummary, AutoPlayDelaySeconds).
3. After `AutoPlayDelaySeconds`, add:

```csharp
    public bool NotifyOnFinishedTurn { get; set; } = true;
    public string FinishedTurnVoice { get; set; } = "summary"; // "summary" (Claude Haiku) or "message" (read last assistant message verbatim)
    public int FinishedTurnWordLimit { get; set; } // spoken word cap for "message" mode; 0 = unlimited
    public bool NotifyOnQuestion { get; set; } = true;
    public string QuestionVoice { get; set; } = "announce"; // "announce", "message" (read the hook's message), or "summary" (Claude Haiku)
    public int QuestionWordLimit { get; set; } // spoken word cap for "message" mode; 0 = unlimited
```

4. After `LoadOrCreate`, add:

```csharp
    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            FileLog.Error($"Could not save config to {path}", ex);
        }
    }
```

- [ ] **Step 4: Run the full suite**

Run: `dotnet test tests/Raiven.Core.Tests`
Expected: PASS — 65 passed, 2 skipped.

- [ ] **Step 5: Commit**

```bash
git add src/Raiven.Core/Config/RaivenConfig.cs tests/Raiven.Core.Tests/RaivenConfigTests.cs
git commit -m "feat: notification config keys with mutable, saveable RaivenConfig"
```

---

### Task 2: SpeechText.LimitWords

**Files:**
- Create: `src/Raiven.Core/Voice/SpeechText.cs`
- Test: `tests/Raiven.Core.Tests/SpeechTextTests.cs`

**Interfaces:**
- Produces (used by Tasks 4-5): `public static string SpeechText.LimitWords(string text, int limit)` in namespace `Raiven.Core.Voice` — `limit <= 0` returns text unchanged; otherwise first `limit` whitespace-separated words joined by single spaces (original text returned unchanged when it has ≤ limit words).

- [ ] **Step 1: Write the failing tests**

Create `tests/Raiven.Core.Tests/SpeechTextTests.cs`:

```csharp
using Raiven.Core.Voice;

namespace Raiven.Core.Tests;

public class SpeechTextTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void LimitWords_ZeroOrNegative_ReturnsUnchanged(int limit)
    {
        Assert.Equal("one two  three", SpeechText.LimitWords("one two  three", limit));
    }

    [Fact]
    public void LimitWords_BelowWordCount_TruncatesJoinedBySingleSpaces()
    {
        Assert.Equal("one two three", SpeechText.LimitWords("one  two\nthree four five", 3));
    }

    [Fact]
    public void LimitWords_AtOrAboveWordCount_ReturnsUnchanged()
    {
        Assert.Equal("one two three", SpeechText.LimitWords("one two three", 3));
        Assert.Equal("one two three", SpeechText.LimitWords("one two three", 10));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Raiven.Core.Tests --filter "LimitWords"`
Expected: build FAILURE — `The type or namespace name 'SpeechText' could not be found`.

- [ ] **Step 3: Implement**

Create `src/Raiven.Core/Voice/SpeechText.cs`:

```csharp
namespace Raiven.Core.Voice;

public static class SpeechText
{
    /// <summary>
    /// Caps spoken text at <paramref name="limit"/> whitespace-separated words;
    /// limit &lt;= 0 means unlimited. Truncated output is joined by single spaces.
    /// </summary>
    public static string LimitWords(string text, int limit)
    {
        if (limit <= 0) return text;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= limit ? text : string.Join(' ', words.Take(limit));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Raiven.Core.Tests --filter "LimitWords"`
Expected: PASS (4 test cases).

- [ ] **Step 5: Commit**

```bash
git add src/Raiven.Core/Voice/SpeechText.cs tests/Raiven.Core.Tests/SpeechTextTests.cs
git commit -m "feat: spoken word-limit helper"
```

---

### Task 3: Notification event parsing

**Files:**
- Create: `src/Raiven.Core/Events/ClaudeNotificationEvent.cs`
- Modify: `src/Raiven.Core/Events/EventParser.cs`
- Test: `tests/Raiven.Core.Tests/EventParserTests.cs`

**Interfaces:**
- Consumes: existing `RaivenEvent`, private `TryGetString` in EventParser.
- Produces (used by Tasks 4 and 6): `public sealed record ClaudeNotificationEvent(string SessionId, string TranscriptPath, string Cwd, string Message, string? NotificationType);` and `public static bool EventParser.TryParseClaudeNotification(RaivenEvent e, out ClaudeNotificationEvent notification)`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Raiven.Core.Tests/EventParserTests.cs` (match the file's existing style for constructing events via `EventParser.Parse`):

```csharp
[Fact]
public void TryParseClaudeNotification_FullPayload_ParsesAllFields()
{
    var evt = EventParser.Parse(
        """{"hook_event_name":"Notification","session_id":"s1","transcript_path":"C:\\t.jsonl","cwd":"E:\\Repos\\RAIVEN","message":"Claude needs your permission to use Bash","notification_type":"permission_prompt"}""");

    Assert.True(EventParser.TryParseClaudeNotification(evt, out var n));
    Assert.Equal("s1", n.SessionId);
    Assert.Equal(@"C:\t.jsonl", n.TranscriptPath);
    Assert.Equal(@"E:\Repos\RAIVEN", n.Cwd);
    Assert.Equal("Claude needs your permission to use Bash", n.Message);
    Assert.Equal("permission_prompt", n.NotificationType);
}

[Fact]
public void TryParseClaudeNotification_MissingOptionalFields_DefaultsApplied()
{
    var evt = EventParser.Parse(
        """{"hook_event_name":"Notification","session_id":"s1","transcript_path":"C:\\t.jsonl","cwd":"E:\\Repos\\RAIVEN"}""");

    Assert.True(EventParser.TryParseClaudeNotification(evt, out var n));
    Assert.Equal("", n.Message);
    Assert.Null(n.NotificationType);
}

[Fact]
public void TryParseClaudeNotification_MissingRequiredField_ReturnsFalse()
{
    var evt = EventParser.Parse(
        """{"hook_event_name":"Notification","session_id":"s1","cwd":"E:\\Repos\\RAIVEN"}""");

    Assert.False(EventParser.TryParseClaudeNotification(evt, out _));
}

[Fact]
public void TryParseClaudeNotification_StopEvent_ReturnsFalse()
{
    var evt = EventParser.Parse(
        """{"hook_event_name":"Stop","session_id":"s1","transcript_path":"C:\\t.jsonl","cwd":"E:\\Repos\\RAIVEN"}""");

    Assert.False(EventParser.TryParseClaudeNotification(evt, out _));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Raiven.Core.Tests --filter "TryParseClaudeNotification"`
Expected: build FAILURE — `TryParseClaudeNotification` not found.

- [ ] **Step 3: Implement**

Create `src/Raiven.Core/Events/ClaudeNotificationEvent.cs`:

```csharp
namespace Raiven.Core.Events;

public sealed record ClaudeNotificationEvent(
    string SessionId,
    string TranscriptPath,
    string Cwd,
    string Message,
    string? NotificationType);
```

In `src/Raiven.Core/Events/EventParser.cs`, after `TryParseClaudeStop`, add:

```csharp
    public static bool TryParseClaudeNotification(RaivenEvent e, out ClaudeNotificationEvent notification)
    {
        notification = null!;
        if (e.Source != "claude-code" || e.Type != "Notification")
            return false;
        if (!TryGetString(e.Payload, "session_id", out var sessionId) ||
            !TryGetString(e.Payload, "transcript_path", out var transcriptPath) ||
            !TryGetString(e.Payload, "cwd", out var cwd))
            return false;

        TryGetString(e.Payload, "message", out var message);
        var notificationType = TryGetString(e.Payload, "notification_type", out var t) ? t : null;
        notification = new ClaudeNotificationEvent(sessionId, transcriptPath, cwd, message ?? "", notificationType);
        return true;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Raiven.Core.Tests --filter "TryParseClaudeNotification"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Raiven.Core/Events/ClaudeNotificationEvent.cs src/Raiven.Core/Events/EventParser.cs tests/Raiven.Core.Tests/EventParserTests.cs
git commit -m "feat: parse Claude Code Notification hook events"
```

---

### Task 4: QuestionPipeline

**Files:**
- Create: `src/Raiven.Core/Questions/QuestionPipeline.cs`
- Test: `tests/Raiven.Core.Tests/QuestionPipelineTests.cs`

**Interfaces:**
- Consumes: `RaivenConfig` (Task 1), `SpeechText.LimitWords` (Task 2), `ClaudeNotificationEvent` (Task 3), existing `IClaudeClient`, `IVoice`, `TranscriptReader.ReadLastTurn`, `SummaryService.BuildUserContent`-style content building (own copy), `FileLog`.
- Produces (used by Task 6): `public sealed class QuestionPipeline(RaivenConfig config, IClaudeClient claude, IVoice voice)` with `Task AnnounceAsync(ClaudeNotificationEvent evt)` and `public static bool IsQuestion(string? notificationType)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Raiven.Core.Tests/QuestionPipelineTests.cs`:

```csharp
using Raiven.Core.Config;
using Raiven.Core.Events;
using Raiven.Core.Questions;
using Raiven.Core.Summaries;
using Raiven.Core.Voice;

namespace Raiven.Core.Tests;

public class QuestionPipelineTests
{
    private sealed class FakeVoice : IVoice
    {
        public List<string> Spoken { get; } = [];
        public void Speak(string text) => Spoken.Add(text);
    }

    private sealed class FakeClaudeClient : IClaudeClient
    {
        public string Response = "Claude wants permission to run the tests.";
        public Exception? Throws;
        public int Calls;
        public Task<string> CompleteAsync(string systemPrompt, string userContent, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Throws is null ? Task.FromResult(Response) : Task.FromException<string>(Throws);
        }
    }

    private static ClaudeNotificationEvent Evt(string message = "Claude needs your permission to use Bash") =>
        new("s1", Path.Combine(Path.GetTempPath(), "raiven-does-not-exist.jsonl"), @"E:\Repos\RAIVEN", message, "permission_prompt");

    [Theory]
    [InlineData(null, true)]
    [InlineData("permission_prompt", true)]
    [InlineData("idle_prompt", true)]
    [InlineData("something_new", true)]
    [InlineData("auth_success", false)]
    [InlineData("AGENT_COMPLETED", false)]
    [InlineData("elicitation_complete", false)]
    [InlineData("elicitation_response", false)]
    public void IsQuestion_FiltersHousekeepingTypes(string? type, bool expected)
    {
        Assert.Equal(expected, QuestionPipeline.IsQuestion(type));
    }

    [Fact]
    public async Task AnnounceAsync_AnnounceMode_SpeaksFixedLineWithFolder()
    {
        var voice = new FakeVoice();
        var claude = new FakeClaudeClient();
        var pipeline = new QuestionPipeline(new RaivenConfig { QuestionVoice = "announce" }, claude, voice);

        await pipeline.AnnounceAsync(Evt());

        Assert.Equal(["Claude Code has a question in RAIVEN."], voice.Spoken);
        Assert.Equal(0, claude.Calls);
    }

    [Fact]
    public async Task AnnounceAsync_MessageMode_SpeaksWordLimitedMessage()
    {
        var voice = new FakeVoice();
        var config = new RaivenConfig { QuestionVoice = "message", QuestionWordLimit = 4 };
        var pipeline = new QuestionPipeline(config, new FakeClaudeClient(), voice);

        await pipeline.AnnounceAsync(Evt("Claude needs your permission to use Bash"));

        Assert.Equal(["Claude needs your permission"], voice.Spoken);
    }

    [Fact]
    public async Task AnnounceAsync_MessageMode_EmptyMessage_FallsBackToAnnounce()
    {
        var voice = new FakeVoice();
        var pipeline = new QuestionPipeline(new RaivenConfig { QuestionVoice = "message" }, new FakeClaudeClient(), voice);

        await pipeline.AnnounceAsync(Evt(""));

        Assert.Equal(["Claude Code has a question in RAIVEN."], voice.Spoken);
    }

    [Fact]
    public async Task AnnounceAsync_SummaryMode_SpeaksClaudeResponse()
    {
        var voice = new FakeVoice();
        var claude = new FakeClaudeClient();
        var pipeline = new QuestionPipeline(new RaivenConfig { QuestionVoice = "summary" }, claude, voice);

        await pipeline.AnnounceAsync(Evt());

        Assert.Equal(["Claude wants permission to run the tests."], voice.Spoken);
        Assert.Equal(1, claude.Calls);
    }

    [Fact]
    public async Task AnnounceAsync_SummaryMode_ClaudeFails_FallsBackToAnnounce()
    {
        var voice = new FakeVoice();
        var claude = new FakeClaudeClient { Throws = new InvalidOperationException("boom") };
        var pipeline = new QuestionPipeline(new RaivenConfig { QuestionVoice = "summary" }, claude, voice);

        await pipeline.AnnounceAsync(Evt());

        Assert.Equal(["Claude Code has a question in RAIVEN."], voice.Spoken);
    }

    [Fact]
    public async Task AnnounceAsync_UnknownMode_BehavesAsAnnounce()
    {
        var voice = new FakeVoice();
        var pipeline = new QuestionPipeline(new RaivenConfig { QuestionVoice = "yodel" }, new FakeClaudeClient(), voice);

        await pipeline.AnnounceAsync(Evt());

        Assert.Equal(["Claude Code has a question in RAIVEN."], voice.Spoken);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Raiven.Core.Tests --filter "QuestionPipeline"`
Expected: build FAILURE — namespace `Raiven.Core.Questions` not found.

- [ ] **Step 3: Implement**

Create `src/Raiven.Core/Questions/QuestionPipeline.cs`:

```csharp
using System.Text;
using Raiven.Core.Config;
using Raiven.Core.Events;
using Raiven.Core.Logging;
using Raiven.Core.Summaries;
using Raiven.Core.Transcripts;
using Raiven.Core.Voice;

namespace Raiven.Core.Questions;

/// <summary>
/// Speaks an immediate voice notification when Claude Code has a question
/// (permission prompt, waiting for input). No countdown, no history entry.
/// </summary>
public sealed class QuestionPipeline(RaivenConfig config, IClaudeClient claude, IVoice voice)
{
    public const string SystemPrompt =
        "You are RAIVEN, a voice assistant. Claude Code is waiting for the user's attention. " +
        "Reply with ONE short spoken-style sentence telling the user what Claude Code is asking or " +
        "waiting for, in plain conversational language. No markdown, no preamble.";

    private static readonly HashSet<string> IgnoredTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "auth_success", "agent_completed", "elicitation_complete", "elicitation_response",
    };

    /// <summary>Housekeeping notification types are not questions; unknown/missing types are (fail open).</summary>
    public static bool IsQuestion(string? notificationType) =>
        notificationType is null || !IgnoredTypes.Contains(notificationType);

    public async Task AnnounceAsync(ClaudeNotificationEvent evt)
    {
        var folder = Path.GetFileName(evt.Cwd.TrimEnd('\\', '/'));
        if (folder.Length == 0) folder = evt.Cwd;
        var announceLine = $"Claude Code has a question in {folder}.";

        try
        {
            switch (config.QuestionVoice.ToLowerInvariant())
            {
                case "message" when !string.IsNullOrWhiteSpace(evt.Message):
                    voice.Speak(SpeechText.LimitWords(evt.Message, config.QuestionWordLimit));
                    return;
                case "summary":
                    voice.Speak(await SummarizeQuestionAsync(evt));
                    return;
                default: // "announce", "message" with empty message, and unknown values
                    voice.Speak(announceLine);
                    return;
            }
        }
        catch (Exception ex)
        {
            FileLog.Error($"Question announcement failed for {evt.SessionId}; falling back to announce line", ex);
            try { voice.Speak(announceLine); }
            catch (Exception voiceEx) { FileLog.Error("Fallback announcement also failed", voiceEx); }
        }
    }

    private async Task<string> SummarizeQuestionAsync(ClaudeNotificationEvent evt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Claude Code sent this notification:");
        sb.AppendLine(evt.Message.Length > 0 ? evt.Message : "(no message text)");
        try
        {
            var slice = TranscriptReader.ReadLastTurn(evt.TranscriptPath, config.MaxTranscriptChars);
            sb.AppendLine();
            sb.AppendLine("The user's last request was:");
            sb.AppendLine(slice.UserPrompt);
        }
        catch (Exception ex)
        {
            FileLog.Info($"Question summary proceeding without transcript context: {ex.Message}");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        return await claude.CompleteAsync(SystemPrompt, sb.ToString(), cts.Token);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Raiven.Core.Tests --filter "QuestionPipeline"`
Expected: PASS (14 test cases including the theory rows).

- [ ] **Step 5: Commit**

```bash
git add src/Raiven.Core/Questions/QuestionPipeline.cs tests/Raiven.Core.Tests/QuestionPipelineTests.cs
git commit -m "feat: question pipeline with announce, message, and summary voice modes"
```

---

### Task 5: SummaryPipeline message mode

**Files:**
- Modify: `src/Raiven.Core/Summaries/SummaryPipeline.cs`
- Test: `tests/Raiven.Core.Tests/SummaryPipelineTests.cs`

**Interfaces:**
- Consumes: `RaivenConfig.FinishedTurnVoice`/`FinishedTurnWordLimit` (Task 1), `SpeechText.LimitWords` (Task 2).
- Produces: no signature changes — `PlaySummaryAsync(string sessionId)` behavior now depends on `config.FinishedTurnVoice`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Raiven.Core.Tests/SummaryPipelineTests.cs` (reuse the existing `WriteTranscript`, `NewHistory`, fake classes; the transcript's assistant text is "Fixed it. Tests pass."):

```csharp
    [Fact]
    public async Task PlaySummaryAsync_MessageMode_SpeaksLastMessageWithoutClaude()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var claude = new FakeClaudeClient();
        var voice = new FakeVoice();
        var config = new RaivenConfig { FinishedTurnVoice = "message" };
        var pipeline = new SummaryPipeline(registry, config, claude, new FakeNotifier(), voice, NewHistory());

        await pipeline.PlaySummaryAsync("s1");

        Assert.Equal(["Fixed it. Tests pass."], voice.Spoken);
        Assert.Equal(0, claude.Calls);
    }

    [Fact]
    public async Task PlaySummaryAsync_MessageMode_AppliesWordLimitAndCaches()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var voice = new FakeVoice();
        var history = NewHistory();
        var config = new RaivenConfig { FinishedTurnVoice = "message", FinishedTurnWordLimit = 2 };
        var pipeline = new SummaryPipeline(registry, config, new FakeClaudeClient(), new FakeNotifier(), voice, history);

        await pipeline.PlaySummaryAsync("s1");
        await pipeline.PlaySummaryAsync("s1"); // second call must replay from cache

        Assert.Equal(["Fixed it.", "Fixed it."], voice.Spoken);
        var entry = Assert.Single(history.Entries);
        Assert.Equal("Fixed it.", entry.SummaryText);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Raiven.Core.Tests --filter "MessageMode"`
Expected: FAIL — message mode not implemented, so the fake Claude response ("I fixed the login bug.") is spoken instead.

- [ ] **Step 3: Implement**

In `src/Raiven.Core/Summaries/SummaryPipeline.cs`, add `using Raiven.Core.Voice;` and replace the block from `using var cts = ...` through `FileLog.Info($"Summary for {sessionId}: {summary}");` (currently lines 40-42) with:

```csharp
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
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                spokenText = await _summaries.SummarizeAsync(slice, cts.Token);
                FileLog.Info($"Summary for {sessionId}: {spokenText}");
            }
```

and rename the later uses of `summary` to `spokenText` (the `history.Add(...)` entry text and the `voice.Speak(...)` argument).

- [ ] **Step 4: Run the full suite**

Run: `dotnet test tests/Raiven.Core.Tests`
Expected: PASS — 89 passed (63 prior + 26 new across Tasks 1-5: 2 config, 4 speech, 4 parser, 14 question, 2 summary), 2 skipped. All pre-existing summary tests still green (default mode unchanged).

- [ ] **Step 5: Commit**

```bash
git add src/Raiven.Core/Summaries/SummaryPipeline.cs tests/Raiven.Core.Tests/SummaryPipelineTests.cs
git commit -m "feat: read-last-message mode for finished-turn voice"
```

---

### Task 6: ShowQuestion toast, hook snippet, Program wiring

**Files:**
- Modify: `src/Raiven.Core/Notifications/INotifier.cs`
- Modify: `src/Raiven.App/AppSdkNotifier.cs`
- Modify: `src/Raiven.App/Program.cs`
- Modify: `docs/hook-snippet.json`
- Modify: `tests/Raiven.Core.Tests/SummaryPipelineTests.cs` (FakeNotifier gains ShowQuestion)

No dedicated unit tests (Windows-shell-bound toast + Program wiring); gates are the Core suite staying green and `dotnet build Raiven.sln` with 0 errors, plus Task 8 live verification.

- [ ] **Step 1: Extend INotifier**

In `src/Raiven.Core/Notifications/INotifier.cs`, after `ShowFinishedCountdown`, add:

```csharp
    /// <summary>Toast for a Claude Code question / attention request. No actions; informational only.</summary>
    void ShowQuestion(string folderName, string? headline, string message);
```

- [ ] **Step 2: Update FakeNotifier**

In `tests/Raiven.Core.Tests/SummaryPipelineTests.cs`, add to the `FakeNotifier` class:

```csharp
        public List<(string Folder, string? Headline, string Message)> Questions { get; } = [];
        public void ShowQuestion(string folderName, string? headline, string message) => Questions.Add((folderName, headline, message));
```

Run: `dotnet test tests/Raiven.Core.Tests` — expected PASS (89 passed, 2 skipped).

- [ ] **Step 3: Implement ShowQuestion in AppSdkNotifier**

In `src/Raiven.App/AppSdkNotifier.cs`, after `ShowFinishedCountdown`, add:

```csharp
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
```

- [ ] **Step 4: Register the Notification hook in the snippet**

Replace `docs/hook-snippet.json` with:

```json
{
  "hooks": {
    "Stop": [
      {
        "hooks": [
          {
            "type": "http",
            "url": "http://127.0.0.1:9876/events",
            "timeout": 10
          }
        ]
      }
    ],
    "Notification": [
      {
        "hooks": [
          {
            "type": "http",
            "url": "http://127.0.0.1:9876/events",
            "timeout": 10
          }
        ]
      }
    ]
  }
}
```

- [ ] **Step 5: Wire Program.cs**

In `src/Raiven.App/Program.cs`:

1. Add `using Raiven.Core.Questions;` to the usings.

2. In `RunTray`, after `var pipeline = new SummaryPipeline(...)` (line 88), add:

```csharp
        var questions = new QuestionPipeline(config, claude, voice);
```

3. In `HandleEvent`, change the Stop-branch pause gate from `if (!state.Paused)` to:

```csharp
                if (!state.Paused && config.NotifyOnFinishedTurn)
```

4. In `HandleEvent`, insert a new branch between the Stop branch and the final `else`:

```csharp
            else if (EventParser.TryParseClaudeNotification(evt, out var question))
            {
                FileLog.Info($"Notification event for session {question.SessionId}: [{question.NotificationType ?? "unknown"}] {question.Message}");
                if (QuestionPipeline.IsQuestion(question.NotificationType) && config.NotifyOnQuestion && !state.Paused)
                {
                    ChimePlayer.Play(config);
                    var folder = Path.GetFileName(question.Cwd.TrimEnd('\\', '/'));
                    var folderName = folder.Length > 0 ? folder : question.Cwd;
                    string? headline = null;
                    try { headline = TranscriptReader.ReadFirstPrompt(question.TranscriptPath); }
                    catch (Exception ex) { FileLog.Error("Could not read chat headline", ex); }
                    notifier.ShowQuestion(folderName, headline, question.Message);
                    _ = Task.Run(() => questions.AnnounceAsync(question));
                }
            }
```

- [ ] **Step 6: Build and test gates**

Run: `dotnet build Raiven.sln`
Expected: 0 errors.
Run: `dotnet test tests/Raiven.Core.Tests`
Expected: PASS — 89 passed, 2 skipped.

- [ ] **Step 7: Commit**

```bash
git add src/Raiven.Core/Notifications/INotifier.cs src/Raiven.App/AppSdkNotifier.cs src/Raiven.App/Program.cs docs/hook-snippet.json tests/Raiven.Core.Tests/SummaryPipelineTests.cs
git commit -m "feat: question notifications - toast, voice, and Notification hook wiring"
```

---

### Task 7: Tray Settings submenu + README

**Files:**
- Modify: `src/Raiven.App/TrayContext.cs`
- Modify: `src/Raiven.App/Program.cs:182-188` (TrayContext construction)
- Modify: `README.md`

**Interfaces:**
- Consumes: mutable `RaivenConfig` + `Save` (Task 1).
- Produces: `TrayContext` constructor gains two parameters after `state`: `RaivenConfig config, Action saveConfig` (full order: `state, config, saveConfig, history, replaySummary, testToast, testVoice, onPauseChanged`).

- [ ] **Step 1: Add the Settings submenu to TrayContext**

In `src/Raiven.App/TrayContext.cs`:

1. Add `using Raiven.Core.Config;` to the usings.
2. Change the constructor signature to:

```csharp
    public TrayContext(
        AppState state,
        RaivenConfig config,
        Action saveConfig,
        SummaryHistory history,
        Action<SummaryHistoryEntry> replaySummary,
        Action testToast,
        Action testVoice,
        Action<bool>? onPauseChanged = null)
```

3. After the `recentItem` block, add:

```csharp
        var settingsItem = BuildSettingsMenu(config, saveConfig);
```

4. Change the menu assembly to insert it after "Recent summaries":

```csharp
        menu.Items.Add(pauseItem);
        menu.Items.Add(recentItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
```

5. Add the private helpers to the class:

```csharp
    private static ToolStripMenuItem BuildSettingsMenu(RaivenConfig config, Action saveConfig)
    {
        var settings = new ToolStripMenuItem("Settings");

        var notifyTurns = new ToolStripMenuItem("Notify on finished turns")
        {
            CheckOnClick = true,
            Checked = config.NotifyOnFinishedTurn,
        };
        notifyTurns.CheckedChanged += (_, _) => { config.NotifyOnFinishedTurn = notifyTurns.Checked; saveConfig(); };

        var notifyQuestions = new ToolStripMenuItem("Notify on questions")
        {
            CheckOnClick = true,
            Checked = config.NotifyOnQuestion,
        };
        notifyQuestions.CheckedChanged += (_, _) => { config.NotifyOnQuestion = notifyQuestions.Checked; saveConfig(); };

        var turnVoice = new ToolStripMenuItem("Finished turn voice");
        AddRadioGroup(turnVoice,
            [("Haiku summary", "summary"), ("Read last message", "message")],
            config.FinishedTurnVoice,
            value => { config.FinishedTurnVoice = value; saveConfig(); });

        var questionVoice = new ToolStripMenuItem("Question voice");
        AddRadioGroup(questionVoice,
            [("Announce only", "announce"), ("Read message", "message"), ("Haiku summary", "summary")],
            config.QuestionVoice,
            value => { config.QuestionVoice = value; saveConfig(); });

        settings.DropDownItems.Add(notifyTurns);
        settings.DropDownItems.Add(notifyQuestions);
        settings.DropDownItems.Add(new ToolStripSeparator());
        settings.DropDownItems.Add(turnVoice);
        settings.DropDownItems.Add(questionVoice);
        return settings;
    }

    private static void AddRadioGroup(
        ToolStripMenuItem parent, (string Label, string Value)[] options, string current, Action<string> apply)
    {
        foreach (var (label, value) in options)
        {
            var item = new ToolStripMenuItem(label)
            {
                Checked = string.Equals(current, value, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) =>
            {
                foreach (var sibling in parent.DropDownItems.OfType<ToolStripMenuItem>())
                    sibling.Checked = false;
                item.Checked = true;
                apply(value);
            };
            parent.DropDownItems.Add(item);
        }

        // Unknown config value: show the default (first) option as selected.
        if (!parent.DropDownItems.OfType<ToolStripMenuItem>().Any(i => i.Checked))
            ((ToolStripMenuItem)parent.DropDownItems[0]).Checked = true;
    }
```

- [ ] **Step 2: Update the TrayContext construction in Program.cs**

Replace the `Application.Run(new TrayContext(...))` call with:

```csharp
            Application.Run(new TrayContext(
                state,
                config,
                saveConfig: () => config.Save(AppPaths.ConfigFile),
                history,
                replaySummary: entry => Task.Run(() => voice.Speak(entry.SummaryText)),
                testToast: () => { ChimePlayer.Play(config); notifier.ShowFinished("test-session-001", "RAIVEN", "This is a test notification"); },
                testVoice: () => Task.Run(() => voice.Speak("RAIVEN online. All systems operational.")),
                onPauseChanged: paused => { if (paused) countdown.CancelAll(); }));
```

- [ ] **Step 3: Update README**

In `README.md`: in the hook-registration section, note that the snippet now registers **two** hooks (`Stop` and `Notification`) and that existing users must re-merge `docs/hook-snippet.json` into `%USERPROFILE%\.claude\settings.json` to get question notifications. In the configuration section, add rows/description for the six new keys (defaults and meanings per the Global Constraints table) and a short "Question notifications" subsection: chime + toast + immediate voice, tray Settings submenu toggles, word limits are config.json-only, and mode changes replay already-cached turns with their original cached text.

- [ ] **Step 4: Build and test gates**

Run: `dotnet build Raiven.sln`
Expected: 0 errors.
Run: `dotnet test tests/Raiven.Core.Tests`
Expected: PASS — 89 passed, 2 skipped.

- [ ] **Step 5: Commit**

```bash
git add src/Raiven.App/TrayContext.cs src/Raiven.App/Program.cs README.md
git commit -m "feat: tray Settings submenu persisting notification preferences"
```

---

### Task 8: Manual verification

**Files:** none (verification only; fix-up commits if issues surface).

- [ ] **Step 1: Fake question event end-to-end**

Build and run the tray app (`dotnet run --project src/Raiven.App` or the published exe), then POST a fake Notification event:

```powershell
$body = @{ hook_event_name = "Notification"; session_id = "verify-q1"; transcript_path = "<any real transcript path>"; cwd = "E:\Repos\RAIVEN"; message = "Claude needs your permission to use Bash"; notification_type = "permission_prompt" } | ConvertTo-Json
Invoke-RestMethod -Uri "http://127.0.0.1:9876/" -Method Post -Body $body -ContentType "application/json"
```

Verify: chime; toast titled "Claude Code has a question" with headline and message lines plus logo; voice speaks "Claude Code has a question in RAIVEN." within a couple of seconds; log records the notification event; NO countdown appears; no history entry is added.

- [ ] **Step 2: Ignore-list and toggles**

- POST the same event with `notification_type = "auth_success"` → logged but no chime/toast/voice.
- Tray → Settings → uncheck "Notify on questions"; POST the permission event again → nothing happens; confirm `%APPDATA%\Raiven\config.json` now has `"NotifyOnQuestion": false`. Re-check it (config flips back to true and saves).
- Tray → Settings → Question voice → "Read message"; POST again → voice speaks the message text.

- [ ] **Step 3: Finished-turn modes still work**

- POST a Stop event (existing pattern) with default settings → countdown auto-play speaks a Haiku summary (or cached).
- Settings → Finished turn voice → "Read last message"; POST a Stop event for a transcript not yet cached → voice reads the last assistant message verbatim, log shows "Reading last message", no Claude call in the log.
- Uncheck "Notify on finished turns"; POST a Stop event → silent (event logged, registry updated).

- [ ] **Step 4: Real hook registration**

Merge the updated `docs/hook-snippet.json` Notification entry into `%USERPROFILE%\.claude\settings.json` (preserving existing content), then trigger a real attention request in any Claude Code session (e.g. run a command that needs permission approval) and confirm the question notification arrives.

- [ ] **Step 5: Fix anything found**

Any defects go through the usual loop (failing test where feasible → fix → green → commit with `fix:` prefix).
