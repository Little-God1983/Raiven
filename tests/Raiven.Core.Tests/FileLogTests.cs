using Raiven.Core.Logging;

namespace Raiven.Core.Tests;

public class FileLogTests
{
    private static string TempLogDir() =>
        Path.Combine(Path.GetTempPath(), $"raiven-log-{Guid.NewGuid():N}");

    [Fact]
    public void InfoAndError_AppendLinesToTodaysDatedFile()
    {
        var dir = TempLogDir();
        FileLog.Configure(dir);

        FileLog.Info("hello");
        FileLog.Error("boom", new InvalidOperationException("bad"));

        var expected = Path.Combine(dir, $"raiven-log-{DateTimeOffset.Now:yyyyMMdd}.log");
        var text = File.ReadAllText(expected);
        Assert.Contains("INFO hello", text);
        Assert.Contains("ERROR boom", text);
        Assert.Contains("InvalidOperationException", text);
    }

    [Fact]
    public void PruneOldLogs_KeepsNewestAndDeletesOlder()
    {
        var dir = TempLogDir();
        Directory.CreateDirectory(dir);
        // Ten dated logs plus an unrelated file that must never be pruned.
        for (var day = 1; day <= 10; day++)
            File.WriteAllText(Path.Combine(dir, $"raiven-log-202601{day:D2}.log"), "x");
        File.WriteAllText(Path.Combine(dir, "raiven.log"), "legacy");
        FileLog.Configure(dir);

        FileLog.PruneOldLogs(keep: 3);

        var remaining = Directory.GetFiles(dir, "raiven-log-*.log").Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(["raiven-log-20260108.log", "raiven-log-20260109.log", "raiven-log-20260110.log"], remaining);
        Assert.True(File.Exists(Path.Combine(dir, "raiven.log"))); // non-dated file preserved
    }

    [Fact]
    public void PruneOldLogs_FewerThanKeep_DeletesNothing()
    {
        var dir = TempLogDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "raiven-log-20260101.log"), "x");
        File.WriteAllText(Path.Combine(dir, "raiven-log-20260102.log"), "x");
        FileLog.Configure(dir);

        FileLog.PruneOldLogs(keep: 30);

        Assert.Equal(2, Directory.GetFiles(dir, "raiven-log-*.log").Length);
    }
}
