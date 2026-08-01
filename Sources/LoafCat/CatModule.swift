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

    /// When set AND this module wins the state contest, every other module's
    /// squash, offset and overlay is discarded for the frame — and so is
    /// everything any module posted to `CatStage`'s per-part channels, which is
    /// why an exclusive module must express itself through this struct alone.
    ///
    /// Priority alone only decides which *state* wins; the numbers still blend. A
    /// stretch break needs the stronger guarantee — the cat is the size of the
    /// screen and any leftover kneading or hunting squash reads as a glitch, not as
    /// a second opinion. Defaults to false, so no existing module changes behaviour.
    var exclusive: Bool = false

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
    ///
    /// Unless the winner asked to be exclusive, in which case it is the only module
    /// that contributes anything this frame.
    ///
    /// The shared stage is cleared before the modules run and sealed after, so a
    /// module reading `CatStage.shared.state` during its own update sees the
    /// PREVIOUS frame's winner. That one-frame lag is what lets a module yield to
    /// dragging without knowing which module owns dragging.
    func update(_ ctx: TickContext) -> ModuleOutput {
        let stage = CatStage.shared
        stage.beginFrame()

        var outputs: [ModuleOutput] = []
        outputs.reserveCapacity(modules.count)
        var best = -1
        var winner: ModuleOutput?
        state = .idle
        stateOwner = "-"
        overlays.removeAll()

        // Every module is still ticked, even when one is exclusive: a module that
        // stops being called mid-gesture loses its own timers and comes back wrong.
        for m in modules {
            let out = m.update(ctx)
            outputs.append(out)
            if let s = out.state, s.priority > best {
                best = s.priority
                state = s
                stateOwner = m.id
                winner = out
            }
        }

        var combined = ModuleOutput()
        if let winner, winner.exclusive {
            combined.squash = winner.squash
            combined.offset = winner.offset
            if let o = winner.overlay { overlays.append(o) }
            // Same rule, applied to the other channel modules write on. Priority
            // decided the state; `exclusive` is the stronger claim that nothing
            // else is on screen, and a stray paw offset or puff of steam would
            // break it exactly as visibly as a stray squash.
            stage.discardPartChannels()
        } else {
            for out in outputs {
                combined.squash *= out.squash
                combined.offset.x += out.offset.x
                combined.offset.y += out.offset.y
                if let o = out.overlay { overlays.append(o) }
            }
        }
        combined.state = state
        stage.endFrame(state: state, bodyOffset: combined.offset)
        logState(ctx)
        return combined
    }

    // --- --debug-state ------------------------------------------------------
    // Prints the winning state and every metric the modules publish, so behaviour
    // can be checked from a log rather than by a human watching a cat. Off unless
    // asked for, and rate-limited, because this runs on the 120Hz tick.

    private let debugging = CommandLine.arguments.contains("--debug-state")
    private let launched = CFAbsoluteTimeGetCurrent()
    private var lastLog: CFAbsoluteTime = 0
    private var lastLoggedState: CatState?

    private func logState(_ ctx: TickContext) {
        guard debugging else { return }
        let now = CFAbsoluteTimeGetCurrent()
        let changed = lastLoggedState != state
        guard changed || now - lastLog >= 0.1 else { return }
        lastLog = now
        lastLoggedState = state

        let stage = CatStage.shared
        let metrics = stage.metrics.keys.sorted()
            .map { "\($0)=\(Self.n(stage.metrics[$0] ?? 0))" }
            .joined(separator: " ")
        var line = "[state] t=\(Self.n(CGFloat(now - launched)))"
        line += " \(Self.pad(state.rawValue, 11)) by=\(Self.pad(stateOwner, 8))"
        line += " kps=\(Self.n(ctx.keysPerSecond)) heat=\(Self.n(stage.heat)) \(metrics)"
        if !overlays.isEmpty { line += " fx=\(overlays.joined(separator: ","))" }
        if changed { line += "  <-" }
        FileHandle.standardError.write((line + "\n").data(using: .utf8)!)
    }

    private static func n(_ v: CGFloat) -> String { String(format: "%.2f", Double(v)) }
    private static func pad(_ s: String, _ w: Int) -> String {
        s.count >= w ? s : s + String(repeating: " ", count: w - s.count)
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
