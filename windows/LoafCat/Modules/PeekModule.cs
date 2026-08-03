using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.Versioning;
using System.Windows.Forms;
using LoafCat.Interop;

namespace LoafCat;

/// Which side of the display the cat parks against.
public enum PeekEdge
{
    Left,
    Right,
}

/// The dwell-to-arm gesture, as a pure state machine over time and one number.
///
/// Pulled out of the module deliberately. Nobody can drag a cat on a CI runner, so the
/// only way this gets checked on every commit is if the decision is separable from the
/// window, the cursor and the clock — the same reason `SelfInstall.Decide` is its own
/// function. `--demo-peek` drives it with a scripted time base on both platforms, and
/// the two ports are expected to agree exactly.
public struct Arming()
{
    public double ArmMs = 320;
    public double DisarmMs = 80;

    private PeekEdge? _dwellEdge = null;
    private double _dwellSince = 0;
    private double? _leftZoneAt = null;

    /// The edge the snap is armed for, or null. This is also precisely the condition
    /// under which the indicator is visible, which is what makes "no line means no
    /// snap" a fact rather than a hope.
    public PeekEdge? Armed { get; private set; } = null;

    public void Step(double cursorX, double minX, double maxX, double band, double now)
    {
        PeekEdge? edge =
            cursorX <= minX + band ? PeekEdge.Left :
            cursorX >= maxX - band ? PeekEdge.Right : null;

        if (edge is not { } e)
        {
            // A little hysteresis before disarming. Without it one pixel of hand
            // wobble at the boundary strobes the capsule on and off.
            if (Armed is not null || _dwellEdge is not null)
            {
                _leftZoneAt ??= now;
                if (now - _leftZoneAt.Value >= DisarmMs / 1000) Clear();
            }
            return;
        }

        _leftZoneAt = null;
        if (_dwellEdge != e)
        {
            _dwellEdge = e;
            _dwellSince = now;
            Armed = null;
        }
        // Strictly greater: arming exactly ON the threshold would make the result
        // depend on whether a tick happened to land there, and the two ports do not
        // tick at the same instants.
        if (now - _dwellSince > ArmMs / 1000) Armed = e;
    }

    public void Clear()
    {
        _dwellEdge = null;
        Armed = null;
        _leftZoneAt = null;
    }
}

/// Parking the cat against a screen edge, so it peers in from the side instead of
/// sitting on top of what you are looking at.
///
/// The direct counterpart of Modules/PeekModule.swift, in the same order, with the
/// same comments where the reasoning is the same. Read them side by side.
///
/// Two ways in, and they are deliberately different in how sticky they are:
///
/// 1. **You snap it there.** Drag the cat until the cursor rests in the band at the
///    screen edge; after `arm_ms` the snap arms and a short white capsule fades in
///    where the cat will land. Let go and it parks. Let go anywhere else and it just
///    falls where you dropped it, exactly as before. A manual park stays until you
///    drag it out — you asked for it, so nothing takes it away.
/// 2. **A full-screen video parks it for you.** Temporary: the cat remembers where it
///    stood and walks back when the video ends.
///
/// **The dwell is the whole gesture**, and it is what makes "come in a certain way and
/// it won't snap" work. Brushing the edge on the way past never arms, because arming
/// takes time standing still. It is also honest in both directions: the capsule only
/// appears once the snap is armed, so *no line means no snap* and you can always tell
/// before you let go.
///
/// It is not a modifier key, and that is forced rather than chosen. `GetAsyncKeyState`
/// and `GetKeyState` are banned outright by `scripts/check-privacy.sh`, so there is no
/// way to read Option/Alt that both ports could share — and this is the case where the
/// constraint gives the better answer anyway, since dwell-to-tile is what macOS itself
/// switched to.
[SupportedOSPlatform("windows")]
public sealed class PeekModule(CatWindow window) : ICatModule, IAtlasTuned
{
    public string Id => "peek";
    public int TunedGeneration { get; set; } = -1;

    private readonly CatWindow _window = window;
    private readonly FullscreenWatch _watch = new();
    private SnapIndicator? _indicator;

    // --- tuning, all of it from cat.json ------------------------------------
    // Lengths are LOGICAL pixels of the 48px canvas and are multiplied by the render
    // scale at the point of use, so the edge band and the parked reveal grow with the
    // cat. A bigger cat is a bigger target and deserves a wider band.
    private double _edgeZonePx = 12;
    private double _armMs = 320;
    private double _disarmMs = 80;
    private double _revealPx = 20;
    private double _slideRate = 11;
    private double _settlePt = 0.35;
    private double _bodyTuckPx = 1.5;
    private double _headLeanPx = 3;
    private double _headRisePx = 1;
    private double _gripPx = 1.5;
    private double _bobPx = 1.5;
    private double _bobHz = 0.42;
    private double _indicatorWPx = 3;
    private double _indicatorFadeMs = 120;

    /// The cat's ink inside the 48px canvas, so `reveal_px` means "this much cat" and
    /// not "this much canvas, most of which is transparent".
    private double _inkMinX;
    private double _inkMaxX = 48;
    private double _inkHeight = 48;

    /// Parts that are drawn but do not count as "cat" when deciding how much of it to
    /// leave on screen.
    ///
    /// The shadow for the obvious reason. The tail because it is an APPENDAGE that
    /// reaches far past the body — 30..46 against a body that stops at 36 — so
    /// measuring the reveal against it spends a left-edge park's whole budget on tail
    /// and on the empty notch between tail and flank, and cuts the face in half.
    /// Excluding it is also what makes the two edges symmetric: 9..24 on the right,
    /// 24..39 on the left, one whole eye and one whole ear either way. The tail still
    /// gets drawn, and on a left park it is now the part hanging off the screen, which
    /// is exactly where a tail should be.
    private static readonly HashSet<string> NotCat = ["shadow", "tail", "tail_hot"];

    public void Retune(Atlas atlas)
    {
        double V(string k, double d) => atlas.Tune("peek", k, d);
        _edgeZonePx = V("edge_zone_px", _edgeZonePx);
        _armMs = V("arm_ms", _armMs);
        _disarmMs = V("disarm_ms", _disarmMs);
        _revealPx = V("reveal_px", _revealPx);
        _slideRate = V("slide_rate", _slideRate);
        _settlePt = V("settle_pt", _settlePt);
        _bodyTuckPx = V("body_tuck_px", _bodyTuckPx);
        _headLeanPx = V("head_lean_px", _headLeanPx);
        _headRisePx = V("head_rise_px", _headRisePx);
        _gripPx = V("grip_px", _gripPx);
        _bobPx = V("bob_px", _bobPx);
        _bobHz = V("bob_hz", _bobHz);
        _indicatorWPx = V("indicator_w_px", _indicatorWPx);
        _indicatorFadeMs = V("indicator_fade_ms", _indicatorFadeMs);

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var (name, p) in atlas.Parts)
        {
            if (NotCat.Contains(name)) continue;
            minX = Math.Min(minX, p.Origin.X);
            maxX = Math.Max(maxX, p.Origin.X + p.Size.W);
            minY = Math.Min(minY, p.Origin.Y);
            maxY = Math.Max(maxY, p.Origin.Y + p.Size.H);
        }
        if (minX <= maxX)
        {
            _inkMinX = minX;
            _inkMaxX = maxX;
            _inkHeight = Math.Max(maxY - minY, 1);
        }
    }

    // --- how the cat came to be parked --------------------------------------
    // Worth distinguishing, because it decides what takes it back out again.
    private enum ParkKind
    {
        None,
        Manual,     // you put it there; only you take it away
        Auto,       // a video put it there; the video takes it away
    }
    private ParkKind _park = ParkKind.None;
    private PeekEdge _parkEdge = PeekEdge.Right;
    private bool IsParked => _park != ParkKind.None;

    /// Where the cat stood before a video moved it, so it can be put back. Cleared the
    /// moment the user drags, because then they have chosen a new home and putting it
    /// back would be overruling them.
    private double? _preParkX;

    /// The slide's authoritative position, in pixels, kept apart from the window's own
    /// because the window's is quantised. Null whenever the window is not ours to move
    /// — while it is being dragged, and while there is nowhere to go.
    private double? _slideX;

    /// Set when the user drags out of an automatic park. Suppresses re-parking until
    /// the full-screen window goes away, or the cat would spring straight back to the
    /// edge and there would be no way to keep it out.
    private bool _autoOverridden;

    // --- the arm state machine ----------------------------------------------
    private bool _wasDragging;
    private Arming _arming = new();
    private PeekEdge? ArmedEdge => _arming.Armed;

    // --- presentation --------------------------------------------------------
    private double _indicatorAlpha;
    private double _bobPhase;
    /// 0 while free, 1 when fully parked. Drives the lean, so the cat does not snap
    /// into its peeking pose before it has arrived at the edge.
    private double _settled;
    private PeekEdge? _lastEdge;
    private PeekEdge? _lastArmed;

    private readonly bool _demoRequested =
        Environment.GetCommandLineArgs().Contains("--demo-peek");
    private bool _demoRan;

    // MARK: - Settings

    /// Both default ON. Someone who finds either annoying must be able to switch it
    /// off without switching the other off with it.
    public static bool AutoPeekEnabled => Prefs.GetBool("peekFullscreen", true);
    public static bool SnapOnDragEnabled => Prefs.GetBool("peekSnapDrag", true);

    /// "Centre on screen" has to mean it, so the menu item clears any park through
    /// here rather than fighting the easing for the rest of the session.
    public void ReleasePark()
    {
        _park = ParkKind.None;
        _preParkX = null;
        _slideX = null;
        _arming.Clear();
    }

    // MARK: - Tick

    public ModuleOutput Update(in TickContext ctx)
    {
        if (this.TunedAtlas() is not { } atlas) return ModuleOutput.None;
        var stage = CatStage.Shared;
        double now = Clock.Now;
        var display = Screens.Holding(ctx.Frame);
        var work = display.Work;

        if (_demoRequested && !_demoRan)
        {
            _demoRan = true;
            PeekDemo.Run(this, atlas, _window, work, ctx.Scale);
        }

        _watch.Poll(display.Frame, now);
        bool busy = _watch.FullscreenBusy;
        stage.FullscreenBusy = busy;
        stage.Metric("peek.fs", _watch.Covering ? 1 : 0);
        stage.Metric("peek.awake", _watch.Awake ? 1 : 0);

        // The one-frame lag on `stage.State` is exactly what this wants: it lets the
        // module notice a drag without knowing which module owns dragging.
        bool dragging = stage.State == CatState.Dragging;
        bool dragEnded = _wasDragging && !dragging;
        _wasDragging = dragging;

        // --- being carried overrides everything ------------------------------
        if (dragging)
        {
            if (_park == ParkKind.Auto) _autoOverridden = true;
            if (IsParked)
            {
                _park = ParkKind.None;
                _preParkX = null;
            }
            StepArming(in ctx, work, now);
        }
        else if (dragEnded)
        {
            if (ArmedEdge is { } edge && SnapOnDragEnabled)
            {
                _park = ParkKind.Manual;
                _parkEdge = edge;
                _preParkX = null;       // a manual park has nowhere to go back to
            }
            _arming.Clear();
        }
        else
        {
            _arming.Clear();
        }

        // --- the automatic half ----------------------------------------------
        if (!busy) _autoOverridden = false;
        if (!dragging)
        {
            if (busy && AutoPeekEnabled && !_autoOverridden && _park == ParkKind.None)
            {
                _preParkX = ctx.Frame.X;
                _park = ParkKind.Auto;
                _parkEdge = NearerEdge(ctx.Frame, work);
            }
            else if (_park == ParkKind.Auto && (!busy || !AutoPeekEnabled))
            {
                // Either the video ended or the setting was just turned off. Both mean
                // the same thing to the cat: walk back. The target below falls back to
                // `_preParkX`.
                _park = ParkKind.None;
            }
        }

        // --- move the window --------------------------------------------------
        double? target = IsParked
            ? ParkedX(_parkEdge, work, atlas, ctx.Scale)
            : _preParkX;

        if (dragging || target is null)
        {
            _slideX = null;             // the hand owns the window, or nobody does
        }
        else
        {
            // Accumulated in a double of our own and rounded only on the way out.
            //
            // Reading the position back off the window each frame instead loses every
            // sub-pixel step to quantisation — and because an exponential approach
            // makes the steps smaller the closer it gets, the last fraction is never
            // travelled. Measured on the macOS build before the fix: walking home to
            // x=727 it stopped at 728 and sat there for the rest of the run.
            double x = _slideX ?? ctx.Frame.X;
            double d = target.Value - x;
            if (Math.Abs(d) < _settlePt)
            {
                x = target.Value;
                if (!IsParked) _preParkX = null;
            }
            else
            {
                x += d * Math.Min(1, _slideRate * ctx.Dt);
            }
            _slideX = x;
            double put = MathX.Round(x);
            if (ctx.Frame.X != put) _window.SetOrigin(put, ctx.Frame.Y);
        }

        // --- how parked does it look ------------------------------------------
        double want = IsParked ? 1 : 0;
        _settled += (want - _settled) * Math.Min(1, _slideRate * ctx.Dt);
        stage.Metric("peek.settled", _settled);
        stage.Metric("peek.armed", ArmedEdge is null ? 0 : 1);
        stage.Metric("peek.x", ctx.Frame.X);

        DrawIndicator(in ctx, work);

        if (_settled <= 0.002) return ModuleOutput.None;
        if ((IsParked ? _parkEdge : _lastEdge) is not { } edgeNow) return ModuleOutput.None;
        _lastEdge = edgeNow;

        // The pose, and the whole thing rests on ONE idea: the body tucks a little
        // further behind the edge while the head cranes the other way, out past it. It
        // is the DIFFERENCE between those two that reads as an animal looking round a
        // corner.
        //
        // The first version moved the whole cat inward instead, which does nothing but
        // put more cat on screen — the opposite of peeking, and it looked like a window
        // had sliced the cat rather than the cat had hidden. Combined with a reveal that
        // left 54% of the ink showing, there was no peek there at all.
        //
        // Offsets rather than new art, so nothing here needs a sprite that does not
        // already exist — and so a theme retunes the pose in the same JSON diff that
        // retunes everything else.
        _bobPhase += ctx.Dt * _bobHz;
        while (_bobPhase >= 1) _bobPhase -= 1;

        var outv = new ModuleOutput();
        double toEdge = edgeNow == PeekEdge.Right ? 1 : -1;
        outv.Offset.X = toEdge * _bodyTuckPx * _settled;
        outv.Offset.Y = Math.Sin(_bobPhase * 2 * Math.PI) * _bobPx * _settled;
        stage.HeadOffset.X -= toEdge * _headLeanPx * _settled;
        stage.HeadOffset.Y -= _headRisePx * _settled;
        // The paw hooked over the edge. An overlay rather than a body part, which gets
        // it three things for free: it is not in the draw order or the hit mask, it can
        // be faded in with the park, and — because overlays do not take `BodyOffset` —
        // it stays pinned to the screen edge while the body tucks away behind it. That
        // last one is the whole gag: the cat slides back, the paw does not let go.
        string grip = edgeNow == PeekEdge.Right ? "grip_r" : "grip_l";
        if (atlas.Overlays.ContainsKey(grip))
        {
            stage.Overlays.Add(new OverlayInstance(grip, Pt.Zero, _settled));
        }
        // The paw on the side still showing lifts to the edge, as if holding on to it.
        // The other one is behind the screen edge and would be lifting in private.
        if (edgeNow == PeekEdge.Right) stage.PawOffsetL.Y -= _gripPx * _settled;
        else stage.PawOffsetR.Y -= _gripPx * _settled;
        if (IsParked) outv.State = CatState.Peeking;
        return outv;
    }

    // MARK: - Arming

    private void StepArming(in TickContext ctx, Rect work, double now)
    {
        if (!SnapOnDragEnabled)
        {
            _arming.Clear();
            return;
        }
        // The cursor, recovered from the frame and the cat-relative reading the tick
        // already computed. No second cursor call, and no new plumbing.
        _arming.ArmMs = _armMs;
        _arming.DisarmMs = _disarmMs;
        _arming.Step(ctx.Frame.MidX + ctx.Cursor.X * ctx.Scale,
                     work.MinX, work.MaxX, _edgeZonePx * ctx.Scale, now);
    }

    // MARK: - Geometry

    /// Auto-peek goes to whichever edge the cat is already nearest, right on a tie —
    /// so a cat that lives on the left does not get flung across the display the
    /// moment a video starts.
    public static PeekEdge NearerEdge(Rect frame, Rect work) =>
        frame.MidX - work.MinX < work.MaxX - frame.MidX ? PeekEdge.Left : PeekEdge.Right;

    private double ParkedX(PeekEdge edge, Rect work, Atlas atlas, double scale) =>
        ParkedX(edge, work.MinX, work.MaxX, atlas.Layout.PadX,
                _inkMinX, _inkMaxX, _revealPx, scale);

    /// Window origin that leaves exactly `reveal_px` of the cat's INK on screen.
    ///
    /// Measured off the ink and not the window, because the window carries a
    /// transparent margin for the speech bubble — parking by the window edge would
    /// leave the margin on screen and the cat entirely off it.
    ///
    /// Pure, and for the same reason `Arming` is: it is the other half of what
    /// `--demo-peek` has to be able to assert without a screen.
    public static double ParkedX(PeekEdge edge, double minX, double maxX, double padX,
                                 double inkMinX, double inkMaxX,
                                 double revealPx, double scale) =>
        edge == PeekEdge.Right
            ? maxX - (revealPx + padX + inkMinX) * scale
            : minX + (revealPx - padX - inkMaxX) * scale;

    // MARK: - The indicator

    private void DrawIndicator(in TickContext ctx, Rect work)
    {
        double want = ArmedEdge is null ? 0 : 1;
        double step = ctx.Dt / Math.Max(_indicatorFadeMs / 1000, 0.001);
        _indicatorAlpha += MathX.Clamp(want - _indicatorAlpha, -step, step);

        if (_indicatorAlpha <= 0.001)
        {
            _indicator?.Hide();
            return;
        }
        if ((ArmedEdge ?? _lastArmed) is not { } edge) return;
        _lastArmed = edge;

        double w = MathX.Round(_indicatorWPx * ctx.Scale);
        double h = MathX.Round(_inkHeight * ctx.Scale);
        double x = edge == PeekEdge.Right ? work.MaxX - w : work.MinX;
        var rect = new Rect(x, MathX.Round(ctx.Frame.MidY - h / 2), w, h);

        _indicator ??= new SnapIndicator();
        _indicator.Present(rect, _indicatorAlpha);
    }

    // Read by the scripted demo, which needs the tuning it is asserting against.
    internal (double EdgeZonePx, double ArmMs, double DisarmMs, double RevealPx,
              double InkMinX, double InkMaxX) DemoTuning =>
        (_edgeZonePx, _armMs, _disarmMs, _revealPx, _inkMinX, _inkMaxX);
}

// MARK: - Scripted verification

/// `--demo-peek`. Asserts the gesture and the parked geometry without a hand.
///
/// The same script as the macOS build's, against the same pure `Arming` and `ParkedX`,
/// so the two ports can be compared by reading two logs rather than by trusting that
/// two translations of a state machine came out the same. They are expected to agree
/// exactly — there is no spring here to disagree about, only thresholds.
///
/// The last check differs from the macOS one, and deliberately. There the open question
/// was whether AppKit would let a borderless panel hang off the side of the display at
/// all; here `UpdateLayeredWindow` is simply handed a position and no window manager
/// argues. What CAN undo a park on this platform is `CatWindow.ClampIntoView`, which
/// drags a fully-off-display window back on a monitor change — so what is asserted is
/// that a parked cat still overlaps the work area and is therefore left alone.
[SupportedOSPlatform("windows")]
internal static class PeekDemo
{
    internal static void Run(PeekModule m, Atlas atlas, CatWindow window,
                             Rect work, double scale)
    {
        int failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failures++;
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name}"
                              + (detail.Length == 0 ? "" : $"  — {detail}"));
        }

        var t = m.DemoTuning;
        double band = t.EdgeZonePx * scale;
        double dwell = t.ArmMs / 1000, grace = t.DisarmMs / 1000;
        double now = 0;
        var a = new Arming { ArmMs = t.ArmMs, DisarmMs = t.DisarmMs };
        void Hold(double x, double seconds)
        {
            double end = now + seconds;
            while (now < end)
            {
                a.Step(x, work.MinX, work.MaxX, band, now);
                now += 1.0 / 120;
            }
        }
        double inBandR = work.MaxX - 2, inBandL = work.MinX + 2, middle = work.MidX;

        Console.WriteLine($"# demo: peek — screen {(int)work.W}x{(int)work.H} at "
                          + $"({(int)work.MinX},{(int)work.MinY}), scale {(int)scale}x, "
                          + $"band {(int)band}pt, arm {(int)t.ArmMs}ms");

        // 1. Brushing the edge on the way past must NOT arm. This is the gesture the
        //    user asked for by name: come in a certain way and it will not snap.
        Hold(middle, 0.20);
        Hold(inBandR, dwell * 0.5);
        Hold(middle, 0.20);
        Check("a flick through the band never arms", a.Armed is null);

        // 2. Resting there does.
        Hold(inBandR, dwell + 0.05);
        Check("dwelling at the right edge arms right", a.Armed == PeekEdge.Right);

        // 3. A wobble out of the band is forgiven; leaving properly is not.
        Hold(middle, grace * 0.4);
        Check("a brief wobble keeps it armed", a.Armed == PeekEdge.Right);
        Hold(middle, grace + 0.05);
        Check("leaving the band disarms", a.Armed is null);

        // 4. The other edge works and the dwell restarts when you switch.
        Hold(inBandL, dwell + 0.05);
        Check("dwelling at the left edge arms left", a.Armed == PeekEdge.Left);
        Hold(inBandR, dwell * 0.5);
        Check("switching edges restarts the dwell", a.Armed is null);
        Hold(inBandR, dwell);
        Check("and arms once the new dwell completes", a.Armed == PeekEdge.Right);

        // 5. The parked position leaves exactly `reveal_px` of INK on screen — not of
        //    canvas, most of which is the transparent bubble margin.
        double padX = atlas.Layout.PadX;
        double want = t.RevealPx * scale;
        double pr = PeekModule.ParkedX(PeekEdge.Right, work.MinX, work.MaxX, padX,
                                       t.InkMinX, t.InkMaxX, t.RevealPx, scale);
        double pl = PeekModule.ParkedX(PeekEdge.Left, work.MinX, work.MaxX, padX,
                                       t.InkMinX, t.InkMaxX, t.RevealPx, scale);
        double shownR = work.MaxX - (pr + (padX + t.InkMinX) * scale);
        double shownL = pl + (padX + t.InkMaxX) * scale - work.MinX;
        Check($"parked right shows {(int)t.RevealPx}px of cat", Math.Abs(shownR - want) < 0.01,
              $"{shownR:F2}pt vs {want:F2}pt");
        Check($"parked left shows {(int)t.RevealPx}px of cat", Math.Abs(shownL - want) < 0.01,
              $"{shownL:F2}pt vs {want:F2}pt");

        // 6. WHICH parts the cut lands between, which is the whole difference between a
        //    peek and a cat with a slice taken off it. One eye showing and one hidden
        //    is the thing being aimed at; at reveal 20 both were on screen and it read
        //    as a window clipping a cat. Retuning past that is a design change and
        //    should have to argue with a failing check first.
        // Both edges, separately. They are NOT mirror images of each other — the cat
        // carries a tail on one side and nothing on the other — and assuming they were
        // is exactly how a left-edge park came to spend its whole reveal on tail and
        // cut the face in half.
        double seenTo = t.InkMinX + t.RevealPx;     // right park: 0..seenTo is on screen
        double seenFrom = t.InkMaxX - t.RevealPx;   // left park: seenFrom.. is on screen
        // Spelled out rather than as a ternary on a separate `ok`: nullable flow
        // analysis cannot see through that and rejects the dereference.
        bool Lo(string n, out double lo)
        {
            if (atlas.Parts.TryGetValue(n, out var p)) { lo = p.Origin.X; return true; }
            lo = 0;
            return false;
        }
        bool Hi(string n, out double hi)
        {
            if (atlas.Parts.TryGetValue(n, out var p))
            {
                hi = p.Origin.X + p.Size.W;
                return true;
            }
            hi = 0;
            return false;
        }
        bool ShowsR(string n) => Hi(n, out double hi) && hi <= seenTo + 1;
        bool HidesR(string n) => !Lo(n, out double lo) || lo >= seenTo;
        bool ShowsL(string n) => Lo(n, out double lo) && lo >= seenFrom - 1;
        bool HidesL(string n) => !Hi(n, out double hi) || hi <= seenFrom;

        Check("right park: the near eye and paw are on screen",
              ShowsR("eye_l") && ShowsR("paw_l"));
        Check("right park: the far eye, far paw and tail are not",
              HidesR("eye_r") && HidesR("paw_r") && HidesR("tail"));
        Check("left park: the near eye and paw are on screen",
              ShowsL("eye_r") && ShowsL("paw_r"));
        Check("left park: the far eye and far paw are not",
              HidesL("eye_l") && HidesL("paw_l"));
        Check("the two edges show the same amount of cat",
              Math.Abs((seenTo - t.InkMinX) - (t.InkMaxX - seenFrom)) < 0.001,
              "which is only true because the tail is excluded from the ink");
        Check("a gripping paw exists for each edge",
              atlas.Overlays.ContainsKey("grip_l") && atlas.Overlays.ContainsKey("grip_r"),
              "");

        // 7. A parked cat must still overlap the work area, or ClampIntoView will
        //    haul it back the next time a monitor is plugged in and the park will
        //    silently stop working on exactly the machines that have two screens.
        var f = window.Frame;
        var atR = new Rect(pr, f.Y, f.W, f.H);
        var atL = new Rect(pl, f.Y, f.W, f.H);
        Check("a right-parked cat is not fully off-display", atR.IntersectionArea(work) > 0);
        Check("a left-parked cat is not fully off-display", atL.IntersectionArea(work) > 0);

        Console.WriteLine($"# demo: peek band={band:F0}pt arm={t.ArmMs:F0}ms "
                          + $"disarm={t.DisarmMs:F0}ms reveal={want:F0}pt "
                          + $"parkedR={pr:F1} parkedL={pl:F1}");
        Console.WriteLine(failures == 0
            ? "# demo: PASS — the gesture arms only on a dwell, and the cat parks where claimed"
            : $"# demo: FAIL — {failures} check(s) failed");
        Console.Out.Flush();
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}

// MARK: - The edge capsule

/// The line that says a snap is armed.
///
/// A separate window rather than something drawn into the cat's, because it has to be
/// at the screen edge and the cat is not — and because it must never take a click,
/// which `WS_EX_TRANSPARENT` guarantees outright.
///
/// Deliberately system chrome and not cat art: a capsule, the same shape Windows and
/// macOS use to say "this is where it lands". The pixel-grid rules that govern every
/// sprite do not apply to it, and pretending otherwise would make it look like a bug
/// rather than like the OS.
///
/// Uses `Form.Opacity` — i.e. `SetLayeredWindowAttributes` — rather than the
/// `UpdateLayeredWindow` path the cat itself needs. The cat needs per-pixel alpha
/// because it is a silhouette; this is one flat shape at one uniform alpha, and the
/// simpler call is the whole of what it requires.
[SupportedOSPlatform("windows")]
internal sealed class SnapIndicator : Form
{
    internal SnapIndicator()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        // Not scaled by WinForms: the bounds handed in are already in the physical
        // pixels the cat window is placed in, and letting the framework scale them
        // again would double-apply the display factor.
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.White;
        Opacity = 0;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Layered (for Opacity), transparent (never takes a click, ever),
            // no-activate (clicking near it must not steal focus from the editor),
            // and tool-window (out of Alt-Tab and the taskbar) — the same four the cat
            // window needs, for the same four reasons.
            cp.ExStyle |= (int)(Win32.WsExLayered | Win32.WsExTransparent
                              | Win32.WsExNoActivate | Win32.WsExToolWindow);
            return cp;
        }
    }

    /// Not called `Show` — that is `Form.Show`, and one of the two would end up
    /// calling the other by accident.
    internal void Present(Rect r, double alpha)
    {
        var bounds = new Rectangle((int)r.X, (int)r.Y, (int)r.W, (int)r.H);
        if (Bounds != bounds)
        {
            Bounds = bounds;
            // Rounded ends. A GraphicsPath region has hard edges rather than
            // antialiased ones, which at three logical pixels wide is invisible and
            // costs nothing next to a second layered surface.
            using var path = new GraphicsPath();
            float d = Math.Min(bounds.Width, bounds.Height);
            if (d > 2)
            {
                path.AddArc(0, 0, d, d, 180, 180);
                path.AddArc(0, bounds.Height - d, d, d, 0, 180);
                path.CloseFigure();
                Region?.Dispose();
                Region = new Region(path);
            }
        }
        Opacity = MathX.Clamp(alpha, 0, 1);
        if (!Visible) Show();
    }
}

// MARK: - The detector

/// "Is a full-screen window covering the cat's display, and is something holding the
/// display awake?"
///
/// The second question is what separates a film from a full-screen text editor, and it
/// is the closest a permission-free app can honestly get to "a video is playing":
/// every video player takes a display-sleep assertion so the screen does not dim
/// mid-scene, and nothing that is merely being typed into does.
///
/// Both are needed to ENTER. Only the full-screen window is needed to STAY, so pausing
/// a film does not make the cat walk back out in front of it.
///
/// Polled at 4Hz. Note the one place the two ports genuinely diverge: macOS has to
/// enumerate every window on screen — eighty-odd dictionaries — so it does that on a
/// background queue, while here the foreground window answers the same question in
/// three cheap calls and there is nothing to get off the tick.
[SupportedOSPlatform("windows")]
internal sealed class FullscreenWatch
{
    internal bool Covering { get; private set; }
    internal bool Awake { get; private set; }
    private bool _latched;

    private double _lastPoll = -1;
    private const double Interval = 0.25;

    /// True while the cat should stay out of the way.
    internal bool FullscreenBusy => _latched;

    internal void Poll(Rect displayFrame, double now)
    {
        if (_lastPoll >= 0 && now - _lastPoll < Interval) return;
        _lastPoll = now;
        Covering = WindowCovers(displayFrame);
        Awake = DisplayHeldAwake();
        // Enter on both, stay on one.
        _latched = Covering && (Awake || _latched);
    }

    /// The foreground window, if its bounds are the whole display.
    ///
    /// Compared against `Frame` and not `Work` on purpose: real full screen covers the
    /// taskbar, and a merely maximised window does not. That distinction is the whole
    /// reason a maximised terminal is not mistaken for a film — and it is the exact
    /// counterpart of the macOS build comparing against `frame` rather than
    /// `visibleFrame`.
    private static bool WindowCovers(Rect display)
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        if (!Win32.GetWindowRect(hwnd, out var r)) return false;
        return Math.Abs(r.Left - display.MinX) < 2
            && Math.Abs(r.Top - display.MinY) < 2
            && Math.Abs(r.Right - r.Left - display.W) < 2
            && Math.Abs(r.Bottom - r.Top - display.H) < 2;
    }

    private static bool DisplayHeldAwake()
    {
        if (Win32.CallNtPowerInformation(Win32.SystemExecutionState, IntPtr.Zero, 0,
                                         out uint state, sizeof(uint)) != 0)
        {
            return false;
        }
        return (state & Win32.EsDisplayRequired) != 0;
    }
}
