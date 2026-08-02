using System.Runtime.Versioning;
using System.Windows.Forms;
using LoafCat.Interop;
using LoafCat.Modules;

namespace LoafCat;

[SupportedOSPlatform("windows")]
internal static class Program
{
    /// Single-instance handles. `Local\` rather than `Global\`: two different users on
    /// the same machine each get their own cat, which is the only reading that makes
    /// sense for a per-user desktop pet, and `Global\` would need privileges we do not
    /// ask for.
    private const string MutexName = @"Local\dev.loafcat.app";
    private const string ReopenEventName = @"Local\dev.loafcat.reopen";

    [STAThread]
    private static int Main(string[] args)
    {
        // A GUI subsystem executable that throws before the logger is up dies
        // silently — no console, no window, no file, just an exit code. That is a
        // miserable thing to debug on a machine you do not have, so the crash goes
        // next to the executable, which is somewhere we know exists.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Panic(e.ExceptionObject);
        try
        {
            return Run(args);
        }
        catch (Exception e)
        {
            Panic(e);
            return 1;
        }
    }

    private static void Panic(object? error)
    {
        string text = $"loafcat crashed at {DateTime.Now:u}\n{error}\n";
        try { Console.Error.WriteLine(text); } catch (IOException) { }
        try { Log.Warn(text); } catch (IOException) { }
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "loafcat-crash.log"), text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static int Run(string[] args)
    {
        Log.Start();
        Prefs.Load();

        // Runs without a window, so it works on a CI runner with no interactive desktop.
        if (args.Contains("--selftest")) return SelfTest.Run();

        // Before anything is on screen: a staged update renames the running executable
        // out of the way, moves the new one in, and relaunches into it. See Updater.
        if (Updater.ApplyStagedUpdate()) return 0;

        // Before the single-instance check, not after: an installed copy that is already
        // running holds its own file open, so the copy below fails, and falling through
        // to the mutex is exactly the right thing to do next. See SelfInstall.
        if (SelfInstall.Promote(args)) return 0;

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            // Launching an app is a request for it to be on. Rather than starting a
            // second cat — or, worse, doing nothing at all, which reads as the app being
            // broken — poke the copy that is already running and get out of its way.
            // This is the counterpart of `applicationShouldHandleReopen` on macOS.
            try
            {
                using var reopen = EventWaitHandle.OpenExisting(ReopenEventName);
                reopen.Set();
                Log.Line("loafcat is already running — asked it to come to the front");
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Log.Warn("loafcat is already running, but did not answer");
            }
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var controller = new CatController();
        try
        {
            controller.Start();
        }
        catch (Atlas.LoadException e)
        {
            Log.Warn(e.Message);
            MessageBox.Show(
                e.Message + "\n\nThe assets folder should sit next to LoafCat.exe.",
                "loafcat could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        using var reopenEvent = new EventWaitHandle(false, EventResetMode.AutoReset,
            ReopenEventName);
        StartReopenWatcher(reopenEvent, controller);

        Application.Run();
        controller.Shutdown();
        Log.Stop();
        return 0;
    }

    private static void StartReopenWatcher(EventWaitHandle handle, CatController controller)
    {
        var t = new Thread(() =>
        {
            while (handle.WaitOne())
            {
                controller.PostToUi(controller.HandleReopen);
            }
        })
        {
            IsBackground = true,
            Name = "loafcat.reopen",
        };
        t.Start();
    }
}

/// The app.
///
/// The counterpart to `CatController` in main.swift, and deliberately the same shape:
/// it owns the window, the atlas, the rig and the view, drives one tick, and is the
/// only thing Settings is allowed to talk to.
[SupportedOSPlatform("windows")]
public sealed class CatController : ISettingsHost
{
    private CatWindow _window = null!;
    private Atlas _atlas = null!;
    private Rig _rig = null!;
    private TrayMenu _tray = null!;

    private double _lastTick = Clock.Now;
    private Thread? _tickThread;
    private volatile bool _running;
    private int _tickPending;

    /// Typing rate over a sliding window, for kneading and overheat.
    private readonly List<double> _keyStamps = [];
    private long _lastKeyCount;
    private long _lastScrollCount;
    private const double KeyWindow = 1.5;

    /// Features live here, one file each. See CatModule.cs.
    public ModuleRegistry Modules { get; } = new();
    private WellnessSuite? _wellness;
    private Updater? _updater;

    /// Smoothed cursor velocity, in logical px/sec. Raw frame-to-frame deltas are far
    /// too noisy for a velocity threshold to be usable.
    private Pt _smoothedVelocity = Pt.Zero;
    private Pt? _lastCursor;
    private Pt _lastCursorScreen = Pt.Zero;

    /// Integer only — a fractional scale turns pixel art to mush and cannot be fixed
    /// downstream.
    private double _renderScale;
    private string _themeName = Prefs.GetString("theme", "mono");

    /// The app's on switch.
    ///
    /// Quitting is not the same thing as turning the cat off: quitting also takes away
    /// the tray icon, which is the only way back in. This is the off switch that leaves
    /// a way to switch it on again — and it really is off, not merely hidden. The tick
    /// stops, so nothing animates, no wellness timer fires and no window is resized
    /// behind your back.
    private bool _catVisible = Prefs.GetBool("showCat", true);

    public void Start()
    {
        _atlas = Atlas.Load(Assets.ThemeDir(_themeName));

        _window = new CatWindow { Modules = Modules };
        _ = _window.Handle;                     // force handle creation before any Invoke

        _renderScale = ResolveScale();
        _rig = new Rig(_atlas);
        _window.Adopt(new CatView(_atlas, _rig, _renderScale));
        _window.PlaceForFirstRun();

        _tray = new TrayMenu(
            isCatVisible: () => _catVisible,
            toggleCat: () => SetCatVisible(!_catVisible),
            openSettings: OpenSettings,
            centre: CentreCat,
            quit: Quit);

        InputTelemetry.Start();
        _lastKeyCount = InputTelemetry.KeyCount;
        _lastScrollCount = InputTelemetry.ScrollCount;

        RegisterModules();
        _tray.BindWellness(() => _wellness);

        // Announced through the tray rather than the cat: an update is news about the
        // app, not something the cat did, and the speech bubble is the cat's voice.
        _updater = new Updater(message => PostToUi(() =>
        {
            _tray.Notify("loafcat", message);
            _tray.Rebuild();
        }));
        _tray.BindUpdater(() => _updater);
        _updater.Start();

        _tray.Rebuild();

        _window.SetCatVisible(_catVisible);
        StartTickLoop();

        // `--settings` opens the window straight away, and so does the very first
        // launch. Starting a tray app looks like nothing happening: no window, no
        // taskbar button, just one more small icon in an area that is probably
        // collapsed behind a chevron. Showing settings once is how the first run says
        // "I am running, here is where I live, here is how to turn me off."
        bool firstRun = !Prefs.GetBool("hasLaunched");
        Prefs.Set("hasLaunched", true);
        if (firstRun || Environment.GetCommandLineArgs().Contains("--settings"))
        {
            OpenSettings();
            if (firstRun) _tray.SayHello();
        }

        var (w, h) = CatView.PanelSize(_atlas, _renderScale);
        uint dpi = Screens.DpiFor(_window.Handle);
        Log.Line($"""
            loafcat running
              theme   {_themeName} -- {_atlas.Parts.Count} parts, {(int)_atlas.Canvas}px @{(int)_renderScale}x
              window  layered topmost tool window, {w}x{h} at ({(int)_window.Frame.X}, {(int)_window.Frame.Y}), {dpi}dpi
              input   permission-free: GetLastInputInfo + a mouse-only hook{(InputTelemetry.HookInstalled ? "" : " (hook unavailable)")}
              log     {Log.FilePath}
            Quit from the tray cat.
            """);
    }

    /// The starting render scale.
    ///
    /// The app is per-monitor DPI aware, which means Windows never scales the window for
    /// us — one unit we draw is one physical pixel. That is exactly what pixel art
    /// wants, but it also means the cat does not grow on a high-DPI display the way an
    /// ordinary app would, so a 2x cat on a 150% laptop panel would come out noticeably
    /// smaller than it looks on a Mac. Picking the first-run scale from the DPI puts it
    /// back, and after that the user's choice in Settings is authoritative.
    private double ResolveScale()
    {
        if (Prefs.Has("scale")) return Prefs.GetDouble("scale", 2);
        uint dpi = Screens.DpiFor(_window.Handle);
        double suggested = MathX.Clamp(MathX.Round(dpi / 96.0 * 2), 2, 4);
        Prefs.Set("scale", suggested);
        Log.Line($"first run: {dpi}dpi -> starting at {(int)suggested}x");
        return suggested;
    }

    /// Every feature is registered here and nowhere else. Adding one should be a single
    /// line plus a single new file under Modules/.
    private void RegisterModules()
    {
        Modules.Register(new DragModule(_window, Modules));
        Modules.Register(new TypingModule());
        Modules.Register(new HuntModule());
        Modules.Register(new PettingModule());
        Modules.Register(new ScrollModule());
        Modules.Register(AgentModule.Shared);
        _wellness = new WellnessSuite(_atlas, _window.View!, _window, Modules);
    }

    // MARK: - the tick

    /// One 120Hz tick drives everything: cursor tracking, typing rate, every module and
    /// the frame.
    ///
    /// The timing thread does nothing but wait and post; the work happens on the UI
    /// thread, so modules can touch the window and Settings without marshalling. A
    /// WinForms `Timer` would have been the obvious choice and cannot do this — it is
    /// driven by WM_TIMER, whose resolution is the ~15ms system tick, so it tops out
    /// near 64Hz and jitters badly.
    private void StartTickLoop()
    {
        _running = true;
        _tickThread = new Thread(TickLoop)
        {
            IsBackground = true,
            Name = "loafcat.tick",
            Priority = ThreadPriority.AboveNormal,
        };
        _tickThread.Start();
    }

    private void TickLoop()
    {
        const double hz = 120.0;
        IntPtr timer = Win32.CreateWaitableTimerEx(
            IntPtr.Zero, null, Win32.CreateWaitableTimerHighResolution, Win32.TimerAllAccess);

        // Negative means relative, in 100ns units. -83333 is 8.3333ms.
        long due = -(long)(10_000_000 / hz);
        if (timer != IntPtr.Zero)
        {
            Win32.SetWaitableTimer(timer, ref due, (int)Math.Round(1000 / hz),
                IntPtr.Zero, IntPtr.Zero, false);
        }

        try
        {
            while (_running)
            {
                if (timer != IntPtr.Zero) Win32.WaitForSingleObject(timer, 100);
                else Thread.Sleep(8);   // pre-1803: coarser, but the tick is dt-driven

                // Never queue a second tick behind one that has not run. A modal dialog
                // or a slow frame would otherwise build a backlog that all fires at once
                // the moment the UI thread frees up.
                if (Interlocked.CompareExchange(ref _tickPending, 1, 0) != 0) continue;
                if (!PostToUi(Tick)) Interlocked.Exchange(ref _tickPending, 0);
            }
        }
        finally
        {
            if (timer != IntPtr.Zero) Win32.CloseHandle(timer);
        }
    }

    /// Runs an action on the UI thread. Returns false when the window is gone, which is
    /// the normal state during shutdown.
    public bool PostToUi(Action action)
    {
        try
        {
            if (!_window.IsHandleCreated || _window.IsDisposed) return false;
            _window.BeginInvoke(action);
            return true;
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    private void Tick()
    {
        try
        {
            TickBody();
        }
        finally
        {
            Interlocked.Exchange(ref _tickPending, 0);
        }
    }

    private void TickBody()
    {
        double now = Clock.Now;
        double dt = Math.Min(now - _lastTick, 0.1);
        _lastTick = now;
        // Off means off. `_lastTick` is still advanced above so the first frame back is a
        // normal one rather than a several-second dt fired through the springs.
        if (!_catVisible || _window.View is not { } view) return;

        InputTelemetry.Poll();

        Win32.GetCursorPos(out var cursorPos);
        var mouse = new Pt(cursorPos.X, cursorPos.Y);
        if (Math.Abs(mouse.X - _lastCursorScreen.X) > 0.5 ||
            Math.Abs(mouse.Y - _lastCursorScreen.Y) > 0.5)
        {
            InputTelemetry.NoteCursorMoved();
        }
        _lastCursorScreen = mouse;

        var frame = _window.Frame;

        // --- proximity --------------------------------------------------------
        // Windows hit-tests clicks against the composed alpha itself, so unlike the
        // macOS build there is nothing to toggle here. This is only the dilated
        // proximity test the modules read as `CursorOnCat`.
        var client = new Pt(mouse.X - frame.X, mouse.Y - frame.Y);
        bool onCat = frame.Inset(-8, -8).Contains(mouse.X, mouse.Y) && view.IsOnCat(client);

        // --- typing rate ------------------------------------------------------
        long keys = InputTelemetry.KeyCount;
        long delta = keys - _lastKeyCount;
        _lastKeyCount = keys;
        if (delta > 0 && delta < 100)
        {
            for (long i = 0; i < delta; i++) _keyStamps.Add(now);
        }
        _keyStamps.RemoveAll(t => now - t > KeyWindow);

        // --- cursor, relative to the cat's centre, in LOGICAL pixels ----------
        // The window's centre IS the cat's centre: the transparent bubble margin is
        // symmetric precisely so this stays true. `EffectiveScale` folds in the stretch
        // break's magnification, without which tracking would saturate the moment the
        // cat grew.
        double unit = view.EffectiveScale;
        var cursor = new Pt(
            (mouse.X - frame.MidX) / unit,
            // No flip. Windows screen space is y-down and so is the atlas — this is one
            // of the few places the port is simpler than the original, which has to
            // negate here because AppKit's screen is y-up.
            (mouse.Y - frame.MidY) / unit);

        // Exponential moving average. Raw per-frame deltas at 120Hz are far too noisy
        // for any velocity threshold to be usable against.
        if (_lastCursor is { } prev && dt > 0)
        {
            double vx = (cursor.X - prev.X) / dt;
            double vy = (cursor.Y - prev.Y) / dt;
            const double a = 0.25;
            _smoothedVelocity.X += (vx - _smoothedVelocity.X) * a;
            _smoothedVelocity.Y += (vy - _smoothedVelocity.Y) * a;
        }
        _lastCursor = cursor;

        long scroll = InputTelemetry.ScrollCount;
        long scrollDelta = scroll - _lastScrollCount;
        _lastScrollCount = scroll;

        var ctx = new TickContext
        {
            Dt = dt,
            Cursor = cursor,
            CursorVelocity = _smoothedVelocity,
            CursorOnCat = onCat,
            KeysPerSecond = _keyStamps.Count / KeyWindow,
            // Guard against a huge burst after a stall.
            ScrollDelta = scrollDelta is > 0 and < 1000 ? (uint)scrollDelta : 0,
            SecondsSinceKey = InputTelemetry.SecondsSinceKey,
            Frame = frame,
            Scale = unit,
        };

        var outv = Modules.Update(in ctx);
        _rig.SetSquash(outv.Squash);
        _rig.Update(dt, cursor, isBlinkSuppressed: Modules.State == CatState.Sleeping);
        _window.Present();

        // Cheap, and only every few seconds: a full-screen app coming and going can
        // leave any topmost window behind it in the z-order.
        if (++_topmostCounter >= 600)
        {
            _topmostCounter = 0;
            _window.ReassertTopmost();
        }
    }

    private int _topmostCounter;

    // MARK: - lifecycle

    /// Clicking the tray icon, or launching the app again while it is already running.
    ///
    /// Launching an app is a request for it to be on, so this turns the cat back on
    /// before anything else — that is what makes "open loafcat and it starts" literally
    /// true whether or not it was already running.
    public void HandleReopen()
    {
        if (!_catVisible) SetCatVisible(true);
        else OpenSettings();
    }

    private void OpenSettings() => SettingsWindow.Show(this);

    private void Quit()
    {
        Shutdown();
        Application.ExitThread();
    }

    public void Shutdown()
    {
        if (!_running) return;
        _running = false;
        _tickThread?.Join(200);
        AgentModule.Shared.CleanUp();
        _updater?.Dispose();
        InputTelemetry.Stop();
        Prefs.Flush();
        _tray.Dispose();
        _window.Dispose();
    }

    /// Rebuilds the view for a new theme or scale, keeping the cat where it stands.
    private void Reload()
    {
        Atlas loaded;
        try
        {
            loaded = Atlas.Load(Assets.ThemeDir(_themeName));
        }
        catch (Exception e) when (e is Atlas.LoadException or IOException)
        {
            Log.Warn($"could not load theme '{_themeName}': {e.Message}");
            return;
        }
        _atlas = loaded;
        _rig = new Rig(_atlas);
        _window.Adopt(new CatView(_atlas, _rig, _renderScale));
        _wellness?.Rebind(_window.View!);
        _tray.Rebuild();
    }

    // MARK: - ISettingsHost

    public string CurrentTheme => _themeName;
    public double CurrentScale => _renderScale;
    public WellnessSuite? WellnessSuite => _wellness;

    public string UpdateStatus => _updater switch
    {
        { StagedAndReady: true, AvailableVersion: { } v } =>
            $"loafcat {v} is ready, and starts the next time you open the app.",
        { AvailableVersion: { } v } => $"loafcat {v} is available.",
        _ => $"loafcat {Branding.Version}.",
    };

    public async Task CheckForUpdates(Action<string> report)
    {
        if (_updater is not { } updater) { report("Updates are unavailable."); return; }
        await updater.CheckAsync(quiet: false).ConfigureAwait(true);
        report(UpdateStatus);
    }
    public bool IsCatVisible => _catVisible;

    public void ApplyTheme(string theme)
    {
        _themeName = theme;
        Prefs.Set("theme", theme);
        Reload();
    }

    public void ApplyScale(double scale)
    {
        _renderScale = scale;
        Prefs.Set("scale", scale);
        Reload();
    }

    public void ApplyDragFeel(DragFeel feel)
    {
        Prefs.Set("dragFeel", feel.Raw());
        Reload();   // modules re-read their tuning when the rig is rebuilt
    }

    public void CentreCat() => _window.Centre();

    public void SetCatVisible(bool visible)
    {
        _catVisible = visible;
        Prefs.Set("showCat", visible);
        if (visible) _lastTick = Clock.Now;
        _window.SetCatVisible(visible);
        _tray.Rebuild();
        SettingsWindow.RefreshPanes();
    }
}
