import AppKit

/// Persisted settings for every wellness feature.
///
/// One place for the keys so a typo cannot silently create a second setting, and so
/// the menu and the modules provably read the same thing.
struct WellnessSettings {
    private let d = UserDefaults.standard

    // Minutes. 0 means off everywhere.
    static let stretchOptions = [0, 10, 15, 20, 30, 45, 60, 90, 120]
    static let hydrationOptions = [0, 30, 45, 60, 90]
    static let focusOptions = [15, 20, 25, 30, 45, 50]
    static let breakOptions = [3, 5, 10, 15]
    static let roundOptions = [1, 2, 3, 4, 6, 8]

    private func minutes(_ key: String, _ fallback: Int) -> Int {
        d.object(forKey: key) == nil ? fallback : d.integer(forKey: key)
    }

    var stretchMinutes: Int {
        get { minutes("wellness.stretchMinutes", 30) }
        nonmutating set { d.set(newValue, forKey: "wellness.stretchMinutes") }
    }
    /// Off by default: a hydration nudge nobody asked for is the fastest way to get
    /// a desktop pet quit on day one.
    var hydrationMinutes: Int {
        get { minutes("wellness.hydrationMinutes", 0) }
        nonmutating set { d.set(newValue, forKey: "wellness.hydrationMinutes") }
    }
    var focusMinutes: Int {
        get { minutes("wellness.focusMinutes", 25) }
        nonmutating set { d.set(newValue, forKey: "wellness.focusMinutes") }
    }
    var breakMinutes: Int {
        get { minutes("wellness.breakMinutes", 5) }
        nonmutating set { d.set(newValue, forKey: "wellness.breakMinutes") }
    }
    var rounds: Int {
        get { minutes("wellness.rounds", 4) }
        nonmutating set { d.set(newValue, forKey: "wellness.rounds") }
    }
    var reminderEnabled: Bool {
        get { d.bool(forKey: "wellness.reminderEnabled") }
        nonmutating set { d.set(newValue, forKey: "wellness.reminderEnabled") }
    }
    /// "HH:MM", 24-hour.
    var reminderTime: String {
        get { d.string(forKey: "wellness.reminderTime") ?? "" }
        nonmutating set { d.set(newValue, forKey: "wellness.reminderTime") }
    }
    var reminderText: String {
        get { d.string(forKey: "wellness.reminderText") ?? "" }
        nonmutating set { d.set(newValue, forKey: "wellness.reminderText") }
    }
    var pinnedNote: String {
        get { d.string(forKey: "wellness.pinnedNote") ?? "" }
        nonmutating set { d.set(newValue, forKey: "wellness.pinnedNote") }
    }
    var soundEnabled: Bool {
        get { d.bool(forKey: "wellness.soundEnabled") }
        nonmutating set { d.set(newValue, forKey: "wellness.soundEnabled") }
    }
}

/// The little that the wellness modules need to know about each other.
///
/// Passed by reference so no module has to import another's file, which is what
/// keeps "delete the file to remove the feature" true.
final class WellnessBus {
    let settings = WellnessSettings()
    let atlas: Atlas

    /// `--demo-timers`: every interval is compressed and forced on, so the whole
    /// sequence can be watched in a minute instead of two hours.
    let isDemo: Bool
    let launched = CFAbsoluteTimeGetCurrent()

    var bubble: BubbleModule!
    weak var stretch: StretchBreakModule?

    /// True while the stretch break owns the cat and the screen.
    var busy: Bool { stretch?.isRunning ?? false }

    init(atlas: Atlas, isDemo: Bool) {
        self.atlas = atlas
        self.isDemo = isDemo
    }

    /// A user setting in minutes, or the compressed demo value. `nil` means off.
    func interval(userMinutes: Int, demoSeconds: Double) -> Double? {
        if isDemo { return demoSeconds }
        guard userMinutes > 0 else { return nil }
        return Double(userMinutes) * 60
    }

    /// When a timer should first go off. In demo mode the first one is pulled well
    /// forward, so a 40-second run actually shows the sequence instead of waiting
    /// out a full compressed interval.
    func firstFire(demoDelay: Double, interval: Double) -> CFAbsoluteTime {
        isDemo ? launched + demoDelay : CFAbsoluteTimeGetCurrent() + interval
    }

    /// Seconds of keyboard silence past which a reminder is dropped rather than
    /// banked. Effectively disabled in demo mode, or an unattended demo run would
    /// skip everything it is meant to be showing.
    var awaySeconds: Double {
        isDemo ? .greatestFiniteMagnitude : atlas.wellness.awaySeconds
    }

    func log(_ message: String) {
        guard isDemo else { return }
        let t = CFAbsoluteTimeGetCurrent() - launched
        print(String(format: "[demo %6.2fs] %@", t, message))
        fflush(stdout)
    }

    func describe(_ r: NSRect) -> String {
        String(format: "%.0fx%.0f @ (%.0f, %.0f)", r.width, r.height, r.origin.x, r.origin.y)
    }

    func chime() {
        guard settings.soundEnabled else { return }
        NSSound(named: "Tink")?.play()
    }
}

/// Builds, registers and configures the wellness features.
///
/// Exists so `main.swift` gains one line rather than six — several agents work in
/// that file at once, and a feature that needs a paragraph there is a feature that
/// causes merge conflicts.
final class WellnessSuite {
    let bus: WellnessBus
    private let bubble: BubbleModule
    private let stretch: StretchBreakModule
    private let hydration: HydrationModule
    private let pomodoro: PomodoroModule
    private let messages: MessageModule

    init(atlas: Atlas, view: CatView, panel: NSPanel, registry: ModuleRegistry) {
        let demo = CommandLine.arguments.contains("--demo-timers")
        bus = WellnessBus(atlas: atlas, isDemo: demo)

        bubble = BubbleModule(atlas: atlas, view: view, bus: bus)
        bus.bubble = bubble
        stretch = StretchBreakModule(atlas: atlas, view: view, panel: panel, bus: bus)
        bus.stretch = stretch
        hydration = HydrationModule(bus: bus)
        pomodoro = PomodoroModule(atlas: atlas, view: view, bus: bus)
        messages = MessageModule(bus: bus)

        // Bubble last: it renders what the others asked for this frame.
        registry.register(stretch)
        registry.register(hydration)
        registry.register(pomodoro)
        registry.register(messages)
        registry.register(bubble)

        if demo {
            print("""
            --demo-timers: intervals compressed
              stretch break   5s, then every 45s
              hydration      14s, then every 30s
              pomodoro       10s focus / 6s break, 2 rounds, auto-started
              reminder       8s after launch
              away-skip      disabled for the demo
            """)
            fflush(stdout)
            bus.log("panel at launch  \(bus.describe(panel.frame))")
            pomodoro.start()
            messages.armDemoReminder(after: 8)
        }
    }

    /// The theme/scale reload in `main.swift` builds a fresh `CatView`; anything
    /// holding the old one would silently draw into a detached layer tree.
    func rebind(view: CatView) {
        bubble.rebind(view: view)
        stretch.rebind(view: view)
        pomodoro.rebind(view: view)
    }

    // MARK: - menu

    /// Appends a "Wellness" submenu. Follows the same shape as the existing Size and
    /// Cat menus: represented objects carry the value, checkmarks show the current one.
    func addMenuItems(to menu: NSMenu) {
        let root = NSMenuItem(title: "Wellness", action: nil, keyEquivalent: "")
        let sub = NSMenu()

        sub.addItem(intervalMenu(
            title: "Stretch break", options: WellnessSettings.stretchOptions,
            current: bus.settings.stretchMinutes, action: #selector(setStretch(_:))))
        sub.addItem(intervalMenu(
            title: "Hydration", options: WellnessSettings.hydrationOptions,
            current: bus.settings.hydrationMinutes, action: #selector(setHydration(_:))))

        let pom = NSMenuItem(title: "Pomodoro", action: nil, keyEquivalent: "")
        let pomMenu = NSMenu()
        add(to: pomMenu, title: pomodoro.isRunning ? "Pause" : "Start",
            action: #selector(togglePomodoro))
        add(to: pomMenu, title: "Reset", action: #selector(resetPomodoro))
        pomMenu.addItem(.separator())
        pomMenu.addItem(intervalMenu(
            title: "Focus", options: WellnessSettings.focusOptions,
            current: bus.settings.focusMinutes, action: #selector(setFocus(_:))))
        pomMenu.addItem(intervalMenu(
            title: "Break", options: WellnessSettings.breakOptions,
            current: bus.settings.breakMinutes, action: #selector(setBreak(_:))))
        pomMenu.addItem(intervalMenu(
            title: "Rounds", options: WellnessSettings.roundOptions,
            current: bus.settings.rounds, action: #selector(setRounds(_:)), unit: ""))
        pom.submenu = pomMenu
        sub.addItem(pom)

        sub.addItem(.separator())
        add(to: sub, title: "Stretch now", action: #selector(stretchNow))
        add(to: sub, title: "Set reminder…", action: #selector(setReminder))
        if bus.settings.reminderEnabled && !bus.settings.reminderTime.isEmpty {
            add(to: sub, title: "Clear reminder (\(bus.settings.reminderTime))",
                action: #selector(clearReminder))
        }
        add(to: sub, title: "Pin a note…", action: #selector(pinNote))
        if !bus.settings.pinnedNote.isEmpty {
            add(to: sub, title: "Unpin note", action: #selector(unpinNote))
        }
        let sound = add(to: sub, title: "Sound with reminders", action: #selector(toggleSound))
        sound.state = bus.settings.soundEnabled ? .on : .off

        root.submenu = sub
        menu.addItem(root)
    }

    @discardableResult
    private func add(to menu: NSMenu, title: String, action: Selector) -> NSMenuItem {
        let mi = NSMenuItem(title: title, action: action, keyEquivalent: "")
        mi.target = self
        menu.addItem(mi)
        return mi
    }

    private func intervalMenu(title: String, options: [Int], current: Int,
                              action: Selector, unit: String = " min") -> NSMenuItem {
        let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        let m = NSMenu()
        for v in options {
            let mi = NSMenuItem(
                title: v == 0 ? "Off" : "\(v)\(unit)", action: action, keyEquivalent: "")
            mi.target = self
            mi.representedObject = v
            mi.state = (v == current) ? .on : .off
            m.addItem(mi)
        }
        item.submenu = m
        // Show the current value on the parent row, so it reads without opening.
        item.title = current == 0 ? "\(title): Off" : "\(title): \(current)\(unit)"
        return item
    }

    private func value(_ sender: Any?) -> Int? { (sender as? NSMenuItem)?.representedObject as? Int }

    @objc private func setStretch(_ s: NSMenuItem) {
        guard let v = value(s) else { return }
        bus.settings.stretchMinutes = v
        stretch.settingsChanged()
    }
    @objc private func setHydration(_ s: NSMenuItem) {
        guard let v = value(s) else { return }
        bus.settings.hydrationMinutes = v
        hydration.settingsChanged()
    }
    @objc private func setFocus(_ s: NSMenuItem) {
        guard let v = value(s) else { return }
        bus.settings.focusMinutes = v
        pomodoro.settingsChanged()
    }
    @objc private func setBreak(_ s: NSMenuItem) {
        guard let v = value(s) else { return }
        bus.settings.breakMinutes = v
        pomodoro.settingsChanged()
    }
    @objc private func setRounds(_ s: NSMenuItem) {
        guard let v = value(s) else { return }
        bus.settings.rounds = v
        pomodoro.settingsChanged()
    }

    @objc private func stretchNow() { stretch.trigger(reason: "menu") }
    @objc private func togglePomodoro() { pomodoro.isRunning ? pomodoro.pause() : pomodoro.start() }
    @objc private func resetPomodoro() { pomodoro.reset() }
    @objc private func setReminder() { messages.promptForReminder() }
    @objc private func clearReminder() { messages.clearReminder() }
    @objc private func pinNote() { messages.promptForNote() }
    @objc private func unpinNote() { messages.pin(nil) }
    @objc private func toggleSound() { bus.settings.soundEnabled.toggle() }
}
