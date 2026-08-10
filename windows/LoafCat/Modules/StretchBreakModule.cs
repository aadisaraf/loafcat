using System.Runtime.Versioning;
using LoafCat.Interop;

namespace LoafCat.Modules;

/// Every so often the cat swells to fill the middle of the screen and stretches, which
/// is an invitation to do the same.
///
/// The window has to grow because the cat does: it is sized to the sprite, so
/// magnifying inside a fixed frame would just clip. The frame, the magnification and
/// the pose all animate off one wall-clock phase machine rather than accumulated `dt`,
/// so a stalled frame or a lid close cannot leave the cat stuck at 20x.
[SupportedOSPlatform("windows")]
public sealed class StretchBreakModule : ICatModule
{
    public string Id => "stretch";

    private readonly Atlas _atlas;
    private CatView _view;
    private readonly CatWindow _window;
    private readonly WellnessBus _bus;

    private enum Phase { Waiting, Growing, Stretching, Settling, Shrinking }
    private Phase _phase = Phase.Waiting;
    private double _phaseStart;
    private double _nextFire;

    /// Restored verbatim when the break ends. Saved before anything moves.
    private Rect _savedFrame;
    private Rect _targetFrame;
    private double _targetZoom = 1;

    public bool IsRunning => _phase != Phase.Waiting;

    public StretchBreakModule(Atlas atlas, CatView view, CatWindow window, WellnessBus bus)
    {
        _atlas = atlas;
        _view = view;
        _window = window;
        _bus = bus;
        SettingsChanged();
    }

    public void Rebind(CatView view) => _view = view;

    /// Restarts the countdown. Called when Settings changes the interval, so a user
    /// picking "10 min" waits ten minutes rather than inheriting an old deadline.
    public void SettingsChanged()
    {
        if (Interval is not { } iv) { _nextFire = Clock.Never; return; }
        _nextFire = _bus.FirstFire(5, iv);
    }

    private double? Interval => _bus.Interval(
        _bus.Settings.StretchMinutes, _bus.IsDemo ? 45 : 0);

    /// Starts a break now. Safe to call from the tray menu or from the pomodoro.
    public void Trigger(string reason)
    {
        if (IsRunning) return;
        Begin(reason);
    }

    public ModuleOutput Update(in TickContext ctx)
    {
        double now = Clock.Now;
        var w = _atlas.Wellness;

        if (_phase == Phase.Waiting)
        {
            if (Interval is not { } iv || now < _nextFire) return ModuleOutput.None;
            // Held, NOT skipped and NOT rescheduled: this break magnifies the cat to
            // the size of the screen, and doing that over a film — or over a call, or
            // a presentation you are giving — is the worst thing this app could do.
            // Leaving `_nextFire` where it is means the break happens as soon as the
            // full-screen window goes away, which is when it was wanted anyway.
            if (CatStage.Shared.FullscreenBusy) return ModuleOutput.None;
            if (ctx.SecondsSinceKey > _bus.AwaySeconds)
            {
                // Skip rather than bank it. Coming back from lunch to six queued
                // stretch breaks firing in a row is worse than missing all six.
                _nextFire = now + iv;
                _bus.LogDemo($"stretch SKIPPED, away {ctx.SecondsSinceKey:0}s");
                return ModuleOutput.None;
            }
            Begin("timer");
            return Claim();
        }

        double elapsed = now - _phaseStart;
        switch (_phase)
        {
            case Phase.Growing:
            {
                double p = Math.Min(elapsed / Math.Max(w.GrowDuration, 0.001), 1);
                double e = EaseInOut(p);
                _window.SetFrame(Lerp(_savedFrame, _targetFrame, e));
                _view.SetZoom(1 + (_targetZoom - 1) * e);
                if (p >= 1)
                {
                    _window.SetFrame(_targetFrame);
                    _view.SetZoom(_targetZoom);
                    _phase = Phase.Stretching;
                    _phaseStart = now;
                    _bus.LogDemo($"stretch  full size {WellnessBus.Describe(_window.Frame)} " +
                                 $"zoom {_targetZoom}x");
                }
                return Claim();
            }

            case Phase.Stretching:
            {
                double p = Math.Min(elapsed / Math.Max(w.StretchDuration, 0.001), 1);
                _view.SetTint(Tint(p));
                if (p >= 1)
                {
                    _phase = Phase.Settling;
                    _phaseStart = now;
                    _view.SetTint(0);
                    _bus.LogDemo($"stretch  pose done, holding {w.RestoreDelay}s");
                }
                var outv = Claim();
                outv.Squash = Pose(p);
                // Rises onto its toes through the middle of the stretch. Fractional on
                // purpose: CatView rounds it to a whole logical pixel before scaling.
                outv.Offset.Y = -Math.Sin(Math.PI * p) * w.BobHeight;
                return outv;
            }

            case Phase.Settling:
                if (elapsed >= w.RestoreDelay)
                {
                    _phase = Phase.Shrinking;
                    _phaseStart = now;
                }
                return Claim();

            case Phase.Shrinking:
            {
                double p = Math.Min(elapsed / Math.Max(w.GrowDuration, 0.001), 1);
                double e = EaseInOut(p);
                _window.SetFrame(Lerp(_targetFrame, _savedFrame, e));
                _view.SetZoom(_targetZoom + (1 - _targetZoom) * e);
                if (p >= 1) Finish();
                return Claim();
            }

            default:
                return ModuleOutput.None;
        }
    }

    // MARK: - phases

    private void Begin(string reason)
    {
        _savedFrame = _window.Frame;
        var screen = Screens.Holding(_savedFrame);
        var vf = screen.Work;

        // Integer logical scale only. A stretch that settles on 19.25x would make every
        // pixel of the cat a different size, which at this magnification is not subtle.
        // Snapping down keeps it just under the requested fraction.
        double cap = vf.H * _atlas.Wellness.ScreenFraction;
        double target = Math.Max(_view.Scale, Math.Floor(cap / _atlas.Canvas));
        _targetZoom = target / _view.Scale;
        double side = MathX.Round(_atlas.Canvas * target);
        _targetFrame = new Rect(
            MathX.Round(vf.MidX - side / 2), MathX.Round(vf.MidY - side / 2), side, side);

        _bus.Bubble?.Suppress(true);
        _view.SetAuxHidden(true);
        _phase = Phase.Growing;
        _phaseStart = Clock.Now;
        _bus.LogDemo($"stretch  BEGIN ({reason}) saved {WellnessBus.Describe(_savedFrame)} " +
                     $"-> {WellnessBus.Describe(_targetFrame)} on {vf}");
    }

    private void Finish()
    {
        // Snap, do not settle: the whole point of saving the frame is that the cat ends
        // up exactly where it was, not a rounding error away from it.
        _window.SetFrame(_savedFrame);
        _view.SetZoom(1);
        _view.SetTint(0);
        _view.SetAuxHidden(false);
        _bus.Bubble?.Suppress(false);
        _phase = Phase.Waiting;
        if (Interval is { } iv) _nextFire = Clock.Now + iv;
        _bus.LogDemo($"stretch  END restored {WellnessBus.Describe(_window.Frame)}");
    }

    /// Claims the frame. `Stretching` outranks everything but a drag, and `Exclusive`
    /// stops the losers' squash and offset blending in underneath — nothing else gets a
    /// say while the cat is the size of a window.
    private static ModuleOutput Claim() => new()
    {
        State = CatState.Stretching,
        Exclusive = true,
    };

    // MARK: - curves

    /// The stretch arc: up onto the toes, hold, fold down, settle. The rig clamps to
    /// ±14%, so these are the extremes rather than suggestions.
    private static double Pose(double p)
    {
        if (p < 0.22) return 1.0 + 0.14 * EaseInOut(p / 0.22);
        if (p < 0.55) return 1.14;
        if (p < 0.78) return 1.14 - 0.24 * EaseInOut((p - 0.55) / 0.23);
        return 0.90 + 0.10 * EaseInOut((p - 0.78) / 0.22);
    }

    /// Ramps in, holds, then eases back from `tint_release_at` so the cat is its own
    /// colour again before it starts shrinking — the release is the cue that the break
    /// is ending, and it has to land before the window moves.
    private double Tint(double p)
    {
        var w = _atlas.Wellness;
        double release = MathX.Clamp(w.TintReleaseAt, 0.05, 0.99);
        if (p < 0.3) return w.TintPeak * (p / 0.3);
        if (p < release) return w.TintPeak;
        return w.TintPeak * (1 - (p - release) / (1 - release));
    }

    private static double EaseInOut(double t)
    {
        double x = MathX.Clamp(t, 0, 1);
        return x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
    }

    private static Rect Lerp(Rect a, Rect b, double t) => new(
        // Whole pixels: the container centres itself on the surface's midpoint, and a
        // half-pixel frame would put that midpoint between two device pixels.
        MathX.Round(a.X + (b.X - a.X) * t),
        MathX.Round(a.Y + (b.Y - a.Y) * t),
        MathX.Round(a.W + (b.W - a.W) * t),
        MathX.Round(a.H + (b.H - a.H) * t));
}
