namespace ZeroTone;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing && components is not null)
        {
            components.Dispose();
        }

        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();

        startStopButton = new Button();
        labelStatusCaption = new Label();
        keepAliveStatusLabel = new Label();
        outputDeviceLabel = new Label();

        trayIcon = new NotifyIcon(components);
        mainTabToolTip = new ToolTip(components);
        trayDoubleClickTimer = new System.Windows.Forms.Timer(components);

        trayMenu = new ContextMenuStrip(components);
        menuStatus = new ToolStripMenuItem();
        menuSeparatorStatus = new ToolStripSeparator();
        menuShow = new ToolStripMenuItem();
        menuStartStop = new ToolStripMenuItem();
        menuSeparator1 = new ToolStripSeparator();
        menuAbout = new ToolStripMenuItem();
        menuSeparator2 = new ToolStripSeparator();
        menuExit = new ToolStripMenuItem();

        tabs = new TabControl();
        tabMain = new TabPage();
        tabSettings = new TabPage();
        checkStartWithWindows = new CheckBox();
        checkMinimizeOnClose = new CheckBox();
        checkMinimizeToTray = new CheckBox();
        checkStartOnLaunch = new CheckBox();
        checkMinimizeOnLaunch = new CheckBox();
        tabAudio = new TabPage();
        groupAudioType = new GroupBox();
        radioInaudible = new RadioButton();
        radioSilence = new RadioButton();
        groupPattern = new GroupBox();
        radioConstant = new RadioButton();
        radioPulsed = new RadioButton();

        trayMenu.SuspendLayout();
        tabs.SuspendLayout();
        tabMain.SuspendLayout();
        tabSettings.SuspendLayout();
        tabAudio.SuspendLayout();
        groupAudioType.SuspendLayout();
        groupPattern.SuspendLayout();
        SuspendLayout();

        // Design-time Y is approximate; runtime layout equalizes margins.
        startStopButton.Location = new Point(8, 12);
        startStopButton.Name = "startStopButton";
        startStopButton.Size = new Size(53, 23);
        startStopButton.TabIndex = 0;
        startStopButton.Text = "Start";
        startStopButton.UseVisualStyleBackColor = true;
        startStopButton.Click += startStopButton_Click;

        labelStatusCaption.AutoSize = true;
        labelStatusCaption.Location = new Point(63, 16);
        labelStatusCaption.Name = "labelStatusCaption";
        labelStatusCaption.Size = new Size(60, 15);
        labelStatusCaption.TabIndex = 1;
        labelStatusCaption.Text = "ZeroTone:";

        // AutoSize must be false for AutoEllipsis. Runtime layout sets bounds
        // (after caption, remaining tab width) so long Running/drain text cannot
        // paint past the client edge.
        keepAliveStatusLabel.AutoSize = false;
        keepAliveStatusLabel.AutoEllipsis = true;
        keepAliveStatusLabel.Location = new Point(170, 16);
        keepAliveStatusLabel.Name = "keepAliveStatusLabel";
        keepAliveStatusLabel.Size = new Size(49, 15);
        keepAliveStatusLabel.TabIndex = 2;
        keepAliveStatusLabel.Text = "Stopped";

        // AutoSize must be false for AutoEllipsis. Runtime layout sets Width.
        outputDeviceLabel.AutoSize = false;
        outputDeviceLabel.AutoEllipsis = true;
        outputDeviceLabel.Location = new Point(8, 48);
        outputDeviceLabel.Name = "outputDeviceLabel";
        outputDeviceLabel.Size = new Size(370, 15);
        outputDeviceLabel.TabIndex = 3;
        outputDeviceLabel.Text = "Output Device:  ...";

        trayIcon.Text = "ZeroTone";
        trayIcon.Visible = true;
        trayIcon.MouseClick += trayIcon_MouseClick;
        trayIcon.MouseDoubleClick += trayIcon_MouseDoubleClick;

        // Interval is set at click-time from SystemInformation.DoubleClickTime.
        trayDoubleClickTimer.Tick += trayDoubleClickTimer_Tick;

        trayMenu.Items.AddRange(new ToolStripItem[]
        {
            menuStatus,
            menuSeparatorStatus,
            menuShow,
            menuStartStop,
            menuSeparator1,
            menuAbout,
            menuSeparator2,
            menuExit
        });
        trayMenu.Name = "trayMenu";
        trayMenu.Size = new Size(180, 152);

        // Informational; not clickable.
        menuStatus.Name = "menuStatus";
        menuStatus.Size = new Size(179, 22);
        menuStatus.Text = "Status: Stopped";
        menuStatus.Enabled = false;

        menuSeparatorStatus.Name = "menuSeparatorStatus";
        menuSeparatorStatus.Size = new Size(176, 6);

        // Bold default action, matching left-click on the tray icon.
        menuShow.Name = "menuShow";
        menuShow.Size = new Size(179, 22);
        menuShow.Text = "Show ZeroTone";
        menuShow.Font = new Font(menuShow.Font, FontStyle.Bold);
        menuShow.Click += menuShow_Click;

        menuStartStop.Name = "menuStartStop";
        menuStartStop.Size = new Size(179, 22);
        menuStartStop.Text = "Start ZeroTone";
        menuStartStop.Click += menuStartStop_Click;

        menuSeparator1.Name = "menuSeparator1";
        menuSeparator1.Size = new Size(149, 6);

        menuAbout.Name = "menuAbout";
        menuAbout.Size = new Size(152, 22);
        menuAbout.Text = "About";
        menuAbout.Click += menuAbout_Click;

        menuSeparator2.Name = "menuSeparator2";
        menuSeparator2.Size = new Size(149, 6);

        menuExit.Name = "menuExit";
        menuExit.Size = new Size(152, 22);
        menuExit.Text = "Exit";
        menuExit.Click += menuExit_Click;

        tabs.Controls.Add(tabMain);
        tabs.Controls.Add(tabSettings);
        tabs.Controls.Add(tabAudio);
        tabs.Dock = DockStyle.Fill;
        // Width 0: keep text-sized tab widths. Height 21 = native 20 at 96 DPI
        // + 1 so the Settings "g" descender is not clipped at 200%+. Runtime
        // re-applies LogicalToDeviceUnits(21) — ItemSize is not AutoScaled.
        tabs.ItemSize = new Size(0, 21);
        // Default is (6, 3). 10 matches short "Main" trailing slack on the
        // longer labels. Runtime re-applies via TCM_SETPADDING (no recreate).
        tabs.Padding = new Point(10, 3);
        tabs.Location = new Point(0, 0);
        tabs.Name = "tabs";
        tabs.SelectedIndex = 0;
        tabs.Size = new Size(400, 106);
        tabs.TabIndex = 0;

        tabMain.Controls.Add(startStopButton);
        tabMain.Controls.Add(labelStatusCaption);
        tabMain.Controls.Add(keepAliveStatusLabel);
        tabMain.Controls.Add(outputDeviceLabel);
        tabMain.Location = new Point(4, 25);
        tabMain.Name = "tabMain";
        tabMain.Padding = new Padding(3);
        tabMain.Size = new Size(392, 77);
        tabMain.TabIndex = 0;
        tabMain.Text = "Main";
        tabMain.UseVisualStyleBackColor = true;

        tabSettings.Controls.Add(checkStartWithWindows);
        tabSettings.Controls.Add(checkMinimizeOnClose);
        tabSettings.Controls.Add(checkMinimizeToTray);
        tabSettings.Controls.Add(checkStartOnLaunch);
        tabSettings.Controls.Add(checkMinimizeOnLaunch);
        tabSettings.Location = new Point(4, 25);
        tabSettings.Name = "tabSettings";
        tabSettings.Padding = new Padding(3);
        tabSettings.Size = new Size(392, 77);
        tabSettings.TabIndex = 1;
        tabSettings.Text = "Settings";
        tabSettings.UseVisualStyleBackColor = true;

        // Settings: design-time Y is the 96 DPI 5/29/53 even grid (19 px rows,
        // 5 px margins and gaps). Runtime layout remeasures.
        checkStartWithWindows.AutoSize = true;
        checkStartWithWindows.Location = new Point(8, 5);
        checkStartWithWindows.Name = "checkStartWithWindows";
        checkStartWithWindows.Size = new Size(130, 19);
        checkStartWithWindows.TabIndex = 0;
        checkStartWithWindows.Text = "Start with Windows";
        checkStartWithWindows.UseVisualStyleBackColor = true;
        checkStartWithWindows.CheckedChanged += checkStartWithWindows_CheckedChanged;

        checkStartOnLaunch.AutoSize = true;
        checkStartOnLaunch.Location = new Point(8, 29);
        checkStartOnLaunch.Name = "checkStartOnLaunch";
        checkStartOnLaunch.Size = new Size(100, 19);
        checkStartOnLaunch.TabIndex = 1;
        checkStartOnLaunch.Text = "Start on Launch";
        checkStartOnLaunch.UseVisualStyleBackColor = true;
        checkStartOnLaunch.CheckedChanged += checkStartOnLaunch_CheckedChanged;

        checkMinimizeOnLaunch.AutoSize = true;
        checkMinimizeOnLaunch.Location = new Point(8, 53);
        checkMinimizeOnLaunch.Name = "checkMinimizeOnLaunch";
        checkMinimizeOnLaunch.Size = new Size(110, 19);
        checkMinimizeOnLaunch.TabIndex = 2;
        checkMinimizeOnLaunch.Text = "Minimize on Launch";
        checkMinimizeOnLaunch.UseVisualStyleBackColor = true;
        checkMinimizeOnLaunch.CheckedChanged += checkMinimizeOnLaunch_CheckedChanged;

        checkMinimizeToTray.AutoSize = true;
        checkMinimizeToTray.Location = new Point(190, 5);
        checkMinimizeToTray.Name = "checkMinimizeToTray";
        checkMinimizeToTray.Size = new Size(150, 19);
        checkMinimizeToTray.TabIndex = 3;
        checkMinimizeToTray.Text = "Minimize to System Tray";
        checkMinimizeToTray.UseVisualStyleBackColor = true;
        checkMinimizeToTray.CheckedChanged += checkMinimizeToTray_CheckedChanged;

        checkMinimizeOnClose.AutoSize = true;
        checkMinimizeOnClose.Location = new Point(190, 29);
        checkMinimizeOnClose.Name = "checkMinimizeOnClose";
        checkMinimizeOnClose.Size = new Size(118, 19);
        checkMinimizeOnClose.TabIndex = 4;
        checkMinimizeOnClose.Text = "Minimize on Close";
        checkMinimizeOnClose.UseVisualStyleBackColor = true;
        checkMinimizeOnClose.CheckedChanged += checkMinimizeOnClose_CheckedChanged;

        tabAudio.Controls.Add(groupAudioType);
        tabAudio.Controls.Add(groupPattern);
        tabAudio.Location = new Point(4, 25);
        tabAudio.Name = "tabAudio";
        tabAudio.Padding = new Padding(3);
        tabAudio.Size = new Size(392, 77);
        tabAudio.TabIndex = 2;
        tabAudio.Text = "Audio Options";
        tabAudio.UseVisualStyleBackColor = true;

        // Audio Options: design-time is the 96 DPI 8/184/8 grid and 62 px
        // groups (7 px top after leftover split). Runtime layout remeasures.
        groupAudioType.Controls.Add(radioSilence);
        groupAudioType.Controls.Add(radioInaudible);
        groupAudioType.Location = new Point(8, 7);
        groupAudioType.Name = "groupAudioType";
        groupAudioType.Size = new Size(184, 62);
        groupAudioType.TabIndex = 0;
        groupAudioType.TabStop = false;
        groupAudioType.Text = "Audio Type";

        radioSilence.AutoSize = true;
        radioSilence.Location = new Point(10, 18);
        radioSilence.Name = "radioSilence";
        radioSilence.Size = new Size(63, 19);
        radioSilence.TabIndex = 0;
        radioSilence.TabStop = true;
        radioSilence.Text = "Silence";
        radioSilence.UseVisualStyleBackColor = true;
        radioSilence.CheckedChanged += radioSilence_CheckedChanged;

        radioInaudible.AutoSize = true;
        radioInaudible.Location = new Point(10, 38);
        radioInaudible.Name = "radioInaudible";
        radioInaudible.Size = new Size(110, 19);
        radioInaudible.TabIndex = 1;
        radioInaudible.TabStop = true;
        radioInaudible.Text = "Inaudible Sound";
        radioInaudible.UseVisualStyleBackColor = true;
        radioInaudible.CheckedChanged += radioInaudible_CheckedChanged;

        groupPattern.Controls.Add(radioConstant);
        groupPattern.Controls.Add(radioPulsed);
        groupPattern.Location = new Point(200, 7);
        groupPattern.Name = "groupPattern";
        groupPattern.Size = new Size(184, 62);
        groupPattern.TabIndex = 1;
        groupPattern.TabStop = false;
        groupPattern.Text = "Pattern";

        radioConstant.AutoSize = true;
        radioConstant.Location = new Point(10, 18);
        radioConstant.Name = "radioConstant";
        radioConstant.Size = new Size(75, 19);
        radioConstant.TabIndex = 0;
        radioConstant.TabStop = true;
        radioConstant.Text = "Constant";
        radioConstant.UseVisualStyleBackColor = true;
        radioConstant.CheckedChanged += radioConstant_CheckedChanged;

        radioPulsed.AutoSize = true;
        radioPulsed.Location = new Point(10, 38);
        radioPulsed.Name = "radioPulsed";
        radioPulsed.Size = new Size(63, 19);
        radioPulsed.TabIndex = 1;
        radioPulsed.TabStop = true;
        radioPulsed.Text = "Pulsed";
        radioPulsed.UseVisualStyleBackColor = true;
        radioPulsed.CheckedChanged += radioPulsed_CheckedChanged;

        // Dpi mode scales by DeviceDpi/96. Font mode grew X/Y unevenly at 125%+.
        // Design sizes at 96 DPI; keep AutoScaleDimensions at 96F baseline.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        // 106 = 105 + 1 tab-strip pixel (96 DPI). Page content stays 77.
        ClientSize = new Size(400, 106);
        Controls.Add(tabs);
        Name = "MainForm";
        Text = "ZeroTone";
        FormClosing += MainForm_FormClosing;

        trayMenu.ResumeLayout(false);
        tabs.ResumeLayout(false);
        tabMain.ResumeLayout(false);
        tabMain.PerformLayout();
        tabSettings.ResumeLayout(false);
        tabSettings.PerformLayout();
        tabAudio.ResumeLayout(false);
        groupAudioType.ResumeLayout(false);
        groupAudioType.PerformLayout();
        groupPattern.ResumeLayout(false);
        groupPattern.PerformLayout();
        ResumeLayout(false);
    }

    #endregion

    private Button startStopButton;
    private Label labelStatusCaption;
    private Label keepAliveStatusLabel;
    private Label outputDeviceLabel;
    private ToolTip mainTabToolTip;
    private NotifyIcon trayIcon;
    private System.Windows.Forms.Timer trayDoubleClickTimer;
    private ContextMenuStrip trayMenu;
    private ToolStripMenuItem menuStatus;
    private ToolStripSeparator menuSeparatorStatus;
    private ToolStripMenuItem menuShow;
    private ToolStripMenuItem menuStartStop;
    private ToolStripSeparator menuSeparator1;
    private ToolStripMenuItem menuAbout;
    private ToolStripSeparator menuSeparator2;
    private ToolStripMenuItem menuExit;
    private TabControl tabs;
    private TabPage tabMain;
    private TabPage tabSettings;
    private CheckBox checkStartWithWindows;
    private CheckBox checkMinimizeOnClose;
    private CheckBox checkMinimizeToTray;
    private CheckBox checkStartOnLaunch;
    private CheckBox checkMinimizeOnLaunch;
    private TabPage tabAudio;
    private GroupBox groupAudioType;
    private RadioButton radioInaudible;
    private RadioButton radioSilence;
    private GroupBox groupPattern;
    private RadioButton radioConstant;
    private RadioButton radioPulsed;
}
