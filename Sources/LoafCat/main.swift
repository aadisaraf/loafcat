import AppKit
import ApplicationServices

// NSApplication MUST be initialised before any NSEvent / CGEventSource call.
// Without it the process has no window-server connection and both the cursor
// position and the event counters silently freeze. (Found the hard way in spike S2.)
let app = NSApplication.shared
app.setActivationPolicy(.accessory)

/// Reads system-wide input activity without asking for a single permission.
///
/// `counterForEventType` returns an integer count of events since login — a rate,
/// never content. There is no code path here by which a keycode could reach us, so
/// content-blindness is structural rather than a promise the user has to trust.
/// Verified in spike S2 with Accessibility and Input Monitoring both denied.
///
/// Do NOT reach for a CGEventTap or uiohook. Their taps are active filters that can
/// read and suppress every keystroke system-wide, which is why apps using them show
/// the "control your computer" dialog most users bounce off.
struct InputTelemetry {
    // Only .combinedSessionState is safe: .hidSystemState and .privateState BLOCK
    // INDEFINITELY for an unprivileged process, with no error and no prompt.
    private static let state = CGEventSourceStateID.combinedSessionState

    static func keyCount() -> UInt32 {
        UInt32(CGEventSource.counterForEventType(state, eventType: .keyDown))
    }
    static func scrollCount() -> UInt32 {
        UInt32(CGEventSource.counterForEventType(state, eventType: .scrollWheel))
    }
    static func secondsSinceKey() -> Double {
        CGEventSource.secondsSinceLastEventType(state, eventType: .keyDown)
    }
}

final class CatController: NSObject, NSApplicationDelegate {
    private var panel: NSPanel!
    private var view: CatView!
    private var rig: Rig!
    private var atlas: Atlas!
    private var tray: NSStatusItem!   // module-scope lifetime, or the icon vanishes

    private var lastTick = CFAbsoluteTimeGetCurrent()
    private var displayTimer: Timer?

    /// Typing rate over a sliding window, for kneading and overheat.
    private var keyStamps: [CFAbsoluteTime] = []
    private var lastKeyCount: UInt32 = 0
    private var lastScrollCount: UInt32 = 0
    private let keyWindow: CFAbsoluteTime = 1.5

    /// Features live here, one file each. See CatModule.swift.
    let modules = ModuleRegistry()

    /// Smoothed cursor velocity, in logical px/sec. Raw frame-to-frame deltas are
    /// far too noisy for a velocity threshold to be usable.
    private var smoothedVelocity = CGPoint.zero
    private var lastCursor: CGPoint?

    /// Integer only -- a fractional scale turns pixel art to mush and cannot be
    /// fixed downstream. 2x is ~96pt, which is the size desktop pets settle on.
    private var renderScale: CGFloat = UserDefaults.standard.object(forKey: "scale")
        .map { CGFloat($0 as? Double ?? 2) } ?? 2
    private var themeName: String = UserDefaults.standard.string(forKey: "theme") ?? "mono"

    func applicationDidFinishLaunching(_ note: Notification) {
        do {
            atlas = try Atlas.load(from: Self.themeDir(themeName))
        } catch {
            FileHandle.standardError.write("\(error)\n".data(using: .utf8)!)
            NSApp.terminate(nil)
            return
        }

        rig = Rig(atlas: atlas)
        view = CatView(atlas: atlas, rig: rig, scale: renderScale)

        let side = atlas.canvas * renderScale
        let screen = NSScreen.main!
        // visibleFrame, never frame: this display reports safeAreaInsets.top = 33
        // (the notch), and .frame would let the cat sit under it.
        let vf = screen.visibleFrame
        let origin = NSPoint(x: vf.midX - side / 2, y: vf.origin.y + 120)

        panel = NSPanel(
            contentRect: NSRect(origin: origin, size: NSSize(width: side, height: side)),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered, defer: false)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.acceptsMouseMovedEvents = true

        // Level 101. The Dock is at 20, so `.floating` (3) would hide the cat behind it.
        panel.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.popUpMenuWindow)))
        // .stationary matters: any window with level != 0 defaults to .transient,
        // which makes it blink out during Mission Control.
        panel.collectionBehavior = [
            .canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle,
        ]
        panel.contentView = view
        panel.orderFrontRegardless()

        buildTray()
        lastKeyCount = InputTelemetry.keyCount()
        lastScrollCount = InputTelemetry.scrollCount()
        registerModules()

        // One 120Hz timer drives everything: cursor tracking, click-through
        // hit-testing, typing rate. Polling rather than event monitors is what
        // fixed the click-through race in spike S1 (88% -> 97%).
        let t = Timer(timeInterval: 1.0 / 120.0, repeats: true) { [weak self] _ in
            self?.tick()
        }
        RunLoop.main.add(t, forMode: .common)
        displayTimer = t

        print("""
        loafcat running
          theme   \(themeName) -- \(atlas.parts.count) parts, \(Int(atlas.canvas))px @\(Int(renderScale))x
          window  level \(panel.level.rawValue), \(Int(side))x\(Int(side)) at \
        (\(Int(origin.x)), \(Int(origin.y)))
          input   Accessibility=\(AXIsProcessTrusted() ? "granted" : "not needed") \
        InputMonitoring=\(CGPreflightListenEventAccess() ? "granted" : "not needed")
        Quit from the menu bar cat, or Ctrl+C.
        """)
        fflush(stdout)
    }

    /// Every feature is registered here and nowhere else. Adding one should be a
    /// single line plus a single new file under Modules/.
    private func registerModules() {
        // (modules are added on feature branches)
    }

    /// Assets live next to the executable in a packaged app, and at the repo root
    /// during development. Checking both keeps a plain `swiftc` build runnable.
    private static func assetsRoot() -> URL {
        let candidates = [
            Bundle.main.bundleURL.appendingPathComponent("Contents/Resources/assets"),
            URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
                .appendingPathComponent("assets"),
        ]
        for c in candidates where FileManager.default.fileExists(atPath: c.path) { return c }
        return candidates[1]
    }

    /// Every theme is a self-contained directory of parts plus a cat.json. Swapping
    /// themes is therefore a directory swap -- no code knows anything about a
    /// specific cat, which is what makes community themes possible later.
    private static func themeDir(_ name: String) -> URL {
        assetsRoot().appendingPathComponent("themes/\(name)")
    }

    private static func availableThemes() -> [String] {
        let root = assetsRoot().appendingPathComponent("themes")
        let names = (try? FileManager.default.contentsOfDirectory(atPath: root.path)) ?? []
        return names.filter { !$0.hasPrefix(".") }.sorted()
    }

    private func buildTray() {
        tray = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        tray.button?.title = "🐈"
        let menu = NSMenu()
        menu.addItem(withTitle: "loafcat", action: nil, keyEquivalent: "")
        menu.addItem(.separator())
        menu.addItem(
            withTitle: "Centre on screen", action: #selector(centre), keyEquivalent: "")
            .target = self

        let sizeItem = NSMenuItem(title: "Size", action: nil, keyEquivalent: "")
        let sizeMenu = NSMenu()
        for (label, s) in [("Small", 2.0), ("Medium", 3.0), ("Large", 4.0)] {
            let mi = NSMenuItem(
                title: label, action: #selector(setScale(_:)), keyEquivalent: "")
            mi.target = self
            mi.representedObject = s
            mi.state = (renderScale == CGFloat(s)) ? .on : .off
            sizeMenu.addItem(mi)
        }
        sizeItem.submenu = sizeMenu
        menu.addItem(sizeItem)

        let themeItem = NSMenuItem(title: "Cat", action: nil, keyEquivalent: "")
        let themeMenu = NSMenu()
        for name in Self.availableThemes() {
            let mi = NSMenuItem(
                title: name.capitalized, action: #selector(setTheme(_:)), keyEquivalent: "")
            mi.target = self
            mi.representedObject = name
            mi.state = (name == themeName) ? .on : .off
            themeMenu.addItem(mi)
        }
        themeItem.submenu = themeMenu
        menu.addItem(themeItem)

        menu.addItem(.separator())
        menu.addItem(withTitle: "Quit", action: #selector(quit), keyEquivalent: "q")
            .target = self
        tray.menu = menu
    }

    @objc private func centre() {
        let vf = NSScreen.main!.visibleFrame
        let side = atlas.canvas * renderScale
        panel.setFrameOrigin(NSPoint(x: vf.midX - side / 2, y: vf.midY - side / 2))
    }

    @objc private func setScale(_ sender: NSMenuItem) {
        guard let s = sender.representedObject as? Double else { return }
        renderScale = CGFloat(s)
        UserDefaults.standard.set(s, forKey: "scale")
        reload()
    }

    @objc private func setTheme(_ sender: NSMenuItem) {
        guard let n = sender.representedObject as? String else { return }
        themeName = n
        UserDefaults.standard.set(n, forKey: "theme")
        reload()
    }

    /// Rebuilds the view for a new theme or scale, keeping the cat where it stands.
    /// Anchored on the BOTTOM-CENTRE so a resize looks like the cat growing from
    /// the floor rather than teleporting.
    private func reload() {
        guard let newAtlas = try? Atlas.load(from: Self.themeDir(themeName)) else { return }
        atlas = newAtlas
        rig = Rig(atlas: atlas)

        let old = panel.frame
        let side = atlas.canvas * renderScale
        view = CatView(atlas: atlas, rig: rig, scale: renderScale)
        panel.contentView = view
        panel.setFrame(
            NSRect(x: old.midX - side / 2, y: old.minY, width: side, height: side),
            display: true)
        buildTray()
    }

    @objc private func quit() { NSApp.terminate(nil) }

    private func tick() {
        let now = CFAbsoluteTimeGetCurrent()
        let dt = CGFloat(min(now - lastTick, 0.1))
        lastTick = now

        let mouse = NSEvent.mouseLocation
        let frame = panel.frame

        // --- click-through ---------------------------------------------------
        // Toggle only on a boolean transition. Setting it every tick would churn
        // the window server 120 times a second for nothing.
        let local = CGPoint(x: mouse.x - frame.origin.x, y: mouse.y - frame.origin.y)
        let onCat = frame.insetBy(dx: -8, dy: -8).contains(mouse) && view.isOnCat(viewPoint: local)
        if panel.ignoresMouseEvents == onCat { panel.ignoresMouseEvents = !onCat }

        // --- typing rate ------------------------------------------------------
        let keys = InputTelemetry.keyCount()
        let delta = keys &- lastKeyCount
        lastKeyCount = keys
        if delta > 0 && delta < 100 {
            for _ in 0..<delta { keyStamps.append(now) }
        }
        keyStamps.removeAll { now - $0 > keyWindow }

        // --- cursor, relative to the cat's centre, in LOGICAL pixels ----------
        let centre = CGPoint(x: frame.midX, y: frame.midY)
        let cursor = CGPoint(
            x: (mouse.x - centre.x) / renderScale,
            // Flip: screen y is up, the rig thinks in y-down like the atlas.
            y: -(mouse.y - centre.y) / renderScale)

        // Exponential moving average. Raw per-frame deltas at 120Hz are far too
        // noisy for any velocity threshold to be usable against.
        if let prev = lastCursor, dt > 0 {
            let vx = (cursor.x - prev.x) / dt
            let vy = (cursor.y - prev.y) / dt
            let a: CGFloat = 0.25
            smoothedVelocity.x += (vx - smoothedVelocity.x) * a
            smoothedVelocity.y += (vy - smoothedVelocity.y) * a
        }
        lastCursor = cursor

        let scroll = InputTelemetry.scrollCount()
        let scrollDelta = scroll &- lastScrollCount
        lastScrollCount = scroll

        let ctx = TickContext(
            dt: dt,
            cursor: cursor,
            cursorVelocity: smoothedVelocity,
            cursorOnCat: onCat,
            keysPerSecond: CGFloat(Double(keyStamps.count) / keyWindow),
            // Guard against the counter wrapping or a huge burst after a stall.
            scrollDelta: scrollDelta < 1000 ? scrollDelta : UInt32(0),
            secondsSinceKey: InputTelemetry.secondsSinceKey(),
            frame: frame,
            scale: renderScale)

        let out = modules.update(ctx)
        rig.setSquash(out.squash)
        rig.update(dt: dt, cursor: cursor,
                   isBlinkSuppressed: modules.state == .sleeping)
        view.sync()
    }
}

let controller = CatController()
app.delegate = controller
app.run()
