using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ZeroTone.Audio;
using ZeroTone.Settings;

namespace ZeroTone;

/// <summary>
/// Primary window and tray icon controller. Consumes audio/device services owned by Program.
/// </summary>
internal partial class MainForm : Form
{
    // Delayed tray single-click restore needs AttachThreadInput; Windows
    // blocks SetForegroundWindow from timer callbacks.

    private const int SwRestore = 9;
    private const int WmSysCommand = 0x0112;

    /// <summary>
    /// SC_* commands in WM_SYSCOMMAND; low 4 bits are reserved (mask with 0xFFF0).
    /// </summary>
    private const int ScMinimize = 0xF020;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    /// <summary>
    /// Temporarily joins input queues so SetForegroundWindow is allowed.
    /// Must always detach (fAttach: false) afterward.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const string AppName = "ZeroTone";
    private const string StoppedMessage = "Stopped";
    private const string StartingMessage = "Starting...";
    private const string RunningMessage = "Running";
    private const string ReconnectingMessage = "Reconnecting...";

    /// <summary>
    /// Defer painting amber phase chrome (Starting / Reconnecting) so a fast open
    /// can skip straight to Running (happy-path no amber flash). Engagement chrome
    /// is never deferred.
    /// </summary>
    private const int AmberPhaseUiCoalesceDelayMs = 120;

    /// <summary>
    /// Appended to the output-device tooltip when Core Audio notifications
    /// could not be registered.
    /// </summary>
    private const string FollowDeviceDisabledTooltip =
        "Automatic follow of default device changes is unavailable this session.";

    /// <summary>
    /// Hover time for Audio Options radios (and other mainTabToolTip uses).
    /// Default ~5 s cuts off two-sentence tips.
    /// </summary>
    private const int MainTabToolTipAutoPopMs = 12_000;

    private const string SilenceOptionTip =
        "Digital silence (zeros). Start here. If the output still sleeps, use Inaudible Sound.";

    private const string InaudibleOptionTip =
        "A very quiet tone for outputs that ignore pure digital silence. Prefer Silence unless the output still sleeps.";

    private const string ConstantOptionTip =
        "Continuous keep-alive. Start here. It holds outputs that sleep quickly.";

    private const string PulsedOptionTip =
        "1 second every 10 seconds. If the output sleeps before the next pulse, use Constant.";

    private const string StartWithWindowsTip =
        "Starts ZeroTone when you sign in. Turn on Start on Launch to begin keep-alive, and Minimize on Launch to stay in the tray.";

    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly KeepAliveAudioService _audioService;
    private readonly SettingsLoadStatus _settingsLoadStatus;

    /// <summary>Injected; owned and disposed by Program, not this form.</summary>
    private readonly PlaybackGraphInvalidator _graphInvalidator;

    /// <summary>Injected; owned and disposed by Program, not this form.</summary>
    private readonly DefaultAudioDeviceMonitor _deviceMonitor;

    /// <summary>
    /// False when <see cref="DefaultAudioDeviceMonitor.Start"/> has not succeeded —
    /// keep-alive and wake-from-sleep re-arm still work; only automatic COM
    /// device-follow is off until a UI-thread retry succeeds.
    /// </summary>
    private bool _followDefaultDevice = true;

    /// <summary>
    /// One-shot retry of device-follow registration on first proven Running.
    /// Resume-driven <see cref="PlaybackGraph_Invalidated"/> retries separately.
    /// </summary>
    private bool _deviceFollowRetryOnRunningAttempted;

    private bool _settingsResetWarningShown;

    /// <summary>
    /// Avoids spamming on every checkbox tick if the disk stays full.
    /// </summary>
    private bool _settingsSaveFailureNotified;

    /// <summary>
    /// Avoids repeating the HKCU Run warning if the key stays unwritable.
    /// </summary>
    private bool _startupRegistrationFailureNotified;

    /// <summary>
    /// Reverting Start with Windows after a failed registry write must not
    /// recurse into <see cref="checkStartWithWindows_CheckedChanged"/>.
    /// </summary>
    private bool _suppressStartWithWindowsHandler;

    /// <summary>
    /// Single delayed retry after a graph-invalidation restart (Bluetooth often needs
    /// a second chance). One timer avoids stacking many timers on rapid events.
    /// </summary>
    private System.Windows.Forms.Timer? _graphInvalidationRetryTimer;

    /// <summary>
    /// Defers amber phase chrome (Starting / Reconnecting status/icons) so a fast
    /// open can coalesce to Running.
    /// </summary>
    private System.Windows.Forms.Timer? _amberPhaseUiCoalesceTimer;

    // Multi-res .ico bytes (16–80). Kept so we can re-extract sizes on DPI change.
    private readonly byte[] _iconRunningData;
    private readonly byte[] _iconUnprovenData;
    private readonly byte[] _iconStoppedData;

    private Icon _trayIconRunning = null!;
    private Icon _trayIconUnproven = null!;
    private Icon _trayIconStopped = null!;

    // Form.Icon multi-res copies (green/amber/red from .ico).
    // WinForms pushes SM_CXSMICON + SM_CXICON (32); Win11 taskbar then scales 32→24
    // and blurs. ApplyNativeWindowIcons overwrites ICON_BIG with exact pixels.
    private Icon _windowIconRunning = null!;
    private Icon _windowIconUnproven = null!;
    private Icon _windowIconStopped = null!;

    // HICONs installed via WM_SETICON. Must stay alive; Windows does not copy them.
    private Icon? _nativeSmallIcon;
    private Icon? _nativeBigIcon;

    private readonly Dictionary<int, Icon> _getIconCacheRunning = new();
    private readonly Dictionary<int, Icon> _getIconCacheUnproven = new();
    private readonly Dictionary<int, Icon> _getIconCacheStopped = new();

    /// <summary>Last values applied to chrome — skip redundant GDI / layout work.</summary>
    private bool _uiAppliedIsEngaged;
    private KeepAlivePhase _uiAppliedPhase = KeepAlivePhase.Stopped;
    private string _uiAppliedStatus = string.Empty;
    private string _uiAppliedTooltip = string.Empty;
    private KeepAliveIconVisual _uiAppliedVisual = KeepAliveIconVisual.Stopped;

    /// <summary>
    /// Tray / window icon set (driven by engaged + phase, not engaged alone).
    /// Starting and Reconnecting share the Unproven visual.
    /// </summary>
    private enum KeepAliveIconVisual
    {
        Stopped,
        Running,
        /// <summary>Starting or Reconnecting (stream not proven).</summary>
        Unproven
    }

    private const int WmGetIcon = 0x007F;
    private const int WmSetIcon = 0x0080;
    private const int IconSmall = 0;

    /// <summary>
    /// ICON_BIG: what the Win11 taskbar displays (after scaling if wrong size).
    /// We set this to the real taskbar pixel size (24 @ 100%), not SM_CXICON (32).
    /// </summary>
    private const int IconBig = 1;

    private const int IconSmall2 = 2;

    private static readonly int[] EmbeddedIconSizes =
        [16, 20, 24, 28, 32, 36, 40, 48, 56, 64, 72, 80];

    /// <summary>Logical taskbar button size at 96 DPI on Windows 10/11.</summary>
    private const int TaskbarIconPxAt96Dpi = 24;

    /// <summary>
    /// Logical caption (ICON_SMALL) size at 96 DPI. Must track <em>this</em>
    /// window's DPI — SM_CXSMICON follows the primary monitor.
    /// </summary>
    private const int CaptionIconPxAt96Dpi = 16;

    /// <summary>
    /// Tab-strip item height at 96 DPI. Native commctl height is 20; +1 keeps
    /// the Settings "g" descender inside the tab at 200%+. Width stays 0
    /// (text-sized). Re-applied after DPI change — ItemSize is not AutoScaled.
    /// </summary>
    private const int TabStripItemHeightDesign = 21;

    /// <summary>
    /// Horizontal tab-label padding at 96 DPI. Commctl default is 6; 10 gives
    /// the longer labels trailing slack. Vertical stays 3.
    /// </summary>
    private const int TabStripPaddingXDesign = 10;

    private const int TabStripPaddingYDesign = 3;

    /// <summary>
    /// TCM_SETPADDING — applied directly so we do not RecreateHandle
    /// (<see cref="TabControl.Padding"/> setter does).
    /// </summary>
    private const int TcmSetPadding = 0x132B;

    /// <summary>
    /// When false, <see cref="SetVisibleCore"/> forces the form hidden so
    /// "start minimized to tray" never flashes a visible window.
    /// </summary>
    private bool _isAppVisible = true;

    /// <summary>
    /// True while applying saved settings to checkboxes. CheckedChanged would
    /// otherwise persist on every assignment during startup.
    /// </summary>
    private bool _isInitializing = true;

    /// <summary>
    /// Start on Launch is evaluated once on first <see cref="OnHandleCreated"/>,
    /// not in <see cref="LoadState"/> (no HWND yet). A later handle recreate
    /// must not re-evaluate.
    /// </summary>
    private bool _startOnLaunchApplied;

    /// <summary>
    /// Distinguishes the first HWND from a later recreate so Start on Launch
    /// stays one-shot and post-recreate layout can run.
    /// </summary>
    private bool _windowHandleCreatedOnce;

    private readonly object _uiMarshalGate = new();
    private bool _pendingGraphInvalidation;
    private string? _pendingOutputDeviceName;

    /// <summary>
    /// MMDevice friendly-name probe (not <see cref="_uiMarshalGate"/> — completions
    /// must not contend with keep-alive / graph marshal flags).
    /// </summary>
    private readonly object _outputDeviceNameGate = new();
    private string? _outputDeviceName;
    private int _outputDeviceNameGeneration;
    private bool _outputDeviceNameProbeBusy;
    private bool _outputDeviceNameProbeQueued;

    public MainForm(
        AppSettings settings,
        SettingsStore settingsStore,
        KeepAliveAudioService audioService,
        PlaybackGraphInvalidator graphInvalidator,
        DefaultAudioDeviceMonitor deviceMonitor,
        SettingsLoadStatus settingsLoadStatus = SettingsLoadStatus.Ok)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _audioService = audioService ?? throw new ArgumentNullException(nameof(audioService));
        _graphInvalidator = graphInvalidator ?? throw new ArgumentNullException(nameof(graphInvalidator));
        _deviceMonitor = deviceMonitor ?? throw new ArgumentNullException(nameof(deviceMonitor));
        _settingsLoadStatus = settingsLoadStatus;

        _iconRunningData = LoadEmbeddedResourceBytes("ZeroTone.bar-chart-green.ico");
        _iconUnprovenData = LoadEmbeddedResourceBytes("ZeroTone.bar-chart-amber.ico");
        _iconStoppedData = LoadEmbeddedResourceBytes("ZeroTone.bar-chart-red.ico");

        InitializeComponent();

        // Tray is Visible after InitializeComponent. Program never receives this
        // instance if we throw — hide the icon before the exception escapes.
        try
        {
            ApplyAudioOptionExplanations();
            ReloadIconsForCurrentDpi();

            ShowIcon = true;
            Icon = _windowIconStopped;
            MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedSingle;

            trayIcon.Text = $"{AppName} - {StoppedMessage}";
            trayIcon.Icon = _trayIconStopped;
            trayIcon.ContextMenuStrip = trayMenu;

            menuStartStop.Text = $"Start {AppName}";
            keepAliveStatusLabel.Text = StoppedMessage;

            // Invalidator before monitor.Start; audio/power before LoadState
            // so Start on Launch events are heard.
            Resize += MainForm_Resize;
            tabs.SelectedIndexChanged += Tabs_SelectedIndexChanged;

            _graphInvalidator.Invalidated += PlaybackGraph_Invalidated;
            try
            {
                _deviceMonitor.Start();
                _followDefaultDevice = true;
            }
            catch
            {
                // Follow is off until a later retry; keep-alive and resume still work.
                _followDefaultDevice = false;
            }

            _audioService.IsEngagedChanged += AudioService_IsEngagedChanged;
            _audioService.PhaseChanged += AudioService_PhaseChanged;
            _audioService.LastIssueChanged += AudioService_LastIssueChanged;

            SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;

            ApplySavedWindowPosition();
            LoadState();

            _isInitializing = false;
        }
        catch
        {
            DetachExternalEvents();
            TryHideTrayForExit();
            throw;
        }
    }

    /// <summary>
    /// Hover text for Audio Type / Pattern and Start with Windows. The same
    /// string is <see cref="Control.AccessibleDescription"/> for Narrator.
    /// </summary>
    private void ApplyAudioOptionExplanations()
    {
        mainTabToolTip.AutoPopDelay = MainTabToolTipAutoPopMs;

        SetAudioOptionExplanation(radioSilence, SilenceOptionTip);
        SetAudioOptionExplanation(radioInaudible, InaudibleOptionTip);
        SetAudioOptionExplanation(radioConstant, ConstantOptionTip);
        SetAudioOptionExplanation(radioPulsed, PulsedOptionTip);

        mainTabToolTip.SetToolTip(checkStartWithWindows, StartWithWindowsTip);
        checkStartWithWindows.AccessibleDescription = StartWithWindowsTip;
    }

    private void SetAudioOptionExplanation(RadioButton radio, string text)
    {
        mainTabToolTip.SetToolTip(radio, text);
        radio.AccessibleDescription = text;
    }

    /// <summary>
    /// After DPI/font autoscaling, place the status word just after the caption.
    /// Designer X leaves only a few pixels; at 125%+ the caption paints over
    /// the first letter of Running/Stopped.
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Device name is a thread-pool probe — do not block OnLoad on MMDevice.
        ApplyNativeWindowIcons(CurrentIconVisual());
        ScheduleApplyKeepAliveUi();
        ScheduleOutputDeviceNameRefresh();
        // Post-scale size is known. First show: every page HWND is at this DPI.
        ApplyScaledUiLayout();
        LayoutAllTabs();
    }

    /// <summary>
    /// WinForms may refresh window icons on first show; re-apply for a sharp taskbar.
    /// Also the right moment for a one-shot settings-reset warning when visible.
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyNativeWindowIcons(CurrentIconVisual());
        MaybeWarnSettingsReset();
    }

    /// <summary>
    /// Task Manager / Settings → Startup can flip StartupApproved while we are
    /// running. Resample when the user returns to this window.
    /// </summary>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        SyncStartWithWindowsCheckbox();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyNativeWindowIcons(CurrentIconVisual());
        ApplyTabStripItemSize();
        FlushPendingUiMarshals();
        MaybeApplyStartOnLaunch();

        // RecreateHandle (not the first create) can leave PerMonitorV2 bounds
        // stale relative to DeviceDpi. First create is laid out in OnLoad.
        if (_windowHandleCreatedOnce)
        {
            ReloadIconsForCurrentDpi();
            ApplyScaledUiLayout();
        }
        else
        {
            _windowHandleCreatedOnce = true;
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ReloadIconsForCurrentDpi();
        ScheduleApplyKeepAliveUi();
        UpdateOutputDeviceToolTip();
        // Do not remeasure here. Hidden-tab child HWNDs often still report the
        // previous monitor's DPI (PreferredSize / glyphs). AFTERPARENT and
        // TabControl's own work run before a posted invoke.
        // Re-stamp caption HICONs after this message: WinForms may SETICON
        // from SM_CXSMICON (primary DPI) after we return.
        PostToUi(ApplyScaledUiLayoutAfterDpi);
    }

    /// <summary>
    /// Prefer native-size handles for shell queries. Taskbar often uses GetIcon /
    /// ICON_BIG, which is the 32→24 blur source if left at SM_CXICON.
    /// When minimize-to-tray is on, handle SC_MINIMIZE before the window becomes
    /// iconic so hide/restore match close-to-tray (no SW_RESTORE taskbar animation).
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmGetIcon)
        {
            var handle = GetIconHandleForShell((int)m.WParam);
            if (handle != IntPtr.Zero)
            {
                m.Result = handle;
                return;
            }
        }

        if (m.Msg == WmSysCommand
            && (m.WParam.ToInt64() & 0xFFF0) == ScMinimize
            && _settings.MinimizeToSystemTray)
        {
            // Do not call base — that would minimize first (iconic / -32000 coords).
            MinimizeToTrayOrTaskbar();
            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>
    /// Main tab layout: equal top/bottom margins; status after caption (DPI-safe);
    /// status and device labels sized for AutoEllipsis.
    /// </summary>
    private void LayoutMainTabStatusLabels()
    {
        // Design-time constants at 96 DPI; convert so margins stay proportional.
        const int rowGapDesign = 12;
        const int labelValueGapDesign = 4;
        const int leftDesign = 8;
        const int rightMarginDesign = 8;
        const int minMarginDesign = 4;

        var rowGapPx = LogicalToDeviceUnits(rowGapDesign);
        var labelValueGapPx = LogicalToDeviceUnits(labelValueGapDesign);
        var leftPx = LogicalToDeviceUnits(leftDesign);
        var rightMarginPx = LogicalToDeviceUnits(rightMarginDesign);
        var minMarginPx = LogicalToDeviceUnits(minMarginDesign);

        // Unselected pages are not Scale()'d by TabControl on DPI change.
        var buttonPref = startStopButton.PreferredSize;
        if (buttonPref.Width > 0 && buttonPref.Height > 0)
        {
            startStopButton.Size = buttonPref;
        }

        // Prefer font-driven height when AutoSize is off (AutoEllipsis requires that).
        var statusLineHeight = Math.Max(
            keepAliveStatusLabel.PreferredHeight,
            TextRenderer.MeasureText("Ag", keepAliveStatusLabel.Font).Height);
        var deviceLineHeight = Math.Max(
            outputDeviceLabel.PreferredHeight,
            TextRenderer.MeasureText("Ag", outputDeviceLabel.Font).Height);

        var topRowHeight = Math.Max(
            startStopButton.Height,
            Math.Max(labelStatusCaption.Height, statusLineHeight));
        var blockHeight = topRowHeight + rowGapPx + deviceLineHeight;

        var clientHeight = tabMain.ClientSize.Height;
        var topMargin = Math.Max(minMarginPx, (clientHeight - blockHeight) / 2);

        startStopButton.Left = leftPx;
        startStopButton.Top = topMargin + (topRowHeight - startStopButton.Height) / 2;

        labelStatusCaption.Top = topMargin + (topRowHeight - labelStatusCaption.Height) / 2;
        labelStatusCaption.Left = startStopButton.Right + labelValueGapPx;

        var statusLeft = labelStatusCaption.Right + labelValueGapPx;
        var statusWidth = Math.Max(1, tabMain.ClientSize.Width - statusLeft - rightMarginPx);
        var statusTop = topMargin + (topRowHeight - statusLineHeight) / 2;
        keepAliveStatusLabel.SetBounds(statusLeft, statusTop, statusWidth, statusLineHeight);
        keepAliveStatusLabel.BringToFront();

        var deviceTop = topMargin + topRowHeight + rowGapPx;
        var deviceWidth = Math.Max(1, tabMain.ClientSize.Width - leftPx - rightMarginPx);
        outputDeviceLabel.SetBounds(leftPx, deviceTop, deviceWidth, deviceLineHeight);

        UpdateOutputDeviceToolTip();
    }

    /// <summary>
    /// Settings tab: three rows, two columns. Measures row height and splits
    /// leftover space into equal top, gaps, and bottom. Designer Y is approximate.
    /// </summary>
    private void LayoutSettingsTab()
    {
        const int leftDesign = 8;
        const int col2Design = 190;
        const int colGutterDesign = 8;
        const int rowCount = 3;

        var rowHeight = Math.Max(
            Math.Max(
                Math.Max(checkStartWithWindows.PreferredSize.Height, checkStartOnLaunch.PreferredSize.Height),
                checkMinimizeOnLaunch.PreferredSize.Height),
            Math.Max(checkMinimizeToTray.PreferredSize.Height, checkMinimizeOnClose.PreferredSize.Height));
        if (rowHeight <= 0)
        {
            rowHeight = LogicalToDeviceUnits(19);
        }

        var leftover = tabSettings.ClientSize.Height - (rowCount * rowHeight);
        if (leftover < 0)
        {
            leftover = 0;
        }

        // Four equal chrome slots (top, gap, gap, bottom). Remainder is unused
        // bottom slack so gaps stay equal.
        var unit = leftover / 4;

        var leftPx = LogicalToDeviceUnits(leftDesign);
        var col2Px = LogicalToDeviceUnits(col2Design);
        var leftColWidth = Math.Max(
            Math.Max(checkStartWithWindows.PreferredSize.Width, checkStartOnLaunch.PreferredSize.Width),
            checkMinimizeOnLaunch.PreferredSize.Width);
        col2Px = Math.Max(col2Px, leftPx + leftColWidth + LogicalToDeviceUnits(colGutterDesign));

        var y0 = unit;
        var y1 = y0 + rowHeight + unit;
        var y2 = y1 + rowHeight + unit;

        checkStartWithWindows.Location = new Point(leftPx, y0);
        checkMinimizeToTray.Location = new Point(col2Px, y0);
        checkStartOnLaunch.Location = new Point(leftPx, y1);
        checkMinimizeOnClose.Location = new Point(col2Px, y1);
        checkMinimizeOnLaunch.Location = new Point(leftPx, y2);
    }

    /// <summary>
    /// Audio Options: two equal group boxes. Scales 96-DPI margins, then
    /// splits each group's leftover display height into equal top, gap, and
    /// bottom. Designer Y is approximate.
    /// </summary>
    private void LayoutAudioOptionsTab()
    {
        const int marginDesign = 8;
        const int groupHeightDesign = 62;

        var marginPx = LogicalToDeviceUnits(marginDesign);
        var client = tabAudio.ClientSize;

        var innerWidth = client.Width - (3 * marginPx);
        if (innerWidth < 2)
        {
            innerWidth = 2;
        }

        var groupWidth = innerWidth / 2;
        var gutterPx = marginPx + (innerWidth % 2);

        var groupHeight = LogicalToDeviceUnits(groupHeightDesign);
        if (groupHeight > client.Height)
        {
            groupHeight = client.Height;
        }

        var topPx = (client.Height - groupHeight) / 2;

        groupAudioType.SetBounds(marginPx, topPx, groupWidth, groupHeight);
        groupPattern.SetBounds(marginPx + groupWidth + gutterPx, topPx, groupWidth, groupHeight);

        LayoutAudioOptionRadios(groupAudioType, radioSilence, radioInaudible);
        LayoutAudioOptionRadios(groupPattern, radioConstant, radioPulsed);
    }

    /// <summary>
    /// Places two radios in <paramref name="group"/> using the current
    /// <see cref="GroupBox.DisplayRectangle"/> (call after the group is sized).
    /// </summary>
    private void LayoutAudioOptionRadios(GroupBox group, RadioButton first, RadioButton second)
    {
        const int radioLeftDesign = 10;
        const int radioCount = 2;

        var display = group.DisplayRectangle;
        var rowHeight = Math.Max(first.PreferredSize.Height, second.PreferredSize.Height);
        if (rowHeight <= 0)
        {
            rowHeight = LogicalToDeviceUnits(19);
        }

        var leftover = display.Height - (radioCount * rowHeight);
        if (leftover < 0)
        {
            leftover = 0;
        }

        var unit = leftover / 3;
        var leftPx = Math.Max(display.X, LogicalToDeviceUnits(radioLeftDesign));
        var y0 = display.Y + unit;
        var y1 = y0 + rowHeight + unit;

        first.Location = new Point(leftPx, y0);
        second.Location = new Point(leftPx, y1);
    }

    /// <summary>
    /// Re-applies DPI-dependent chrome after first show, a DPI change,
    /// tray restore, or HWND recreate. Lays out the visible tab only —
    /// remasuring a hidden page stamps a stale PreferredSize.
    /// </summary>
    private void ApplyScaledUiLayout()
    {
        if (!IsHandleCreated || IsDisposed || Disposing)
        {
            return;
        }

        ApplyTabStripItemSize();
        // TabControl only assigns DisplayRectangle to the selected page.
        SyncHiddenTabPageBounds();
        EnsureWindowUsableOnScreen();
        LayoutVisibleTab();
    }

    /// <summary>
    /// Posted from <see cref="OnDpiChanged"/> so caption icons and tab layout
    /// run after WinForms / DWM finish the DPI message.
    /// </summary>
    private void ApplyScaledUiLayoutAfterDpi()
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            return;
        }

        ApplyNativeWindowIcons(CurrentIconVisual());
        ApplyScaledUiLayout();
    }

    private void LayoutAllTabs()
    {
        RefreshAutoSizeTree(tabMain);
        LayoutMainTabStatusLabels();
        RefreshAutoSizeTree(tabSettings);
        LayoutSettingsTab();
        RefreshAutoSizeTree(tabAudio);
        LayoutAudioOptionsTab();
    }

    private void LayoutVisibleTab()
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        var page = tabs.SelectedTab;
        if (page == tabSettings)
        {
            RefreshAutoSizeTree(tabSettings);
            LayoutSettingsTab();
        }
        else if (page == tabAudio)
        {
            RefreshAutoSizeTree(tabAudio);
            LayoutAudioOptionsTab();
        }
        else if (page == tabMain)
        {
            RefreshAutoSizeTree(tabMain);
            LayoutMainTabStatusLabels();
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> after the current message (so child
    /// WM_DPICHANGED_AFTERPARENT and TabControl page-show can update HWND DPI
    /// before we read PreferredSize).
    /// </summary>
    private void PostToUi(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Gives every tab page the current display rectangle. Call after strip
    /// height / DPI so hidden pages have a current-DPI ClientSize.
    /// </summary>
    private void SyncHiddenTabPageBounds()
    {
        if (!tabs.IsHandleCreated)
        {
            return;
        }

        var display = tabs.DisplayRectangle;
        foreach (TabPage page in tabs.TabPages)
        {
            if (page.Bounds != display)
            {
                page.Bounds = display;
            }
        }
    }

    /// <summary>
    /// AutoSize checkboxes/radios keep the previous DPI's Size when their tab
    /// was hidden during WM_DPICHANGED. Call only when the page is visible
    /// (or at first show) — PreferredSize on a hidden page follows the child
    /// HWND DPI, which lags.
    /// </summary>
    private static void RefreshAutoSizeTree(Control root)
    {
        foreach (Control child in root.Controls)
        {
            RefreshAutoSizeTree(child);
            if (!child.AutoSize)
            {
                continue;
            }

            var preferred = child.PreferredSize;
            if (preferred.Width > 0 && preferred.Height > 0)
            {
                child.Size = preferred;
            }
        }
    }

    /// <summary>
    /// Sets tab-strip height and label padding at the current DPI. Width 0
    /// keeps SizeMode.Normal (each tab sized to its text). Padding is sent
    /// with TCM_SETPADDING so the handle is not recreated.
    /// </summary>
    private void ApplyTabStripItemSize()
    {
        var height = LogicalToDeviceUnits(TabStripItemHeightDesign);
        if (height < 1)
        {
            height = 1;
        }

        ApplyTabStripPadding();

        if (tabs.ItemSize.Width == 0 && tabs.ItemSize.Height == height)
        {
            return;
        }

        tabs.ItemSize = new Size(0, height);
    }

    /// <summary>
    /// Applies <see cref="TabStripPaddingXDesign"/> / Y in device pixels.
    /// Does not use <see cref="TabControl.Padding"/> (that RecreateHandles).
    /// </summary>
    private void ApplyTabStripPadding()
    {
        if (!tabs.IsHandleCreated)
        {
            return;
        }

        var x = Math.Max(0, LogicalToDeviceUnits(TabStripPaddingXDesign));
        var y = Math.Max(0, LogicalToDeviceUnits(TabStripPaddingYDesign));
        SendMessage(
            tabs.Handle,
            TcmSetPadding,
            IntPtr.Zero,
            (IntPtr)((y << 16) | (x & 0xFFFF)));
    }

    /// <summary>
    /// Applies an already-read Multimedia default name. UI thread + HWND only.
    /// Does not call MMDevice.
    /// </summary>
    private void UpdateOutputDeviceLabel(string name)
    {
        var text = $"Output Device:  {name}";
        var changed = !string.Equals(_outputDeviceName, name, StringComparison.Ordinal)
            || outputDeviceLabel.Text != text;
        outputDeviceLabel.Text = text;
        _outputDeviceName = name;
        if (changed)
        {
            LayoutMainTabStatusLabels();
        }
        else
        {
            UpdateOutputDeviceToolTip();
        }
    }

    /// <summary>
    /// Full name when truncated, and/or a note when follow-default failed to start.
    /// </summary>
    private void UpdateOutputDeviceToolTip()
    {
        var text = outputDeviceLabel.Text;
        if (string.IsNullOrEmpty(text) || outputDeviceLabel.Width <= 0)
        {
            mainTabToolTip.SetToolTip(
                outputDeviceLabel,
                _followDefaultDevice ? string.Empty : FollowDeviceDisabledTooltip);
            return;
        }

        var preferred = TextRenderer.MeasureText(
            text,
            outputDeviceLabel.Font,
            Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

        var truncated = preferred.Width > outputDeviceLabel.ClientSize.Width;
        string tip;
        if (truncated && !_followDefaultDevice)
        {
            tip = text + Environment.NewLine + FollowDeviceDisabledTooltip;
        }
        else if (truncated)
        {
            tip = text;
        }
        else if (!_followDefaultDevice)
        {
            tip = FollowDeviceDisabledTooltip;
        }
        else
        {
            tip = string.Empty;
        }

        mainTabToolTip.SetToolTip(outputDeviceLabel, tip);
    }

    /// <summary>
    /// Requests a thread-pool MMDevice friendly-name read. Single in-flight
    /// probe plus one queued follow-up; completions are generation-tagged.
    /// Safe from any thread. Does not block.
    /// </summary>
    private void ScheduleOutputDeviceNameRefresh()
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        int generation;
        lock (_outputDeviceNameGate)
        {
            if (_outputDeviceNameProbeBusy)
            {
                _outputDeviceNameProbeQueued = true;
                return;
            }

            _outputDeviceNameProbeBusy = true;
            generation = ++_outputDeviceNameGeneration;
        }

        ThreadPool.QueueUserWorkItem(ProbeOutputDeviceName, generation);
    }

    private void ProbeOutputDeviceName(object? state)
    {
        var generation = (int)state!;
        string name;
        try
        {
            name = DefaultPlaybackDeviceName.GetFriendlyNameOrFallback();
        }
        catch
        {
            name = "(unavailable)";
        }

        bool apply = false;
        var startNext = false;
        var nextGeneration = 0;

        lock (_outputDeviceNameGate)
        {
            if (generation != _outputDeviceNameGeneration || _outputDeviceNameProbeQueued)
            {
                if (_outputDeviceNameProbeQueued)
                {
                    _outputDeviceNameProbeQueued = false;
                    nextGeneration = ++_outputDeviceNameGeneration;
                    startNext = true;
                }
                else
                {
                    _outputDeviceNameProbeBusy = false;
                }
            }
            else
            {
                _outputDeviceNameProbeBusy = false;
                apply = true;
            }
        }

        if (startNext)
        {
            ThreadPool.QueueUserWorkItem(ProbeOutputDeviceName, nextGeneration);
            return;
        }

        if (apply)
        {
            MarshalOutputDeviceName(name);
        }
    }

    /// <summary>
    /// Marshals a probed name onto the UI thread. Without an HWND,
    /// <see cref="Control.InvokeRequired"/> is false — pend until
    /// <see cref="OnHandleCreated"/> instead of touching WinForms on the pool.
    /// </summary>
    private void MarshalOutputDeviceName(string name)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (TryPendOutputDeviceNameIfNoHandle(name))
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(ApplyOutputDeviceNameFromProbe, name);
            }
            catch (ObjectDisposedException)
            {
                // Form closed between the check and BeginInvoke.
            }
            catch (InvalidOperationException)
            {
                TryPendOutputDeviceNameIfNoHandle(name);
            }

            return;
        }

        if (TryPendOutputDeviceNameIfNoHandle(name))
        {
            return;
        }

        ApplyOutputDeviceNameFromProbe(name);
    }

    private void ApplyOutputDeviceNameFromProbe(string name)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        UpdateOutputDeviceLabel(name);
    }

    private bool TryPendOutputDeviceNameIfNoHandle(string name)
    {
        lock (_uiMarshalGate)
        {
            if (IsHandleCreated)
            {
                return false;
            }

            _pendingOutputDeviceName = name;
            return true;
        }
    }

    /// <summary>
    /// UI thread only. Retries COM device-follow registration after a ctor miss.
    /// <see cref="DefaultAudioDeviceMonitor.Start"/> is registration only.
    /// </summary>
    private void TryStartDeviceFollow()
    {
        if (_followDefaultDevice || IsDisposed || Disposing)
        {
            return;
        }

        try
        {
            _deviceMonitor.Start();
            _followDefaultDevice = true;
            UpdateOutputDeviceToolTip();
        }
        catch
        {
            // Follow still unavailable; keep-alive and resume continue.
        }
    }

    /// <summary>
    /// Keeps the form invisible when tray-only, including first show (avoids flash).
    /// Still CreateHandle() so the form has a HWND for message processing.
    /// </summary>
    protected override void SetVisibleCore(bool value)
    {
        if (!_isAppVisible)
        {
            value = false;
            if (!IsHandleCreated)
            {
                CreateHandle();
            }
        }

        base.SetVisibleCore(value);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        DetachExternalEvents();

        if (_graphInvalidationRetryTimer is not null)
        {
            _graphInvalidationRetryTimer.Stop();
            _graphInvalidationRetryTimer.Dispose();
            _graphInvalidationRetryTimer = null;
        }

        if (_amberPhaseUiCoalesceTimer is not null)
        {
            _amberPhaseUiCoalesceTimer.Stop();
            _amberPhaseUiCoalesceTimer.Dispose();
            _amberPhaseUiCoalesceTimer = null;
        }

        CrashExit.UnregisterForm();

        // Program owns audio / invalidator / device monitor.
        DisposeAllIcons();

        base.OnFormClosed(e);
    }

    /// <summary>
    /// Drops form-owned handlers on process-owned services and static SystemEvents.
    /// Safe if a given += never ran (-= of an unsubscribed handler is a no-op).
    /// Used from <see cref="OnFormClosed"/> and ctor failure after the first +=.
    /// </summary>
    private void DetachExternalEvents()
    {
        Resize -= MainForm_Resize;
        tabs.SelectedIndexChanged -= Tabs_SelectedIndexChanged;
        SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
        _graphInvalidator.Invalidated -= PlaybackGraph_Invalidated;
        _audioService.IsEngagedChanged -= AudioService_IsEngagedChanged;
        _audioService.PhaseChanged -= AudioService_PhaseChanged;
        _audioService.LastIssueChanged -= AudioService_LastIssueChanged;
    }

    /// <summary>
    /// Debounced graph invalidation (device follow and/or power resume).
    /// Retries follow registration if needed, then refreshes the device name
    /// and re-arms keep-alive if desired. Resume re-arm does not wait on
    /// follow retry or the name probe.
    /// </summary>
    private void PlaybackGraph_Invalidated(object? sender, EventArgs e)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        // InvokeRequired is false without an HWND — do not run layout/timers
        // on the caller. Re-check after InvokeRequired: a handle recreate
        // can destroy the handle between the first check and this thread's use.
        if (TryPendGraphInvalidationIfNoHandle())
        {
            return;
        }

        // Timer / COM callbacks are not the UI thread.
        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(PlaybackGraph_Invalidated, sender, e);
            }
            catch (ObjectDisposedException)
            {
                // Form closed between the check and BeginInvoke.
            }
            catch (InvalidOperationException)
            {
                TryPendGraphInvalidationIfNoHandle();
            }

            return;
        }

        if (TryPendGraphInvalidationIfNoHandle())
        {
            return;
        }

        if (!_followDefaultDevice)
        {
            TryStartDeviceFollow();
        }

        ScheduleOutputDeviceNameRefresh();
        LayoutMainTabStatusLabels();
        RestartKeepAliveAfterGraphInvalidation();
    }

    private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
        {
            return;
        }

        _graphInvalidator.Signal();
    }

    /// <summary>
    /// Request a WASAPI session replace when keep-alive is still desired.
    /// Non-blocking; a second attempt after 1.5s helps slow Bluetooth handoffs.
    /// </summary>
    private void RestartKeepAliveAfterGraphInvalidation()
    {
        try
        {
            if (!_audioService.RestartIfEngaged(_settings.AudioType, _settings.AudioPattern))
            {
                return;
            }
        }
        catch
        {
            // Device may not be ready; retry timer may recover.
        }

        ScheduleApplyKeepAliveUi();
        ScheduleGraphInvalidationRetry();
    }

    private void AudioService_IsEngagedChanged(object? sender, EventArgs e)
    {
        MarshalKeepAliveUi();
    }

    private void AudioService_PhaseChanged(object? sender, EventArgs e)
    {
        MarshalKeepAliveUi();
    }

    private void AudioService_LastIssueChanged(object? sender, EventArgs e)
    {
        MarshalKeepAliveUi();
    }

    /// <summary>
    /// Marshals keep-alive chrome apply onto the UI thread. Without an HWND,
    /// <see cref="Control.InvokeRequired"/> is false — queue and flush on
    /// <see cref="OnHandleCreated"/> instead of running WinForms on the caller.
    /// </summary>
    private void MarshalKeepAliveUi()
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (TryPendKeepAliveUiIfNoHandle())
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(ScheduleApplyKeepAliveUi);
            }
            catch (ObjectDisposedException)
            {
                // Form closed between the check and BeginInvoke.
            }
            catch (InvalidOperationException)
            {
                TryPendKeepAliveUiIfNoHandle();
            }

            return;
        }

        if (TryPendKeepAliveUiIfNoHandle())
        {
            return;
        }

        ScheduleApplyKeepAliveUi();
    }

    private bool TryPendKeepAliveUiIfNoHandle()
    {
        lock (_uiMarshalGate)
        {
            if (IsHandleCreated)
            {
                return false;
            }

            return true;
        }
    }

    private bool TryPendGraphInvalidationIfNoHandle()
    {
        lock (_uiMarshalGate)
        {
            if (IsHandleCreated)
            {
                return false;
            }

            _pendingGraphInvalidation = true;
            return true;
        }
    }

    /// <summary>
    /// Runs marshals queued before the HWND existed. Graph first, then
    /// keep-alive chrome (always — a graph re-arm must not drop a deferred
    /// status sample), then any completed name probe.
    /// </summary>
    private void FlushPendingUiMarshals()
    {
        bool applyGraph;
        string? pendingName;
        lock (_uiMarshalGate)
        {
            applyGraph = _pendingGraphInvalidation;
            pendingName = _pendingOutputDeviceName;
            _pendingGraphInvalidation = false;
            _pendingOutputDeviceName = null;
        }

        if (applyGraph)
        {
            PlaybackGraph_Invalidated(null, EventArgs.Empty);
        }

        if (!IsDisposed && !Disposing)
        {
            ScheduleApplyKeepAliveUi();
        }

        if (pendingName is not null && !IsDisposed && !Disposing)
        {
            UpdateOutputDeviceLabel(pendingName);
        }
    }

    /// <summary>
    /// Evaluate Start on Launch once the form has an HWND. Once per lifetime:
    /// mark consumed even when the preference is off so a later handle recreate
    /// cannot start keep-alive.
    /// </summary>
    private void MaybeApplyStartOnLaunch()
    {
        if (_startOnLaunchApplied || IsDisposed || Disposing)
        {
            return;
        }

        _startOnLaunchApplied = true;
        if (_settings.StartOnLaunch)
        {
            RequestStartKeepAlive();
        }
    }

    /// <summary>
    /// Arms (or re-arms) a single 1.5s retry so rapid graph-invalidation events do not
    /// create many overlapping timers.
    /// </summary>
    private void ScheduleGraphInvalidationRetry()
    {
        if (IsDisposed)
        {
            return;
        }

        if (_graphInvalidationRetryTimer is null)
        {
            _graphInvalidationRetryTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            _graphInvalidationRetryTimer.Tick += GraphInvalidationRetryTimer_Tick;
        }

        _graphInvalidationRetryTimer.Stop();
        _graphInvalidationRetryTimer.Start();
    }

    private void GraphInvalidationRetryTimer_Tick(object? sender, EventArgs e)
    {
        _graphInvalidationRetryTimer?.Stop();

        if (IsDisposed)
        {
            return;
        }

        try
        {
            if (!_audioService.RestartIfEngaged(_settings.AudioType, _settings.AudioPattern))
            {
                return;
            }
        }
        catch
        {
            // Leave state as-is; user can Stop/Start manually.
        }

        ScheduleApplyKeepAliveUi();
    }

    private void LoadState()
    {
        // _isInitializing blocks Save and restart from CheckedChanged.
        checkMinimizeOnLaunch.Checked = _settings.MinimizeOnLaunch;
        checkStartOnLaunch.Checked = _settings.StartOnLaunch;
        checkMinimizeToTray.Checked = _settings.MinimizeToSystemTray;
        checkMinimizeOnClose.Checked = _settings.MinimizeOnClose;
        checkStartWithWindows.Checked = WindowsStartupRegistration.IsEnabled();

        radioInaudible.Checked = _settings.AudioType == AudioType.Inaudible;
        radioSilence.Checked = _settings.AudioType == AudioType.Silence;
        radioConstant.Checked = _settings.AudioPattern == AudioPattern.Constant;
        radioPulsed.Checked = _settings.AudioPattern == AudioPattern.Pulsed;

        if (_settings.MinimizeOnLaunch)
        {
            MinimizeToTrayOrTaskbar();
        }
    }

    /// <summary>
    /// Copies live HKCU Run / StartupApproved into the checkbox without writing.
    /// </summary>
    private void SyncStartWithWindowsCheckbox()
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        var enabled = WindowsStartupRegistration.IsEnabled();
        if (checkStartWithWindows.Checked == enabled)
        {
            return;
        }

        _suppressStartWithWindowsHandler = true;
        try
        {
            checkStartWithWindows.Checked = enabled;
        }
        finally
        {
            _suppressStartWithWindowsHandler = false;
        }
    }

    private void Tabs_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (tabs.SelectedTab == tabSettings)
        {
            SyncStartWithWindowsCheckbox();
        }

        // Immediate layout now that the page is visible; posted pass waits
        // for child HWND DPI after a monitor change.
        LayoutVisibleTab();
        PostToUi(LayoutVisibleTab);
    }

    /// <summary>
    /// Writes settings to disk unless still initializing. One-shot warning on failure.
    /// </summary>
    private void PersistSettings()
    {
        if (_isInitializing)
        {
            return;
        }

        if (_settingsStore.Save(_settings))
        {
            _settingsSaveFailureNotified = false;
            return;
        }

        if (_settingsSaveFailureNotified || IsDisposed)
        {
            return;
        }

        _settingsSaveFailureNotified = true;
        MessageBox.Show(
            this,
            "Could not save preferences to:" + Environment.NewLine +
            _settingsStore.SettingsPath + Environment.NewLine + Environment.NewLine +
            "Your changes may not be remembered after exit.",
            AppName,
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    /// <summary>
    /// Once per process: warn if settings.json was corrupt. Deferred until the
    /// window is user-visible so start-minimized (tray or taskbar) is not modal.
    /// </summary>
    private void MaybeWarnSettingsReset()
    {
        if (_settingsResetWarningShown
            || _settingsLoadStatus != SettingsLoadStatus.ResetToDefaults
            || IsDisposed)
        {
            return;
        }

        // Tray-hidden or taskbar-minimized: wait until the user restores the window.
        if (!_isAppVisible
            || !Visible
            || WindowState == FormWindowState.Minimized
            || (IsHandleCreated && IsIconic(Handle)))
        {
            return;
        }

        _settingsResetWarningShown = true;

        var bakHint = File.Exists(_settingsStore.SettingsBackupPath)
            ? Environment.NewLine + Environment.NewLine +
              "A backup of the previous file was saved as:" + Environment.NewLine +
              _settingsStore.SettingsBackupPath
            : string.Empty;

        MessageBox.Show(
            this,
            "Preferences could not be read and were reset to defaults." + Environment.NewLine +
            Environment.NewLine +
            "File:" + Environment.NewLine +
            _settingsStore.SettingsPath +
            bakHint,
            AppName,
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    /// <summary>
    /// Applies saved window position if present. Placement only — visibility is
    /// decided after AutoScale by <see cref="EnsureWindowUsableOnScreen"/>.
    /// Also used on tray restore so a handle recreate cannot drop the monitor.
    /// </summary>
    private void ApplySavedWindowPosition()
    {
        if (_settings.WindowLeft is not int left || _settings.WindowTop is not int top)
        {
            return;
        }

        StartPosition = FormStartPosition.Manual;
        Location = new Point(left, top);
    }

    /// <summary>
    /// Keeps the window usable after scale / DPI change. UI thread, post-HWND.
    /// Entire window on a working area when it fits; otherwise pin so the
    /// caption stays on-screen. No-op while iconic (RestoreBounds is get-only).
    /// </summary>
    private void EnsureWindowUsableOnScreen()
    {
        if (!IsHandleCreated || IsDisposed)
        {
            return;
        }

        if (WindowState != FormWindowState.Normal || IsIconic(Handle))
        {
            return;
        }

        var bounds = Bounds;
        if (IsRectangleFullyOnAWorkingArea(bounds))
        {
            return;
        }

        var clamped = ClampRectangleToVisibleScreen(bounds);
        if (clamped.Location != bounds.Location)
        {
            Location = clamped.Location;
        }
    }

    /// <summary>
    /// Records window location into settings. No-ops during construction so
    /// LoadState's minimize-to-tray path does not overwrite a just-loaded position.
    /// </summary>
    private void SaveWindowPosition()
    {
        if (_isInitializing)
        {
            return;
        }

        if (!TryCaptureWindowPosition())
        {
            return;
        }

        PersistSettings();
    }

    /// <summary>
    /// Captures the last normal on-screen position. Caption Minimize sets
    /// WindowState before Resize runs, so Left/Top can be bogus (e.g. -32000);
    /// use RestoreBounds in that case. Close-to-tray still has Normal state.
    /// </summary>
    private bool TryCaptureWindowPosition()
    {
        if (!IsHandleCreated)
        {
            return false;
        }

        int left;
        int top;

        if (WindowState == FormWindowState.Normal)
        {
            left = Left;
            top = Top;
        }
        else
        {
            var restored = RestoreBounds;
            if (restored.Width <= 0 || restored.Height <= 0)
            {
                return false;
            }

            left = restored.Left;
            top = restored.Top;
        }

        // Reject still-invalid coordinates (classic minimized ghost values).
        if (left < -10_000 || top < -10_000)
        {
            return false;
        }

        _settings.WindowLeft = left;
        _settings.WindowTop = top;
        return true;
    }

    private static bool IsRectangleFullyOnAWorkingArea(Rectangle bounds)
    {
        foreach (var screen in Screen.AllScreens)
        {
            if (screen.WorkingArea.Contains(bounds))
            {
                return true;
            }
        }

        return false;
    }

    private static Rectangle ClampRectangleToVisibleScreen(Rectangle bounds)
    {
        var center = new Point(
            bounds.Left + bounds.Width / 2,
            bounds.Top + bounds.Height / 2);
        var working = Screen.FromPoint(center).WorkingArea;

        var width = Math.Min(bounds.Width, working.Width);
        var height = Math.Min(bounds.Height, working.Height);

        var x = bounds.Left;
        var y = bounds.Top;

        if (x + width > working.Right)
        {
            x = working.Right - width;
        }

        if (y + height > working.Bottom)
        {
            y = working.Bottom - height;
        }

        if (x < working.Left)
        {
            x = working.Left;
        }

        if (y < working.Top)
        {
            y = working.Top;
        }

        return new Rectangle(x, y, width, height);
    }

    /// <summary>
    /// Executes the Start/Stop command the chrome is advertising
    /// (<see cref="_uiAppliedIsEngaged"/>), not a fresh sample of
    /// <see cref="KeepAliveAudioService.IsEngaged"/>.
    /// </summary>
    private void HandleStartStopClick()
    {
        if (_uiAppliedIsEngaged)
        {
            RequestStopKeepAlive();
        }
        else
        {
            RequestStartKeepAlive();
        }
    }

    /// <summary>
    /// Absolute start: engage keep-alive with current audio settings.
    /// Start/Stop return immediately; WASAPI tear-down runs on the audio control thread.
    /// </summary>
    private void RequestStartKeepAlive()
    {
        _audioService.Start(_settings.AudioType, _settings.AudioPattern);
        ScheduleApplyKeepAliveUi();
    }

    /// <summary>
    /// Absolute stop: revoke keep-alive desire (idempotent if already stopped).
    /// Start/Stop return immediately; WASAPI tear-down runs on the audio control thread.
    /// </summary>
    private void RequestStopKeepAlive()
    {
        _audioService.Stop();
        CancelAmberPhaseUiCoalesce();
        ApplyKeepAliveUi();
    }

    /// <summary>
    /// Request a session replace when keep-alive is desired. Non-blocking.
    /// No-op during <see cref="LoadState"/>.
    /// </summary>
    private void RestartAudioIfEngaged()
    {
        if (_isInitializing)
        {
            return;
        }

        if (!_audioService.RestartIfEngaged(_settings.AudioType, _settings.AudioPattern))
        {
            return;
        }

        ScheduleApplyKeepAliveUi();
    }

    /// <summary>
    /// Engagement chrome immediately; amber phase chrome (Starting / Reconnecting)
    /// coalesced (~120 ms) so a fast open can paint Running without an amber flash.
    /// Other phases apply now.
    /// </summary>
    private void ScheduleApplyKeepAliveUi()
    {
        if (IsDisposed)
        {
            return;
        }

        ApplyEngagementChrome();

        var isEngaged = _audioService.IsEngaged;
        var phase = _audioService.Phase;

        if (isEngaged
            && phase is KeepAlivePhase.Starting or KeepAlivePhase.Reconnecting)
        {
            ArmAmberPhaseUiCoalesce();
            return;
        }

        CancelAmberPhaseUiCoalesce();
        ApplyKeepAliveUi();
    }

    /// <summary>
    /// Updates Start/Stop button and menu from live engagement only (no phase/icons).
    /// </summary>
    private void ApplyEngagementChrome()
    {
        var isEngaged = _audioService.IsEngaged;
        if (isEngaged == _uiAppliedIsEngaged)
        {
            return;
        }

        _uiAppliedIsEngaged = isEngaged;
        startStopButton.Text = isEngaged ? "Stop" : "Start";
        menuStartStop.Text = isEngaged ? $"Stop {AppName}" : $"Start {AppName}";
    }

    private void ArmAmberPhaseUiCoalesce()
    {
        if (IsDisposed)
        {
            return;
        }

        if (_amberPhaseUiCoalesceTimer is null)
        {
            _amberPhaseUiCoalesceTimer = new System.Windows.Forms.Timer
            {
                Interval = AmberPhaseUiCoalesceDelayMs,
            };
            _amberPhaseUiCoalesceTimer.Tick += AmberPhaseUiCoalesceTimer_Tick;
        }

        _amberPhaseUiCoalesceTimer.Stop();
        _amberPhaseUiCoalesceTimer.Start();
    }

    private void CancelAmberPhaseUiCoalesce()
    {
        _amberPhaseUiCoalesceTimer?.Stop();
    }

    private void AmberPhaseUiCoalesceTimer_Tick(object? sender, EventArgs e)
    {
        CancelAmberPhaseUiCoalesce();
        if (IsDisposed)
        {
            return;
        }

        ApplyKeepAliveUi();
    }

    private void ApplyKeepAliveUi()
    {
        var isEngaged = _audioService.IsEngaged;
        var phase = _audioService.Phase;
        var issue = _audioService.LastIssue;

        var status = phase switch
        {
            KeepAlivePhase.Starting when isEngaged => StartingMessage,
            KeepAlivePhase.Reconnecting when isEngaged => ReconnectingMessage,
            KeepAlivePhase.Running when isEngaged => RunningMessage,
            _ => isEngaged ? StartingMessage : StoppedMessage,
        };

        // One-shot follow retry after the first proven stream if ctor Start failed.
        if (isEngaged
            && phase == KeepAlivePhase.Running
            && !_followDefaultDevice
            && !_deviceFollowRetryOnRunningAttempted)
        {
            _deviceFollowRetryOnRunningAttempted = true;
            TryStartDeviceFollow();
        }

        var displayStatus = FormatKeepAliveStatusDisplay(status, issue);
        var visual = ResolveIconVisual(isEngaged, phase);
        var statusTip = BuildKeepAliveStatusToolTip(phase, isEngaged, issue);

        if (isEngaged == _uiAppliedIsEngaged
            && phase == _uiAppliedPhase
            && displayStatus == _uiAppliedStatus
            && statusTip == _uiAppliedTooltip
            && visual == _uiAppliedVisual)
        {
            return;
        }

        var statusTextChanged = displayStatus != _uiAppliedStatus;
        var visualChanged = visual != _uiAppliedVisual;

        _uiAppliedIsEngaged = isEngaged;
        _uiAppliedPhase = phase;
        _uiAppliedStatus = displayStatus;
        _uiAppliedTooltip = statusTip;
        _uiAppliedVisual = visual;

        trayIcon.Text = $"{AppName} - {displayStatus}";
        keepAliveStatusLabel.Text = displayStatus;
        startStopButton.Text = isEngaged ? "Stop" : "Start";
        menuStartStop.Text = isEngaged ? $"Stop {AppName}" : $"Start {AppName}";
        menuStatus.Text = BuildTrayMenuStatusText(status, displayStatus, issue);

        mainTabToolTip.SetToolTip(keepAliveStatusLabel, statusTip);

        if (visualChanged)
        {
            trayIcon.Icon = visual switch
            {
                KeepAliveIconVisual.Running => _trayIconRunning,
                KeepAliveIconVisual.Unproven => _trayIconUnproven,
                _ => _trayIconStopped,
            };
            Icon = visual switch
            {
                KeepAliveIconVisual.Running => _windowIconRunning,
                KeepAliveIconVisual.Unproven => _windowIconUnproven,
                _ => _windowIconStopped,
            };
            ApplyNativeWindowIcons(visual);
        }

        if (statusTextChanged)
        {
            LayoutMainTabStatusLabels();
        }
    }

    private static KeepAliveIconVisual ResolveIconVisual(bool isEngaged, KeepAlivePhase phase)
    {
        if (!isEngaged)
        {
            return KeepAliveIconVisual.Stopped;
        }

        return phase is KeepAlivePhase.Starting or KeepAlivePhase.Reconnecting
            ? KeepAliveIconVisual.Unproven
            : KeepAliveIconVisual.Running;
    }

    private static string BuildKeepAliveStatusToolTip(
        KeepAlivePhase phase,
        bool isEngaged,
        KeepAliveStatusDetail? issue)
    {
        if (phase == KeepAlivePhase.Running && isEngaged && issue is null)
        {
            return string.Empty;
        }

        if (issue is not null)
        {
            return issue.ToTooltipText();
        }

        if (phase == KeepAlivePhase.Starting && isEngaged)
        {
            return "Opening default playback device...";
        }

        if (phase == KeepAlivePhase.Reconnecting && isEngaged)
        {
            return "Re-establishing playback...";
        }

        return string.Empty;
    }

    /// <summary>
    /// Status phrase for the main label, tray icon, and tray menu.
    /// Previous-session closing uses the full sentence unless already Running
    /// (then a short suffix). Running mute/zero and Stopped faults use short suffixes.
    /// </summary>
    private static string FormatKeepAliveStatusDisplay(string status, KeepAliveStatusDetail? issue)
    {
        if (issue is not null
            && KeepAliveIssueMapper.IsPreviousSessionClosing(issue.Kind))
        {
            if (status != RunningMessage)
            {
                return issue.Message;
            }

            return $"{RunningMessage} {PreviousSessionClosingRunningSuffix}";
        }

        if (status == RunningMessage
            && TryGetRunningAttenuationSuffix(issue) is { } suffix)
        {
            return $"{status} {suffix}";
        }

        if (status == StoppedMessage
            && TryGetStoppedFaultSuffix(issue) is { } stoppedSuffix)
        {
            return $"{status} {stoppedSuffix}";
        }

        return status;
    }

    /// <summary>
    /// Disabled tray-menu line. Reconnecting open/stream failures may append a short reason.
    /// </summary>
    private static string BuildTrayMenuStatusText(
        string status,
        string displayStatus,
        KeepAliveStatusDetail? issue)
    {
        if (issue is null
            || status == StoppedMessage
            || status == StartingMessage
            || status == RunningMessage
            || KeepAliveIssueMapper.IsPreviousSessionClosing(issue.Kind))
        {
            return $"Status: {displayStatus}";
        }

        var reason = issue.Message;
        if (reason.Length > 40)
        {
            reason = reason[..37] + "...";
        }

        return $"Status: {displayStatus} — {reason}";
    }

    /// <summary>
    /// NotifyIcon-safe suffix while Running during a dual re-arm drain (full sentence on tooltip).
    /// </summary>
    private const string PreviousSessionClosingRunningSuffix = "(previous session closing)";

    /// <summary>
    /// Short suffix for mixer mute / zero volume while Running.
    /// </summary>
    private static string? TryGetRunningAttenuationSuffix(KeepAliveStatusDetail? issue)
    {
        if (issue is null)
        {
            return null;
        }

        return issue.Kind switch
        {
            KeepAliveIssueKind.SessionMuted => "(But muted in volume mixer)",
            KeepAliveIssueKind.SessionVolumeZero => "(But zero in volume mixer)",
            _ => null,
        };
    }

    /// <summary>
    /// Short suffix while Stopped with a retained non-ambient LastIssue.
    /// Full text stays on the main status tooltip.
    /// </summary>
    private static string? TryGetStoppedFaultSuffix(KeepAliveStatusDetail? issue)
    {
        if (issue is null
            || KeepAliveIssueMapper.IsSessionAttenuation(issue.Kind)
            || KeepAliveIssueMapper.IsPreviousSessionClosing(issue.Kind)
            || issue.Kind == KeepAliveIssueKind.None)
        {
            return null;
        }

        return issue.Kind switch
        {
            KeepAliveIssueKind.NoPlaybackDevice => "(No playback device)",
            KeepAliveIssueKind.DeviceInUse => "(Device in use)",
            KeepAliveIssueKind.ExclusiveMode => "(Exclusive mode)",
            KeepAliveIssueKind.UnsupportedFormat => "(Unsupported format)",
            KeepAliveIssueKind.EndpointUnavailable => "(Endpoint unavailable)",
            KeepAliveIssueKind.StreamLost => "(Stream interrupted)",
            KeepAliveIssueKind.UnexpectedError => "(Unexpected error)",
            KeepAliveIssueKind.Unknown => "(Could not start)",
            _ => null,
        };
    }

    private void MinimizeToTrayOrTaskbar()
    {
        SaveWindowPosition();

        trayIcon.Visible = true;

        if (_settings.MinimizeToSystemTray)
        {
            _isAppVisible = false;
            // Before the first HWND exists (start-minimized), omit WS_EX_APPWINDOW
            // so the shell does not flash a taskbar button. After a handle exists,
            // do not toggle ShowInTaskbar: that setter RecreateHandles, and
            // PerMonitorV2 then restores fonts at the target monitor DPI with
            // control bounds/glyphs still at the hide-time (or primary) DPI —
            // clipped labels and tiny checkbox/radio marks. A hidden Normal
            // window is already off the taskbar; Hide() is enough.
            if (!IsHandleCreated)
            {
                ShowInTaskbar = false;
            }

            Hide();
            // Stay Normal (SC_MINIMIZE is intercepted). Forcing Normal after a
            // real minimize leaves the HWND at iconic coordinates.
        }
        else
        {
            _isAppVisible = true;
            ShowInTaskbar = true;
            WindowState = FormWindowState.Minimized;
        }
    }

    /// <summary>
    /// Second process asked this instance to show the UI. Must run on the UI thread.
    /// </summary>
    public void ActivateFromSecondInstance()
    {
        if (IsDisposed)
        {
            return;
        }

        RestoreWindow();
    }

    private void RestoreWindow()
    {
        _isAppVisible = true;

        // Re-pin before any handle recreate so the new HWND is created on the
        // saved monitor (mixed-DPI: wrong monitor → wrong DeviceDpi).
        ApplySavedWindowPosition();

        // Only assign when false (start-minimized first show). Setting true
        // when already true is a no-op; false→true RecreateHandles.
        if (!ShowInTaskbar)
        {
            ShowInTaskbar = true;
        }

        if (WindowState == FormWindowState.Minimized || (IsHandleCreated && IsIconic(Handle)))
        {
            WindowState = FormWindowState.Normal;
            ShowWindow(Handle, SwRestore);
        }

        Show();
        BringToFront();
        Activate();
        ForceForeground();

        ApplyScaledUiLayout();
        MaybeWarnSettingsReset();
    }

    /// <summary>
    /// Requests foreground activation. Needed when restore is deferred by a timer
    /// (focus-stealing restrictions block plain Activate from timer callbacks).
    /// </summary>
    private void ForceForeground()
    {
        var handle = Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var foreground = GetForegroundWindow();
        if (foreground == handle)
        {
            return;
        }

        var foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        var currentThread = GetCurrentThreadId();

        if (foregroundThread != currentThread)
        {
            AttachThreadInput(foregroundThread, currentThread, true);
            try
            {
                SetForegroundWindow(handle);
            }
            finally
            {
                AttachThreadInput(foregroundThread, currentThread, false);
            }
        }
        else
        {
            SetForegroundWindow(handle);
        }

        // Briefly topmost if SetForegroundWindow is denied.
        if (GetForegroundWindow() != handle)
        {
            TopMost = true;
            TopMost = false;
            Activate();
        }
    }

    /// <summary>
    /// Stop keep-alive intent and hide the tray icon. WASAPI drain happens in
    /// Program.Main's finally. A visible icon after exit becomes a ghost until hovered.
    /// </summary>
    private void PrepareToExit()
    {
        _audioService.Stop();
        TryHideTrayForExit();
    }

    /// <summary>
    /// Best-effort tray shell teardown for clean exit and crash paths.
    /// Never throws. Safe if the form or icon is already disposed.
    /// </summary>
    internal void TryHideTrayForExit()
    {
        try
        {
            if (trayIcon is not null)
            {
                trayIcon.Visible = false;
                trayIcon.Icon = null;
            }
        }
        catch
        {
            // Crash / dispose races.
        }
    }

    private void MainForm_Resize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            MinimizeToTrayOrTaskbar();
            return;
        }

        // Taskbar restore skips RestoreWindow; clamp now (OnLoad may have been iconic).
        EnsureWindowUsableOnScreen();
        MaybeWarnSettingsReset();
    }

    private void startStopButton_Click(object? sender, EventArgs e) => HandleStartStopClick();

    /// <summary>
    /// Tray menu default action — same restore path as left single/double-click.
    /// </summary>
    private void menuShow_Click(object? sender, EventArgs e) => RestoreWindow();

    private void menuStartStop_Click(object? sender, EventArgs e) => HandleStartStopClick();

    /// <summary>
    /// Native MessageBox on purpose. USER32 MessageBox is system-DPI: after an
    /// in-session Display scale change it may look slightly soft until restart.
    /// Do not replace with a Form/TaskDialog for sharpness — see AGENTS.md.
    /// </summary>
    private void menuAbout_Click(object? sender, EventArgs e)
    {
        var version = AppVersion.GetDisplayString();
        MessageBox.Show(
            $"ZeroTone{Environment.NewLine}" +
            $"Version {version}{Environment.NewLine}{Environment.NewLine}" +
            $"Because the first second counts.{Environment.NewLine}{Environment.NewLine}" +
            "Keeps your default audio output awake with silence" + Environment.NewLine +
            "or near-silence (WASAPI shared mode)." + Environment.NewLine + Environment.NewLine +
            "Silence or inaudible; Constant or pulsed pattern." + Environment.NewLine +
            "Follows the default playback device while running.",
            "About ZeroTone",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void menuExit_Click(object? sender, EventArgs e)
    {
        SaveWindowPosition();
        PrepareToExit();
        Application.Exit();
    }

    /// <summary>
    /// Left single-click: arm a timer equal to DoubleClickTime. If no second click
    /// arrives, treat as single-click restore. Immediate restore would race with
    /// the first click of a double-click.
    /// </summary>
    private void trayIcon_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        trayDoubleClickTimer.Stop();
        trayDoubleClickTimer.Interval = SystemInformation.DoubleClickTime;
        trayDoubleClickTimer.Start();
    }

    private void trayDoubleClickTimer_Tick(object? sender, EventArgs e)
    {
        trayDoubleClickTimer.Stop();
        RestoreWindow();
    }

    private void trayIcon_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        trayDoubleClickTimer.Stop();

        // Defer restore until after NotifyIcon finishes the double-click gesture.
        // WinForms NotifyIcon sets an internal doubleClick flag on WM_LBUTTONDBLCLK
        // *after* raising MouseDoubleClick, and clears it only on the matching
        // WM_LBUTTONUP. Showing/activating the form here can steal that UP, leave
        // the flag stuck, and suppress the next MouseClick — so a later single-click
        // restore does nothing (the eaten click only clears the flag).
        if (IsHandleCreated)
        {
            BeginInvoke(RestoreWindow);
        }
        else
        {
            RestoreWindow();
        }
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        SaveWindowPosition();

        // Minimize-on-close is for the user Close button, not system shutdown.
        if (e.CloseReason != CloseReason.UserClosing)
        {
            return;
        }

        if (_settings.MinimizeOnClose)
        {
            MinimizeToTrayOrTaskbar();
            e.Cancel = true;
            return;
        }

        PrepareToExit();
    }

    private void checkStartWithWindows_CheckedChanged(object? sender, EventArgs e)
    {
        if (_isInitializing || _suppressStartWithWindowsHandler)
        {
            return;
        }

        if (WindowsStartupRegistration.TrySetEnabled(checkStartWithWindows.Checked))
        {
            _startupRegistrationFailureNotified = false;
            return;
        }

        _suppressStartWithWindowsHandler = true;
        try
        {
            checkStartWithWindows.Checked = WindowsStartupRegistration.IsEnabled();
        }
        finally
        {
            _suppressStartWithWindowsHandler = false;
        }

        WarnStartupRegistrationFailed();
    }

    /// <summary>
    /// One-shot warning when HKCU Run cannot be written. Does not crash the UI.
    /// </summary>
    private void WarnStartupRegistrationFailed()
    {
        if (_startupRegistrationFailureNotified || IsDisposed)
        {
            return;
        }

        _startupRegistrationFailureNotified = true;
        MessageBox.Show(
            this,
            "Could not change Start with Windows." + Environment.NewLine +
            Environment.NewLine +
            "Windows may have blocked the sign-in registration. ZeroTone will keep running.",
            AppName,
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private void checkMinimizeOnLaunch_CheckedChanged(object? sender, EventArgs e)
    {
        _settings.MinimizeOnLaunch = checkMinimizeOnLaunch.Checked;
        PersistSettings();
    }

    private void checkStartOnLaunch_CheckedChanged(object? sender, EventArgs e)
    {
        _settings.StartOnLaunch = checkStartOnLaunch.Checked;
        PersistSettings();
    }

    private void checkMinimizeToTray_CheckedChanged(object? sender, EventArgs e)
    {
        _settings.MinimizeToSystemTray = checkMinimizeToTray.Checked;
        PersistSettings();
    }

    private void checkMinimizeOnClose_CheckedChanged(object? sender, EventArgs e)
    {
        _settings.MinimizeOnClose = checkMinimizeOnClose.Checked;
        PersistSettings();
    }

    private void radioInaudible_CheckedChanged(object? sender, EventArgs e)
    {
        if (!radioInaudible.Checked)
        {
            return;
        }

        _settings.AudioType = AudioType.Inaudible;
        PersistSettings();
        RestartAudioIfEngaged();
    }

    private void radioSilence_CheckedChanged(object? sender, EventArgs e)
    {
        if (!radioSilence.Checked)
        {
            return;
        }

        _settings.AudioType = AudioType.Silence;
        PersistSettings();
        RestartAudioIfEngaged();
    }

    private void radioConstant_CheckedChanged(object? sender, EventArgs e)
    {
        if (!radioConstant.Checked)
        {
            return;
        }

        _settings.AudioPattern = AudioPattern.Constant;
        PersistSettings();
        RestartAudioIfEngaged();
    }

    private void radioPulsed_CheckedChanged(object? sender, EventArgs e)
    {
        if (!radioPulsed.Checked)
        {
            return;
        }

        _settings.AudioPattern = AudioPattern.Pulsed;
        PersistSettings();
        RestartAudioIfEngaged();
    }

    private static byte[] LoadEmbeddedResourceBytes(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded icon '{resourceName}' was not found.");

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private KeepAliveIconVisual CurrentIconVisual() =>
        ResolveIconVisual(_audioService.IsEngaged, _audioService.Phase);

    private void ReloadIconsForCurrentDpi()
    {
        var traySize = BestEmbeddedIconSize(SystemInformation.SmallIconSize);
        var newTrayRunning = CreateSizedIconFromBytes(_iconRunningData, traySize);
        var newTrayUnproven = CreateSizedIconFromBytes(_iconUnprovenData, traySize);
        var newTrayStopped = CreateSizedIconFromBytes(_iconStoppedData, traySize);
        var newWindowRunning = CreateMultiResIconFromBytes(_iconRunningData);
        var newWindowUnproven = CreateMultiResIconFromBytes(_iconUnprovenData);
        var newWindowStopped = CreateMultiResIconFromBytes(_iconStoppedData);

        var oldTrayRunning = _trayIconRunning;
        var oldTrayUnproven = _trayIconUnproven;
        var oldTrayStopped = _trayIconStopped;
        var oldWindowRunning = _windowIconRunning;
        var oldWindowUnproven = _windowIconUnproven;
        var oldWindowStopped = _windowIconStopped;
        var oldGetRunning = SnapshotAndClearIconCache(_getIconCacheRunning);
        var oldGetUnproven = SnapshotAndClearIconCache(_getIconCacheUnproven);
        var oldGetStopped = SnapshotAndClearIconCache(_getIconCacheStopped);

        _trayIconRunning = newTrayRunning;
        _trayIconUnproven = newTrayUnproven;
        _trayIconStopped = newTrayStopped;
        _windowIconRunning = newWindowRunning;
        _windowIconUnproven = newWindowUnproven;
        _windowIconStopped = newWindowStopped;

        _uiAppliedVisual = (KeepAliveIconVisual)(-1); // force reinstall even if phase is unchanged

        if (trayIcon is not null)
        {
            var visual = CurrentIconVisual();
            trayIcon.Icon = visual switch
            {
                KeepAliveIconVisual.Running => _trayIconRunning,
                KeepAliveIconVisual.Unproven => _trayIconUnproven,
                _ => _trayIconStopped,
            };
            Icon = visual switch
            {
                KeepAliveIconVisual.Running => _windowIconRunning,
                KeepAliveIconVisual.Unproven => _windowIconUnproven,
                _ => _windowIconStopped,
            };
            ApplyNativeWindowIcons(visual);
            _uiAppliedVisual = visual;
        }

        oldTrayRunning?.Dispose();
        oldTrayUnproven?.Dispose();
        oldTrayStopped?.Dispose();
        oldWindowRunning?.Dispose();
        oldWindowUnproven?.Dispose();
        oldWindowStopped?.Dispose();
        foreach (var icon in oldGetRunning)
        {
            icon.Dispose();
        }

        foreach (var icon in oldGetUnproven)
        {
            icon.Dispose();
        }

        foreach (var icon in oldGetStopped)
        {
            icon.Dispose();
        }
    }

    /// <summary>
    /// Installs caption + taskbar HICONs via WM_SETICON.
    /// ICON_SMALL follows this window's DPI (16 @ 96), not SM_CXSMICON — that
    /// metric stays on the primary, so a 100%→200% drag would keep a 16px
    /// handle and DWM would stretch it (anti-aliased caption).
    /// ICON_BIG is the Win11 taskbar slot (24 @ 96), not SM_CXICON (32).
    /// Missing embedded sizes use the hard-alpha glyph.
    /// </summary>
    private void ApplyNativeWindowIcons(KeepAliveIconVisual visual)
    {
        var smallPx = GetCaptionIconPixelSize();
        var bigPx = Math.Max(1, GetTaskbarIconPixelSize().Width);

        Icon newSmall;
        Icon newBig;
        switch (visual)
        {
            case KeepAliveIconVisual.Running:
                newSmall = CreateExactPixelIcon(smallPx, BarChartIconGlyph.RunningGreen, _iconRunningData);
                newBig = CreateExactPixelIcon(bigPx, BarChartIconGlyph.RunningGreen, _iconRunningData);
                break;
            case KeepAliveIconVisual.Unproven:
                newSmall = CreateExactPixelIcon(smallPx, BarChartIconGlyph.UnprovenAmber, _iconUnprovenData);
                newBig = CreateExactPixelIcon(bigPx, BarChartIconGlyph.UnprovenAmber, _iconUnprovenData);
                break;
            default:
                newSmall = CreateExactPixelIcon(smallPx, BarChartIconGlyph.StoppedRed, _iconStoppedData);
                newBig = CreateExactPixelIcon(bigPx, BarChartIconGlyph.StoppedRed, _iconStoppedData);
                break;
        }

        var oldSmall = _nativeSmallIcon;
        var oldBig = _nativeBigIcon;
        _nativeSmallIcon = newSmall;
        _nativeBigIcon = newBig;

        if (IsHandleCreated)
        {
            SendMessage(Handle, WmSetIcon, (IntPtr)IconSmall, newSmall.Handle);
            SendMessage(Handle, WmSetIcon, (IntPtr)IconBig, newBig.Handle);
        }

        oldSmall?.Dispose();
        oldBig?.Dispose();
    }

    /// <summary>
    /// Exact pixel-size icon: embedded frame when available, else painted glyph.
    /// </summary>
    private static Icon CreateExactPixelIcon(int pixelSize, Color color, byte[] icoData)
    {
        foreach (var embedded in EmbeddedIconSizes)
        {
            if (embedded == pixelSize)
            {
                return CreateSizedIconFromBytes(icoData, new Size(pixelSize, pixelSize));
            }
        }

        return BarChartIconGlyph.CreateIcon(pixelSize, color);
    }

    /// <summary>
    /// HICON for WM_GETICON. ICON_BIG / ICON_SMALL2 use taskbar pixel size, not SM_CXICON.
    /// </summary>
    private IntPtr GetIconHandleForShell(int iconType)
    {
        if (_windowIconRunning is null || _windowIconStopped is null)
        {
            return IntPtr.Zero;
        }

        if (iconType == IconSmall && _nativeSmallIcon is not null)
        {
            return _nativeSmallIcon.Handle;
        }

        if ((iconType == IconBig || iconType == IconSmall2) && _nativeBigIcon is not null)
        {
            return _nativeBigIcon.Handle;
        }

        var visual = CurrentIconVisual();

        int px;
        if (iconType is IconBig or IconSmall2)
        {
            px = Math.Max(1, GetTaskbarIconPixelSize().Width);
        }
        else
        {
            px = GetCaptionIconPixelSize();
        }

        var cache = visual switch
        {
            KeepAliveIconVisual.Running => _getIconCacheRunning,
            KeepAliveIconVisual.Unproven => _getIconCacheUnproven,
            _ => _getIconCacheStopped,
        };

        if (!cache.TryGetValue(px, out var icon))
        {
            icon = visual switch
            {
                KeepAliveIconVisual.Running => CreateExactPixelIcon(
                    px,
                    BarChartIconGlyph.RunningGreen,
                    _iconRunningData),
                KeepAliveIconVisual.Unproven => CreateExactPixelIcon(
                    px,
                    BarChartIconGlyph.UnprovenAmber,
                    _iconUnprovenData),
                _ => CreateExactPixelIcon(px, BarChartIconGlyph.StoppedRed, _iconStoppedData),
            };
            cache[px] = icon;
        }

        return icon.Handle;
    }

    /// <summary>
    /// Win11 taskbar glyphs are based on 24×24 at 96 DPI (not SM_CXICON).
    /// </summary>
    private Size GetTaskbarIconPixelSize()
    {
        var dpi = DeviceDpi > 0 ? DeviceDpi : 96;
        var px = (int)Math.Round(TaskbarIconPxAt96Dpi * (dpi / 96.0));
        return new Size(px, px);
    }

    /// <summary>
    /// Caption ICON_SMALL pixels for this window's DPI (16 @ 96).
    /// Not <see cref="SystemInformation.SmallIconSize"/> (primary SM_CXSMICON).
    /// </summary>
    private int GetCaptionIconPixelSize()
    {
        return Math.Max(1, LogicalToDeviceUnits(CaptionIconPxAt96Dpi));
    }

    private static Size BestEmbeddedIconSize(Size desired)
    {
        var target = Math.Max(desired.Width, desired.Height);
        var best = EmbeddedIconSizes[0];
        var bestDist = Math.Abs(best - target);

        foreach (var candidate in EmbeddedIconSizes)
        {
            var dist = Math.Abs(candidate - target);
            if (dist < bestDist || (dist == bestDist && candidate > best))
            {
                best = candidate;
                bestDist = dist;
            }
        }

        return new Size(best, best);
    }

    private static Icon CreateSizedIconFromBytes(byte[] data, Size size)
    {
        using var stream = new MemoryStream(data, writable: false);
        using var temp = new Icon(stream, size.Width, size.Height);
        return (Icon)temp.Clone();
    }

    private static Icon CreateMultiResIconFromBytes(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        // Icon copies the bytes; the stream may close.
        return new Icon(stream);
    }

    private static List<Icon> SnapshotAndClearIconCache(Dictionary<int, Icon> cache)
    {
        var list = new List<Icon>(cache.Values);
        cache.Clear();
        return list;
    }

    private void DisposeAllIcons()
    {
        if (IsHandleCreated)
        {
            SendMessage(Handle, WmSetIcon, (IntPtr)IconSmall, IntPtr.Zero);
            SendMessage(Handle, WmSetIcon, (IntPtr)IconBig, IntPtr.Zero);
        }

        trayIcon.Icon = null;
        Icon = null;

        _trayIconRunning?.Dispose();
        _trayIconUnproven?.Dispose();
        _trayIconStopped?.Dispose();
        _windowIconRunning?.Dispose();
        _windowIconUnproven?.Dispose();
        _windowIconStopped?.Dispose();
        _nativeSmallIcon?.Dispose();
        _nativeBigIcon?.Dispose();
        _nativeSmallIcon = null;
        _nativeBigIcon = null;

        foreach (var icon in _getIconCacheRunning.Values)
        {
            icon.Dispose();
        }

        foreach (var icon in _getIconCacheUnproven.Values)
        {
            icon.Dispose();
        }

        foreach (var icon in _getIconCacheStopped.Values)
        {
            icon.Dispose();
        }

        _getIconCacheRunning.Clear();
        _getIconCacheUnproven.Clear();
        _getIconCacheStopped.Clear();
    }
}
