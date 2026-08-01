// S1 — Click-through spike.
//
// Settles the disagreement between research rounds: does a transparent NSWindow
// pass mouse events through on transparent pixels, and by what mechanism?
//
// Three modes, switched with the 1/2/3 keys (or --mode on launch):
//   A  plain transparent panel, no intervention      <- tests the "free per-pixel" claim
//   B  hitTest() returns nil over transparent pixels <- the AppKit override claim
//   C  alpha-sampled ignoresMouseEvents toggling     <- the fallback everyone else ships
//
// The window draws an opaque disc in the middle of a transparent square.
// Click the disc: the spike should log HIT.
// Click the transparent corner while an app is behind it: that app should get
// the click and the spike should log nothing.

import AppKit

enum Mode: String {
    case a = "A", b = "B", c = "C", d = "D"

    var explanation: String {
        switch self {
        case .a: return "plain transparent panel, no intervention"
        case .b: return "hitTest() returns nil over transparent pixels"
        case .c: return "alpha-sampled toggling, driven by mouseMoved events"
        case .d: return "alpha-sampled toggling, POLLED + hysteresis (the fix)"
        }
    }
}

/// How far outside the silhouette we stay interactive.
///
/// The race that makes mode C leak: `ignoresMouseEvents` is set from a mouse-moved
/// event, but the window server decides where a click goes using whatever the flag
/// was when the click arrived. If the pointer crosses the boundary and clicks before
/// our update lands, the click goes to the wrong place.
///
/// Two fixes, both needed. Poll the cursor instead of waiting to be told (bounds the
/// staleness to the poll interval), and dilate the interactive region so we flip to
/// interactive *before* the pointer reaches the cat.
let hysteresisPadding: CGFloat = 6

// Shared so the view and the mouse monitor agree on what counts as opaque.
let discRadius: CGFloat = 70
let windowSide: CGFloat = 300
var currentMode: Mode = .b

/// True when the point (in view coordinates) lands on a pixel we painted.
func isOnCat(at point: NSPoint, in bounds: NSRect, padding: CGFloat = 0) -> Bool {
    let center = NSPoint(x: bounds.midX, y: bounds.midY)
    let dx = point.x - center.x
    let dy = point.y - center.y
    return (dx * dx + dy * dy).squareRoot() <= discRadius + padding
}

final class CatView: NSView {
    override func draw(_ dirtyRect: NSRect) {
        NSColor.clear.set()
        dirtyRect.fill()

        // Opaque disc — the "cat body".
        let rect = NSRect(
            x: bounds.midX - discRadius, y: bounds.midY - discRadius,
            width: discRadius * 2, height: discRadius * 2)
        NSColor.systemPink.setFill()
        NSBezierPath(ovalIn: rect).fill()

        // Outline the full window so the transparent region is visible while testing.
        NSColor.systemBlue.withAlphaComponent(0.35).setStroke()
        let border = NSBezierPath(rect: bounds.insetBy(dx: 1, dy: 1))
        border.lineWidth = 2
        border.stroke()

        let label = "MODE \(currentMode.rawValue)" as NSString
        label.draw(
            at: NSPoint(x: 8, y: 8),
            withAttributes: [
                .font: NSFont.monospacedSystemFont(ofSize: 13, weight: .bold),
                .foregroundColor: NSColor.white,
            ])
    }

    // Mode B is the mechanism under test: returning nil here means "this view does
    // not want the event". Whether AppKit then routes it to the app *below* — rather
    // than just to the window itself — is exactly the open question.
    override func hitTest(_ point: NSPoint) -> NSView? {
        guard currentMode == .b else { return super.hitTest(point) }
        let local = convert(point, from: superview)
        return isOnCat(at: local, in: bounds) ? super.hitTest(point) : nil
    }

    override func mouseDown(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        let region = isOnCat(at: p, in: bounds) ? "DISC (opaque)" : "CORNER (transparent)"
        print("  HIT  mode=\(currentMode.rawValue)  \(region)  at (\(Int(p.x)), \(Int(p.y)))")
        fflush(stdout)
    }
}

/// An opaque window placed directly beneath the panel. If a click on a transparent
/// pixel passes through, THIS view receives it — which makes the spike self-reporting
/// instead of relying on eyeballing some other app's reaction.
final class TargetView: NSView {
    override func draw(_ dirtyRect: NSRect) {
        NSColor(calibratedWhite: 0.16, alpha: 1).setFill()
        dirtyRect.fill()
        let text = """
            TARGET WINDOW (below the panel)

            A click landing here means the
            transparent pixel passed through.
            """ as NSString
        text.draw(
            in: bounds.insetBy(dx: 16, dy: 16),
            withAttributes: [
                .font: NSFont.monospacedSystemFont(ofSize: 12, weight: .regular),
                .foregroundColor: NSColor.systemGreen,
            ])
    }

    override func mouseDown(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        print("  PASSED THROUGH  mode=\(currentMode.rawValue)  target got click at (\(Int(p.x)), \(Int(p.y)))")
        fflush(stdout)
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    var panel: NSPanel!
    var view: CatView!
    var target: NSWindow!
    var monitor: Any?
    var stdinSource: DispatchSourceRead?
    var pollTimer: Timer?

    func applicationDidFinishLaunching(_ note: Notification) {
        let screen = NSScreen.main!
        // visibleFrame, not frame — avoids the notch and the menu bar.
        let vf = screen.visibleFrame
        let origin = NSPoint(x: vf.midX - windowSide / 2, y: vf.midY - windowSide / 2)

        panel = NSPanel(
            contentRect: NSRect(origin: origin, size: NSSize(width: windowSide, height: windowSide)),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false)

        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.isMovableByWindowBackground = false
        panel.ignoresMouseEvents = false

        // Level 101. The Dock is at 20 — `.floating` (3) would put us behind it.
        panel.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.popUpMenuWindow)))

        // .stationary matters: any window with level != 0 defaults to .transient,
        // which means it blinks out during Mission Control.
        panel.collectionBehavior = [
            .canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle,
        ]

        // Opaque target, slightly larger and centred on the same point, ordered first
        // so the panel sits on top of it.
        let targetSide = windowSide + 80
        target = NSWindow(
            contentRect: NSRect(
                x: vf.midX - targetSide / 2, y: vf.midY - targetSide / 2,
                width: targetSide, height: targetSide),
            styleMask: [.titled, .closable],
            backing: .buffered,
            defer: false)
        target.title = "Target (click-through test)"
        target.contentView = TargetView()
        target.level = .normal
        target.orderFrontRegardless()

        view = CatView(frame: NSRect(x: 0, y: 0, width: windowSide, height: windowSide))
        panel.contentView = view
        panel.orderFrontRegardless()

        installModeSwitcher()
        installAlphaSampler()
        installPolledSampler()
        installRawClickLogger()
        if CommandLine.arguments.contains("--cycle") { installModeCycler() }

        print("""

        ┌─ S1 click-through spike ────────────────────────────────────────
        │ window level : \(panel.level.rawValue)  (Dock is 20)
        │ screen       : \(Int(screen.frame.width))x\(Int(screen.frame.height)) \
        scale \(screen.backingScaleFactor)  safeAreaTop \(Int(screen.safeAreaInsets.top))
        │ visibleFrame : \(Int(vf.width))x\(Int(vf.height)) at y=\(Int(vf.origin.y))
        ├─ modes ─────────────────────────────────────────────────────────
        │ type 1 + Enter → A  \(Mode.a.explanation)
        │ type 2 + Enter → B  \(Mode.b.explanation)
        │ type 3 + Enter → C  \(Mode.c.explanation)
        │ type q + Enter → quit
        ├─ test ──────────────────────────────────────────────────────────
        │ A dark TARGET window sits directly behind the pink disc.
        │
        │   click the PINK DISC      → want:  HIT ... DISC
        │   click a BLUE-EDGED CORNER → want:  PASSED THROUGH
        │
        │ If a corner click logs neither, the event was swallowed —
        │ that is the failure case, and it is worse than useless.
        │ Try all three modes.
        └─────────────────────────────────────────────────────────────────
        starting in mode \(currentMode.rawValue)

        """)
        fflush(stdout)
    }

    /// Mode switching over stdin, deliberately NOT a global key monitor:
    /// global keyboard monitors require the Accessibility permission, and the whole
    /// point of this spike is to find out what we can do with no permissions at all.
    /// Global *mouse* monitors (used below) do not require it.
    func installModeSwitcher() {
        // Without a tty, readLine() returns nil forever and the source spins.
        guard isatty(FileHandle.standardInput.fileDescriptor) == 1 else {
            print("(stdin is not a tty — mode switching disabled, staying in \(currentMode.rawValue))")
            fflush(stdout)
            return
        }
        let stdinSource = DispatchSource.makeReadSource(
            fileDescriptor: FileHandle.standardInput.fileDescriptor, queue: .main)
        stdinSource.setEventHandler {
            guard let line = readLine(strippingNewline: true) else { return }
            let next: Mode?
            switch line.trimmingCharacters(in: .whitespaces).uppercased() {
            case "1", "A": next = .a
            case "2", "B": next = .b
            case "3", "C": next = .c
            case "4", "D": next = .d
            case "Q": exit(0)
            default: next = nil
            }
            guard let mode = next else { return }
            currentMode = mode
            if mode != .c && mode != .d { self.panel.ignoresMouseEvents = false }
            self.view.needsDisplay = true
            print("→ mode \(mode.rawValue): \(mode.explanation)")
            fflush(stdout)
        }
        stdinSource.resume()
        self.stdinSource = stdinSource
    }

    /// Rotates A → B → C on a timer so all three modes can be exercised in one
    /// sitting without an interactive tty.
    func installModeCycler() {
        let order: [Mode] = [.c, .d]
        var idx = 0
        Timer.scheduledTimer(withTimeInterval: 14, repeats: true) { [weak self] _ in
            guard let self else { return }
            idx = (idx + 1) % order.count
            currentMode = order[idx]
            if currentMode != .c && currentMode != .d { self.panel.ignoresMouseEvents = false }
            self.view.needsDisplay = true
            print("\n══ MODE \(currentMode.rawValue) — \(currentMode.explanation) ══")
            print("   click the disc, then a transparent corner")
            fflush(stdout)
        }
    }

    /// Logs every click the panel's window receives, BEFORE view hit-testing decides
    /// anything. This is what distinguishes "the corner click was swallowed" from
    /// "no corner click was ever made" — the two look identical in the view logs.
    func installRawClickLogger() {
        NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown]) { [weak self] event in
            guard let self else { return event }
            let inPanel = event.window === self.panel
            let inTarget = event.window === self.target
            let where_: String
            if inPanel {
                let p = self.view.convert(event.locationInWindow, from: nil)
                let region = isOnCat(at: p, in: self.view.bounds) ? "disc" : "TRANSPARENT CORNER"
                where_ = "panel/\(region) at (\(Int(p.x)),\(Int(p.y)))"
            } else if inTarget {
                where_ = "target window"
            } else {
                where_ = "some other window"
            }
            print("  [raw] window-server delivered this click to: \(where_)")
            fflush(stdout)
            return event
        }

        // A global monitor sees clicks that went to *other processes* — i.e. clicks
        // that genuinely left our app. Requires no permission for mouse events.
        NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown]) { [weak self] _ in
            guard let self else { return }
            let m = NSEvent.mouseLocation
            guard self.panel.frame.contains(m) else { return }
            print("  [raw] click inside panel bounds went to ANOTHER PROCESS (passed through)")
            fflush(stdout)
        }
    }

    /// Mode C: event-driven alpha sampling — what Electron/Tauri pets ship.
    /// Measured at ~84% reliable: 3 of 19 boundary clicks leaked to the wrong target.
    func installAlphaSampler() {
        monitor = NSEvent.addGlobalMonitorForEvents(matching: [.mouseMoved, .leftMouseDragged]) {
            [weak self] _ in
            guard let self, currentMode == .c else { return }
            self.updatePassThrough(padding: 0)
        }
    }

    /// Mode D: poll the cursor at display rate and apply hysteresis.
    ///
    /// Polling bounds staleness to one tick regardless of whether the window server
    /// bothered to send us a move event; the padding means we are already interactive
    /// by the time the pointer arrives at the silhouette.
    func installPolledSampler() {
        let timer = Timer(timeInterval: 1.0 / 120.0, repeats: true) { [weak self] _ in
            guard let self, currentMode == .d else { return }
            self.updatePassThrough(padding: hysteresisPadding)
        }
        // .common so the poll keeps running during window drags and menu tracking.
        RunLoop.main.add(timer, forMode: .common)
        pollTimer = timer
    }

    /// Sets `ignoresMouseEvents` from the cursor position. `padding` grows the
    /// interactive region; pass 0 for the naive behaviour.
    func updatePassThrough(padding: CGFloat) {
        let mouse = NSEvent.mouseLocation
        let frame = panel.frame
        guard frame.insetBy(dx: -padding, dy: -padding).contains(mouse) else {
            if !panel.ignoresMouseEvents { panel.ignoresMouseEvents = true }
            return
        }
        let local = NSPoint(x: mouse.x - frame.origin.x, y: mouse.y - frame.origin.y)
        // Asymmetric: while already interactive, hold on until clearly outside.
        let effective = panel.ignoresMouseEvents ? padding : padding * 2
        let onCat = isOnCat(at: local, in: view.bounds, padding: effective)
        if panel.ignoresMouseEvents == onCat { panel.ignoresMouseEvents = !onCat }
    }
}

// --mode A|B|C so each mode can be exercised without an interactive tty.
if let i = CommandLine.arguments.firstIndex(of: "--mode"),
    i + 1 < CommandLine.arguments.count,
    let m = Mode(rawValue: CommandLine.arguments[i + 1].uppercased())
{
    currentMode = m
}

let app = NSApplication.shared
// .accessory = no Dock icon, no Cmd-Tab entry. Also required to float over fullscreen apps.
app.setActivationPolicy(.accessory)
let delegate = AppDelegate()
app.delegate = delegate
app.run()
