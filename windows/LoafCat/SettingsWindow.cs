using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;
using LoafCat.Modules;
using Microsoft.Win32;

namespace LoafCat;

/// What the settings window is allowed to reach back into.
///
/// An interface rather than a reference to `CatController` for the same reason modules
/// get one: the window outlives the rig and the view, both of which are thrown away and
/// rebuilt on every theme or scale change. Anything that captured them would be holding
/// a dead object the first time somebody picked another cat.
public interface ISettingsHost
{
    string CurrentTheme { get; }
    double CurrentScale { get; }
    bool IsCatVisible { get; }
    void SetCatVisible(bool visible);
    void ApplyTheme(string theme);
    void ApplyScale(double scale);
    void ApplyDragFeel(DragFeel feel);
    void ApplyStretchTempo(StretchTempo tempo);
    void CentreCat();
    WellnessSuite? WellnessSuite { get; }

    /// Updates. The host owns the Updater; a pane is only ever allowed to ask, which is
    /// the same rule that keeps Settings from holding a Rig or a CatView.
    string UpdateStatus { get; }
    Task CheckForUpdates(Action<string> report);

    /// -1 when nothing is downloading, otherwise 0-100.
    int DownloadPercent { get; }
}

/// The settings window: one place to change everything the cat can be told.
///
/// Every control applies immediately. No OK, no Apply — that is the platform
/// convention on both platforms, and a cat that changes as you click is the point.
[SupportedOSPlatform("windows")]
public static class SettingsWindow
{
    private static SettingsForm? _form;

    public static void Show(ISettingsHost host)
    {
        if (_form is null || _form.IsDisposed)
        {
            _form = new SettingsForm(host);
            _form.FormClosed += (_, _) => _form = null;
        }
        _form.Refresh(all: true);
        if (!_form.Visible) _form.Show();
        // A tray app does not come forward just by opening a window, and a settings
        // window that appears behind the editor reads as the click doing nothing.
        _form.WindowState = FormWindowState.Normal;
        _form.BringToFront();
        _form.Activate();
    }

    /// For state the window does not own: the cat can also be turned on and off from the
    /// tray, and the checkbox has to follow.
    public static void RefreshPanes()
    {
        if (_form is { IsDisposed: false, Visible: true }) _form.Refresh(all: true);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class SettingsForm : Form
{
    private readonly ISettingsHost _host;
    private readonly TabControl _tabs = new();
    private readonly List<SettingsPane> _panes = [];

    internal const int ContentWidth = 500;

    public SettingsForm(ISettingsHost host)
    {
        _host = host;
        Text = "loafcat Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        ClientSize = new Size(ContentWidth + 26, 560);
        ShowInTaskbar = TaskbarPresence.ShowInTaskbar;
        try { Icon = Branding.AppIcon(); } catch (ArgumentException) { }
        KeyPreview = true;

        _tabs.Dock = DockStyle.Fill;
        _tabs.Padding = new Point(12, 6);
        Controls.Add(_tabs);

        AddPane(new CatPane(host));
        AddPane(new WellnessPane(host));
        AddPane(new AgentPane(host));
        AddPane(new AdvancedPane(host));
        AddPane(new AboutPane(host));

        // Panes refresh as they come into view, so switching tabs never shows a stale
        // value written from the tray or by the cat itself.
        _tabs.SelectedIndexChanged += (_, _) => Current()?.Refresh();
    }

    private void AddPane(SettingsPane pane)
    {
        var page = new TabPage(pane.Title) { UseVisualStyleBackColor = true };
        pane.Build();
        pane.Root.Dock = DockStyle.Fill;
        page.Controls.Add(pane.Root);
        _tabs.TabPages.Add(page);
        _panes.Add(pane);
    }

    private SettingsPane? Current() =>
        _tabs.SelectedIndex >= 0 && _tabs.SelectedIndex < _panes.Count
            ? _panes[_tabs.SelectedIndex]
            : null;

    public void Refresh(bool all)
    {
        if (all)
        {
            foreach (var p in _panes) p.Refresh();
        }
        else
        {
            Current()?.Refresh();
        }
        ShowInTaskbar = TaskbarPresence.ShowInTaskbar;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Escape and Ctrl+W close, which is what every other window on the platform
        // does. Closing hides rather than destroys: `SettingsWindow.Show` reuses the
        // instance so a second visit keeps the selected tab.
        if (keyData == Keys.Escape || keyData == (Keys.Control | Keys.W))
        {
            Hide();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}

// MARK: - pane scaffolding

/// One tab. Panes build their layout once and re-read state in `Refresh()`, which runs
/// every time the pane is shown — settings can also be changed from the tray and by the
/// cat itself, so a pane that only read state at build time would show stale values.
[SupportedOSPlatform("windows")]
internal abstract class SettingsPane(ISettingsHost host)
{
    protected ISettingsHost Host { get; } = host;

    public abstract string Title { get; }

    public FlowLayoutPanel Root { get; } = new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        Padding = new Padding(16, 14, 16, 14),
    };

    public abstract void Build();
    public abstract void Refresh();

    // --- shared builders --------------------------------------------------
    // Small and blunt on purpose. A settings window is the one place in this codebase
    // where laying things out in code is cheaper than any abstraction.

    protected const int LabelWidth = 130;
    protected static int Wide => SettingsForm.ContentWidth - 40;

    protected void Add(Control c) => Root.Controls.Add(c);

    protected Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont,
                        FontStyle.Bold),
        Margin = new Padding(0, 8, 0, 4),
    };

    protected Label Caption(string text) => new()
    {
        Text = text,
        MaximumSize = new Size(Wide, 0),
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 0, 0, 8),
    };

    protected Panel Row(string title, Control control)
    {
        var label = new Label
        {
            Text = title,
            AutoSize = false,
            Width = LabelWidth,
            TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(0, 3),
            Height = 22,
        };
        control.Location = new Point(LabelWidth + 10, 0);
        var panel = new Panel
        {
            Width = Wide,
            Height = Math.Max(control.Height, 24) + 6,
            Margin = new Padding(0, 2, 0, 2),
        };
        panel.Controls.Add(label);
        panel.Controls.Add(control);
        return panel;
    }

    protected Control Divider() => new Label
    {
        BorderStyle = BorderStyle.Fixed3D,
        Height = 2,
        Width = Wide,
        Margin = new Padding(0, 10, 0, 10),
    };

    /// A minutes dropdown. `0` is rendered as "Off" everywhere, which is the convention
    /// `WellnessSettings` already uses for "this feature is disabled".
    protected static ComboBox MinutesBox(int[] options, string unit = " min")
    {
        var box = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 110,
        };
        foreach (int v in options) box.Items.Add(new Choice(v, v == 0 ? "Off" : $"{v}{unit}"));
        return box;
    }

    protected sealed record Choice(int Value, string Text)
    {
        public override string ToString() => Text;
    }

    protected static void Select(ComboBox box, int value)
    {
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is Choice c && c.Value == value) { box.SelectedIndex = i; return; }
        }
    }

    protected static int? ValueOf(ComboBox box) =>
        box.SelectedItem is Choice c ? c.Value : null;

    protected static Button MakeButton(string title, Action onClick)
    {
        var b = new Button { Text = title, AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        b.Click += (_, _) => onClick();
        return b;
    }

    protected static CheckBox Checkbox(string title, Action onToggle)
    {
        var c = new CheckBox { Text = title, AutoSize = true, Margin = new Padding(0, 4, 0, 2) };
        c.CheckedChanged += (_, _) => onToggle();
        return c;
    }
}

// MARK: - Cat

[SupportedOSPlatform("windows")]
internal sealed class CatPane(ISettingsHost host) : SettingsPane(host)
{
    public override string Title => "Cat";

    private readonly List<Button> _themeButtons = [];
    private readonly ComboBox _size = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly ComboBox _feel = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private CheckBox _login = null!;
    private Label _loginNote = null!;
    private CheckBox _taskbar = null!;
    private CheckBox _onOff = null!;
    private bool _updating;

    private static readonly (string Label, double Scale)[] Sizes =
        [("Small", 2), ("Medium", 3), ("Large", 4)];

    public override void Build()
    {
        // The on switch, at the top, because it is the one control someone opens this
        // window looking for.
        _onOff = Checkbox("Show the cat on screen", () =>
        {
            if (!_updating) Host.SetCatVisible(_onOff.Checked);
        });
        _onOff.Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont,
                               FontStyle.Bold);
        Add(_onOff);
        Add(Caption(
            "Turning it off really is off, not hidden: nothing animates and no timer "
            + "fires. loafcat stays in the notification area, and opening the app again "
            + "turns it back on."));
        Add(Divider());

        Add(Heading("Cat"));
        var themeRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = true,
            Width = Wide,
            Margin = new Padding(0, 0, 0, 4),
        };
        foreach (string name in Assets.Themes())
        {
            var b = new Button
            {
                Text = char.ToUpperInvariant(name[0]) + name[1..],
                TextImageRelation = TextImageRelation.ImageAboveText,
                Size = new Size(84, 92),
                Tag = name,
                FlatStyle = FlatStyle.Standard,
            };
            // 2x of a 48px canvas, then shown at 96px: a whole-number scale, so the
            // thumbnail stays as crisp as the cat on the desktop.
            if (ThemeThumbnail.Image(name, 2) is { } thumb) b.Image = thumb;
            b.Click += (_, _) =>
            {
                Host.ApplyTheme(name);
                Refresh();
            };
            _themeButtons.Add(b);
            themeRow.Controls.Add(b);
        }
        Add(themeRow);
        // The resolved path, not a hardcoded one. It differs between the zip (beside
        // the executable) and the standalone .exe (unpacked under LOCALAPPDATA), and
        // telling someone to look in a folder that is not the one being read is worse
        // than saying nothing.
        Add(Caption($"Themes are directories under {Path.Combine(Assets.Root(), "themes")}. "
                    + "Drop one in and it appears here."));

        Add(Divider());
        Add(Heading("Size and feel"));

        foreach (var (label, _) in Sizes) _size.Items.Add(label);
        _size.SelectedIndexChanged += (_, _) =>
        {
            if (!_updating && _size.SelectedIndex >= 0)
                Host.ApplyScale(Sizes[_size.SelectedIndex].Scale);
        };
        Add(Row("Size", _size));

        foreach (var f in DragFeelExtensions.All) _feel.Items.Add(f.Label());
        _feel.SelectedIndexChanged += (_, _) =>
        {
            if (!_updating && _feel.SelectedIndex >= 0)
                Host.ApplyDragFeel(DragFeelExtensions.All[_feel.SelectedIndex]);
        };
        Add(Row("Drag", _feel));
        Add(Caption("How far the cat stretches when you pick it up and pull. "
                    + "Subtle barely droops; springy snaps back hardest."));

        Add(Divider());
        Add(Heading("Starting up"));
        _login = Checkbox("Open loafcat at login", ToggleLogin);
        Add(_login);
        _loginNote = Caption("");
        Add(_loginNote);

        _taskbar = Checkbox("Show loafcat in the taskbar", () =>
        {
            if (_updating) return;
            TaskbarPresence.ShowInTaskbar = _taskbar.Checked;
            SettingsWindow.RefreshPanes();
        });
        Add(_taskbar);
        Add(Caption(
            "Off by default: a notification-area pet has no business taking a taskbar "
            + "slot. Turn it on if you would rather reach loafcat the way you reach "
            + "every other app. The cat itself never appears there."));

        Add(Divider());
        Add(MakeButton("Centre on screen", Host.CentreCat));
    }

    public override void Refresh()
    {
        _updating = true;
        try
        {
            foreach (var b in _themeButtons)
            {
                bool selected = (b.Tag as string) == Host.CurrentTheme;
                b.FlatStyle = selected ? FlatStyle.Flat : FlatStyle.Standard;
                b.BackColor = selected ? SystemColors.Highlight : SystemColors.Control;
                b.ForeColor = selected ? SystemColors.HighlightText : SystemColors.ControlText;
            }

            int sizeIndex = Array.FindIndex(Sizes, s => s.Scale == Host.CurrentScale);
            _size.SelectedIndex = sizeIndex >= 0 ? sizeIndex : 0;
            _feel.SelectedIndex = Array.IndexOf(
                DragFeelExtensions.All, DragFeelExtensions.Current);
            _taskbar.Checked = TaskbarPresence.ShowInTaskbar;
            _onOff.Checked = Host.IsCatVisible;
            RefreshLogin();
        }
        finally
        {
            _updating = false;
        }
    }

    // --- launch at login ---------------------------------------------------
    // The Run key under HKEY_CURRENT_USER. Per-user, needs no elevation, and is the
    // mechanism Windows' own Startup Apps list reads and lets the user override — which
    // matters: a user who turns loafcat off in Task Manager expects it to STAY off, and
    // Windows records that as a separate `StartupApproved` entry we deliberately do not
    // touch. The checkbox therefore reports what we wrote, not what Windows will do.
    //
    // The macOS counterpart is SMAppService, which registers by code signature.

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "loafcat";

    private static string LaunchCommand => $"\"{Environment.ProcessPath}\"";

    private void RefreshLogin()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            string? existing = key?.GetValue(RunValue) as string;
            _login.Checked = existing is not null;
            _loginNote.Text = existing is not null && existing != LaunchCommand
                ? "Registered from a different location — untick and tick to update it."
                : "";
        }
        catch (Exception e) when (e is System.Security.SecurityException
                                      or UnauthorizedAccessException or IOException)
        {
            _login.Enabled = false;
            _loginNote.Text = "This setting is managed by your organisation.";
        }
    }

    private void ToggleLogin()
    {
        if (_updating) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;
            if (_login.Checked) key.SetValue(RunValue, LaunchCommand);
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
            _loginNote.Text = "";
        }
        catch (Exception e) when (e is System.Security.SecurityException
                                      or UnauthorizedAccessException or IOException)
        {
            // A checkbox that silently un-ticks itself is the worst possible outcome, so
            // say what happened rather than swallowing it.
            _loginNote.Text = $"Could not change this: {e.Message}";
        }
        RefreshLogin();
    }
}

// MARK: - Wellness

[SupportedOSPlatform("windows")]
internal sealed class WellnessPane(ISettingsHost host) : SettingsPane(host)
{
    public override string Title => "Wellness";

    private ComboBox _stretch = null!;
    private ComboBox _hydration = null!;
    private ComboBox _focus = null!;
    private ComboBox _break = null!;
    private ComboBox _rounds = null!;
    private Button _pomodoro = null!;
    private CheckBox _sound = null!;
    private CheckBox _reminderOn = null!;
    private TextBox _reminderTime = null!;
    private TextBox _reminderText = null!;
    private TextBox _note = null!;
    private bool _updating;

    private WellnessSuite? Suite => Host.WellnessSuite;

    public override void Build()
    {
        Add(Heading("Breaks"));
        _stretch = MinutesBox(WellnessSettings.StretchOptions);
        _hydration = MinutesBox(WellnessSettings.HydrationOptions);
        _stretch.SelectedIndexChanged += (_, _) => ChangeIntervals();
        _hydration.SelectedIndexChanged += (_, _) => ChangeIntervals();
        Add(Row("Stretch break", _stretch));
        Add(Row("Hydration", _hydration));
        Add(MakeButton("Stretch now", () => Suite?.StretchNow()));

        Add(Divider());
        Add(Heading("Pomodoro"));
        _focus = MinutesBox(WellnessSettings.FocusOptions);
        _break = MinutesBox(WellnessSettings.BreakOptions);
        _rounds = MinutesBox(WellnessSettings.RoundOptions, unit: "");
        _focus.SelectedIndexChanged += (_, _) => ChangeIntervals();
        _break.SelectedIndexChanged += (_, _) => ChangeIntervals();
        _rounds.SelectedIndexChanged += (_, _) => ChangeIntervals();
        Add(Row("Focus", _focus));
        Add(Row("Break", _break));
        Add(Row("Rounds", _rounds));

        _pomodoro = MakeButton("Start", () =>
        {
            Suite?.TogglePomodoro();
            _pomodoro.Text = (Suite?.PomodoroRunning ?? false) ? "Pause" : "Start";
        });
        var controls = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Width = Wide,
        };
        controls.Controls.Add(_pomodoro);
        controls.Controls.Add(MakeButton("Reset", () => { Suite?.ResetPomodoro(); Refresh(); }));
        Add(controls);

        Add(Divider());
        Add(Heading("Reminders"));
        _reminderOn = Checkbox("Remind me daily at", CommitReminder);
        _reminderTime = new TextBox { Width = 70, Margin = new Padding(6, 4, 0, 0) };
        _reminderTime.Leave += (_, _) => CommitReminder();
        var when = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Width = Wide,
        };
        when.Controls.Add(_reminderOn);
        when.Controls.Add(_reminderTime);
        Add(when);

        _reminderText = new TextBox
        {
            Width = Wide,
            PlaceholderText = "Stand up and look out of a window",
        };
        _reminderText.Leave += (_, _) => CommitReminder();
        Add(_reminderText);
        Add(Caption("24-hour time. Skipped rather than banked if you are away from the "
                    + "keyboard when it comes due — a reminder is about a moment."));

        _sound = Checkbox("Play a sound with reminders", () =>
        {
            if (!_updating && Suite is { } s) s.Settings.SoundEnabled = _sound.Checked;
        });
        Add(_sound);

        Add(Divider());
        Add(Heading("Pinned note"));
        _note = new TextBox
        {
            Width = Wide,
            PlaceholderText = "Something the cat should keep holding",
        };
        _note.Leave += (_, _) => { if (!_updating) Suite?.PinNote(_note.Text); };
        Add(_note);
        Add(Caption("Shown in the cat's speech bubble until you clear it. "
                    + "Leave empty to unpin."));
    }

    public override void Refresh()
    {
        if (Suite?.Settings is not { } s) return;
        _updating = true;
        try
        {
            Select(_stretch, s.StretchMinutes);
            Select(_hydration, s.HydrationMinutes);
            Select(_focus, s.FocusMinutes);
            Select(_break, s.BreakMinutes);
            Select(_rounds, s.Rounds);
            _pomodoro.Text = (Suite?.PomodoroRunning ?? false) ? "Pause" : "Start";
            _sound.Checked = s.SoundEnabled;
            _reminderOn.Checked = s.ReminderEnabled && s.ReminderTime.Length > 0;
            _reminderTime.Text = s.ReminderTime.Length == 0
                ? MessageModule.DefaultTimeString()
                : s.ReminderTime;
            _reminderTime.ForeColor = SystemColors.WindowText;
            _reminderText.Text = s.ReminderText;
            _note.Text = s.PinnedNote;
        }
        finally
        {
            _updating = false;
        }
    }

    private void ChangeIntervals()
    {
        if (_updating || Suite?.Settings is not { } s) return;
        if (ValueOf(_stretch) is { } a) s.StretchMinutes = a;
        if (ValueOf(_hydration) is { } b) s.HydrationMinutes = b;
        if (ValueOf(_focus) is { } c) s.FocusMinutes = c;
        if (ValueOf(_break) is { } d) s.BreakMinutes = d;
        if (ValueOf(_rounds) is { } e) s.Rounds = e;
        Suite?.SettingsChanged();
    }

    private void CommitReminder()
    {
        if (_updating || Suite is not { } suite) return;
        if (!_reminderOn.Checked)
        {
            suite.ClearReminder();
            return;
        }
        if (suite.SetReminder(_reminderTime.Text, _reminderText.Text))
        {
            _reminderTime.Text = suite.ReminderTime;
            _reminderTime.ForeColor = SystemColors.WindowText;
        }
        else
        {
            // Red rather than a message box: the field is right there, and a modal for a
            // typo is the kind of thing that makes people stop using a settings panel.
            _reminderTime.ForeColor = Color.Firebrick;
        }
    }
}

// MARK: - Claude Code

[SupportedOSPlatform("windows")]
internal sealed class AgentPane(ISettingsHost host) : SettingsPane(host)
{
    public override string Title => "Claude Code";

    private Label _state = null!;
    private Label _status = null!;
    private Button _connect = null!;
    private Button _disconnect = null!;

    public override void Build()
    {
        Add(Heading("Claude Code"));
        Add(Caption("The cat can show what Claude Code is doing: thinking while a "
                    + "request is running, a hop when it finishes, an alert when it "
                    + "needs you."));

        _state = new Label
        {
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont,
                            FontStyle.Bold),
            Margin = new Padding(0, 4, 0, 8),
        };
        Add(_state);

        _connect = MakeButton("Connect to Claude Code", Connect);
        _disconnect = MakeButton("Disconnect", Disconnect);
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Width = Wide,
        };
        buttons.Controls.Add(_connect);
        buttons.Controls.Add(_disconnect);
        Add(buttons);

        Add(Divider());
        _status = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Font = new Font(FontFamily.GenericMonospace, 8.5f),
        };
        Add(_status);

        Add(Caption(
            "Connecting adds hook entries to ~\\.claude\\settings.json and copies the "
            + "hook script to ~\\.loafcat\\. Your previous settings file is backed up "
            + "alongside it first.\n\n"
            + "Every hook is asynchronous, carries a short timeout and exits zero "
            + "whatever happens, so it cannot slow a Claude Code session down — not "
            + "even with loafcat quit. Disconnecting removes only loafcat's entries and "
            + "leaves anyone else's alone."));

        // The user can edit settings.json by hand, so this pane has to notice.
        AgentModule.Shared.ConnectionChanged += OnConnectionChanged;
    }

    private void OnConnectionChanged()
    {
        if (Root.IsHandleCreated && !Root.IsDisposed) Root.BeginInvoke(Refresh);
    }

    public override void Refresh()
    {
        var agent = AgentModule.Shared;
        bool connected = agent.IsConnected;
        _state.Text = connected
            ? $"Connected — {agent.HookCount} hooks registered."
            : "Not connected.";
        _state.ForeColor = connected ? Color.SeaGreen : SystemColors.GrayText;
        _connect.Enabled = !connected;
        _disconnect.Enabled = connected;
        _status.Text = agent.ListenerStatus;
    }

    private void Connect()
    {
        string? error = AgentModule.Shared.Connect();
        if (error is null)
        {
            MessageBox.Show(
                $"{AgentModule.Shared.HookCount} hooks registered in "
                + "~\\.claude\\settings.json. The previous file was copied to "
                + "settings.json.loafcat-backup.\n\n"
                + "Every hook is async with a short timeout and exits 0 whatever "
                + "happens, so it cannot slow a session down — even with loafcat quit.",
                "Connected to Claude Code", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show(error, "Could not connect",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        Refresh();
    }

    private void Disconnect()
    {
        string? error = AgentModule.Shared.Disconnect();
        MessageBox.Show(
            error ?? "Only loafcat's hook entries were removed.",
            error is null ? "Disconnected" : "Could not disconnect",
            MessageBoxButtons.OK,
            error is null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        Refresh();
    }
}

// MARK: - About

[SupportedOSPlatform("windows")]
/// The knobs that are real but that most people should never have to find.
///
/// A separate pane rather than more controls in Cat, because Cat is the pane someone
/// opens on their first run and every extra row there is one more decision asked of
/// somebody who only wanted a cat. Nothing in here changes what the app does, only how
/// it moves.
internal sealed class AdvancedPane(ISettingsHost host) : SettingsPane(host)
{
    public override string Title => "Advanced";

    private readonly ComboBox _tempo =
        new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private Control _tempoDetail = null!;
    private bool _updating;

    public override void Build()
    {
        Add(Heading("Stretch"));

        foreach (var t in StretchTempoExtensions.All) _tempo.Items.Add(t.Label());
        _tempo.SelectedIndexChanged += (_, _) =>
        {
            if (_updating || _tempo.SelectedIndex < 0) return;
            var picked = StretchTempoExtensions.All[_tempo.SelectedIndex];
            Host.ApplyStretchTempo(picked);
            _tempoDetail.Text = $"Unstretches {picked.Detail()}.";
        };
        Add(Row("Tempo", _tempo));
        _tempoDetail = Caption("");
        Add(_tempoDetail);
        Add(Caption(
            "How quickly the stretch comes on and goes away \u2014 separate from how FAR "
            + "it goes, which is Drag over in Cat. A big slow stretch and a small snappy "
            + "one are both coherent, and one control cannot give you either.\n\n"
            + "The gesture is deliberately lopsided: it snaps taut about five times "
            + "faster than it eases back, and holds the stretch for a moment first. That "
            + "is what makes it read as elastic rather than as a slider being dragged, so "
            + "these scale the lopsidedness rather than flattening it."));

        Add(Divider());
        Add(Caption(
            "Everything here is a multiplier on the numbers in the theme's cat.json, so a "
            + "theme that retunes the drag keeps all four presets meaningful. Normal is "
            + "1.0 by definition \u2014 the shipped tuning is the normal preset."));
    }

    public override void Refresh()
    {
        _updating = true;
        try
        {
            var current = StretchTempoExtensions.Current;
            _tempo.SelectedIndex = Array.IndexOf(StretchTempoExtensions.All, current);
            _tempoDetail.Text = $"Unstretches {current.Detail()}.";
        }
        finally { _updating = false; }
    }
}

internal sealed class AboutPane(ISettingsHost host) : SettingsPane(host)
{
    public override string Title => "About";

    private CheckBox _autoUpdate = null!;
    private CheckBox _restartWhenReady = null!;
    private Control _updateStatus = null!;
    private ProgressBar _progress = null!;
    private System.Windows.Forms.Timer? _progressTicker;
    private bool _updating;

    public override void Build()
    {
        var header = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Width = Wide,
            Margin = new Padding(0, 0, 0, 8),
        };
        try
        {
            header.Controls.Add(new PictureBox
            {
                Image = Branding.AppIcon().ToBitmap(),
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(88, 88),
                Margin = new Padding(0, 0, 14, 0),
            });
        }
        catch (ArgumentException) { /* no icon on disk; the text below still says it all */ }

        var text = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 14, 0, 0),
        };
        text.Controls.Add(new Label
        {
            Text = "loafcat",
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif,
                            18f, FontStyle.Bold),
        });
        text.Controls.Add(new Label
        {
            Text = $"Version {Branding.Version} · MIT licensed",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        });
        header.Controls.Add(text);
        Add(header);

        Add(Divider());
        Add(Heading("This app asks for no permissions"));
        Add(Caption(
            "No elevation prompt, no accessibility settings, no keyboard hook. Typing "
            + "reactions come from a system call that returns when input last happened "
            + "— a number of milliseconds, nothing else — combined with a mouse-only "
            + "hook that has no field capable of carrying a keystroke. There is no code "
            + "path by which a key could reach loafcat, so being unable to read what "
            + "you type is structural rather than a promise.\n\n"
            + "A build check blocks any code that would change that."));

        Add(Divider());
        Add(Heading("Updates"));
        _autoUpdate = Checkbox("Install updates automatically", () =>
        {
            if (!_updating) Updater.Enabled = _autoUpdate.Checked;
        });
        Add(_autoUpdate);
        _restartWhenReady = Checkbox("Restart loafcat as soon as an update is ready", () =>
        {
            if (!_updating) Updater.RestartWhenReady = _restartWhenReady.Checked;
        });
        Add(_restartWhenReady);
        Add(Caption(
            "Off, an update waits for the next time you happen to start the app — which "
            + "for something that lives in the notification area can be a very long "
            + "time. On, the cat blinks out and comes straight back on the new version."));

        _updateStatus = Caption("");
        Add(_updateStatus);

        // Hidden until there is something to show. A progress bar that sits at zero
        // whenever the app is idle reads as a stuck download.
        _progress = new ProgressBar
        {
            Width = Wide,
            Height = 6,
            Style = ProgressBarStyle.Continuous,
            Visible = false,
            Margin = new Padding(0, 2, 0, 6),
        };
        Add(_progress);
        Add(MakeButton("Check now", () =>
        {
            _updateStatus.Text = "Checking\u2026";
            // CheckForUpdates resumes on the UI thread -- it awaits with
            // ConfigureAwait(true), and a button click already has the WinForms
            // synchronisation context -- so this assignment needs no marshalling.
            _ = Host.CheckForUpdates(m => _updateStatus.Text = m);
        }));
        Add(Caption(
            "loafcat checks GitHub a few times a day, and installs a new version only "
            + "if it carries a valid signature from the project's update key. A checksum "
            + "on its own would prove the download was not corrupted, not who made it, "
            + "so anything unsigned is reported here and never installed.\n\n"
            + "The download is verified before anything is written where the app would "
            + "find it, and the swap itself happens at startup, before there is a "
            + "window. Nothing is ever exchanged underneath a running cat."));

        Add(Divider());
        Add(Heading("Art"));
        Add(Caption(
            "Every pixel in this app is generated by tools/generate_art.py — this icon "
            + "too, composited from the same parts by tools/generate_icon.py. Nothing "
            + "is traced, sampled or derived from any existing sprite. The Windows and "
            + "macOS builds read the same generated files."));

        Add(Divider());
        Add(MakeButton("Open the repository", () =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Branding.Repo,
                    UseShellExecute = true,
                });
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception
                                          or InvalidOperationException)
            {
                // No default browser configured. Nothing useful to do about it.
            }
        }));
        Add(MakeButton("Open the log file", () =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Log.FilePath,
                    UseShellExecute = true,
                });
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception
                                          or InvalidOperationException) { }
        }));
    }

    public override void Refresh()
    {
        _updating = true;
        try
        {
            _autoUpdate.Checked = Updater.Enabled;
            _restartWhenReady.Checked = Updater.RestartWhenReady;
            _updateStatus.Text = Host.UpdateStatus;
        }
        finally { _updating = false; }

        // Polled rather than pushed. The download runs on a thread pool thread and
        // publishes one int; a timer that only exists while the window is open is a
        // great deal less to get wrong than marshalling an event out of it, and four
        // times a second is as often as a progress bar is worth redrawing.
        _progressTicker ??= new System.Windows.Forms.Timer { Interval = 250 };
        _progressTicker.Tick -= OnProgressTick;
        _progressTicker.Tick += OnProgressTick;
        _progressTicker.Start();
        OnProgressTick(this, EventArgs.Empty);
    }

    private void OnProgressTick(object? sender, EventArgs e)
    {
        int pct = Host.DownloadPercent;
        if (pct < 0)
        {
            if (_progress.Visible) _progress.Visible = false;
            return;
        }
        _progress.Visible = true;
        _progress.Value = Math.Clamp(pct, 0, 100);
        _updateStatus.Text = $"Downloading\u2026 {pct}%";
    }
}
