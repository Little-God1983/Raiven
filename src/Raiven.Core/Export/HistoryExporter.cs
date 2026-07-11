using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Raiven.Core.Export;

public sealed record ExportSummary(string Time, string? Project, string SessionId, string Text);
public sealed record ExportError(string Time, string? Project, string? SessionId, string Message);
public sealed record ExportQuestion(string Time, string? Project, string SessionId, string Text);
public sealed record ExportLogEntry(string Time, string Level, string Message);

/// <summary>
/// The analyzable history bundle. In "signal" scope it carries the summaries, errors and question
/// prompts reconstructed from the daily logs (routine noise dropped); "full" scope also attaches
/// every raw log entry in <see cref="LogEntries"/>.
/// </summary>
public sealed record HistoryExport(
    string ExportedAt,
    string AppVersion,
    string Scope,
    IReadOnlyList<ExportSummary> Summaries,
    IReadOnlyList<ExportError> Errors,
    IReadOnlyList<ExportQuestion> Questions,
    IReadOnlyList<ExportLogEntry>? LogEntries);

/// <summary>
/// Reconstructs the complete history from RAIVEN's daily logs (history.json only keeps the last
/// few) into a single analyzable JSON bundle for feeding to an LLM or a script (#10).
/// </summary>
public static class HistoryExporter
{
    public enum Scope { Signal, Full }

    private static readonly Regex StopEvent = new(@"^Stop event for session (\S+) in (.+)$", RegexOptions.Compiled);
    private static readonly Regex Summary = new(@"^Summary for (\S+): (.*)$", RegexOptions.Compiled);
    private static readonly Regex Question = new(@"^(?:AskUserQuestion|Notification) event for session (\S+): (.*)$", RegexOptions.Compiled);
    private static readonly Regex ErrorSession = new(@"for session (\S+)", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static HistoryExport Build(IReadOnlyList<LogEntry> entries, string appVersion, DateTimeOffset now, Scope scope)
    {
        var summaries = new List<ExportSummary>();
        var errors = new List<ExportError>();
        var questions = new List<ExportQuestion>();
        var projectBySession = new Dictionary<string, string>();

        // Chronological order so a session's project (from its Stop event) is known before its summary.
        var ordered = entries.OrderBy(e => e.Time).ToList();

        foreach (var entry in ordered)
        {
            var firstLine = FirstLine(entry.Message);

            var stop = StopEvent.Match(firstLine);
            if (stop.Success)
            {
                var project = ProjectFromPath(stop.Groups[2].Value);
                if (project.Length > 0) projectBySession[stop.Groups[1].Value] = project;
                continue;
            }

            if (entry.Level == "ERROR")
            {
                var sid = ErrorSession.Match(firstLine) is { Success: true } m ? m.Groups[1].Value : null;
                errors.Add(new ExportError(Iso(entry.Time), Project(projectBySession, sid), sid, TrimStack(entry.Message)));
                continue;
            }

            var summary = Summary.Match(firstLine);
            if (summary.Success)
            {
                var sid = summary.Groups[1].Value;
                summaries.Add(new ExportSummary(Iso(entry.Time), Project(projectBySession, sid), sid, summary.Groups[2].Value));
                continue;
            }

            var question = Question.Match(firstLine);
            if (question.Success)
            {
                var sid = question.Groups[1].Value;
                questions.Add(new ExportQuestion(Iso(entry.Time), Project(projectBySession, sid), sid, question.Groups[2].Value));
            }
            // else: routine noise - dropped in signal scope (still present in full scope's raw log below).
        }

        var raw = scope == Scope.Full
            ? ordered.Select(e => new ExportLogEntry(Iso(e.Time), e.Level, e.Message)).ToList()
            : null;

        return new HistoryExport(
            now.ToString("yyyy-MM-ddTHH:mm:ssK"),
            appVersion,
            scope == Scope.Full ? "full" : "signal",
            summaries, errors, questions, raw);
    }

    /// <summary>Reads every daily log file in <paramref name="logDir"/> (tolerating RAIVEN writing
    /// the current day's file concurrently) and builds the export.</summary>
    public static HistoryExport ExportFromLogs(string logDir, string appVersion, DateTimeOffset now, Scope scope)
    {
        var lines = new List<string>();
        if (Directory.Exists(logDir))
            foreach (var file in Directory.GetFiles(logDir, "raiven-log-*.log")
                         .OrderBy(Path.GetFileName, StringComparer.Ordinal))
                lines.AddRange(ReadLinesShared(file));

        return Build(LogParser.Parse(lines), appVersion, now, scope);
    }

    public static string ToJson(HistoryExport export) => JsonSerializer.Serialize(export, JsonOptions);

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
            yield return line;
    }

    private static string Iso(DateTime time) => time.ToString("yyyy-MM-ddTHH:mm:ss");

    private static string FirstLine(string message)
    {
        var nl = message.IndexOf('\n');
        return nl < 0 ? message : message[..nl];
    }

    private static string ProjectFromPath(string path) => Path.GetFileName(path.TrimEnd('\\', '/'));

    private static string? Project(Dictionary<string, string> map, string? sessionId) =>
        sessionId is not null && map.TryGetValue(sessionId, out var project) ? project : null;

    // Keep the message and the exception type/message lines, drop the "   at ..." stack frames
    // so errors stay compact for an LLM.
    private static string TrimStack(string message)
    {
        var kept = message.Split('\n')
            .Where((line, i) => i == 0 || !line.TrimStart().StartsWith("at ", StringComparison.Ordinal))
            .Select(line => line.Trim());
        return string.Join(' ', kept).Trim();
    }
}
