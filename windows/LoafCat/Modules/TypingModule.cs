namespace LoafCat.Modules;

/// The two things typing does to the cat: kneading, and overheating.
///
/// Both run off `ctx.KeysPerSecond`, which is a rate derived from a tick counter and
/// a mouse-event timestamp — never a keycode. There is no code path by which the
/// identity of a key could reach this file. See CLAUDE.md, and the long note at the
/// top of Interop/InputTelemetry.cs: never reach for a keyboard hook to make a
/// reaction here nicer.
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

        _kpsMin = b.F("overheat.kps_min");
        _kpsSpan = Math.Max(b.F("overheat.kps_max") - _kpsMin, 0.001);
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

        // --- the gate -------------------------------------------------------
        if (ctx.KeysPerSecond >= _gateKps) _kneading = true;
        if (_kneading && ctx.SecondsSinceKey > _releaseDelay) _kneading = false;

        // Ease the amplitude rather than the pose: snapping the paws to zero mid
        // stroke reads as a dropped frame, and easing the phase instead would make the
        // strokes slow down, which reads as the cat getting tired.
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
            outv.Offset.Y = -Math.Abs(Math.Sin(_phase * Math.PI)) * _bodyBob * _amp;
            outv.Squash = 1 - Math.Abs(Math.Sin(_phase * Math.PI)) * _squashDepth * _amp;
        }
        else
        {
            _phase = 0;
        }

        // --- heat -----------------------------------------------------------
        // Continuous, never a binary state: zero below kps_min so ordinary typing can
        // never redden the cat, 1 at kps_max, curved in between.
        double t = MathX.Clamp((ctx.KeysPerSecond - _kpsMin) / _kpsSpan, 0, 1);
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
        return outv;
    }
}
