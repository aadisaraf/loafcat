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
    private var tray: NSStatusItem?   // created once; see buildTray

    private var lastTick = CFAbsoluteTimeGetCurrent()
    private var displayTimer: Timer?

    /// Typing rate over a sliding window, for kneading and overheat.
    private var keyStamps: [CFAbsoluteTime] = []
    private var lastKeyCount: UInt32 = 0
    private var lastScrollCount: UInt32 = 0
    private let keyWindow: CFAbsoluteTime = 1.5

    /// Features live here, one file each. See CatModule.swift.
    let modules = ModuleRegistry()
    private var wellness: WellnessSuite?
    private var updater: Updater?

    /// Smoothed cursor velocity, in logical px/sec. Raw frame-to-frame deltas are
    /// far too noisy for a velocity threshold to be usable.
    private var smoothedVelocity = CGPoint.zero
    private var lastCursor: CGPoint?

    /// Integer only -- a fractional scale turns pixel art to mush and cannot be
    /// fixed downstream. 2x is ~96pt, which is the size desktop pets settle on.
    private var renderScale: CGFloat = UserDefaults.standard.object(forKey: "scale")
        .map { CGFloat($0 as? Double ?? 2) } ?? 2
    private var themeName: String = UserDefaults.standard.string(forKey: "theme") ?? "mono"

    /// The app's on switch.
    ///
    /// Quitting is not the same thing as turning the cat off: quitting also takes
    /// away the menu bar item, which is the only way back in. This is the off
    /// switch that leaves a way to switch it on again — and it really is off, not
    /// merely hidden. The tick stops, so nothing animates, no wellness timer fires
    /// and no window is resized behind your back.
    private var catVisible: Bool =
        UserDefaults.standard.object(forKey: "showCat") as? Bool ?? true

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

        // Larger than the cat: the atlas asks for a transparent margin so a speech
        // bubble has somewhere to live. See CatView.
        let size = CatView.panelSize(atlas: atlas, scale: renderScale)
        let screen = NSScreen.main!
        // visibleFrame, never frame: this display reports safeAreaInsets.top = 33
        // (the notch), and .frame would let the cat sit under it.
        let vf = screen.visibleFrame
        let origin = NSPoint(x: vf.midX - size.width / 2, y: vf.origin.y + 120)

        panel = NSPanel(
            contentRect: NSRect(origin: origin, size: size),
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
        if catVisible { panel.orderFrontRegardless() }

        buildTray()
        MainMenu.install(target: self, settings: #selector(openSettings), quit: #selector(quit))
        DockPresence.apply()
        lastKeyCount = InputTelemetry.keyCount()
        lastScrollCount = InputTelemetry.scrollCount()
        registerModules()

        // Announced through the menu bar rather than the cat: an update is news about
        // the app, not something the cat did, and the bubble is the cat's voice.
        updater = Updater(
            announce: { [weak self] message in
                self?.notifyAboutUpdate(message)
            },
            // Terminate first, relaunch second, and never the other way round: `open`
            // on a bundle this process still owns is a request to activate the copy
            // already running, not to start a new one. So a detached shell waits for
            // this pid to go away and then opens the app — which applies the staged
            // update before it puts anything on screen, exactly as a manual restart
            // would. The path is the bundle's, and staging never moves the bundle.
            restart: { [weak self] in
                let path = Bundle.main.bundlePath
                let pid = ProcessInfo.processInfo.processIdentifier
                let sh = Process()
                sh.executableURL = URL(fileURLWithPath: "/bin/sh")
                sh.arguments = ["-c",
                    "while kill -0 \(pid) 2>/dev/null; do sleep 0.2; done; "
                    + "open -n \"\(path)\""]
                try? sh.run()
                _ = self
                NSApp.terminate(nil)
            })
        updater?.start()

        // One 120Hz timer drives everything: cursor tracking, click-through
        // hit-testing, typing rate. Polling rather than event monitors is what
        // fixed the click-through race in spike S1 (88% -> 97%).
        let t = Timer(timeInterval: 1.0 / 120.0, repeats: true) { [weak self] _ in
            self?.tick()
        }
        RunLoop.main.add(t, forMode: .common)
        displayTimer = t

        // `--settings` opens the window straight away, and so does the very first
        // launch. Starting an accessory app looks like nothing happening: no Dock
        // bounce, no window, just one more small icon in a menu bar that already has
        // fifteen. Showing settings once is how the first run says "I am running,
        // here is where I live, here is how to turn me off."
        let firstRun = !UserDefaults.standard.bool(forKey: "hasLaunched")
        UserDefaults.standard.set(true, forKey: "hasLaunched")
        if firstRun || CommandLine.arguments.contains("--settings") { openSettings() }

        print("""
        loafcat running
          theme   \(themeName) -- \(atlas.parts.count) parts, \(Int(atlas.canvas))px @\(Int(renderScale))x
          window  level \(panel.level.rawValue), \(Int(size.width))x\(Int(size.height)) at \
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
        modules.register(DragModule(panel: panel, registry: modules))
        modules.register(TypingModule())
        modules.register(HuntModule())
        modules.register(PettingModule())
        modules.register(ScrollModule())
        modules.register(AgentModule.shared)
        wellness = WellnessSuite(
            atlas: atlas, view: view, panel: panel, registry: modules)
    }

    // Asset lookup lives in Branding.swift, so the settings window and the icon
    // loader resolve exactly the same paths this does.
    private static func themeDir(_ name: String) -> URL { Assets.themeDir(name) }

    /// Creates the menu bar item. Exactly once, for the life of the process.
    ///
    /// Asking `NSStatusBar` for a second item and dropping the reference to the
    /// first does NOT swap them in place: the old item is destroyed and the new one
    /// is appended at the end of the bar. On a full menu bar -- and a notched
    /// display has far less room than it looks -- the re-added item lands past the
    /// edge and never comes back. That is why the cat kept vanishing after a theme
    /// or size change: `reload()` used to rebuild the whole item, when all it
    /// actually needed was a fresh menu.
    private func buildTray() {
        guard tray == nil else { return rebuildMenu() }
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        // Lets macOS remember where the user dragged it, across launches.
        item.autosaveName = "dev.loafcat.status"
        item.isVisible = true
        if let glyph = Branding.trayImage() {
            item.button?.image = glyph
            item.button?.title = ""
        } else {
            item.button?.title = "🐈"   // asset missing; a visible fallback beats none
        }
        tray = item
        rebuildMenu()
    }

    /// Rebuilds only the menu. Safe to call as often as needed — the item itself,
    /// and therefore its position in the bar, is untouched.
    private func rebuildMenu() {
        guard let tray else { return }
        // Quick actions only. Everything configurable lives in Settings -- a value
        // reachable from two places is a value that will eventually disagree with
        // itself, and a nested checkmark submenu was never a good way to pick a
        // number anyway.
        let menu = NSMenu()
        menu.addItem(withTitle: "loafcat", action: nil, keyEquivalent: "")
        menu.addItem(.separator())
        // The on switch, first, because it is the thing people come here for.
        menu.addItem(
            withTitle: catVisible ? "Turn the cat off" : "Turn the cat on",
            action: #selector(toggleCat), keyEquivalent: "")
            .target = self
        menu.addItem(
            withTitle: "Settings…", action: #selector(openSettings), keyEquivalent: ",")
            .target = self
        menu.addItem(
            withTitle: "Centre on screen", action: #selector(centre), keyEquivalent: "")
            .target = self

        menu.addItem(.separator())
        // Each module supplies its own items, targeted at itself. main.swift only
        // places them, so a feature's menu lives in the feature's file.
        wellness?.addMenuItems(to: menu)

        // Only once there is something to say. A permanently visible "you are up to
        // date" is one more line to read past every time the menu opens.
        if let updater, updater.stagedAndReady, let v = updater.availableVersion {
            menu.addItem(.separator())
            menu.addItem(withTitle: "loafcat \(v) starts next time you open it",
                         action: nil, keyEquivalent: "")
        }

        menu.addItem(.separator())
        menu.addItem(AgentModule.shared.statusMenuItem())

        menu.addItem(.separator())
        menu.addItem(withTitle: "Quit", action: #selector(quit), keyEquivalent: "q")
            .target = self
        tray.menu = menu
    }

    @objc private func openSettings() {
        SettingsWindowController.shared.show(host: self)
    }

    @objc private func toggleCat() { setCat(visible: !catVisible) }

    /// Clicking the Dock icon, or launching the app again while it is already
    /// running.
    ///
    /// Launching an app is a request for it to be on, so this turns the cat back
    /// on before anything else — that is what makes "open loafcat and it starts"
    /// literally true whether or not it was already running. Without any of this,
    /// launching a second time did nothing at all, which reads as the app being
    /// broken.
    func applicationShouldHandleReopen(_ app: NSApplication, hasVisibleWindows: Bool) -> Bool {
        if !catVisible {
            setCat(visible: true)
        } else {
            openSettings()
        }
        return true
    }

    @objc private func centre() {
        let vf = NSScreen.main!.visibleFrame
        let size = CatView.panelSize(atlas: atlas, scale: renderScale)
        panel.setFrameOrigin(
            NSPoint(x: vf.midX - size.width / 2, y: vf.midY - size.height / 2))
    }

    /// Rebuilds the view for a new theme or scale, keeping the cat where it stands.
    /// Anchored on the BOTTOM-CENTRE so a resize looks like the cat growing from
    /// the floor rather than teleporting.
    private func reload() {
        guard let newAtlas = try? Atlas.load(from: Self.themeDir(themeName)) else { return }
        atlas = newAtlas
        rig = Rig(atlas: atlas)

        let old = panel.frame
        let size = CatView.panelSize(atlas: atlas, scale: renderScale)
        view = CatView(atlas: atlas, rig: rig, scale: renderScale)
        panel.contentView = view
        panel.setFrame(
            NSRect(x: old.midX - size.width / 2, y: old.minY,
                   width: size.width, height: size.height),
            display: true)
        wellness?.rebind(view: view)
        rebuildMenu()   // NOT buildTray: replacing the status item loses its place
    }

    @objc private func quit() { NSApp.terminate(nil) }

    private func tick() {
        let now = CFAbsoluteTimeGetCurrent()
        let dt = CGFloat(min(now - lastTick, 0.1))
        lastTick = now
        // Off means off. `lastTick` is still advanced above so the first frame back
        // is a normal one rather than a several-second dt fired through the springs.
        guard catVisible else { return }

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
        // The panel's centre IS the cat's centre: the transparent bubble margin is
        // symmetric precisely so this stays true. `effectiveScale` folds in the
        // stretch break's magnification, without which tracking would saturate the
        // moment the cat grew.
        let centre = CGPoint(x: frame.midX, y: frame.midY)
        let unit = view.effectiveScale
        let cursor = CGPoint(
            x: (mouse.x - centre.x) / unit,
            // Flip: screen y is up, the rig thinks in y-down like the atlas.
            y: -(mouse.y - centre.y) / unit)

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
            scale: unit)

        let out = modules.update(ctx)
        rig.setSquash(out.squash)
        rig.update(dt: dt, cursor: cursor,
                   isBlinkSuppressed: modules.state == .sleeping)
        view.sync()
    }
}

/// The settings window drives the app through this and nothing else, so it never
/// holds a `Rig` or a `CatView` -- both are thrown away and rebuilt by `reload()`,
/// and anything that captured one would be pointing at a dead object the first time
/// somebody picked another cat.
extension CatController: SettingsHost {
    var currentTheme: String { themeName }
    var currentScale: CGFloat { renderScale }
    var wellnessSuite: WellnessSuite? { wellness }

    var updateStatus: String {
        guard let updater else { return "loafcat \(Branding.version)." }
        if updater.stagedAndReady, let v = updater.availableVersion {
            return "loafcat \(v) is ready, and starts the next time you open the app."
        }
        if let v = updater.availableVersion { return "loafcat \(v) is available." }
        return "loafcat \(Branding.version)."
    }

    var downloadPercent: Int { updater?.downloadPercent ?? -1 }

    func checkForUpdates(_ report: @escaping (String) -> Void) {
        guard let updater else { report("Updates are unavailable."); return }
        updater.check(quiet: false) { [weak self] in
            report(self?.updateStatus ?? "")
        }
    }

    /// A user notification would need permission this app has never asked for, and the
    /// speech bubble is the cat's voice rather than the app's. So the menu bar says it:
    /// the line appears in the menu, and rebuilding is what makes it show up.
    private func notifyAboutUpdate(_ message: String) {
        FileHandle.standardError.write(("loafcat: " + message + "\n").data(using: .utf8)!)
        rebuildMenu()
        SettingsWindowController.shared.refreshPanes()
    }

    func apply(theme: String) {
        themeName = theme
        UserDefaults.standard.set(theme, forKey: "theme")
        reload()
    }

    func apply(scale: CGFloat) {
        renderScale = scale
        UserDefaults.standard.set(Double(scale), forKey: "scale")
        reload()
    }

    func apply(dragFeel: DragFeel) {
        UserDefaults.standard.set(dragFeel.rawValue, forKey: "dragFeel")
        reload()   // modules re-read their tuning when the rig is rebuilt
    }

    func apply(stretchTempo: StretchTempo) {
        UserDefaults.standard.set(stretchTempo.rawValue, forKey: "stretchTempo")
        reload()
    }

    func apply(heatSensitivity: HeatSensitivity) {
        UserDefaults.standard.set(heatSensitivity.rawValue, forKey: "heatSensitivity")
        reload()
    }

    func centreCat() { centre() }

    var isCatVisible: Bool { catVisible }

    func setCat(visible: Bool) {
        catVisible = visible
        UserDefaults.standard.set(visible, forKey: "showCat")
        if visible {
            lastTick = CFAbsoluteTimeGetCurrent()
            panel.orderFrontRegardless()
        } else {
            panel.orderOut(nil)
            // Or the invisible panel keeps swallowing clicks in its own rectangle.
            panel.ignoresMouseEvents = true
        }
        rebuildMenu()
        SettingsWindowController.shared.refreshPanes()
    }
}

// Before anything is on screen: a staged update renames the installed bundle out of
// the way, moves the new one into place, and relaunches into it. See Updater.swift.
if Updater.applyStagedUpdate() { exit(0) }

let controller = CatController()
app.delegate = controller
app.run()
