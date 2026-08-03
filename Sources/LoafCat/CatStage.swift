import AppKit
import QuartzCore

/// One overlay sprite, placed for one frame.
///
/// `offset` is in LOGICAL pixels from the sprite's anchor in the atlas, y-down, and
/// is rounded to a whole logical pixel by the view before scaling — the same path
/// every body part takes, so an overlay cannot be the thing that crawls.
struct OverlayInstance {
    let part: String
    var offset: CGPoint
    var alpha: CGFloat
}

/// The one-frame mailbox between the modules and the rig, and the clock for the
/// keyframed animations they ask to play.
///
/// `ModuleOutput` carries what the coordinator has to *arbitrate* — a requested
/// state, a squash, a whole-cat offset. This carries the two things that need no
/// arbitration: what a module wants to do to individual parts (offsets simply add),
/// and which named animation it wants running (only one module drives each).
///
/// It is a shared mailbox rather than a reference each module holds because both the
/// rig and the view are rebuilt when the theme or the scale changes: a module that
/// captured either at registration would spend the rest of the session driving a
/// dead object. Publishing through here means modules never hold anything that can
/// go stale, and it is why registering a feature stays a single line in `main.swift`.
///
/// Main thread only. Modules may take input on a background queue — the agent
/// listener does — but everything below is touched from `update(_:)`, which is the
/// tick.
final class CatStage {
    static let shared = CatStage()
    private init() {}

    // --- published by the runtime, read by modules --------------------------

    /// The atlas currently on screen. Modules read their geometry and their tuning
    /// constants from it; nothing about the cat's body or behaviour is declared in
    /// Swift.
    private(set) var atlas: Atlas?

    /// Bumped every time a new atlas is published, so a module knows to re-read the
    /// constants it cached. Cheaper than a dictionary lookup per constant per frame,
    /// and correct across a theme switch.
    private(set) var atlasGeneration = 0

    /// The winning state as of the PREVIOUS frame. Modules read it to yield to
    /// direct manipulation ("stop purring the instant a drag starts") without having
    /// to know which module owns dragging.
    private(set) var state: CatState = .idle

    /// True while a full-screen window is covering the cat's display and something is
    /// holding the screen awake — a film, a call, a presentation. Published by
    /// `PeekModule`; read by the wellness reminders, which must not blow the cat up
    /// to the size of the screen over any of those.
    var fullscreenBusy = false

    func publish(atlas: Atlas) {
        self.atlas = atlas
        atlasGeneration &+= 1
    }

    // --- written by modules, read by the rig and the view -------------------

    /// Whole-cat offset. Written by the registry from the summed `ModuleOutput`s,
    /// never by a module directly, so there is exactly one way to move the cat.
    private(set) var bodyOffset = CGPoint.zero

    /// Offset for the head and everything parented to it — ears, face, eyes, pupils,
    /// lids. A module that moved `head` alone would tear the face off.
    var headOffset = CGPoint.zero

    var pawOffsetL = CGPoint.zero
    var pawOffsetR = CGPoint.zero
    var tailOffset = CGPoint.zero

    /// Parts the winning module does not want drawn this frame.
    ///
    /// Added for the peek pose, where the cat hides behind the screen edge and puts
    /// only its head and two paws over it — the body is not clipped, it is *absent*,
    /// which is the difference between a cat hiding and a cat someone has cut in half.
    /// Every other pose leaves this empty and draws the whole animal.
    var hiddenParts: Set<String> = []

    /// True while the cat is in that pose. The view keeps a second hit mask for it:
    /// the silhouette really is a different shape, and testing clicks against the
    /// whole-cat mask would leave a body-sized patch of dead screen under the chin
    /// where there is nothing to click on.
    var peekPose = false

    /// 0 = normal coat, 1 = fully overheated. The view cross-fades the `_hot`
    /// palette-remapped variant of each coat part by this much.
    var heat: CGFloat = 0

    /// Every sprite the modules want drawn above the cat this frame — steam, hearts,
    /// and the agent's status glyph, which resolves itself through `overlayFrame()`
    /// and posts here like anything else. One list, so the view has one overlay path.
    var overlays: [OverlayInstance] = []

    /// Free-form numbers for `--debug-state`. Never read by the rig or the view, so
    /// a module can publish whatever makes its behaviour checkable from a log.
    /// Deliberately not cleared per frame: a metric keeps its last value, which is
    /// what makes a 10Hz log readable.
    var metrics: [String: CGFloat] = [:]

    func beginFrame() {
        bodyOffset = .zero
        discardPartChannels()
    }

    /// Throws away everything the modules posted about individual parts.
    ///
    /// Called at the top of every frame, and again when the winning module asked to
    /// be `exclusive` — that flag promises the winner is the ONLY thing affecting
    /// the cat this frame, and a leftover paw offset or a puff of steam would break
    /// the promise just as visibly as a leftover squash would. The corollary is that
    /// an exclusive module must express itself entirely through `ModuleOutput`.
    func discardPartChannels() {
        headOffset = .zero
        pawOffsetL = .zero
        pawOffsetR = .zero
        tailOffset = .zero
        heat = 0
        overlays.removeAll(keepingCapacity: true)
        hiddenParts.removeAll(keepingCapacity: true)
        peekPose = false
    }

    func endFrame(state: CatState, bodyOffset: CGPoint) {
        self.state = state
        self.bodyOffset = bodyOffset
    }

    func metric(_ key: String, _ value: CGFloat) { metrics[key] = value }

    // --- the keyframe clock -------------------------------------------------
    // Whole-body reactions authored as keyframes in `cat.json` rather than computed
    // per frame: the celebratory hop, the error slump, the thinking bob. The clock
    // lives here for the same reason the mailbox does — it has to survive the rig
    // and the view being rebuilt underneath a running animation.

    private var current: String?
    private var startedAt: CFTimeInterval = 0
    private var overlayName: String?

    /// How long a named animation runs, straight from the atlas. Modules ask this
    /// rather than hardcoding a duration, so "how long is the hop" has exactly one
    /// answer and it is in `cat.json`.
    func duration(of name: String) -> Double { atlas?.animations[name]?.duration ?? 0 }

    /// Asks for an animation and its overlay flipbook. Restarting only on a *change*
    /// is what makes this callable every frame — a module can state what it wants
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

    /// The overlay SPRITE to draw right now, resolved through the flipbook, or nil.
    func overlayFrame() -> String? {
        guard let name = overlayName, let anim = atlas?.overlayAnimations[name] else {
            return nil
        }
        return anim.frame(at: CACurrentMediaTime() - startedAt)
    }
}

/// Boilerplate for the one thing every module has to get right: its constants come
/// from `cat.json`, and `cat.json` changes when the user picks another cat.
///
/// Caching them and re-reading on a generation bump keeps the 120Hz path free of
/// dictionary lookups while staying correct across a theme switch.
protocol AtlasTuned: AnyObject {
    var tunedGeneration: Int { get set }
    /// Re-read every constant. Called once at startup and again on a theme change.
    func retune(_ atlas: Atlas)
}

extension AtlasTuned {
    /// The live atlas, retuned first if the theme changed. `nil` before the first
    /// atlas is published, which no module should assume cannot happen.
    func tunedAtlas() -> Atlas? {
        let stage = CatStage.shared
        guard let atlas = stage.atlas else { return nil }
        if tunedGeneration != stage.atlasGeneration {
            tunedGeneration = stage.atlasGeneration
            retune(atlas)
        }
        return atlas
    }
}
