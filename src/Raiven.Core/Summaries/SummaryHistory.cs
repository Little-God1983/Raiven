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
    string SummaryText)
{
    /// <summary>Tray menu label, e.g. "04.07.2026 14:32 — RAIVEN — Fix login bug".</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string MenuLabel =>
        $"{GeneratedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture)} — {Folder} — {Headline}";
}

/// <summary>
/// Last-N generated summaries, newest first, persisted to a JSON file so replays
/// never need another Claude call. All persistence failures are best-effort.
/// </summary>
public sealed class SummaryHistory
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _filePath;
    private int _capacity;
    private readonly List<SummaryHistoryEntry> _entries;
    private readonly Lock _lock = new();

    private SummaryHistory(string filePath, int capacity, List<SummaryHistoryEntry> entries)
    {
        _filePath = filePath;
        _capacity = capacity;
        _entries = entries;
    }

    public static SummaryHistory Load(string filePath, int capacity = 10)
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

    /// <summary>Change how many entries are kept; trims and persists immediately if lowered.</summary>
    public void SetCapacity(int capacity)
    {
        lock (_lock)
        {
            _capacity = Math.Max(1, capacity);
            if (_entries.Count > _capacity)
            {
                _entries.RemoveRange(_capacity, _entries.Count - _capacity);
                Save();
            }
        }
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
