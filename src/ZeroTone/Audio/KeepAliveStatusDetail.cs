using System.Runtime.InteropServices;
using ZeroTone.Audio.Com;

namespace ZeroTone.Audio;

/// <summary>
/// Category of keep-alive problem (stable for UI). Includes ambient conditions
/// (mixer mute/zero volume, previous session still closing) that are not
/// stream-open failures.
/// </summary>
internal enum KeepAliveIssueKind
{
    None,
    NoPlaybackDevice,
    DeviceInUse,
    ExclusiveMode,
    UnsupportedFormat,
    EndpointUnavailable,
    StreamLost,
    /// <summary>This process session is muted in the Windows volume mixer.</summary>
    SessionMuted,
    /// <summary>This process session master volume is effectively zero (not muted flag).</summary>
    SessionVolumeZero,
    /// <summary>
    /// A cancelled keep-alive worker is still unwinding (bounded dual / drain wait).
    /// Ambient only — not a stream-open failure.
    /// </summary>
    PreviousSessionClosing,
    UnexpectedError,
    Unknown
}

/// <summary>
/// Immutable snapshot of the last keep-alive problem for ambient UI (tooltips).
/// May be a stream/open failure or an ambient session attenuation notice.
/// </summary>
internal sealed class KeepAliveStatusDetail
{
    public KeepAliveStatusDetail(
        KeepAliveIssueKind kind,
        string message,
        int? hResult = null,
        DateTimeOffset? timestamp = null)
    {
        Kind = kind;
        Message = message ?? throw new ArgumentNullException(nameof(message));
        HResult = hResult;
        Timestamp = timestamp ?? DateTimeOffset.Now;
    }

    public KeepAliveIssueKind Kind { get; }

    /// <summary>Short user-facing explanation.</summary>
    public string Message { get; }

    /// <summary>Optional COM/WASAPI HRESULT from the failing call.</summary>
    public int? HResult { get; }

    public DateTimeOffset Timestamp { get; }

    /// <summary>Tooltip text: message, plus HRESULT when present.</summary>
    public string ToTooltipText()
    {
        if (HResult is int hr)
        {
            return $"{Message} (0x{unchecked((uint)hr):X8})";
        }

        return Message;
    }

    /// <summary>
    /// Same kind, message, and HRESULT (ignores timestamp). Avoids redundant
    /// <c>LastIssueChanged</c> raises.
    /// </summary>
    public bool SameContentAs(KeepAliveStatusDetail? other)
    {
        if (other is null)
        {
            return false;
        }

        return Kind == other.Kind
            && HResult == other.HResult
            && string.Equals(Message, other.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// Maps WASAPI / MMDevice failure codes and open steps to <see cref="KeepAliveStatusDetail"/>.
/// </summary>
internal static class KeepAliveIssueMapper
{
    // E_NOTFOUND — often returned when there is no default audio endpoint.
    private const int E_NotFound = unchecked((int)0x80070490);

    // AUDCLNT_E_DEVICE_INVALIDATED — endpoint went away mid-stream.
    private const int DeviceInvalidated = unchecked((int)0x88890004);

    public static KeepAliveStatusDetail NoPlaybackDevice(int? hResult = null) =>
        new(
            KeepAliveIssueKind.NoPlaybackDevice,
            "No default playback device.",
            hResult);

    public static KeepAliveStatusDetail UnsupportedFormat(int? hResult = null) =>
        new(
            KeepAliveIssueKind.UnsupportedFormat,
            "Could not read the device mix format.",
            hResult);

    public static KeepAliveStatusDetail StreamLost(int? hResult = null) =>
        new(
            KeepAliveIssueKind.StreamLost,
            "Audio stream was interrupted.",
            hResult);

    /// <summary>
    /// Ambient: session muted in the mixer. Not a stream failure — phase stays Running.
    /// </summary>
    public static KeepAliveStatusDetail SessionMuted() =>
        new(
            KeepAliveIssueKind.SessionMuted,
            "Muted in the Windows volume mixer — keep-alive may be ineffective.");

    /// <summary>
    /// Ambient: session volume ~0. Not a stream failure — phase stays Running.
    /// </summary>
    public static KeepAliveStatusDetail SessionVolumeZero() =>
        new(
            KeepAliveIssueKind.SessionVolumeZero,
            "Session volume is zero in the Windows volume mixer — keep-alive may be ineffective.");

    /// <summary>
    /// Ambient: previous cancelled worker still unwinding (tracked drain).
    /// Not a stream-open failure — phase may be Starting / Running / Reconnecting.
    /// </summary>
    public static KeepAliveStatusDetail PreviousSessionClosing() =>
        new(
            KeepAliveIssueKind.PreviousSessionClosing,
            "A previous session is still closing...");

    /// <summary>
    /// True for ambient mixer attenuation kinds (not open/stream failures).
    /// </summary>
    public static bool IsSessionAttenuation(KeepAliveIssueKind kind) =>
        kind is KeepAliveIssueKind.SessionMuted or KeepAliveIssueKind.SessionVolumeZero;

    /// <summary>
    /// True for the previous-session drain ambient kind.
    /// </summary>
    public static bool IsPreviousSessionClosing(KeepAliveIssueKind kind) =>
        kind == KeepAliveIssueKind.PreviousSessionClosing;

    public static KeepAliveStatusDetail FromException(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var msg = string.IsNullOrWhiteSpace(ex.Message)
            ? $"{ex.GetType().Name}."
            : $"{ex.GetType().Name}: {ex.Message}";
        msg = msg.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (msg.Length > 160)
        {
            msg = msg[..157] + "...";
        }

        return new KeepAliveStatusDetail(KeepAliveIssueKind.UnexpectedError, msg);
    }

    /// <summary>
    /// Open-time thrown exception (CoCreate, cast, marshal). <see cref="COMException"/>
    /// uses the same HRESULT table as PreserveSig failures; other types use
    /// <see cref="FromException"/>. Do not route every <c>Exception.HResult</c>
    /// through <see cref="FromOpenHResult"/> — CLR exceptions carry generic codes.
    /// </summary>
    public static KeepAliveStatusDetail FromOpenException(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex is COMException com && com.HResult < 0)
        {
            return FromOpenHResult(com.HResult);
        }

        return FromException(ex);
    }

    public static KeepAliveStatusDetail FromOpenHResult(int hr)
    {
        if (hr == E_NotFound)
        {
            return NoPlaybackDevice(hr);
        }

        if (hr == AudClntHResults.DeviceInUse)
        {
            return new KeepAliveStatusDetail(
                KeepAliveIssueKind.DeviceInUse,
                "Playback device is in use (another app may have exclusive access).",
                hr);
        }

        if (hr == AudClntHResults.ExclusiveModeNotAllowed)
        {
            return new KeepAliveStatusDetail(
                KeepAliveIssueKind.ExclusiveMode,
                "Exclusive-mode playback is blocking keep-alive.",
                hr);
        }

        if (hr == AudClntHResults.UnsupportedFormat)
        {
            return new KeepAliveStatusDetail(
                KeepAliveIssueKind.UnsupportedFormat,
                "Unsupported audio mix format.",
                hr);
        }

        if (hr == AudClntHResults.ServiceNotRunning)
        {
            return new KeepAliveStatusDetail(
                KeepAliveIssueKind.EndpointUnavailable,
                "Windows Audio service is not available.",
                hr);
        }

        if (hr is AudClntHResults.EndpointCreateFailed
            or AudClntHResults.NotInitialized
            or AudClntHResults.BufferOperationPending
            or DeviceInvalidated)
        {
            return new KeepAliveStatusDetail(
                KeepAliveIssueKind.EndpointUnavailable,
                "Could not open the playback endpoint.",
                hr);
        }

        return new KeepAliveStatusDetail(
            KeepAliveIssueKind.Unknown,
            "Could not start keep-alive.",
            hr);
    }

    public static KeepAliveStatusDetail FromStreamHResult(int hr)
    {
        if (hr == DeviceInvalidated || hr == AudClntHResults.DeviceInUse)
        {
            return StreamLost(hr);
        }

        if (hr == AudClntHResults.ServiceNotRunning)
        {
            return new KeepAliveStatusDetail(
                KeepAliveIssueKind.EndpointUnavailable,
                "Windows Audio service is not available.",
                hr);
        }

        return StreamLost(hr);
    }
}
