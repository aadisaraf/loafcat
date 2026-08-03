namespace LoafCat.Modules;

/// Purring when the cursor is stroked across the cat's head.
///
/// Two conditions, not one. Being *inside* the head region is not petting — a cursor
/// parked on the cat while its owner reads is not a hand. Petting is movement inside
/// the region, which is why the trigger is speed-gated and why it lapses shortly after
/// the cursor stops even without leaving.
public sealed class PettingModule : ICatModule, IAtlasTuned
{
    public string Id => "pet";
    public int TunedGeneration { get; set; } = -1;

    // --- the hit region, read from the head part's own rectangle ------------
    // Not a hardcoded box: a theme with a bigger head gets a bigger petting region for
    // free, and nothing here has to know where this atlas put the head.
    private Pt _centre = Pt.Zero;
    private Sz _radius = new(1, 1);
    private double _halfCanvas = 24;

    // --- constants ----------------------------------------------------------
    private double _moveMin;
    private double _stopDelay;
    private double _leaveDelay;
    private double _lean;
    private double _purrHz;
    private double _purrAmp;
    private double _petSquash = 1;
    private double _attack = 0.1;
    private double _heartPeriod = 1;
    private double _heartRise;
    private double _heartDrift;
    private int _heartSlots;

    // --- state --------------------------------------------------------------
    private double _amp;
    private double _lastStroke;
    private double? _leftAt;
    private double _elapsed;

    /// Logical pixels travelled inside the head region since arriving. Petting engages
    /// only once this clears `_strokeMin`.
    private double _stroked;
    private double _strokeMin = 14;

    /// How strongly a body stroke reads compared with a head scratch.
    private double _bodyResponse = 0.65;
    private double _heartPhase;

    public void Retune(Atlas atlas)
    {
        var b = atlas.Behaviour;
        _halfCanvas = atlas.Canvas / 2;
        if (atlas.Parts.TryGetValue("head", out var head))
        {
            double scale = b.F("pet.ellipse_scale");
            _centre = new Pt(
                head.Origin.X + head.Size.W / 2,
                head.Origin.Y + head.Size.H / 2);
            _radius = new Sz(
                Math.Max(head.Size.W / 2 * scale, 0.001),
                Math.Max(head.Size.H / 2 * scale, 0.001));
        }
        _moveMin = b.F("pet.move_min");
        _stopDelay = b.F("pet.stop_delay");
        _leaveDelay = b.F("pet.leave_delay");
        _lean = b.F("pet.lean");
        _strokeMin = b.F("pet.stroke_min_px");
        _bodyResponse = b.F("pet.body_response");
        _purrHz = b.F("pet.purr_hz");
        _purrAmp = b.F("pet.purr_amp");
        _petSquash = b.F("pet.squash");
        _attack = Math.Max(b.F("pet.attack"), 0.001);
        _heartPeriod = Math.Max(b.F("pet.heart_period"), 0.01);
        _heartRise = b.F("pet.heart_rise");
        _heartDrift = b.F("pet.heart_drift");
        _heartSlots = atlas.Overlays.TryGetValue("heart", out var heart) ? heart.Slots : 0;
    }

    public ModuleOutput Update(in TickContext ctx)
    {
        if (this.TunedAtlas() is null) return ModuleOutput.None;
        var stage = CatStage.Shared;
        double now = Clock.Now;
        _elapsed += ctx.Dt;

        // Being picked up is not being petted. Cut immediately rather than easing — a
        // cat that keeps purring for a third of a second after you grab it reads as a
        // bug, not as affection.
        if (stage.State == CatState.Dragging)
        {
            _amp = 0;
            _lastStroke = 0;
            _leftAt = null;
            return ModuleOutput.None;
        }

        // The cursor arrives relative to the cat's CENTRE; the atlas measures from the
        // top-left corner. One conversion, here, and the ellipse test below is then
        // plain normalised cat-local coordinates.
        var p = new Pt(ctx.Cursor.X + _halfCanvas, ctx.Cursor.Y + _halfCanvas);
        double u = (p.X - _centre.X) / _radius.W;
        double w = (p.Y - _centre.Y) / _radius.H;
        // Anywhere on the cat, not just the head. The head ellipse alone meant
        // stroking the body did nothing, which reads as the cat ignoring you — and
        // `CursorOnCat` is the same dilated silhouette everything else uses, so "can I
        // touch it" and "does it feel it" agree by construction. The head ellipse
        // survives as the SWEET SPOT: strokes there lean and purr harder, which is
        // where a real cat wants to be scratched anyway.
        bool onHead = u * u + w * w <= 1;
        bool inside = onHead || ctx.CursorOnCat;
        bool moving = MathX.Hypot(ctx.CursorVelocity.X, ctx.CursorVelocity.Y) >= _moveMin;

        // Being inside and moving is not yet petting. Merely crossing the cat on the
        // way somewhere else does both, and the cat purring at a cursor in transit
        // reads as broken. Require a deliberate stroke: some distance actually
        // travelled ACROSS the head before it counts.
        if (inside)
        {
            _leftAt = null;
            if (moving)
            {
                _stroked += MathX.Hypot(ctx.CursorVelocity.X, ctx.CursorVelocity.Y) * ctx.Dt;
                if (_stroked >= _strokeMin) _lastStroke = now;
            }
            else
            {
                // Parked mid-stroke: bleed the credit away so resuming after a long
                // pause needs a fresh stroke rather than one twitch.
                _stroked = Math.Max(0, _stroked - _strokeMin * ctx.Dt / Math.Max(_stopDelay, 0.001));
            }
        }
        else if (_leftAt is null)
        {
            _leftAt = now;
            _stroked = 0;     // arriving again starts the stroke over
        }

        bool stalled = now - _lastStroke > _stopDelay;
        bool gone = _leftAt is { } left && now - left > _leaveDelay;
        bool petting = _lastStroke > 0 && !stalled && !gone;

        _amp += ((petting ? 1 : 0) - _amp) * (1 - Math.Exp(-ctx.Dt / _attack));
        stage.Metric("pet.in", inside ? 1 : 0);
        stage.Metric("pet.stroked", _stroked);
        stage.Metric("pet.amp", _amp);
        if (_amp <= 0.002) return ModuleOutput.None;

        var outv = new ModuleOutput();

        // Lean into the hand. Measured from the head's own centre so the lean is
        // toward where the stroking actually is, not toward the whole cat's middle.
        double dx = p.X - _centre.X, dy = p.Y - _centre.Y;
        double d = Math.Max(MathX.Hypot(dx, dy), 0.0001);
        double reach = Math.Min(d / Math.Max(_radius.W, 0.001), 1);
        double sweetSpot = onHead ? 1.0 : _bodyResponse;
        stage.HeadOffset.X += dx / d * _lean * reach * _amp * sweetSpot;
        stage.HeadOffset.Y += dy / d * _lean * 0.5 * reach * _amp;

        // The purr itself: a fast, sub-pixel vibration. It survives the whole-pixel
        // rounding as a 0/1 flicker, which is exactly what a purr looks like at this
        // resolution and is why the amplitude is deliberately under one pixel.
        double purr = Math.Sin(_elapsed * 2 * Math.PI * _purrHz) * _purrAmp * _amp;
        outv.Offset.Y = purr;
        stage.HeadOffset.Y += purr * 0.5;
        outv.Squash = 1 - (1 - _petSquash) * _amp;

        if (_heartSlots > 0)
        {
            _heartPhase += ctx.Dt / _heartPeriod;
            while (_heartPhase >= 1) _heartPhase -= 1;
            for (int i = 0; i < _heartSlots; i++)
            {
                double t = _heartPhase + (double)i / _heartSlots;
                if (t >= 1) t -= 1;
                double spread = (i - (_heartSlots - 1) / 2.0) * _heartDrift * 1.6;
                stage.Overlays.Add(new OverlayInstance(
                    "heart",
                    new Pt(spread + Math.Sin(t * Math.PI * 2 + i) * _heartDrift, -_heartRise * t),
                    Math.Sin(t * Math.PI) * _amp));
            }
            outv.Overlay = "hearts";
        }

        // The state ends when the stroking does; the pose is allowed a few frames to
        // ease out after it. Gating the state on the envelope instead would leave the
        // cat nominally purring for most of a second after the hand left.
        if (petting) outv.State = CatState.Purring;
        return outv;
    }
}
