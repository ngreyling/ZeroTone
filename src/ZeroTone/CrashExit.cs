using ZeroTone.Audio;

namespace ZeroTone;

/// <summary>
/// Last-chance unhandled-exception teardown for the tray app.
/// Policy: best-effort hide tray + stop keep-alive intent, optional UI notice, then exit.
/// Not recovery — do not keep the message loop running after unexplained failure.
/// </summary>
internal static class CrashExit
{
    private static readonly object Gate = new();

    private static KeepAliveAudioService? _audioService;
    private static MainForm? _form;

    /// <summary>0 = not run; 1 = cleanup in progress or finished.</summary>
    private static int _ran;

    /// <summary>
    /// Registers process-owned services for crash teardown. Safe to call again (replaces refs).
    /// </summary>
    public static void Register(KeepAliveAudioService audioService, MainForm? form)
    {
        lock (Gate)
        {
            _audioService = audioService;
            _form = form;
        }
    }

    /// <summary>Clears the form reference (e.g. after normal close). Audio may still be registered.</summary>
    public static void UnregisterForm()
    {
        lock (Gate)
        {
            _form = null;
        }
    }

    /// <summary>Clears all registration after composition-root dispose.</summary>
    public static void Unregister()
    {
        lock (Gate)
        {
            _audioService = null;
            _form = null;
        }
    }

    /// <summary>
    /// Best-effort crash path. Never throws. Runs at most once process-wide.
    /// </summary>
    /// <param name="exception">Optional exception for the UI notice.</param>
    /// <param name="showUi">
    /// True only for <see cref="Application.ThreadException"/> (UI thread).
    /// False for <see cref="AppDomain.UnhandledException"/> (may be terminating; no MessageBox).
    /// </param>
    /// <param name="requestAppExit">
    /// When true (UI <see cref="Application.ThreadException"/> path), end the message
    /// loop after cleanup so we do not continue with corrupted UI state.
    /// Uses <see cref="Application.Exit"/> (not <see cref="Environment.Exit"/>) so
    /// <c>Program.Main</c>'s finally can still Dispose audio/monitor.
    /// </param>
    public static void TryRun(Exception? exception, bool showUi, bool requestAppExit)
    {
        if (Interlocked.Exchange(ref _ran, 1) != 0)
        {
            return;
        }

        KeepAliveAudioService? audio;
        MainForm? form;
        lock (Gate)
        {
            audio = _audioService;
            form = _form;
        }

        // Hide tray first so a ghost icon is avoided even if Stop throws.
        try
        {
            form?.TryHideTrayForExit();
        }
        catch
        {
            // Tray hide is best-effort on a crash path.
        }

        try
        {
            audio?.Stop();
        }
        catch
        {
            // Stop intent only — do not join WASAPI or abort threads here.
        }

        if (showUi)
        {
            try
            {
                var detail = exception is null
                    ? "An unexpected error occurred."
                    : $"{exception.GetType().Name}: {Truncate(exception.Message, 300)}";

                MessageBox.Show(
                    "ZeroTone hit an unexpected error and will close." +
                    Environment.NewLine + Environment.NewLine +
                    detail,
                    "ZeroTone",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // MessageBox can fail if the UI is already torn down.
            }
        }

        if (!requestAppExit)
        {
            return;
        }

        // End the message loop so Program.Main's finally can dispose services.
        // Environment.Exit would skip that cleanup.
        try
        {
            Application.Exit();
        }
        catch
        {
            // Exit may already be in progress.
        }
    }

    private static string Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxChars ? value : value[..maxChars] + "...";
    }
}
