using Raiven.Core.Voice;

namespace Raiven.Core.Tests;

public class SpeechCoordinatorTests
{
    /// <summary>Fixed transition lines so playback order is deterministic in tests.</summary>
    private sealed class FixedNarrator(string interruptLine, string resumeLine) : ITransitionNarrator
    {
        public string InterruptLine() => interruptLine;
        public string ResumeLine() => resumeLine;
    }

    /// <summary>
    /// Inner voice whose playback blocks until the test releases it (or Stop cuts it), so we can
    /// hold an utterance "on air" while enqueuing another and assert exactly what plays when.
    /// </summary>
    private sealed class GatedVoice : IVoice
    {
        private readonly object _l = new();
        private readonly List<TaskCompletionSource> _gates = [];
        public List<string> Started { get; } = [];

        public Task SpeakAsync(string text, Action<VoicePhase>? onPhase = null, SpeechPriority priority = SpeechPriority.Normal)
        {
            TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_l)
            {
                Started.Add(text);
                _gates.Add(gate);
            }
            onPhase?.Invoke(VoicePhase.Speaking);
            return gate.Task;
        }

        /// <summary>Cut whatever is currently "on air" (the newest unfinished playback).</summary>
        public void Stop()
        {
            lock (_l)
                for (var i = _gates.Count - 1; i >= 0; i--)
                    if (!_gates[i].Task.IsCompleted) { _gates[i].SetResult(); return; }
        }

        /// <summary>End the index-th playback naturally, as if the audio finished.</summary>
        public void Finish(int index)
        {
            lock (_l) _gates[index].TrySetResult();
        }

        /// <summary>Await until at least (index+1) playbacks have started; throws rather than hang.</summary>
        public async Task WaitStarted(int index)
        {
            for (var i = 0; i < 600; i++) // ~3s ceiling
            {
                lock (_l) if (Started.Count > index) return;
                await Task.Delay(5);
            }
            throw new TimeoutException($"inner playback #{index} never started");
        }

        public string[] StartedSnapshot()
        {
            lock (_l) return [.. Started];
        }
    }

    [Fact]
    public async Task Summaries_QueueInsteadOfCuttingEachOtherOff()
    {
        var inner = new GatedVoice();
        using var coord = new SpeechCoordinator(inner, new FixedNarrator("IN", "OUT"));

        var a = coord.SpeakAsync("A");           // Normal summary, starts playing
        await inner.WaitStarted(0);
        var b = coord.SpeakAsync("B");           // Normal summary while A is on air

        await Task.Delay(50);
        Assert.Equal(["A"], inner.StartedSnapshot()); // B must wait, not cut A off

        inner.Finish(0);
        await a;
        await inner.WaitStarted(1);              // B only starts after A finished
        inner.Finish(1);
        await b;

        Assert.Equal(["A", "B"], inner.StartedSnapshot());
    }

    [Fact]
    public async Task Question_InterruptsSummary_ThenNarratesAndResumes()
    {
        var inner = new GatedVoice();
        using var coord = new SpeechCoordinator(inner, new FixedNarrator("INTERRUPT", "RESUME"));

        var summary = coord.SpeakAsync("SUMMARY", priority: SpeechPriority.Normal);
        await inner.WaitStarted(0);

        var question = coord.SpeakAsync("QUESTION", priority: SpeechPriority.Urgent);

        await inner.WaitStarted(1); inner.Finish(1); // transition-in
        await inner.WaitStarted(2); inner.Finish(2); // the question
        await question;
        await inner.WaitStarted(3); inner.Finish(3); // transition-out
        await inner.WaitStarted(4); inner.Finish(4); // summary replayed from the top
        await summary;

        Assert.Equal(["SUMMARY", "INTERRUPT", "QUESTION", "RESUME", "SUMMARY"], inner.StartedSnapshot());
    }

    [Fact]
    public async Task Question_WithNothingPlaying_SpeaksDirectlyWithoutTransition()
    {
        var inner = new GatedVoice();
        using var coord = new SpeechCoordinator(inner, new FixedNarrator("IN", "OUT"));

        var question = coord.SpeakAsync("QUESTION", priority: SpeechPriority.Urgent);
        await inner.WaitStarted(0);
        inner.Finish(0);
        await question;

        Assert.Equal(["QUESTION"], inner.StartedSnapshot()); // no transition-in when nothing was cut off
    }

    [Fact]
    public async Task Question_DoesNotInterruptAnotherQuestion()
    {
        var inner = new GatedVoice();
        using var coord = new SpeechCoordinator(inner, new FixedNarrator("IN", "OUT"));

        var q1 = coord.SpeakAsync("Q1", priority: SpeechPriority.Urgent);
        await inner.WaitStarted(0);
        var q2 = coord.SpeakAsync("Q2", priority: SpeechPriority.Urgent);

        await Task.Delay(50);
        Assert.Equal(["Q1"], inner.StartedSnapshot()); // Q2 waits its turn; it does not cut off Q1

        inner.Finish(0); await q1;
        await inner.WaitStarted(1); inner.Finish(1); await q2;

        Assert.Equal(["Q1", "Q2"], inner.StartedSnapshot());
    }

    [Fact]
    public async Task SpeakAsync_CleansTextBeforePlaying()
    {
        var inner = new GatedVoice();
        using var coord = new SpeechCoordinator(inner, new FixedNarrator("IN", "OUT"));

        var t = coord.SpeakAsync("Done **fixing** the bug ✅");
        await inner.WaitStarted(0);
        inner.Finish(0);
        await t;

        Assert.Equal(["Done fixing the bug"], inner.StartedSnapshot()); // emoji + markdown removed on the way to Kokoro
    }

    [Fact]
    public async Task Stop_DuringSummary_EndsItWithoutResumeOrNarration()
    {
        var inner = new GatedVoice();
        using var coord = new SpeechCoordinator(inner, new FixedNarrator("IN", "OUT"));

        var summary = coord.SpeakAsync("SUMMARY", priority: SpeechPriority.Normal);
        await inner.WaitStarted(0);

        coord.Stop();   // user Stop, not an urgent interrupt
        await summary;

        await Task.Delay(50);
        Assert.Equal(["SUMMARY"], inner.StartedSnapshot()); // no transition, no replay
    }
}
