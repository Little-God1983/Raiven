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
    public void Load_DefaultCapacity_IsTen()
    {
        var history = SummaryHistory.Load(TempHistoryPath());
        for (var i = 1; i <= 12; i++)
            history.Add(Entry($"s{i}"));

        Assert.Equal(10, history.Entries.Count);
    }

    [Fact]
    public void SetCapacity_Lower_TrimsAndPersists()
    {
        var path = TempHistoryPath();
        var history = SummaryHistory.Load(path, capacity: 10);
        for (var i = 1; i <= 10; i++)
            history.Add(Entry($"s{i}"));

        history.SetCapacity(3);

        Assert.Equal(3, history.Entries.Count);
        Assert.Equal("s10", history.Entries[0].SessionId); // newest kept
        var reloaded = SummaryHistory.Load(path, capacity: 10);
        Assert.Equal(3, reloaded.Entries.Count); // trim was persisted to disk
    }

    [Fact]
    public void SetCapacity_Raise_KeepsExistingAndAllowsMore()
    {
        var history = SummaryHistory.Load(TempHistoryPath(), capacity: 3);
        for (var i = 1; i <= 3; i++)
            history.Add(Entry($"s{i}"));

        history.SetCapacity(5);
        history.Add(Entry("s4"));
        history.Add(Entry("s5"));

        Assert.Equal(5, history.Entries.Count);
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

    [Fact]
    public void MenuLabel_FormatsDatetimeFolderHeadline()
    {
        // Local-kind DateTime -> DateTimeOffset assumes the local offset, so
        // LocalDateTime round-trips the same wall time on any machine/timezone.
        var generatedAt = new DateTimeOffset(new DateTime(2026, 7, 4, 14, 32, 0));
        var entry = new SummaryHistoryEntry(
            "s1", "Fix login bug", "RAIVEN", generatedAt,
            @"C:\transcripts\s1.jsonl", DateTime.UtcNow, "text");

        Assert.Equal("04.07.2026 14:32 — RAIVEN — Fix login bug", entry.MenuLabel);
    }

    [Fact]
    public void MenuLabel_IsNotPersistedToJson()
    {
        var path = TempHistoryPath();
        var history = SummaryHistory.Load(path);
        history.Add(Entry("s1"));

        Assert.DoesNotContain("MenuLabel", File.ReadAllText(path));
    }
}
