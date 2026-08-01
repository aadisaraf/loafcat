import AppKit

/// Purring when the cursor is stroked across the cat's head.
///
/// Two conditions, not one. Being *inside* the head region is not petting — a cursor
/// parked on the cat while its owner reads is not a hand. Petting is movement inside
/// the region, which is why the trigger is speed-gated and why it lapses shortly
/// after the cursor stops even without leaving.
final class PettingModule: CatModule, AtlasTuned {
    let id = "pet"
    var tunedGeneration = -1

    // --- the hit region, read from the head part's own rectangle ------------
    // Not a hardcoded box: a theme with a bigger head gets a bigger petting region
    // for free, and nothing here has to know where this atlas put the head.
    private var centre = CGPoint.zero
    private var radius = CGSize(width: 1, height: 1)
    private var halfCanvas: CGFloat = 24

    // --- constants ----------------------------------------------------------
    private var moveMin: CGFloat = 0
    private var stopDelay: Double = 0
    private var leaveDelay: Double = 0
    private var lean: CGFloat = 0
    private var purrHz: CGFloat = 0
    private var purrAmp: CGFloat = 0
    private var petSquash: CGFloat = 1
    private var attack: CGFloat = 0.1
    private var heartPeriod: CGFloat = 1
    private var heartRise: CGFloat = 0
    private var heartDrift: CGFloat = 0
    private var heartSlots = 0

    // --- state --------------------------------------------------------------
    private var amp: CGFloat = 0
    private var lastStroke: CFAbsoluteTime = 0
    private var leftAt: CFAbsoluteTime?
    private var elapsed: CGFloat = 0
    private var heartPhase: CGFloat = 0

    func retune(_ atlas: Atlas) {
        let b = atlas.behaviour
        halfCanvas = atlas.canvas / 2
        if let head = atlas.parts["head"] {
            let scale = b.f("pet.ellipse_scale")
            centre = CGPoint(
                x: head.origin.x + head.size.width / 2,
                y: head.origin.y + head.size.height / 2)
            radius = CGSize(
                width: max(head.size.width / 2 * scale, 0.001),
                height: max(head.size.height / 2 * scale, 0.001))
        }
        moveMin = b.f("pet.move_min")
        stopDelay = Double(b.f("pet.stop_delay"))
        leaveDelay = Double(b.f("pet.leave_delay"))
        lean = b.f("pet.lean")
        purrHz = b.f("pet.purr_hz")
        purrAmp = b.f("pet.purr_amp")
        petSquash = b.f("pet.squash")
        attack = max(b.f("pet.attack"), 0.001)
        heartPeriod = max(b.f("pet.heart_period"), 0.01)
        heartRise = b.f("pet.heart_rise")
        heartDrift = b.f("pet.heart_drift")
        heartSlots = atlas.overlays["heart"] ?? 0
    }

    func update(_ ctx: TickContext) -> ModuleOutput {
        guard tunedAtlas() != nil else { return .none }
        let stage = CatStage.shared
        let now = CFAbsoluteTimeGetCurrent()
        elapsed += ctx.dt

        // Being picked up is not being petted. Cut immediately rather than easing —
        // a cat that keeps purring for a third of a second after you grab it reads
        // as a bug, not as affection.
        if stage.state == .dragging {
            amp = 0
            lastStroke = 0
            leftAt = nil
            return .none
        }

        // The cursor arrives relative to the cat's CENTRE; the atlas measures from
        // the top-left corner. One conversion, here, and the ellipse test below is
        // then plain normalised cat-local coordinates.
        let p = CGPoint(x: ctx.cursor.x + halfCanvas, y: ctx.cursor.y + halfCanvas)
        let u = (p.x - centre.x) / radius.width
        let w = (p.y - centre.y) / radius.height
        let inside = u * u + w * w <= 1
        let moving = hypot(ctx.cursorVelocity.x, ctx.cursorVelocity.y) >= moveMin

        if inside {
            leftAt = nil
            if moving { lastStroke = now }
        } else if leftAt == nil {
            leftAt = now
        }

        let stalled = now - lastStroke > stopDelay
        let gone = leftAt.map { now - $0 > leaveDelay } ?? false
        let petting = lastStroke > 0 && !stalled && !gone

        amp += ((petting ? 1 : 0) - amp) * (1 - exp(-ctx.dt / attack))
        stage.metric("pet.in", inside ? 1 : 0)
        stage.metric("pet.amp", amp)
        guard amp > 0.002 else { return .none }

        var out = ModuleOutput()

        // Lean into the hand. Measured from the head's own centre so the lean is
        // toward where the stroking actually is, not toward the whole cat's middle.
        let dx = p.x - centre.x, dy = p.y - centre.y
        let d = max(hypot(dx, dy), 0.0001)
        let reach = min(d / max(radius.width, 0.001), 1)
        stage.headOffset.x += dx / d * lean * reach * amp
        stage.headOffset.y += dy / d * lean * 0.5 * reach * amp

        // The purr itself: a fast, sub-pixel vibration. It survives the whole-pixel
        // rounding as a 0/1 flicker, which is exactly what a purr looks like at this
        // resolution and is why the amplitude is deliberately under one pixel.
        let purr = sin(elapsed * 2 * .pi * purrHz) * purrAmp * amp
        out.offset.y = purr
        stage.headOffset.y += purr * 0.5
        out.squash = 1 - (1 - petSquash) * amp

        if heartSlots > 0 {
            heartPhase += ctx.dt / heartPeriod
            while heartPhase >= 1 { heartPhase -= 1 }
            for i in 0..<heartSlots {
                var t = heartPhase + CGFloat(i) / CGFloat(heartSlots)
                if t >= 1 { t -= 1 }
                let spread = (CGFloat(i) - CGFloat(heartSlots - 1) / 2) * heartDrift * 1.6
                stage.overlays.append(OverlayInstance(
                    part: "heart",
                    offset: CGPoint(
                        x: spread + sin(t * .pi * 2 + CGFloat(i)) * heartDrift,
                        y: -heartRise * t),
                    alpha: sin(t * .pi) * amp))
            }
            out.overlay = "hearts"
        }

        // The state ends when the stroking does; the pose is allowed a few frames to
        // ease out after it. Gating the state on the envelope instead would leave the
        // cat nominally purring for most of a second after the hand left.
        if petting { out.state = .purring }
        return out
    }
}
