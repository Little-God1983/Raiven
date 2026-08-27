using Raiven.Core.Logging;

namespace Raiven.Core.Tests;

public class FileLogTests
{
    private static string TempLogDir() =>
        Path.Combine(Path.GetTempPath(), $"raiven-log-{Guid.NewGuid():N}");

    // The prune tests deliberately do NOT call Configure: FileLog's target directory is one static
    // shared by the whole process, so pointing it at a temp dir invites every other test class
    // running in parallel to write today's log file into it - which is exactly what used to make
    // these assertions flaky. They prune an explicitly named directory instead.

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

        FileLog.PruneOldLogs(dir, keep: 3);

        var remaining = Directory.GetFiles(dir, "raiven-log-*.log").Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(["raiven-log-20260108.log", "raiven-log-20260109.log", "raiven-log-20260110.log"], remaining);
        Assert.True(File.Exists(Path.Combine(dir, "raiven.log"))); // non-dated file preserved
    }

    [Fact]
    public void PruneOldLogs_WithExplicitDirectory_IgnoresTheConfiguredOne()
    {
        // Guards the property the other prune tests rely on: the explicit overload reads no
        // shared state, so a concurrently configured directory cannot leak into its result.
        var configured = TempLogDir();
        var target = TempLogDir();
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "raiven-log-20260101.log"), "x");
        File.WriteAllText(Path.Combine(target, "raiven-log-20260102.log"), "x");
        FileLog.Configure(configured);
        File.WriteAllText(Path.Combine(configured, "raiven-log-20260101.log"), "x");

        FileLog.PruneOldLogs(target, keep: 1);

        Assert.Equal(["raiven-log-20260102.log"],
            Directory.GetFiles(target, "raiven-log-*.log").Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(configured, "raiven-log-20260101.log")));
    }

    [Fact]
    public void PruneOldLogs_FewerThanKeep_DeletesNothing()
    {
        var dir = TempLogDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "raiven-log-20260101.log"), "x");
        File.WriteAllText(Path.Combine(dir, "raiven-log-20260102.log"), "x");

        FileLog.PruneOldLogs(dir, keep: 30);

        Assert.Equal(2, Directory.GetFiles(dir, "raiven-log-*.log").Length);
    }
}
