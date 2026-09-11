using System.Runtime.InteropServices;
using ZeroTone.Audio.Com;

namespace ZeroTone.Audio;

/// <summary>DEVICE_STATE_* values from mmdeviceapi.h.</summary>
internal static class AudioDeviceStates
{
    public const int Active = 0x00000001;
    public const int Disabled = 0x00000002;
    public const int NotPresent = 0x00000004;
    public const int Unplugged = 0x00000008;
}

/// <summary>
/// Core Audio adapter for Multimedia default playback. Relevant callbacks
/// call <see cref="PlaybackGraphInvalidator.Signal"/>; debounce lives there.
/// Multimedia role only. Power-resume is owned by MainForm so re-arm still
/// works when <see cref="Start"/> fails.
/// <see cref="Start"/> only CoCreates and registers on the caller (UI STA).
/// The initial tracked-endpoint id is filled on a thread-pool worker with a
/// separate enumerator so a wedged <c>GetDefaultAudioEndpoint</c> cannot
/// marshal back onto the UI thread.
/// </summary>
internal sealed class DefaultAudioDeviceMonitor : IMMNotificationClient, IDisposable
{
    private readonly PlaybackGraphInvalidator _invalidator;
    private readonly object _gate = new();
    private IMMDeviceEnumerator? _enumerator;
    private bool _disposed;
    private bool _listening;

    /// <summary>
    /// Last known Multimedia default endpoint id (for selective state/remove).
    /// </summary>
    private string? _lastMultimediaDefaultId;

    public DefaultAudioDeviceMonitor(PlaybackGraphInvalidator invalidator)
    {
        _invalidator = invalidator ?? throw new ArgumentNullException(nameof(invalidator));
    }

    /// <summary>
    /// Creates the COM enumerator and registers for notifications.
    /// Later calls are no-ops while listening. Does not query the default
    /// endpoint on this thread. On failure, throws — keep-alive and
    /// power-resume re-arm still work; only COM device-follow is unavailable.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var scheduleSnapshot = false;

        lock (_gate)
        {
            if (_listening)
            {
                return;
            }

            IMMDeviceEnumerator? enumerator = null;
            try
            {
                var comObject = new MMDeviceEnumeratorComObject();
                enumerator = (IMMDeviceEnumerator)comObject;

                var hr = enumerator.RegisterEndpointNotificationCallback(this);
                if (hr < 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                _enumerator = enumerator;
                enumerator = null; // transferred; catch must not ReleaseComObject
                _listening = true;
                scheduleSnapshot = true;
            }
            catch
            {
                if (enumerator is not null)
                {
                    try
                    {
                        Marshal.ReleaseComObject(enumerator);
                    }
                    catch
                    {
                        // Best-effort cleanup after failed Start.
                    }
                }

                _enumerator = null;
                _listening = false;
                _lastMultimediaDefaultId = null;
                throw;
            }
        }

        if (scheduleSnapshot)
        {
            // Own-thread enumerator: the registered RCW is UI-STA affine.
            ThreadPool.QueueUserWorkItem(
                static state => ((DefaultAudioDeviceMonitor)state!).TryFillTrackedMultimediaDefaultId(),
                this);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopCore();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            StopCore();
            _disposed = true;
        }
    }

    private void StopCore()
    {
        if (_enumerator is not null && _listening)
        {
            try
            {
                _enumerator.UnregisterEndpointNotificationCallback(this);
            }
            catch
            {
                // Best-effort unregister during teardown.
            }
        }

        if (_enumerator is not null)
        {
            try
            {
                Marshal.ReleaseComObject(_enumerator);
            }
            catch
            {
                // Ignore release failures.
            }

            _enumerator = null;
        }

        _listening = false;
        _lastMultimediaDefaultId = null;
    }

    /// <summary>
    /// Forwards to the shared invalidator when this instance is still the live COM listener.
    /// </summary>
    private void SignalGraphIfListening()
    {
        lock (_gate)
        {
            if (_disposed || !_listening)
            {
                return;
            }
        }

        _invalidator.Signal();
    }

    private bool IsTrackedMultimediaDefault(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(_lastMultimediaDefaultId))
        {
            return false;
        }

        return string.Equals(deviceId, _lastMultimediaDefaultId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Initial fill of the tracked Multimedia default id. No-op if
    /// <see cref="OnDefaultDeviceChanged"/> already set it, or if Stop/Dispose
    /// raced the worker. Until this lands, state/remove matching may miss;
    /// Multimedia default-change callbacks still Signal.
    /// </summary>
    private void TryFillTrackedMultimediaDefaultId()
    {
        lock (_gate)
        {
            if (_disposed || !_listening || !string.IsNullOrEmpty(_lastMultimediaDefaultId))
            {
                return;
            }
        }

        var id = TryReadMultimediaDefaultIdOnThisThread();
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || !_listening || !string.IsNullOrEmpty(_lastMultimediaDefaultId))
            {
                return;
            }

            _lastMultimediaDefaultId = id;
        }
    }

    /// <summary>
    /// MMDevice default-id read on the calling thread. Must not use
    /// <see cref="_enumerator"/> (STA — a pool call would marshal to the UI).
    /// </summary>
    private static string? TryReadMultimediaDefaultIdOnThisThread()
    {
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            return TryGetMultimediaDefaultId(enumerator);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (enumerator is not null)
            {
                try
                {
                    Marshal.ReleaseComObject(enumerator);
                }
                catch
                {
                    // Ignore release failures.
                }
            }
        }
    }

    private static string? TryGetMultimediaDefaultId(IMMDeviceEnumerator enumerator)
    {
        IMMDevice? device = null;
        try
        {
            var hr = enumerator.GetDefaultAudioEndpoint(
                EDataFlow.Render,
                ERole.Multimedia,
                out var devicePtr);
            if (hr < 0 || devicePtr == IntPtr.Zero)
            {
                return null;
            }

            device = (IMMDevice)Marshal.GetObjectForIUnknown(devicePtr);
            Marshal.Release(devicePtr);

            hr = device.GetId(out var id);
            if (hr < 0 || string.IsNullOrEmpty(id))
            {
                return null;
            }

            return id;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (device is not null)
            {
                try
                {
                    Marshal.ReleaseComObject(device);
                }
                catch
                {
                    // Ignore release failures.
                }
            }
        }
    }

    // IMMNotificationClient — often not on the UI thread

    void IMMNotificationClient.OnDefaultDeviceChanged(
        EDataFlow flow,
        ERole role,
        string defaultDeviceId)
    {
        // Product: Multimedia default playback only.
        if (flow != EDataFlow.Render || role != ERole.Multimedia)
        {
            return;
        }

        lock (_gate)
        {
            if (!string.IsNullOrEmpty(defaultDeviceId))
            {
                _lastMultimediaDefaultId = defaultDeviceId;
            }
        }

        SignalGraphIfListening();
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, int newState)
    {
        // Only the tracked Multimedia default — not every endpoint on the PC.
        bool tracked;
        lock (_gate)
        {
            tracked = IsTrackedMultimediaDefault(deviceId);
        }

        if (!tracked)
        {
            return;
        }

        const int knownMask =
            AudioDeviceStates.Active
            | AudioDeviceStates.Disabled
            | AudioDeviceStates.NotPresent
            | AudioDeviceStates.Unplugged;

        if ((newState & knownMask) == 0)
        {
            return;
        }

        SignalGraphIfListening();
    }

    void IMMNotificationClient.OnDeviceAdded(string deviceId)
    {
        // Wait for Multimedia OnDefaultDeviceChanged or state on tracked id.
    }

    void IMMNotificationClient.OnDeviceRemoved(string deviceId)
    {
        bool tracked;
        lock (_gate)
        {
            tracked = IsTrackedMultimediaDefault(deviceId);
        }

        if (!tracked)
        {
            return;
        }

        SignalGraphIfListening();
    }

    void IMMNotificationClient.OnPropertyValueChanged(string deviceId, PropertyKey key)
    {
        // Extremely noisy (volume, names, …). Do not restart on property spam.
    }
}
