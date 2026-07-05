using Raiven.Core.Notifications;

namespace Raiven.Core.Tests;

public class AutoPlayCountdownTests
{
    private static readonly TimeSpan Total = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Expiry_FiresOnce_AndProgressReachesFull()
    {
        using var countdown = new AutoPlayCountdown(Total, Tick);
        var expired = new List<string>();
        var lastFraction = 0.0;
        var done = new TaskCompletionSource();
        countdown.Progress += (_, f) => lastFraction = f;
        countdown.Expired += id => { lock (expired) expired.Add(id); done.TrySetResult(); };

        countdown.Start("s1");
        await done.Task.WaitAsync(WaitLimit);
        await Task.Delay(Total); // room for any (buggy) second expiry

        Assert.Equal(["s1"], expired);
        Assert.Equal(1.0, lastFraction, precision: 5);
    }

    [Fact]
    public async Task Start_WithExplicitTotal_UsesItInsteadOfConstructorDefault()
    {
        // A long constructor default would blow past WaitLimit; the short explicit
        // total is what must drive this run.
        using var countdown = new AutoPlayCountdown(TimeSpan.FromSeconds(30), Tick);
        var done = new TaskCompletionSource();
        countdown.Expired += _ => done.TrySetResult();

        countdown.Start("s1", Total);

        await done.Task.WaitAsync(WaitLimit);
    }

    [Fact]
    public async Task Cancel_PreventsExpiry()
    {
        using var countdown = new AutoPlayCountdown(Total, Tick);
        var expiredCount = 0;
        countdown.Expired += _ => Interlocked.Increment(ref expiredCount);

        countdown.Start("s1");
        Assert.True(countdown.Cancel("s1"));
        await Task.Delay(Total + Total);

        Assert.Equal(0, expiredCount);
        Assert.False(countdown.Cancel("s1")); // already gone
    }

    [Fact]
    public async Task Start_SameSession_RestartsWithSingleExpiry()
    {
        using var countdown = new AutoPlayCountdown(Total, Tick);
        var expiredCount = 0;
        var done = new TaskCompletionSource();
        countdown.Expired += _ => { Interlocked.Increment(ref expiredCount); done.TrySetResult(); };

        countdown.Start("s1");
        await Task.Delay(Tick * 2);
        countdown.Start("s1"); // restart mid-flight

        await done.Task.WaitAsync(WaitLimit);
        await Task.Delay(Total + Total);

        Assert.Equal(1, expiredCount);
    }

    [Fact]
    public async Task Sessions_AreIndependent()
    {
        using var countdown = new AutoPlayCountdown(Total, Tick);
        var expired = new List<string>();
        var both = new TaskCompletionSource();
        countdown.Expired += id =>
        {
            lock (expired) { expired.Add(id); if (expired.Count == 2) both.TrySetResult(); }
        };

        countdown.Start("s1");
        countdown.Start("s2");
        countdown.Cancel("s1");
        countdown.Start("s1");

        await both.Task.WaitAsync(WaitLimit);

        lock (expired)
        {
            Assert.Equal(2, expired.Count);
            Assert.Contains("s1", expired);
            Assert.Contains("s2", expired);
        }
    }

    [Fact]
    public void CancelAll_ReturnsTheCanceledSessionIds()
    {
        using var countdown = new AutoPlayCountdown(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
        countdown.Start("s1");
        countdown.Start("s2");

        var canceled = countdown.CancelAll();

        Assert.Equal(["s1", "s2"], canceled.OrderBy(x => x));
        Assert.Empty(countdown.CancelAll());
    }
}
