# Auto-play Summary, Richer Toasts, and Summary History Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finished-turn toasts count down 5 seconds with a filling progress bar and auto-play the summary (Play now / Abort buttons), show the raven logo and chat headline in the popup, and the last 5 summaries are cached to disk and replayable from the tray menu without calling Claude Haiku again.

**Architecture:** All new logic lives in `Raiven.Core` (testable, no WinUI deps): `AutoPlayCountdown` (per-session timers), `SummaryHistory` (persisted last-5 cache), `TranscriptReader.ReadFirstPrompt` (headline). `AppSdkNotifier` (Raiven.App) grows a countdown-toast variant with a bindable progress bar updated via `AppNotificationManager.UpdateAsync`. `Program.cs` wires countdown expiry/abort/play-now to the existing `SummaryPipeline`, which now checks the history cache before calling Claude.

**Tech Stack:** .NET 10, Windows App SDK 2.2 (`Microsoft.Windows.AppNotifications`), WinForms tray, xUnit.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-03-autoplay-toast-history-design.md`.
- Target frameworks stay as-is: `net10.0` (Core), `net10.0-windows10.0.17763.0` (App). No new NuGet packages.
- New config keys: `AutoPlaySummary` (bool, default `true`), `AutoPlayDelaySeconds` (int, default `5`).
- History capacity is exactly 5, newest first, persisted to `%APPDATA%\Raiven\history.json`.
- All failure paths log via `FileLog` and degrade gracefully — never crash the tray app.
- Run tests with: `dotnet test tests/Raiven.Core.Tests` (from repo root `e:\Repos\RAIVEN`).
- Commit messages follow existing style: `feat:`, `fix:`, `docs:`, `test:` prefixes, plus the Claude co-author trailer.

---

### Task 1: Config keys for auto-play

**Files:**
- Modify: `src/Raiven.Core/Config/RaivenConfig.cs`
- Test: `tests/Raiven.Core.Tests/RaivenConfigTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `RaivenConfig.AutoPlaySummary` (bool, default `true`), `RaivenConfig.AutoPlayDelaySeconds` (int, default `5`) — used by Task 7.

- [ ] **Step 1: Write the failing test**

Add to `tests/Raiven.Core.Tests/RaivenConfigTests.cs` (inside the existing `RaivenConfigTests` class):

```csharp
[Fact]
public void LoadOrCreate_MissingFile_HasAutoPlayDefaults()
{
    var config = RaivenConfig.LoadOrCreate(TempConfigPath());

    Assert.True(config.AutoPlaySummary);
    Assert.Equal(5, config.AutoPlayDelaySeconds);
}

[Fact]
public void LoadOrCreate_ExistingFile_ReadsAutoPlayValues()
{
    var path = TempConfigPath();
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, """{"AutoPlaySummary":false,"AutoPlayDelaySeconds":9}""");

    var config = RaivenConfig.LoadOrCreate(path);

    Assert.False(config.AutoPlaySummary);
    Assert.Equal(9, config.AutoPlayDelaySeconds);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Raiven.Core.Tests --filter "AutoPlay"`
Expected: build FAILURE — `'RaivenConfig' does not contain a definition for 'AutoPlaySummary'`.

- [ ] **Step 3: Add the properties**

In `src/Raiven.Core/Config/RaivenConfig.cs`, after the `CliModelAlias` property (line 16), add:

```csharp
    public bool AutoPlaySummary { get; init; } = true;
    public int AutoPlayDelaySeconds { get; init; } = 5;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Raiven.Core.Tests --filter "AutoPlay"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Raiven.Core/Config/RaivenConfig.cs tests/Raiven.Core.Tests/RaivenConfigTests.cs
git commit -m "feat: add AutoPlaySummary and AutoPlayDelaySeconds config keys"
```

---

### Task 2: TranscriptReader.ReadFirstPrompt (chat headline)

**Files:**
- Modify: `src/Raiven.Core/Transcripts/TranscriptReader.cs`
- Test: `tests/Raiven.Core.Tests/TranscriptReaderTests.cs`

**Interfaces:**
- Consumes: the existing private `TryExtractPrompt(JsonElement, out string)` in the same class (skips `isMeta` lines and `tool_result` content).
- Produces: `public static string? ReadFirstPrompt(string transcriptPath, int maxChars = 60)` — returns the first user prompt flattened to one line and truncated to `maxChars` with a trailing `…`, or `null` when the file is missing or has no prompt. Used by Tasks 5 and 7.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Raiven.Core.Tests/TranscriptReaderTests.cs` (inside the existing test class; it already has a temp-file helper pattern — add this helper if no equivalent exists):

```csharp
private static string WriteLines(params string[] lines)
{
    var path = Path.Combine(Path.GetTempPath(), $"raiven-first-{Guid.NewGuid():N}.jsonl");
    File.WriteAllLines(path, lines);
    return path;
}

[Fact]
public void ReadFirstPrompt_ReturnsFirstUserPrompt()
{
    var path = WriteLines(
        """{"type":"user","message":{"role":"user","content":"Fix the login bug"}}""",
        """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Done."}]}}""",
        """{"type":"user","message":{"role":"user","content":"Now add tests"}}""");

    Assert.Equal("Fix the login bug", TranscriptReader.ReadFirstPrompt(path));
}

[Fact]
public void ReadFirstPrompt_SkipsMetaAndToolResultLines()
{
    var path = WriteLines(
        """{"type":"queue-operation","operation":"enqueue"}""",
        """{"type":"user","isMeta":true,"message":{"role":"user","content":"meta noise"}}""",
        """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"tool output"}]}}""",
        """{"type":"user","message":{"role":"user","content":"The real prompt"}}""");

    Assert.Equal("The real prompt", TranscriptReader.ReadFirstPrompt(path));
}

[Fact]
public void ReadFirstPrompt_TruncatesAndFlattensNewlines()
{
    var longPrompt = "I want you to extend the software\nby adding auto play summary and lots more text beyond sixty characters";
    var path = WriteLines(
        $$"""{"type":"user","message":{"role":"user","content":{{System.Text.Json.JsonSerializer.Serialize(longPrompt)}}}}""");

    var result = TranscriptReader.ReadFirstPrompt(path);

    Assert.NotNull(result);
    Assert.True(result!.Length <= 61); // 60 chars + ellipsis
    Assert.EndsWith("…", result);
    Assert.DoesNotContain("\n", result);
}

[Fact]
public void ReadFirstPrompt_MissingFileOrNoPrompt_ReturnsNull()
{
    Assert.Null(TranscriptReader.ReadFirstPrompt(Path.Combine(Path.GetTempPath(), "raiven-nope.jsonl")));

    var emptyish = WriteLines("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"hi"}]}}""");
    Assert.Null(TranscriptReader.ReadFirstPrompt(emptyish));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Raiven.Core.Tests --filter "ReadFirstPrompt"`
Expected: build FAILURE — `'TranscriptReader' does not contain a definition for 'ReadFirstPrompt'`.

- [ ] **Step 3: Implement ReadFirstPrompt**

In `src/Raiven.Core/Transcripts/TranscriptReader.cs`, after the `ReadLastTurn` method, add:

```csharp
    /// <summary>
    /// Returns the chat's first user prompt as a single-line headline, truncated to
    /// <paramref name="maxChars"/> with an ellipsis; null when the file is missing or
    /// contains no user prompt. Stops reading at the first match.
    /// </summary>
    public static string? ReadFirstPrompt(string transcriptPath, int maxChars = 60)
    {
        if (!File.Exists(transcriptPath))
            return null;

        foreach (var line in File.ReadLines(transcriptPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String) continue;
                if (typeProp.GetString() != "user") continue;
                if (!TryExtractPrompt(root, out var prompt)) continue;

                var headline = prompt.ReplaceLineEndings(" ").Trim();
                if (headline.Length == 0) continue;
                return headline.Length <= maxChars ? headline : headline[..maxChars].TrimEnd() + "…";
            }
        }

        return null;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Raiven.Core.Tests --filter "ReadFirstPrompt"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Raiven.Core/Transcripts/TranscriptReader.cs tests/Raiven.Core.Tests/TranscriptReaderTests.cs
git commit -m "feat: extract chat headline from transcript's first user prompt"
```

---

### Task 3: SummaryHistory (persisted last-5 cache)

**Files:**
- Create: `src/Raiven.Core/Summaries/SummaryHistory.cs`
- Test: `tests/Raiven.Core.Tests/SummaryHistoryTests.cs`

**Interfaces:**
- Consumes: `FileLog` (safe when unconfigured — it no-ops).
- Produces (used by Tasks 5 and 7):

```csharp
public sealed record SummaryHistoryEntry(
    string SessionId, string Headline, string Folder, DateTimeOffset GeneratedAt,
    string TranscriptPath, DateTime TranscriptLastWriteUtc, string SummaryText);

public sealed class SummaryHistory
{
    public static SummaryHistory Load(string filePath, int capacity = 5);
    public IReadOnlyList<SummaryHistoryEntry> Entries { get; }   // snapshot, newest first
    public void Add(SummaryHistoryEntry entry);                   // dedupes, trims, saves
    public bool TryGetCached(string sessionId, DateTime transcriptLastWriteUtc, out SummaryHistoryEntry entry);
}
```

- [ ] **Step 1: Write the failing tests**

Create `tests/Raiven.Core.Tests/SummaryHistoryTests.cs`:

```csharp
using Raiven.Core.Summaries;

namespace Raiven.Core.Tests;

public class SummaryHistoryTests
{
    private static string TempHistoryPath() =>
        Path.Combine(Path.GetTempPath(), $"raiven-hist-{Guid.NewGuid():N}", "history.json");

    private static SummaryHistoryEntry Entry(string sessionId, string text = "summary", DateTime? lastWrite = null) =>
        new(sessionId, $"Headline {sessionId}", "RAIVEN", DateTimeOffset.Now,
            $@"C:\transcripts\{sessionId}.jsonl", lastWrite ?? new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc), text);

    [Fact]
    public void Load_MissingFile_StartsEmpty()
    {
        var history = SummaryHistory.Load(TempHistoryPath());
        Assert.Empty(history.Entries);
    }

    [Fact]
    public void Load_CorruptFile_StartsEmpty()
    {
        var path = TempHistoryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{not json[");

        var history = SummaryHistory.Load(path);

        Assert.Empty(history.Entries);
    }

    [Fact]
    public void Add_KeepsNewestFirst_AndTrimsToCapacity()
    {
        var history = SummaryHistory.Load(TempHistoryPath(), capacity: 5);
        for (var i = 1; i <= 7; i++)
            history.Add(Entry($"s{i}"));

        Assert.Equal(5, history.Entries.Count);
        Assert.Equal("s7", history.Entries[0].SessionId);
        Assert.Equal("s3", history.Entries[4].SessionId);
    }

    [Fact]
    public void Add_PersistsAcrossReload()
    {
        var path = TempHistoryPath();
        var history = SummaryHistory.Load(path);
        history.Add(Entry("s1", "I fixed the login bug."));

        var reloaded = SummaryHistory.Load(path);

        Assert.Single(reloaded.Entries);
        Assert.Equal("I fixed the login bug.", reloaded.Entries[0].SummaryText);
    }

    [Fact]
    public void Add_SameSessionAndTimestamp_ReplacesInsteadOfDuplicating()
    {
        var history = SummaryHistory.Load(TempHistoryPath());
        var stamp = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);
        history.Add(Entry("s1", "first", stamp));
        history.Add(Entry("s1", "second", stamp));

        Assert.Single(history.Entries);
        Assert.Equal("second", history.Entries[0].SummaryText);
    }

    [Fact]
    public void TryGetCached_MatchesOnSessionAndTranscriptTimestamp()
    {
        var history = SummaryHistory.Load(TempHistoryPath());
        var stamp = new DateTime(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);
        history.Add(Entry("s1", "cached text", stamp));

        Assert.True(history.TryGetCached("s1", stamp, out var hit));
        Assert.Equal("cached text", hit.SummaryText);

        Assert.False(history.TryGetCached("s1", stamp.AddSeconds(1), out _)); // transcript changed
        Assert.False(history.TryGetCached("s2", stamp, out _));               // different session
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Raiven.Core.Tests --filter "SummaryHistory"`
Expected: build FAILURE — `The type or namespace name 'SummaryHistory' could not be found`.

- [ ] **Step 3: Implement SummaryHistory**

Create `src/Raiven.Core/Summaries/SummaryHistory.cs`:

```csharp
using System.Text.Json;
using Raiven.Core.Logging;

namespace Raiven.Core.Summaries;

public sealed record SummaryHistoryEntry(
    string SessionId,
    string Headline,
    string Folder,
    DateTimeOffset GeneratedAt,
    string TranscriptPath,
    DateTime TranscriptLastWriteUtc,
    string SummaryText);

/// <summary>
/// Last-N generated summaries, newest first, persisted to a JSON file so replays
/// never need another Claude call. All persistence failures are best-effort.
/// </summary>
public sealed class SummaryHistory
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly int _capacity;
    private readonly List<SummaryHistoryEntry> _entries;
    private readonly Lock _lock = new();

    private SummaryHistory(string filePath, int capacity, List<SummaryHistoryEntry> entries)
    {
        _filePath = filePath;
        _capacity = capacity;
        _entries = entries;
    }

    public static SummaryHistory Load(string filePath, int capacity = 5)
    {
        List<SummaryHistoryEntry> entries = [];
        try
        {
            if (File.Exists(filePath))
                entries = JsonSerializer.Deserialize<List<SummaryHistoryEntry>>(File.ReadAllText(filePath)) ?? [];
        }
        catch (Exception ex)
        {
            FileLog.Error($"Could not load summary history from {filePath}; starting empty", ex);
            entries = [];
        }

        if (entries.Count > capacity)
            entries = entries.Take(capacity).ToList();
        return new SummaryHistory(filePath, capacity, entries);
    }

    public IReadOnlyList<SummaryHistoryEntry> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    public void Add(SummaryHistoryEntry entry)
    {
        lock (_lock)
        {
            _entries.RemoveAll(e =>
                e.SessionId == entry.SessionId && e.TranscriptLastWriteUtc == entry.TranscriptLastWriteUtc);
            _entries.Insert(0, entry);
            if (_entries.Count > _capacity)
                _entries.RemoveRange(_capacity, _entries.Count - _capacity);
            Save();
        }
    }

    public bool TryGetCached(string sessionId, DateTime transcriptLastWriteUtc, out SummaryHistoryEntry entry)
    {
        lock (_lock)
        {
            entry = _entries.FirstOrDefault(e =>
                e.SessionId == sessionId && e.TranscriptLastWriteUtc == transcriptLastWriteUtc)!;
            return entry is not null;
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_entries, Options));
        }
        catch (Exception ex)
        {
            FileLog.Error($"Could not save summary history to {_filePath}", ex);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Raiven.Core.Tests --filter "SummaryHistory"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Raiven.Core/Summaries/SummaryHistory.cs tests/Raiven.Core.Tests/SummaryHistoryTests.cs
git commit -m "feat: persisted last-5 summary history with cache lookup"
```

---

### Task 4: AutoPlayCountdown (per-session countdown timers)

**Files:**
- Create: `src/Raiven.Core/Notifications/AutoPlayCountdown.cs`
- Test: `tests/Raiven.Core.Tests/AutoPlayCountdownTests.cs`

**Interfaces:**
- Consumes: nothing project-specific.
- Produces (used by Task 7):

```csharp
public sealed class AutoPlayCountdown(TimeSpan total, TimeSpan tick) : IDisposable
{
    public event Action<string, double>? Progress; // (sessionId, fraction 0..1), fired each tick
    public event Action<string>? Expired;          // fired at most once per Start, only if not cancelled
    public void Start(string sessionId);           // restarts any countdown already running for that session
    public bool Cancel(string sessionId);          // true if a live countdown was cancelled
    public void CancelAll();
    public void Dispose();                         // = CancelAll
}
```

Events fire on thread-pool threads — callers must not assume a UI thread.

- [ ] **Step 1: Write the failing tests**

Create `tests/Raiven.Core.Tests/AutoPlayCountdownTests.cs`. Timings are deliberately short (total 200 ms, tick 40 ms) so tests run fast without a clock abstraction; waits use generous timeouts to avoid flakes.

```csharp
using Raiven.Core.Notifications;

namespace Raiven.Core.Tests;

public class AutoPlayCountdownTests
{
    private static readonly TimeSpan Total = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Expiry_FiresOnce_AndProgressReachesFull()
    {
        using var countdown = new AutoPlayCountdown(Total, Tick);
        var expired = new List<string>();
        var lastFraction = 0.0;
        var done = new TaskCompletionSource();
        countdown.Progress += (_, f) => lastFraction = f;
        countdown.Expired += id => { lock (expired) expired.Add(id); done.TrySetResult(); };

        countdown.Start("s1");
        await done.Task.WaitAsync(WaitLimit);
        await Task.Delay(Total); // room for any (buggy) second expiry

        Assert.Equal(["s1"], expired);
        Assert.Equal(1.0, lastFraction, precision: 5);
    }

    [Fact]
    public async Task Cancel_PreventsExpiry()
    {
        using var countdown = new AutoPlayCountdown(Total, Tick);
        var expiredCount = 0;
        countdown.Expired += _ => Interlocked.Increment(ref expiredCount);

        countdown.Start("s1");
        Assert.True(countdown.Cancel("s1"));
        await Task.Delay(Total + Total);

        Assert.Equal(0, expiredCount);
        Assert.False(countdown.Cancel("s1")); // already gone
    }

    [Fact]
    public async Task Start_SameSession_RestartsWithSingleExpiry()
    {
        using var countdown = new AutoPlayCountdown(Total, Tick);
        var expiredCount = 0;
        var done = new TaskCompletionSource();
        countdown.Expired += _ => { Interlocked.Increment(ref expiredCount); done.TrySetResult(); };

        countdown.Start("s1");
        await Task.Delay(Tick * 2);
        countdown.Start("s1"); // restart mid-flight

        await done.Task.WaitAsync(WaitLimit);
        await Task.Delay(Total + Total);

        Assert.Equal(1, expiredCount);
    }

    [Fact]
    public async Task Sessions_AreIndependent()
    {
        using var countdown = new AutoPlayCountdown(Total, Tick);
        var expired = new List<string>();
        var both = new TaskCompletionSource();
        countdown.Expired += id =>
        {
            lock (expired) { expired.Add(id); if (expired.Count == 2) both.TrySetResult(); }
        };

        countdown.Start("s1");
        countdown.Start("s2");
        countdown.Cancel("s1");
        countdown.Start("s1");

        await both.Task.WaitAsync(WaitLimit);

        lock (expired)
        {
            Assert.Equal(2, expired.Count);
            Assert.Contains("s1", expired);
            Assert.Contains("s2", expired);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Raiven.Core.Tests --filter "AutoPlayCountdown"`
Expected: build FAILURE — `The type or namespace name 'AutoPlayCountdown' could not be found`.

- [ ] **Step 3: Implement AutoPlayCountdown**

Create `src/Raiven.Core/Notifications/AutoPlayCountdown.cs`:

```csharp
using System.Collections.Concurrent;

namespace Raiven.Core.Notifications;

/// <summary>
/// Per-session countdown timers for auto-playing summaries. Progress ticks report a
/// 0..1 fraction; Expired fires at most once per Start and never after a successful
/// Cancel. Events run on thread-pool threads.
/// </summary>
public sealed class AutoPlayCountdown(TimeSpan total, TimeSpan tick) : IDisposable
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    public event Action<string, double>? Progress;
    public event Action<string>? Expired;

    public void Start(string sessionId)
    {
        var cts = new CancellationTokenSource();
        _running.AddOrUpdate(sessionId, cts, (_, old) =>
        {
            old.Cancel();
            old.Dispose();
            return cts;
        });
        _ = RunAsync(sessionId, cts);
    }

    public bool Cancel(string sessionId)
    {
        if (_running.TryRemove(sessionId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            return true;
        }
        return false;
    }

    public void CancelAll()
    {
        foreach (var sessionId in _running.Keys)
            Cancel(sessionId);
    }

    public void Dispose() => CancelAll();

    private async Task RunAsync(string sessionId, CancellationTokenSource cts)
    {
        var steps = Math.Max(1, (int)Math.Round(total.TotalMilliseconds / tick.TotalMilliseconds));
        try
        {
            for (var i = 1; i <= steps; i++)
            {
                await Task.Delay(tick, cts.Token).ConfigureAwait(false);
                Progress?.Invoke(sessionId, (double)i / steps);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Only the task that removes its own CTS may fire Expired; a concurrent
        // Cancel or restart wins this race and suppresses it.
        if (_running.TryRemove(new KeyValuePair<string, CancellationTokenSource>(sessionId, cts)))
        {
            cts.Dispose();
            Expired?.Invoke(sessionId);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Raiven.Core.Tests --filter "AutoPlayCountdown"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Raiven.Core/Notifications/AutoPlayCountdown.cs tests/Raiven.Core.Tests/AutoPlayCountdownTests.cs
git commit -m "feat: per-session auto-play countdown with cancel and restart semantics"
```

---

### Task 5: SummaryPipeline cache-before-generate

**Files:**
- Modify: `src/Raiven.Core/Summaries/SummaryPipeline.cs`
- Test: `tests/Raiven.Core.Tests/SummaryPipelineTests.cs`

**Interfaces:**
- Consumes: `SummaryHistory` (Task 3), `TranscriptReader.ReadFirstPrompt` (Task 2).
- Produces: `SummaryPipeline` constructor gains a sixth parameter `SummaryHistory history`. `PlaySummaryAsync(string sessionId)` signature unchanged (Task 7 relies on this).

- [ ] **Step 1: Update existing tests and add new ones**

In `tests/Raiven.Core.Tests/SummaryPipelineTests.cs`:

1. Change `FakeClaudeClient` to count calls:

```csharp
    private sealed class FakeClaudeClient : IClaudeClient
    {
        public string Response = "I fixed the login bug.";
        public Exception? Throws;
        public int Calls;
        public Task<string> CompleteAsync(string systemPrompt, string userContent, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Throws is null ? Task.FromResult(Response) : Task.FromException<string>(Throws);
        }
    }
```

2. Add a history helper and update every `new SummaryPipeline(...)` call in the file to pass a fresh history as the sixth argument:

```csharp
    private static SummaryHistory NewHistory() =>
        SummaryHistory.Load(Path.Combine(Path.GetTempPath(), $"raiven-hist-{Guid.NewGuid():N}", "history.json"));
```

Example updated construction (apply the same pattern to all four existing tests):

```csharp
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), new FakeClaudeClient(), notifier, voice, NewHistory());
```

Also add `using Raiven.Core.Summaries;` if not already present (it is).

3. Add the new tests:

```csharp
    [Fact]
    public async Task PlaySummaryAsync_SecondCallSameTranscript_UsesCacheAndSkipsClaude()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var claude = new FakeClaudeClient();
        var voice = new FakeVoice();
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), claude, new FakeNotifier(), voice, NewHistory());

        await pipeline.PlaySummaryAsync("s1");
        await pipeline.PlaySummaryAsync("s1");

        Assert.Equal(1, claude.Calls);
        Assert.Equal(["I fixed the login bug.", "I fixed the login bug."], voice.Spoken);
    }

    [Fact]
    public async Task PlaySummaryAsync_RecordsHistoryEntryWithHeadline()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var history = NewHistory();
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), new FakeClaudeClient(), new FakeNotifier(), new FakeVoice(), history);

        await pipeline.PlaySummaryAsync("s1");

        var entry = Assert.Single(history.Entries);
        Assert.Equal("s1", entry.SessionId);
        Assert.Equal("Fix the login bug", entry.Headline);
        Assert.Equal("RAIVEN", entry.Folder);
        Assert.Equal("I fixed the login bug.", entry.SummaryText);
    }

    [Fact]
    public async Task PlaySummaryAsync_ChangedTranscript_RegeneratesInsteadOfCaching()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        var path = WriteTranscript();
        registry.Upsert("s1", path, @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var claude = new FakeClaudeClient();
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), claude, new FakeNotifier(), new FakeVoice(), NewHistory());

        await pipeline.PlaySummaryAsync("s1");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1)); // simulate a new turn
        await pipeline.PlaySummaryAsync("s1");

        Assert.Equal(2, claude.Calls);
    }

    [Fact]
    public async Task PlaySummaryAsync_ClaudeFails_RecordsNothing()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", WriteTranscript(), @"E:\Repos\RAIVEN", DateTimeOffset.Now);
        var history = NewHistory();
        var claude = new FakeClaudeClient { Throws = new InvalidOperationException("boom") };
        var pipeline = new SummaryPipeline(registry, new RaivenConfig(), claude, new FakeNotifier(), new FakeVoice(), history);

        await pipeline.PlaySummaryAsync("s1");

        Assert.Empty(history.Entries);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Raiven.Core.Tests --filter "SummaryPipeline"`
Expected: build FAILURE — the `SummaryPipeline` constructor does not take 6 arguments yet.

- [ ] **Step 3: Implement cache-before-generate**

Replace the body of `src/Raiven.Core/Summaries/SummaryPipeline.cs` with:

```csharp
using Raiven.Core.Config;
using Raiven.Core.Logging;
using Raiven.Core.Notifications;
using Raiven.Core.Sessions;
using Raiven.Core.Transcripts;
using Raiven.Core.Voice;

namespace Raiven.Core.Summaries;

public sealed class SummaryPipeline(
    SessionRegistry registry,
    RaivenConfig config,
    IClaudeClient claude,
    INotifier notifier,
    IVoice voice,
    SummaryHistory history)
{
    private readonly SummaryService _summaries = new(claude);

    public async Task PlaySummaryAsync(string sessionId)
    {
        try
        {
            if (!registry.TryGet(sessionId, DateTimeOffset.Now, out var info))
            {
                notifier.ShowError("That session's details are no longer available.");
                return;
            }

            var lastWriteUtc = File.GetLastWriteTimeUtc(info.TranscriptPath);
            if (history.TryGetCached(sessionId, lastWriteUtc, out var cached))
            {
                FileLog.Info($"Replaying cached summary for {sessionId}");
                voice.Speak(cached.SummaryText);
                return;
            }

            var slice = TranscriptReader.ReadLastTurn(info.TranscriptPath, config.MaxTranscriptChars);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var summary = await _summaries.SummarizeAsync(slice, cts.Token);
            FileLog.Info($"Summary for {sessionId}: {summary}");

            var folder = Path.GetFileName(info.Cwd.TrimEnd('\\', '/'));
            if (folder.Length == 0) folder = info.Cwd;
            var headline = TranscriptReader.ReadFirstPrompt(info.TranscriptPath) ?? folder;
            history.Add(new SummaryHistoryEntry(
                sessionId, headline, folder, DateTimeOffset.Now, info.TranscriptPath, lastWriteUtc, summary));

            voice.Speak(summary);
        }
        catch (Exception ex)
        {
            FileLog.Error($"Summary failed for session {sessionId}", ex);
            notifier.ShowError("Couldn't get the summary - check the RAIVEN log for details.");
        }
    }
}
```

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test tests/Raiven.Core.Tests`
Expected: PASS — all tests, including the 4 pre-existing SummaryPipeline tests with updated constructors.

- [ ] **Step 5: Commit**

```bash
git add src/Raiven.Core/Summaries/SummaryPipeline.cs tests/Raiven.Core.Tests/SummaryPipelineTests.cs
git commit -m "feat: replay cached summaries instead of regenerating unchanged turns"
```

---

### Task 6: INotifier extension + AppSdkNotifier countdown toast, logo, headline

**Files:**
- Modify: `src/Raiven.Core/Notifications/INotifier.cs`
- Modify: `src/Raiven.App/AppSdkNotifier.cs`
- Modify: `src/Raiven.App/Raiven.App.csproj`
- Create: `src/Raiven.App/Assets/raiven-logo.png` (copied from `Images/Raiven-logo.png`)
- Modify: `tests/Raiven.Core.Tests/SummaryPipelineTests.cs` (FakeNotifier gains new members)

This task has no meaningful unit tests (the App SDK notifier is Windows-shell-bound); the gate is a clean build plus the manual verification in Task 8.

- [ ] **Step 1: Extend INotifier**

Replace `src/Raiven.Core/Notifications/INotifier.cs` with:

```csharp
namespace Raiven.Core.Notifications;

public interface INotifier
{
    /// <summary>Raised with the session id when the user asks to hear a summary (toast body, "Play summary", or "Play now").</summary>
    event Action<string>? PlaySummaryRequested;

    /// <summary>Raised with the session id when the user aborts a pending auto-play.</summary>
    event Action<string>? AbortRequested;

    /// <summary>Static finished-turn toast (no countdown). Headline is the chat's first prompt, or null to fall back to the folder line.</summary>
    void ShowFinished(string sessionId, string folderName, string? headline);

    /// <summary>Finished-turn toast with an auto-play progress bar and Play now / Abort buttons.</summary>
    void ShowFinishedCountdown(string sessionId, string folderName, string? headline, int totalSeconds);

    /// <summary>Advance the countdown toast's progress bar (fraction 0..1). Safe to call for a dismissed toast.</summary>
    void UpdateCountdownProgress(string sessionId, double fraction);

    /// <summary>Remove the toast for a session (e.g. once auto-play fires).</summary>
    void RemoveNotification(string sessionId);

    /// <summary>Show a non-fatal error to the user.</summary>
    void ShowError(string message);
}
```

- [ ] **Step 2: Update FakeNotifier in the tests**

In `tests/Raiven.Core.Tests/SummaryPipelineTests.cs`, replace the `FakeNotifier` class with:

```csharp
    private sealed class FakeNotifier : INotifier
    {
        public event Action<string>? PlaySummaryRequested;
        public event Action<string>? AbortRequested;
        public List<string> Errors { get; } = [];
        public List<(string SessionId, string Folder)> Finished { get; } = [];
        public void ShowFinished(string sessionId, string folderName, string? headline) => Finished.Add((sessionId, folderName));
        public void ShowFinishedCountdown(string sessionId, string folderName, string? headline, int totalSeconds) => Finished.Add((sessionId, folderName));
        public void ShowError(string message) => Errors.Add(message);
        public void UpdateCountdownProgress(string sessionId, double fraction) { }
        public void RemoveNotification(string sessionId) { }
        public void RaisePlaySummary(string sessionId) => PlaySummaryRequested?.Invoke(sessionId);
        public void RaiseAbort(string sessionId) => AbortRequested?.Invoke(sessionId);
    }
```

- [ ] **Step 3: Ship the logo PNG**

```bash
cp Images/Raiven-logo.png src/Raiven.App/Assets/raiven-logo.png
```

In `src/Raiven.App/Raiven.App.csproj`, inside the existing `<ItemGroup>` that contains the `raiven.ico` entry, add:

```xml
    <None Include="Assets\raiven-logo.png">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
```

- [ ] **Step 4: Rewrite AppSdkNotifier**

Replace `src/Raiven.App/AppSdkNotifier.cs` with:

```csharp
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Raiven.Core.Logging;
using Raiven.Core.Notifications;

namespace Raiven.App;

public sealed class AppSdkNotifier : INotifier
{
    public event Action<string>? PlaySummaryRequested;
    public event Action<string>? AbortRequested;

    private readonly Dictionary<string, uint> _progressSequences = [];
    private readonly Lock _sequenceLock = new();

    public AppSdkNotifier()
    {
        // Subscribe BEFORE Register, per the Windows App SDK contract, so activations
        // that arrive during registration are not lost.
        AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
        AppNotificationManager.Default.Register();
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try
        {
            if (!args.Arguments.TryGetValue("action", out var action) ||
                !args.Arguments.TryGetValue("sessionId", out var sessionId))
                return;

            switch (action)
            {
                case "playSummary" or "playNow":
                    FileLog.Info($"Toast activated: {action} for {sessionId}");
                    PlaySummaryRequested?.Invoke(sessionId);
                    break;
                case "abort":
                    FileLog.Info($"Toast activated: abort for {sessionId}");
                    AbortRequested?.Invoke(sessionId);
                    break;
            }
        }
        catch (Exception ex)
        {
            FileLog.Error("Toast activation handling failed", ex);
        }
    }

    public void ShowFinished(string sessionId, string folderName, string? headline)
    {
        var builder = new AppNotificationBuilder()
            .AddArgument("action", "playSummary")
            .AddArgument("sessionId", sessionId);
        AddHeadline(builder, folderName, headline);
        builder
            .AddText("Click to hear a summary.")
            .AddButton(new AppNotificationButton("Play summary")
                .AddArgument("action", "playSummary")
                .AddArgument("sessionId", sessionId))
            .SetDuration(AppNotificationDuration.Long);
        TrySetLogo(builder);

        var notification = builder.BuildNotification();
        notification.Tag = sessionId;
        AppNotificationManager.Default.Show(notification);
    }

    public void ShowFinishedCountdown(string sessionId, string folderName, string? headline, int totalSeconds)
    {
        var builder = new AppNotificationBuilder()
            .AddArgument("action", "playNow")
            .AddArgument("sessionId", sessionId);
        AddHeadline(builder, folderName, headline);
        builder
            .AddProgressBar(new AppNotificationProgressBar()
                .BindValue()
                .BindStatus())
            .AddButton(new AppNotificationButton("Play now")
                .AddArgument("action", "playNow")
                .AddArgument("sessionId", sessionId))
            .AddButton(new AppNotificationButton("Abort")
                .AddArgument("action", "abort")
                .AddArgument("sessionId", sessionId))
            .SetDuration(AppNotificationDuration.Long);
        TrySetLogo(builder);

        var notification = builder.BuildNotification();
        notification.Tag = sessionId;
        notification.Progress = new AppNotificationProgressData(sequenceNumber: 1)
        {
            Value = 0,
            Status = $"Auto-playing in {totalSeconds}s…",
        };
        lock (_sequenceLock) _progressSequences[sessionId] = 1;
        AppNotificationManager.Default.Show(notification);
    }

    public void UpdateCountdownProgress(string sessionId, double fraction)
    {
        try
        {
            uint sequence;
            lock (_sequenceLock)
            {
                sequence = _progressSequences.TryGetValue(sessionId, out var current) ? current + 1 : 2;
                _progressSequences[sessionId] = sequence;
            }

            var data = new AppNotificationProgressData(sequence)
            {
                Value = Math.Clamp(fraction, 0, 1),
                Status = "Auto-playing summary…",
            };
            _ = AppNotificationManager.Default.UpdateAsync(data, sessionId);
        }
        catch (Exception ex)
        {
            FileLog.Error($"Countdown progress update failed for {sessionId}", ex);
        }
    }

    public void RemoveNotification(string sessionId)
    {
        try
        {
            lock (_sequenceLock) _progressSequences.Remove(sessionId);
            _ = AppNotificationManager.Default.RemoveByTagAsync(sessionId);
        }
        catch (Exception ex)
        {
            FileLog.Error($"Removing notification failed for {sessionId}", ex);
        }
    }

    public void ShowError(string message)
    {
        var notification = new AppNotificationBuilder()
            .AddText("RAIVEN")
            .AddText(message)
            .BuildNotification();

        AppNotificationManager.Default.Show(notification);
    }

    private static void AddHeadline(AppNotificationBuilder builder, string folderName, string? headline)
    {
        if (headline is not null)
        {
            builder.AddText(headline);
            builder.AddText($"Claude finished in {folderName}");
        }
        else
        {
            builder.AddText($"Claude finished in {folderName}");
        }
    }

    private static void TrySetLogo(AppNotificationBuilder builder)
    {
        try
        {
            var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "raiven-logo.png");
            if (File.Exists(logoPath))
                builder.SetAppLogoOverride(new Uri(logoPath));
        }
        catch (Exception ex)
        {
            FileLog.Error("Setting toast logo failed", ex);
        }
    }
}
```

- [ ] **Step 5: Verify Core tests pass and note the App build state**

Run: `dotnet test tests/Raiven.Core.Tests`
Expected: PASS (FakeNotifier now satisfies the extended interface).

Run: `dotnet build src/Raiven.App`
Expected: FAILURE in `Program.cs` — `ShowFinished` is now called with 2 args at lines ~92/119/134, and `SummaryPipeline` needs 6 args. **This is expected**; Task 7 fixes the wiring. Do not "fix" it here — keeping the notifier and wiring commits separate makes review cleaner. Commit only if `dotnet test tests/Raiven.Core.Tests` passes; the solution-wide build gate is Task 7 Step 6.

- [ ] **Step 6: Commit**

```bash
git add src/Raiven.Core/Notifications/INotifier.cs src/Raiven.App/AppSdkNotifier.cs src/Raiven.App/Raiven.App.csproj src/Raiven.App/Assets/raiven-logo.png tests/Raiven.Core.Tests/SummaryPipelineTests.cs
git commit -m "feat: countdown toast with progress bar, logo, headline, and abort action"
```

---

### Task 7: Wire it all up — Program, TrayContext, AppPaths

**Files:**
- Modify: `src/Raiven.App/AppPaths.cs`
- Modify: `src/Raiven.App/Program.cs`
- Modify: `src/Raiven.App/TrayContext.cs`

**Interfaces:**
- Consumes: `AutoPlayCountdown` (Task 4), `SummaryHistory` / `SummaryHistoryEntry` (Task 3), `SummaryPipeline(…, SummaryHistory)` (Task 5), `INotifier.ShowFinishedCountdown/UpdateCountdownProgress/RemoveNotification/AbortRequested` (Task 6), `TranscriptReader.ReadFirstPrompt` (Task 2), `RaivenConfig.AutoPlaySummary/AutoPlayDelaySeconds` (Task 1).
- Produces: `AppPaths.HistoryFile`; `TrayContext` constructor becomes `TrayContext(AppState state, SummaryHistory history, Action<SummaryHistoryEntry> replaySummary, Action testToast, Action testVoice)`.

- [ ] **Step 1: Add the history path**

In `src/Raiven.App/AppPaths.cs`, after `ConfigFile`, add:

```csharp
    public static string HistoryFile => Path.Combine(DataDir, "history.json");
```

- [ ] **Step 2: Add the "Recent summaries" submenu to TrayContext**

In `src/Raiven.App/TrayContext.cs`:

1. Add `using Raiven.Core.Summaries;` to the top of the file.

2. Change the constructor signature to:

```csharp
    public TrayContext(
        AppState state,
        SummaryHistory history,
        Action<SummaryHistoryEntry> replaySummary,
        Action testToast,
        Action testVoice)
```

3. After the `pauseItem` block (before `menu.Items.Add(pauseItem);`), add:

```csharp
        var recentItem = new ToolStripMenuItem("Recent summaries");
        menu.Opening += (_, _) => RebuildRecentSummaries(recentItem, history, replaySummary);
        RebuildRecentSummaries(recentItem, history, replaySummary);
```

4. Change the menu assembly so the submenu sits between Pause and the separator:

```csharp
        menu.Items.Add(pauseItem);
        menu.Items.Add(recentItem);
        menu.Items.Add(new ToolStripSeparator());
```

5. Add the private helper method to the class:

```csharp
    private static void RebuildRecentSummaries(
        ToolStripMenuItem parent, SummaryHistory history, Action<SummaryHistoryEntry> replaySummary)
    {
        parent.DropDownItems.Clear();
        var entries = history.Entries;
        if (entries.Count == 0)
        {
            parent.DropDownItems.Add(new ToolStripMenuItem("(none yet)") { Enabled = false });
            return;
        }

        foreach (var entry in entries)
        {
            var captured = entry;
            var label = $"{captured.Headline} ({captured.Folder}, {captured.GeneratedAt.LocalDateTime:HH:mm})";
            parent.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => replaySummary(captured)));
        }
    }
```

- [ ] **Step 3: Rewire Program.cs**

In `src/Raiven.App/Program.cs`:

1. Add `using Raiven.Core.Notifications;` and `using Raiven.Core.Transcripts;` to the usings.

2. In `Run(string[] args, RaivenConfig config)`, extend the cold-activation branch so an Abort click on a stale toast exits silently instead of showing an error. Replace the existing `if (activatedArgs.Kind == ...)` block with:

```csharp
        var activatedArgs = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activatedArgs.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.AppNotification)
        {
            // Toast clicked from a previous run; the in-memory session registry is gone.
            if (activatedArgs.Data is Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs toast &&
                toast.Arguments.TryGetValue("action", out var act) && act == "abort")
            {
                FileLog.Info("Stale toast aborted; nothing to do.");
                return;
            }
            new AppSdkNotifier().ShowError("RAIVEN wasn't running - that session's summary is no longer available.");
            Thread.Sleep(TimeSpan.FromSeconds(3));
            return;
        }
```

3. In `RunTray`, replace everything from `var pipeline = new SummaryPipeline(...)` through `notifier.PlaySummaryRequested += ...` with:

```csharp
        var history = Raiven.Core.Summaries.SummaryHistory.Load(AppPaths.HistoryFile);
        var pipeline = new SummaryPipeline(registry, config, claude, notifier, voice, history);

        using var countdown = new AutoPlayCountdown(
            TimeSpan.FromSeconds(Math.Max(1, config.AutoPlayDelaySeconds)),
            TimeSpan.FromMilliseconds(500));
        countdown.Progress += (id, fraction) => notifier.UpdateCountdownProgress(id, fraction);
        countdown.Expired += id =>
        {
            notifier.RemoveNotification(id);
            _ = Task.Run(() => pipeline.PlaySummaryAsync(id));
        };
        notifier.PlaySummaryRequested += id =>
        {
            countdown.Cancel(id);
            _ = Task.Run(() => pipeline.PlaySummaryAsync(id));
        };
        notifier.AbortRequested += id =>
        {
            countdown.Cancel(id);
            FileLog.Info($"Auto-play aborted for {id}");
        };
```

4. In `HandleEvent`, replace the `if (!state.Paused)` block with:

```csharp
                if (!state.Paused)
                {
                    ChimePlayer.Play(config);
                    var folder = Path.GetFileName(stop.Cwd.TrimEnd('\\', '/'));
                    var folderName = folder.Length > 0 ? folder : stop.Cwd;
                    string? headline = null;
                    try { headline = TranscriptReader.ReadFirstPrompt(stop.TranscriptPath); }
                    catch (Exception ex) { FileLog.Error("Could not read chat headline", ex); }

                    if (config.AutoPlaySummary)
                    {
                        notifier.ShowFinishedCountdown(stop.SessionId, folderName, headline, config.AutoPlayDelaySeconds);
                        countdown.Start(stop.SessionId);
                    }
                    else
                    {
                        notifier.ShowFinished(stop.SessionId, folderName, headline);
                    }
                }
```

5. Update the `Application.Run(new TrayContext(...))` call:

```csharp
            Application.Run(new TrayContext(
                state,
                history,
                replaySummary: entry => Task.Run(() => voice.Speak(entry.SummaryText)),
                testToast: () => { ChimePlayer.Play(config); notifier.ShowFinished("test-session-001", "RAIVEN", "This is a test notification"); },
                testVoice: () => Task.Run(() => voice.Speak("RAIVEN online. All systems operational."))));
```

6. Replace `RunToastTest` so it exercises the countdown variant:

```csharp
    private static void RunToastTest(RaivenConfig config)
    {
        var notifier = new AppSdkNotifier();
        notifier.PlaySummaryRequested += id => FileLog.Info($"TEST: play now requested for {id}");
        notifier.AbortRequested += id => FileLog.Info($"TEST: abort requested for {id}");
        ChimePlayer.Play(config);
        notifier.ShowFinishedCountdown("test-session-001", "RAIVEN", "Testing the countdown toast", 5);
        for (var step = 1; step <= 10; step++)
        {
            Thread.Sleep(500);
            notifier.UpdateCountdownProgress("test-session-001", step / 10.0);
        }
        FileLog.Info("Countdown complete; waiting 10s for clicks...");
        Thread.Sleep(TimeSpan.FromSeconds(10));
    }
```

- [ ] **Step 4: Build the solution**

Run: `dotnet build Raiven.sln`
Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test tests/Raiven.Core.Tests`
Expected: PASS — all tests.

- [ ] **Step 6: Commit**

```bash
git add src/Raiven.App/AppPaths.cs src/Raiven.App/Program.cs src/Raiven.App/TrayContext.cs
git commit -m "feat: wire auto-play countdown, headline toasts, and recent-summaries menu"
```

---

### Task 8: Manual verification

**Files:** none (verification only; fix-up commits if issues surface).

- [ ] **Step 1: Countdown toast visuals**

Run: `dotnet run --project src/Raiven.App -- --test-toast`

Verify in the toast that appears:
- Raven logo visible in the popup body (left side).
- Title line "Testing the countdown toast", second line "Claude finished in RAIVEN".
- Progress bar fills from empty to full over ~5 seconds, status text "Auto-playing summary…".
- Two buttons: "Play now" and "Abort".
- Clicking "Play now" logs `TEST: play now requested` in `%APPDATA%\Raiven\logs\raiven.log`; "Abort" logs `TEST: abort requested`.

- [ ] **Step 2: End-to-end auto-play and history**

Run the tray app (`dotnet run --project src/Raiven.App`), then finish a Claude Code turn (or POST a fake Stop event to `http://127.0.0.1:9876/` using a real transcript path). Verify:
- Toast shows the chat's first prompt as headline; after 5 untouched seconds the toast disappears and the summary is spoken.
- Pressing "Abort" within 5 seconds → nothing is spoken.
- Right-click tray icon → "Recent summaries" lists the entry; clicking it re-speaks instantly (no Haiku delay; log shows `Replaying cached summary`).
- `%APPDATA%\Raiven\history.json` exists and holds the entry; restart RAIVEN and confirm the submenu still lists it.

- [ ] **Step 3: Fix anything found, commit fixes**

Any defects found go through the usual loop (failing test where feasible → fix → green → commit with `fix:` prefix).
