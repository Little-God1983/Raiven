using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Raiven.Core.Export;

/// <summary>One parsed daily-log record; <see cref="Message"/> includes any continuation lines
/// (e.g. an exception stack trace) that followed the timestamped line.</summary>
public sealed record LogEntry(DateTime Time, string Level, string Message);

/// <summary>Parses RAIVEN's daily log files (<c>[yyyy-MM-dd HH:mm:ss] LEVEL message</c>) back into
/// structured entries, re-attaching multi-line exception text to the entry it belongs to.</summary>
public static class LogParser
{
    private static readonly Regex LineRegex =
        new(@"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] (\w+) (.*)$", RegexOptions.Compiled);

    public static List<LogEntry> Parse(IEnumerable<string> lines)
    {
        var entries = new List<LogEntry>();
        DateTime? time = null;
        string? level = null;
        var message = new StringBuilder();

        void Flush()
        {
            if (time is not null && level is not null)
                entries.Add(new LogEntry(time.Value, level, message.ToString().TrimEnd()));
        }

        foreach (var line in lines)
        {
            var match = LineRegex.Match(line);
            if (match.Success)
            {
                Flush();
                time = DateTime.ParseExact(match.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                level = match.Groups[2].Value;
                message.Clear();
                message.Append(match.Groups[3].Value);
            }
            else if (time is not null && !string.IsNullOrWhiteSpace(line))
            {
                message.Append('\n').Append(line); // continuation (e.g. a stack trace) of the current entry
            }
            // else: blank or leading junk before any timestamped line - ignore.
        }

        Flush();
        return entries;
    }
}
