namespace Raiven.Core.Logging;

public static class FileLog
{
    private static readonly Lock Sync = new();
    private static string? _path;

    public static void Configure(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (Sync) { _path = path; }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        lock (Sync)
        {
            if (_path is null) return;
            try
            {
                File.AppendAllText(_path, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {level} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
        }
    }
}
