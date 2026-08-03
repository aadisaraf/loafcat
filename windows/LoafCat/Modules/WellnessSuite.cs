using System.Media;
using System.Runtime.Versioning;

namespace LoafCat.Modules;

/// Persisted settings for every wellness feature.
///
/// One place for the keys so a typo cannot silently create a second setting, and so
/// the settings window and the modules provably read the same thing. The key strings
/// are identical to the macOS build's UserDefaults keys — see Prefs.cs.
public sealed class WellnessSettings
{
    // Minutes. 0 means off everywhere.
    public static readonly int[] StretchOptions = [0, 10, 15, 20, 30, 45, 60, 90, 120];
    public static readonly int[] HydrationOptions = [0, 30, 45, 60, 90];
    public static readonly int[] FocusOptions = [15, 20, 25, 30, 45, 50];
    public static readonly int[] BreakOptions = [3, 5, 10, 15];
    public static readonly int[] RoundOptions = [1, 2, 3, 4, 6, 8];

    private static int Minutes(string key, int fallback) =>
        Prefs.Has(key) ? Prefs.GetInt(key) : fallback;

    /// Off by default, for the same reason hydration is — more so, in fact. A stretch
    /// break takes the middle of the screen and holds it for several seconds, so the
    /// first one arrives as an interruption rather than an invitation unless it was
    /// asked for. Settings › Wellness turns it on, and the tray menu triggers one
    /// whenever you want it.
    public int StretchMinutes
    {
        get => Minutes("wellness.stretchMinutes", 0);
        set => Prefs.Set("wellness.stretchMinutes", value);
    }

    /// Off by default: a hydration nudge nobody asked for is the fastest way to get a
    /// desktop pet uninstalled on day one.
    public int HydrationMinutes
    {
        get => Minutes("wellness.hydrationMinutes", 0);
        set => Prefs.Set("wellness.hydrationMinutes", value);
    }

    public int FocusMinutes
    {
        get => Minutes("wellness.focusMinutes", 25);
        set => Prefs.Set("wellness.focusMinutes", value);
    }

    public int BreakMinutes
    {
        get => Minutes("wellness.breakMinutes", 5);
        set => Prefs.Set("wellness.breakMinutes", value);
    }

    public int Rounds
    {
        get => Minutes("wellness.rounds", 4);
        set => Prefs.Set("wellness.rounds", value);
    }

    public bool ReminderEnabled
    {
        get => Prefs.GetBool("wellness.reminderEnabled");
        set => Prefs.Set("wellness.reminderEnabled", value);
    }

    /// "HH:MM", 24-hour.
    public string ReminderTime
    {
        get => Prefs.GetString("wellness.reminderTime");
        set => Prefs.Set("wellness.reminderTime", value);
    }

    public string ReminderText
    {
        get => Prefs.GetString("wellness.reminderText");
        set => Prefs.Set("wellness.reminderText", value);
    }

    public string PinnedNote
    {
        get => Prefs.GetString("wellness.pinnedNote");
        set => Prefs.Set("wellness.pinnedNote", value);
    }

    public bool SoundEnabled
    {
        get => Prefs.GetBool("wellness.soundEnabled");
        set => Prefs.Set("wellness.soundEnabled", value);
    }
}

/// The little that the wellness modules need to know about each other.
///
/// Passed by reference so no module has to reach into another's file, which is what
/// keeps "delete the file to remove the feature" true.
[SupportedOSPlatform("windows")]
public sealed class WellnessBus(Atlas atlas, bool isDemo)
{
    public WellnessSettings Settings { get; } = new();
    public Atlas Atlas { get; } = atlas;

    /// `--demo-timers`: every interval is compressed and forced on, so the whole
    /// sequence can be watched in a minute instead of two hours.
    public bool IsDemo { get; } = isDemo;
    public double Launched { get; } = Clock.Now;

    public BubbleModule? Bubble { get; set; }
    public StretchBreakModule? Stretch { get; set; }

    /// True while the stretch break owns the cat and the screen.
    public bool Busy => Stretch?.IsRunning ?? false;

    /// A user setting in minutes, or the compressed demo value. Null means off.
    public double? Interval(int userMinutes, double demoSeconds)
    {
        if (IsDemo) return demoSeconds;
        if (userMinutes <= 0) return null;
        return userMinutes * 60.0;
    }

    /// When a timer should first go off. In demo mode the first one is pulled well
    /// forward, so a 40-second run actually shows the sequence instead of waiting out a
    /// full compressed interval.
    public double FirstFire(double demoDelay, double interval) =>
        IsDemo ? Launched + demoDelay : Clock.Now + interval;

    /// Seconds of keyboard silence past which a reminder is dropped rather than
    /// banked. Effectively disabled in demo mode, or an unattended demo run would skip
    /// everything it is meant to be showing.
    public double AwaySeconds => IsDemo ? double.MaxValue : Atlas.Wellness.AwaySeconds;

    public void LogDemo(string message)
    {
        if (!IsDemo) return;
        Log.Line($"[demo {Clock.Now - Launched,6:0.00}s] {message}");
    }

    public static string Describe(Rect r) => r.ToString();

    public void Chime()
    {
        if (!Settings.SoundEnabled) return;
        // The system's own notification sound rather than a bundled asset. macOS gets
        // "Tink" for free from NSSound; this is the closest equivalent that respects
        // the user having turned system sounds down or off.
        try { SystemSounds.Asterisk.Play(); }
        catch (Exception) { /* no audio device; a missing chime is not worth a crash */ }
    }
}

/// Builds, registers and configures the wellness features.
///
/// Exists so `Program.cs` gains one line rather than six — a feature that needs a
/// paragraph in the entry point is a feature that causes merge conflicts.
[SupportedOSPlatform("windows")]
public sealed class WellnessSuite
{
    public WellnessBus Bus { get; }
    private readonly BubbleModule _bubble;
    private readonly StretchBreakModule _stretch;
    private readonly HydrationModule _hydration;
    private readonly PomodoroModule _pomodoro;
    private readonly MessageModule _messages;

    public WellnessSuite(Atlas atlas, CatView view, CatWindow window, ModuleRegistry registry)
    {
        bool demo = Environment.GetCommandLineArgs().Contains("--demo-timers");
        Bus = new WellnessBus(atlas, demo);

        _bubble = new BubbleModule(atlas, view, Bus);
        Bus.Bubble = _bubble;
        _stretch = new StretchBreakModule(atlas, view, window, Bus);
        Bus.Stretch = _stretch;
        _hydration = new HydrationModule(Bus);
        _pomodoro = new PomodoroModule(atlas, view, Bus);
        _messages = new MessageModule(Bus);

        // Bubble last: it renders what the others asked for this frame.
        registry.Register(_stretch);
        registry.Register(_hydration);
        registry.Register(_pomodoro);
        registry.Register(_messages);
        registry.Register(_bubble);

        if (demo)
        {
            Log.Line("""
                --demo-timers: intervals compressed
                  stretch break   5s, then every 45s
                  hydration      14s, then every 30s
                  pomodoro       10s focus / 6s break, 2 rounds, auto-started
                  reminder       8s after launch
                  away-skip      disabled for the demo
                """);
            Bus.LogDemo($"window at launch  {WellnessBus.Describe(window.Frame)}");
            _pomodoro.Start();
            _messages.ArmDemoReminder(8);
        }
    }

    /// The theme/scale reload in `Program.cs` builds a fresh `CatView`; anything
    /// holding the old one would silently draw into a surface nobody presents.
    public void Rebind(CatView view)
    {
        _bubble.Rebind(view);
        _stretch.Rebind(view);
        _pomodoro.Rebind(view);
    }

    // MARK: - the surface Settings drives

    public WellnessSettings Settings => Bus.Settings;
    public bool PomodoroRunning => _pomodoro.IsRunning;
    public string ReminderTime => Bus.Settings.ReminderTime;

    /// Re-arms every timer from whatever the settings now say.
    ///
    /// Deliberately re-arms all three rather than taking a "which one changed"
    /// argument: it costs nothing, and it means the settings window never has to know
    /// which module owns which interval.
    public void SettingsChanged()
    {
        _stretch.SettingsChanged();
        _hydration.SettingsChanged();
        _pomodoro.SettingsChanged();
    }

    /// Sets the daily reminder, normalising the time the same way the field does.
    /// Returns false when the text is not a time, so the caller can say so rather than
    /// silently storing something that will never fire.
    public bool SetReminder(string time, string text)
    {
        if (MessageModule.Parse(time) is not { } parsed) return false;
        Bus.Settings.ReminderTime = $"{parsed.H:00}:{parsed.M:00}";
        Bus.Settings.ReminderText = text;
        Bus.Settings.ReminderEnabled = true;
        return true;
    }

    public void PinNote(string? text)
    {
        string trimmed = text?.Trim() ?? "";
        Bus.Settings.PinnedNote = trimmed;
        _messages.Pin(trimmed.Length == 0 ? null : trimmed);
    }

    public void StretchNow() => _stretch.Trigger("menu");
    public void TogglePomodoro()
    {
        if (_pomodoro.IsRunning) _pomodoro.Pause(); else _pomodoro.Start();
    }
    public void ResetPomodoro() => _pomodoro.Reset();
    public void ClearReminder() => _messages.ClearReminder();
}
