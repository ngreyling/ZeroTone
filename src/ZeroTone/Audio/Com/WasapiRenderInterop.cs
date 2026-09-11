using System.Runtime.InteropServices;

namespace ZeroTone.Audio.Com;

/// <summary>WASAPI share mode (we only use shared).</summary>
internal enum AudClntShareMode
{
    Shared = 0,
    Exclusive = 1
}

/// <summary>Flags for <see cref="IAudioClient.Initialize"/>.</summary>
[Flags]
internal enum AudClntStreamFlags : uint
{
    None = 0,
    EventCallback = 0x00040000,
    AutoConvertPcm = 0x80000000,
    SrcDefaultQuality = 0x08000000
}

/// <summary>
/// Flags for <see cref="IAudioRenderClient.ReleaseBuffer"/>.
/// <see cref="Silent"/> is declared for COM completeness only; keep-alive
/// writes digital zeros and always releases with <see cref="None"/>.
/// </summary>
[Flags]
internal enum AudClntBufferFlags : uint
{
    None = 0,

    /// <summary>
    /// <c>AUDCLNT_BUFFERFLAGS_SILENT</c> — engine treats the packet as silence
    /// regardless of buffer contents. Not used by keep-alive.
    /// </summary>
    Silent = 0x00000001
}

/// <summary>WAVEFORMATEX layout (also the prefix of WAVEFORMATEXTENSIBLE).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatEx
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

internal static class WaveFormatTags
{
    public const ushort Pcm = 0x0001;
    public const ushort IeeeFloat = 0x0003;
    public const ushort Extensible = 0xFFFE;
}

/// <summary>SubFormat GUIDs inside WAVEFORMATEXTENSIBLE.</summary>
internal static class AudioSubtypes
{
    /// <summary>KSDATAFORMAT_SUBTYPE_PCM</summary>
    public static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");

    /// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT</summary>
    public static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
}

internal static class ClsCtx
{
    public const int All = 0x17;
}

/// <summary>Common AUDCLNT HRESULT values for failure mapping.</summary>
internal static class AudClntHResults
{
    public const int DeviceInUse = unchecked((int)0x8889000A);
    public const int UnsupportedFormat = unchecked((int)0x88890008);
    public const int ExclusiveModeNotAllowed = unchecked((int)0x8889000E);
    public const int EndpointCreateFailed = unchecked((int)0x8889000F);
    public const int ServiceNotRunning = unchecked((int)0x88890010);
    public const int BufferOperationPending = unchecked((int)0x8889000B);
    public const int NotInitialized = unchecked((int)0x88890001);
}

/// <summary>IMMDevice — activate IAudioClient on an endpoint.</summary>
[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(
        ref Guid iid,
        int dwClsCtx,
        IntPtr pActivationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object? ppInterface);

    [PreserveSig]
    int OpenPropertyStore(int stgmAccess, out IntPtr properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out int state);
}

/// <summary>
/// IAudioClient — shared-mode stream setup and control.
/// Vtable order must match the native interface.
/// </summary>
[ComImport]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(
        AudClntShareMode shareMode,
        AudClntStreamFlags streamFlags,
        long hnsBufferDuration,
        long hnsPeriodicity,
        IntPtr pFormat,
        IntPtr audioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint bufferFrameCount);

    [PreserveSig]
    int GetStreamLatency(out long hnsLatency);

    [PreserveSig]
    int GetCurrentPadding(out uint numPaddingFrames);

    [PreserveSig]
    int IsFormatSupported(
        AudClntShareMode shareMode,
        IntPtr pFormat,
        out IntPtr ppClosestMatch);

    [PreserveSig]
    int GetMixFormat(out IntPtr ppDeviceFormat);

    [PreserveSig]
    int GetDevicePeriod(out long hnsDefaultDevicePeriod, out long hnsMinimumDevicePeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);

    [PreserveSig]
    int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);
}

/// <summary>IAudioRenderClient — write PCM frames into the endpoint buffer.</summary>
[ComImport]
[Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    [PreserveSig]
    int GetBuffer(uint numFramesRequested, out IntPtr dataPointer);

    [PreserveSig]
    int ReleaseBuffer(uint numFramesWritten, AudClntBufferFlags flags);
}

/// <summary>
/// Per-session mute and master volume (this process's mixer row).
/// Keep-alive only reads mute/level for status; it never writes volume.
/// Vtable order must match the native interface.
/// </summary>
[ComImport]
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    // Declared for COM layout only — keep-alive must not write volume.
    [PreserveSig]
    int SetMasterVolume(float fLevel, ref Guid eventContext);

    [PreserveSig]
    int GetMasterVolume(out float pfLevel);

    // Declared for COM layout only — keep-alive must not write mute.
    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid eventContext);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
}

internal static class WasapiGuids
{
    public static readonly Guid IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static readonly Guid ISimpleAudioVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");
}
