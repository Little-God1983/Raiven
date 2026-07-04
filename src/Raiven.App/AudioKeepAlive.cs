using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using Raiven.Core.Logging;

namespace Raiven.App;

/// <summary>
/// Plays endless digital silence to keep the default output device - and a
/// Bluetooth audio link - awake, so speech doesn't lose its first second to
/// device wake-up. Follows default-device changes and recovers from device
/// errors. Everything is best-effort: this must never crash the tray app.
/// </summary>
public sealed class AudioKeepAlive : IDisposable
{
    private readonly Lock _lock = new();
    private MMDeviceEnumerator? _enumerator;
    private DeviceChangeListener? _listener;
    private WaveOutEvent? _output;
    private bool _shouldRun;
    private int _restartPending;

    public void Start()
    {
        lock (_lock)
        {
            _shouldRun = true;
            if (_enumerator is null)
            {
                try
                {
                    _enumerator = new MMDeviceEnumerator();
                    _listener = new DeviceChangeListener(this);
                    _enumerator.RegisterEndpointNotificationCallback(_listener);
                }
                catch (Exception ex)
                {
                    FileLog.Error("Audio keep-alive: device-change watcher unavailable; continuing without it", ex);
                }
            }
            StartStreamLocked();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _shouldRun = false;
            StopStreamLocked();
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_lock)
        {
            if (_enumerator is not null && _listener is not null)
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(_listener); }
                catch (Exception ex) { FileLog.Error("Audio keep-alive: unregister failed", ex); }
            }
            _enumerator?.Dispose();
            _enumerator = null;
            _listener = null;
        }
    }

    private void StartStreamLocked()
    {
        if (!_shouldRun || _output is not null) return;
        WaveOutEvent? output = null;
        try
        {
            output = new WaveOutEvent();
            output.Init(new SilenceProvider(new WaveFormat(44100, 16, 2)));
            output.PlaybackStopped += OnPlaybackStopped;
            output.Play();
            _output = output;
            FileLog.Info("Audio keep-alive stream started.");
        }
        catch (Exception ex)
        {
            FileLog.Error("Audio keep-alive could not start (will retry on the next device change)", ex);
            try { output?.Dispose(); } catch { /* best-effort cleanup of a partially constructed player */ }
            _output = null;
        }
    }

    private void StopStreamLocked()
    {
        if (_output is null) return;
        try
        {
            _output.PlaybackStopped -= OnPlaybackStopped; // manual stop must not trigger the restart path
            _output.Stop();
            _output.Dispose();
        }
        catch (Exception ex)
        {
            FileLog.Error("Audio keep-alive stop failed", ex);
        }
        _output = null;
        FileLog.Info("Audio keep-alive stream stopped.");
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // The device vanished or errored; retry shortly on whatever is default by then.
        if (e.Exception is not null)
            FileLog.Error("Audio keep-alive playback stopped unexpectedly", e.Exception);
        RestartSoon();
    }

    // Debounced: OnDefaultDeviceChanged fires once per role, and errors can burst.
    private void RestartSoon()
    {
        if (Interlocked.Exchange(ref _restartPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Interlocked.Exchange(ref _restartPending, 0);
            lock (_lock)
            {
                if (!_shouldRun) return;
                StopStreamLocked();
                StartStreamLocked();
            }
        });
    }

    private sealed class DeviceChangeListener(AudioKeepAlive owner) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            // WaveOutEvent binds the device at Init and never migrates; restart on the
            // new default so the keep-alive follows e.g. freshly connected BT headphones.
            if (flow == DataFlow.Render && role == Role.Multimedia)
                owner.RestartSoon();
        }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
