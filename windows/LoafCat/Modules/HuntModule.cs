namespace LoafCat.Modules;

/// Stalking a cursor that is being wiggled like a cat toy.
///
/// The thing this deliberately is NOT is a speed threshold. A speed threshold fires on
/// every ordinary sweep across a wide display, which is most cursor movement, and then
/// the cat is permanently crouched. What actually reads as "prey" is *changing
/// direction*: a real cat ignores a mouse running in a straight line past it and
/// fixates on one that jinks.
///
/// So this is an energy accumulator whose reversal term dwarfs its speed term. The
/// speed term exists only to gate — its ceiling, `gain * excess / (1 - decay)`, is
/// tuned to sit below the trigger for any plausible straight sweep, so speed alone can
/// never pounce the cat. Reversals are what carry it over.
public sealed class HuntModule : ICatModule, IAtlasTuned
{
    public string Id => "hunt";
    public int TunedGeneration { get; set; } = -1;

    // --- constants, every one of them from cat.json -------------------------
    private double _decayPerFrame = 1;
    private double _speedMin;
    private double _speedGain;
    private double _reverseLag;
    private double _reverseDot;
    private double _reverseSpeed;
    private double _reverseBonus;
    private double _refractory;
    private double _accelMin;
    private double _accelGain;
    private double _trigger = 1;
    private double _resetTo;
    private double _crouchTime;
    private double _recoverTime;
    private double _attack = 0.1;
    private double _crouchSquash = 1;
    private double _lean;
    private double _bodyLean;
    private double _pawReach;
    private double _wiggleHz;
    private double _wiggleAmp;

    // --- state --------------------------------------------------------------
    private enum Phase { Idle, Crouch, Recover }
    private Phase _phase = Phase.Idle;
    private double _phaseUntil;
    private double _energy;
    private double _pose;
    private double _elapsed;
    private Pt _lastVelocity = Pt.Zero;
    private double _lastReverse;
    private double _reversals;

    /// A short trail of velocities, so a reversal is measured against where the cursor
    /// was heading a moment ago rather than one frame ago. At 120Hz the frame-to-frame
    /// angle is mostly EMA noise; over ~60ms it is the gesture.
    private readonly List<(double T, Pt V)> _trail = [];
    private const int TrailCap = 48;

    public void Retune(Atlas atlas)
    {
        var b = atlas.Behaviour;
        _decayPerFrame = b.F("hunt.decay_per_frame");
        _speedMin = b.F("hunt.speed_min");
        _speedGain = b.F("hunt.speed_gain_per_frame");
        _reverseLag = b.F("hunt.reverse_lag");
        _reverseDot = b.F("hunt.reverse_dot");
        _reverseSpeed = b.F("hunt.reverse_speed");
        _reverseBonus = b.F("hunt.reverse_bonus");
        _refractory = b.F("hunt.reverse_refractory");
        _accelMin = b.F("hunt.accel_min");
        _accelGain = b.F("hunt.accel_gain_per_frame");
        _trigger = Math.Max(b.F("hunt.trigger"), 0.001);
        _resetTo = b.F("hunt.reset");
        _crouchTime = b.F("hunt.crouch");
        _recoverTime = b.F("hunt.recover");
        _attack = Math.Max(b.F("hunt.attack"), 0.001);
        _crouchSquash = b.F("hunt.squash");
        _lean = b.F("hunt.lean");
        _bodyLean = b.F("hunt.body_lean");
        _pawReach = b.F("hunt.paw_reach");
        _wiggleHz = b.F("hunt.wiggle_hz");
        _wiggleAmp = b.F("hunt.wiggle_amp");
    }

    public ModuleOutput Update(in TickContext ctx)
    {
        if (this.TunedAtlas() is null) return ModuleOutput.None;
        var stage = CatStage.Shared;
        double now = Clock.Now;
        _elapsed += ctx.Dt;

        var v = ctx.CursorVelocity;
        double speed = MathX.Hypot(v.X, v.Y);

        // Direct manipulation wins outright. A cat being dragged or stretched is not
        // also stalking, and the accumulator is emptied rather than frozen so that
        // letting go does not immediately fire whatever built up during the drag.
        bool manipulated = stage.State is CatState.Dragging or CatState.Stretching;
        if (manipulated)
        {
            _energy = 0;
            _phase = Phase.Idle;
        }
        else
        {
            Accumulate(now, ctx.Dt, v, speed);
        }
        _lastVelocity = v;

        // --- pounce state machine -------------------------------------------
        if (_phase == Phase.Idle && _energy >= _trigger)
        {
            _phase = Phase.Crouch;
            _phaseUntil = now + _crouchTime;
            // Not zero: a cat that has just been teased is easier to tease again, and
            // emptying it would make a second pounce need the full build-up.
            _energy = _trigger * _resetTo;
        }
        switch (_phase)
        {
            case Phase.Crouch:
                if (now >= _phaseUntil) { _phase = Phase.Recover; _phaseUntil = now + _recoverTime; }
                break;
            case Phase.Recover:
                if (now >= _phaseUntil) _phase = Phase.Idle;
                break;
        }

        // Ramp in over `attack`; ramp out over `recover`, which is a time constant of a
        // third of it so the pose has visibly finished when the phase does.
        double tau = _phase == Phase.Crouch ? _attack : _recoverTime / 3;
        _pose += ((_phase == Phase.Crouch ? 1 : 0) - _pose)
                 * (1 - Math.Exp(-ctx.Dt / Math.Max(tau, 0.001)));

        stage.Metric("hunt.e", _energy);
        stage.Metric("hunt.spd", speed);
        stage.Metric("hunt.rev", _reversals);

        if (_pose <= 0.002) return ModuleOutput.None;

        // --- the crouch ------------------------------------------------------
        var outv = new ModuleOutput();
        double dist = Math.Max(MathX.Hypot(ctx.Cursor.X, ctx.Cursor.Y), 0.0001);
        var dir = new Pt(ctx.Cursor.X / dist, ctx.Cursor.Y / dist);
        // The haunch wiggle every cat does before it commits.
        double wiggle = Math.Sin(_elapsed * 2 * Math.PI * _wiggleHz) * _wiggleAmp * _pose;

        stage.HeadOffset.X += dir.X * _lean * _pose + wiggle;
        stage.HeadOffset.Y += dir.Y * _lean * 0.6 * _pose;
        stage.PawOffsetL.X += dir.X * _pawReach * _pose;
        stage.PawOffsetR.X += dir.X * _pawReach * _pose;
        stage.TailOffset.X -= dir.X * _pawReach * _pose;

        outv.Offset.X = dir.X * _bodyLean * _pose + wiggle * 0.5;
        // Squash alone lowers the cat: the rig lifts the body by (scale - 1), so a
        // scale below 1 drops it. Getting low IS the crouch.
        outv.Squash = 1 - (1 - _crouchSquash) * _pose;
        // The state lasts exactly as long as the crouch and the return; the last few
        // frames of the pose settling are not "hunting" any more.
        if (_phase != Phase.Idle) outv.State = CatState.Hunting;
        return outv;
    }

    private void Accumulate(double now, double dt, Pt v, double speed)
    {
        // Decay is quoted per frame at a nominal 60fps and normalised by dt, so the
        // accumulator has the same half-life whatever rate the tick actually runs at.
        _energy *= Math.Pow(_decayPerFrame, dt * 60);

        // The gate term. Bounded by construction: sustained excess speed E settles at
        // E * gain / (1 - decay), which is deliberately under the trigger.
        if (speed > _speedMin)
        {
            _energy += (speed - _speedMin) * _speedGain * dt * 60;
        }

        // The term that actually matters. Compared against the heading from
        // `reverseLag` ago, and only counted once per refractory window — a single
        // reversal spans several 120Hz frames and would otherwise be paid for twice.
        _trail.Add((now, v));
        if (_trail.Count > TrailCap) _trail.RemoveRange(0, _trail.Count - TrailCap);

        // The most recent sample that is already at least `reverseLag` old, which is
        // Swift's `last(where:)` over a list ordered oldest-first.
        for (int i = _trail.Count - 1; i >= 0; i--)
        {
            if (now - _trail[i].T < _reverseLag) continue;
            var old = _trail[i];
            double oldSpeed = MathX.Hypot(old.V.X, old.V.Y);
            if (Math.Min(speed, oldSpeed) > _reverseSpeed && now - _lastReverse > _refractory)
            {
                double dot = (v.X * old.V.X + v.Y * old.V.Y) / (speed * oldSpeed);
                if (dot < _reverseDot)
                {
                    _energy += _reverseBonus;
                    _lastReverse = now;
                    _reversals += 1;
                }
            }
            break;
        }

        // A smaller nudge for sheer violence of movement, which catches a flick that is
        // over before it has time to reverse.
        if (dt > 0)
        {
            double accel = MathX.Hypot(v.X - _lastVelocity.X, v.Y - _lastVelocity.Y) / dt;
            if (accel > _accelMin)
            {
                _energy += (accel - _accelMin) * _accelGain * dt * 60;
            }
        }
    }
}
