using System.Collections.Concurrent;

namespace Raiven.Core.Notifications;

/// <summary>
/// Per-session countdown timers for auto-playing summaries. Progress ticks report a
/// 0..1 fraction; Expired fires at most once per Start and never after a successful
/// Cancel. Events run on thread-pool threads.
/// </summary>
public sealed class AutoPlayCountdown(TimeSpan defaultTotal, TimeSpan tick) : IDisposable
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    public event Action<string, double>? Progress;
    public event Action<string>? Expired;

    /// <summary>Start a countdown of the constructor's default duration.</summary>
    public void Start(string sessionId) => Start(sessionId, defaultTotal);

    /// <summary>Start a countdown of an explicit duration (e.g. the configured auto-play delay).</summary>
    public void Start(string sessionId, TimeSpan total)
    {
        var cts = new CancellationTokenSource();
        _running.AddOrUpdate(sessionId, cts, (_, old) =>
        {
            old.Cancel();
            old.Dispose();
            return cts;
        });
        _ = RunAsync(sessionId, cts, total);
    }

    public bool Cancel(string sessionId)
    {
        if (_running.TryRemove(sessionId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            return true;
        }
        return false;
    }

    /// <summary>Cancel every running countdown; returns the session ids that were actually canceled.</summary>
    public IReadOnlyList<string> CancelAll()
    {
        List<string> canceled = [];
        foreach (var sessionId in _running.Keys)
        {
            if (Cancel(sessionId))
                canceled.Add(sessionId);
        }
        return canceled;
    }

    public void Dispose() => CancelAll();

    private async Task RunAsync(string sessionId, CancellationTokenSource cts, TimeSpan total)
    {
        var steps = Math.Max(1, (int)Math.Round(total.TotalMilliseconds / tick.TotalMilliseconds));
        CancellationToken token;
        try { token = cts.Token; }
        catch (ObjectDisposedException) { return; }
        try
        {
            for (var i = 1; i <= steps; i++)
            {
                await Task.Delay(tick, token).ConfigureAwait(false);
                Progress?.Invoke(sessionId, (double)i / steps);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Only the task that removes its own CTS may fire Expired; a concurrent
        // Cancel or restart wins this race and suppresses it.
        if (_running.TryRemove(new KeyValuePair<string, CancellationTokenSource>(sessionId, cts)))
        {
            cts.Dispose();
            Expired?.Invoke(sessionId);
        }
    }
}
