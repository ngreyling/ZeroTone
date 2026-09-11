using System.Runtime.InteropServices;

namespace ZeroTone.Audio.Com;

/// <summary>Core Audio data-flow direction (playback vs capture).</summary>
internal enum EDataFlow
{
    Render = 0,
    Capture = 1,
    All = 2
}

/// <summary>
/// Default-device role. One physical device can be default for more than one role;
/// changing output often fires multiple role notifications.
/// </summary>
internal enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2
}

/// <summary>
/// PROPERTYKEY layout for OnPropertyValueChanged. We do not read properties;
/// the struct must still match the COM vtable layout.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid fmtid;
    public int pid;
}

/// <summary>
/// COM coclass for IMMDeviceEnumerator (activate via new + cast; no type library).
/// </summary>
[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}

/// <summary>
/// IMMDeviceEnumerator subset. Method order must match the native IUnknown layout.
/// </summary>
[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    // Not used — placeholders preserve vtable slots.
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IntPtr devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IntPtr endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IMMNotificationClient client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

/// <summary>
/// Callback Windows invokes when the audio endpoint graph changes.
/// Implemented by <see cref="DefaultAudioDeviceMonitor"/>.
/// </summary>
[ComImport]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    void OnDeviceStateChanged(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        int newState);

    void OnDeviceAdded(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    void OnDeviceRemoved(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    void OnDefaultDeviceChanged(
        EDataFlow flow,
        ERole role,
        [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);

    void OnPropertyValueChanged(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId,
        PropertyKey key);
}
