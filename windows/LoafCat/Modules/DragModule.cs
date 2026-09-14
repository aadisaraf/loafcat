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
    ///
    /// Much narrower than they used to be (1.35 and 1.75), because what they scale
    /// changed underneath them: the atlas baseline is now the full reference hang, 2.4x
    /// the cat's own height, rather than the timid 0.6 it used to be. The old
    /// multipliers on the new baseline would have hung the springy cat 117px below its
    /// own paws — past the transparent margin, and therefore sliced off by the window
    /// edge. generate_art.py asserts the top of this range fits.
    public static double HangScale(this DragFeel f) => f switch
    {
        DragFeel.Subtle => 1.0,      // the atlas baseline, untouched
        DragFeel.Normal => 1.12,
        DragFeel.Springy => 1.25,
        _ => 1.0,
    };

    public static double MaxScale(this DragFeel f) => f switch
    {
        DragFeel.Subtle => 1.0,
        DragFeel.Normal => 1.12,
        DragFeel.Springy => 1.25,
        _ => 1.0,
    };

    public static readonly DragFeel[] All =
        [DragFeel.Subtle, DragFeel.Normal, DragFeel.Springy];

    /// `Subtle` is the default, which is the same thing as saying the atlas is: it is the
    /// only one of the three that multiplies by 1.0, so an untouched install gets the
    /// tuning the theme actually shipped. The louder two are there to be chosen.
    public static DragFeel Current =>
        Enum.TryParse(Prefs.GetString("dragFeel", "subtle"), ignoreCase: true, out DragFeel f)
            ? f
            : DragFeel.Subtle;

    /// Lower-case, matching the Swift enum's raw values, so the two builds write the
    /// same string into their settings.
    public static string Raw(this DragFeel f) => f.ToString().ToLowerInvariant();
}

/// How QUICKLY the stretch happens, as distinct from how far it goes.
///
/// `DragFeel` is the amplitude — how much the cat deforms. This is the tempo, and they
/// are genuinely separate tastes: a big slow stretch and a small snappy one are both
/// coherent, and one control cannot give you either.
///
/// The atlas deliberately makes the gesture asymmetric — `rise_px_s` 400 against
/// `fall_px_s` 80, so the cat snaps taut five times faster than it gives anything back.
/// That asymmetry is what makes it read as elastic rather than as a slider being
/// dragged, so these presets scale it rather than flattening it.
public enum StretchTempo
{
    Snappy,
    Quick,
    Normal,
    Languid,
}

public static class StretchTempoExtensions
{
    public static string Label(this StretchTempo t) => t switch
    {
        StretchTempo.Snappy => "Snappy",
        StretchTempo.Quick => "Quick",
        StretchTempo.Normal => "Normal",
        StretchTempo.Languid => "Languid",
        _ => "Normal",
    };

    /// What the preset does, in the units a person can check against the cat.
    public static string Detail(this StretchTempo t) => t switch
    {
        StretchTempo.Snappy => "back almost at once",
        StretchTempo.Quick => "about twice as fast as normal",
        StretchTempo.Normal => "the shipped tuning",
        StretchTempo.Languid => "hangs on to the stretch",
        _ => "",
    };

    /// Multipliers on the atlas baseline rather than replacements, exactly like
    /// DragFeel and for the same reason: a theme that retunes the drag keeps all four
    /// meaningful instead of silently drifting. Normal is 1.0 by definition — the
    /// shipped tuning IS the normal preset.

    /// The onset — which is now the lift itself, all 170ms of it, rather than a detail of
    /// how a yank registers. Kept narrow deliberately: much faster and the cat arrives at
    /// full length before the eye has followed the cursor, much slower and the lift stops
    /// reading as a consequence of the grab.
    public static double RiseScale(this StretchTempo t) => t switch
    {
        StretchTempo.Snappy => 1.25,
        StretchTempo.Quick => 1.10,
        StretchTempo.Normal => 1.0,
        StretchTempo.Languid => 0.85,
        _ => 1.0,
    };

    /// Giving length back WHILE STILL HELD, which now happens only when the shake
    /// headroom relaxes — the hang itself never comes back down until you let go. The
    /// recovery a person actually watches is ReleaseDampingScale below.
    public static double FallScale(this StretchTempo t) => t switch
    {
        StretchTempo.Snappy => 3.0,      // fall_px_s 80 -> 240
        StretchTempo.Quick => 1.8,       // -> 144
        StretchTempo.Normal => 1.0,      // -> 80
        StretchTempo.Languid => 0.6,     // -> 48
        _ => 1.0,
    };

    /// How long a held stretch stays taut before it begins to ease at all.
    public static double HoldScale(this StretchTempo t) => t switch
    {
        StretchTempo.Snappy => 0.25,     // stretch_hold_ms 900 -> 225
        StretchTempo.Quick => 0.55,      // -> 495
        StretchTempo.Normal => 1.0,      // -> 900
        StretchTempo.Languid => 1.6,     // -> 1440
        _ => 1.0,
    };

    /// The bounce after you let go, which measurement says is most of what "slow to
    /// unstretch" actually means: the cat snaps home in about 110ms and then wobbles,
    /// +0.28 at 300ms and +0.03 at 620ms, taking two seconds to be properly still.
    /// fall_px_s has nothing to do with that -- this spring does, and now that the hang
    /// holds flat until release, this spring is the ONLY thing that does.
    ///
    /// Note the direction. Spring.Damping is the fraction of velocity KEPT each frame
    /// -- velocity *= pow(damping, h * 60) -- so a LOWER number is a more damped spring
    /// that stops sooner. Reading it as "how damped is it" gets the presets exactly
    /// backwards, which is a mistake worth only making once.
    public static double ReleaseDampingScale(this StretchTempo t) => t switch
    {
        StretchTempo.Snappy => 0.78,     // release_damping 0.78 -> 0.61, barely bounces
        StretchTempo.Quick => 0.89,      // -> 0.69
        StretchTempo.Normal => 1.0,      // -> 0.78
        StretchTempo.Languid => 1.10,    // -> 0.86, rings on
        _ => 1.0,
    };

    public static readonly StretchTempo[] All =
        [StretchTempo.Snappy, StretchTempo.Quick, StretchTempo.Normal, StretchTempo.Languid];

    public static StretchTempo Current =>
        Enum.TryParse(Prefs.GetString("stretchTempo", "normal"), ignoreCase: true,
                      out StretchTempo t)
            ? t
            : StretchTempo.Normal;

    /// Lower-case, matching the Swift enum's raw values, so the two builds write the
    /// same string into their settings.
    public static string Raw(this StretchTempo t) => t.ToString().ToLowerInvariant();
}

/// Picking the cat up.
///
/// Three coupled behaviours that share a grab but simulate independently:
///
///  1. **A deadzone.** A press registers only a *pending* drag. Without the 4px
///     threshold every click-to-pet becomes an accidental lift, which is the single
///     most irritating bug a desktop pet can have.
///  2. **A hang that is a LENGTH, not a rubber band.** Distance-driven stretch reads
///     as a band anchored to the cursor. This one is a constant: lift the cat and it
///     lengthens to `hang_px` over `hang_px / rise_px_s`, and then it stays there for as
///     long as you hold it, however far or fast you carry it. Measured off the reference
///     behaviour frame by frame — a flat 2.4x, no sag, no gathering back in — and it is
///     what makes a carried cat read as dangling rather than as a value easing towards
///     something.
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
        // Lengths, in canvas pixels of added height. See the atlas on why these are
        // pixels and not multiples of the span below the grab.
        public double MaxPx = 76;
        public double HangPx = 67;
        public double YankSpeedRef = 900;
        public double YankAttack = 14;
        public double YankRelease = 3.2;
        public double SpeedSmoothing = 8.0;
        public double RisePxS = 400;
        public double FallPxS = 80;
        public double ReleaseStiffness = 468;
        public double ReleaseDamping = 0.78;
        public double ReleaseVelocityGain = 0.35;
        public double ReleaseSettleEps = 0.0001;
        public double LandingSquashPerPx = 0.0130;
        public double GrabMinY = 26;
        public double GrabMaxY = 34;
        public double HeadLagPx = 1.5;
        public double HeadSwingShare = 0.08;
        public double ShadowShrink = 0.65;
        public double SwingArmFrac = 0.5;
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
            MaxPx = V("max_px", MaxPx);
            HangPx = V("hang_px", HangPx);
            var feel = DragFeelExtensions.Current;
            HangPx *= feel.HangScale();
            MaxPx *= feel.MaxScale();
            // Nothing may hang past the transparent margin, because the window ends
            // there and a paw across that line is simply gone. The generator asserts it
            // for the themes shipped here; this is the same guarantee for a community
            // theme, which nothing in this build gets to check first.
            double room = a.Layout.PadY + a.Canvas - InkBottom(a);
            MaxPx = Math.Min(MaxPx, Math.Max(room, 1));
            HangPx = Math.Min(HangPx, MaxPx);
            YankSpeedRef = V("yank_speed_ref", YankSpeedRef);
            YankAttack = V("yank_attack", YankAttack);
            YankRelease = V("yank_release", YankRelease);
            SpeedSmoothing = V("speed_smoothing", SpeedSmoothing);
            RisePxS = V("rise_px_s", RisePxS);
            FallPxS = V("fall_px_s", FallPxS);
            var tempo = StretchTempoExtensions.Current;
            RisePxS *= tempo.RiseScale();
            FallPxS *= tempo.FallScale();
            StretchHoldMs *= tempo.HoldScale();
            ReleaseStiffness = V("release_stiffness", ReleaseStiffness);
            ReleaseDamping = V("release_damping", ReleaseDamping);
            // After the atlas read, and clamped: damping is a multiplier on a value that
            // must stay under 1, or the release spring never comes to rest.
            ReleaseDamping = Math.Max(0.35,
                Math.Min(ReleaseDamping * tempo.ReleaseDampingScale(), 0.97));
            ReleaseVelocityGain = V("release_velocity_gain", ReleaseVelocityGain);
            ReleaseSettleEps = V("release_settle_eps", ReleaseSettleEps);
            LandingSquashPerPx = V("landing_squash_per_px", LandingSquashPerPx);
            GrabMinY = V("grab_min_y", GrabMinY);
            GrabMaxY = V("grab_max_y", GrabMaxY);
            HeadLagPx = V("head_lag_px", HeadLagPx);
            HeadSwingShare = V("head_swing_share", HeadSwingShare);
            ShadowShrink = V("shadow_shrink", ShadowShrink);
            SwingArmFrac = V("swing_arm_frac", SwingArmFrac);
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

    private double _yank;
    private double _dragSpeed;

    // --- hang ---------------------------------------------------------------
    /// How much longer the cat is than it is standing up, in canvas pixels. The whole
    /// simulation runs in this unit and converts once, at the very end, into the
    /// fraction the rig wants — which is why the length no longer depends on where
    /// along the scruff the cat happened to be grabbed.
    private double _stretchPx;
    /// Canvas pixels between the grab and the lowest ink, fixed for the gesture. The
    /// divisor for that conversion, and nothing else.
    private double _spanPx = 13;
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
        foreach (var (name, p) in atlas.Standing)
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
        _spanPx = Math.Max(InkBottom(v.Atlas) - _grabY, 1);
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
        _yank = 0;
        _dragSpeed = 0;
        _stretchPx = 0;
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
        _release.Value = _stretchPx;
        _release.Velocity = -_stretchPx * _t.ReleaseVelocityGain * 60;
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

                // Two channels:
                //
                //   hangPx  a constant. A cat held up by the scruff dangles at its full
                //           length for as long as you hold it — it does not gather
                //           itself back in, and it does not sag further either. The
                //           reference behaviour holds a flat 2.4x for the whole drag,
                //           measured frame by frame, and this is that flat line.
                //   yank    how hard it is being thrown around right now. Rises fast,
                //           falls slower, and decays to nothing when you stop moving.
                //
                // Together: pick it up and it lengthens over `hang_px / rise_px_s` and
                // stays; whip it about and it gains a little more; drop it and the
                // release spring brings it home. Everything the eye reads as "slowly
                // unstretching" happens after the release, not during the drag.
                //
                // Smoothed, not instantaneous. A raw per-tick delta at 120Hz is mostly
                // noise, and feeding that into a whole-pixel quantiser downstream makes
                // the rendered length flicker between two values several times a second.
                double rawSpeed = MathX.Hypot(moved.X, moved.Y) / Math.Max(dt, 0.0001);
                _dragSpeed += (rawSpeed - _dragSpeed) * Math.Min(1, _t.SpeedSmoothing * dt);
                double speed = _dragSpeed;

                double headroom = Math.Max(_t.MaxPx - _t.HangPx, 0);
                double yankTarget = Math.Min(speed / Math.Max(_t.YankSpeedRef, 1), 1) * headroom;
                // Asymmetric: a yank must register on the frame it happens, but relaxing
                // slowly is what makes it read as weight rather than a snap.
                double rate = yankTarget > _yank ? _t.YankAttack : _t.YankRelease;
                _yank += (yankTarget - _yank) * Math.Min(1, rate * dt);

                // No `Max(..., hang)` floor any more, and none needed: the hang is a
                // constant rather than something still climbing out of zero, so the sum
                // cannot dip beneath it and the cat cannot shrink mid-carry.
                double target = Math.Min(_t.HangPx + _yank, _t.MaxPx);

                // Rate-limit what is actually drawn. Downstream the extent is snapped to
                // whole logical pixels, so an abrupt change in the target crosses several
                // pixel boundaries in one frame and reads as a jump rather than a settle.
                // This limiter IS the lift: the target is at full length from the first
                // frame of the drag, and RisePxS is what makes getting there take the
                // 170ms it should.
                double limit = (target > _stretchPx ? _t.RisePxS : _t.FallPxS) * dt;
                _stretchPx += MathX.Clamp(target - _stretchPx, -limit, limit);

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
                _stretchPx = _release.Value;
                _yank = 0;
                ConsumePointer(null);
                StepSwing(dt, f, 0, dragging: false);

                if (_release.Value == 0 && _release.Velocity == 0 && _angle == 0 && _angVel == 0)
                {
                    _phase = Phase.Idle;
                    _stretchPx = 0;
                    v.Rig.ClearDrag();
                    return ModuleOutput.None;
                }
                break;
            }
        }

        // The hang only ever elongates. The spring's negative excursion is the landing
        // squash instead, which is exactly what SetSquash is for — and being uniform is
        // right for an impact, where the whole cat compresses.
        // The one place pixels become the fraction the rig works in. Dividing here
        // rather than storing a fraction is what makes a scruff-grab and a rump-grab
        // produce the same length of cat.
        v.Rig.SetDrag(
            stretch: Math.Max(0, _stretchPx) / _spanPx,
            grabY: _grabY,
            leanPx: LeanPx,
            headLagPx: _t.HeadLagPx,
            headSwingShare: _t.HeadSwingShare,
            shadowShrink: _t.ShadowShrink);
        outv.Squash = 1 + Math.Min(0, _stretchPx) * _t.LandingSquashPerPx;
        return outv;
    }

    /// The pendulum's arm is how long the cat currently is below the grab, so a
    /// stretched cat whips further than a compact one from the same angle. A constant
    /// here is why a 2.2x cat used to shake by seven degrees.
    private double LeanPx =>
        Math.Sin(_angle) * (_spanPx + Math.Max(0, _stretchPx)) * _t.SwingArmFrac;

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
        return new DebugState(
            name,
            _phase == Phase.Dragging ? hold : 0,
            _stretchPx / _spanPx,
            Math.Max(0, _stretchPx),
            1 + Math.Min(0, _stretchPx) * _t.LandingSquashPerPx,
            _angle * 180 / Math.PI,
            _angVel,
            LeanPx);
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

    // The extremes the gesture reached. Printed as one line at the end so the two
    // platforms can be compared by a number rather than by scrolling 856 frames of
    // trace side by side — which is the only practical way to tell whether a port of
    // a spring system actually behaves like its original.
    private double _maxStretch, _minStretch, _maxAngle, _maxDrop, _maxLean;
    private double _minSquash = 1;

    // How long the stretch takes to let go, which is the ONLY thing the stretch tempo
    // presets change. The peaks above are amplitudes and are identical across all four
    // presets by design, so without this the demo cannot tell the presets apart -- and
    // a comparison that cannot fail is not one.
    private double _releasedAt;
    private double _lastLoud;

    // The lift: how long the cat gets on being picked up, and how long that takes.
    // Neither is visible in the peaks above, because the shake saturates the length at
    // `max_px` whatever the pickup does -- so without these two the demo would print an
    // identical PASS for a build that had lost the lift entirely.
    //
    // _liftPx is the plateau at the end of the hold; _liftMovedAt dates the last frame
    // that was still climbing. A plateau is exactly what makes the second measurable.
    private double _liftPx;
    private double _liftMovedAt;
    private double _liftPrev = -1;

    private void Track(DragModule.DebugState s, double shakeStart)
    {
        _maxStretch = Math.Max(_maxStretch, s.Stretch);
        _minStretch = Math.Min(_minStretch, s.Stretch);
        // When the stretch channel goes quiet, which is what the tempo presets move and
        // what the peaks above cannot show -- they are amplitudes, and the presets scale
        // rates. Measured as the LAST frame that was still visibly moving rather than
        // the first quiet one, because the recovery bounces and an early sample sits in
        // a trough. The settle line is the pendulum, which is a different spring.
        if (_released && Math.Abs(s.Stretch) > 0.02) _lastLoud = _t;
        if (_t < shakeStart)
        {
            if (Math.Abs(s.DropPx - _liftPrev) > 0.05) _liftMovedAt = _t;
            _liftPrev = s.DropPx;
            _liftPx = s.DropPx;
        }
        _maxAngle = Math.Max(_maxAngle, Math.Abs(s.AngleDeg));
        _maxDrop = Math.Max(_maxDrop, s.DropPx);
        _minSquash = Math.Min(_minSquash, s.Squash);
        _maxLean = Math.Max(_maxLean, Math.Abs(s.LeanPx));
    }

    private string Peaks() =>
        $"# demo: peaks stretch=+{_maxStretch:F4}/{_minStretch:F4} " +
        $"angle={_maxAngle:F3}deg dropPx={_maxDrop:F2} " +
        $"squash={_minSquash:F4} leanPx={_maxLean:F2} " +
        $"quietMs={Math.Max(0, (_lastLoud - _releasedAt) * 1000):F0} " +
        $"liftPx={_liftPx:F2} liftMs={Math.Max(0, (_liftMovedAt - BreakAt) * 1000):F0}";

    private static readonly Pt GrabAt = new(24, 36);
    private const double StartAt = 0.10;
    private const double BreakAt = StartAt + 0.02;
    private const double HoldSeconds = 0.80;
    private const double ShakeAmp = 40;
    private const double ShakePeriod = 0.30;
    private const double ShakeCycles = 4;
    private const double IdleWatch = 3.0;

    public void Advance(DragModule m, in TickContext ctx)
    {
        double dt = ctx.Dt;
        const double startAt = StartAt;
        const double breakAt = BreakAt;
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
            _releasedAt = _t;
            Log.Line("# demo: release");
            m.MouseUp(GrabAt);
        }
        m.DemoInjecting = false;

        if (_t < startAt) return;
        var s = m.Debug();
        Track(s, shakeStart);

        // Sign first, THEN pad — which is what Swift's `%+.3f` does. Padding the
        // number and prepending the sign gives "+ 7.025" where the other build prints
        // " +7.025", and two traces that are meant to be compared line by line have to
        // be formatted identically to be worth anything.
        static string F(double v, int places, int width) =>
            ((v < 0 ? "-" : "+") + Math.Abs(v).ToString("F" + places)).PadLeft(width);

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
                Log.Line(Peaks());
                Log.Line(_residualBreaches == 0
                    ? "# demo: PASS -- came to rest and stayed there"
                    : "# demo: FAIL -- still moving after settle");
                Log.Stop();
                Environment.Exit(_residualBreaches == 0 ? 0 : 1);
            }
        }
    }
}
