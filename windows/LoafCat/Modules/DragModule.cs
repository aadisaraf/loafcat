using System.Runtime.Versioning;
using LoafCat.Interop;

namespace LoafCat.Modules;

/// How dramatic the drag deformation is. Purely a taste setting.
public enum DragFeel
{
    Subtle,
    Normal,
    Springy,
}

public static class DragFeelExtensions
{
    public static string Label(this DragFeel f) => f switch
    {
        DragFeel.Subtle => "Subtle",
        DragFeel.Normal => "Normal",
        DragFeel.Springy => "Springy",
        _ => "Normal",
    };

    /// Multipliers on the atlas baseline rather than replacements, so a theme that
    /// retunes the feel keeps these three meaningful instead of silently drifting.
    public static double HangScale(this DragFeel f) => f switch
    {
        DragFeel.Subtle => 1.0,      // the old Normal
        DragFeel.Normal => 1.35,     // the old Springy, now the default
        DragFeel.Springy => 1.75,
        _ => 1.35,
    };

    public static double MaxScale(this DragFeel f) => f switch
    {
        DragFeel.Subtle => 1.0,
        DragFeel.Normal => 1.38,
        DragFeel.Springy => 1.80,
        _ => 1.38,
    };

    public static readonly DragFeel[] All =
        [DragFeel.Subtle, DragFeel.Normal, DragFeel.Springy];

    public static DragFeel Current =>
        Enum.TryParse(Prefs.GetString("dragFeel", "normal"), ignoreCase: true, out DragFeel f)
            ? f
            : DragFeel.Normal;

    /// Lower-case, matching the Swift enum's raw values, so the two builds write the
    /// same string into their settings.
    public static string Raw(this DragFeel f) => f.ToString().ToLowerInvariant();
}

/// Picking the cat up.
///
/// Three coupled behaviours that share a grab but simulate independently:
///
///  1. **A deadzone.** A press registers only a *pending* drag. Without the 4px
///     threshold every click-to-pet becomes an accidental lift, which is the single
///     most irritating bug a desktop pet can have.
///  2. **A hang driven by HOLD TIME, not drag distance.** This is the whole trick.
///     Distance-driven stretch reads as a rubber band anchored to the cursor;
///     time-driven stretch reads as a warm animal slowly giving in to gravity.
///  3. **A pendulum.** Impulse comes from drag *acceleration* through a power law, so
///     a flick swings hard and a slow pan barely disturbs it.
///
/// The three are deliberately not one simulation. The hang must saturate while the
/// swing stays lively, and coupling them would make a shake shorten the cat.
[SupportedOSPlatform("windows")]
public sealed class DragModule : ICatModule
{
    public string Id => "drag";

    private readonly CatWindow _window;
    private readonly ModuleRegistry _registry;

    private enum Phase { Idle, Pending, Dragging, Settling }
    private Phase _phase = Phase.Idle;

    /// Everything tunable, from cat.json. Defaults are the shipped mono values, so a
    /// theme that omits the block still behaves rather than collapsing to zero.
    private sealed class Tuning
    {
        public double DeadzonePx = 4;
        public double StretchHoldMs = 900;
        public double StretchMax = 1.00;
        public double HangRest = 0.34;
        public double HangRate = 6.0;
        public double YankSpeedRef = 900;
        public double YankAttack = 14;
        public double YankRelease = 3.2;
        public double SpeedSmoothing = 8.0;
        public double RiseRate = 9.0;
        public double FallRate = 1.8;
        public double ReleaseStiffness = 468;
        public double ReleaseDamping = 0.78;
        public double ReleaseVelocityGain = 0.35;
        public double ReleaseSettleEps = 0.0001;
        public double LandingSquashGain = 0.5;
        public double GrabMinY = 26;
        public double GrabMaxY = 34;
        public double HeadLagPx = 1.5;
        public double HeadSwingShare = 0.08;
        public double ShadowShrink = 0.65;
        public double SwingLengthPx = 14;
        public double SwingMaxDeg = 45;
        public double SwingImpulse = 0.0012;
        public double SwingAccelCap = 20;
        public double SwingVelSmoothing = 0.35;
        public double SwingSpringDrag = 0.018;
        public double SwingSpringFree = 0.003;
        public double SwingDampingDrag = 0.86;
        public double SwingDampingFree = 0.962;
        public double SwingSettleEpsRad = 0.007;
        public double SwingSettleEpsVel = 0.003;
        public double PadPx = 12;

        public Tuning() { }

        public Tuning(Atlas a)
        {
            double V(string k, double d) => a.Tune("drag", k, d);
            DeadzonePx = V("deadzone_px", DeadzonePx);
            StretchHoldMs = V("stretch_hold_ms", StretchHoldMs);
            StretchMax = V("stretch_max", StretchMax);
            HangRest = V("hang_rest", HangRest);
            var feel = DragFeelExtensions.Current;
            HangRest *= feel.HangScale();
            StretchMax *= feel.MaxScale();
            HangRate = V("hang_rate", HangRate);
            YankSpeedRef = V("yank_speed_ref", YankSpeedRef);
            YankAttack = V("yank_attack", YankAttack);
            YankRelease = V("yank_release", YankRelease);
            SpeedSmoothing = V("speed_smoothing", SpeedSmoothing);
            RiseRate = V("rise_rate", RiseRate);
            FallRate = V("fall_rate", FallRate);
            ReleaseStiffness = V("release_stiffness", ReleaseStiffness);
            ReleaseDamping = V("release_damping", ReleaseDamping);
            ReleaseVelocityGain = V("release_velocity_gain", ReleaseVelocityGain);
            ReleaseSettleEps = V("release_settle_eps", ReleaseSettleEps);
            LandingSquashGain = V("landing_squash_gain", LandingSquashGain);
            GrabMinY = V("grab_min_y", GrabMinY);
            GrabMaxY = V("grab_max_y", GrabMaxY);
            HeadLagPx = V("head_lag_px", HeadLagPx);
            HeadSwingShare = V("head_swing_share", HeadSwingShare);
            ShadowShrink = V("shadow_shrink", ShadowShrink);
            SwingLengthPx = V("swing_length_px", SwingLengthPx);
            SwingMaxDeg = V("swing_max_deg", SwingMaxDeg);
            SwingImpulse = V("swing_impulse", SwingImpulse);
            SwingAccelCap = V("swing_accel_cap", SwingAccelCap);
            SwingVelSmoothing = V("swing_vel_smoothing", SwingVelSmoothing);
            SwingSpringDrag = V("swing_spring_drag", SwingSpringDrag);
            SwingSpringFree = V("swing_spring_free", SwingSpringFree);
            SwingDampingDrag = V("swing_damping_drag", SwingDampingDrag);
            SwingDampingFree = V("swing_damping_free", SwingDampingFree);
            SwingSettleEpsRad = V("swing_settle_eps_rad", SwingSettleEpsRad);
            SwingSettleEpsVel = V("swing_settle_eps_vel", SwingSettleEpsVel);
            PadPx = V("pad_px", PadPx);
        }
    }

    private Tuning _t = new();

    // --- gesture ------------------------------------------------------------
    private double _pendingTravel;
    private Pt _pendingDelta = Pt.Zero;
    private double _grabY = 30;
    private double _heldSeconds;

    /// Gravity droop while held. Deliberately NOT a spring: a spring overshoots, and an
    /// overshooting droop makes the cat dip below its resting length as the yank
    /// decays, then rise back — which reads as a glitch. Exponential approach is
    /// monotonic.
    private double _hang;
    private double _yank;
    private double _dragSpeed;

    // --- hang ---------------------------------------------------------------
    private double _stretch;
    /// Only runs after release. While held, the stretch is a pure function of how long
    /// the cat has hung, so a spring would just fight the hold curve.
    private Spring _release = new(468, 0.78);

    // --- swing --------------------------------------------------------------
    private double _angle;             // radians
    private double _angVel;            // radians per 60Hz frame
    private double _smoothedVel;       // logical px per 60Hz frame
    private double _prevSmoothedVel;

    private readonly DragDemo? _demo;

    /// True only inside a scripted demo's injected call. When a demo is running, real
    /// pointer input is refused: a stray click from whoever happens to be at the machine
    /// would otherwise start a second drag partway through the capture and make the
    /// trace non-reproducible.
    internal bool DemoInjecting;
    private bool InputIsScripted => _demo is not null;

    public DragModule(CatWindow window, ModuleRegistry registry)
    {
        _window = window;
        _registry = registry;
        if (Environment.GetCommandLineArgs().Contains("--demo-drag")) _demo = new DragDemo();
    }

    // MARK: - Wiring

    /// The view is rebuilt on every theme or size change, so the event hookup is
    /// re-checked rather than made once. Doing it here instead of in Program.cs keeps
    /// this feature to a single registration line, per architecture rule 2.
    private CatView? View
    {
        get
        {
            if (_window.View is not { } v) return null;
            if (!ReferenceEquals(v.Modules, _registry))
            {
                v.Modules = _registry;
                _t = new Tuning(v.Atlas);
            }
            return v;
        }
    }

    /// Bottom of the cat's ink, from the atlas. The hang is measured against it.
    private static double InkBottom(Atlas atlas)
    {
        double bottom = 0;
        foreach (var (name, p) in atlas.Parts)
        {
            if (name == "shadow") continue;
            bottom = Math.Max(bottom, p.Origin.Y + p.Size.H);
        }
        return Math.Max(bottom, 1);
    }

    // MARK: - Events

    public bool MouseDown(Pt point)
    {
        if (InputIsScripted && !DemoInjecting) return false;
        if (_phase is not (Phase.Idle or Phase.Settling)) return false;
        // A stretch or reminder animation owns the cat outright; interrupting it
        // mid-pose would snap the rig. `Dragging` outranks it for the NEXT grab.
        if (_registry.State == CatState.Stretching) return false;
        if (View is not { } v) return false;

        _t = new Tuning(v.Atlas);
        _phase = Phase.Pending;
        _pendingTravel = 0;
        _pendingDelta = Pt.Zero;
        // Anchor the hang at the scruff. Wherever the cat is actually grabbed, a real
        // lift happens at the neck — and it guarantees there is always body below the
        // anchor to stretch, which a grab on the paws would not.
        _grabY = MathX.Clamp(point.Y, _t.GrabMinY, _t.GrabMaxY);
        return true;
    }

    public void MouseDragged(Pt delta)
    {
        if (InputIsScripted && !DemoInjecting) return;
        // Accumulated here and consumed on the tick: mouse events arrive at the event
        // rate, not ours, and integrating them twice would double-count the
        // acceleration the pendulum reads.
        _pendingDelta.X += delta.X;
        _pendingDelta.Y += delta.Y;
        if (_phase == Phase.Pending)
        {
            _pendingTravel += MathX.Hypot(delta.X, delta.Y);
            if (_pendingTravel > _t.DeadzonePx) BeginDrag();
        }
    }

    public void MouseUp(Pt point)
    {
        if (InputIsScripted && !DemoInjecting) return;
        switch (_phase)
        {
            case Phase.Dragging: EndDrag(); break;
            case Phase.Pending: _phase = Phase.Idle; break;    // a click, not a lift
        }
    }

    private void BeginDrag()
    {
        _phase = Phase.Dragging;
        _heldSeconds = 0;
        _hang = 0;
        _yank = 0;
        _dragSpeed = 0;
        _stretch = 0;
        _angle = 0;
        _angVel = 0;
        _smoothedVel = 0;
        _prevSmoothedVel = 0;
    }

    private void EndDrag()
    {
        _phase = Phase.Settling;
        // Overshoot rather than snap: launch the spring inward at a speed set by how far
        // it was stretched, so it boings past neutral into a compression and back. Gain
        // is per 60Hz frame; Spring integrates per second.
        _release.Value = _stretch;
        _release.Velocity = -_stretch * _t.ReleaseVelocityGain * 60;
    }

    // MARK: - Tick

    public ModuleOutput Update(in TickContext ctx)
    {
        if (View is not { } v) return ModuleOutput.None;
        _demo?.Advance(this, in ctx);

        // Safety net. A mouse-up that never arrives — the window manager drops one if
        // the window is reconfigured under a held button — would otherwise strand the
        // cat stretched, enlarged and unclickable, with no way back. The button state
        // comes from the mouse hook, which is an observation rather than a query; see
        // InputTelemetry.LeftButtonDown for why that distinction is load-bearing.
        if (!InputIsScripted && _phase is Phase.Pending or Phase.Dragging &&
            !InputTelemetry.LeftButtonDown)
        {
            if (_phase == Phase.Dragging) EndDrag(); else _phase = Phase.Idle;
        }

        double dt = ctx.Dt;
        // Everything below was authored for a 60Hz loop. `f` is this tick measured in
        // those frames, and every per-frame constant is raised to it, which is what
        // makes the motion identical at our 120Hz and unchanged if the tick rate ever
        // moves again.
        double f = Math.Max(dt * 60, 0.0001);

        var outv = new ModuleOutput();

        switch (_phase)
        {
            case Phase.Idle:
                return ModuleOutput.None;

            case Phase.Pending:
                // Deadzone not cleared: the cat is being touched, not carried.
                return ModuleOutput.None;

            case Phase.Dragging:
            {
                _heldSeconds += dt;
                var moved = ConsumePointer(ctx.Scale);

                // Two components, because a single hold-time ramp could only ever
                // increase — so the cat reached full stretch and stayed there for as
                // long as you held it, with no way to relax.
                //
                //   hang  gravity. Springs to a modest resting droop and stays.
                //   yank  how hard it is being thrown around right now. Rises fast,
                //         falls slower, and decays to nothing when you stop moving.
                //
                // Smoothed, not instantaneous. A raw per-tick delta at 120Hz is mostly
                // noise, and feeding that into a whole-pixel quantiser downstream makes
                // the rendered length flicker between two values several times a second.
                double rawSpeed = MathX.Hypot(moved.X, moved.Y) / Math.Max(dt, 0.0001);
                _dragSpeed += (rawSpeed - _dragSpeed) * Math.Min(1, _t.SpeedSmoothing * dt);
                double speed = _dragSpeed;
                _hang += (_t.HangRest - _hang) * Math.Min(1, _t.HangRate * dt);

                double headroom = Math.Max(_t.StretchMax - _t.HangRest, 0);
                double yankTarget = Math.Min(speed / Math.Max(_t.YankSpeedRef, 1), 1) * headroom;
                // Asymmetric: a yank must register on the frame it happens, but relaxing
                // slowly is what makes it read as weight rather than a snap.
                double rate = yankTarget > _yank ? _t.YankAttack : _t.YankRelease;
                _yank += (yankTarget - _yank) * Math.Min(1, rate * dt);

                // The floor is the hang, explicitly. `hang + yank` alone can dip below
                // the settled hang height, because hang is still RISING from zero while
                // yank is already falling — so the cat shrinks past where gravity holds
                // it and then climbs back. Gravity does not let go.
                double target = Math.Max(Math.Min(_hang + _yank, _t.StretchMax), _hang);

                // Rate-limit what is actually drawn. Downstream the extent is snapped to
                // whole logical pixels, so an abrupt change in the target crosses several
                // pixel boundaries in one frame and reads as a jump rather than a settle.
                double limit = (target > _stretch ? _t.RiseRate : _t.FallRate) * dt;
                _stretch += MathX.Clamp(target - _stretch, -limit, limit);

                StepSwing(dt, f, moved.X, dragging: true);

                // On macOS this is where the module reasserts `ignoresMouseEvents`,
                // because that build toggles click-through from a 120Hz poll and the
                // cursor leaves the silhouette constantly while carrying the cat.
                // Windows needs no equivalent: the window manager hit-tests the composed
                // alpha directly, and CatWindow took the mouse capture on the press, so
                // events keep arriving wherever the cursor goes.
                outv.State = CatState.Dragging;
                break;
            }

            case Phase.Settling:
            {
                _release.Stiffness = _t.ReleaseStiffness;
                _release.Damping = _t.ReleaseDamping;
                _release.Step(0, dt);
                if (Math.Abs(_release.Value) < _t.ReleaseSettleEps &&
                    Math.Abs(_release.Velocity) < _t.ReleaseSettleEps * 60)
                {
                    _release.Snap(0);
                }
                _stretch = _release.Value;
                _yank = 0;
                _hang = 0;
                ConsumePointer(null);
                StepSwing(dt, f, 0, dragging: false);

                if (_release.Value == 0 && _release.Velocity == 0 && _angle == 0 && _angVel == 0)
                {
                    _phase = Phase.Idle;
                    _stretch = 0;
                    v.Rig.ClearDrag();
                    return ModuleOutput.None;
                }
                break;
            }
        }

        // The hang only ever elongates. The spring's negative excursion is the landing
        // squash instead, which is exactly what SetSquash is for — and being uniform is
        // right for an impact, where the whole cat compresses.
        v.Rig.SetDrag(
            stretch: Math.Max(0, _stretch),
            grabY: _grabY,
            leanPx: Math.Sin(_angle) * _t.SwingLengthPx,
            headLagPx: _t.HeadLagPx,
            headSwingShare: _t.HeadSwingShare,
            shadowShrink: _t.ShadowShrink);
        outv.Squash = 1 + Math.Min(0, _stretch) * _t.LandingSquashGain;
        return outv;
    }

    /// Applies this tick's pointer movement to the window and returns it.
    private Pt ConsumePointer(double? scale)
    {
        var d = _pendingDelta;
        _pendingDelta = Pt.Zero;
        if (scale is not { } sc || d.IsZero) return d;
        // The atlas is y-down and so are Windows screen coordinates, so this is a plain
        // add — the macOS build has to subtract here because its screen is y-up.
        _window.SetOrigin(
            _window.Frame.X + d.X * sc,
            _window.Frame.Y + d.Y * sc);
        return d;
    }

    // MARK: - Pendulum

    private void StepSwing(double dt, double f, double pointerDx, bool dragging)
    {
        // Velocity in logical px per 60Hz frame, smoothed. Raw per-tick deltas at 120Hz
        // are far too noisy to raise to a power of 2.2 — one jittery frame would read as
        // a flick.
        double alpha = 1 - Math.Pow(1 - _t.SwingVelSmoothing, f);
        double instant = pointerDx / Math.Max(dt, 0.0001) / 60;
        _smoothedVel += (instant - _smoothedVel) * alpha;

        // Acceleration, normalised to px per 60Hz frame squared. Dividing the
        // frame-to-frame difference by `f` is what makes this rate independent: a raw
        // difference is half as large at 120Hz, and the power law would then turn that
        // into a fifth of the swing.
        double accel = (_smoothedVel - _prevSmoothedVel) / f;
        _prevSmoothedVel = _smoothedVel;
        accel = MathX.Clamp(accel, -_t.SwingAccelCap, _t.SwingAccelCap);

        // Power law: a fast flick swings much harder than a slow pan, rather than
        // proportionally harder.
        if (Math.Abs(accel) > 1e-9)
        {
            double kick = Math.Pow(Math.Abs(accel), 2.2) * _t.SwingImpulse * f;
            _angVel -= accel < 0 ? -kick : kick;
        }

        // Stiffer and much more damped while held, so a reversal bleeds the old angle
        // off fast instead of fighting the new direction.
        double k = dragging ? _t.SwingSpringDrag : _t.SwingSpringFree;
        double damp = dragging ? _t.SwingDampingDrag : _t.SwingDampingFree;
        _angVel -= _angle * k * f;
        _angVel *= Math.Pow(damp, f);
        _angle += _angVel * f;

        double maxRad = _t.SwingMaxDeg * Math.PI / 180;
        _angle = MathX.Clamp(_angle, -maxRad, maxRad);

        // Terminate rather than ring forever. The thresholds are set in rendered pixels:
        // at settle the remaining angle is worth 0.03px of shear, so the snap to exact
        // zero is invisible, and the state stops changing.
        if (!dragging && Math.Abs(_angle) < _t.SwingSettleEpsRad &&
            Math.Abs(_angVel) < _t.SwingSettleEpsVel)
        {
            _angle = 0;
            _angVel = 0;
        }
    }

    // MARK: - debug surface for the scripted demo

    internal string DebugGeometry
    {
        get
        {
            var f = _window.Frame;
            int pad = View?.Atlas.Layout.PadY ?? 0;
            return $"window {(int)f.W}x{(int)f.H}px, static margin {pad}px";
        }
    }

    internal readonly record struct DebugState(
        string Phase, double HoldT, double Stretch, double DropPx,
        double Squash, double AngleDeg, double AngVel, double LeanPx);

    internal DebugState Debug()
    {
        string name = _phase switch
        {
            Phase.Idle => "idle",
            Phase.Pending => "pend",
            Phase.Dragging => "DRAG",
            Phase.Settling => "rel",
            _ => "?",
        };
        double hold = Math.Min(1, _heldSeconds * 1000 / Math.Max(_t.StretchHoldMs, 1));
        double bottom = View is { } v ? InkBottom(v.Atlas) : 47;
        return new DebugState(
            name,
            _phase == Phase.Dragging ? hold : 0,
            _stretch,
            Math.Max(0, _stretch) * Math.Max(0, bottom - _grabY),
            1 + Math.Min(0, _stretch) * _t.LandingSquashGain,
            _angle * 180 / Math.PI,
            _angVel,
            Math.Sin(_angle) * _t.SwingLengthPx);
    }
}

/// Drives a synthetic grab-hold-shake-release so the physics can be checked without a
/// human hand. Enabled with `--demo-drag`.
///
/// It calls the same entry points the real events do, so what it exercises is the
/// shipping path and not a parallel copy of it. This is the one automated check that
/// the ported physics actually behaves like the original: run it on both platforms and
/// the traces should agree.
[SupportedOSPlatform("windows")]
internal sealed class DragDemo
{
    private double _t;
    private double _shakeX;
    private bool _released;
    private double? _settledAt;
    private int _residualBreaches;

    private static readonly Pt GrabAt = new(24, 36);
    private const double HoldSeconds = 0.80;
    private const double ShakeAmp = 40;
    private const double ShakePeriod = 0.30;
    private const double ShakeCycles = 4;
    private const double IdleWatch = 3.0;

    public void Advance(DragModule m, in TickContext ctx)
    {
        double dt = ctx.Dt;
        const double startAt = 0.10;
        const double breakAt = startAt + 0.02;
        double shakeStart = breakAt + HoldSeconds;
        double shakeEnd = shakeStart + ShakePeriod * ShakeCycles;

        double was = _t;
        _t += dt;

        m.DemoInjecting = true;
        if (was < startAt && _t >= startAt)
        {
            Log.Line($"# demo: grab at atlas {(int)GrabAt.X},{(int)GrabAt.Y}");
            m.MouseDown(GrabAt);
        }
        if (was < breakAt && _t >= breakAt)
        {
            Log.Line("# demo: clear the 4px deadzone (6px step) -> drag begins");
            m.MouseDragged(new Pt(6, 0));
            Log.Line($"# demo: {m.DebugGeometry}");
        }
        if (_t > shakeStart && _t <= shakeEnd)
        {
            if (was <= shakeStart) Log.Line("# demo: shake, 4 cycles at 40px / 0.30s");
            double phase = (_t - shakeStart) / ShakePeriod * 2 * Math.PI;
            double next = Math.Sin(phase) * ShakeAmp;
            m.MouseDragged(new Pt(next - _shakeX, 0));
            _shakeX = next;
        }
        if (!_released && _t > shakeEnd)
        {
            _released = true;
            Log.Line("# demo: release");
            m.MouseUp(GrabAt);
        }
        m.DemoInjecting = false;

        if (_t < startAt) return;
        var s = m.Debug();

        static string F(double v, int places, int width) =>
            (v >= 0 ? "+" : "") + v.ToString("F" + places).PadLeft(width - (v >= 0 ? 1 : 0));

        Log.Line($"t={F(_t, 3, 7)} {s.Phase.PadRight(4)}"
            + $" hold={F(s.HoldT, 3, 6)}"
            + $" stretch={F(s.Stretch, 4, 7)}"
            + $" dropPx={F(s.DropPx, 2, 6)}"
            + $" squash={F(s.Squash, 3, 6)}"
            + $" angle={F(s.AngleDeg, 3, 8)}deg"
            + $" angVel={F(s.AngVel, 5, 9)}"
            + $" leanPx={F(s.LeanPx, 2, 6)}");

        if (_released && _settledAt is null && s.Phase == "idle")
        {
            _settledAt = _t;
            Log.Line($"# demo: SETTLED {_t - shakeEnd:F3}s after release; "
                + $"watching {(int)IdleWatch}s for residual motion");
            Log.Line($"# demo: {m.DebugGeometry}");
        }
        if (_settledAt is { } settled)
        {
            if (s.Stretch != 0 || s.AngleDeg != 0 || s.AngVel != 0 || s.LeanPx != 0)
            {
                _residualBreaches++;
            }
            if (_t - settled > IdleWatch)
            {
                Log.Line($"# demo: residual non-zero frames after settle: {_residualBreaches}");
                Log.Line(_residualBreaches == 0
                    ? "# demo: PASS -- came to rest and stayed there"
                    : "# demo: FAIL -- still moving after settle");
                Log.Stop();
                Environment.Exit(_residualBreaches == 0 ? 0 : 1);
            }
        }
    }
}
