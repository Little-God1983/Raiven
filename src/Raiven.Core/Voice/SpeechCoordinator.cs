using Raiven.Core.Logging;

namespace Raiven.Core.Voice;

/// <summary>
/// Serializes all speech through a single worker so overlapping utterances never cut each
/// other off (#13). <see cref="SpeechPriority.Normal"/> lines (finished-turn summaries) queue
/// and play in order. A <see cref="SpeechPriority.Urgent"/> line (a question - Claude Code is
/// waiting on the user) interrupts a summary that is playing: RAIVEN speaks a short transition,
/// plays the question, then speaks a second transition and replays the interrupted summary from
/// the top (Kokoro can't resume mid-utterance). Urgent lines never interrupt each other, and
/// Normal lines never interrupt anything. Wraps a raw <see cref="IVoice"/> backend
/// (VoiceService), which it only ever drives one utterance at a time.
/// </summary>
public sealed class SpeechCoordinator : IVoice, IDisposable
{
    private sealed class Job(string text, Action<VoicePhase>? onPhase, SpeechPriority priority)
    {
        public string Text { get; } = text;
        public Action<VoicePhase>? OnPhase { get; } = onPhase;
        public SpeechPriority Priority { get; } = priority;
        public bool Resuming { get; set; } // being replayed after an interruption (speak a transition-out first)
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly IVoice _inner;
    private readonly ITransitionNarrator _narrator;
    private readonly object _gate = new();
    private readonly Queue<Job> _urgent = new();
    private readonly Queue<Job> _normal = new();
    private Job? _current;      // job the worker is playing right now
    private Job? _resume;       // a summary that was interrupted mid-play, waiting to resume
    private bool _interrupted;  // set when the current (Normal) job was cut by an urgent arrival
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    public SpeechCoordinator(IVoice inner, ITransitionNarrator narrator)
    {
        _inner = inner;
        _narrator = narrator;
        _worker = Task.Run(WorkerLoopAsync);
    }

    public Task SpeakAsync(string text, Action<VoicePhase>? onPhase = null, SpeechPriority priority = SpeechPriority.Normal)
    {
        var job = new Job(text, onPhase, priority);
        var interruptNow = false;
        lock (_gate)
        {
            if (priority == SpeechPriority.Urgent)
            {
                _urgent.Enqueue(job);
                // Cut a summary that's on air so the question can jump in front of it.
                if (_current is { Priority: SpeechPriority.Normal })
                {
                    _interrupted = true;
                    interruptNow = true;
                }
            }
            else
            {
                _normal.Enqueue(job);
            }
        }
        if (interruptNow) _inner.Stop(); // ends the worker's current playback so it can react
        _signal.Release();
        return job.Done.Task;
    }

    /// <summary>User-initiated stop of the current utterance: ends it with no narration or
    /// resume (distinct from an urgent interrupt, which sets <c>_interrupted</c>).</summary>
    public void Stop() => _inner.Stop();

    private Job? DequeueLocked()
    {
        if (_urgent.Count > 0) return _urgent.Dequeue();
        if (_resume is not null) { var r = _resume; _resume = null; return r; }
        return _normal.Count > 0 ? _normal.Dequeue() : null;
    }

    private async Task WorkerLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Job? job;
            lock (_gate)
            {
                job = DequeueLocked();
                _current = job;
                _interrupted = false;
            }

            if (job is null)
            {
                try { await _signal.WaitAsync(_cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                while (_signal.Wait(0)) { } // collapse any releases that piled up while busy
                continue;
            }

            if (job.Resuming)
            {
                job.Resuming = false;
                await SpeakRawAsync(_narrator.ResumeLine(), null).ConfigureAwait(false);
            }

            await SpeakRawAsync(job.Text, job.OnPhase).ConfigureAwait(false);

            bool wasInterrupted;
            lock (_gate)
            {
                wasInterrupted = _interrupted && ReferenceEquals(_current, job);
                _current = null;
            }

            if (wasInterrupted && job.Priority == SpeechPriority.Normal)
            {
                await SpeakRawAsync(_narrator.InterruptLine(), null).ConfigureAwait(false);
                job.Resuming = true;
                lock (_gate) _resume = job; // replayed after the urgent item(s) ahead of it
                _signal.Release();
            }
            else
            {
                job.Done.TrySetResult();
            }
        }

        // Shutdown: let any awaiters go rather than hang on a half-finished queue.
        lock (_gate)
        {
            _current?.Done.TrySetResult();
            _resume?.Done.TrySetResult();
            while (_urgent.Count > 0) _urgent.Dequeue().Done.TrySetResult();
            while (_normal.Count > 0) _normal.Dequeue().Done.TrySetResult();
        }
    }

    private async Task SpeakRawAsync(string text, Action<VoicePhase>? onPhase)
    {
        // Single choke point for all speech: strip emojis/markdown/symbols Kokoro mispronounces (#12).
        try { await _inner.SpeakAsync(SpeechText.CleanForSpeech(text), onPhase).ConfigureAwait(false); }
        catch (Exception ex) { FileLog.Error("Voice playback failed", ex); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _signal.Release();
        try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch { /* best-effort shutdown */ }
        _cts.Dispose();
        _signal.Dispose();
    }
}
