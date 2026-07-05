namespace Raiven.Core.Logging;

public static class FileLog
{
    private static readonly Lock Sync = new();
    private static string? _dir;

    /// <summary>Configure the directory that daily log files are written into.</summary>
    public static void Configure(string directory)
    {
        Directory.CreateDirectory(directory);
        lock (Sync) { _dir = directory; }
    }

    /// <summary>Keep the newest <paramref name="keep"/> daily log files, delete older ones.
    /// Retention is by count (days RAIVEN actually ran), never by age.</summary>
    public static void PruneOldLogs(int keep)
    {
        string? dir;
        lock (Sync) { dir = _dir; }
        if (dir is null) return;
        try
        {
            // File names are raiven-log-YYYYMMDD.log, so name order is chronological.
            var stale = Directory.GetFiles(dir, "raiven-log-*.log")
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                .Skip(Math.Max(0, keep));
            foreach (var file in stale)
            {
                try { File.Delete(file); }
                catch (IOException) { /* best-effort */ }
                catch (UnauthorizedAccessException) { /* best-effort */ }
            }
        }
        catch (Exception)
        {
            // Pruning must never take the app down.
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        lock (Sync)
        {
            if (_dir is null) return;
            try
            {
                // Resolve the target file per write so a long-running instance rolls
                // to a new file automatically at midnight.
                var path = Path.Combine(_dir, $"raiven-log-{DateTimeOffset.Now:yyyyMMdd}.log");
                File.AppendAllText(path, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {level} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
        }
    }
}
