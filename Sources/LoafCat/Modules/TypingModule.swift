import AppKit

/// The two things typing does to the cat: kneading, and overheating.
///
/// Both run off `ctx.keysPerSecond`, which is a rate derived from
/// `CGEventSource.counterForEventType` — an integer count of events, never a
/// keycode. There is no code path by which the identity of a key could reach this
/// file, which is what makes the privacy claim structural rather than a promise.
/// See CLAUDE.md: never reach for a CGEventTap or a global keyboard monitor to make
/// a reaction here nicer.
/// How much typing it takes to make the cat steam.
///
/// The atlas decides what "a lot of typing" means for a theme; this decides how much of
/// that a particular person has to do. Multipliers on `overheat.kps_min` and `kps_max`
/// together, so the SHAPE of the curve is untouched and only the range it spans moves —
/// which is why every preset still has a band where the cat kneads without reddening.
///
/// The same pattern as `DragFeel` and `StretchTempo`, for the same reason: a theme that
/// retunes overheating keeps all four presets meaningful instead of silently drifting.
/// `normal` is 1.0 by definition — the shipped tuning IS the normal preset.
enum HeatSensitivity: String, CaseIterable {
    case instant, quick, normal, patient

    var label: String {
        switch self {
        case .instant: return "Instant"
        case .quick: return "Quick"
        case .normal: return "Normal"
        case .patient: return "Patient"
        }
    }

    /// Against the shipped 3.5-to-9.5, these put a fully burning cat at roughly 5.7,
    /// 7.6, 9.5 and 12.8 keystrokes a second. Patient is about where the whole thing sat
    /// before it was retuned, which is the point of keeping it.
    var scale: CGFloat {
        switch self {
        case .instant: return 0.60
        case .quick: return 0.80
        case .normal: return 1.0
        case .patient: return 1.35
        }
    }

    static var current: HeatSensitivity {
        HeatSensitivity(rawValue:
            UserDefaults.standard.string(forKey: "heatSensitivity") ?? "normal") ?? .normal
    }
}

final class TypingModule: CatModule, AtlasTuned {
    let id = "typing"
    var tunedGeneration = -1

    // --- constants, every one of them from cat.json -------------------------
    private var gateKps: CGFloat = 0
    private var releaseDelay: Double = 0
    private var pawPeriod: CGFloat = 0.2
    private var pawLift: CGFloat = 0
    private var pawReach: CGFloat = 0
    private var bodyBob: CGFloat = 0
    private var squashDepth: CGFloat = 0
    private var attack: CGFloat = 0.1
    private var decay: CGFloat = 0.1

    private var kpsMin: CGFloat = 0
    private var kpsSpan: CGFloat = 1
    private var curve: CGFloat = 1
    private var easePerFrame: CGFloat = 0
    private var stateAt: CGFloat = 1
    private var steamAt: CGFloat = 1
    private var steamPeriod: CGFloat = 1
    private var steamRise: CGFloat = 0
    private var steamSlots = 0
    /// How far to shift a steam puff to mirror it to the cat's other side. Derived
    /// from where the art actually sits, not typed in here.
    private var steamMirror: CGFloat = 0

    // --- state --------------------------------------------------------------
    private var kneading = false
    private var amp: CGFloat = 0        // eased 0..1 envelope on the knead pose
    private var phase: CGFloat = 0      // 0..2; one stroke per unit, paws alternate
    private var heat: CGFloat = 0
    private var steamPhase: CGFloat = 0

    func retune(_ atlas: Atlas) {
        let b = atlas.behaviour
        // "5 keystrokes inside a 2s sliding window" is, for a sliding-window RATE,
        // exactly `kps >= 5/2`. That equivalence is the whole gate: a single
        // keypress produces a rate far below it, so isolated keys are ignored
        // without the module having to see individual keystrokes it is not allowed
        // to see anyway.
        gateKps = b.f("typing.burst_keys") / max(b.f("typing.burst_window"), 0.001)
        releaseDelay = Double(b.f("typing.release_delay"))
        pawPeriod = max(b.f("typing.paw_period"), 0.01)
        pawLift = b.f("typing.paw_lift")
        pawReach = b.f("typing.paw_reach")
        bodyBob = b.f("typing.body_bob")
        squashDepth = b.f("typing.squash")
        attack = max(b.f("typing.attack"), 0.001)
        decay = max(b.f("typing.decay"), 0.001)

        // Read after the atlas, exactly like the drag presets: the theme's numbers are
        // the baseline and the preference scales them.
        let sensitivity = HeatSensitivity.current.scale
        kpsMin = b.f("overheat.kps_min") * sensitivity
        kpsSpan = max(b.f("overheat.kps_max") * sensitivity - kpsMin, 0.001)
        curve = b.f("overheat.curve")
        easePerFrame = b.f("overheat.ease_per_frame")
        stateAt = b.f("overheat.state_at")
        steamAt = b.f("overheat.steam_at")
        steamPeriod = max(b.f("overheat.steam_period"), 0.01)
        steamRise = b.f("overheat.steam_rise")

        if let steam = atlas.overlays["steam"] {
            steamSlots = steam.slots
            let s = steam.part
            steamMirror = atlas.canvas - 2 * (s.origin.x + s.size.width / 2)
        } else {
            steamSlots = 0
        }
    }

    func update(_ ctx: TickContext) -> ModuleOutput {
        guard tunedAtlas() != nil else { return .none }
        let stage = CatStage.shared
        var out = ModuleOutput()

        // --- the gate -------------------------------------------------------
        if ctx.keysPerSecond >= gateKps { kneading = true }
        if kneading && ctx.secondsSinceKey > releaseDelay { kneading = false }

        // Ease the amplitude rather than the pose: snapping the paws to zero mid
        // stroke reads as a dropped frame, and easing the phase instead would make
        // the strokes slow down, which reads as the cat getting tired.
        let tau = kneading ? attack : decay
        amp += ((kneading ? 1 : 0) - amp) * (1 - exp(-ctx.dt / tau))

        if amp > 0.002 {
            phase += ctx.dt / pawPeriod
            while phase >= 2 { phase -= 2 }

            // One stroke per unit of phase, alternating paws — that alternation is
            // what makes it read as kneading rather than as a two-handed thump.
            let left = phase < 1
            let frac = left ? phase : phase - 1
            let swing = sin(frac * .pi)
            let lift = swing * pawLift * amp
            let reach = swing * pawReach * amp
            if left {
                stage.pawOffsetL.y -= lift
                stage.pawOffsetL.x -= reach
            } else {
                stage.pawOffsetR.y -= lift
                stage.pawOffsetR.x += reach
            }
            // The body rocks at twice the stroke rate, once per paw.
            out.offset.y = -abs(sin(phase * .pi)) * bodyBob * amp
            out.squash = 1 - abs(sin(phase * .pi)) * squashDepth * amp
        } else {
            phase = 0
        }

        // --- heat -----------------------------------------------------------
        // Continuous, never a binary state: zero below kps_min so ordinary typing
        // can never redden the cat, 1 at kps_max, curved in between.
        let t = min(max((ctx.keysPerSecond - kpsMin) / kpsSpan, 0), 1)
        let target = pow(t, curve)
        // The ease is quoted per frame at a nominal 60fps; normalising by dt makes
        // the 120Hz tick ramp at the same wall-clock rate instead of twice as fast.
        heat += (target - heat) * (1 - pow(1 - easePerFrame, ctx.dt * 60))
        stage.heat = heat

        if heat >= steamAt && steamSlots > 0 {
            steamPhase += ctx.dt / steamPeriod
            while steamPhase >= 1 { steamPhase -= 1 }
            let strength = min((heat - steamAt) / max(1 - steamAt, 0.001), 1)
            for i in 0..<steamSlots {
                var p = steamPhase + CGFloat(i) / CGFloat(steamSlots)
                if p >= 1 { p -= 1 }
                stage.overlays.append(OverlayInstance(
                    part: "steam",
                    // Odd puffs mirror to the cat's other side.
                    offset: CGPoint(x: i % 2 == 1 ? steamMirror : 0, y: -steamRise * p),
                    alpha: sin(p * .pi) * strength))
            }
            out.overlay = "steam"
        }

        if heat >= stateAt {
            out.state = .overheat
        } else if kneading {
            out.state = .kneading
        }

        stage.metric("knead", kneading ? 1 : 0)
        return out
    }
}
