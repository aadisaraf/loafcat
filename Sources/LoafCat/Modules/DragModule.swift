import AppKit

/// How dramatic the drag deformation is. Purely a taste setting.
enum DragFeel: String, CaseIterable {
    case subtle, normal, springy

    var label: String {
        switch self {
        case .subtle: return "Subtle"
        case .normal: return "Normal"
        case .springy: return "Springy"
        }
    }

    /// Multipliers on the atlas baseline rather than replacements, so a theme that
    /// retunes the feel keeps these three meaningful instead of silently drifting.
    ///
    /// Much narrower than they used to be (1.35 and 1.75), because what they scale
    /// changed underneath them: the atlas baseline is now the full reference hang,
    /// 2.4x the cat's own height, rather than the timid 0.6 it used to be. The old
    /// multipliers on the new baseline would have hung the springy cat 117px below
    /// its own paws — past the transparent margin, and therefore sliced off by the
    /// window edge. `generate_art.py` asserts the top of this range fits.
    var hangScale: CGFloat {
        switch self {
        case .subtle: return 1.0      // the atlas baseline, untouched
        case .normal: return 1.12
        case .springy: return 1.25
        }
    }
    var maxScale: CGFloat {
        switch self {
        case .subtle: return 1.0
        case .normal: return 1.12
        case .springy: return 1.25
        }
    }

    /// `subtle` is the default, which is the same thing as saying the atlas is: it is the
    /// only one of the three that multiplies by 1.0, so an untouched install gets the
    /// tuning the theme actually shipped. The louder two are there to be chosen.
    static var current: DragFeel {
        DragFeel(rawValue: UserDefaults.standard.string(forKey: "dragFeel") ?? "subtle")
            ?? .subtle
    }
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
///
/// Multipliers on the atlas baseline rather than replacements, exactly like `DragFeel`
/// and for the same reason: a theme that retunes the drag keeps all four meaningful
/// instead of silently drifting. `normal` is 1.0 by definition — the shipped tuning IS
/// the normal preset.
enum StretchTempo: String, CaseIterable {
    case snappy, quick, normal, languid

    var label: String {
        switch self {
        case .snappy: return "Snappy"
        case .quick: return "Quick"
        case .normal: return "Normal"
        case .languid: return "Languid"
        }
    }

    /// What the preset does, in the units a person can check against the cat.
    var detail: String {
        switch self {
        case .snappy: return "back almost at once"
        case .quick: return "about twice as fast as normal"
        case .normal: return "the shipped tuning"
        case .languid: return "hangs on to the stretch"
        }
    }

    /// The onset — which is now the lift itself, all 170ms of it, rather than a detail
    /// of how a yank registers. Kept narrow deliberately: much faster and the cat
    /// arrives at full length before the eye has followed the cursor, much slower and
    /// the lift stops reading as a consequence of the grab.
    var riseScale: CGFloat {
        switch self {
        case .snappy: return 1.25
        case .quick: return 1.10
        case .normal: return 1.0
        case .languid: return 0.85
        }
    }

    /// Giving length back WHILE STILL HELD, which now happens only when the shake
    /// headroom relaxes — the hang itself never comes back down until you let go. The
    /// recovery a person actually watches is `releaseDampingScale` below.
    var fallScale: CGFloat {
        switch self {
        case .snappy: return 3.0      // fall_px_s 80 -> 240
        case .quick: return 1.8       // -> 144
        case .normal: return 1.0      // -> 80
        case .languid: return 0.6     // -> 48
        }
    }

    /// How long a held stretch stays taut before it begins to ease at all.
    var holdScale: CGFloat {
        switch self {
        case .snappy: return 0.25     // stretch_hold_ms 900 -> 225
        case .quick: return 0.55      // -> 495
        case .normal: return 1.0      // -> 900
        case .languid: return 1.6     // -> 1440
        }
    }

    /// The bounce after you let go, which measurement says is most of what "slow to
    /// unstretch" actually means: the cat snaps home in about 110ms and then wobbles,
    /// +0.28 at 300ms and +0.03 at 620ms, taking two seconds to be properly still.
    /// `fall_px_s` has nothing to do with that -- this spring does, and now that the
    /// hang holds flat until release, this spring is the ONLY thing that does.
    ///
    /// Note the direction. `Spring.damping` is the fraction of velocity KEPT each
    /// frame -- `velocity *= pow(damping, h * 60)` -- so a LOWER number is a more
    /// damped spring that stops sooner. Reading it as "how damped is it" gets the
    /// presets exactly backwards, which is a mistake worth only making once.
    ///
    /// Clamped in `Tuning`: at 1 the spring never comes to rest at all, and this is a
    /// user-facing multiplier on a value a theme is free to have already moved.
    var releaseDampingScale: CGFloat {
        switch self {
        case .snappy: return 0.78     // release_damping 0.78 -> 0.61, barely bounces
        case .quick: return 0.89      // -> 0.69
        case .normal: return 1.0      // -> 0.78
        case .languid: return 1.10    // -> 0.86, rings on
        }
    }

    static var current: StretchTempo {
        StretchTempo(
            rawValue: UserDefaults.standard.string(forKey: "stretchTempo") ?? "normal")
            ?? .normal
    }
}

/// Picking the cat up.
///
/// Three coupled behaviours that share a grab but simulate independently:
///
/// 1. **A deadzone.** A press registers only a *pending* drag. Without the 4px
///    threshold every click-to-pet becomes an accidental lift, which is the single
///    most irritating bug a desktop pet can have.
/// 2. **A hang that is a LENGTH, not a rubber band.** Distance-driven stretch reads
///    as a band anchored to the cursor. This one is a constant: lift the cat and it
///    lengthens to `hang_px` over `hang_px / rise_px_s`, and then it stays there for
///    as long as you hold it, however far or fast you carry it. Measured off the
///    reference behaviour frame by frame — a flat 2.4x, no sag, no gathering back
///    in — and it is what makes a carried cat read as dangling rather than as a
///    value easing towards something.
/// 3. **A pendulum.** Impulse comes from drag *acceleration* through a power law,
///    so a flick swings hard and a slow pan barely disturbs it.
///
/// The three are deliberately not one simulation. The hang must saturate while the
/// swing stays lively, and coupling them would make a shake shorten the cat.
///
/// Note on the mouse-down: this module consumes the press so it can watch for the
/// deadzone. A press that never clears it does nothing, leaving a future petting
/// module free to work from the 120Hz cursor poll, which is how this project reads
/// the pointer for everything else anyway (see spikes/RESULTS.md, S1).
final class DragModule: CatModule {
    let id = "drag"

    private weak var panel: NSPanel?
    private unowned let registry: ModuleRegistry

    private enum Phase { case idle, pending, dragging, settling }
    private var phase: Phase = .idle

    /// Everything tunable, from cat.json. Defaults are the shipped mono values, so
    /// a theme that omits the block still behaves rather than collapsing to zero.
    private struct Tuning {
        var deadzonePx: CGFloat = 4
        var stretchHoldMs: CGFloat = 900
        // Lengths, in canvas pixels of added height. See the atlas on why these are
        // pixels and not multiples of the span below the grab.
        var maxPx: CGFloat = 76
        var hangPx: CGFloat = 67
        var yankSpeedRef: CGFloat = 900
        var yankAttack: CGFloat = 14
        var yankRelease: CGFloat = 3.2
        var speedSmoothing: CGFloat = 8.0
        var risePxS: CGFloat = 400
        var fallPxS: CGFloat = 80
        var releaseStiffness: CGFloat = 468
        var releaseDamping: CGFloat = 0.78
        var releaseVelocityGain: CGFloat = 0.35
        var releaseSettleEps: CGFloat = 0.0001
        var landingSquashPerPx: CGFloat = 0.0130
        var grabMinY: CGFloat = 26
        var grabMaxY: CGFloat = 34
        var headLagPx: CGFloat = 1.5
        var headSwingShare: CGFloat = 0.08
        var shadowShrink: CGFloat = 0.65
        var swingArmFrac: CGFloat = 0.5
        var swingMaxDeg: CGFloat = 45
        var swingImpulse: CGFloat = 0.0012
        var swingAccelCap: CGFloat = 20
        var swingVelSmoothing: CGFloat = 0.35
        var swingSpringDrag: CGFloat = 0.018
        var swingSpringFree: CGFloat = 0.003
        var swingDampingDrag: CGFloat = 0.86
        var swingDampingFree: CGFloat = 0.962
        var swingSettleEpsRad: CGFloat = 0.007
        var swingSettleEpsVel: CGFloat = 0.003
        var padPx: CGFloat = 12

        init() {}
        init(_ a: Atlas) {
            func v(_ k: String, _ d: CGFloat) -> CGFloat { a.tune("drag", k, d) }
            deadzonePx = v("deadzone_px", deadzonePx)
            stretchHoldMs = v("stretch_hold_ms", stretchHoldMs)
            maxPx = v("max_px", maxPx)
            hangPx = v("hang_px", hangPx)
            let feel = DragFeel.current
            hangPx *= feel.hangScale
            maxPx *= feel.maxScale
            // Nothing may hang past the transparent margin, because the window ends
            // there and a paw across that line is simply gone. The generator asserts
            // it for the themes shipped here; this is the same guarantee for a
            // community theme, which nothing in this build gets to check first.
            let room = CGFloat(a.layout.padY) + a.canvas - DragModule.inkBottom(a)
            maxPx = min(maxPx, max(room, 1))
            hangPx = min(hangPx, maxPx)
            yankSpeedRef = v("yank_speed_ref", yankSpeedRef)
            yankAttack = v("yank_attack", yankAttack)
            yankRelease = v("yank_release", yankRelease)
            speedSmoothing = v("speed_smoothing", speedSmoothing)
            risePxS = v("rise_px_s", risePxS)
            fallPxS = v("fall_px_s", fallPxS)
            let tempo = StretchTempo.current
            risePxS *= tempo.riseScale
            fallPxS *= tempo.fallScale
            stretchHoldMs *= tempo.holdScale
            releaseStiffness = v("release_stiffness", releaseStiffness)
            releaseDamping = v("release_damping", releaseDamping)
            // After the atlas read, and clamped: damping is a multiplier on a value
            // that must stay under 1, or the release spring never comes to rest.
            releaseDamping = max(0.35, min(releaseDamping * tempo.releaseDampingScale, 0.97))
            releaseVelocityGain = v("release_velocity_gain", releaseVelocityGain)
            releaseSettleEps = v("release_settle_eps", releaseSettleEps)
            landingSquashPerPx = v("landing_squash_per_px", landingSquashPerPx)
            grabMinY = v("grab_min_y", grabMinY)
            grabMaxY = v("grab_max_y", grabMaxY)
            headLagPx = v("head_lag_px", headLagPx)
            headSwingShare = v("head_swing_share", headSwingShare)
            shadowShrink = v("shadow_shrink", shadowShrink)
            swingArmFrac = v("swing_arm_frac", swingArmFrac)
            swingMaxDeg = v("swing_max_deg", swingMaxDeg)
            swingImpulse = v("swing_impulse", swingImpulse)
            swingAccelCap = v("swing_accel_cap", swingAccelCap)
            swingVelSmoothing = v("swing_vel_smoothing", swingVelSmoothing)
            swingSpringDrag = v("swing_spring_drag", swingSpringDrag)
            swingSpringFree = v("swing_spring_free", swingSpringFree)
            swingDampingDrag = v("swing_damping_drag", swingDampingDrag)
            swingDampingFree = v("swing_damping_free", swingDampingFree)
            swingSettleEpsRad = v("swing_settle_eps_rad", swingSettleEpsRad)
            swingSettleEpsVel = v("swing_settle_eps_vel", swingSettleEpsVel)
            padPx = v("pad_px", padPx)
        }
    }
    private var t = Tuning()

    // --- gesture ------------------------------------------------------------
    private var pendingTravel: CGFloat = 0
    private var pendingDelta = CGPoint.zero
    private var grabY: CGFloat = 30
    private var heldSeconds: CGFloat = 0

    private var yank: CGFloat = 0
    private var dragSpeed: CGFloat = 0

    // --- hang ---------------------------------------------------------------
    /// How much longer the cat is than it is standing up, in canvas pixels. The
    /// whole simulation runs in this unit and converts once, at the very end, into
    /// the fraction the rig wants — which is why the length no longer depends on
    /// where along the scruff the cat happened to be grabbed.
    private var stretchPx: CGFloat = 0
    /// Canvas pixels between the grab and the lowest ink, fixed for the gesture.
    /// The divisor for that conversion, and nothing else.
    private var spanPx: CGFloat = 13
    /// Only runs after release. While held, the stretch is a pure function of how
    /// long the cat has hung, so a spring would just fight the hold curve.
    private var release = Spring(stiffness: 468, damping: 0.78)

    // --- swing --------------------------------------------------------------
    private var angle: CGFloat = 0        // radians
    private var angVel: CGFloat = 0       // radians per 60Hz frame
    private var smoothedVel: CGFloat = 0  // logical px per 60Hz frame
    private var prevSmoothedVel: CGFloat = 0

    private var demo: Demo?

    /// True only inside a scripted demo's injected call. When a demo is running,
    /// real pointer input is refused: a stray click from whoever happens to be at
    /// the machine would otherwise start a second drag partway through the capture
    /// and make the trace non-reproducible. Observed doing exactly that.
    fileprivate var demoInjecting = false
    private var inputIsScripted: Bool { demo != nil }

    init(panel: NSPanel, registry: ModuleRegistry) {
        self.panel = panel
        self.registry = registry
        if CommandLine.arguments.contains("--demo-drag") { demo = Demo() }
    }

    // MARK: - Wiring

    /// The view is rebuilt on every theme or size change, so the event hookup is
    /// re-checked rather than made once. Doing it here instead of in main.swift
    /// keeps this feature to a single registration line, per architecture rule 2.
    private var view: CatView? {
        guard let v = panel?.contentView as? CatView else { return nil }
        if v.modules !== registry {
            v.modules = registry
            t = Tuning(v.atlas)
        }
        return v
    }

    /// Bottom of the cat's ink, from the atlas. The hang is measured against it.
    fileprivate static func inkBottom(_ atlas: Atlas) -> CGFloat {
        var bottom: CGFloat = 0
        for (name, p) in atlas.standing where name != "shadow" {
            bottom = max(bottom, p.origin.y + p.size.height)
        }
        return max(bottom, 1)
    }

    // MARK: - Events

    func mouseDown(at point: CGPoint) -> Bool {
        guard !inputIsScripted || demoInjecting else { return false }
        guard phase == .idle || phase == .settling else { return false }
        // A stretch or reminder animation owns the cat outright; interrupting it
        // mid-pose would snap the rig. `.dragging` outranks it for the NEXT grab.
        guard registry.state != .stretching else { return false }
        guard let v = view else { return false }

        t = Tuning(v.atlas)
        phase = .pending
        pendingTravel = 0
        pendingDelta = .zero
        // Anchor the hang at the scruff. Wherever the cat is actually grabbed, a
        // real lift happens at the neck -- and it guarantees there is always body
        // below the anchor to stretch, which a grab on the paws would not.
        grabY = min(max(point.y, t.grabMinY), t.grabMaxY)
        spanPx = max(DragModule.inkBottom(v.atlas) - grabY, 1)
        return true
    }

    func mouseDragged(by delta: CGPoint) {
        guard !inputIsScripted || demoInjecting else { return }
        // Accumulated here and consumed on the tick: mouse events arrive at the
        // event rate, not ours, and integrating them twice would double-count the
        // acceleration the pendulum reads.
        pendingDelta.x += delta.x
        pendingDelta.y += delta.y
        if phase == .pending {
            pendingTravel += hypot(delta.x, delta.y)
            if pendingTravel > t.deadzonePx { beginDrag() }
        }
    }

    func mouseUp(at point: CGPoint) {
        guard !inputIsScripted || demoInjecting else { return }
        switch phase {
        case .dragging: endDrag()
        case .pending: phase = .idle        // a click, not a lift
        default: break
        }
    }

    private func beginDrag() {
        phase = .dragging
        heldSeconds = 0
        yank = 0
        dragSpeed = 0
        stretchPx = 0
        angle = 0
        angVel = 0
        smoothedVel = 0
        prevSmoothedVel = 0
        setPad(t.padPx)
    }

    private func endDrag() {
        phase = .settling
        // Overshoot rather than snap: launch the spring inward at a speed set by
        // how far it was stretched, so it boings past neutral into a compression
        // and back. Gain is per 60Hz frame; Spring integrates per second.
        release.value = stretchPx
        release.velocity = -stretchPx * t.releaseVelocityGain * 60
    }

    // MARK: - Tick

    func update(_ ctx: TickContext) -> ModuleOutput {
        guard let v = view else { return .none }
        demo?.advance(self, ctx)

        // Safety net. A mouseUp that never arrives -- the window server drops one
        // if the panel is reconfigured under a held button -- would otherwise
        // strand the cat stretched, enlarged and unclickable, with no way back.
        // pressedMouseButtons is a state query, not an event tap, so it needs no
        // permission (see CLAUDE.md on why that boundary matters).
        if !inputIsScripted, phase == .pending || phase == .dragging,
           NSEvent.pressedMouseButtons & 1 == 0 {
            if phase == .dragging { endDrag() } else { phase = .idle }
        }

        let dt = ctx.dt
        // Everything below was authored for a 60Hz loop. `f` is this tick measured
        // in those frames, and every per-frame constant is raised to it, which is
        // what makes the motion identical at our 120Hz and unchanged if the tick
        // rate ever moves again.
        let f = max(dt * 60, 0.0001)

        var out = ModuleOutput()

        switch phase {
        case .idle:
            return .none

        case .pending:
            // Deadzone not cleared: the cat is being touched, not carried.
            return .none

        case .dragging:
            heldSeconds += dt
            let moved = consumePointer(ctx)

            // Two channels:
            //
            //   hangPx  a constant. A cat held up by the scruff dangles at its full
            //           length for as long as you hold it -- it does not gather
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
            let rawSpeed = hypot(moved.x, moved.y) / max(dt, 0.0001)
            dragSpeed += (rawSpeed - dragSpeed) * min(1, t.speedSmoothing * dt)
            let speed = dragSpeed

            let headroom = max(t.maxPx - t.hangPx, 0)
            let yankTarget = min(speed / max(t.yankSpeedRef, 1), 1) * headroom
            // Asymmetric: a yank must register on the frame it happens, but
            // relaxing slowly is what makes it read as weight rather than a snap.
            let rate = yankTarget > yank ? t.yankAttack : t.yankRelease
            yank += (yankTarget - yank) * min(1, rate * dt)

            // No `max(..., hang)` floor any more, and none needed: the hang is a
            // constant rather than something still climbing out of zero, so the sum
            // cannot dip beneath it and the cat cannot shrink mid-carry.
            let target = min(t.hangPx + yank, t.maxPx)

            // Rate-limit what is actually drawn. Downstream the extent is snapped
            // to whole logical pixels, so an abrupt change in the target crosses
            // several pixel boundaries in one frame and reads as a jump rather than
            // a settle. This limiter IS the lift: the target is at full length from
            // the first frame of the drag, and `rise_px_s` is what makes getting
            // there take the 170ms it should.
            let limit = (target > stretchPx ? t.risePxS : t.fallPxS) * dt
            stretchPx += max(-limit, min(limit, target - stretchPx))

            // Sideways-ness is expressed as LEAN by the pendulum below, never as a
            // change of shape. An earlier version split the pull into horizontal
            // and vertical components: it widened the cat AND cut the hang, so a
            // sideways drag made it fatter and shorter instead of longer -- and
            // because the split tracked a smoothed direction, the cut varied frame
            // to frame and the length visibly jittered.

            stepSwing(dt: dt, f: f, pointerDX: moved.x, dragging: true)

            // main.swift toggles this from the silhouette every tick, and the
            // cursor leaves the silhouette constantly while carrying the cat.
            // Losing it mid-gesture would strand the drag, so reassert it here --
            // modules run after the click-through poll.
            panel?.ignoresMouseEvents = false
            out.state = .dragging

        case .settling:
            release.stiffness = t.releaseStiffness
            release.damping = t.releaseDamping
            release.step(to: 0, dt: dt)
            if abs(release.value) < t.releaseSettleEps,
               abs(release.velocity) < t.releaseSettleEps * 60 {
                release.snap(to: 0)
            }
            stretchPx = release.value
            // The horizontal channel rides the same spring so the cat cannot land
            // still elongated sideways.
            yank = 0
            _ = consumePointer(nil)
            stepSwing(dt: dt, f: f, pointerDX: 0, dragging: false)

            if release.value == 0, release.velocity == 0, angle == 0, angVel == 0 {
                phase = .idle
                stretchPx = 0
                v.rig.clearDrag()
                setPad(0)
                return .none
            }
        }

        // The hang only ever elongates. The spring's negative excursion is the
        // landing squash instead, which is exactly what setSquash is for -- and
        // being uniform is right for an impact, where the whole cat compresses.
        // The pendulum's arm is how long the cat currently is below the grab, so a
        // stretched cat whips further than a compact one from the same angle. A
        // constant here is why a 2.2x cat used to shake by seven degrees.
        let leanPx = sin(angle) * (spanPx + max(0, stretchPx)) * t.swingArmFrac

        // The one place pixels become the fraction the rig works in. Dividing here
        // rather than storing a fraction is what makes a scruff-grab and a
        // rump-grab produce the same length of cat.
        v.rig.setDrag(
            stretch: max(0, stretchPx) / spanPx,
            grabY: grabY,
            leanPx: leanPx,
            headLagPx: t.headLagPx,
            headSwingShare: t.headSwingShare,
            shadowShrink: t.shadowShrink)
        out.squash = 1 + min(0, stretchPx) * t.landingSquashPerPx
        return out
    }

    /// Applies this tick's pointer movement to the window and returns it.
    @discardableResult
    private func consumePointer(_ ctx: TickContext?) -> CGPoint {
        let d = pendingDelta
        pendingDelta = .zero
        guard let ctx, d != .zero, let panel else { return d }
        var o = panel.frame.origin
        o.x += d.x * ctx.scale
        o.y -= d.y * ctx.scale     // atlas is y-down, the screen is y-up
        panel.setFrameOrigin(o)
        return d
    }

    // MARK: - Pendulum

    private func stepSwing(dt: CGFloat, f: CGFloat, pointerDX: CGFloat, dragging: Bool) {
        // Velocity in logical px per 60Hz frame, smoothed. Raw per-tick deltas at
        // 120Hz are far too noisy to raise to a power of 2.2 -- one jittery frame
        // would read as a flick.
        let alpha = 1 - pow(1 - t.swingVelSmoothing, f)
        let instant = (pointerDX / max(dt, 0.0001)) / 60
        smoothedVel += (instant - smoothedVel) * alpha

        // Acceleration, normalised to px per 60Hz frame squared. Dividing the
        // frame-to-frame difference by `f` is what makes this rate independent: a
        // raw difference is half as large at 120Hz, and the power law would then
        // turn that into a fifth of the swing.
        var accel = (smoothedVel - prevSmoothedVel) / f
        prevSmoothedVel = smoothedVel
        accel = min(max(accel, -t.swingAccelCap), t.swingAccelCap)

        // Power law: a fast flick swings much harder than a slow pan, rather than
        // proportionally harder. Measured range is 0.2deg for a slow drag against
        // the full 45deg clamp for a hard flick.
        if abs(accel) > 1e-9 {
            let kick = pow(abs(accel), 2.2) * t.swingImpulse * f
            angVel -= accel < 0 ? -kick : kick
        }

        // Stiffer and much more damped while held, so a reversal bleeds the old
        // angle off fast instead of fighting the new direction.
        let k = dragging ? t.swingSpringDrag : t.swingSpringFree
        let damp = dragging ? t.swingDampingDrag : t.swingDampingFree
        angVel -= angle * k * f
        angVel *= pow(damp, f)
        angle += angVel * f

        let maxRad = t.swingMaxDeg * .pi / 180
        angle = min(max(angle, -maxRad), maxRad)

        // Terminate rather than ring forever. The thresholds are set in rendered
        // pixels: at settle the remaining angle is worth 0.03px of shear, so the
        // snap to exact zero is invisible, and the state stops changing.
        if !dragging, abs(angle) < t.swingSettleEpsRad, abs(angVel) < t.swingSettleEpsVel {
            angle = 0
            angVel = 0
        }
    }

    private func easeInOutCubic(_ x: CGFloat) -> CGFloat {
        x < 0.5 ? 4 * x * x * x : 1 - pow(-2 * x + 2, 3) / 2
    }

    // MARK: - Panel padding

    /// Grows the window so the hang and the swing have somewhere to go.
    ///
    /// The cat's ink fills the canvas edge to edge, so without this the paws and
    /// the tail are sliced off by the window the moment it stretches or swings.
    /// Only applied for the length of a gesture, so the resting window is
    /// untouched and click-through is unaffected.
    /// No longer resizes anything.
    ///
    /// This used to grow the panel for the length of a gesture. The atlas now
    /// carries a permanent transparent margin (layout.pad_x / pad_y) that the
    /// speech bubble needs anyway, and it is far larger than a drag requires — so
    /// the room is simply always there. Resizing a window mid-gesture was also the
    /// riskier design: every resize invalidates the click-through hit test for a
    /// frame. Kept as a named no-op so the call sites still read intentionally.
    private func setPad(_ pad: CGFloat) {}
}

// MARK: - Scripted drag, for verification

/// Drives a synthetic grab-hold-shake-release so the physics can be checked
/// without a human hand. Enabled with `--demo-drag`.
///
/// It calls the same entry points the real events do, so what it exercises is the
/// shipping path and not a parallel copy of it.
private final class Demo {
    private var t: CGFloat = 0
    private var shakeX: CGFloat = 0
    private var released = false
    private var settledAt: CGFloat?
    private var frames = 0
    private var residualBreaches = 0

    // The extremes the gesture reached. Printed as one line at the end so this build
    // and the Windows one can be compared by a number rather than by scrolling 856
    // frames of trace side by side -- which is the only practical way to tell whether
    // a port of a spring system actually behaves like its original.
    private var maxStretch: CGFloat = 0
    private var minStretch: CGFloat = 0
    private var maxAngle: CGFloat = 0
    private var maxDrop: CGFloat = 0
    private var minSquash: CGFloat = 1
    private var maxLean: CGFloat = 0

    // How long the stretch takes to let go, which is the ONLY thing the stretch tempo
    // presets change. The peaks above are amplitudes and are identical across all four
    // presets by design, so without this the demo cannot tell the presets apart -- and
    // a comparison that cannot fail is not one.
    private var releasedAt: CGFloat = 0
    private var lastLoud: CGFloat = 0

    // The lift: how long the cat gets on being picked up, and how long that takes.
    // Neither is visible in the peaks above, because the shake saturates the length at
    // `max_px` whatever the pickup does -- so without these two the demo would print
    // an identical PASS for a build that had lost the lift entirely.
    //
    // `liftPx` is the plateau at the end of the hold; `liftMs` is how long it took to
    // stop climbing. A plateau is exactly what makes the second measurable: the lift
    // is done at the last frame that still moved.
    private var liftPx: CGFloat = 0
    private var liftMovedAt: CGFloat = 0
    private var liftPrev: CGFloat = -1

    private let grabAt = CGPoint(x: 24, y: 36)
    private let holdSeconds: CGFloat = 0.80
    private let shakeAmp: CGFloat = 40
    private let shakePeriod: CGFloat = 0.30
    private let shakeCycles: CGFloat = 4
    private let idleWatch: CGFloat = 3.0

    func advance(_ m: DragModule, _ ctx: TickContext) {
        let dt = ctx.dt
        let startAt: CGFloat = 0.10
        let breakAt = startAt + 0.02
        let shakeStart = breakAt + holdSeconds
        let shakeEnd = shakeStart + shakePeriod * shakeCycles

        let was = t
        t += dt

        // Scripted input only; see DragModule.demoInjecting.
        m.demoInjecting = true
        if was < startAt, t >= startAt {
            print("# demo: grab at atlas \(Int(grabAt.x)),\(Int(grabAt.y))")
            _ = m.mouseDown(at: grabAt)
        }
        if was < breakAt, t >= breakAt {
            print("# demo: clear the 4px deadzone (6px step) -> drag begins")
            m.mouseDragged(by: CGPoint(x: 6, y: 0))
            print("# demo: \(m.debugGeometry)")
        }
        if t > shakeStart, t <= shakeEnd {
            if was <= shakeStart { print("# demo: shake, 4 cycles at 40px / 0.30s") }
            let phase = (t - shakeStart) / shakePeriod * 2 * .pi
            let next = sin(phase) * shakeAmp
            m.mouseDragged(by: CGPoint(x: next - shakeX, y: 0))
            shakeX = next
        }
        if !released, t > shakeEnd {
            released = true
            releasedAt = t
            print("# demo: release")
            m.mouseUp(at: grabAt)
        }
        m.demoInjecting = false

        guard t >= startAt else { return }
        frames += 1
        let s = m.debugState
        maxStretch = max(maxStretch, s.stretch)
        minStretch = min(minStretch, s.stretch)
        // When the stretch channel goes quiet, which is what the tempo presets move and
        // what the peaks above cannot show -- they are amplitudes, and the presets scale
        // rates. Measured as the LAST frame that was still visibly moving rather than
        // the first quiet one, because the recovery bounces and an early sample sits in
        // a trough. The settle line below is the pendulum, which is a different spring.
        if released, abs(s.stretch) > 0.02 { lastLoud = t }
        if t < shakeStart {
            if abs(s.dropPx - liftPrev) > 0.05 { liftMovedAt = t }
            liftPrev = s.dropPx
            liftPx = s.dropPx
        }
        maxAngle = max(maxAngle, abs(s.angleDeg))
        maxDrop = max(maxDrop, s.dropPx)
        minSquash = min(minSquash, s.squash)
        maxLean = max(maxLean, abs(s.leanPx))
        func f(_ v: CGFloat, _ places: Int, _ width: Int) -> String {
            let text = String(format: "%+.\(places)f", Double(v))
            return String(repeating: " ", count: max(0, width - text.count)) + text
        }
        print("t=\(f(t, 3, 7)) \(s.phase.padding(toLength: 4, withPad: " ", startingAt: 0))"
              + " hold=\(f(s.holdT, 3, 6))"
              + " stretch=\(f(s.stretch, 4, 7))"
              + " dropPx=\(f(s.dropPx, 2, 6))"
              + " squash=\(f(s.squash, 3, 6))"
              + " angle=\(f(s.angleDeg, 3, 8))deg"
              + " angVel=\(f(s.angVel, 5, 9))"
              + " leanPx=\(f(s.leanPx, 2, 6))")

        if released, settledAt == nil, s.phase == "idle" {
            settledAt = t
            print("# demo: SETTLED \(String(format: "%.3f", Double(t - shakeEnd)))s "
                  + "after release; watching \(Int(idleWatch))s for residual motion")
            print("# demo: \(m.debugGeometry)")
        }
        if let settledAt {
            if s.stretch != 0 || s.angleDeg != 0 || s.angVel != 0 || s.leanPx != 0 {
                residualBreaches += 1
            }
            if t - settledAt > idleWatch {
                print("# demo: residual non-zero frames after settle: \(residualBreaches)")
                print(String(
                    format: "# demo: peaks stretch=+%.4f/%.4f angle=%.3fdeg dropPx=%.2f "
                          + "squash=%.4f leanPx=%.2f quietMs=%.0f liftPx=%.2f liftMs=%.0f",
                    Double(maxStretch), Double(minStretch), Double(maxAngle),
                    Double(maxDrop), Double(minSquash), Double(maxLean),
                    Double(max(0, (lastLoud - releasedAt) * 1000)),
                    Double(liftPx), Double(max(0, (liftMovedAt - breakAt) * 1000))))
                print(residualBreaches == 0
                      ? "# demo: PASS -- came to rest and stayed there"
                      : "# demo: FAIL -- still moving after settle")
                fflush(stdout)
                exit(residualBreaches == 0 ? 0 : 1)
            }
        }
    }
}

extension DragModule {
    /// Window and inset, so the demo can show that the cat actually got the room
    /// it needs rather than being quietly clipped by the window edge.
    fileprivate var debugGeometry: String {
        let f = panel?.frame ?? .zero
        let pad = view?.atlas.layout.padY ?? 0
        return "panel \(Int(f.width))x\(Int(f.height))pt, static margin \(pad)px"
    }

    /// Read-only snapshot for the scripted demo and any future debug overlay.
    fileprivate var debugState:
        (phase: String, holdT: CGFloat,stretch: CGFloat, dropPx: CGFloat,
         squash: CGFloat, angleDeg: CGFloat, angVel: CGFloat, leanPx: CGFloat) {
        let name: String
        switch phase {
        case .idle: name = "idle"
        case .pending: name = "pend"
        case .dragging: name = "DRAG"
        case .settling: name = "rel"
        }
        let hold = min(1, heldSeconds * 1000 / max(t.stretchHoldMs, 1))
        return (name,
                phase == .dragging ? hold : 0,
                stretchPx / spanPx,
                max(0, stretchPx),
                1 + min(0, stretchPx) * t.landingSquashPerPx,
                angle * 180 / .pi,
                angVel,
                sin(angle) * (spanPx + max(0, stretchPx)) * t.swingArmFrac)
    }
}
