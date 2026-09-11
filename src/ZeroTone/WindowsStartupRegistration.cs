using Microsoft.Win32;

namespace ZeroTone;

/// <summary>
/// Per-user "Start with Windows" via HKCU Run. Not an <c>AppSettings</c> /
/// <c>settings.json</c> preference — the OS is the source of truth.
/// </summary>
/// <remarks>
/// <para>
/// Value <c>ZeroTone</c> under <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>
/// (quoted exe path). Task Manager / Settings → Startup disable is
/// <c>Explorer\StartupApproved\Run</c> (same name). The Settings checkbox
/// means <b>will start at sign-in</b>, not merely that a Run value exists.
/// </para>
/// <para>
/// HKCU only (no admin). Registry errors return false / treat as off; they
/// do not throw. Unchecking deletes both values. Independent of
/// <c>StartOnLaunch</c> and not stored in settings.json.
/// </para>
/// </remarks>
internal static class WindowsStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "ZeroTone";

    // 12-byte StartupApproved blob. First DWORD: 2 = enabled, 3 = disabled
    // (Explorer / Task Manager). Remainder is unused FILETIME on enable.
    private const int ApprovedEnabledKind = 2;
    private const int ApprovedDisabledKind = 3;

    private static readonly byte[] EnabledApprovedBlob =
    [
        ApprovedEnabledKind, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0
    ];

    /// <summary>
    /// True when this user will start ZeroTone at sign-in (Run present and not
    /// StartupApproved-disabled). If Run exists with a stale path, rewrites it.
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            if (!TryReadRunValue(out var registeredPath) || string.IsNullOrEmpty(registeredPath))
            {
                return false;
            }

            TryHealPath(registeredPath);
            return !IsApprovedDisabled();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Adds or removes the Run (and approved) values. Returns false on
    /// registry failure; does not throw.
    /// </summary>
    public static bool TrySetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var path = GetExecutablePath();
                if (string.IsNullOrEmpty(path))
                {
                    return false;
                }

                using (var run = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true))
                {
                    if (run is null)
                    {
                        return false;
                    }

                    run.SetValue(ValueName, QuotePath(path), RegistryValueKind.String);
                }

                using (var approved = Registry.CurrentUser.CreateSubKey(ApprovedKeyPath, writable: true))
                {
                    approved?.SetValue(ValueName, EnabledApprovedBlob, RegistryValueKind.Binary);
                }

                return true;
            }

            DeleteValue(RunKeyPath, ValueName);
            DeleteValue(ApprovedKeyPath, ValueName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryHealPath(string registeredPath)
    {
        var current = GetExecutablePath();
        if (string.IsNullOrEmpty(current))
        {
            return;
        }

        if (string.Equals(registeredPath, current, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            run?.SetValue(ValueName, QuotePath(current), RegistryValueKind.String);
        }
        catch
        {
            // Leave the stale path; next successful enable rewrites.
        }
    }

    private static bool IsApprovedDisabled()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: false);
        if (approved?.GetValue(ValueName) is not byte[] blob || blob.Length < 4)
        {
            // No approved value: Run alone is treated as enabled.
            return false;
        }

        var kind = BitConverter.ToInt32(blob, 0);
        return kind == ApprovedDisabledKind;
    }

    private static bool TryReadRunValue(out string path)
    {
        path = string.Empty;
        using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        if (run?.GetValue(ValueName) is not string raw || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        path = UnquotePath(raw);
        return path.Length > 0;
    }

    private static void DeleteValue(string keyPath, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    private static string GetExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Application.ExecutablePath;
        }

        return path?.Trim() ?? string.Empty;
    }

    private static string QuotePath(string path)
    {
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
        {
            return path;
        }

        return "\"" + path + "\"";
    }

    private static string UnquotePath(string raw)
    {
        var path = raw.Trim();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
        {
            path = path[1..^1].Trim();
        }

        return path;
    }
}
