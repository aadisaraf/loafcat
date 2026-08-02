using System.Runtime.Versioning;
using System.Windows.Forms;
using LoafCat.Modules;

namespace LoafCat;

/// Whether loafcat appears in the taskbar at all.
///
/// The macOS counterpart is `DockPresence`, which switches the app between `.accessory`
/// and `.regular`. Windows has no activation policy: a process is in the taskbar if it
/// owns a window that asks to be, so this is simply whether the Settings window carries
/// `ShowInTaskbar`. The cat window itself is always a tool window and never appears —
/// a pet has no business taking a taskbar slot, and one that did could be minimised,
/// which is a state this app has no meaning for.
///
/// The escape hatch matters for the same reason it does on macOS: if the tray icon is
/// ever hard to find, there has to be another way in.
public static class TaskbarPresence
{
    public static bool ShowInTaskbar
    {
        get => Prefs.GetBool("showInDock");
        set => Prefs.Set("showInDock", value);
    }
}

/// The tray icon and its menu.
///
/// The counterpart to the macOS build's `NSStatusItem` plus `MainMenu.swift`. Windows
/// has no application menu bar, so the accelerators that file exists to register (⌘, and
/// ⌘Q) have nowhere to live; the Settings window handles Ctrl+W and Escape itself, and
/// everything else is here.
///
/// One `NotifyIcon`, created once for the life of the process. The macOS build learned
/// the hard way that asking for a second status item and dropping the first does not
/// swap them — the old one is destroyed and the new one is appended at the end of the
/// bar, past the edge on a full menu bar, never to be seen again. Windows is more
/// forgiving, but the same discipline applies for the same reason: `Rebuild` replaces
/// the MENU, never the icon.
[SupportedOSPlatform("windows")]
public sealed class TrayMenu : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly Func<bool> _isCatVisible;
    private readonly Action _toggleCat;
    private readonly Action _openSettings;
    private readonly Action _centre;
    private readonly Action _quit;
    private Func<WellnessSuite?> _wellness = () => null;

    public TrayMenu(Func<bool> isCatVisible, Action toggleCat, Action openSettings,
                    Action centre, Action quit)
    {
        _isCatVisible = isCatVisible;
        _toggleCat = toggleCat;
        _openSettings = openSettings;
        _centre = centre;
        _quit = quit;

        _icon = new NotifyIcon
        {
            Icon = Branding.TrayIcon(),
            Text = "loafcat",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        // Left-clicking the tray icon opens Settings, which is what a Windows user
        // expects; right-click gets the menu. The macOS build cannot make this
        // distinction — a status item shows its menu on either button.
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _openSettings();
        };
        // The menu is rebuilt as it opens rather than on every state change, so nothing
        // has to remember to invalidate it.
        _menu.Opening += (_, _) => Rebuild();
        Rebuild();
    }

    public void BindWellness(Func<WellnessSuite?> wellness) => _wellness = wellness;

    /// Rebuilds only the menu. Safe to call as often as needed — the icon itself, and
    /// therefore its place in the tray, is untouched.
    public void Rebuild()
    {
        _menu.Items.Clear();

        var header = new ToolStripMenuItem("loafcat") { Enabled = false };
        _menu.Items.Add(header);
        _menu.Items.Add(new ToolStripSeparator());

        // Quick actions only. Everything configurable lives in Settings — a value
        // reachable from two places is a value that will eventually disagree with
        // itself, and a nested checkmark submenu was never a good way to pick a number.

        // The on switch, first, because it is the thing people come here for.
        Add(_isCatVisible() ? "Turn the cat off" : "Turn the cat on", _toggleCat);
        Add("Settings…", _openSettings);
        Add("Centre on screen", _centre);

        if (_wellness() is { } suite)
        {
            _menu.Items.Add(new ToolStripSeparator());
            Add("Stretch now", suite.StretchNow);
            Add(suite.PomodoroRunning ? "Pause pomodoro" : "Start pomodoro",
                suite.TogglePomodoro);
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(AgentModule.Shared.ListenerStatus)
        {
            Enabled = false,
        });

        _menu.Items.Add(new ToolStripSeparator());
        Add("Quit", _quit);
    }

    private void Add(string title, Action action)
    {
        var item = new ToolStripMenuItem(title);
        item.Click += (_, _) => action();
        _menu.Items.Add(item);
    }

    /// Tells the user the app is running, once, on the very first launch.
    ///
    /// The macOS build opens Settings on first run for the same reason — starting an
    /// accessory app looks like nothing happening. Windows adds a second failure mode
    /// the Mac does not have: new tray icons are hidden in the overflow chevron by
    /// default, so even the icon may not be visible until the user drags it out.
    public void SayHello()
    {
        try
        {
            _icon.BalloonTipTitle = "loafcat is running";
            _icon.BalloonTipText =
                "The cat lives in the notification area. If you cannot see it, "
                + "drag it out of the ^ overflow. Right-click for settings and quit.";
            _icon.ShowBalloonTip(8000);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            // Notifications are switched off system-wide, or focus assist is on. The
            // Settings window opens on first run regardless, so nothing is lost.
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
