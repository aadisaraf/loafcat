import AppKit
import QuartzCore

/// A critically-damped spring. Used for every easing in the rig.
///
/// Springs rather than tweens because the cat is reacting to a target that moves
/// continuously (the cursor), and a tween would have to be restarted every frame.
/// A spring just chases, which is also what makes the motion read as alive rather
/// than scripted.
struct Spring {
    var value: CGFloat = 0
    var velocity: CGFloat = 0
    var stiffness: CGFloat
    var damping: CGFloat

    mutating func step(to target: CGFloat, dt: CGFloat) {
        // Clamp dt so a stalled frame cannot fling the spring across the screen.
        let h = min(dt, 1.0 / 30.0)
        let force = (target - value) * stiffness
        velocity = (velocity + force * h) * pow(damping, h * 60)
        value += velocity * h
    }

    mutating func snap(to target: CGFloat) {
        value = target
        velocity = 0
    }
}

/// Owns the per-part transforms and the animation state that produces them.
///
/// The rig never redraws art. Every motion here is a transform of the same parts,
/// which is why the whole animation set costs ~16 drawn pieces instead of ~150
/// frames — and why frame-to-frame consistency is structural rather than a thing
/// someone has to maintain.
final class Rig {
    let atlas: Atlas

    // --- cursor tracking ---------------------------------------------------
    // Four layers, each chasing the same target at a different rate. Pupils lead,
    // then eyes, then face, then body. That stagger is the entire "alive" effect —
    // move them together and it reads as a sprite being nudged.
    private var pupilX = Spring(stiffness: 180, damping: 0.72)
    private var pupilY = Spring(stiffness: 180, damping: 0.72)
    private var eyeX = Spring(stiffness: 130, damping: 0.74)
    private var eyeY = Spring(stiffness: 130, damping: 0.74)
    private var headX = Spring(stiffness: 90, damping: 0.75)
    private var headY = Spring(stiffness: 90, damping: 0.75)
    private var bodyX = Spring(stiffness: 40, damping: 0.80)

    /// How far each layer may travel, in logical pixels.
    private let pupilRange: CGFloat
    private let headRange: CGFloat = 3.2
    private let eyeRange: CGFloat = 1.3
    private let bodyRange: CGFloat = 1.0

    /// Cursor distance at which tracking saturates. Beyond this the cat is already
    /// looking as far as it can, so there is nothing more to express.
    private let trackSaturation: CGFloat = 400

    // --- idle ambience -----------------------------------------------------
    private var elapsed: CGFloat = 0
    private var nextBlink: CGFloat = 2.5
    private var blinkUntil: CGFloat = -1
    private var breathe = Spring(stiffness: 60, damping: 0.9)

    // --- tail --------------------------------------------------------------
    private var tailSway = Spring(stiffness: 55, damping: 0.86)

    // --- squash / stretch --------------------------------------------------
    private(set) var squash: CGFloat = 1.0

    struct Transform {
        var offset = CGPoint.zero
        var scale = CGSize(width: 1, height: 1)
        var hidden = false
    }

    private(set) var transforms: [String: Transform] = [:]

    init(atlas: Atlas) {
        self.atlas = atlas
        // The pupil may travel exactly as far as the sclera allows, no further —
        // a pupil that clips outside its eye is the classic rig tell.
        self.pupilRange = atlas.eye.maxOffset
    }

    /// `cursor` is the cursor position relative to the cat's centre, in logical px.
    func update(dt: CGFloat, cursor: CGPoint, isBlinkSuppressed: Bool = false) {
        elapsed += dt

        // Normalise and saturate. Using the raw vector would make the cat's gaze
        // jitter wildly for small movements far away.
        let dist = max(hypot(cursor.x, cursor.y), 0.0001)
        let clamped = min(dist, trackSaturation) / trackSaturation
        let nx = (cursor.x / dist) * clamped
        let ny = (cursor.y / dist) * clamped

        pupilX.step(to: nx * pupilRange, dt: dt)
        pupilY.step(to: ny * pupilRange, dt: dt)
        eyeX.step(to: nx * eyeRange, dt: dt)
        eyeY.step(to: ny * eyeRange * 0.85, dt: dt)
        headX.step(to: nx * headRange, dt: dt)
        headY.step(to: ny * headRange * 0.85, dt: dt)
        bodyX.step(to: nx * bodyRange, dt: dt)

        // Breathing: a slow sine on vertical scale. Tiny — 2% — because anything
        // larger reads as panting.
        let breath = sin(elapsed * 1.6) * 0.5 + 0.5
        breathe.step(to: breath, dt: dt)
        let breathScale = 1.0 + breathe.value * 0.02

        // Tail sway, lagging the body. Highest-leverage aliveness per unit of
        // effort in the whole rig, and it costs zero frames.
        tailSway.step(to: sin(elapsed * 1.1) * 1.6 + bodyX.value * 2.0, dt: dt)

        // Blink on a Poisson-ish schedule. Perfectly periodic blinking is one of
        // the strongest cues that something is a looping GIF rather than a creature.
        var blinking = false
        if !isBlinkSuppressed {
            if elapsed >= nextBlink {
                blinkUntil = elapsed + 0.12
                nextBlink = elapsed + CGFloat.random(in: 2.2...6.5)
            }
            blinking = elapsed < blinkUntil
        }

        rebuild(breathScale: breathScale, blinking: blinking)
    }

    private func rebuild(breathScale: CGFloat, blinking: Bool) {
        var t: [String: Transform] = [:]

        // Volume-preserving squash: widen as it shortens. Without the inverse the
        // cat visibly loses mass at the extremes.
        let sy = breathScale * squash
        let sx = 1.0 / sqrt(sy)
        let bodyLift = (sy - 1.0) * 8.0

        for name in atlas.order {
            var tr = Transform()

            switch name {
            case "body", "paw_l", "paw_r", "shadow":
                tr.offset.x = bodyX.value
                if name == "body" {
                    tr.scale = CGSize(width: sx, height: sy)
                    tr.offset.y = -bodyLift * 0.5
                }
                if name == "shadow" {
                    // Shadow scales inversely: as the cat rises it shrinks and
                    // darkens less. This is what sells the lift as vertical motion.
                    tr.scale = CGSize(width: 1 + (1 - sy) * 1.8, height: 1)
                }

            case "head", "ear_l", "ear_r", "face":
                tr.offset.x = headX.value + bodyX.value * 0.5
                tr.offset.y = headY.value - bodyLift
                if name == "ear_l" { tr.offset.x -= headX.value * 0.15 }
                if name == "ear_r" { tr.offset.x += headX.value * 0.15 }

            case "eye_l", "eye_r":
                tr.offset.x = headX.value + eyeX.value + bodyX.value * 0.5
                tr.offset.y = headY.value + eyeY.value - bodyLift
                tr.hidden = blinking

            case "pupil_l", "pupil_r":
                // Pupils carry the head, the eye AND their own offset — so they
                // travel furthest and arrive first. That ordering is the effect.
                tr.offset.x = headX.value + eyeX.value + bodyX.value * 0.5 + pupilX.value
                tr.offset.y = headY.value + eyeY.value - bodyLift + pupilY.value
                tr.hidden = blinking

            case "lid_l", "lid_r":
                tr.offset.x = headX.value + eyeX.value + bodyX.value * 0.5
                tr.offset.y = headY.value + eyeY.value - bodyLift
                tr.hidden = !blinking

            case "tail":
                tr.offset.x = tailSway.value + bodyX.value
                tr.offset.y = -bodyLift * 0.3

            default:
                break
            }
            t[name] = tr
        }
        transforms = t
    }

    /// Drives squash directly — used by drag, landing and the agent-done hop.
    func setSquash(_ v: CGFloat) {
        // Clamped hard. Beyond this range pixel art stops reading as the same
        // character and starts reading as a rendering bug.
        squash = min(max(v, 0.88), 1.14)
    }
}
