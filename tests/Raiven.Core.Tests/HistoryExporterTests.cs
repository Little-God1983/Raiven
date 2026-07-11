using System.Text.Json;
using Raiven.Core.Export;

namespace Raiven.Core.Tests;

public class HistoryExporterTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 12, 14, 30, 0, TimeSpan.Zero);

    // --- LogParser ---

    [Fact]
    public void Parse_SingleLine_ExtractsTimeLevelAndMessage()
    {
        var e = Assert.Single(LogParser.Parse(["[2026-07-11 07:58:41] INFO Summary for s1: Did the thing"]));
        Assert.Equal(new DateTime(2026, 7, 11, 7, 58, 41), e.Time);
        Assert.Equal("INFO", e.Level);
        Assert.Equal("Summary for s1: Did the thing", e.Message);
    }

    [Fact]
    public void Parse_ContinuationLines_AttachToPreviousEntry()
    {
        var e = Assert.Single(LogParser.Parse(
        [
            "[2026-07-11 07:56:37] ERROR Summary failed for session s1",
            "System.IO.IOException: The process cannot access the file",
            "   at Foo.Bar()",
        ]));
        Assert.Equal("ERROR", e.Level);
        Assert.Contains("Summary failed for session s1", e.Message);
        Assert.Contains("System.IO.IOException", e.Message);
        Assert.Contains("at Foo.Bar()", e.Message);
    }

    [Fact]
    public void Parse_SkipsBlankAndLeadingNonTimestampedLines()
    {
        var e = Assert.Single(LogParser.Parse(["", "junk with no timestamp", "[2026-07-11 07:58:41] INFO hello"]));
        Assert.Equal("hello", e.Message);
    }

    // --- HistoryExporter.Build ---

    [Fact]
    public void Build_ExtractsSummaries_WithProjectFromPrecedingStopEvent()
    {
        var entries = LogParser.Parse(
        [
            @"[2026-07-11 07:58:34] INFO Stop event for session s1 in E:\Repos\ProjectWeaver",
            "[2026-07-11 07:58:41] INFO Summary for s1: I fixed the login bug.",
        ]);
        var s = Assert.Single(HistoryExporter.Build(entries, "1.1.1", Now, HistoryExporter.Scope.Signal).Summaries);
        Assert.Equal("s1", s.SessionId);
        Assert.Equal("ProjectWeaver", s.Project);
        Assert.Equal("I fixed the login bug.", s.Text);
        Assert.Equal("2026-07-11T07:58:41", s.Time);
    }

    [Fact]
    public void Build_ExtractsErrors_WithSession()
    {
        var entries = LogParser.Parse(
        [
            "[2026-07-11 07:56:37] ERROR Summary failed for session s1",
            "System.IO.IOException: locked",
        ]);
        var err = Assert.Single(HistoryExporter.Build(entries, "1.1.1", Now, HistoryExporter.Scope.Signal).Errors);
        Assert.Equal("s1", err.SessionId);
        Assert.Contains("Summary failed", err.Message);
    }

    [Fact]
    public void Build_ExtractsQuestions()
    {
        var entries = LogParser.Parse(
            ["[2026-07-10 11:46:17] INFO AskUserQuestion event for session s2: What should count as a failure?"]);
        var q = Assert.Single(HistoryExporter.Build(entries, "1.1.1", Now, HistoryExporter.Scope.Signal).Questions);
        Assert.Equal("s2", q.SessionId);
        Assert.Contains("What should count", q.Text);
    }

    [Fact]
    public void Build_SignalScope_DropsNoiseAndOmitsRawLog()
    {
        var entries = LogParser.Parse(
        [
            "[2026-07-11 08:09:34] INFO Audio keep-alive stream started.",
            "[2026-07-11 08:14:17] INFO Stop requested for s1 (countdown canceled: False)",
            "[2026-07-11 07:58:41] INFO Summary for s1: kept",
        ]);
        var export = HistoryExporter.Build(entries, "1.1.1", Now, HistoryExporter.Scope.Signal);
        Assert.Single(export.Summaries);
        Assert.Empty(export.Errors);
        Assert.Empty(export.Questions);
        Assert.Null(export.LogEntries);
    }

    [Fact]
    public void Build_FullScope_IncludesEveryLogEntry()
    {
        var entries = LogParser.Parse(
        [
            "[2026-07-11 08:09:34] INFO Audio keep-alive stream started.",
            "[2026-07-11 07:58:41] INFO Summary for s1: kept",
        ]);
        var export = HistoryExporter.Build(entries, "1.1.1", Now, HistoryExporter.Scope.Full);
        Assert.NotNull(export.LogEntries);
        Assert.Equal(2, export.LogEntries!.Count);
    }

    [Fact]
    public void Build_SortsSummariesByTimeAscending()
    {
        var entries = LogParser.Parse(
        [
            "[2026-07-11 09:00:00] INFO Summary for s1: later",
            "[2026-07-11 08:00:00] INFO Summary for s2: earlier",
        ]);
        var export = HistoryExporter.Build(entries, "1.1.1", Now, HistoryExporter.Scope.Signal);
        Assert.Equal(["earlier", "later"], export.Summaries.Select(s => s.Text));
    }

    [Fact]
    public void ToJson_IsCamelCaseWithMetadataAndSummaries()
    {
        var entries = LogParser.Parse(["[2026-07-11 07:58:41] INFO Summary for s1: hi"]);
        var export = HistoryExporter.Build(entries, "1.1.1", Now, HistoryExporter.Scope.Signal);
        using var doc = JsonDocument.Parse(HistoryExporter.ToJson(export));
        Assert.Equal("1.1.1", doc.RootElement.GetProperty("appVersion").GetString());
        Assert.Equal("signal", doc.RootElement.GetProperty("scope").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("summaries").GetArrayLength());
    }
}
