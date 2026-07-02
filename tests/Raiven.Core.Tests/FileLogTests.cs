using Raiven.Core.Logging;

namespace Raiven.Core.Tests;

public class FileLogTests
{
    [Fact]
    public void InfoAndError_AppendLinesToConfiguredFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"raiven-log-{Guid.NewGuid():N}", "raiven.log");
        FileLog.Configure(path);

        FileLog.Info("hello");
        FileLog.Error("boom", new InvalidOperationException("bad"));

        var text = File.ReadAllText(path);
        Assert.Contains("INFO hello", text);
        Assert.Contains("ERROR boom", text);
        Assert.Contains("InvalidOperationException", text);
    }
}
