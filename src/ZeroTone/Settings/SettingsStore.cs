using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZeroTone.Settings;

/// <summary>
/// Outcome of <see cref="SettingsStore.Load"/> so the UI can surface corrupt files.
/// </summary>
internal enum SettingsLoadStatus
{
    /// <summary>File read and deserialized successfully.</summary>
    Ok,

    /// <summary>No settings file yet (first run) — defaults applied.</summary>
    MissingFile,

    /// <summary>
    /// File existed but could not be read or parsed. Defaults applied; a
    /// <c>.bak</c> copy may have been written for recovery.
    /// </summary>
    ResetToDefaults
}

/// <summary>
/// Loads/saves <see cref="AppSettings"/> as JSON under %AppData%\ZeroTone\.
/// </summary>
/// <remarks>
/// <para>
/// Construction only computes paths (does not create AppData). <see cref="Save"/>
/// creates the folder on demand and writes via a sibling <c>.tmp</c> then replace
/// so a crash mid-write cannot truncate the last good <c>settings.json</c>.
/// When the payload matches what was last loaded or successfully written,
/// <see cref="Save"/> is a no-op.
/// </para>
/// <para>
/// JSON property names match camelCase <see cref="AppSettings"/> members and
/// are case-sensitive. Unmapped keys and non-string enum values are treated
/// as a corrupt file (reset to defaults). There is no key aliasing.
/// </para>
/// </remarks>
internal sealed class SettingsStore
{
    private const string AppFolderName = "ZeroTone";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, // unknown / wrong-case keys
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private readonly string _settingsPath;

    /// <summary>
    /// Canonical JSON last known to match on-disk content (after a successful
    /// <see cref="Load"/> or <see cref="Save"/>). Used to skip redundant writes.
    /// Null when there is no trusted on-disk baseline (missing/corrupt file, or
    /// never saved in this process).
    /// </summary>
    private string? _lastPersistedJson;

    /// <summary>
    /// Resolves the settings file path under %AppData%\ZeroTone\. Does not create
    /// directories (unwritable profiles must not prevent the app from starting).
    /// </summary>
    public SettingsStore()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppFolderName);

        _settingsPath = Path.Combine(folder, "settings.json");
    }

    public string SettingsPath => _settingsPath;

    /// <summary>Backup path used when a corrupt settings file is quarantined.</summary>
    public string SettingsBackupPath => _settingsPath + ".bak";

    /// <summary>
    /// Reads settings from disk. Never throws for missing/corrupt files: falls back
    /// to defaults. Inspect <paramref name="status"/> when preferences were reset.
    /// </summary>
    public AppSettings Load(out SettingsLoadStatus status)
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                _lastPersistedJson = null;
                status = SettingsLoadStatus.MissingFile;
                return new AppSettings();
            }

            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null || !HasDefinedAudioEnums(settings))
            {
                return ResetCorrupt(out status);
            }

            // Compare later Saves against canonical JSON, not the raw file bytes.
            _lastPersistedJson = JsonSerializer.Serialize(settings, JsonOptions);
            status = SettingsLoadStatus.Ok;
            return settings;
        }
        catch
        {
            // Corrupt / unreadable file (including numeric or unknown enum strings).
            return ResetCorrupt(out status);
        }
    }

    /// <summary>
    /// Writes settings to disk via a sibling <c>.tmp</c> file, then replaces
    /// <see cref="SettingsPath"/>, unless the serialized payload matches what
    /// was last loaded or successfully saved (and the file still exists).
    /// Returns false on any I/O failure (callers may warn once); never throws
    /// for disk errors.
    /// </summary>
    /// <returns>
    /// True if the settings are durable on disk (write succeeded, or skipped
    /// because content was already up to date).
    /// </returns>
    public bool Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var json = JsonSerializer.Serialize(settings, JsonOptions);

        if (_lastPersistedJson is not null
            && _lastPersistedJson == json
            && File.Exists(_settingsPath))
        {
            return true;
        }

        var tempPath = _settingsPath + ".tmp";

        try
        {
            var folder = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _settingsPath, overwrite: true);
            _lastPersistedJson = json;
            return true;
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best-effort temp cleanup; ignore.
            }

            return false;
        }
    }

    /// <summary>
    /// True when both audio enums are named members (not a leftover undefined
    /// integral value). Unknown JSON strings already fail deserialize.
    /// </summary>
    private static bool HasDefinedAudioEnums(AppSettings settings) =>
        Enum.IsDefined(settings.AudioType) && Enum.IsDefined(settings.AudioPattern);

    /// <summary>
    /// Quarantine the on-disk file and return defaults. Used for unreadable JSON,
    /// null payload, and undefined audio enums.
    /// </summary>
    private AppSettings ResetCorrupt(out SettingsLoadStatus status)
    {
        TryQuarantineCorruptFile();
        _lastPersistedJson = null;
        status = SettingsLoadStatus.ResetToDefaults;
        return new AppSettings();
    }

    /// <summary>
    /// Renames a bad settings file to <see cref="SettingsBackupPath"/> so the next
    /// Save can write cleanly without destroying the user's last JSON.
    /// </summary>
    private void TryQuarantineCorruptFile()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return;
            }

            var bak = SettingsBackupPath;
            if (File.Exists(bak))
            {
                File.Delete(bak);
            }

            File.Move(_settingsPath, bak);
        }
        catch
        {
            // Best-effort; defaults still apply even if quarantine fails.
        }
    }
}
