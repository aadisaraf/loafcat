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

/// The channel a module uses to ask for a keyframed reaction, and the clock that
/// plays it.
///
/// A shared object rather than a constructor argument for one concrete reason:
/// the rig and the view are thrown away and rebuilt on every theme or scale
/// change, while modules live for the lifetime of the app. Handing a module a
/// reference to the rig would leave it holding a dead one the first time somebody
/// picks a different cat.
///
/// Main thread only. Modules take network input on a background queue, but the
/// decision to start an animation is made in `update(_:)`, which is the tick.
final class Stage {
    static let shared = Stage()
    private init() {}

    private(set) var atlas: Atlas?
    private var current: String?
    private var startedAt: CFTimeInterval = 0
    private var overlayName: String?

    /// Displacement already handed to the window server, in PHYSICAL points.
    ///
    /// It lives here rather than in the view because the view is what gets
    /// rebuilt mid-animation; keeping the applied value alongside the clock is
    /// what lets the next view finish the move instead of stranding the panel a
    /// few pixels off the floor.
    var appliedOffset = CGPoint.zero

    func adopt(_ a: Atlas) { atlas = a }

    /// How long a named animation runs, straight from the atlas. Modules ask this
    /// rather than hardcoding a duration, so "how long is the hop" has exactly one
    /// answer and it is in `cat.json`.
    func duration(of name: String) -> Double { atlas?.animations[name]?.duration ?? 0 }

    /// Asks for an animation and its overlay. Restarting only on a *change* is
    /// what makes this callable every frame — a module can state what it wants
    /// 120 times a second without ever resetting the clock.
    func request(_ name: String?, overlay: String?) {
        if name != current {
            current = name
            startedAt = CACurrentMediaTime()
        }
        overlayName = overlay
    }

    /// Whole-body offset (logical px, y-down) and squash for this instant.
    func sample() -> (offset: CGPoint, squash: CGFloat) {
        guard let name = current, let anim = atlas?.animations[name] else {
            return (.zero, 1.0)
        }
        return anim.sample(at: CACurrentMediaTime() - startedAt)
    }

    /// The overlay PART to draw right now, resolved through the flipbook, or nil.
    func overlayFrame() -> String? {
        guard let name = overlayName, let anim = atlas?.overlayAnimations[name] else {
            return nil
        }
        return anim.frame(at: CACurrentMediaTime() - startedAt)
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

    // --- drag deformation ---------------------------------------------------
    // `squash` is a single uniform number clamped to 0.88...1.14, which is right
    // for breathing and for a landing thump but cannot express being held up by
    // the scruff: that is not a uniform scale, it is a gradient. The head keeps
    // its shape, the torso elongates, and the paws and tail hang furthest. So the
    // drag gets its own channel that is DISTRIBUTED by each part's depth below
    // the held point, rather than fighting the uniform clamp.
    private(set) var dragStretch: CGFloat = 0

    /// Horizontal elongation, for when the cat is being swung sideways.
    ///
    /// A gel stretches along the axis it is being pulled, not always downward.
    /// Kept as a separate channel rather than rotating the hang, because rotating
    /// pixel art destroys the grid -- so we decompose the pull into a vertical
    /// component (this rig already had) and a horizontal one (this).
    private(set) var dragStretchX: CGFloat = 0
    private var dragGrabY: CGFloat = 0
    private var dragLeanPx: CGFloat = 0
    private var dragHeadLagPx: CGFloat = 0
    private var dragHeadSwingShare: CGFloat = 0
    private var dragShadowShrink: CGFloat = 0

    /// Vertical extent of the drawn cat, taken from the atlas rather than assumed,
    /// so depth normalises correctly for a theme whose cat is a different height.
    private let inkTop: CGFloat
    private let inkBottom: CGFloat

    /// Which deformation rule a part follows. Keyed by name, matching how
    /// `rebuild` already dispatches — the names are the atlas's contract.
    private enum DragGroup {
        case head    // head, ears, face, eyes, pupils, lids: rigid, so the face
                     // never shears apart into separate pieces
        case soft    // body, paws, tail: elongate to span their stretched extent
        case shadow  // stays on the ground and shrinks as the cat leaves it
    }

    private static func dragGroup(_ name: String) -> DragGroup {
        switch name {
        case "shadow": return .shadow
        case "body", "tail", "paw_l", "paw_r": return .soft
        default: return .head
        }
    }

    struct Transform {
        var offset = CGPoint.zero
        var scale = CGSize(width: 1, height: 1)
        var hidden = false
    }

    private(set) var transforms: [String: Transform] = [:]

    init(atlas: Atlas) {
        self.atlas = atlas
        // The stage outlives the rig, so it is re-pointed at whatever theme is
        // current rather than owning one.
        Stage.shared.adopt(atlas)
        // The pupil may travel exactly as far as the sclera allows, no further —
        // a pupil that clips outside its eye is the classic rig tell.
        self.pupilRange = atlas.eye.maxOffset

        // Measured, not assumed: the shadow is excluded because it is cast ON the
        // floor rather than part of the body, and including it would put the
        // bottom of the pendulum below the paws.
        var top = CGFloat.greatestFiniteMagnitude
        var bottom: CGFloat = 0
        for (name, part) in atlas.parts where name != "shadow" {
            top = min(top, part.origin.y)
            bottom = max(bottom, part.origin.y + part.size.height)
        }
        self.inkTop = top.isFinite ? top : 0
        self.inkBottom = max(bottom, self.inkTop + 1)
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
            applyDrag(to: &tr, part: name)
            t[name] = tr
        }
        transforms = t
    }

    /// Folds the hang and the swing into a part's transform.
    ///
    /// Two independent effects share one pass because both are functions of the
    /// same quantity: how far below the held point the part sits.
    private func applyDrag(to tr: inout Transform, part name: String) {
        guard dragStretch != 0 || dragLeanPx != 0 || dragStretchX != 0 else { return }
        guard let part = atlas.parts[name] else { return }

        let group = Self.dragGroup(name)
        let hang = inkBottom - dragGrabY

        // --- vertical: the hang -------------------------------------------
        // Displacement grows with distance BELOW the held point; anything above
        // it is being supported and does not fall.
        func drop(_ y: CGFloat) -> CGFloat { dragStretch * max(0, y - dragGrabY) }

        switch group {
        case .head:
            // Rigid, and deliberately barely moves: a head that stretched with
            // the body would drag the eyes and muzzle out of register. The small
            // lag is what stops it reading as bolted on.
            tr.offset.y += dragStretch * dragHeadLagPx

        case .soft:
            // Scale the part to exactly span its own stretched extent, so the
            // torso ELONGATES to bridge the gap instead of the head and paws
            // sliding apart and tearing the silhouette open.
            let top = part.origin.y
            let bottom = top + part.size.height
            let stretchedTop = top + drop(top)
            let stretchedBottom = bottom + drop(bottom)
            let height = max(bottom - top, 0.0001)
            let k = (stretchedBottom - stretchedTop) / height

            // CatView scales about the atlas pivot, so back out the translation
            // that re-lands the part's top edge where the hang wants it.
            let pivotY = atlas.pivot(for: name).y
            tr.offset.y += stretchedTop - (pivotY + (top - pivotY) * k)
            tr.scale.height *= k

            // Horizontal elongation, with the vertical squeezed to conserve
            // volume. Without the inverse the cat visibly gains mass when swung.
            if dragStretchX != 0 {
                let kx = 1 + dragStretchX
                tr.scale.width *= kx
                tr.scale.height /= sqrt(kx)
            }

        case .shadow:
            // A lifted cat casts a smaller contact shadow. Selling the lift is
            // most of what makes the drag read as vertical at all.
            let shrink = 1 - min(1, dragStretch) * dragShadowShrink
            tr.scale.width *= max(shrink, 0.05)
        }

        // --- horizontal: the swing ----------------------------------------
        // Depth-quadratic, so the top barely moves and the bottom swings fully.
        // The brief says each part's share is proportional to d^2 - dPrev^2; those
        // increments telescope, so a part's CUMULATIVE share is just d^2 — which
        // is what gets applied here.
        //
        // Rounded to a whole logical pixel: this is an integer perpendicular
        // shear, never a rotation. Rotating a pixel-art layer resamples it off
        // the grid and the jaggies are unrecoverable.
        if dragLeanPx != 0 {
            let share: CGFloat
            if group == .shadow {
                share = 0                       // the floor does not swing
            } else if group == .head {
                share = dragHeadSwingShare      // a pixel of drift, no more
            } else {
                let anchorY = atlas.pivot(for: name).y
                let d = min(max((anchorY - dragGrabY) / max(hang, 0.0001), 0), 1)
                share = d * d
            }
            tr.offset.x += (dragLeanPx * share).rounded()
        }
    }

    /// Drives squash directly — used by drag, landing and the agent-done hop.
    func setSquash(_ v: CGFloat) {
        // Clamped hard. Beyond this range pixel art stops reading as the same
        // character and starts reading as a rendering bug.
        squash = min(max(v, 0.88), 1.14)
    }

    /// Drives the hang and the swing. Separate from `setSquash` because this one
    /// is a gradient down the body rather than a single uniform number, and
    /// because it legitimately needs to exceed the uniform squash clamp.
    ///
    /// - Parameters:
    ///   - stretch: how much longer the hanging part of the cat is, as a
    ///     fraction. 0 is at rest.
    ///   - grabY: the held point in atlas coordinates, y-down.
    ///   - leanPx: horizontal travel of the LOWEST part, in logical pixels.
    ///     Distributed upward as an integer shear.
    ///   - headLagPx: how far the head trails the body at full stretch.
    ///   - headSwingShare: the head's fixed share of the swing.
    ///   - shadowShrink: how much of the contact shadow is lost at full stretch.
    func setDrag(
        stretch: CGFloat, stretchX: CGFloat = 0, grabY: CGFloat, leanPx: CGFloat,
        headLagPx: CGFloat, headSwingShare: CGFloat, shadowShrink: CGFloat
    ) {
        dragStretch = max(0, stretch)
        dragStretchX = max(0, stretchX)
        dragGrabY = min(max(grabY, inkTop), inkBottom - 1)
        dragLeanPx = leanPx
        dragHeadLagPx = headLagPx
        dragHeadSwingShare = headSwingShare
        dragShadowShrink = shadowShrink
    }

    /// Clears the drag channel. Cheaper and more obvious at the call site than
    /// passing six zeroes.
    func clearDrag() {
        dragStretch = 0
        dragStretchX = 0
        dragLeanPx = 0
    }
}
