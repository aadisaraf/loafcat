using System.Runtime.Versioning;

namespace LoafCat.Modules;

/// Focus blocks with a countdown the cat carries beside it.
///
/// The plate is drawn from the same 9-slice and the same pixel font as the speech
/// bubble, which is the only reason a live-updating timer can sit next to pixel art
/// without looking pasted on: it is made of the same pixels, at the same 1x, and
/// magnified by the same integer factor. A `DrawString` here would be anti-aliased
/// text floating over a pixel cat.
[SupportedOSPlatform("windows")]
public sealed class PomodoroModule : ICatModule
{
    public string Id => "pomodoro";

    private readonly Atlas _atlas;
    private CatView _view;
    private readonly WellnessBus _bus;

    private enum Mode { Stopped, Focus, Rest, Done }
    private Mode _mode = Mode.Stopped;
    private bool _running;
    private double _remaining;
    private double _lastTick = Clock.Now;
    private int _round;

    private double _flourishStart = -1;

    /// Only re-rasterise when the visible digits change, not 120 times a second.
    private string? _shownLabel;

    public bool IsRunning => _running && _mode is Mode.Focus or Mode.Rest;

    public PomodoroModule(Atlas atlas, CatView view, WellnessBus bus)
    {
        _atlas = atlas;
        _view = view;
        _bus = bus;
    }

    public void Rebind(CatView view)
    {
        _view = view;
        _shownLabel = null;
    }

    // MARK: - control

    public void Start()
    {
        if (_mode is Mode.Stopped or Mode.Done)
        {
            _round = 0;
            BeginFocus();
        }
        _running = true;
        _lastTick = Clock.Now;
        _bus.LogDemo($"pomodoro START round 1/{Rounds} focus {(int)_remaining}s");
    }

    public void Pause()
    {
        _running = false;
        _bus.LogDemo($"pomodoro PAUSE at {Label(_remaining)}");
    }

    public void Reset()
    {
        _running = false;
        _mode = Mode.Stopped;
        _round = 0;
        _remaining = 0;
        ClearPlate();
        _bus.LogDemo("pomodoro RESET");
    }

    /// A duration change mid-block would be confusing; it applies from the next one.
    public void SettingsChanged()
    {
        if (_mode is Mode.Stopped or Mode.Done) ClearPlate();
    }

    private double FocusSeconds => _bus.IsDemo ? 10 : _bus.Settings.FocusMinutes * 60.0;
    private double RestSeconds => _bus.IsDemo ? 6 : _bus.Settings.BreakMinutes * 60.0;
    private int Rounds => _bus.IsDemo ? 2 : Math.Max(_bus.Settings.Rounds, 1);

    private void BeginFocus()
    {
        _round++;
        _mode = Mode.Focus;
        _remaining = FocusSeconds;
        _flourishStart = Clock.Now;
        _bus.Bubble?.Say($"Round {_round} of {Rounds}. Focus!", 3);
    }

    private void BeginRest(bool away)
    {
        _mode = Mode.Rest;
        _remaining = RestSeconds;
        if (away)
        {
            // The break still runs down, but there is nobody to stretch at.
            _bus.LogDemo("pomodoro break: skipping the stretch, user away");
        }
        else
        {
            _bus.Stretch?.Trigger("pomodoro break");
        }
        _bus.Chime();
    }

    // MARK: - tick

    public ModuleOutput Update(in TickContext ctx)
    {
        double now = Clock.Now;
        // Wall-clock, not accumulated dt: dt is clamped to 0.1s per frame, so a
        // sleeping laptop would leave a 25-minute block hours behind.
        double step = MathX.Clamp(now - _lastTick, 0, 5);
        _lastTick = now;

        if (_running && _mode is Mode.Focus or Mode.Rest)
        {
            _remaining -= step;
            if (_remaining <= 0)
            {
                if (_mode == Mode.Focus)
                {
                    _bus.LogDemo($"pomodoro focus {_round}/{Rounds} done -> break");
                    BeginRest(ctx.SecondsSinceKey > _bus.AwaySeconds);
                }
                else if (_round >= Rounds)
                {
                    _mode = Mode.Done;
                    _running = false;
                    _remaining = 0;
                    _bus.Bubble?.Say($"{Rounds} rounds done. Nice.", 6);
                    _bus.LogDemo("pomodoro COMPLETE");
                }
                else
                {
                    _bus.LogDemo($"pomodoro break done -> focus {_round + 1}/{Rounds}");
                    BeginFocus();
                }
            }
        }

        RenderPlate();

        // The plate belongs to the cat's chrome, not to the stretch break's screen —
        // hidden wholesale by CatView while a stretch is running.
        if (_mode != Mode.Focus || _flourishStart < 0) return ModuleOutput.None;
        double d = _atlas.Wellness.FlourishDuration;
        double p = (now - _flourishStart) / d;
        if (p >= 1) { _flourishStart = -1; return ModuleOutput.None; }

        // "Getting to work": one decisive crouch-and-up, not a wiggle.
        var outv = new ModuleOutput();
        double t = p;
        double envelope = (1 - t) * (1 - t);
        outv.Squash = 1 - envelope * 0.10 * Math.Cos(Math.PI * 3 * t);
        outv.Offset.Y = -envelope * Math.Sin(Math.PI * 2 * t) * _atlas.Wellness.BobHeight * 0.7;
        return outv;
    }

    // MARK: - the plate

    private static string Label(double seconds)
    {
        int s = Math.Max((int)Math.Ceiling(seconds), 0);
        return $"{s / 60:00}:{s % 60:00}";
    }

    private void RenderPlate()
    {
        string? want = _mode switch
        {
            Mode.Focus or Mode.Rest => Label(_remaining),
            _ => null,
        };
        if (want == _shownLabel) return;
        bool firstOfBlock = _shownLabel is null;
        _shownLabel = want;

        if (want is not { } text || _atlas.Bubble is not { } bubble) { ClearPlate(); return; }
        if (bubble.Render(text, withTail: false) is not { } r) { ClearPlate(); return; }

        var w = _atlas.Wellness;
        // Right-aligned against the cat so the plate grows leftwards into the margin,
        // keeping its inner edge still while the digits change width.
        var origin = new Pt(
            MathX.Round(w.TimerRight - r.Image.Width),
            MathX.Round(w.TimerCY - r.Image.Height / 2.0));
        _view.SetAux("pomodoro", r.Image, origin);
        if (firstOfBlock)
        {
            _bus.LogDemo($"timer    plate {r.Image.Width}x{r.Image.Height}px " +
                         $"at atlas ({(int)origin.X}, {(int)origin.Y}) showing {text}");
        }
    }

    private void ClearPlate()
    {
        _shownLabel = null;
        _view.SetAux("pomodoro", null, Pt.Zero);
    }
}
