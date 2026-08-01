import AppKit

/// Everything a module is allowed to know about the world, gathered once per tick.
///
/// Passing a snapshot rather than letting modules reach into AppKit keeps them
/// testable and keeps the expensive calls (cursor position, event counters) to one
/// per frame no matter how many modules want them.
struct TickContext {
    /// Seconds since the previous tick, already clamped so a stalled frame cannot
    /// fling a spring across the screen.
    let dt: CGFloat

    /// Cursor position relative to the cat's centre, in LOGICAL pixels, y-down to
    /// match the atlas. Modules should never need screen coordinates.
    let cursor: CGPoint

    /// Cursor velocity in logical px/sec, y-down. Already smoothed.
    let cursorVelocity: CGPoint

    /// True when the cursor is over the cat's dilated silhouette.
    let cursorOnCat: Bool

    /// Keystrokes in the last `keyWindow` seconds, as a rate. Never key identity —
    /// see CLAUDE.md on why this is structural rather than a promise.
    let keysPerSecond: CGFloat

    /// Scroll wheel events observed since the previous tick.
    let scrollDelta: UInt32

    /// Seconds since the user last pressed any key, system-wide.
    let secondsSinceKey: Double

    /// The cat's window frame in screen coordinates, for modules that move it.
    let frame: NSRect

    /// Logical-pixels-per-point, so modules can convert if they must.
    let scale: CGFloat
}

/// Named states the cat can be in. Modules request them; the coordinator resolves
/// conflicts by priority so two modules cannot fight over the same frame.
enum CatState: String {
    case idle
    case kneading       // typing
    case overheat       // typing fast
    case hunting        // fast, reversing cursor
    case purring        // being petted
    case dragging
    case scrolling
    case thinking       // agent working
    case celebrating    // agent finished
    case errored        // agent failed
    case sleeping
    case stretching

    /// Higher wins when two modules want the cat at the same time. Direct physical
    /// manipulation always beats an ambient reaction — being picked up should
    /// interrupt a stretch, never the other way round.
    var priority: Int {
        switch self {
        case .idle: return 0
        case .sleeping: return 1
        case .thinking: return 2
        case .kneading, .scrolling: return 3
        case .overheat: return 4
        case .purring, .hunting: return 5
        case .celebrating, .errored: return 6
        case .stretching: return 7
        case .dragging: return 10
        }
    }
}

/// What a module wants to happen this frame. Everything is optional; a module that
/// has nothing to say returns `.none`.
struct ModuleOutput {
    /// The state this module is requesting, if any.
    var state: CatState?

    /// Extra vertical squash, multiplied into the rig's. 1.0 is neutral.
    var squash: CGFloat = 1.0

    /// Offset applied to the whole cat, in logical pixels.
    var offset: CGPoint = .zero

    /// A short-lived overlay to show above the cat (steam, hearts, zzz, a bubble).
    var overlay: String?

    static let none = ModuleOutput()
}

/// One feature, in one file.
///
/// Modules are registered in `main.swift` and are otherwise independent, which is
/// what lets several be developed in parallel without conflicting. A module can be
/// removed by deleting its file and its one registration line.
protocol CatModule: AnyObject {
    /// Stable identifier, used in logs and the debug overlay.
    var id: String { get }

    /// Called once per tick at 120Hz. Must not block — anything slow belongs on a
    /// background queue with its result read here.
    func update(_ ctx: TickContext) -> ModuleOutput

    /// Called when the user clicks the cat's body. Return true to consume.
    func mouseDown(at point: CGPoint) -> Bool

    /// Called when the user releases. Only sent to whoever consumed the down.
    func mouseUp(at point: CGPoint)

    /// Called on drag, in logical pixels of movement since the last event.
    func mouseDragged(by delta: CGPoint)
}

// Most modules care about one or two of these, so default them away.
extension CatModule {
    func mouseDown(at point: CGPoint) -> Bool { false }
    func mouseUp(at point: CGPoint) {}
    func mouseDragged(by delta: CGPoint) {}
}

/// Runs the registered modules and resolves what they collectively want.
final class ModuleRegistry {
    private(set) var modules: [CatModule] = []
    private weak var dragOwner: AnyObject?

    /// The winning state this frame, and which module asked for it.
    private(set) var state: CatState = .idle
    private(set) var stateOwner: String = "-"
    private(set) var overlays: [String] = []

    func register(_ m: CatModule) { modules.append(m) }

    /// Combined output for this tick. Squash multiplies (so two modules both
    /// compressing the cat compound), offsets add, and the highest-priority state
    /// wins outright rather than blending — a cat cannot be half-dragged.
    func update(_ ctx: TickContext) -> ModuleOutput {
        var combined = ModuleOutput()
        var best = -1
        state = .idle
        stateOwner = "-"
        overlays.removeAll()

        for m in modules {
            let out = m.update(ctx)
            combined.squash *= out.squash
            combined.offset.x += out.offset.x
            combined.offset.y += out.offset.y
            if let o = out.overlay { overlays.append(o) }
            if let s = out.state, s.priority > best {
                best = s.priority
                state = s
                stateOwner = m.id
            }
        }
        combined.state = state
        return combined
    }

    func mouseDown(at point: CGPoint) -> Bool {
        for m in modules where m.mouseDown(at: point) {
            dragOwner = m
            return true
        }
        return false
    }

    func mouseUp(at point: CGPoint) {
        (dragOwner as? CatModule)?.mouseUp(at: point)
        dragOwner = nil
    }

    func mouseDragged(by delta: CGPoint) {
        (dragOwner as? CatModule)?.mouseDragged(by: delta)
    }
}
