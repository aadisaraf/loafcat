import AppKit
import ServiceManagement

/// What the settings window is allowed to reach back into.
///
/// A protocol rather than a reference to `CatController` for the same reason
/// modules get one: the window outlives the rig and the view, both of which are
/// thrown away and rebuilt on every theme or scale change. Anything that captured
/// them would be holding a dead object the first time somebody picked another cat.
protocol SettingsHost: AnyObject {
    var currentTheme: String { get }
    var currentScale: CGFloat { get }
    func apply(theme: String)
    func apply(scale: CGFloat)
    func apply(dragFeel: DragFeel)
    func centreCat()
    var wellnessSuite: WellnessSuite? { get }
}

/// The settings window: one place to change everything the cat can be told.
///
/// Menu bar submenus got the app this far, but picking a number out of a nested
/// checkmark list is a poor way to configure anything, and half the settings had no
/// menu representation at all. This is what makes loafcat feel like something you
/// install rather than something you run.
///
/// Every control applies immediately. No OK, no Apply — that is the platform
/// convention, and a cat that changes as you drag the slider is the whole point.
final class SettingsWindowController: NSWindowController {
    static let shared = SettingsWindowController()

    private weak var host: SettingsHost?
    private var tabs: NSTabViewController?

    private init() {
        // Placeholder; the real window is built on first show, once a host exists.
        super.init(window: nil)
    }

    required init?(coder: NSCoder) { fatalError("not from a nib") }

    func show(host: SettingsHost) {
        self.host = host
        if window == nil {
            build(host: host)
            window?.center()     // only the first time: moving it should stick
        }
        // An accessory app has no menu bar of its own and does not activate by
        // opening a window. Without this the settings window comes up *behind*
        // whatever the user was working in, which reads as the click doing nothing.
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
        // Panes refresh themselves in viewWillAppear. Doing it from here instead
        // would reach panes whose views have never loaded -- NSTabViewController
        // loads them lazily -- and every control in them is still nil.
    }

    private func build(host: SettingsHost) {
        let controller = NSTabViewController()
        controller.tabStyle = .toolbar
        for pane in [
            CatPane(host: host),
            WellnessPane(host: host),
            AgentPane(host: host),
            AboutPane(host: host),
        ] as [SettingsPane] {
            // Built explicitly rather than letting addChild synthesise one: the tab
            // label and icon have to be set before the pane's view loads, and
            // NSTabViewController loads those lazily.
            let item = NSTabViewItem(viewController: pane)
            item.label = pane.paneTitle
            item.image = NSImage(
                systemSymbolName: pane.paneSymbol, accessibilityDescription: pane.paneTitle)
            controller.addTabViewItem(item)
        }
        tabs = controller

        let w = NSWindow(contentViewController: controller)
        w.title = "loafcat Settings"
        w.styleMask = [.titled, .closable, .miniaturizable]
        // Closing must not deallocate it: `shared` would then be holding a freed
        // window and the second Cmd-, would crash.
        w.isReleasedWhenClosed = false
        window = w
    }
}

// MARK: - pane scaffolding

/// One tab. Panes build their layout once and re-read state in `refresh()`, which
/// runs every time the window is shown — settings can also be changed from the menu
/// bar and from the cat itself, so a pane that only read state at build time would
/// show stale values.
class SettingsPane: NSViewController {
    let host: SettingsHost
    let stack = NSStackView()

    init(host: SettingsHost) {
        self.host = host
        super.init(nibName: nil, bundle: nil)
        title = paneTitle
    }
    required init?(coder: NSCoder) { fatalError("not from a nib") }

    /// Declared as overridable properties rather than assigned in `populate()`,
    /// because the toolbar needs both before the pane's view is ever loaded.
    var paneTitle: String { "" }
    var paneSymbol: String { "questionmark" }

    /// Fixed rather than intrinsic. NSTabViewController resizes the window to each
    /// pane, and panes that disagree by a few points make the window jitter as you
    /// click between tabs.
    static let width: CGFloat = 460
    static let margin: CGFloat = 20

    override func loadView() {
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 14
        stack.edgeInsets = NSEdgeInsets(
            top: Self.margin, left: Self.margin,
            bottom: Self.margin, right: Self.margin)

        let container = NSView()
        container.addSubview(stack)
        stack.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            stack.topAnchor.constraint(equalTo: container.topAnchor),
            stack.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: container.trailingAnchor),
            stack.bottomAnchor.constraint(equalTo: container.bottomAnchor),
            container.widthAnchor.constraint(equalToConstant: Self.width),
        ])
        view = container
        populate()
        refresh()
        // NSTabViewController resizes the window to each tab's preferredContentSize.
        // Leave it unset and every pane gets the tallest pane's height, which leaves
        // About sitting under 200 points of nothing.
        preferredContentSize = container.fittingSize
    }

    /// Settings are also changed from the menu bar and by the cat itself (a
    /// pomodoro finishing, an agent connecting), so a pane that only read state
    /// when it was built would show stale values the second time you looked.
    override func viewWillAppear() {
        super.viewWillAppear()
        refresh()
    }

    /// Build the controls. Called once, from `loadView`.
    func populate() {}

    /// Read current state into the controls. Called whenever the pane appears.
    func refresh() {}

    // --- shared builders --------------------------------------------------
    // Small and blunt on purpose. A settings window is the one place in this
    // codebase where laying things out in code is cheaper than any abstraction.

    func heading(_ text: String) -> NSTextField {
        let label = NSTextField(labelWithString: text)
        label.font = .systemFont(ofSize: NSFont.systemFontSize, weight: .semibold)
        return label
    }

    func caption(_ text: String) -> NSTextField {
        let label = NSTextField(wrappingLabelWithString: text)
        label.font = .systemFont(ofSize: NSFont.smallSystemFontSize)
        label.textColor = .secondaryLabelColor
        label.preferredMaxLayoutWidth = Self.width - Self.margin * 2
        return label
    }

    func row(_ title: String, _ control: NSView) -> NSStackView {
        let label = NSTextField(labelWithString: title)
        label.alignment = .right
        label.setContentHuggingPriority(.defaultLow, for: .horizontal)
        label.widthAnchor.constraint(equalToConstant: 132).isActive = true
        let r = NSStackView(views: [label, control])
        r.orientation = .horizontal
        r.alignment = .firstBaseline
        r.spacing = 10
        return r
    }

    func divider() -> NSBox {
        let box = NSBox()
        box.boxType = .separator
        box.widthAnchor.constraint(equalToConstant: Self.width - Self.margin * 2).isActive = true
        return box
    }

    /// A minutes popup. `0` is rendered as "Off" everywhere, which is the
    /// convention `WellnessSettings` already uses for "this feature is disabled".
    func minutesPopup(_ options: [Int], unit: String = " min",
                      action: Selector) -> NSPopUpButton {
        let popup = NSPopUpButton()
        popup.target = self
        popup.action = action
        for v in options {
            popup.addItem(withTitle: v == 0 ? "Off" : "\(v)\(unit)")
            popup.lastItem?.representedObject = v
        }
        return popup
    }

    func select(_ popup: NSPopUpButton, value: Int) {
        for item in popup.itemArray where (item.representedObject as? Int) == value {
            popup.select(item)
            return
        }
    }

    func value(of sender: Any?) -> Int? {
        (sender as? NSPopUpButton)?.selectedItem?.representedObject as? Int
    }

    func button(_ title: String, _ action: Selector) -> NSButton {
        let b = NSButton(title: title, target: self, action: action)
        b.bezelStyle = .rounded
        return b
    }

    func checkbox(_ title: String, _ action: Selector) -> NSButton {
        NSButton(checkboxWithTitle: title, target: self, action: action)
    }

    func field(_ placeholder: String, width: CGFloat, action: Selector) -> NSTextField {
        let f = NSTextField()
        f.placeholderString = placeholder
        f.target = self
        f.action = action
        // Commit on focus loss as well as on Return. A user who types a time and
        // clicks away has told us what they want just as clearly.
        f.isContinuous = false
        f.widthAnchor.constraint(equalToConstant: width).isActive = true
        return f
    }
}

// MARK: - Cat

final class CatPane: SettingsPane {
    private var themeButtons: [NSButton] = []
    private let size = NSSegmentedControl()
    private let feel = NSSegmentedControl()
    private var login: NSButton!
    private var loginNote: NSTextField!

    private static let sizes: [(String, CGFloat)] = [("Small", 2), ("Medium", 3), ("Large", 4)]

    override var paneTitle: String { "Cat" }
    override var paneSymbol: String { "pawprint" }

    override func populate() {
        stack.addArrangedSubview(heading("Cat"))
        let themeRow = NSStackView()
        themeRow.orientation = .horizontal
        themeRow.spacing = 12
        for name in Assets.themes() {
            let b = NSButton()
            b.setButtonType(.pushOnPushOff)
            b.bezelStyle = .flexiblePush
            b.imagePosition = .imageAbove
            b.title = name.capitalized
            // 3x of a 48px canvas, then displayed at 48pt: a whole-number scale, so
            // the thumbnail stays as crisp as the cat on the desktop.
            if let thumb = ThemeThumbnail.image(theme: name, scale: 3) {
                thumb.size = NSSize(width: 52, height: 52)
                b.image = thumb
            }
            b.target = self
            b.action = #selector(pickTheme(_:))
            b.identifier = NSUserInterfaceItemIdentifier(name)
            themeButtons.append(b)
            themeRow.addArrangedSubview(b)
        }
        stack.addArrangedSubview(themeRow)
        stack.addArrangedSubview(caption(
            "Themes are directories under assets/themes. Drop one in and it appears here."))

        stack.addArrangedSubview(divider())
        stack.addArrangedSubview(heading("Size and feel"))

        size.segmentCount = Self.sizes.count
        for (i, s) in Self.sizes.enumerated() { size.setLabel(s.0, forSegment: i) }
        size.target = self
        size.action = #selector(pickSize)
        stack.addArrangedSubview(row("Size", size))

        feel.segmentCount = DragFeel.allCases.count
        for (i, f) in DragFeel.allCases.enumerated() { feel.setLabel(f.label, forSegment: i) }
        feel.target = self
        feel.action = #selector(pickFeel)
        stack.addArrangedSubview(row("Drag", feel))
        stack.addArrangedSubview(caption(
            "How far the cat stretches when you pick it up and pull. Subtle barely "
            + "droops; springy snaps back hardest."))

        stack.addArrangedSubview(divider())
        stack.addArrangedSubview(heading("Startup"))
        login = checkbox("Open loafcat at login", #selector(toggleLogin))
        stack.addArrangedSubview(login)
        loginNote = caption("")
        stack.addArrangedSubview(loginNote)

        stack.addArrangedSubview(divider())
        stack.addArrangedSubview(button("Centre on screen", #selector(centre)))
    }

    override func refresh() {
        for b in themeButtons {
            b.state = (b.identifier?.rawValue == host.currentTheme) ? .on : .off
        }
        size.selectedSegment =
            Self.sizes.firstIndex { $0.1 == host.currentScale } ?? 0
        feel.selectedSegment =
            DragFeel.allCases.firstIndex(of: DragFeel.current) ?? 0
        refreshLogin()
    }

    private func refreshLogin() {
        guard #available(macOS 13.0, *) else {
            login.isEnabled = false
            loginNote.stringValue = "Needs macOS 13 or later."
            return
        }
        switch SMAppService.mainApp.status {
        case .enabled:
            login.state = .on
            loginNote.stringValue = ""
        case .requiresApproval:
            login.state = .on
            loginNote.stringValue =
                "Waiting for approval in System Settings › General › Login Items."
        default:
            login.state = .off
            loginNote.stringValue = ""
        }
    }

    @objc private func pickTheme(_ sender: NSButton) {
        guard let name = sender.identifier?.rawValue else { return }
        host.apply(theme: name)
        refresh()               // reload() rebuilds the menu; keep the buttons in step
    }

    @objc private func pickSize() {
        host.apply(scale: Self.sizes[max(size.selectedSegment, 0)].1)
    }

    @objc private func pickFeel() {
        host.apply(dragFeel: DragFeel.allCases[max(feel.selectedSegment, 0)])
    }

    @objc private func centre() { host.centreCat() }

    @objc private func toggleLogin() {
        guard #available(macOS 13.0, *) else { return }
        do {
            // SMAppService registers the bundle by its code signature. An ad-hoc
            // signed build run from a build directory can legitimately fail here,
            // so the error is shown rather than swallowed — a checkbox that
            // silently un-ticks itself is the worst possible outcome.
            if login.state == .on {
                try SMAppService.mainApp.register()
            } else {
                try SMAppService.mainApp.unregister()
            }
            loginNote.stringValue = ""
        } catch {
            loginNote.stringValue = "Could not change this: \(error.localizedDescription)"
        }
        refreshLogin()
    }
}

// MARK: - Wellness

final class WellnessPane: SettingsPane {
    private var stretchPopup: NSPopUpButton!
    private var hydrationPopup: NSPopUpButton!
    private var focusPopup: NSPopUpButton!
    private var breakPopup: NSPopUpButton!
    private var roundsPopup: NSPopUpButton!
    private var pomodoroButton: NSButton!
    private var sound: NSButton!
    private var reminderOn: NSButton!
    private var reminderTime: NSTextField!
    private var reminderText: NSTextField!
    private var note: NSTextField!

    private var suite: WellnessSuite? { host.wellnessSuite }

    override var paneTitle: String { "Wellness" }
    override var paneSymbol: String { "heart" }

    override func populate() {
        stack.addArrangedSubview(heading("Breaks"))
        stretchPopup = minutesPopup(
            WellnessSettings.stretchOptions, action: #selector(changeIntervals))
        hydrationPopup = minutesPopup(
            WellnessSettings.hydrationOptions, action: #selector(changeIntervals))
        stack.addArrangedSubview(row("Stretch break", stretchPopup))
        stack.addArrangedSubview(row("Hydration", hydrationPopup))
        stack.addArrangedSubview(button("Stretch now", #selector(stretchNow)))

        stack.addArrangedSubview(divider())
        stack.addArrangedSubview(heading("Pomodoro"))
        focusPopup = minutesPopup(
            WellnessSettings.focusOptions, action: #selector(changeIntervals))
        breakPopup = minutesPopup(
            WellnessSettings.breakOptions, action: #selector(changeIntervals))
        roundsPopup = minutesPopup(
            WellnessSettings.roundOptions, unit: "", action: #selector(changeIntervals))
        stack.addArrangedSubview(row("Focus", focusPopup))
        stack.addArrangedSubview(row("Break", breakPopup))
        stack.addArrangedSubview(row("Rounds", roundsPopup))
        pomodoroButton = button("Start", #selector(togglePomodoro))
        let controls = NSStackView(views: [
            pomodoroButton, button("Reset", #selector(resetPomodoro)),
        ])
        controls.orientation = .horizontal
        controls.spacing = 8
        stack.addArrangedSubview(controls)

        stack.addArrangedSubview(divider())
        stack.addArrangedSubview(heading("Reminders"))
        reminderOn = checkbox("Remind me daily at", #selector(commitReminder))
        reminderTime = field("09:30", width: 70, action: #selector(commitReminder))
        let when = NSStackView(views: [reminderOn, reminderTime])
        when.orientation = .horizontal
        when.spacing = 8
        stack.addArrangedSubview(when)
        reminderText = field("Stand up and look out of a window",
                             width: Self.width - Self.margin * 2,
                             action: #selector(commitReminder))
        stack.addArrangedSubview(reminderText)
        stack.addArrangedSubview(caption(
            "24-hour time. Skipped rather than banked if you are away from the "
            + "keyboard when it comes due — a reminder is about a moment."))

        sound = checkbox("Play a sound with reminders", #selector(toggleSound))
        stack.addArrangedSubview(sound)

        stack.addArrangedSubview(divider())
        stack.addArrangedSubview(heading("Pinned note"))
        note = field("Something the cat should keep holding",
                     width: Self.width - Self.margin * 2, action: #selector(commitNote))
        stack.addArrangedSubview(note)
        stack.addArrangedSubview(caption(
            "Shown in the cat's speech bubble until you clear it. Leave empty to unpin."))
    }

    override func refresh() {
        guard let s = suite?.settings else { return }
        select(stretchPopup, value: s.stretchMinutes)
        select(hydrationPopup, value: s.hydrationMinutes)
        select(focusPopup, value: s.focusMinutes)
        select(breakPopup, value: s.breakMinutes)
        select(roundsPopup, value: s.rounds)
        pomodoroButton.title = (suite?.pomodoroRunning ?? false) ? "Pause" : "Start"
        sound.state = s.soundEnabled ? .on : .off
        reminderOn.state = s.reminderEnabled && !s.reminderTime.isEmpty ? .on : .off
        reminderTime.stringValue =
            s.reminderTime.isEmpty ? MessageModule.defaultTimeString() : s.reminderTime
        reminderText.stringValue = s.reminderText
        note.stringValue = s.pinnedNote
    }

    @objc private func changeIntervals(_ sender: Any?) {
        guard let s = suite?.settings else { return }
        if let v = value(of: stretchPopup) { s.stretchMinutes = v }
        if let v = value(of: hydrationPopup) { s.hydrationMinutes = v }
        if let v = value(of: focusPopup) { s.focusMinutes = v }
        if let v = value(of: breakPopup) { s.breakMinutes = v }
        if let v = value(of: roundsPopup) { s.rounds = v }
        suite?.settingsChanged()
    }

    @objc private func stretchNow() { suite?.stretchNow() }

    @objc private func togglePomodoro() {
        suite?.togglePomodoro()
        pomodoroButton.title = (suite?.pomodoroRunning ?? false) ? "Pause" : "Start"
    }

    @objc private func resetPomodoro() {
        suite?.resetPomodoro()
        refresh()
    }

    @objc private func toggleSound() {
        suite?.settings.soundEnabled = sound.state == .on
    }

    @objc private func commitReminder() {
        guard let suite else { return }
        guard reminderOn.state == .on else {
            suite.clearReminder()
            return
        }
        if suite.setReminder(time: reminderTime.stringValue, text: reminderText.stringValue) {
            reminderTime.stringValue = suite.reminderTime
            reminderTime.textColor = .labelColor
        } else {
            // Red rather than an alert: the field is right there, and a modal for a
            // typo is the kind of thing that makes people stop using a settings panel.
            reminderTime.textColor = .systemRed
        }
    }

    @objc private func commitNote() {
        suite?.pinNote(note.stringValue)
    }
}

// MARK: - Claude Code

final class AgentPane: SettingsPane {
    private var status: NSTextField!
    private var state: NSTextField!
    private var connectButton: NSButton!
    private var disconnectButton: NSButton!

    override var paneTitle: String { "Claude Code" }
    override var paneSymbol: String { "terminal" }

    override func populate() {
        stack.addArrangedSubview(heading("Claude Code"))
        stack.addArrangedSubview(caption(
            "The cat can show what Claude Code is doing: thinking while a request "
            + "is running, a hop when it finishes, an alert when it needs you."))

        state = NSTextField(labelWithString: "")
        state.font = .systemFont(ofSize: NSFont.systemFontSize, weight: .medium)
        stack.addArrangedSubview(state)

        connectButton = button("Connect to Claude Code", #selector(connect))
        disconnectButton = button("Disconnect", #selector(disconnect))
        let buttons = NSStackView(views: [connectButton, disconnectButton])
        buttons.orientation = .horizontal
        buttons.spacing = 8
        stack.addArrangedSubview(buttons)

        stack.addArrangedSubview(divider())
        status = NSTextField(labelWithString: "")
        status.font = .monospacedSystemFont(ofSize: NSFont.smallSystemFontSize, weight: .regular)
        status.textColor = .secondaryLabelColor
        stack.addArrangedSubview(status)

        stack.addArrangedSubview(caption(
            "Connecting adds hook entries to ~/.claude/settings.json and copies the "
            + "hook script to ~/.loafcat/. Your previous settings file is backed up "
            + "alongside it first.\n\n"
            + "Every hook is asynchronous, carries a short timeout and exits zero "
            + "whatever happens, so it cannot slow a Claude Code session down — not "
            + "even with loafcat quit. Disconnecting removes only loafcat's entries "
            + "and leaves anyone else's alone."))

        // The menu bar can also connect, and the user can edit settings.json by
        // hand; either way this pane has to notice.
        NotificationCenter.default.addObserver(
            self, selector: #selector(refreshFromNotification),
            name: AgentModule.connectionChanged, object: nil)
    }

    override func refresh() {
        let agent = AgentModule.shared
        let connected = agent.isConnected
        state.stringValue = connected
            ? "Connected — \(agent.hookCount) hooks registered."
            : "Not connected."
        state.textColor = connected ? .systemGreen : .secondaryLabelColor
        connectButton.isEnabled = !connected
        disconnectButton.isEnabled = connected
        status.stringValue = agent.listenerStatus
    }

    @objc private func refreshFromNotification() { refresh() }
    @objc private func connect() { AgentModule.shared.connect() }
    @objc private func disconnect() { AgentModule.shared.disconnect() }

    deinit { NotificationCenter.default.removeObserver(self) }
}

// MARK: - About

final class AboutPane: SettingsPane {
    private static let repo = "https://github.com/aadisaraf/loafcat"

    override var paneTitle: String { "About" }
    override var paneSymbol: String { "info.circle" }

    override func populate() {
        let icon = NSImageView(image: Branding.appIcon())
        icon.widthAnchor.constraint(equalToConstant: 96).isActive = true
        icon.heightAnchor.constraint(equalToConstant: 96).isActive = true

        let name = NSTextField(labelWithString: "loafcat")
        name.font = .systemFont(ofSize: 22, weight: .semibold)
        let version = NSTextField(labelWithString: "Version \(Branding.version) · MIT licensed")
        version.textColor = .secondaryLabelColor

        let text = NSStackView(views: [name, version])
        text.orientation = .vertical
        text.alignment = .leading
        text.spacing = 2

        let header = NSStackView(views: [icon, text])
        header.orientation = .horizontal
        header.alignment = .centerY
        header.spacing = 16
        stack.addArrangedSubview(header)

        stack.addArrangedSubview(divider())
        stack.addArrangedSubview(heading("This app asks for no permissions"))
        stack.addArrangedSubview(caption(
            "No Accessibility prompt, no Input Monitoring, no Screen Recording. "
            + "Typing reactions come from a system counter that returns how many "
            + "keys have been pressed — an integer. There is no code path by which "
            + "a keystroke could reach loafcat, so being unable to read what you "
            + "type is structural rather than a promise.\n\n"
            + "A build check blocks any code that would change that."))

        stack.addArrangedSubview(divider())
        stack.addArrangedSubview(heading("Art"))
        stack.addArrangedSubview(caption(
            "Every pixel in this app is generated by tools/generate_art.py — this "
            + "icon too, composited from the same parts by tools/generate_icon.py. "
            + "Nothing is traced, sampled or derived from any existing sprite."))

        stack.addArrangedSubview(divider())
        stack.addArrangedSubview(button("Open the repository", #selector(openRepo)))
    }

    @objc private func openRepo() {
        guard let url = URL(string: Self.repo) else { return }
        NSWorkspace.shared.open(url)
    }
}
