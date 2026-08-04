import AppKit
import IOKit.pwr_mgt

/// Which side of the display the cat parks against.
enum PeekEdge {
    case left, right
}

/// The dwell-to-arm gesture, as a pure state machine over time and one number.
///
/// Pulled out of the module deliberately. Nobody can drag a cat on a CI runner, so the
/// only way this gets checked on every commit is if the decision is separable from the
/// window, the cursor and the clock — the same reason `SelfInstall.Decide` is its own
/// function. `--demo-peek` drives it with a scripted time base on both platforms, and
/// the two ports are expected to agree exactly.
struct Arming {
    var armMs: Double = 320
    var disarmMs: Double = 80

    private var dwellEdge: PeekEdge?
    private var dwellSince: Double = 0
    private var leftZoneAt: Double?

    /// The edge the snap is armed for, or nil. This is also precisely the condition
    /// under which the indicator is visible, which is what makes "no line means no
    /// snap" a fact rather than a hope.
    private(set) var armed: PeekEdge?

    mutating func step(cursorX: CGFloat, minX: CGFloat, maxX: CGFloat,
                       band: CGFloat, now: Double) {
        let edge: PeekEdge? =
            cursorX <= minX + band ? .left :
            cursorX >= maxX - band ? .right : nil

        guard let edge else {
            // A little hysteresis before disarming. Without it one pixel of hand
            // wobble at the boundary strobes the capsule on and off.
            if armed != nil || dwellEdge != nil {
                if leftZoneAt == nil { leftZoneAt = now }
                if now - (leftZoneAt ?? now) >= disarmMs / 1000 { clear() }
            }
            return
        }

        leftZoneAt = nil
        if dwellEdge != edge {
            dwellEdge = edge
            dwellSince = now
            armed = nil
        }
        // Strictly greater: arming exactly ON the threshold would make the result
        // depend on whether a tick happened to land there, and the two ports do not
        // tick at the same instants.
        if now - dwellSince > armMs / 1000 { armed = edge }
    }

    mutating func clear() {
        dwellEdge = nil
        armed = nil
        leftZoneAt = nil
    }
}

/// Parking the cat against a screen edge, so it peers in from the side instead of
/// sitting on top of what you are looking at.
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
///
/// Nothing here reads a window title. `kCGWindowName` is the one field gated behind
/// Screen Recording and is banned; owner, pid, layer and bounds are not, which is
/// exactly what this needs. Measured from a bundle with both Screen Recording and
/// Accessibility denied: 80 of 80 windows still reported bounds, owner, layer and pid,
/// and 1 of 80 reported a name. See spikes/RESULTS.md.
final class PeekModule: CatModule, AtlasTuned {
    let id = "peek"
    var tunedGeneration = -1

    private weak var panel: NSPanel?
    private let watch = FullscreenWatch()
    private var indicator: SnapIndicator?

    // --- tuning, all of it from cat.json ------------------------------------
    // Lengths are LOGICAL pixels of the 48px canvas and are multiplied by the render
    // scale at the point of use, so the edge band and the parked reveal grow with the
    // cat. A bigger cat is a bigger target and deserves a wider band.
    private var edgeZonePx: CGFloat = 12
    private var armMs: Double = 320
    private var disarmMs: Double = 80
    private var revealPx: CGFloat = 28
    private var slideRate: CGFloat = 11
    private var settlePt: CGFloat = 0.35
    private var pawRisePx: CGFloat = 10
    private var pawGatherPx: CGFloat = 1.5
    private var hideAt: CGFloat = 0.55
    private var headLeanPx: CGFloat = 2
    private var headRisePx: CGFloat = 1
    private var bobPx: CGFloat = 1.5
    private var bobHz: CGFloat = 0.42
    private var indicatorWPx: CGFloat = 3
    private var indicatorFadeMs: Double = 120

    /// The cat's ink inside the 48px canvas, so `reveal_px` means "this much cat" and
    /// not "this much canvas, most of which is transparent".
    private var inkMinX: CGFloat = 0
    private var inkMaxX: CGFloat = 48
    private var inkHeight: CGFloat = 48

    /// Everything the peek pose does NOT draw. Published to the stage while parked.
    ///
    /// The body is not clipped, it is absent — and that distinction is the entire
    /// reason this pose works where slicing the cat vertically never could. A cat cut
    /// in half by the screen edge reads as a rendering bug at every width that was
    /// tried; a head and two paws over the edge reads as a cat immediately, because
    /// it is a whole face rather than part of one.
    private static let behindTheEdge: Set<String> = [
        "body", "body_hot", "tail", "tail_hot", "shadow",
    ]

    func retune(_ atlas: Atlas) {
        func v(_ k: String, _ d: CGFloat) -> CGFloat { atlas.tune("peek", k, d) }
        edgeZonePx = v("edge_zone_px", edgeZonePx)
        armMs = Double(v("arm_ms", CGFloat(armMs)))
        disarmMs = Double(v("disarm_ms", CGFloat(disarmMs)))
        revealPx = v("reveal_px", revealPx)
        slideRate = v("slide_rate", slideRate)
        settlePt = v("settle_pt", settlePt)
        pawRisePx = v("paw_rise_px", pawRisePx)
        pawGatherPx = v("paw_gather_px", pawGatherPx)
        hideAt = v("hide_at", hideAt)
        headLeanPx = v("head_lean_px", headLeanPx)
        headRisePx = v("head_rise_px", headRisePx)
        bobPx = v("bob_px", bobPx)
        bobHz = v("bob_hz", bobHz)
        indicatorWPx = v("indicator_w_px", indicatorWPx)
        indicatorFadeMs = Double(v("indicator_fade_ms", CGFloat(indicatorFadeMs)))

        var minX = CGFloat.greatestFiniteMagnitude
        var maxX = -CGFloat.greatestFiniteMagnitude
        var minY = CGFloat.greatestFiniteMagnitude
        var maxY = -CGFloat.greatestFiniteMagnitude
        for name in CatView.peekParts {
            guard let p = atlas.parts[name] else { continue }
            minX = min(minX, p.origin.x)
            maxX = max(maxX, p.origin.x + p.size.width)
            minY = min(minY, p.origin.y)
            maxY = max(maxY, p.origin.y + p.size.height)
        }
        if minX <= maxX {
            inkMinX = minX
            inkMaxX = maxX
            inkHeight = max(maxY - minY, 1)
        }
    }

    // --- how the cat came to be parked --------------------------------------
    // Worth distinguishing, because it decides what takes it back out again.
    private enum Park {
        case none
        case manual(PeekEdge)   // you put it there; only you take it away
        case auto(PeekEdge)     // a video put it there; the video takes it away

        var isParked: Bool {
            if case .none = self { return false }
            return true
        }
    }
    private var park = Park.none
    private var parkedEdge: PeekEdge? {
        switch park {
        case .none: return nil
        case .manual(let e), .auto(let e): return e
        }
    }

    /// Where the cat stood before a video moved it, so it can be put back. Cleared
    /// the moment the user drags, because then they have chosen a new home and
    /// putting it back would be overruling them.
    private var preParkX: CGFloat?

    /// The slide's authoritative position, in points, kept apart from the window's
    /// own because the window's is quantised. Nil whenever the window is not ours to
    /// move — while it is being dragged, and while there is nowhere to go.
    private var slideX: CGFloat?

    /// Set when the user drags out of an automatic park. Suppresses re-parking until
    /// the full-screen window goes away, or the cat would spring straight back to the
    /// edge and there would be no way to keep it out.
    private var autoOverridden = false

    // --- the arm state machine ----------------------------------------------
    private var wasDragging = false
    private var arming = Arming()
    private var armedEdge: PeekEdge? { arming.armed }

    // --- presentation --------------------------------------------------------
    private var indicatorAlpha: CGFloat = 0
    private var bobPhase: CGFloat = 0
    /// 0 while free, 1 when fully parked. Drives the lean, so the cat does not snap
    /// into its peeking pose before it has arrived at the edge.
    private var settled: CGFloat = 0

    private let demoRequested = CommandLine.arguments.contains("--demo-peek")
    private var demoRan = false

    init(panel: NSPanel) {
        self.panel = panel
    }

    // MARK: - Settings

    /// Both default ON. Someone who finds either annoying must be able to switch it
    /// off without switching the other off with it.
    static var autoPeekEnabled: Bool {
        UserDefaults.standard.object(forKey: "peekFullscreen") as? Bool ?? true
    }
    static var snapOnDragEnabled: Bool {
        UserDefaults.standard.object(forKey: "peekSnapDrag") as? Bool ?? true
    }

    /// "Centre on screen" has to mean it, so the menu item clears any park through
    /// here rather than fighting the easing for the rest of the session.
    func releasePark() {
        park = .none
        preParkX = nil
        arming.clear()
    }

    // MARK: - Tick

    func update(_ ctx: TickContext) -> ModuleOutput {
        guard let atlas = tunedAtlas(), let panel else { return .none }
        let stage = CatStage.shared
        let now = CFAbsoluteTimeGetCurrent()
        let screen = Self.screen(holding: ctx.frame)
        let vf = screen.visibleFrame

        if demoRequested, !demoRan {
            demoRan = true
            runDemo(atlas: atlas, panel: panel, vf: vf, scale: ctx.scale)
        }

        watch.poll(screenFrame: screen.frame)
        let busy = watch.fullscreenBusy
        stage.fullscreenBusy = busy
        stage.metric("peek.fs", watch.covering ? 1 : 0)
        stage.metric("peek.awake", watch.awake ? 1 : 0)

        // The one-frame lag on `stage.state` is exactly what this wants: it lets the
        // module notice a drag without knowing which module owns dragging.
        let dragging = stage.state == .dragging
        let dragEnded = wasDragging && !dragging
        wasDragging = dragging

        // --- being carried overrides everything ------------------------------
        if dragging {
            if case .auto = park { autoOverridden = true }
            if park.isParked {
                park = .none
                preParkX = nil
            }
            stepArming(ctx: ctx, vf: vf, now: now)
        } else if dragEnded {
            if let edge = armedEdge, Self.snapOnDragEnabled {
                park = .manual(edge)
                preParkX = nil          // a manual park has nowhere to go back to
            }
            clearArming()
        } else {
            clearArming()
        }

        // --- the automatic half ----------------------------------------------
        if !busy { autoOverridden = false }
        if !dragging {
            if busy, Self.autoPeekEnabled, !autoOverridden, case .none = park {
                preParkX = panel.frame.origin.x
                park = .auto(Self.nearerEdge(frame: ctx.frame, vf: vf))
            } else if case .auto = park, !busy || !Self.autoPeekEnabled {
                // Either the video ended or the setting was just turned off. Both
                // mean the same thing to the cat: walk back. The target below falls
                // back to `preParkX`.
                park = .none
            }
        }

        // --- move the window --------------------------------------------------
        var target: CGFloat?
        if let edge = parkedEdge {
            target = parkedX(edge: edge, vf: vf, atlas: atlas, scale: ctx.scale)
        } else if let back = preParkX {
            target = back
        }
        if dragging || target == nil {
            slideX = nil                // the hand owns the window, or nobody does
        } else if let target {
            // Accumulated in a float of our own and rounded only on the way out.
            //
            // Reading the position back off the panel each frame instead loses every
            // sub-point step to the window server's quantisation — and because an
            // exponential approach makes the steps smaller the closer it gets, the
            // last fraction of a point is never travelled. Measured before the fix:
            // walking home to x=727 it stopped at 728 and sat there for the rest of
            // the run, one point short and permanently `peeking`-adjacent.
            var x = slideX ?? panel.frame.origin.x
            let d = target - x
            if abs(d) < settlePt {
                x = target
                if !park.isParked { preParkX = nil }
            } else {
                x += d * min(1, slideRate * ctx.dt)
            }
            slideX = x
            // Whole points, so the cat is composited on the device pixel grid at every
            // integer scale rather than resampled across it.
            let put = x.rounded()
            if panel.frame.origin.x != put {
                panel.setFrameOrigin(NSPoint(x: put, y: panel.frame.origin.y))
            }
        }

        // --- how parked does it look ------------------------------------------
        let want: CGFloat = park.isParked ? 1 : 0
        settled += (want - settled) * min(1, slideRate * ctx.dt)
        stage.metric("peek.settled", settled)
        stage.metric("peek.armed", armedEdge == nil ? 0 : 1)
        stage.metric("peek.x", panel.frame.origin.x)

        drawIndicator(ctx: ctx, vf: vf, now: now)

        guard settled > 0.002, let edge = parkedEdge ?? lastEdge else { return .none }
        lastEdge = edge

        // The pose, and the whole thing rests on ONE idea: the body tucks a little
        // further behind the edge while the head cranes the other way, out past it.
        // It is the DIFFERENCE between those two that reads as an animal looking
        // round a corner.
        //
        // The first version moved the whole cat inward instead, which does nothing
        // but put more cat on screen — the opposite of peeking, and it looked like a
        // window had sliced the cat rather than the cat had hidden. Combined with a
        // reveal that left 54% of the ink showing, there was no peek there at all.
        //
        // Offsets rather than new art, so nothing here needs a sprite that does not
        // already exist — and so a theme retunes the pose in the same JSON diff that
        // retunes everything else.
        bobPhase += ctx.dt * bobHz
        while bobPhase >= 1 { bobPhase -= 1 }

        var out = ModuleOutput()
        let toEdge: CGFloat = edge == .right ? 1 : -1
        let bobY = sin(bobPhase * 2 * .pi) * bobPx * settled
        out.offset.y = bobY
        stage.headOffset.x -= toEdge * headLeanPx * settled
        stage.headOffset.y -= headRisePx * settled

        // The pose: two paws up under the chin, and no body at all.
        //
        // The paws are the cat's own — no new sprite was needed, which is the tell
        // that this is the right shape rather than a clever one. They ride up to the
        // jaw and gather slightly toward each other, the way an animal's do when it
        // is holding an edge and looking over it.
        stage.pawOffsetL.y -= pawRisePx * settled
        stage.pawOffsetR.y -= pawRisePx * settled
        stage.pawOffsetL.x += pawGatherPx * settled
        stage.pawOffsetR.x -= pawGatherPx * settled

        // The body ducks away late, while the cat is already mostly off screen, so it
        // reads as getting behind the edge rather than as the body being deleted.
        if settled >= hideAt {
            stage.hiddenParts.formUnion(Self.behindTheEdge)
            stage.peekPose = true
        }
        // The paw hooked over the edge. An overlay rather than a body part, which
        // gets it three things for free: it is not in the draw order or the hit mask,
        // it can be faded in with the park, and — because overlays do not take
        // `bodyOffset` — it stays pinned to the screen edge while the body tucks away
        // behind it. That last one is the whole gag: the cat slides back, the paw
        // does not let go.
        if park.isParked { out.state = .peeking }
        return out
    }

    /// Remembered so the lean eases OUT rather than vanishing on the frame the park
    /// is released.
    private var lastEdge: PeekEdge?

    // MARK: - Arming

    private func stepArming(ctx: TickContext, vf: NSRect, now: CFAbsoluteTime) {
        guard Self.snapOnDragEnabled else { return clearArming() }
        // The cursor, recovered from the frame and the cat-relative reading the tick
        // already computed. No second NSEvent call, and no new plumbing.
        arming.armMs = armMs
        arming.disarmMs = disarmMs
        arming.step(cursorX: ctx.frame.midX + ctx.cursor.x * ctx.scale,
                    minX: vf.minX, maxX: vf.maxX,
                    band: edgeZonePx * ctx.scale, now: now)
    }

    private func clearArming() { arming.clear() }

    // MARK: - Geometry

    /// The display the cat is standing on. Its centre rather than its frame, so a cat
    /// straddling two monitors picks the one most of it is on.
    private static func screen(holding frame: NSRect) -> NSScreen {
        let c = CGPoint(x: frame.midX, y: frame.midY)
        return NSScreen.screens.first { $0.frame.contains(c) }
            ?? NSScreen.main
            ?? NSScreen.screens[0]
    }

    /// Auto-peek goes to whichever edge the cat is already nearest, right on a tie —
    /// so a cat that lives on the left does not get flung across the display the
    /// moment a video starts.
    private static func nearerEdge(frame: NSRect, vf: NSRect) -> PeekEdge {
        (frame.midX - vf.minX) < (vf.maxX - frame.midX) ? .left : .right
    }

    /// Window origin that leaves exactly `reveal_px` of the cat's INK on screen.
    ///
    /// Measured off the ink and not the panel, because the panel carries a
    /// transparent margin for the speech bubble — parking by the panel edge would
    /// leave the margin on screen and the cat entirely off it.
    private func parkedX(edge: PeekEdge, vf: NSRect, atlas: Atlas, scale: CGFloat) -> CGFloat {
        Self.parkedX(edge: edge, minX: vf.minX, maxX: vf.maxX,
                     padX: CGFloat(atlas.layout.padX),
                     inkMinX: inkMinX, inkMaxX: inkMaxX,
                     revealPx: revealPx, scale: scale)
    }

    /// Pure, and for the same reason `Arming` is: it is the other half of what
    /// `--demo-peek` has to be able to assert without a screen.
    static func parkedX(edge: PeekEdge, minX: CGFloat, maxX: CGFloat, padX: CGFloat,
                        inkMinX: CGFloat, inkMaxX: CGFloat,
                        revealPx: CGFloat, scale: CGFloat) -> CGFloat {
        switch edge {
        case .right: return maxX - (revealPx + padX + inkMinX) * scale
        case .left:  return minX + (revealPx - padX - inkMaxX) * scale
        }
    }

    // MARK: - The indicator

    private func drawIndicator(ctx: TickContext, vf: NSRect, now: CFAbsoluteTime) {
        let want: CGFloat = armedEdge == nil ? 0 : 1
        let step = ctx.dt / max(indicatorFadeMs / 1000, 0.001)
        indicatorAlpha += max(-step, min(step, want - indicatorAlpha))

        guard indicatorAlpha > 0.001 else {
            indicator?.hide()
            return
        }
        guard let edge = armedEdge ?? lastArmed else { return }
        lastArmed = edge

        let w = (indicatorWPx * ctx.scale).rounded()
        let h = (inkHeight * ctx.scale).rounded()
        let x = edge == .right ? vf.maxX - w : vf.minX
        let rect = NSRect(x: x, y: (ctx.frame.midY - h / 2).rounded(), width: w, height: h)

        let ind = indicator ?? {
            let i = SnapIndicator()
            indicator = i
            return i
        }()
        ind.show(frame: rect, alpha: indicatorAlpha, radius: w / 2)
    }

    private var lastArmed: PeekEdge?
}

// MARK: - Scripted verification

extension PeekModule {
    /// `--demo-peek`. Asserts the gesture and the parked geometry without a hand.
    ///
    /// Everything except the last check runs on a synthetic clock against the pure
    /// `Arming` and `parkedX`, so the identical script runs on a Windows CI runner
    /// where there is nobody to drag a cat. The last check needs a real window and is
    /// the one thing no unit test can answer: whether AppKit will actually let a
    /// borderless panel sit half off the side of the display, or quietly clamps it
    /// back on — which would make the whole feature a no-op that still reported
    /// success.
    fileprivate func runDemo(atlas: Atlas, panel: NSPanel, vf: NSRect, scale: CGFloat) -> Never {
        var failures = 0
        func check(_ name: String, _ ok: Bool, _ detail: String = "") {
            if !ok { failures += 1 }
            print("  \(ok ? "ok  " : "FAIL") \(name)\(detail.isEmpty ? "" : "  — \(detail)")")
        }

        let band = edgeZonePx * scale
        let dwell = armMs / 1000
        let grace = disarmMs / 1000
        var t = 0.0
        var a = Arming()
        a.armMs = armMs
        a.disarmMs = disarmMs
        func hold(_ x: CGFloat, _ seconds: Double) {
            let end = t + seconds
            while t < end {
                a.step(cursorX: x, minX: vf.minX, maxX: vf.maxX, band: band, now: t)
                t += 1.0 / 120
            }
        }
        let inBandR = vf.maxX - 2, inBandL = vf.minX + 2, middle = vf.midX

        print("# demo: peek — screen \(Int(vf.width))x\(Int(vf.height)) at "
              + "(\(Int(vf.minX)),\(Int(vf.minY))), scale \(Int(scale))x, "
              + "band \(Int(band))pt, arm \(Int(armMs))ms")

        // 1. Brushing the edge on the way past must NOT arm. This is the gesture the
        //    user asked for by name: come in a certain way and it will not snap.
        hold(middle, 0.20)
        hold(inBandR, dwell * 0.5)
        hold(middle, 0.20)
        check("a flick through the band never arms", a.armed == nil)

        // 2. Resting there does.
        hold(inBandR, dwell + 0.05)
        check("dwelling at the right edge arms right", a.armed == .right)

        // 3. A wobble out of the band is forgiven; leaving properly is not.
        hold(middle, grace * 0.4)
        check("a brief wobble keeps it armed", a.armed == .right)
        hold(middle, grace + 0.05)
        check("leaving the band disarms", a.armed == nil)

        // 4. The other edge works and the dwell restarts when you switch.
        hold(inBandL, dwell + 0.05)
        check("dwelling at the left edge arms left", a.armed == .left)
        hold(inBandR, dwell * 0.5)
        check("switching edges restarts the dwell", a.armed == nil)
        hold(inBandR, dwell)
        check("and arms once the new dwell completes", a.armed == .right)

        // 5. The parked position leaves exactly `reveal_px` of INK on screen — not
        //    of canvas, most of which is the transparent bubble margin.
        let padX = CGFloat(atlas.layout.padX)
        let want = revealPx * scale
        let pr = Self.parkedX(edge: .right, minX: vf.minX, maxX: vf.maxX, padX: padX,
                              inkMinX: inkMinX, inkMaxX: inkMaxX,
                              revealPx: revealPx, scale: scale)
        let pl = Self.parkedX(edge: .left, minX: vf.minX, maxX: vf.maxX, padX: padX,
                              inkMinX: inkMinX, inkMaxX: inkMaxX,
                              revealPx: revealPx, scale: scale)
        let shownR = vf.maxX - (pr + (padX + inkMinX) * scale)
        let shownL = (pl + (padX + inkMaxX) * scale) - vf.minX
        check("parked right shows \(Int(revealPx))px of cat", abs(shownR - want) < 0.01,
              String(format: "%.2fpt vs %.2fpt", Double(shownR), Double(want)))
        check("parked left shows \(Int(revealPx))px of cat", abs(shownL - want) < 0.01,
              String(format: "%.2fpt vs %.2fpt", Double(shownL), Double(want)))

        // 6. WHICH parts the cut lands between, which is the whole difference between
        //    a peek and a cat with a slice taken off it. One eye showing and one
        //    hidden is the thing being aimed at; at reveal 20 both were on screen and
        //    it read as a window clipping a cat. Retuning past that is a design
        //    change and should have to argue with a failing check first.
        // Both edges, separately. They are NOT mirror images of each other — the cat
        // carries a tail on one side and nothing on the other — and assuming they
        // were is exactly how a left-edge park came to spend its whole reveal on tail
        // and cut the face in half.
        let seenTo = inkMinX + revealPx        // right-edge park: 0..seenTo is on screen
        let seenFrom = inkMaxX - revealPx      // left-edge park: seenFrom.. is on screen
        func box(_ name: String) -> (lo: CGFloat, hi: CGFloat)? {
            guard let p = atlas.parts[name] else { return nil }
            return (p.origin.x, p.origin.x + p.size.width)
        }
        func showsR(_ n: String) -> Bool { box(n).map { $0.hi <= seenTo + 1 } ?? false }
        func hidesR(_ n: String) -> Bool { box(n).map { $0.lo >= seenTo } ?? true }
        func showsL(_ n: String) -> Bool { box(n).map { $0.lo >= seenFrom - 1 } ?? false }
        func hidesL(_ n: String) -> Bool { box(n).map { $0.hi <= seenFrom } ?? true }

        // A WHOLE FACE is the point of this pose, so both eyes have to clear the
        // edge — that is the line between a cat hiding behind something and a cat
        // someone has cut in half, and every earlier version failed it.
        check("right park: both eyes clear the edge",
              showsR("eye_l") && showsR("eye_r"))
        check("left park: both eyes clear the edge",
              showsL("eye_l") && showsL("eye_r"))
        // Stated directly rather than as "not fully shown": the shows/hides pair
        // carry a one-pixel tolerance for the eyes, and reusing them here made a
        // genuine one-pixel tuck read as a failure. Partly behind the edge is the
        // claim, so partly behind the edge is what gets asserted.
        let tuckedR = (box("ear_r")?.hi ?? 0) > seenTo
        let tuckedL = (box("ear_l")?.lo ?? 0) < seenFrom
        check("right park: the far ear tucks behind the edge", tuckedR,
              "so the head reads as coming from behind it, not floating in front")
        check("left park: the far ear tucks behind the edge", tuckedL)
        check("the two edges show the same amount of cat",
              abs((seenTo - inkMinX) - (inkMaxX - seenFrom)) < 0.001,
              "measured on the head, which is the only thing this pose draws wide")
        check("the body, tail and shadow are the parts left behind",
              !CatView.peekParts.contains("body") && !CatView.peekParts.contains("tail")
              && !CatView.peekParts.contains("shadow"))
        check("both paws are part of the pose",
              CatView.peekParts.contains("paw_l") && CatView.peekParts.contains("paw_r"))

        // 7. Live, and the only check here that needs a screen.
        let y = panel.frame.origin.y
        panel.setFrameOrigin(NSPoint(x: pr, y: y))
        let got = panel.frame.origin
        check("the window may hang off the right edge", abs(got.x - pr) < 0.5,
              String(format: "asked %.1f, got %.1f", Double(pr), Double(got.x)))
        check("parking does not move it vertically", abs(got.y - y) < 0.5)

        print(String(format: "# demo: peek band=%.0fpt arm=%.0fms disarm=%.0fms "
                           + "reveal=%.0fpt parkedR=%.1f parkedL=%.1f",
                     Double(band), armMs, disarmMs, Double(want), Double(pr), Double(pl)))
        print(failures == 0
              ? "# demo: PASS — the gesture arms only on a dwell, and the cat parks where claimed"
              : "# demo: FAIL — \(failures) check(s) failed")
        fflush(stdout)
        exit(failures == 0 ? 0 : 1)
    }
}

// MARK: - The edge capsule

/// The line that says a snap is armed.
///
/// A separate window rather than something drawn into the cat's, because it has to be
/// at the screen edge and the cat is not — and because it must never take a click,
/// which a panel that ignores mouse events unconditionally guarantees without going
/// anywhere near the 120Hz click-through poll.
///
/// Deliberately system chrome and not cat art: a rounded capsule, antialiased, the
/// same shape macOS and Windows use to say "this is where it lands". The pixel-grid
/// rules that govern every sprite do not apply to it, and pretending otherwise would
/// make it look like a bug rather than like the OS.
private final class SnapIndicator {
    private let panel: NSPanel
    private let capsule = CALayer()

    init() {
        panel = NSPanel(contentRect: .zero,
                        styleMask: [.borderless, .nonactivatingPanel],
                        backing: .buffered, defer: false)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.ignoresMouseEvents = true
        panel.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.popUpMenuWindow)))
        panel.collectionBehavior = [
            .canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle,
        ]
        let host = NSView()
        host.wantsLayer = true
        host.layer?.addSublayer(capsule)
        panel.contentView = host

        capsule.backgroundColor = NSColor.white.cgColor
        // A white line on a white background is not an affordance. The hairline
        // border is what makes it visible on a bright video as well as a dark one.
        capsule.borderColor = NSColor.black.withAlphaComponent(0.28).cgColor
        capsule.borderWidth = 1
        capsule.actions = ["position": NSNull(), "bounds": NSNull(),
                           "opacity": NSNull(), "cornerRadius": NSNull()]
    }

    func show(frame: NSRect, alpha: CGFloat, radius: CGFloat) {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        panel.setFrame(frame, display: false)
        capsule.frame = CGRect(origin: .zero, size: frame.size)
        capsule.cornerRadius = radius
        CATransaction.commit()
        panel.alphaValue = alpha
        if !panel.isVisible { panel.orderFrontRegardless() }
    }

    func hide() {
        if panel.isVisible { panel.orderOut(nil) }
    }
}

// MARK: - The detector

/// "Is a full-screen window covering the cat's display, and is something holding the
/// display awake?"
///
/// The second question is what separates a film from a full-screen text editor, and it
/// is the closest a permission-free app can honestly get to "a video is playing":
/// every video player takes a display-sleep assertion so the screen does not dim
/// mid-scene, and nothing that is merely being typed into does. `pmset -g assertions`
/// reads exactly this table and needs no privilege.
///
/// Both are needed to ENTER. Only the full-screen window is needed to STAY, so pausing
/// a film does not make the cat walk back out in front of it.
///
/// Polled at 4Hz on a background queue: the window list is 80-odd dictionaries on this
/// machine and has no business running on a 120Hz tick.
private final class FullscreenWatch {
    private(set) var covering = false
    private(set) var awake = false
    private var latched = false

    private var lastPoll: CFAbsoluteTime = 0
    private var inFlight = false
    private let queue = DispatchQueue(label: "dev.loafcat.fullscreen", qos: .utility)
    private let interval: CFTimeInterval = 0.25

    /// True while the cat should stay out of the way.
    var fullscreenBusy: Bool { latched }

    func poll(screenFrame: NSRect) {
        let now = CFAbsoluteTimeGetCurrent()
        guard !inFlight, now - lastPoll >= interval else { return }
        lastPoll = now
        inFlight = true
        queue.async { [weak self] in
            let c = Self.windowCovers(screenFrame)
            let a = Self.displayHeldAwake()
            DispatchQueue.main.async {
                guard let self else { return }
                self.covering = c
                self.awake = a
                // Enter on both, stay on one.
                self.latched = c && (a || self.latched)
                self.inFlight = false
            }
        }
    }

    /// A window from another process, at layer 0, whose bounds are the whole display.
    ///
    /// Compared against `frame` and not `visibleFrame` on purpose: real full screen
    /// covers the menu bar, and a merely maximised window does not. That distinction
    /// is the whole reason a maximised terminal is not mistaken for a film.
    private static func windowCovers(_ screenFrame: NSRect) -> Bool {
        let opts: CGWindowListOption = [.optionOnScreenOnly, .excludeDesktopElements]
        guard let list = CGWindowListCopyWindowInfo(opts, kCGNullWindowID)
                as? [[String: Any]] else { return false }
        // CoreGraphics measures y DOWN from the top-left of the primary display;
        // AppKit measures it up from the bottom-left of the same point.
        let primaryH = NSScreen.screens
            .first { $0.frame.origin == .zero }?.frame.height
            ?? NSScreen.main?.frame.height ?? 0
        let me = ProcessInfo.processInfo.processIdentifier

        for w in list {
            guard let layer = w[kCGWindowLayer as String] as? Int, layer == 0,
                  let pid = w[kCGWindowOwnerPID as String] as? pid_t, pid != me,
                  let b = w[kCGWindowBounds as String] as? [String: CGFloat]
            else { continue }
            let x = b["X"] ?? 0, y = b["Y"] ?? 0
            let cw = b["Width"] ?? 0, ch = b["Height"] ?? 0
            let flipped = NSRect(x: x, y: primaryH - (y + ch), width: cw, height: ch)
            if abs(flipped.minX - screenFrame.minX) < 2,
               abs(flipped.minY - screenFrame.minY) < 2,
               abs(flipped.width - screenFrame.width) < 2,
               abs(flipped.height - screenFrame.height) < 2 {
                return true
            }
        }
        return false
    }

    private static func displayHeldAwake() -> Bool {
        var status: Unmanaged<CFDictionary>?
        guard IOPMCopyAssertionsStatus(&status) == kIOReturnSuccess,
              let d = status?.takeRetainedValue() as? [String: Int] else { return false }
        // Both spellings: the modern assertion and the one older players still take.
        return (d["PreventUserIdleDisplaySleep"] ?? 0) > 0
            || (d["NoDisplaySleepAssertion"] ?? 0) > 0
    }
}
