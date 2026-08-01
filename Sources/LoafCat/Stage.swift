import AppKit

/// One overlay sprite, placed for one frame.
///
/// `offset` is in LOGICAL pixels from the part's anchor in the atlas, y-down, and is
/// rounded to a whole logical pixel by the view before scaling — the same path every
/// body part takes, so an overlay cannot be the thing that crawls.
struct OverlayInstance {
    let part: String
    var offset: CGPoint
    var alpha: CGFloat
}

/// The one-frame mailbox between modules and the rig.
///
/// `ModuleOutput` carries what the coordinator has to *arbitrate* — a requested
/// state, a squash, a whole-cat offset. This carries what a module wants to do to
/// individual parts, which needs no arbitration because offsets simply add.
///
/// It is a shared mailbox rather than a reference each module holds because both the
/// rig and the view are rebuilt when the theme or the scale changes: a module that
/// captured either at registration would spend the rest of the session driving a
/// dead object. Publishing through here means modules never hold anything that can
/// go stale, and it is why registering a feature stays a single line in `main.swift`.
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

    /// 0 = normal coat, 1 = fully overheated. The view cross-fades the `_hot`
    /// palette-remapped variant of each coat part by this much.
    var heat: CGFloat = 0

    var overlays: [OverlayInstance] = []

    /// Free-form numbers for `--debug-state`. Never read by the rig or the view, so
    /// a module can publish whatever makes its behaviour checkable from a log.
    /// Deliberately not cleared per frame: a metric keeps its last value, which is
    /// what makes a 10Hz log readable.
    var metrics: [String: CGFloat] = [:]

    func beginFrame() {
        bodyOffset = .zero
        headOffset = .zero
        pawOffsetL = .zero
        pawOffsetR = .zero
        tailOffset = .zero
        heat = 0
        overlays.removeAll(keepingCapacity: true)
    }

    func endFrame(state: CatState, bodyOffset: CGPoint) {
        self.state = state
        self.bodyOffset = bodyOffset
    }

    func metric(_ key: String, _ value: CGFloat) { metrics[key] = value }
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
