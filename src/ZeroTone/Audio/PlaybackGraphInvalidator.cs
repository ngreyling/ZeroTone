namespace ZeroTone.Audio;

/// <summary>
/// Coalesces "playback graph may be stale" signals (device follow, power resume, …)
/// into a single debounced <see cref="Invalidated"/> event.
/// Always usable once constructed — does not depend on COM notification registration.
/// Owned by Program; producers call <see cref="Signal"/>; MainForm consumes the event.
/// </summary>
internal sealed class PlaybackGraphInvalidator : IDisposable
{
    /// <summary>
    /// Quiet window after the last <see cref="Signal"/> before raising.
    /// Collapses Bluetooth connect storms and resume+COM bursts into one restart.
    /// </summary>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private System.Threading.Timer? _debounceTimer;
    private bool _disposed;

    /// <summary>
    /// Fired on a thread-pool thread after the quiet window.
    /// Subscribers should marshal to the UI thread as needed.
    /// </summary>
    public event EventHandler? Invalidated;

    /// <summary>
    /// Arms or resets the debounce timer. No-op after <see cref="Dispose"/>.
    /// </summary>
    public void Signal()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Create once; Change() resets the due time on each new signal.
            _debounceTimer ??= new System.Threading.Timer(DebounceTimerCallback);
            _debounceTimer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
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
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    private void DebounceTimerCallback(object? state)
    {
        EventHandler? handlers;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            handlers = Invalidated;
        }

        try
        {
            handlers?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Subscribers must not take the process down.
        }
    }
}
