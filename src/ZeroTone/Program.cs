using ZeroTone.Audio;
using ZeroTone.Settings;

namespace ZeroTone;

/// <summary>
/// Application entry and composition root. Owns audio service, playback-graph
/// invalidator, and device-monitor lifetime.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Process entry. [STAThread] is required for WinForms and COM-based multimedia APIs.
    /// </summary>
    [STAThread]
    private static void Main()
    {
        // One instance per logon session (mutex + activate event). A secondary
        // launch restores the primary and exits. If the mutex cannot be created,
        // SingleInstanceGuard still lets this process run.
        if (!SingleInstanceGuard.TryEnterPrimary(out var primaryGuard))
        {
            if (!SingleInstanceGuard.TryRequestActivate())
            {
                // Mutex is held, but the primary could not be signaled.
                MessageBox.Show(
                    "ZeroTone is already running.",
                    "ZeroTone",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            return;
        }

        using (primaryGuard)
        {
            ApplicationConfiguration.Initialize();

            // Hide the tray and stop keep-alive before leaving a broken UI.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += static (_, e) =>
                CrashExit.TryRun(e.Exception, showUi: true, requestAppExit: true);
            AppDomain.CurrentDomain.UnhandledException += static (_, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                // Process may already be terminating — hide tray / stop only; no MessageBox.
                CrashExit.TryRun(ex, showUi: false, requestAppExit: false);
            };

            // Long-lived services live here; MainForm must not dispose them.
            var settingsStore = new SettingsStore();
            var settings = settingsStore.Load(out var loadStatus);
            var audioService = new KeepAliveAudioService();
            var graphInvalidator = new PlaybackGraphInvalidator();
            var deviceMonitor = new DefaultAudioDeviceMonitor(graphInvalidator);

            // Audio available for crash teardown even if form construction fails.
            CrashExit.Register(audioService, form: null);

            try
            {
                // Application.Run also disposes; a second Dispose is idempotent.
                using var form = new MainForm(
                    settings,
                    settingsStore,
                    audioService,
                    graphInvalidator,
                    deviceMonitor,
                    loadStatus);

                CrashExit.Register(audioService, form);
                primaryGuard.StartActivateListener(() => ScheduleActivate(form));
                Application.Run(form);
            }
            finally
            {
                CrashExit.Unregister();
                // Stop audio first, then unregister COM, then the debounce timer.
                audioService.Dispose();
                deviceMonitor.Dispose();
                graphInvalidator.Dispose();
            }
        }
    }

    /// <summary>
    /// Marshals "show window" onto the UI thread. Safe when the form is still
    /// creating its handle (start minimized / tray-only).
    /// Subscribe-then-recheck: a HWND created between the first
    /// <see cref="Control.IsHandleCreated"/> read and <c>HandleCreated +=</c>
    /// must not drop the activate (lost-wakeup).
    /// </summary>
    private static void ScheduleActivate(MainForm form)
    {
        try
        {
            if (form.IsDisposed)
            {
                return;
            }

            if (form.IsHandleCreated)
            {
                TryBeginInvokeActivate(form);
                return;
            }

            // Handle not ready yet (very early secondary launch). Do not touch
            // form.Handle off the UI thread. Subscribe first, then re-check so a
            // create that races the first IsHandleCreated is still observed.
            void OnHandleCreated(object? sender, EventArgs e)
            {
                form.HandleCreated -= OnHandleCreated;
                TryBeginInvokeActivate(form);
            }

            form.HandleCreated += OnHandleCreated;

            if (form.IsHandleCreated)
            {
                form.HandleCreated -= OnHandleCreated;
                TryBeginInvokeActivate(form);
            }
        }
        catch (ObjectDisposedException)
        {
            // Form closed while a secondary launch was signaling.
        }
    }

    /// <summary>
    /// Queues <see cref="MainForm.ActivateFromSecondInstance"/> on the UI thread.
    /// Double-queue is harmless (<c>RestoreWindow</c> is idempotent).
    /// </summary>
    private static void TryBeginInvokeActivate(MainForm form)
    {
        try
        {
            if (form.IsDisposed)
            {
                return;
            }

            form.BeginInvoke(static (MainForm f) => f.ActivateFromSecondInstance(), form);
        }
        catch (ObjectDisposedException)
        {
            // Form closed between the check and invoke.
        }
        catch (InvalidOperationException)
        {
            // Handle torn down mid-close / recreate; next secondary launch retries.
        }
    }
}
