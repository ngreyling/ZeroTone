using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace ZeroTone;

/// <summary>
/// Per-session single-instance guard: mutex for exclusivity, manual-reset event
/// so secondary launches can restore the primary window.
/// </summary>
/// <remarks>
/// <para>
/// <b>Normal path:</b> named mutex <c>Local\zerotone.SingleInstance</c> + activate
/// event <c>Local\zerotone.Activate</c>. Second launch signals the primary and exits.
/// <c>Local\</c> is logon-session only (not <c>Global\</c>).
/// </para>
/// <para>
/// <b>Partial degradation:</b> mutex held but activate event missing — still exclusive;
/// secondary cannot restore the window (may show “already running”).
/// </para>
/// <para>
/// <b>Fail-open:</b> if the mutex cannot be created or opened, this process still
/// runs with no exclusivity. Dual instances are then possible — preferred over
/// refusing to start when the session object namespace is unavailable.
/// </para>
/// </remarks>
internal sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\zerotone.SingleInstance";
    private const string EventName = @"Local\zerotone.Activate";

    private static readonly TimeSpan ActivateOpenTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ActivateRetryDelay = TimeSpan.FromMilliseconds(50);

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _activateEvent;
    private readonly bool _ownsMutex;

    private Thread? _listenerThread;
    private volatile bool _disposed;
    private volatile bool _listenerStop;

    private SingleInstanceGuard(Mutex? mutex, EventWaitHandle? activateEvent, bool ownsMutex)
    {
        _mutex = mutex;
        _activateEvent = activateEvent;
        _ownsMutex = ownsMutex;
    }

    /// <summary>
    /// Tries to become the primary instance.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if this process should run the app: normal primary,
    /// primary without activate IPC, or unrestricted when mutex creation failed
    /// (see type remarks). <see langword="false"/> if another primary holds the
    /// mutex (caller should <see cref="TryRequestActivate"/>).
    /// </returns>
    public static bool TryEnterPrimary([NotNullWhen(true)] out SingleInstanceGuard? guard)
    {
        guard = null;

        try
        {
            var mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out var createdNew);

            if (!createdNew)
            {
                // Another process is the primary. We do not own this mutex.
                mutex.Dispose();
                return false;
            }

            EventWaitHandle? activateEvent;
            try
            {
                // ManualReset: secondary Set() before we WaitOne is not lost.
                activateEvent = new EventWaitHandle(
                    initialState: false,
                    mode: EventResetMode.ManualReset,
                    name: EventName);
            }
            catch
            {
                // Partial degradation: exclusive primary, no second-launch restore IPC.
                activateEvent = null;
            }

            guard = new SingleInstanceGuard(mutex, activateEvent, ownsMutex: true);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Mutex unavailable — run without exclusivity.
            guard = CreateUnrestricted();
            return true;
        }
        catch (IOException)
        {
            guard = CreateUnrestricted();
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            guard = CreateUnrestricted();
            return true;
        }
        catch (AbandonedMutexException)
        {
            // Uncommon with the createdNew constructor; recover by waiting for ownership.
            return TryEnterPrimaryAfterAbandoned(out guard);
        }
    }

    /// <summary>
    /// Secondary process: signal the primary to restore its window.
    /// Retries briefly so a double-launch during primary init still works.
    /// </summary>
    public static bool TryRequestActivate()
    {
        var deadline = Environment.TickCount64 + (long)ActivateOpenTimeout.TotalMilliseconds;

        while (Environment.TickCount64 <= deadline)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(EventName, out var existing))
                {
                    using (existing)
                    {
                        existing.Set();
                    }

                    return true;
                }
            }
            catch
            {
                // Event may not exist yet or may be mid-teardown; retry until timeout.
            }

            Thread.Sleep(ActivateRetryDelay);
        }

        return false;
    }

    /// <summary>
    /// Background waiter for secondary activation signals. Callbacks should marshal
    /// to the UI thread (e.g. BeginInvoke) and must not throw. No-op without an
    /// activate event (degraded mode).
    /// </summary>
    public void StartActivateListener(Action onActivate)
    {
        ArgumentNullException.ThrowIfNull(onActivate);

        if (_disposed || _activateEvent is null || _listenerThread is not null)
        {
            return;
        }

        _listenerStop = false;
        var thread = new Thread(() => ListenLoop(onActivate))
        {
            IsBackground = true,
            Name = "ZeroTone-SingleInstance",
        };
        _listenerThread = thread;
        thread.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listenerStop = true;

        // Unblock WaitOne so the listener can exit promptly.
        try
        {
            _activateEvent?.Set();
        }
        catch
        {
            // Ignore if already disposed.
        }

        var listener = _listenerThread;
        if (listener is not null && listener.IsAlive)
        {
            // Bounded join — do not hang process exit on a stuck callback.
            listener.Join(TimeSpan.FromSeconds(1));
        }

        _listenerThread = null;

        if (_ownsMutex && _mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // Already released or abandoned path.
            }
        }

        try
        {
            _activateEvent?.Dispose();
        }
        catch
        {
            // Best-effort.
        }

        try
        {
            _mutex?.Dispose();
        }
        catch
        {
            // Best-effort.
        }
    }

    /// <summary>
    /// Primary without kernel objects. Dual instances are possible.
    /// </summary>
    private static SingleInstanceGuard CreateUnrestricted() =>
        new(mutex: null, activateEvent: null, ownsMutex: false);

    /// <summary>
    /// Fallback if we observe abandonment during enter: open and wait for ownership.
    /// If recovery cannot establish the mutex, fail-open like <see cref="TryEnterPrimary"/>.
    /// </summary>
    private static bool TryEnterPrimaryAfterAbandoned(out SingleInstanceGuard? guard)
    {
        guard = null;

        try
        {
            var mutex = new Mutex(initiallyOwned: false, name: MutexName);
            bool owned;
            try
            {
                owned = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // Previous owner died; we now own the mutex.
                owned = true;
            }

            if (!owned)
            {
                mutex.Dispose();
                return false;
            }

            EventWaitHandle? activateEvent;
            try
            {
                activateEvent = new EventWaitHandle(
                    initialState: false,
                    mode: EventResetMode.ManualReset,
                    name: EventName);
            }
            catch
            {
                // Partial degradation: exclusive primary, no activate IPC.
                activateEvent = null;
            }

            guard = new SingleInstanceGuard(mutex, activateEvent, ownsMutex: true);
            return true;
        }
        catch
        {
            // Same fail-open as TryEnterPrimary mutex failures.
            guard = CreateUnrestricted();
            return true;
        }
    }

    private void ListenLoop(Action onActivate)
    {
        var ev = _activateEvent;
        if (ev is null)
        {
            return;
        }

        while (!_listenerStop && !_disposed)
        {
            bool signaled;
            try
            {
                // Timeout so Dispose can stop us without depending only on Set().
                signaled = ev.WaitOne(500);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                break;
            }

            if (_listenerStop || _disposed)
            {
                break;
            }

            if (!signaled)
            {
                continue;
            }

            try
            {
                onActivate();
            }
            catch
            {
                // Subscriber must not take the primary process down.
            }

            if (_listenerStop || _disposed)
            {
                break;
            }

            try
            {
                // Clear sticky signal so the next secondary launch can Set again.
                ev.Reset();
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                // Ignore reset failures; loop will re-observe if still signaled.
            }
        }
    }
}
