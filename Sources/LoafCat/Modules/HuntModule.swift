import AppKit

/// Stalking a cursor that is being wiggled like a cat toy.
///
/// The thing this deliberately is NOT is a speed threshold. A speed threshold fires
/// on every ordinary sweep across a wide display, which is most cursor movement, and
/// then the cat is permanently crouched. What actually reads as "prey" is *changing
/// direction*: a real cat ignores a mouse running in a straight line past it and
/// fixates on one that jinks.
///
/// So this is an energy accumulator whose reversal term dwarfs its speed term. The
/// speed term exists only to gate — its ceiling, `gain * excess / (1 - decay)`, is
/// tuned to sit below the trigger for any plausible straight sweep, so speed alone
/// can never pounce the cat. Reversals are what carry it over.
final class HuntModule: CatModule, AtlasTuned {
    let id = "hunt"
    var tunedGeneration = -1

    // --- constants, every one of them from cat.json -------------------------
    private var decayPerFrame: CGFloat = 1
    private var speedMin: CGFloat = 0
    private var speedGain: CGFloat = 0
    private var reverseLag: Double = 0
    private var reverseDot: CGFloat = 0
    private var reverseSpeed: CGFloat = 0
    private var reverseBonus: CGFloat = 0
    private var refractory: Double = 0
    private var accelMin: CGFloat = 0
    private var accelGain: CGFloat = 0
    private var trigger: CGFloat = 1
    private var resetTo: CGFloat = 0
    private var crouchTime: Double = 0
    private var recoverTime: Double = 0
    private var attack: CGFloat = 0.1
    private var crouchSquash: CGFloat = 1
    private var lean: CGFloat = 0
    private var bodyLean: CGFloat = 0
    private var pawReach: CGFloat = 0
    private var wiggleHz: CGFloat = 0
    private var wiggleAmp: CGFloat = 0

    // --- state --------------------------------------------------------------
    private enum Phase { case idle, crouch, recover }
    private var phase: Phase = .idle
    private var phaseUntil: CFAbsoluteTime = 0
    private var energy: CGFloat = 0
    private var pose: CGFloat = 0
    private var elapsed: CGFloat = 0
    private var lastVelocity = CGPoint.zero
    private var lastReverse: CFAbsoluteTime = 0
    private var reversals: CGFloat = 0

    /// A short trail of velocities, so a reversal is measured against where the
    /// cursor was heading a moment ago rather than one frame ago. At 120Hz the
    /// frame-to-frame angle is mostly EMA noise; over ~60ms it is the gesture.
    private var trail: [(t: CFAbsoluteTime, v: CGPoint)] = []
    private let trailCap = 48

    func retune(_ atlas: Atlas) {
        let b = atlas.behaviour
        decayPerFrame = b.f("hunt.decay_per_frame")
        speedMin = b.f("hunt.speed_min")
        speedGain = b.f("hunt.speed_gain_per_frame")
        reverseLag = Double(b.f("hunt.reverse_lag"))
        reverseDot = b.f("hunt.reverse_dot")
        reverseSpeed = b.f("hunt.reverse_speed")
        reverseBonus = b.f("hunt.reverse_bonus")
        refractory = Double(b.f("hunt.reverse_refractory"))
        accelMin = b.f("hunt.accel_min")
        accelGain = b.f("hunt.accel_gain_per_frame")
        trigger = max(b.f("hunt.trigger"), 0.001)
        resetTo = b.f("hunt.reset")
        crouchTime = Double(b.f("hunt.crouch"))
        recoverTime = Double(b.f("hunt.recover"))
        attack = max(b.f("hunt.attack"), 0.001)
        crouchSquash = b.f("hunt.squash")
        lean = b.f("hunt.lean")
        bodyLean = b.f("hunt.body_lean")
        pawReach = b.f("hunt.paw_reach")
        wiggleHz = b.f("hunt.wiggle_hz")
        wiggleAmp = b.f("hunt.wiggle_amp")
    }

    func update(_ ctx: TickContext) -> ModuleOutput {
        guard tunedAtlas() != nil else { return .none }
        let stage = CatStage.shared
        let now = CFAbsoluteTimeGetCurrent()
        elapsed += ctx.dt

        let v = ctx.cursorVelocity
        let speed = hypot(v.x, v.y)

        // Direct manipulation wins outright. A cat being dragged or stretched is not
        // also stalking, and the accumulator is emptied rather than frozen so that
        // letting go does not immediately fire whatever built up during the drag.
        let manipulated = stage.state == .dragging || stage.state == .stretching
        if manipulated {
            energy = 0
            phase = .idle
        } else {
            accumulate(now: now, dt: ctx.dt, v: v, speed: speed)
        }
        lastVelocity = v

        // --- pounce state machine -------------------------------------------
        if phase == .idle && energy >= trigger {
            phase = .crouch
            phaseUntil = now + crouchTime
            // Not zero: a cat that has just been teased is easier to tease again,
            // and emptying it would make a second pounce need the full build-up.
            energy = trigger * resetTo
        }
        switch phase {
        case .crouch: if now >= phaseUntil { phase = .recover; phaseUntil = now + recoverTime }
        case .recover: if now >= phaseUntil { phase = .idle }
        case .idle: break
        }

        // Ramp in over `attack`; ramp out over `recover`, which is a time constant of
        // a third of it so the pose has visibly finished when the phase does.
        let tau = phase == .crouch ? attack : CGFloat(recoverTime) / 3
        pose += ((phase == .crouch ? 1 : 0) - pose) * (1 - exp(-ctx.dt / max(tau, 0.001)))

        stage.metric("hunt.e", energy)
        stage.metric("hunt.spd", speed)
        stage.metric("hunt.rev", reversals)

        guard pose > 0.002 else { return .none }

        // --- the crouch ------------------------------------------------------
        var out = ModuleOutput()
        let dist = max(hypot(ctx.cursor.x, ctx.cursor.y), 0.0001)
        let dir = CGPoint(x: ctx.cursor.x / dist, y: ctx.cursor.y / dist)
        // The haunch wiggle every cat does before it commits.
        let wiggle = sin(elapsed * 2 * .pi * wiggleHz) * wiggleAmp * pose

        stage.headOffset.x += dir.x * lean * pose + wiggle
        stage.headOffset.y += dir.y * lean * 0.6 * pose
        stage.pawOffsetL.x += dir.x * pawReach * pose
        stage.pawOffsetR.x += dir.x * pawReach * pose
        stage.tailOffset.x -= dir.x * pawReach * pose

        out.offset.x = dir.x * bodyLean * pose + wiggle * 0.5
        // Squash alone lowers the cat: the rig lifts the body by (scale - 1), so a
        // scale below 1 drops it. Getting low IS the crouch.
        out.squash = 1 - (1 - crouchSquash) * pose
        // The state lasts exactly as long as the crouch and the return; the last few
        // frames of the pose settling are not "hunting" any more.
        if phase != .idle { out.state = .hunting }
        return out
    }

    private func accumulate(now: CFAbsoluteTime, dt: CGFloat, v: CGPoint, speed: CGFloat) {
        // Decay is quoted per frame at a nominal 60fps and normalised by dt, so the
        // accumulator has the same half-life whatever rate the tick actually runs at.
        energy *= pow(decayPerFrame, dt * 60)

        // The gate term. Bounded by construction: sustained excess speed E settles
        // at E * gain / (1 - decay), which is deliberately under the trigger.
        if speed > speedMin {
            energy += (speed - speedMin) * speedGain * dt * 60
        }

        // The term that actually matters. Compared against the heading from
        // `reverseLag` ago, and only counted once per refractory window — a single
        // reversal spans several 120Hz frames and would otherwise be paid for twice.
        trail.append((now, v))
        if trail.count > trailCap { trail.removeFirst(trail.count - trailCap) }
        if let old = trail.last(where: { now - $0.t >= reverseLag }) {
            let oldSpeed = hypot(old.v.x, old.v.y)
            if min(speed, oldSpeed) > reverseSpeed, now - lastReverse > refractory {
                let dot = (v.x * old.v.x + v.y * old.v.y) / (speed * oldSpeed)
                if dot < reverseDot {
                    energy += reverseBonus
                    lastReverse = now
                    reversals += 1
                }
            }
        }

        // A smaller nudge for sheer violence of movement, which catches a flick that
        // is over before it has time to reverse.
        if dt > 0 {
            let accel = hypot(v.x - lastVelocity.x, v.y - lastVelocity.y) / dt
            if accel > accelMin {
                energy += (accel - accelMin) * accelGain * dt * 60
            }
        }
    }
}
