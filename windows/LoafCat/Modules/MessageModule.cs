using System.Globalization;
using System.Runtime.Versioning;

namespace LoafCat.Modules;

/// Two ways to make the cat carry a message: one that arrives at a time, and one that
/// stays until it is taken down.
///
/// Both go through the same bubble, so a note and a reminder can never overlap; the
/// reminder wins while it is up and the note comes back underneath it.
///
/// The macOS file also carries `promptForReminder` and `promptForNote`, two NSAlert
/// dialogs left over from when the menu bar owned these settings. Nothing calls them
/// there any more — Settings does both — so they are not ported. The parser they
/// shared with the settings field is, because that one IS still shared.
[SupportedOSPlatform("windows")]
public sealed class MessageModule(WellnessBus bus) : ICatModule
{
    public string Id => "message";

    private readonly WellnessBus _bus = bus;

    /// The day the reminder last fired on, so a 120Hz tick cannot fire it 7200 times
    /// during the minute it is due.
    private int _firedOnDay = -1;

    private double _demoFireAt = Clock.Never;
    private double _meowStart = -1;
    private const double MeowDuration = 1.4;
    private const double ShowSeconds = 8;

    /// `--demo-timers` cannot wait for a wall-clock minute to roll around.
    public void ArmDemoReminder(double seconds)
    {
        _demoFireAt = Clock.Now + seconds;
        if (_bus.Settings.ReminderText.Length == 0)
        {
            _bus.Settings.ReminderText = "Stand up and look out of a window.";
        }
    }

    public ModuleOutput Update(in TickContext ctx)
    {
        double now = Clock.Now;

        if (now >= _demoFireAt)
        {
            _demoFireAt = Clock.Never;
            Fire(_bus.Settings.ReminderText, "demo");
        }
        else if (_bus.Settings.ReminderEnabled && !_bus.Busy)
        {
            CheckClock(ctx.SecondsSinceKey);
        }

        if (_meowStart < 0) return ModuleOutput.None;
        double p = (now - _meowStart) / MeowDuration;
        if (p >= 1) { _meowStart = -1; return ModuleOutput.None; }

        // The meow: a head-forward lunge and a wobble. There is no open-mouth cell in
        // the rig and inventing one would need art in three themes, so the gesture
        // carries it.
        var outv = new ModuleOutput();
        double t = p;
        double envelope = (1 - t) * (1 - t);
        outv.Squash = 1 + envelope * 0.07 * Math.Sin(Math.PI * 4 * t);
        outv.Offset.Y = -envelope * _bus.Atlas.Wellness.BobHeight * 0.8
                        * Math.Sin(Math.PI * 2 * t);
        return outv;
    }

    private void CheckClock(double secondsSinceKey)
    {
        if (Parse(_bus.Settings.ReminderTime) is not { } target) return;

        // The one place the app asks for wall-clock time rather than the monotonic
        // Clock: "half past nine" is a question about the user's calendar, not about
        // how long the process has been up.
        var date = DateTime.Now;
        int dayKey = date.Year * 1000 + date.DayOfYear;
        if (date.Hour != target.H || date.Minute != target.M || _firedOnDay == dayKey) return;
        _firedOnDay = dayKey;

        if (secondsSinceKey > _bus.AwaySeconds)
        {
            // Marked as fired, then dropped: a reminder is about a moment, and
            // replaying it an hour later is worse than not having it.
            _bus.LogDemo($"reminder SKIPPED, away {secondsSinceKey:0}s");
            return;
        }
        Fire(_bus.Settings.ReminderText, $"clock {_bus.Settings.ReminderTime}");
    }

    private void Fire(string text, string why)
    {
        string message = text.Length == 0 ? "Reminder!" : text;
        _bus.Bubble?.Say(message, ShowSeconds);
        _bus.Chime();
        _meowStart = Clock.Now;
        _bus.LogDemo($"reminder ({why}) \"{message}\"");
    }

    // MARK: - pinned note

    public void Pin(string? text)
    {
        _bus.Bubble?.Pin(text);
        _bus.LogDemo(text is null ? "note cleared" : $"note pinned: \"{text}\"");
    }

    public void ClearReminder()
    {
        _bus.Settings.ReminderEnabled = false;
        _bus.Settings.ReminderTime = "";
        // Without this, re-enabling a reminder for a time that has already passed today
        // would be blocked by the day guard until midnight.
        _firedOnDay = -1;
    }

    // MARK: - parsing

    /// Public, not private: the settings window validates its time field with the same
    /// parser the tick fires on, so the two cannot come to disagree about what counts
    /// as a time.
    public static (int H, int M)? Parse(string s)
    {
        string[] parts = s.Trim().Split(':');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int h))
            return null;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int m))
            return null;
        if (h is < 0 or > 23 || m is < 0 or > 59) return null;
        return (h, m);
    }

    public static string Normalise(string s) =>
        Parse(s) is { } p ? $"{p.H:00}:{p.M:00}" : s;

    public static string DefaultTimeString() => DateTime.Now.ToString("HH:mm",
        CultureInfo.InvariantCulture);
}
