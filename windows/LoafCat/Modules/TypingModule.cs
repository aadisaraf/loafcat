namespace LoafCat.Modules;

/// The two things typing does to the cat: kneading, and overheating.
///
/// Both run off `ctx.KeysPerSecond`, which is a rate derived from a tick counter and
/// a mouse-event timestamp — never a keycode. There is no code path by which the
/// identity of a key could reach this file. See CLAUDE.md, and the long note at the
/// top of Interop/InputTelemetry.cs: never reach for a keyboard hook to make a
/// reaction here nicer.
/// How much typing it takes to make the cat steam.
///
/// The atlas decides what "a lot of typing" means for a theme; this decides how much of
/// that a particular person has to do. Multipliers on `overheat.kps_min` and `kps_max`
/// together, so the SHAPE of the curve is untouched and only the range it spans moves --
/// which is why every preset still has a band where the cat kneads without reddening.
///
/// The same pattern as DragFeel and StretchTempo, for the same reason: a theme that
/// retunes overheating keeps all four presets meaningful instead of silently drifting.
/// `Normal` is 1.0 by definition -- the shipped tuning IS the normal preset.
public enum HeatSensitivity
{
    Instant,
    Quick,
    Normal,
    Patient,
}

public static class HeatSensitivityExtensions
{
    public static string Label(this HeatSensitivity h) => h switch
    {
        HeatSensitivity.Instant => "Instant",
        HeatSensitivity.Quick => "Quick",
        HeatSensitivity.Normal => "Normal",
        HeatSensitivity.Patient => "Patient",
        _ => "Normal",
    };

    /// Against the shipped 3.5-to-9.5, these put a fully burning cat at roughly 5.7,
    /// 7.6, 9.5 and 12.8 keystrokes a second. Patient is about where the whole thing sat
    /// before it was retuned, which is the point of keeping it.
    public static double Scale(this HeatSensitivity h) => h switch
    {
        HeatSensitivity.Instant => 0.60,
        HeatSensitivity.Quick => 0.80,
        HeatSensitivity.Normal => 1.0,
        HeatSensitivity.Patient => 1.35,
        _ => 1.0,
    };

    public static readonly HeatSensitivity[] All =
        [HeatSensitivity.Instant, HeatSensitivity.Quick, HeatSensitivity.Normal,
         HeatSensitivity.Patient];

    public static HeatSensitivity Current =>
        Enum.TryParse(Prefs.GetString("heatSensitivity", "normal"), ignoreCase: true,
                      out HeatSensitivity h)
            ? h
            : HeatSensitivity.Normal;

    /// Lower-case, matching the Swift enum's raw values, so the two builds write the
    /// same string into their settings.
    public static string Raw(this HeatSensitivity h) => h.ToString().ToLowerInvariant();
}

public sealed class TypingModule : ICatModule, IAtlasTuned
{
    public string Id => "typing";
    public int TunedGeneration { get; set; } = -1;

    // --- constants, every one of them from cat.json -------------------------
    private double _gateKps;
    private double _releaseDelay;
    private double _pawPeriod = 0.2;
    private double _pawLift;
    private double _pawReach;
    private double _bodyBob;
    private double _squashDepth;
    private double _attack = 0.1;
    private double _decay = 0.1;

    /// Whether this theme draws the two keys at all. A theme generated before they
    /// existed still kneads exactly as it always did, on thin air.
    private bool _hasKeys;
    private double _keyShowAt = 1;
    private double _keyRest;
    private double _keyTravel;
    private double _keyDownBelow;

    private double _kpsMin;
    private double _kpsSpan = 1;
    private double _curve = 1;
    private double _easePerFrame;
    private double _stateAt = 1;
    private double _steamAt = 1;
    private double _steamPeriod = 1;
    private double _steamRise;
    private int _steamSlots;

    /// How far to shift a steam puff to mirror it to the cat's other side. Derived
    /// from where the art actually sits, not typed in here.
    private double _steamMirror;

    // --- state --------------------------------------------------------------
    private bool _kneading;
    private double _amp;      // eased 0..1 envelope on the knead pose
    private double _phase;    // 0..2; one stroke per unit, paws alternate
    private double _heat;
    private double _steamPhase;

    /// `--demo-typing` types for the module, so the keys and the overheat can be
    /// watched — and screenshotted — with nobody at the keyboard. Only the RATE is
    /// faked; everything downstream of it is the real module.
    private readonly bool _demo = Environment.GetCommandLineArgs().Contains("--demo-typing");
    private double _demoClock;

    public void Retune(Atlas atlas)
    {
        var b = atlas.Behaviour;
        // "5 keystrokes inside a 2s sliding window" is, for a sliding-window RATE,
        // exactly `kps >= 5/2`. That equivalence is the whole gate: a single keypress
        // produces a rate far below it, so isolated keys are ignored without the
        // module having to see individual keystrokes it is not allowed to see anyway.
        _gateKps = b.F("typing.burst_keys") / Math.Max(b.F("typing.burst_window"), 0.001);
        _releaseDelay = b.F("typing.release_delay");
        _pawPeriod = Math.Max(b.F("typing.paw_period"), 0.01);
        _pawLift = b.F("typing.paw_lift");
        _pawReach = b.F("typing.paw_reach");
        _bodyBob = b.F("typing.body_bob");
        _squashDepth = b.F("typing.squash");
        _attack = Math.Max(b.F("typing.attack"), 0.001);
        _decay = Math.Max(b.F("typing.decay"), 0.001);

        // Checked before the constants are read, so an older theme with no keys does
        // not complain about four numbers it was never going to use.
        _hasKeys = new[] { "key_l", "key_l_down", "key_r", "key_r_down" }
            .All(atlas.Overlays.ContainsKey);
        if (_hasKeys)
        {
            _keyShowAt = b.F("typing.key_show_at");
            _keyRest = b.F("typing.key_rest_px");
            _keyTravel = b.F("typing.key_travel_px");
            _keyDownBelow = b.F("typing.key_down_below_px");
        }

        // Read after the atlas, exactly like the drag presets: the theme's numbers are
        // the baseline and the preference scales them.
        double sensitivity = HeatSensitivityExtensions.Current.Scale();
        _kpsMin = b.F("overheat.kps_min") * sensitivity;
        _kpsSpan = Math.Max(b.F("overheat.kps_max") * sensitivity - _kpsMin, 0.001);
        _curve = b.F("overheat.curve");
        _easePerFrame = b.F("overheat.ease_per_frame");
        _stateAt = b.F("overheat.state_at");
        _steamAt = b.F("overheat.steam_at");
        _steamPeriod = Math.Max(b.F("overheat.steam_period"), 0.01);
        _steamRise = b.F("overheat.steam_rise");

        if (atlas.Overlays.TryGetValue("steam", out var steam))
        {
            _steamSlots = steam.Slots;
            var s = steam.Part;
            _steamMirror = atlas.Canvas - 2 * (s.Origin.X + s.Size.W / 2);
        }
        else
        {
            _steamSlots = 0;
        }
    }

    public ModuleOutput Update(in TickContext ctx)
    {
        if (this.TunedAtlas() is null) return ModuleOutput.None;
        var stage = CatStage.Shared;
        var outv = new ModuleOutput();

        double keysPerSecond = ctx.KeysPerSecond;
        double secondsSinceKey = ctx.SecondsSinceKey;
        if (_demo) (keysPerSecond, secondsSinceKey) = DemoInput(ctx.Dt);

        // --- the gate -------------------------------------------------------
        if (keysPerSecond >= _gateKps) _kneading = true;
        if (_kneading && secondsSinceKey > _releaseDelay) _kneading = false;

        // Ease the amplitude rather than the pose: snapping the paws to zero mid
        // stroke reads as a dropped frame, and easing the phase instead would make the
        // strokes slow down, which reads as the cat getting tired.
        bool keysShown = false;
        double tau = _kneading ? _attack : _decay;
        _amp += ((_kneading ? 1 : 0) - _amp) * (1 - Math.Exp(-ctx.Dt / tau));

        if (_amp > 0.002)
        {
            _phase += ctx.Dt / _pawPeriod;
            while (_phase >= 2) _phase -= 2;

            // One stroke per unit of phase, alternating paws — that alternation is
            // what makes it read as kneading rather than as a two-handed thump.
            bool left = _phase < 1;
            double frac = left ? _phase : _phase - 1;
            double swing = Math.Sin(frac * Math.PI);
            double lift = swing * _pawLift * _amp;
            double reach = swing * _pawReach * _amp;
            if (left)
            {
                stage.PawOffsetL.Y -= lift;
                stage.PawOffsetL.X -= reach;
            }
            else
            {
                stage.PawOffsetR.Y -= lift;
                stage.PawOffsetR.X += reach;
            }
            // The body rocks at twice the stroke rate, once per paw.
            double bob = -Math.Abs(Math.Sin(_phase * Math.PI)) * _bodyBob * _amp;
            outv.Offset.Y = bob;
            outv.Squash = 1 - Math.Abs(Math.Sin(_phase * Math.PI)) * _squashDepth * _amp;

            if (_hasKeys && _amp >= _keyShowAt)
            {
                keysShown = true;
                PlaceKeys(left ? lift : 0, left ? 0 : lift, bob);
            }
        }
        else
        {
            _phase = 0;
        }

        // --- heat -----------------------------------------------------------
        // Continuous, never a binary state: zero below kps_min so ordinary typing can
        // never redden the cat, 1 at kps_max, curved in between.
        double t = MathX.Clamp((keysPerSecond - _kpsMin) / _kpsSpan, 0, 1);
        double target = Math.Pow(t, _curve);
        // The ease is quoted per frame at a nominal 60fps; normalising by dt makes the
        // 120Hz tick ramp at the same wall-clock rate instead of twice as fast.
        _heat += (target - _heat) * (1 - Math.Pow(1 - _easePerFrame, ctx.Dt * 60));
        stage.Heat = _heat;

        if (_heat >= _steamAt && _steamSlots > 0)
        {
            _steamPhase += ctx.Dt / _steamPeriod;
            while (_steamPhase >= 1) _steamPhase -= 1;
            double strength = Math.Min((_heat - _steamAt) / Math.Max(1 - _steamAt, 0.001), 1);
            for (int i = 0; i < _steamSlots; i++)
            {
                double p = _steamPhase + (double)i / _steamSlots;
                if (p >= 1) p -= 1;
                stage.Overlays.Add(new OverlayInstance(
                    "steam",
                    // Odd puffs mirror to the cat's other side.
                    new Pt(i % 2 == 1 ? _steamMirror : 0, -_steamRise * p),
                    Math.Sin(p * Math.PI) * strength));
            }
            outv.Overlay = "steam";
        }

        if (_heat >= _stateAt) outv.State = CatState.Overheat;
        else if (_kneading) outv.State = CatState.Kneading;

        stage.Metric("knead", _kneading ? 1 : 0);
        if (!keysShown) stage.Metric("keys", -1);
        return outv;
    }

    /// Two keys under the front paws while the cat types, the paws standing on them,
    /// and whichever paw is down holding its key down.
    ///
    /// The reference does this as a pose — the cat plants a paw on each of two
    /// keycaps and they go down alternately under it — so the keys arrive all at once
    /// and the paws step up onto them on the same frame. See `spikes/RESULTS.md`, S9.
    private void PlaceKeys(double liftL, double liftR, double bob)
    {
        var stage = CatStage.Shared;
        bool downL = liftL < _keyDownBelow;
        bool downR = liftR < _keyDownBelow;
        // Up onto the key, less the key's own travel while it is held down. The bob is
        // taken back out of the paws: it is the BODY rocking, and a paw that rose with
        // it would float off a key it is meant to be pressing.
        stage.PawOffsetL.Y -= _keyRest - (downL ? _keyTravel : 0) + bob;
        stage.PawOffsetR.Y -= _keyRest - (downR ? _keyTravel : 0) + bob;
        stage.Overlays.Add(new OverlayInstance(downL ? "key_l_down" : "key_l", Pt.Zero, 1));
        stage.Overlays.Add(new OverlayInstance(downR ? "key_r_down" : "key_r", Pt.Zero, 1));
        // Which keys are down, as a two-bit number, for `--debug-state`: 1 left,
        // 2 right, -1 no keys at all. The cheapest way to compare the alternation
        // across ports.
        stage.Metric("keys", (downL ? 1 : 0) + (downR ? 2 : 0));
    }

    /// A fixed 12-second script: three seconds idle, five of steady typing, four of
    /// typing fast enough to steam. A test harness, not tuning — which is why these
    /// numbers are the one thing in this file that is not read from `cat.json`.
    private (double Kps, double SinceKey) DemoInput(double dt)
    {
        _demoClock += dt;
        double t = _demoClock % 12;
        if (t < 3) return (0, t + 10);
        double kps = t < 8 ? 5 : 10;
        return (kps, t % (1 / kps));
    }
}
