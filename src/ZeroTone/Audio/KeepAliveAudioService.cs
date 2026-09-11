using System.Runtime.InteropServices;
using ZeroTone.Audio.Com;
using ZeroTone.Settings;

namespace ZeroTone.Audio;

/// <summary>Coarse keep-alive lifecycle for UI (tray / status label).</summary>
internal enum KeepAlivePhase
{
    /// <summary>
    /// Keep-alive is off (user stop, dispose, or owning worker ended and cleared desire).
    /// </summary>
    Stopped,

    /// <summary>
    /// Cold engage only (<see cref="KeepAliveAudioService.Start"/> / Start on Launch):
    /// stream not yet proven, including failed open retries. Not used for
    /// mid-session replace (<see cref="KeepAliveAudioService.RestartIfEngaged"/>).
    /// </summary>
    Starting,

    /// <summary>
    /// Current registered generation has proven <c>IAudioClient.Start</c>
    /// (includes healthy Pulsed gaps between bursts).
    /// </summary>
    Running,

    /// <summary>
    /// Engaged but stream unproven after a proven loss, or intentional session
    /// replace while desire was already on (<c>RestartIfEngaged</c> / graph pin).
    /// Not the first-open wait of a cold Start — that stays <see cref="Starting"/>.
    /// </summary>
    Reconnecting
}

/// <summary>
/// WASAPI shared-mode keep-alive on the Multimedia default playback device.
/// Start/Stop/RestartIfEngaged return immediately; a control thread joins
/// workers so the UI never waits on COM teardown.
/// </summary>
/// <remarks>
/// Open and stream failures reconnect inside the worker. If the owning worker
/// exits unexpectedly, or <c>Thread.Start</c> fails, desire is cleared (no
/// outer respawn). Worker teardown uses a tracked drain: at most one draining
/// generation; the first join timeout may dual-start; further replaces wait.
/// Dispose may drop a wedged drain after 2s so it can join the current session.
/// Worker phase and LastIssue updates apply only while that generation still
/// owns the registered session. Silence writes mix-format zeros as ordinary
/// packets (not <c>AUDCLNT_BUFFERFLAGS_SILENT</c>). Session mute / ~0 volume
/// is reported as ambient LastIssue only; phase stays Running.
/// </remarks>
internal sealed class KeepAliveAudioService : IDisposable
{
    private static readonly TimeSpan BurstDuration = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PulsePeriod = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// If a stream ran at least this long before failing, reset backoff to the
    /// initial delay (device was healthy; try again soon).
    /// </summary>
    private static readonly TimeSpan HealthyStreamThreshold = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often to sample session mute/volume while streaming (not every pad poll).
    /// </summary>
    private static readonly TimeSpan AttenuationSamplePeriod = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Master volume at or below this is treated as effectively silent (same order
    /// as <see cref="InaudiblePeak"/>).
    /// </summary>
    private const float SessionVolumeZeroThreshold = 0.0001f;

    /// <summary>
    /// First join after demoting a cancelled worker into the drain slot
    /// (control thread only). On timeout a new worker may start while the
    /// drain is still live.
    /// </summary>
    private static readonly TimeSpan WorkerJoinTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Poll quantum while waiting for a busy drain before further demote/start
    /// (control thread only). Keeps Stop/epoch coalescing responsive.
    /// Not used as the first Dispose drain wait — see
    /// <see cref="WorkerJoinDisposeDrainTimeout"/>.
    /// </summary>
    private static readonly TimeSpan WorkerJoinPollQuantum = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// First drain join on <see cref="Dispose"/> (control thread only). Longer
    /// than the live poll so a wedged previous generation gets a real try
    /// before we drop tracking and join the current session.
    /// </summary>
    private static readonly TimeSpan WorkerJoinDisposeDrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Bound for waiting on the control thread during <see cref="Dispose"/>.
    /// Covers one dispose-drain wait plus one post-demote join; process exit may
    /// still leave up to two short-lived tails if both generations are wedged.
    /// </summary>
    private static readonly TimeSpan ControlShutdownTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Near-silence peak (~−80 dBFS). Non-zero so picky digital links see activity.</summary>
    private const float InaudiblePeak = 0.0001f;

    private const double InaudibleFrequencyHz = 40.0;

    // Shared-mode buffer preference (~100 ms). Engine may adjust.
    private const long DesiredBufferDurationHns = 1_000_000; // 100 ms in 100-ns units

    private readonly object _gate = new();

    // Intent applied by the control thread. Also cleared when the owning worker
    // exits spontaneously so reconcile does not respawn it.
    private bool _desiredEngaged;
    private AudioType _desiredAudioType = AudioType.Silence;
    private AudioPattern _desiredAudioPattern = AudioPattern.Constant;

    /// <summary>
    /// Bumped on every Start / RestartIfEngaged so reconcile always replaces the
    /// session (device change must reopen even when modes are unchanged). Latest
    /// epoch wins — multiple wakes coalesce into one tear-down/start.
    /// </summary>
    private int _sessionEpoch;

    /// <summary>Epoch of the worker currently registered in <see cref="_worker"/>.</summary>
    private int _appliedEpoch = -1;

    private CancellationTokenSource? _cts;
    private Thread? _worker;

    /// <summary>
    /// At most one cancelled generation awaiting join (tracked drain). Survives control
    /// self-heal — never abandon a live worker as an untracked orphan.
    /// </summary>
    private CancellationTokenSource? _drainingCts;
    private Thread? _drainingWorker;

    /// <summary>
    /// CTS for a registered worker that was cancelled while the drain slot was full.
    /// <see cref="_cts"/> is cleared so worker <c>finally</c> will not revoke desire;
    /// the thread remains in <see cref="_worker"/> until it can be demoted into drain.
    /// </summary>
    private CancellationTokenSource? _parkedCts;

    /// <summary>
    /// Non-drain <see cref="LastIssue"/> displaced while publishing
    /// <see cref="KeepAliveIssueKind.PreviousSessionClosing"/>. Restored when the
    /// drain clears if still Starting/Reconnecting; dropped on a full issue clear.
    /// </summary>
    private KeepAliveStatusDetail? _issueDeferredUnderDrain;

    private Thread? _controlThread;
    private readonly AutoResetEvent _controlWake = new(false);
    private bool _controlExitRequested;
    private bool _disposed;

    /// <summary>
    /// True while keep-alive is engaged (starting, streaming, reconnecting, or
    /// optimistically after <see cref="Start"/>). Cleared on Stop/Dispose, or when
    /// the owning worker exits spontaneously.
    /// </summary>
    public bool IsEngaged { get; private set; }

    /// <summary>
    /// Finer status: Stopped; Starting (cold Start, unproven — including first-open
    /// retries); Running (generation proven, or healthy Pulsed gap); Reconnecting
    /// (failure after a proven stream, or replace-while-desired).
    /// </summary>
    public KeepAlivePhase Phase { get; private set; } = KeepAlivePhase.Stopped;

    /// <summary>
    /// Last problem explanation (open/stream failure, ambient session
    /// mute/zero-volume, or previous session still closing), or null when
    /// healthy / after user Stop.
    /// </summary>
    public KeepAliveStatusDetail? LastIssue { get; private set; }

    /// <summary>
    /// Raised when <see cref="IsEngaged"/> changes. May fire off the UI thread.
    /// </summary>
    public event EventHandler? IsEngagedChanged;

    /// <summary>
    /// Raised when <see cref="Phase"/> changes. May fire off the UI thread.
    /// </summary>
    public event EventHandler? PhaseChanged;

    /// <summary>
    /// Raised when <see cref="LastIssue"/> changes. May fire off the UI thread.
    /// </summary>
    public event EventHandler? LastIssueChanged;

    private enum SessionOutcome
    {
        Completed,
        Cancelled,
        OpenFailed,
        StreamFailed
    }

    private readonly struct SessionResult
    {
        public SessionOutcome Outcome { get; init; }
        public bool StartedStreaming { get; init; }
        public KeepAliveStatusDetail? Issue { get; init; }

        public static SessionResult Cancelled() => new()
        {
            Outcome = SessionOutcome.Cancelled,
            StartedStreaming = false,
            Issue = null,
        };

        public static SessionResult Completed(bool startedStreaming) => new()
        {
            Outcome = SessionOutcome.Completed,
            StartedStreaming = startedStreaming,
            Issue = null,
        };

        public static SessionResult OpenFailed(KeepAliveStatusDetail issue) => new()
        {
            Outcome = SessionOutcome.OpenFailed,
            StartedStreaming = false,
            Issue = issue,
        };

        public static SessionResult StreamFailed(KeepAliveStatusDetail issue, bool startedStreaming = true) => new()
        {
            Outcome = SessionOutcome.StreamFailed,
            StartedStreaming = startedStreaming,
            Issue = issue,
        };
    }

    public KeepAliveAudioService()
    {
        EnsureControlThread_NoLock();
    }

    /// <summary>
    /// Begin keep-alive. If already running, replaces the worker asynchronously.
    /// Returns immediately — observe IsEngaged/Phase and change events.
    /// </summary>
    public void Start(AudioType audioType, AudioPattern audioPattern)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool engagedChanged;
        bool phaseChanged;
        bool issueChanged;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _desiredEngaged = true;
            _desiredAudioType = audioType;
            _desiredAudioPattern = audioPattern;
            _sessionEpoch++;
            CancelRegisteredWorker_NoLock();

            // Optimistic UI before the control thread finishes reconcile.
            // Cold Start stays Starting until IAudioClient.Start succeeds.
            engagedChanged = ApplyEngaged_NoLock(true);
            phaseChanged = ApplyPhase_NoLock(KeepAlivePhase.Starting);
            issueChanged = ClearLastIssue_NoLock();

            EnsureControlThread_NoLock();
            _controlWake.Set();
        }

        RaiseIfChanged(engagedChanged, phaseChanged);
        if (issueChanged)
        {
            RaiseLastIssueChanged();
        }
    }

    /// <summary>
    /// Stop playback and cancel the worker. Returns immediately — a brief tail
    /// of silence may still play until the worker unwinds.
    /// </summary>
    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        bool engagedChanged;
        bool phaseChanged;
        bool issueChanged;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _desiredEngaged = false;
            CancelRegisteredWorker_NoLock();

            // Optimistic UI — do not wait for COM teardown.
            engagedChanged = ApplyEngaged_NoLock(false);
            phaseChanged = ApplyPhase_NoLock(KeepAlivePhase.Stopped);
            issueChanged = ClearLastIssue_NoLock();

            EnsureControlThread_NoLock();
            _controlWake.Set();
        }

        RaiseIfChanged(engagedChanged, phaseChanged);
        if (issueChanged)
        {
            RaiseLastIssueChanged();
        }
    }

    /// <summary>
    /// Tear down and restart if keep-alive is still desired. No-op after Stop
    /// or a spontaneous owning-worker exit. Returns true when a replace was
    /// requested (not when it has completed). Phase becomes
    /// <see cref="KeepAlivePhase.Reconnecting"/> until the new generation
    /// proves <c>IAudioClient.Start</c>.
    /// </summary>
    public bool RestartIfEngaged(AudioType audioType, AudioPattern audioPattern)
    {
        if (_disposed)
        {
            return false;
        }

        bool engagedChanged = false;
        bool phaseChanged = false;

        lock (_gate)
        {
            if (_disposed || !_desiredEngaged)
            {
                return false;
            }

            _desiredAudioType = audioType;
            _desiredAudioPattern = audioPattern;
            _sessionEpoch++;
            CancelRegisteredWorker_NoLock();

            // Replace while desired: generation is unproven. Starting is cold Start only.
            if (!IsEngaged)
            {
                engagedChanged = ApplyEngaged_NoLock(true);
            }

            phaseChanged = ApplyPhase_NoLock(KeepAlivePhase.Reconnecting);

            EnsureControlThread_NoLock();
            _controlWake.Set();
        }

        RaiseIfChanged(engagedChanged, phaseChanged);
        return true;
    }

    /// <summary>
    /// Stops keep-alive and shuts down the control thread with a bound wait.
    /// Interactive UI should call <see cref="Stop"/> instead.
    /// </summary>
    public void Dispose()
    {
        Thread? control;
        bool engagedChanged;
        bool phaseChanged;
        bool issueChanged;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _desiredEngaged = false;
            _controlExitRequested = true;
            CancelRegisteredWorker_NoLock();
            engagedChanged = ApplyEngaged_NoLock(false);
            phaseChanged = ApplyPhase_NoLock(KeepAlivePhase.Stopped);
            issueChanged = ClearLastIssue_NoLock();

            control = _controlThread;
            try
            {
                _controlWake.Set();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down.
            }
        }

        RaiseIfChanged(engagedChanged, phaseChanged);
        if (issueChanged)
        {
            RaiseLastIssueChanged();
        }

        // Bound wait; may run on the UI thread during form close.
        if (control is not null && control.IsAlive)
        {
            control.Join(ControlShutdownTimeout);
        }

        lock (_gate)
        {
            _controlThread = null;
        }

        try
        {
            _controlWake.Dispose();
        }
        catch
        {
            // Ignore double-dispose of the wait handle.
        }
    }

    /// <summary>
    /// Starts the control thread if missing or dead. Caller must hold <see cref="_gate"/>
    /// (or be in the constructor before concurrent access).
    /// </summary>
    private void EnsureControlThread_NoLock()
    {
        if (_disposed || _controlExitRequested)
        {
            return;
        }

        if (_controlThread is { IsAlive: true })
        {
            return;
        }

        var thread = new Thread(ControlMain)
        {
            IsBackground = true,
            Name = "ZeroTone-AudioControl",
        };
        _controlThread = thread;
        thread.Start();
    }

    /// <summary>
    /// Control thread: reconcile desired state with the audio worker.
    /// Only this thread joins audio workers. Unexpected exit respawns a
    /// successor while the service is live; Dispose sets
    /// <c>_controlExitRequested</c> first so clean shutdown does not.
    /// </summary>
    private void ControlMain()
    {
        try
        {
            while (true)
            {
                try
                {
                    _controlWake.WaitOne();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                while (true)
                {
                    if (!TryReconcileOnce())
                    {
                        break;
                    }
                }

                bool exit;
                lock (_gate)
                {
                    exit = _controlExitRequested
                        && !_desiredEngaged
                        && _worker is null
                        && _drainingWorker is null
                        && _drainingCts is null;
                }

                if (exit)
                {
                    TryReconcileOnce();
                    break;
                }
            }
        }
        catch
        {
            // Must not take down the process; successor starts in finally if still live.
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_controlThread, Thread.CurrentThread))
                {
                    _controlThread = null;
                }

                if (!_disposed && !_controlExitRequested)
                {
                    EnsureControlThread_NoLock();
                    try
                    {
                        _controlWake.Set();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Dispose raced after the flags check; ignore.
                    }
                }
            }
        }
    }

    /// <summary>
    /// One tracked-drain reconcile step. Returns true if the caller should immediately
    /// reconcile again (drain poll, desire/epoch work pending).
    /// </summary>
    private bool TryReconcileOnce()
    {
        bool demotedThisPass = false;

        // Live: short poll so Stop stays responsive. Dispose: longer first wait,
        // then abandon if still stuck so we can join the current session.
        TimeSpan drainWait;
        lock (_gate)
        {
            drainWait = (_disposed || _controlExitRequested)
                ? WorkerJoinDisposeDrainTimeout
                : WorkerJoinPollQuantum;
        }

        _ = TryJoinDrain(drainWait, allowAbandonOnDispose: true);

        lock (_gate)
        {
            var desired = _desiredEngaged;
            var exitRequested = _controlExitRequested;
            var disposed = _disposed;
            var hasWorker = _worker is not null;
            var drainFree = !IsDrainAlive_NoLock() && _drainingWorker is null && _drainingCts is null;

            var needStop = hasWorker && (!desired || exitRequested || disposed);
            var needStart = desired
                && !disposed
                && !exitRequested
                && (!hasWorker || _appliedEpoch != _sessionEpoch);
            var needTeardown = needStop || needStart;

            if (needTeardown && hasWorker)
            {
                CancelRegisteredWorker_NoLock();

                if (drainFree)
                {
                    _drainingCts = _cts ?? _parkedCts;
                    _drainingWorker = _worker;
                    _cts = null;
                    _parkedCts = null;
                    _worker = null;
                    _appliedEpoch = -1;
                    demotedThisPass = true;
                }
                else if (_cts is not null)
                {
                    // Drain busy: detach ownership so worker finally will not
                    // clear desire, but keep the thread until the drain frees.
                    _parkedCts = _cts;
                    _cts = null;
                }
            }
        }

        if (demotedThisPass)
        {
            _ = TryJoinDrain(WorkerJoinTimeout, allowAbandonOnDispose: true);
        }

        bool issueChanged = false;
        bool engagedChanged;
        bool phaseChanged;
        bool again;

        lock (_gate)
        {
            if (IsDrainAlive_NoLock())
            {
                issueChanged = PublishPreviousSessionClosing_NoLock();
            }

            var desired = _desiredEngaged;
            var disposed = _disposed;
            var exitRequested = _controlExitRequested;
            var audioType = _desiredAudioType;
            var audioPattern = _desiredAudioPattern;
            var targetEpoch = _sessionEpoch;

            if (desired && !disposed && !exitRequested)
            {
                // Start only when the registered slot is empty (dual-start if drain is live).
                if (_worker is null)
                {
                    _parkedCts = null;
                    var cts = new CancellationTokenSource();
                    _cts = cts;
                    _appliedEpoch = targetEpoch;
                    engagedChanged = ApplyEngaged_NoLock(true);
                    // Starting is published only by public Start(). A still-Running
                    // generation registering here is unproven — use Reconnecting.
                    phaseChanged = Phase switch
                    {
                        KeepAlivePhase.Running or KeepAlivePhase.Stopped
                            => ApplyPhase_NoLock(KeepAlivePhase.Reconnecting),
                        _ => false,
                    };

                    try
                    {
                        var worker = new Thread(() => WorkerMain(audioType, audioPattern, cts))
                        {
                            IsBackground = true,
                            Name = "ZeroTone-KeepAlive",
                        };
                        _worker = worker;
                        worker.Start();
                    }
                    catch (Exception ex)
                    {
                        // Generation never became viable (typically OOM). Do not
                        // retry Thread.Start — fail closed like owning-worker finally.
                        _worker = null;
                        _cts = null;
                        _appliedEpoch = -1;
                        _desiredEngaged = false;
                        engagedChanged = ApplyEngaged_NoLock(false);
                        phaseChanged = ApplyPhase_NoLock(KeepAlivePhase.Stopped);
                        issueChanged = SetLastIssue_NoLock(
                            KeepAliveIssueMapper.FromException(ex)) || issueChanged;
                        try
                        {
                            cts.Dispose();
                        }
                        catch
                        {
                            // Best-effort; same as drain-join / owning-finally CTS dispose.
                        }
                    }
                }
                else
                {
                    engagedChanged = false;
                    phaseChanged = false;
                }
            }
            else
            {
                // Desire off. Keep PreviousSessionClosing while a drain is still live.
                engagedChanged = ApplyEngaged_NoLock(false);
                phaseChanged = ApplyPhase_NoLock(KeepAlivePhase.Stopped);
                if (!IsDrainAlive_NoLock())
                {
                    issueChanged = ClearLastIssue_NoLock() || issueChanged;
                }
            }

            var hasWorkerNow = _worker is not null;
            var stillNeedStart = _desiredEngaged
                && !_disposed
                && !_controlExitRequested
                && (!hasWorkerNow || _appliedEpoch != _sessionEpoch);
            var stillNeedStop = hasWorkerNow && (!_desiredEngaged || _controlExitRequested || _disposed);
            // Occupied drain slots stay work even after a timed-out Join so the
            // next pass can clear a dead-but-tracked drain.
            var drainSlotsOccupied = _drainingWorker is not null || _drainingCts is not null;
            again = stillNeedStart || stillNeedStop || drainSlotsOccupied;
        }

        RaiseIfChanged(engagedChanged, phaseChanged);
        if (issueChanged)
        {
            RaiseLastIssueChanged();
        }

        return again;
    }

    /// <summary>
    /// True when the drain slot holds a still-alive worker. Caller holds <see cref="_gate"/>.
    /// Slot occupancy (non-null fields) is broader — see <c>again</c> in
    /// <see cref="TryReconcileOnce"/> so dead-but-tracked drains still get cleaned up.
    /// </summary>
    private bool IsDrainAlive_NoLock() =>
        _drainingWorker is { IsAlive: true };

    /// <summary>
    /// Join the drain slot with <paramref name="timeout"/>. Control thread only.
    /// On success: dispose CTS, clear slot, clear previous-session ambient issue.
    /// On timeout while live: leave the drain tracked. When
    /// <paramref name="allowAbandonOnDispose"/> and dispose/exit is requested,
    /// a timed-out join drops tracking so shutdown can join the other generation.
    /// </summary>
    /// <returns>True if a live drain remains after this call.</returns>
    private bool TryJoinDrain(TimeSpan timeout, bool allowAbandonOnDispose)
    {
        Thread? worker;
        CancellationTokenSource? cts;

        lock (_gate)
        {
            worker = _drainingWorker;
            cts = _drainingCts;
        }

        if (worker is null && cts is null)
        {
            return false;
        }

        var joined = true;
        if (worker is not null && worker.IsAlive)
        {
            joined = worker.Join(timeout);
        }

        if (!joined)
        {
            bool abandonedIssueChanged = false;
            lock (_gate)
            {
                if (allowAbandonOnDispose && (_disposed || _controlExitRequested)
                    && !_desiredEngaged)
                {
                    // Leave the CTS for the thread; drop tracking so we can join
                    // the current session (up to two process-exit tails).
                    _drainingWorker = null;
                    _drainingCts = null;
                    abandonedIssueChanged = ClearPreviousSessionIssueOnly_NoLock();
                }
                else
                {
                    return IsDrainAlive_NoLock();
                }
            }

            if (abandonedIssueChanged)
            {
                RaiseLastIssueChanged();
            }

            return false;
        }

        bool issueChanged;
        lock (_gate)
        {
            if (ReferenceEquals(_drainingWorker, worker))
            {
                _drainingWorker = null;
            }

            if (ReferenceEquals(_drainingCts, cts))
            {
                _drainingCts = null;
            }

            issueChanged = ClearPreviousSessionIssueOnly_NoLock();
        }

        try
        {
            cts?.Dispose();
        }
        catch
        {
            // Best-effort CTS dispose after join.
        }

        if (issueChanged)
        {
            RaiseLastIssueChanged();
        }

        return false;
    }

    private bool ApplyEngaged_NoLock(bool value)
    {
        if (IsEngaged == value)
        {
            return false;
        }

        IsEngaged = value;
        return true;
    }

    private bool ApplyPhase_NoLock(KeepAlivePhase value)
    {
        if (Phase == value)
        {
            return false;
        }

        Phase = value;
        return true;
    }

    /// <summary>
    /// Cancel the registered or parked worker CTS. Does not Join. Caller holds
    /// <see cref="_gate"/>. Idempotent if already cancelled or no CTS.
    /// </summary>
    private void CancelRegisteredWorker_NoLock()
    {
        var ctsToCancel = _cts ?? _parkedCts;
        if (ctsToCancel is null)
        {
            return;
        }

        try
        {
            ctsToCancel.Cancel();
        }
        catch
        {
            // Ignore cancel races during teardown.
        }
    }

    /// <summary>
    /// Worker phase/LastIssue publishes only when <paramref name="owner"/> is still
    /// the registered CTS, keep-alive is still desired, and that generation is
    /// still the current epoch. Caller holds <see cref="_gate"/>.
    /// </summary>
    private bool TryBeginOwnerPublish_NoLock(CancellationTokenSource owner) =>
        ReferenceEquals(_cts, owner) && _desiredEngaged && _appliedEpoch == _sessionEpoch;

    /// <summary>
    /// Worker-only phase publish. No-op if <paramref name="owner"/> is not the
    /// registered desired current-epoch generation. Control / <c>finally</c> use
    /// <see cref="ApplyPhase_NoLock"/> directly.
    /// </summary>
    private void SetPhase(CancellationTokenSource owner, KeepAlivePhase value)
    {
        bool changed;
        lock (_gate)
        {
            if (!TryBeginOwnerPublish_NoLock(owner))
            {
                return;
            }

            changed = ApplyPhase_NoLock(value);
        }

        if (changed)
        {
            RaisePhaseChanged();
        }
    }

    /// <summary>
    /// After an open/stream failure: keep <see cref="KeepAlivePhase.Starting"/>
    /// until this cold engage has proven <c>IAudioClient.Start</c>. Otherwise
    /// publish <see cref="KeepAlivePhase.Reconnecting"/> (lost proven stream, or
    /// already Reconnecting from <c>RestartIfEngaged</c>).
    /// </summary>
    private void SetPhaseAfterSessionFailure(CancellationTokenSource owner)
    {
        bool changed;
        lock (_gate)
        {
            if (!TryBeginOwnerPublish_NoLock(owner))
            {
                return;
            }

            if (Phase == KeepAlivePhase.Starting)
            {
                return;
            }

            changed = ApplyPhase_NoLock(KeepAlivePhase.Reconnecting);
        }

        if (changed)
        {
            RaisePhaseChanged();
        }
    }

    private void RaiseIfChanged(bool engagedChanged, bool phaseChanged)
    {
        if (engagedChanged)
        {
            RaiseIsEngagedChanged();
        }

        if (phaseChanged)
        {
            RaisePhaseChanged();
        }
    }

    private void RaiseIsEngagedChanged()
    {
        try
        {
            IsEngagedChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Subscribers must not take the process down.
        }
    }

    private void RaisePhaseChanged()
    {
        try
        {
            PhaseChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Subscribers must not take the process down.
        }
    }

    private void RaiseLastIssueChanged()
    {
        try
        {
            LastIssueChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Subscribers must not take the process down.
        }
    }

    /// <summary>
    /// Worker-only issue publish. No-op if <paramref name="owner"/> is not the
    /// registered desired generation.
    /// </summary>
    private void ReportIssue(CancellationTokenSource owner, KeepAliveStatusDetail issue)
    {
        ArgumentNullException.ThrowIfNull(issue);

        bool changed;
        lock (_gate)
        {
            if (!TryBeginOwnerPublish_NoLock(owner))
            {
                return;
            }

            changed = SetLastIssue_NoLock(issue);
        }

        if (changed)
        {
            RaiseLastIssueChanged();
        }
    }

    private bool SetLastIssue_NoLock(KeepAliveStatusDetail issue)
    {
        if (LastIssue is not null && LastIssue.SameContentAs(issue))
        {
            return false;
        }

        LastIssue = issue;
        return true;
    }

    private bool ClearLastIssue_NoLock()
    {
        _issueDeferredUnderDrain = null;

        if (LastIssue is null)
        {
            return false;
        }

        LastIssue = null;
        return true;
    }

    /// <summary>
    /// Clears <see cref="LastIssue"/> only when it is session mute / zero-volume.
    /// Does not clear open/stream failure or previous-session issues.
    /// No-op if <paramref name="owner"/> is not the registered desired generation.
    /// </summary>
    private void ClearAttenuationIssueOnly(CancellationTokenSource owner)
    {
        bool changed;
        lock (_gate)
        {
            if (!TryBeginOwnerPublish_NoLock(owner))
            {
                return;
            }

            if (LastIssue is null || !KeepAliveIssueMapper.IsSessionAttenuation(LastIssue.Kind))
            {
                return;
            }

            // ClearLastIssue_NoLock would also drop the drain-deferred stash.
            LastIssue = null;
            changed = true;
        }

        if (changed)
        {
            RaiseLastIssueChanged();
        }
    }

    /// <summary>
    /// Publishes drain ambient while a cancelled worker is still live. Other
    /// LastIssue values are stashed and restored when the drain clears.
    /// Caller holds <see cref="_gate"/>.
    /// </summary>
    private bool PublishPreviousSessionClosing_NoLock()
    {
        if (LastIssue is not null
            && !KeepAliveIssueMapper.IsPreviousSessionClosing(LastIssue.Kind))
        {
            _issueDeferredUnderDrain = LastIssue;
        }

        return SetLastIssue_NoLock(KeepAliveIssueMapper.PreviousSessionClosing());
    }

    /// <summary>
    /// Clears previous-session ambient only. Restores a stashed issue when still
    /// engaged and Starting/Reconnecting; otherwise clears LastIssue.
    /// Caller holds <see cref="_gate"/>.
    /// </summary>
    private bool ClearPreviousSessionIssueOnly_NoLock()
    {
        if (LastIssue is null || !KeepAliveIssueMapper.IsPreviousSessionClosing(LastIssue.Kind))
        {
            return false;
        }

        var deferred = _issueDeferredUnderDrain;
        _issueDeferredUnderDrain = null;

        // Restore only if still unwell — do not resurrect a failure after dual recovery.
        if (deferred is not null
            && IsEngaged
            && Phase is KeepAlivePhase.Starting or KeepAlivePhase.Reconnecting)
        {
            LastIssue = deferred;
            return true;
        }

        LastIssue = null;
        return true;
    }

    /// <summary>
    /// After a healthy <c>IAudioClient.Start</c>: drop reconnect/failure issues,
    /// but keep previous-session ambient text while a drain is still live.
    /// </summary>
    private void ClearLastIssuePreservingDrainAmbient(CancellationTokenSource owner)
    {
        bool changed;
        lock (_gate)
        {
            if (!TryBeginOwnerPublish_NoLock(owner))
            {
                return;
            }

            if (LastIssue is null)
            {
                return;
            }

            if (IsDrainAlive_NoLock()
                && KeepAliveIssueMapper.IsPreviousSessionClosing(LastIssue.Kind))
            {
                return;
            }

            var restoreClosing = IsDrainAlive_NoLock();
            changed = ClearLastIssue_NoLock();
            if (restoreClosing)
            {
                changed = SetLastIssue_NoLock(KeepAliveIssueMapper.PreviousSessionClosing()) || changed;
            }
        }

        if (changed)
        {
            RaiseLastIssueChanged();
        }
    }

    /// <summary>
    /// Best-effort read of this session's mute and master volume. False on any
    /// COM failure (caller leaves LastIssue unchanged).
    /// </summary>
    private static bool TryReadSessionAttenuation(
        ISimpleAudioVolume volume,
        out bool muted,
        out float level)
    {
        muted = false;
        level = 1f;

        var hr = volume.GetMute(out muted);
        if (hr < 0)
        {
            return false;
        }

        hr = volume.GetMasterVolume(out level);
        return hr >= 0;
    }

    /// <summary>
    /// Ambient mixer attenuation while streaming. Does not change phase or desire.
    /// Mute wins over ~0 volume. Skipped while a previous session is draining.
    /// </summary>
    private void SampleSessionAttenuation(CancellationTokenSource owner, ISimpleAudioVolume? volume)
    {
        if (volume is null)
        {
            return;
        }

        lock (_gate)
        {
            if (!TryBeginOwnerPublish_NoLock(owner))
            {
                return;
            }

            if (IsDrainAlive_NoLock())
            {
                return;
            }
        }

        if (!TryReadSessionAttenuation(volume, out var muted, out var level))
        {
            return;
        }

        if (muted)
        {
            ReportIssue(owner, KeepAliveIssueMapper.SessionMuted());
            return;
        }

        if (level <= SessionVolumeZeroThreshold)
        {
            ReportIssue(owner, KeepAliveIssueMapper.SessionVolumeZero());
            return;
        }

        ClearAttenuationIssueOnly(owner);
    }

    /// <summary>Background entry: COM work stays on this thread for the worker lifetime.</summary>
    private void WorkerMain(AudioType audioType, AudioPattern audioPattern, CancellationTokenSource cts)
    {
        try
        {
            if (audioPattern == AudioPattern.Constant)
            {
                RunConstantSupervisor(audioType, cts);
            }
            else
            {
                RunPulsedSupervisor(audioType, cts);
            }
        }
        catch (OperationCanceledException)
        {
            // Stop or session replace.
        }
        catch (Exception ex)
        {
            // Unexpected fault after a proven stream — not open-time COM
            // (that is OpenFailed inside RunSession).
            ReportIssue(cts, KeepAliveIssueMapper.FromException(ex));
        }
        finally
        {
            // Owning-session teardown only. If control already detached our CTS,
            // do not touch the newer generation. Does not use TryBeginOwnerPublish
            // (must revoke desire while owning the current epoch).
            bool engagedChanged = false;
            bool phaseChanged = false;
            bool disposeOwnedCts = false;

            lock (_gate)
            {
                if (ReferenceEquals(_cts, cts) && _appliedEpoch == _sessionEpoch)
                {
                    _worker = null;
                    _cts = null;
                    _appliedEpoch = -1;
                    // Spontaneous end of the owning worker: revoke intent so
                    // control does not respawn after a poison-pill exit.
                    _desiredEngaged = false;
                    engagedChanged = ApplyEngaged_NoLock(false);
                    phaseChanged = ApplyPhase_NoLock(KeepAlivePhase.Stopped);
                    disposeOwnedCts = true;
                }
            }

            if (disposeOwnedCts)
            {
                try
                {
                    cts.Dispose();
                }
                catch
                {
                    // Best-effort; same as drain-join CTS dispose.
                }
            }

            RaiseIfChanged(engagedChanged, phaseChanged);
        }
    }

    /// <summary>
    /// Continuous stream with reconnect-on-failure. Worker stays alive while
    /// keep-alive is desired. Cold Start stays Starting until the first proven
    /// <c>IAudioClient.Start</c>; later failures publish Reconnecting.
    /// Running is set only via <see cref="NotifyStreamingStarted"/>.
    /// </summary>
    private void RunConstantSupervisor(AudioType audioType, CancellationTokenSource cts)
    {
        var token = cts.Token;
        var backoff = InitialReconnectDelay;

        while (!token.IsCancellationRequested)
        {
            var streamStartedAt = Environment.TickCount64;
            var result = RunSession(audioType, duration: null, cts);

            if (result.Outcome == SessionOutcome.Cancelled || token.IsCancellationRequested)
            {
                break;
            }

            if (result.Outcome is SessionOutcome.OpenFailed or SessionOutcome.StreamFailed)
            {
                if (result.Issue is not null)
                {
                    ReportIssue(cts, result.Issue);
                }

                if (result.StartedStreaming)
                {
                    var streamedMs = Environment.TickCount64 - streamStartedAt;
                    if (streamedMs >= HealthyStreamThreshold.TotalMilliseconds)
                    {
                        backoff = InitialReconnectDelay;
                    }
                }

                SetPhaseAfterSessionFailure(cts);
                WaitForCancelOrDelay(token, backoff);

                var nextMs = Math.Min(
                    MaxReconnectDelay.TotalMilliseconds,
                    backoff.TotalMilliseconds * 2);
                backoff = TimeSpan.FromMilliseconds(nextMs);
                continue;
            }

            // Unlimited-duration Completed should not happen; finally revokes desire.
            break;
        }
    }

    /// <summary>
    /// Wall-clock 1 s burst every 10 s. Open/stream failures skip the burst;
    /// cancel ends the supervisor. Same Starting-until-first-proof /
    /// Reconnecting-after-proven-loss rule as Constant.
    /// </summary>
    private void RunPulsedSupervisor(AudioType audioType, CancellationTokenSource cts)
    {
        var token = cts.Token;

        while (!token.IsCancellationRequested)
        {
            var periodStart = Environment.TickCount64;
            var result = RunSession(audioType, BurstDuration, cts);

            if (result.Outcome == SessionOutcome.Cancelled || token.IsCancellationRequested)
            {
                break;
            }

            if (result.Outcome is SessionOutcome.OpenFailed or SessionOutcome.StreamFailed)
            {
                if (result.Issue is not null)
                {
                    ReportIssue(cts, result.Issue);
                }

                SetPhaseAfterSessionFailure(cts);
            }

            if (token.IsCancellationRequested)
            {
                break;
            }

            var elapsedMs = Environment.TickCount64 - periodStart;
            var remainingMs = (long)PulsePeriod.TotalMilliseconds - elapsedMs;
            if (remainingMs > 0)
            {
                WaitForCancelOrDelay(
                    token,
                    TimeSpan.FromMilliseconds(remainingMs > int.MaxValue ? int.MaxValue : remainingMs));
            }
        }
    }

    /// <summary>
    /// Called once per successful <c>IAudioClient.Start</c> — healthy streaming.
    /// No-op if <paramref name="owner"/> is not the registered desired generation.
    /// </summary>
    private void NotifyStreamingStarted(CancellationTokenSource owner)
    {
        SetPhase(owner, KeepAlivePhase.Running);
    }

    private static void WaitForCancelOrDelay(CancellationToken token, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero || token.IsCancellationRequested)
        {
            return;
        }

        var ms = delay.TotalMilliseconds;
        if (ms > int.MaxValue)
        {
            ms = int.MaxValue;
        }

        token.WaitHandle.WaitOne((int)ms);
    }

    /// <summary>
    /// Open the default render device, stream until <paramref name="duration"/>
    /// elapses (null = until cancel or failure), then tear down.
    /// Throws before a proven <c>IAudioClient.Start</c> are
    /// <see cref="SessionOutcome.OpenFailed"/>; after Start, unexpected throws
    /// escape and the owning finally fail-closes.
    /// </summary>
    private SessionResult RunSession(
        AudioType audioType,
        TimeSpan? duration,
        CancellationTokenSource owner)
    {
        var token = owner.Token;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioRenderClient? render = null;
        ISimpleAudioVolume? sessionVolume = null;
        IntPtr mixFormatPtr = IntPtr.Zero;
        var streamProven = false;

        try
        {
            if (token.IsCancellationRequested)
            {
                return SessionResult.Cancelled();
            }

            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

            var hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var devicePtr);
            if (hr < 0 || devicePtr == IntPtr.Zero)
            {
                if (token.IsCancellationRequested)
                {
                    return SessionResult.Cancelled();
                }

                return SessionResult.OpenFailed(
                    hr < 0 ? KeepAliveIssueMapper.FromOpenHResult(hr) : KeepAliveIssueMapper.NoPlaybackDevice());
            }

            try
            {
                device = (IMMDevice)Marshal.GetObjectForIUnknown(devicePtr);
            }
            finally
            {
                Marshal.Release(devicePtr);
            }

            var clientIid = WasapiGuids.IAudioClient;
            hr = device.Activate(ref clientIid, ClsCtx.All, IntPtr.Zero, out var clientObj);
            if (hr < 0 || clientObj is null)
            {
                if (token.IsCancellationRequested)
                {
                    return SessionResult.Cancelled();
                }

                return SessionResult.OpenFailed(
                    hr < 0
                        ? KeepAliveIssueMapper.FromOpenHResult(hr)
                        : new KeepAliveStatusDetail(
                            KeepAliveIssueKind.EndpointUnavailable,
                            "Could not open the playback endpoint."));
            }

            if (clientObj is not IAudioClient audioClient)
            {
                SafeReleaseCom(clientObj);
                return token.IsCancellationRequested
                    ? SessionResult.Cancelled()
                    : SessionResult.OpenFailed(
                        new KeepAliveStatusDetail(
                            KeepAliveIssueKind.EndpointUnavailable,
                            "Could not open the playback endpoint."));
            }

            client = audioClient;

            hr = client.GetMixFormat(out mixFormatPtr);
            if (hr < 0 || mixFormatPtr == IntPtr.Zero)
            {
                if (token.IsCancellationRequested)
                {
                    return SessionResult.Cancelled();
                }

                return SessionResult.OpenFailed(
                    hr < 0
                        ? KeepAliveIssueMapper.FromOpenHResult(hr)
                        : KeepAliveIssueMapper.UnsupportedFormat());
            }

            if (!MixFormatInfo.TryParse(mixFormatPtr, out var format))
            {
                if (token.IsCancellationRequested)
                {
                    return SessionResult.Cancelled();
                }

                return SessionResult.OpenFailed(KeepAliveIssueMapper.UnsupportedFormat());
            }

            hr = client.Initialize(
                AudClntShareMode.Shared,
                AudClntStreamFlags.None,
                DesiredBufferDurationHns,
                0,
                mixFormatPtr,
                IntPtr.Zero);

            if (hr < 0)
            {
                return token.IsCancellationRequested
                    ? SessionResult.Cancelled()
                    : SessionResult.OpenFailed(KeepAliveIssueMapper.FromOpenHResult(hr));
            }

            hr = client.GetBufferSize(out var bufferFrames);
            if (hr < 0 || bufferFrames == 0)
            {
                if (token.IsCancellationRequested)
                {
                    return SessionResult.Cancelled();
                }

                return SessionResult.OpenFailed(
                    hr < 0
                        ? KeepAliveIssueMapper.FromOpenHResult(hr)
                        : new KeepAliveStatusDetail(
                            KeepAliveIssueKind.EndpointUnavailable,
                            "Could not open the playback endpoint."));
            }

            hr = client.GetDevicePeriod(out var defaultPeriodHns, out _);
            if (hr < 0)
            {
                defaultPeriodHns = 100_000; // 10 ms fallback
            }

            var renderIid = WasapiGuids.IAudioRenderClient;
            hr = client.GetService(ref renderIid, out var renderObj);
            if (hr < 0 || renderObj is null)
            {
                if (token.IsCancellationRequested)
                {
                    return SessionResult.Cancelled();
                }

                return SessionResult.OpenFailed(
                    hr < 0
                        ? KeepAliveIssueMapper.FromOpenHResult(hr)
                        : new KeepAliveStatusDetail(
                            KeepAliveIssueKind.EndpointUnavailable,
                            "Could not open the playback endpoint."));
            }

            if (renderObj is not IAudioRenderClient renderClient)
            {
                SafeReleaseCom(renderObj);
                return token.IsCancellationRequested
                    ? SessionResult.Cancelled()
                    : SessionResult.OpenFailed(
                        new KeepAliveStatusDetail(
                            KeepAliveIssueKind.EndpointUnavailable,
                            "Could not open the playback endpoint."));
            }

            render = renderClient;

            // Mute/volume is optional; failure is not an open failure.
            var volumeIid = WasapiGuids.ISimpleAudioVolume;
            hr = client.GetService(ref volumeIid, out var volumeObj);
            if (hr >= 0 && volumeObj is not null)
            {
                if (volumeObj is ISimpleAudioVolume volume)
                {
                    sessionVolume = volume;
                }
                else
                {
                    SafeReleaseCom(volumeObj);
                }
            }

            // Pre-roll: fill the buffer once before Start to avoid an initial glitch.
            hr = client.GetCurrentPadding(out var padding);
            if (hr < 0)
            {
                return token.IsCancellationRequested
                    ? SessionResult.Cancelled()
                    : SessionResult.OpenFailed(KeepAliveIssueMapper.FromOpenHResult(hr));
            }

            var phase = 0.0;
            var framesToWrite = bufferFrames - padding;
            if (framesToWrite > 0)
            {
                if (!WriteFrames(render, format, audioType, framesToWrite, ref phase))
                {
                    if (token.IsCancellationRequested)
                    {
                        return SessionResult.Cancelled();
                    }

                    return SessionResult.OpenFailed(
                        new KeepAliveStatusDetail(
                            KeepAliveIssueKind.EndpointUnavailable,
                            "Could not open the playback endpoint."));
                }
            }

            hr = client.Start();
            if (hr < 0)
            {
                return token.IsCancellationRequested
                    ? SessionResult.Cancelled()
                    : SessionResult.OpenFailed(KeepAliveIssueMapper.FromOpenHResult(hr));
            }

            // Cancel may have arrived during blocking Start — do not publish success.
            if (token.IsCancellationRequested)
            {
                return SessionResult.Cancelled();
            }

            streamProven = true;

            ClearLastIssuePreservingDrainAmbient(owner);
            try
            {
                NotifyStreamingStarted(owner);
            }
            catch
            {
                // Phase/UI callbacks must not take down the audio worker.
            }

            var started = Environment.TickCount64;
            var sleepMs = Math.Clamp((int)(defaultPeriodHns / 10_000 / 2), 1, 20);
            var nextAttenuationSampleAt = Environment.TickCount64;

            while (!token.IsCancellationRequested)
            {
                if (duration is TimeSpan limit)
                {
                    var elapsed = Environment.TickCount64 - started;
                    if (elapsed >= limit.TotalMilliseconds)
                    {
                        return SessionResult.Completed(startedStreaming: true);
                    }
                }

                var now = Environment.TickCount64;
                if (now >= nextAttenuationSampleAt)
                {
                    SampleSessionAttenuation(owner, sessionVolume);
                    nextAttenuationSampleAt = now + (long)AttenuationSamplePeriod.TotalMilliseconds;
                }

                hr = client.GetCurrentPadding(out padding);
                if (hr < 0)
                {
                    return token.IsCancellationRequested
                        ? SessionResult.Cancelled()
                        : SessionResult.StreamFailed(KeepAliveIssueMapper.FromStreamHResult(hr));
                }

                framesToWrite = bufferFrames - padding;
                if (framesToWrite > 0)
                {
                    // Cap remaining frames so a pulsed burst does not overshoot.
                    if (duration is TimeSpan burstLimit)
                    {
                        var elapsedMs = Environment.TickCount64 - started;
                        var remainingMs = burstLimit.TotalMilliseconds - elapsedMs;
                        if (remainingMs <= 0)
                        {
                            return SessionResult.Completed(startedStreaming: true);
                        }

                        var maxFrames = (uint)Math.Max(1, format.SampleRate * remainingMs / 1000.0);
                        if (framesToWrite > maxFrames)
                        {
                            framesToWrite = maxFrames;
                        }
                    }

                    if (!WriteFrames(render, format, audioType, framesToWrite, ref phase))
                    {
                        return token.IsCancellationRequested
                            ? SessionResult.Cancelled()
                            : SessionResult.StreamFailed(KeepAliveIssueMapper.StreamLost());
                    }
                }

                token.WaitHandle.WaitOne(sleepMs);
            }

            return SessionResult.Cancelled();
        }
        catch (OperationCanceledException)
        {
            return SessionResult.Cancelled();
        }
        catch (Exception ex) when (!streamProven)
        {
            return token.IsCancellationRequested
                ? SessionResult.Cancelled()
                : SessionResult.OpenFailed(KeepAliveIssueMapper.FromOpenException(ex));
        }
        finally
        {
            if (mixFormatPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(mixFormatPtr);
            }

            // Release COM in reverse order of acquisition (volume before client).
            SafeReleaseCom(sessionVolume);
            SafeReleaseCom(render);
            if (client is not null)
            {
                try
                {
                    client.Stop();
                }
                catch
                {
                    // Already stopped or device gone.
                }

                SafeReleaseCom(client);
            }

            SafeReleaseCom(device);
            SafeReleaseCom(enumerator);
        }
    }

    /// <summary>
    /// Fills and releases one render packet. Silence writes mix-format zeros
    /// and releases with <see cref="AudClntBufferFlags.None"/> (not
    /// <c>AUDCLNT_BUFFERFLAGS_SILENT</c>). Inaudible writes a near-zero tone.
    /// </summary>
    private static bool WriteFrames(
        IAudioRenderClient render,
        MixFormatInfo format,
        AudioType audioType,
        uint frames,
        ref double phase)
    {
        var hr = render.GetBuffer(frames, out var dataPtr);
        if (hr < 0 || dataPtr == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (audioType == AudioType.Silence)
            {
                var byteCount = checked((int)frames * format.BlockAlign);
                unsafe
                {
                    new Span<byte>((void*)dataPtr, byteCount).Clear();
                }
            }
            else
            {
                FillInaudible(dataPtr, frames, format, ref phase);
            }

            hr = render.ReleaseBuffer(frames, AudClntBufferFlags.None);
            return hr >= 0;
        }
        catch
        {
            try
            {
                render.ReleaseBuffer(0, AudClntBufferFlags.None);
            }
            catch
            {
                // Ignore double-fault on release.
            }

            return false;
        }
    }

    private static void FillInaudible(IntPtr dataPtr, uint frames, MixFormatInfo format, ref double phase)
    {
        var twoPiF = 2.0 * Math.PI * InaudibleFrequencyHz / format.SampleRate;

        unsafe
        {
            var p = (byte*)dataPtr;
            for (uint i = 0; i < frames; i++)
            {
                var sample = (float)(InaudiblePeak * Math.Sin(phase));
                phase += twoPiF;
                if (phase > Math.PI * 2.0)
                {
                    phase -= Math.PI * 2.0;
                }

                for (int ch = 0; ch < format.Channels; ch++)
                {
                    WriteSample(p, format, sample);
                    p += format.BytesPerSample;
                }
            }
        }
    }

    private static unsafe void WriteSample(byte* dest, MixFormatInfo format, float sample)
    {
        sample = Math.Clamp(sample, -1f, 1f);

        if (format.IsFloat)
        {
            if (format.BitsPerSample == 32)
            {
                *(float*)dest = sample;
            }
            else
            {
                // Unexpected float width — write zeros.
                for (int b = 0; b < format.BytesPerSample; b++)
                {
                    dest[b] = 0;
                }
            }

            return;
        }

        switch (format.BitsPerSample)
        {
            case 8:
                // 8-bit PCM is unsigned, midpoint 128.
                *dest = (byte)Math.Clamp((int)Math.Round(sample * 127f + 128f), 0, 255);
                break;
            case 16:
                *(short*)dest = (short)Math.Clamp((int)Math.Round(sample * 32767f), short.MinValue, short.MaxValue);
                break;
            case 24:
            {
                var v = Math.Clamp((int)Math.Round(sample * 8388607f), -8388608, 8388607);
                dest[0] = (byte)(v & 0xFF);
                dest[1] = (byte)((v >> 8) & 0xFF);
                dest[2] = (byte)((v >> 16) & 0xFF);
                break;
            }
            case 32:
                *(int*)dest = (int)Math.Clamp((long)Math.Round(sample * 2147483647.0), int.MinValue, int.MaxValue);
                break;
            default:
                for (int b = 0; b < format.BytesPerSample; b++)
                {
                    dest[b] = 0;
                }

                break;
        }
    }

    private static void SafeReleaseCom(object? comObject)
    {
        if (comObject is null)
        {
            return;
        }

        try
        {
            Marshal.ReleaseComObject(comObject);
        }
        catch
        {
            // Ignore release failures during teardown.
        }
    }

    /// <summary>Parsed subset of the device mix format for buffer generation.</summary>
    private readonly struct MixFormatInfo
    {
        public int Channels { get; init; }
        public int SampleRate { get; init; }
        public int BitsPerSample { get; init; }
        public int BlockAlign { get; init; }
        public int BytesPerSample { get; init; }
        public bool IsFloat { get; init; }

        public static bool TryParse(IntPtr mixFormatPtr, out MixFormatInfo info)
        {
            info = default;
            if (mixFormatPtr == IntPtr.Zero)
            {
                return false;
            }

            var fmt = Marshal.PtrToStructure<WaveFormatEx>(mixFormatPtr);
            if (fmt.nChannels == 0 || fmt.nSamplesPerSec == 0 || fmt.nBlockAlign == 0)
            {
                return false;
            }

            var isFloat = false;
            var bits = fmt.wBitsPerSample;

            if (fmt.wFormatTag == WaveFormatTags.IeeeFloat)
            {
                isFloat = true;
            }
            else if (fmt.wFormatTag == WaveFormatTags.Extensible && fmt.cbSize >= 22)
            {
                // WAVEFORMATEXTENSIBLE: SubFormat GUID at offset 24 from start
                // (18 byte WAVEFORMATEX + 2 Samples + 4 dwChannelMask = 24).
                var subFormat = Marshal.PtrToStructure<Guid>(mixFormatPtr + 24);
                if (subFormat == AudioSubtypes.IeeeFloat)
                {
                    isFloat = true;
                }
                else if (subFormat != AudioSubtypes.Pcm)
                {
                    // Unsupported compressed mix format — refuse rather than corrupt audio.
                    return false;
                }
            }
            else if (fmt.wFormatTag != WaveFormatTags.Pcm)
            {
                return false;
            }

            // Only widths WriteSample can encode.
            if (isFloat)
            {
                if (bits != 32)
                {
                    return false;
                }
            }
            else if (bits is not (8 or 16 or 24 or 32))
            {
                return false;
            }

            var bytesPerSample = bits / 8;

            // Inaudible advances BytesPerSample per channel. A wider
            // nBlockAlign (e.g. 24-bit in a 32-bit container) would
            // misalign; refuse rather than write a straddled tone.
            if (fmt.nBlockAlign != bytesPerSample * fmt.nChannels)
            {
                return false;
            }

            info = new MixFormatInfo
            {
                Channels = fmt.nChannels,
                SampleRate = (int)fmt.nSamplesPerSec,
                BitsPerSample = bits,
                BlockAlign = fmt.nBlockAlign,
                BytesPerSample = bytesPerSample,
                IsFloat = isFloat,
            };
            return true;
        }
    }
}
