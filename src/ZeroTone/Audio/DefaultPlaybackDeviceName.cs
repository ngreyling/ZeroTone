using System.Diagnostics;
using System.Runtime.InteropServices;
using ZeroTone.Audio.Com;

namespace ZeroTone.Audio;

/// <summary>
/// Friendly name of the current Multimedia default playback endpoint
/// (same role keep-alive targets).
/// </summary>
/// <remarks>
/// MMDevice can block. Call from a worker thread; apply the string on the UI thread.
/// </remarks>
internal static class DefaultPlaybackDeviceName
{
    private const int StgmRead = 0;
    private const ushort VtLpWStr = 31;

    /// <summary>
    /// PKEY_Device_FriendlyName — human-readable endpoint name.
    /// </summary>
    private static readonly PropertyKey PkeyDeviceFriendlyName = new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14
    };

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    /// <summary>
    /// Returns the friendly name of the default render device, or a short
    /// fallback when the name cannot be read.
    /// </summary>
    public static string GetFriendlyNameOrFallback()
    {
        try
        {
            var name = TryGetFriendlyName();
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }
        catch
        {
            // COM / endpoint failures — show a placeholder instead of crashing.
        }

        return "(unavailable)";
    }

    private static string? TryGetFriendlyName()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IPropertyStore? store = null;
        var variant = new PropVariant();
        Debug.Assert(
            Marshal.SizeOf<PropVariant>() == (IntPtr.Size == 8 ? 24 : 16),
            "PropVariant must match native PROPVARIANT (16 x86 / 24 x64).");
        var haveVariant = false;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

            // Same role as keep-alive: Multimedia default playback endpoint.
            var hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var devicePtr);
            if (hr < 0 || devicePtr == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                device = (IMMDevice)Marshal.GetObjectForIUnknown(devicePtr);
            }
            finally
            {
                Marshal.Release(devicePtr);
            }

            hr = device.OpenPropertyStore(StgmRead, out var storePtr);
            if (hr < 0 || storePtr == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                store = (IPropertyStore)Marshal.GetObjectForIUnknown(storePtr);
            }
            finally
            {
                Marshal.Release(storePtr);
            }

            var key = PkeyDeviceFriendlyName;
            hr = store.GetValue(ref key, out variant);
            haveVariant = true;
            if (hr < 0 || variant.vt != VtLpWStr || variant.pointerValue == IntPtr.Zero)
            {
                return null;
            }

            return Marshal.PtrToStringUni(variant.pointerValue);
        }
        finally
        {
            if (haveVariant)
            {
                try
                {
                    PropVariantClear(ref variant);
                }
                catch
                {
                    // Best-effort free of any COM-allocated string.
                }
            }

            SafeReleaseCom(store);
            SafeReleaseCom(device);
            SafeReleaseCom(enumerator);
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
            // Ignore release failures during cleanup.
        }
    }
}

/// <summary>
/// Native PROPVARIANT for reading VT_LPWSTR values.
/// 16 bytes on x86, 24 on x64. Union payload starts at offset 8;
/// <see cref="unionPad"/> exists only so the union tail matches BLOB-sized
/// native layout — do not read it.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort vt;
    public ushort reserved1;
    public ushort reserved2;
    public ushort reserved3;
    public IntPtr pointerValue;
    public IntPtr unionPad;
}

/// <summary>IPropertyStore — read endpoint metadata (friendly name, etc.).</summary>
[ComImport]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint cProps);

    [PreserveSig]
    int GetAt(uint iProp, out PropertyKey pkey);

    [PreserveSig]
    int GetValue(ref PropertyKey key, out PropVariant pv);

    [PreserveSig]
    int SetValue(ref PropertyKey key, ref PropVariant pv);

    [PreserveSig]
    int Commit();
}
