namespace LoafCat;

/// A critically-damped spring. Used for every easing in the rig.
///
/// Springs rather than tweens because the cat is reacting to a target that moves
/// continuously (the cursor), and a tween would have to be restarted every frame. A
/// spring just chases, which is also what makes the motion read as alive rather than
/// scripted.
public struct Spring(double stiffness, double damping)
{
    public double Value = 0;
    public double Velocity = 0;
    public double Stiffness = stiffness;
    public double Damping = damping;

    public void Step(double target, double dt)
    {
        // Clamp dt so a stalled frame cannot fling the spring across the screen.
        double h = Math.Min(dt, 1.0 / 30.0);
        double force = (target - Value) * Stiffness;
        Velocity = (Velocity + force * h) * Math.Pow(Damping, h * 60);
        Value += Velocity * h;
    }

    public void Snap(double target)
    {
        Value = target;
        Velocity = 0;
    }
}

/// Owns the per-part transforms and the animation state that produces them.
///
/// The rig never redraws art. Every motion here is a transform of the same parts,
/// which is why the whole animation set costs ~16 drawn pieces instead of ~150
/// frames — and why frame-to-frame consistency is structural rather than a thing
/// someone has to maintain.
public sealed class Rig
{
    public Atlas Atlas { get; }

    // --- cursor tracking ---------------------------------------------------
    // Four layers, each chasing the same target at a different rate. Pupils lead,
    // then eyes, then face, then body. That stagger is the entire "alive" effect —
    // move them together and it reads as a sprite being nudged.
    private Spring _pupilX = new(180, 0.72);
    private Spring _pupilY = new(180, 0.72);
    private Spring _eyeX = new(130, 0.74);
    private Spring _eyeY = new(130, 0.74);
    private Spring _headX = new(90, 0.75);
    private Spring _headY = new(90, 0.75);
    private Spring _bodyX = new(40, 0.80);

    /// How far each layer may travel, in logical pixels.
    private readonly double _pupilRange;
    private const double HeadRange = 3.2;
    private const double EyeRange = 1.3;
    private const double BodyRange = 1.0;

    /// Cursor distance at which tracking saturates. Beyond this the cat is already
    /// looking as far as it can, so there is nothing more to express.
    private const double TrackSaturation = 400;

    // --- idle ambience -----------------------------------------------------
    private double _elapsed;
    private double _nextBlink = 2.5;
    private double _blinkUntil = -1;
    private Spring _breathe = new(60, 0.9);
    private readonly Random _random = new();

    // --- tail --------------------------------------------------------------
    private Spring _tailSway = new(55, 0.86);

    // --- squash / stretch --------------------------------------------------
    public double Squash { get; private set; } = 1.0;

    // --- drag deformation ---------------------------------------------------
    // `Squash` is a single uniform number clamped to 0.88...1.14, which is right for
    // breathing and for a landing thump but cannot express being held up by the
    // scruff: that is not a uniform scale, it is a gradient. The head keeps its shape,
    // the torso elongates, and the paws and tail hang furthest. So the drag gets its
    // own channel that is DISTRIBUTED by each part's depth below the held point,
    // rather than fighting the uniform clamp.
    public double DragStretch { get; private set; }

    /// Horizontal elongation, for when the cat is being swung sideways.
    public double DragStretchX { get; private set; }

    private double _dragGrabY;
    private double _dragLeanPx;
    private double _dragHeadLagPx;
    private double _dragHeadSwingShare;
    private double _dragShadowShrink;

    /// Vertical extent of the drawn cat, taken from the atlas rather than assumed, so
    /// depth normalises correctly for a theme whose cat is a different height.
    private readonly double _inkTop;
    private readonly double _inkBottom;

    /// Which deformation rule a part follows. Keyed by name, matching how `Rebuild`
    /// already dispatches — the names are the atlas's contract.
    private enum DragGroup
    {
        Head,    // head, ears, face, eyes, pupils, lids: rigid, so the face never
                 // shears apart into separate pieces
        Soft,    // body, paws, tail: elongate to span their stretched extent
        Shadow,  // stays on the ground and shrinks as the cat leaves it
    }

    private static DragGroup GroupOf(string name) => name switch
    {
        "shadow" => DragGroup.Shadow,
        "body" or "tail" or "paw_l" or "paw_r" => DragGroup.Soft,
        _ => DragGroup.Head,
    };

    public struct Transform()
    {
        public Pt Offset = Pt.Zero;
        public Sz Scale = Sz.One;
        public bool Hidden = false;
    }

    /// Reused between frames rather than rebuilt, so a 120Hz tick does not allocate a
    /// dictionary and ~16 boxed entries every 8 milliseconds.
    private readonly Dictionary<string, Transform> _transforms = [];
    public IReadOnlyDictionary<string, Transform> Transforms => _transforms;

    public Transform TransformFor(string name) =>
        _transforms.TryGetValue(name, out var t) ? t : new Transform();

    public Rig(Atlas atlas)
    {
        Atlas = atlas;
        // The stage outlives the rig, so it is re-pointed at whatever theme is current
        // rather than owning one. The rig is the one object that both launch and a
        // theme switch construct, which makes it the right place to tell the modules
        // which cat they are now driving.
        CatStage.Shared.Publish(atlas);
        // The pupil may travel exactly as far as the sclera allows, no further — a
        // pupil that clips outside its eye is the classic rig tell.
        _pupilRange = atlas.Eye.MaxOffset;

        // Measured, not assumed: the shadow is excluded because it is cast ON the
        // floor rather than part of the body, and including it would put the bottom of
        // the pendulum below the paws.
        double top = double.MaxValue;
        double bottom = 0;
        foreach (var (name, part) in atlas.Parts)
        {
            if (name == "shadow") continue;
            top = Math.Min(top, part.Origin.Y);
            bottom = Math.Max(bottom, part.Origin.Y + part.Size.H);
        }
        _inkTop = double.IsFinite(top) ? top : 0;
        _inkBottom = Math.Max(bottom, _inkTop + 1);

        foreach (string name in atlas.Order) _transforms[name] = new Transform();
    }

    /// `cursor` is the cursor position relative to the cat's centre, in logical px.
    public void Update(double dt, Pt cursor, bool isBlinkSuppressed = false)
    {
        _elapsed += dt;

        // Normalise and saturate. Using the raw vector would make the cat's gaze
        // jitter wildly for small movements far away.
        double dist = Math.Max(MathX.Hypot(cursor.X, cursor.Y), 0.0001);
        double clamped = Math.Min(dist, TrackSaturation) / TrackSaturation;
        double nx = cursor.X / dist * clamped;
        double ny = cursor.Y / dist * clamped;

        _pupilX.Step(nx * _pupilRange, dt);
        _pupilY.Step(ny * _pupilRange, dt);
        _eyeX.Step(nx * EyeRange, dt);
        _eyeY.Step(ny * EyeRange * 0.85, dt);
        _headX.Step(nx * HeadRange, dt);
        _headY.Step(ny * HeadRange * 0.85, dt);
        _bodyX.Step(nx * BodyRange, dt);

        // Breathing: a slow sine on vertical scale. Tiny — 2% — because anything
        // larger reads as panting.
        double breath = Math.Sin(_elapsed * 1.6) * 0.5 + 0.5;
        _breathe.Step(breath, dt);
        double breathScale = 1.0 + _breathe.Value * 0.02;

        // Tail sway, lagging the body. Highest-leverage aliveness per unit of effort
        // in the whole rig, and it costs zero frames.
        _tailSway.Step(Math.Sin(_elapsed * 1.1) * 1.6 + _bodyX.Value * 2.0, dt);

        // Blink on a Poisson-ish schedule. Perfectly periodic blinking is one of the
        // strongest cues that something is a looping GIF rather than a creature.
        bool blinking = false;
        if (!isBlinkSuppressed)
        {
            if (_elapsed >= _nextBlink)
            {
                _blinkUntil = _elapsed + 0.12;
                _nextBlink = _elapsed + 2.2 + _random.NextDouble() * (6.5 - 2.2);
            }
            blinking = _elapsed < _blinkUntil;
        }

        Rebuild(breathScale, blinking);
    }

    private void Rebuild(double breathScale, bool blinking)
    {
        // Volume-preserving squash: widen as it shortens. Without the inverse the cat
        // visibly loses mass at the extremes.
        // Breathing is a continuous 1.00-1.02, which would undo the drag quantiser's
        // whole-pixel work and reintroduce the smearing. A cat being carried is not
        // idly breathing anyway, so it stands down during a drag.
        double sy = (DragStretch > 0 ? 1.0 : breathScale) * Squash;
        double sx = 1.0 / Math.Sqrt(sy);
        double bodyLift = (sy - 1.0) * 8.0;

        // What the feature modules asked for this frame. They post offsets to the
        // stage rather than reaching into the rig, so several can move the same part
        // in one frame and simply add.
        var stage = CatStage.Shared;

        foreach (string name in Atlas.Order)
        {
            var tr = new Transform();

            switch (name)
            {
                case "body":
                case "paw_l":
                case "paw_r":
                case "shadow":
                    tr.Offset.X = _bodyX.Value;
                    if (name == "body")
                    {
                        tr.Scale = new Sz(sx, sy);
                        tr.Offset.Y = -bodyLift * 0.5;
                    }
                    if (name == "shadow")
                    {
                        // Shadow scales inversely: as the cat rises it shrinks and
                        // darkens less. This is what sells the lift as vertical motion.
                        tr.Scale = new Sz(1 + (1 - sy) * 1.8, 1);
                    }
                    break;

                case "head":
                case "ear_l":
                case "ear_r":
                case "face":
                    tr.Offset.X = _headX.Value + _bodyX.Value * 0.5;
                    tr.Offset.Y = _headY.Value - bodyLift;
                    if (name == "ear_l") tr.Offset.X -= _headX.Value * 0.15;
                    if (name == "ear_r") tr.Offset.X += _headX.Value * 0.15;
                    break;

                case "eye_l":
                case "eye_r":
                    tr.Offset.X = _headX.Value + _eyeX.Value + _bodyX.Value * 0.5;
                    tr.Offset.Y = _headY.Value + _eyeY.Value - bodyLift;
                    tr.Hidden = blinking;
                    break;

                case "pupil_l":
                case "pupil_r":
                    // Pupils carry the head, the eye AND their own offset — so they
                    // travel furthest and arrive first. That ordering is the effect.
                    tr.Offset.X = _headX.Value + _eyeX.Value + _bodyX.Value * 0.5 + _pupilX.Value;
                    tr.Offset.Y = _headY.Value + _eyeY.Value - bodyLift + _pupilY.Value;
                    tr.Hidden = blinking;
                    break;

                case "lid_l":
                case "lid_r":
                    tr.Offset.X = _headX.Value + _eyeX.Value + _bodyX.Value * 0.5;
                    tr.Offset.Y = _headY.Value + _eyeY.Value - bodyLift;
                    tr.Hidden = !blinking;
                    break;

                case "tail":
                    tr.Offset.X = _tailSway.Value + _bodyX.Value;
                    tr.Offset.Y = -bodyLift * 0.3;
                    break;
            }

            ApplyDrag(ref tr, name);

            // Module channels, layered on top of the ambient rig. The head channel
            // reaches everything parented to the head, or a lean would tear the face
            // off; the shadow takes only the horizontal component, because a cat
            // bobbing upward should not drag its contact shadow into the air.
            switch (name)
            {
                case "head":
                case "ear_l":
                case "ear_r":
                case "face":
                case "eye_l":
                case "eye_r":
                case "pupil_l":
                case "pupil_r":
                case "lid_l":
                case "lid_r":
                    tr.Offset.X += stage.HeadOffset.X;
                    tr.Offset.Y += stage.HeadOffset.Y;
                    break;
                case "paw_l":
                    tr.Offset.X += stage.PawOffsetL.X;
                    tr.Offset.Y += stage.PawOffsetL.Y;
                    break;
                case "paw_r":
                    tr.Offset.X += stage.PawOffsetR.X;
                    tr.Offset.Y += stage.PawOffsetR.Y;
                    break;
                case "tail":
                    tr.Offset.X += stage.TailOffset.X;
                    tr.Offset.Y += stage.TailOffset.Y;
                    break;
            }
            tr.Offset.X += stage.BodyOffset.X;
            if (name != "shadow") tr.Offset.Y += stage.BodyOffset.Y;

            _transforms[name] = tr;
        }
    }

    /// Folds the hang and the swing into a part's transform.
    ///
    /// Two independent effects share one pass because both are functions of the same
    /// quantity: how far below the held point the part sits.
    private void ApplyDrag(ref Transform tr, string name)
    {
        if (DragStretch == 0 && _dragLeanPx == 0 && DragStretchX == 0) return;
        if (!Atlas.Parts.TryGetValue(name, out var part)) return;

        var group = GroupOf(name);
        double hang = _inkBottom - _dragGrabY;

        // --- vertical: the hang -------------------------------------------
        // Displacement grows with distance BELOW the held point; anything above it is
        // being supported and does not fall.
        double Drop(double y) => DragStretch * Math.Max(0, y - _dragGrabY);

        switch (group)
        {
            case DragGroup.Head:
                // Rigid, and deliberately barely moves: a head that stretched with the
                // body would drag the eyes and muzzle out of register. The small lag
                // is what stops it reading as bolted on.
                tr.Offset.Y += DragStretch * _dragHeadLagPx;
                break;

            case DragGroup.Soft:
            {
                // Scale the part to exactly span its own stretched extent, so the
                // torso ELONGATES to bridge the gap instead of the head and paws
                // sliding apart and tearing the silhouette open.
                double top = part.Origin.Y;
                double bottom = top + part.Size.H;
                // Snap the stretched extent to whole LOGICAL pixels before deriving
                // the scale factor. A continuous factor puts some source rows on two
                // device pixels and others on one, which reads as smearing — worse the
                // further it stretches. Quantising here keeps every row the same size.
                double stretchedTop = MathX.Round(top + Drop(top));
                double stretchedBottom = MathX.Round(bottom + Drop(bottom));
                double height = Math.Max(bottom - top, 0.0001);
                double k = Math.Max(stretchedBottom - stretchedTop, 1) / height;

                // CatView scales about the atlas pivot, so back out the translation
                // that re-lands the part's top edge where the hang wants it.
                double pivotY = Atlas.Pivot(name).Y;
                tr.Offset.Y += stretchedTop - (pivotY + (top - pivotY) * k);
                tr.Scale.H *= k;
                break;
            }

            case DragGroup.Shadow:
            {
                // A lifted cat casts a smaller contact shadow. Selling the lift is
                // most of what makes the drag read as vertical at all.
                double shrink = 1 - Math.Min(1, DragStretch) * _dragShadowShrink;
                tr.Scale.W *= Math.Max(shrink, 0.05);
                break;
            }
        }

        // --- horizontal: the swing ----------------------------------------
        // Depth-quadratic, so the top barely moves and the bottom swings fully. Each
        // part's share is proportional to d^2 - dPrev^2; those increments telescope,
        // so a part's CUMULATIVE share is just d^2 — which is what gets applied here.
        //
        // Rounded to a whole logical pixel: this is an integer perpendicular shear,
        // never a rotation. Rotating a pixel-art layer resamples it off the grid and
        // the jaggies are unrecoverable.
        if (_dragLeanPx != 0)
        {
            double share;
            if (group == DragGroup.Shadow)
            {
                share = 0;                       // the floor does not swing
            }
            else if (group == DragGroup.Head)
            {
                share = _dragHeadSwingShare;     // a pixel of drift, no more
            }
            else
            {
                double anchorY = Atlas.Pivot(name).Y;
                double d = MathX.Clamp((anchorY - _dragGrabY) / Math.Max(hang, 0.0001), 0, 1);
                share = d * d;
            }
            tr.Offset.X += MathX.Round(_dragLeanPx * share);
        }
    }

    /// Drives squash directly — used by drag, landing and the agent-done hop.
    public void SetSquash(double v)
    {
        // Clamped hard. Beyond this range pixel art stops reading as the same
        // character and starts reading as a rendering bug.
        Squash = MathX.Clamp(v, 0.88, 1.14);
    }

    /// Drives the hang and the swing. Separate from `SetSquash` because this one is a
    /// gradient down the body rather than a single uniform number, and because it
    /// legitimately needs to exceed the uniform squash clamp.
    ///
    /// `stretch` is how much longer the hanging part of the cat is, as a fraction;
    /// `grabY` the held point in atlas coordinates, y-down; `leanPx` the horizontal
    /// travel of the LOWEST part, distributed upward as an integer shear.
    public void SetDrag(double stretch, double grabY, double leanPx, double headLagPx,
                        double headSwingShare, double shadowShrink, double stretchX = 0)
    {
        DragStretch = Math.Max(0, stretch);
        DragStretchX = Math.Max(0, stretchX);
        _dragGrabY = MathX.Clamp(grabY, _inkTop, _inkBottom - 1);
        _dragLeanPx = leanPx;
        _dragHeadLagPx = headLagPx;
        _dragHeadSwingShare = headSwingShare;
        _dragShadowShrink = shadowShrink;
    }

    /// Clears the drag channel. Cheaper and more obvious at the call site than passing
    /// six zeroes.
    public void ClearDrag()
    {
        DragStretch = 0;
        DragStretchX = 0;
        _dragLeanPx = 0;
    }
}
