namespace ZeroTone.Settings;

/// <summary>
/// In-memory user preferences. Property names align with the Settings UI and
/// camelCase JSON keys under %AppData%\ZeroTone\settings.json.
/// </summary>
internal sealed class AppSettings
{
    /// <summary>
    /// When true, start keep-alive when the app launches.
    /// Not the live engaged state (that is <c>KeepAliveAudioService.IsEngaged</c>).
    /// </summary>
    public bool StartOnLaunch { get; set; }

    /// <summary>
    /// When true, start minimized (tray or taskbar per <see cref="MinimizeToSystemTray"/>).
    /// </summary>
    public bool MinimizeOnLaunch { get; set; }

    /// <summary>
    /// When true, minimize hides to the system tray (no taskbar button).
    /// Default true for tray-utility behaviour.
    /// </summary>
    public bool MinimizeToSystemTray { get; set; } = true;

    /// <summary>
    /// When true, Close (X) minimizes instead of exiting.
    /// Default true for new installs (tray-utility behaviour).
    /// </summary>
    public bool MinimizeOnClose { get; set; } = true;

    /// <summary>
    /// Silence vs inaudible keep-alive content. Default Silence for new installs.
    /// </summary>
    public AudioType AudioType { get; set; } = AudioType.Silence;

    /// <summary>
    /// Constant (continuous) vs Pulsed (1 s every 10 s). Default Constant for new installs.
    /// </summary>
    public AudioPattern AudioPattern { get; set; } = AudioPattern.Constant;

    /// <summary>
    /// Last window left (X) in screen coordinates, or null for default placement.
    /// </summary>
    public int? WindowLeft { get; set; }

    /// <summary>
    /// Last window top (Y) in screen coordinates, or null for default placement.
    /// </summary>
    public int? WindowTop { get; set; }
}

/// <summary>Keep-alive audio content type (serialized as camelCase strings).</summary>
internal enum AudioType
{
    /// <summary>
    /// Mix-format digital zeros released as ordinary shared-mode packets
    /// (UI: Silence). Not <c>AUDCLNT_BUFFERFLAGS_SILENT</c>.
    /// </summary>
    Silence,

    /// <summary>
    /// Near-zero samples so picky digital paths still see activity
    /// (UI: Inaudible Sound).
    /// </summary>
    Inaudible
}

/// <summary>Keep-alive timing pattern while engaged (serialized as camelCase strings).</summary>
internal enum AudioPattern
{
    /// <summary>Continuous stream while keep-alive is engaged (UI: Constant).</summary>
    Constant,

    /// <summary>1-second burst every 10 seconds (wall-clock; no idle detection) (UI: Pulsed).</summary>
    Pulsed
}
