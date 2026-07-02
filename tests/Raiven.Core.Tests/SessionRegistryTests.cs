using Raiven.Core.Sessions;

namespace Raiven.Core.Tests;

public class SessionRegistryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryGet_AfterUpsert_ReturnsInfo()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", @"C:\t.jsonl", @"E:\Repos\RAIVEN", T0);

        Assert.True(registry.TryGet("s1", T0.AddMinutes(5), out var info));
        Assert.Equal(@"C:\t.jsonl", info.TranscriptPath);
        Assert.Equal(@"E:\Repos\RAIVEN", info.Cwd);
    }

    [Fact]
    public void TryGet_UnknownSession_ReturnsFalse()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));

        Assert.False(registry.TryGet("nope", T0, out _));
    }

    [Fact]
    public void TryGet_ExpiredEntry_ReturnsFalse()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", "t", "c", T0);

        Assert.False(registry.TryGet("s1", T0.AddHours(5), out _));
    }

    [Fact]
    public void Upsert_SameSession_RefreshesLastSeenAndPath()
    {
        var registry = new SessionRegistry(TimeSpan.FromHours(4));
        registry.Upsert("s1", "old", "c", T0);
        registry.Upsert("s1", "new", "c", T0.AddHours(3));

        Assert.True(registry.TryGet("s1", T0.AddHours(6), out var info));
        Assert.Equal("new", info.TranscriptPath);
    }
}
