using System.ComponentModel;
using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace LoafCat;

/// The window a downloaded `loafcat.exe` shows while it installs itself.
///
/// There was none, and that was the bug. Installing was a silent file copy whose only
/// announcement was a tray balloon from the copy it started — and a balloon is the one
/// piece of Windows UI the system is free to throw away, which Focus Assist, Do Not
/// Disturb and a notifications-off setting all do. So the good case looked like nothing
/// happening, and the case where it declined to install at all looked *exactly the
/// same*. Nothing about a double-clicked executable told the user which had occurred.
///
/// This is the counterpart of dragging a `.app` to Applications on macOS, which needs
/// no window because the Finder already is one. Windows hands you a loose binary and no
/// convention at all, so the binary has to account for itself.
///
/// Deliberately not a wizard. There is nothing to choose — no directory to pick, no
/// components, no licence to agree to — so there are no Next buttons. It reports what
/// it is doing while it does it, and then it offers to open what it installed.
[SupportedOSPlatform("windows")]
internal static class InstallWindow
{
    /// Returns true when this process is done — it either handed over to the installed
    /// copy or the user dismissed the window. False means the download should go on and
    /// be the cat itself, which is what happens when installing failed.
    internal static bool Run(InstallPlan plan)
    {
        if (plan == InstallPlan.None) return false;

        // CI has no one to press the button. It drives the same Install underneath.
        if (Environment.GetCommandLineArgs().Contains(SelfInstall.UnattendedFlag))
            return Unattended(plan);

        var form = new InstallForm(plan);
        Application.Run(form);
        return form.Finished;
    }

    private static bool Unattended(InstallPlan plan)
    {
        if (plan is InstallPlan.Fresh or InstallPlan.Replace)
        {
            try
            {
                SelfInstall.Install(null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                        or Win32Exception or NotSupportedException)
            {
                Log.Warn($"could not install to {SelfInstall.Root} ({e.Message}) — running in place");
                return false;
            }
        }
        else
        {
            Log.Line($"install  nothing to do: {plan.ToString().ToLowerInvariant()}");
            if (SelfInstall.WakeRunningCopy()) return true;
        }

        try
        {
            SelfInstall.StartInstalled();
            return true;
        }
        catch (Exception e) when (e is Win32Exception or IOException)
        {
            Log.Warn($"could not start the installed copy ({e.Message})");
            return false;
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class InstallForm : Form
{
    private readonly InstallPlan _plan;
    private readonly PictureBox _art = new();
    private readonly Label _headline = new();
    private readonly Label _body = new();
    private readonly ProgressBar _bar = new();
    private readonly Button _primary = new();
    private readonly Button _secondary = new();
    private Action _primaryAction = () => { };

    /// False only when installing failed, which is the one case where the copy sitting
    /// in Downloads should carry on and be the cat. An app that refuses to start
    /// because it could not tidy itself away would be worse than one with an untidy
    /// name.
    internal bool Finished { get; private set; } = true;

    internal InstallForm(InstallPlan plan)
    {
        _plan = plan;

        Text = "loafcat";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        ClientSize = new Size(470, 206);
        ShowInTaskbar = true;
        BackColor = SystemColors.Window;
        try { Icon = Branding.AppIcon(); } catch (ArgumentException) { }

        // The cat itself, composited from the atlas at a whole scale like everything
        // else this app draws. Not the .ico shrunk to fit: that is a bilinear resample
        // of pixel art, which is the exact mush the art pipeline exists to prevent, and
        // it would be the first thing a new user ever saw of it.
        _art.SetBounds(24, 24, 96, 96);
        _art.SizeMode = PictureBoxSizeMode.CenterImage;
        _art.Image = CatArt();
        Controls.Add(_art);

        _headline.SetBounds(140, 26, 306, 24);
        _headline.Font = new Font(Font.FontFamily, Font.Size + 1.5f, FontStyle.Bold);
        _headline.AutoSize = false;
        Controls.Add(_headline);

        _body.SetBounds(140, 54, 306, 64);
        _body.ForeColor = SystemColors.GrayText;
        _body.AutoSize = false;
        Controls.Add(_body);

        _bar.SetBounds(140, 124, 306, 10);
        _bar.Visible = false;
        Controls.Add(_bar);

        _primary.SetBounds(316, 158, 130, 30);
        _primary.Click += (_, _) => _primaryAction();
        _primary.Visible = false;
        Controls.Add(_primary);

        _secondary.SetBounds(168, 158, 140, 30);
        _secondary.Click += (_, _) => BeginInstall();
        _secondary.Visible = false;
        Controls.Add(_secondary);
    }

    private static Image? CatArt()
    {
        // 2x, because the window is laid out in logical pixels and 48 would be a stamp.
        string theme = Prefs.GetString("theme", "mono");
        return ThemeThumbnail.Image(theme, 2) ?? ThemeThumbnail.Image("mono", 2);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Started from Explorer, so this process is granted the foreground — but the
        // window it puts there has to be this one and not, a moment later, the cat.
        Activate();

        if (_plan is InstallPlan.Fresh or InstallPlan.Replace) BeginInstall();
        else ShowNothingToDo();
    }

    private void BeginInstall()
    {
        _headline.Text = _plan == InstallPlan.Replace
            ? $"Updating loafcat to {Branding.Version}"
            : "Installing loafcat";
        _body.Text = SelfInstall.Root;
        _bar.Style = ProgressBarStyle.Continuous;
        _bar.Value = 0;
        _bar.Visible = true;
        _primary.Visible = false;
        _secondary.Visible = false;
        // No closing it mid-copy: the file being written is the one the Start menu
        // entry is about to point at.
        ControlBox = false;

        // Constructed here, on the UI thread, which is what makes Progress<T> post its
        // callbacks back to it rather than running them on the worker.
        var progress = new Progress<SelfInstall.Step>(Report);
        var worker = new Thread(() =>
        {
            Exception? failure = null;
            try
            {
                SelfInstall.Install(progress);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                        or Win32Exception or NotSupportedException)
            {
                failure = e;
            }
            BeginInvoke(() =>
            {
                ControlBox = true;
                if (failure is null) ShowDone(); else ShowFailure(failure);
            });
        })
        {
            IsBackground = true,
            Name = "loafcat.install",
        };
        worker.Start();
    }

    private void Report(SelfInstall.Step step)
    {
        _body.Text = $"{step.Status}\n{SelfInstall.Root}";
        if (step.Percent < 0)
        {
            // Waiting on the running copy to exit, which takes as long as it takes.
            _bar.Style = ProgressBarStyle.Marquee;
        }
        else
        {
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = Math.Clamp(step.Percent, 0, 100);
        }
    }

    private void ShowDone()
    {
        _headline.Text = "loafcat is installed";
        _body.Text = "It is in the Start menu now — press Start and type loafcat.\n\n"
                   + "The file you downloaded has done its job and can be deleted.";
        _bar.Visible = false;
        Offer("Open loafcat", OpenInstalled);
    }

    private void ShowNothingToDo()
    {
        string installed = SelfInstall.InstalledVersion() ?? "?";
        _bar.Visible = false;

        if (_plan == InstallPlan.Same)
        {
            _headline.Text = $"loafcat {installed} is already installed";
            _body.Text = "This download is the same version, so there is nothing to "
                       + $"install.\n\n{SelfInstall.Root}";
        }
        else
        {
            _headline.Text = "A newer loafcat is already installed";
            _body.Text = $"Version {installed} is installed and this download is "
                       + $"{Branding.Version}, so installing it would put you back a "
                       + "version.";
            _secondary.Text = $"Install {Branding.Version}";
            _secondary.Visible = true;
        }
        Offer("Open loafcat", OpenWhateverIsInstalled);
    }

    private void ShowFailure(Exception e)
    {
        Log.Warn($"could not install to {SelfInstall.Root} ({e.Message}) — running in place");
        _headline.Text = "loafcat could not install itself";
        _body.Text = $"{e.Message}\n\nIt will run from where you downloaded it instead.";
        _bar.Visible = false;
        Finished = false;
        Offer("OK", Close);
    }

    private void Offer(string label, Action action)
    {
        _primaryAction = action;
        _primary.Text = label;
        _primary.Visible = true;
        AcceptButton = _primary;
        _primary.Focus();
    }

    private void OpenInstalled()
    {
        try
        {
            SelfInstall.StartInstalled();
        }
        catch (Exception e) when (e is Win32Exception or IOException)
        {
            // It is installed but would not start. Running in place is still a cat.
            Log.Warn($"could not start the installed copy ({e.Message})");
            Finished = false;
        }
        Close();
    }

    /// For when there was nothing to install: the copy that is already running is the
    /// one to bring forward, and only if nothing answers is there anything to start.
    private void OpenWhateverIsInstalled()
    {
        if (SelfInstall.WakeRunningCopy()) Close();
        else OpenInstalled();
    }
}
