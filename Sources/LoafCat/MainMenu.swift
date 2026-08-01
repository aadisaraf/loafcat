import AppKit

/// The application menu bar, and whether loafcat appears in the Dock at all.
///
/// A menu bar pet has no business taking a Dock slot, so the app ships as an
/// accessory: no Dock icon, no Cmd-Tab. The cost is that launching it looks like
/// nothing happened, and if the menu bar item is ever hard to find there is no
/// other way in. `showInDock` is the escape hatch — one checkbox that turns loafcat
/// into an ordinary app you can Cmd-Tab to and click in the Dock.
///
/// Switching policy at runtime works even though Info.plist says LSUIElement; the
/// plist only sets the starting value.
enum DockPresence {
    static var showInDock: Bool {
        get { UserDefaults.standard.bool(forKey: "showInDock") }
        set { UserDefaults.standard.set(newValue, forKey: "showInDock") }
    }

    static func apply() {
        let wanted: NSApplication.ActivationPolicy = showInDock ? .regular : .accessory
        guard NSApp.activationPolicy() != wanted else { return }
        NSApp.setActivationPolicy(wanted)
        // Becoming .regular mid-flight leaves the app without the menu bar until it
        // is activated once.
        if wanted == .regular { NSApp.activate(ignoringOtherApps: true) }
    }
}

enum MainMenu {
    /// Builds the menu bar.
    ///
    /// Installed whichever activation policy is in force. An accessory app never
    /// draws this, but its key equivalents are still dispatched through it, so
    /// Cmd-, and Cmd-Q work whenever the settings window is key — which they did
    /// not before, because there was no main menu at all.
    static func install(target: AnyObject, settings: Selector, quit: Selector) {
        let main = NSMenu()

        let appItem = NSMenuItem()
        let app = NSMenu()
        app.addItem(withTitle: "About loafcat", action: settings, keyEquivalent: "")
            .target = target
        app.addItem(.separator())
        let prefs = app.addItem(
            withTitle: "Settings…", action: settings, keyEquivalent: ",")
        prefs.target = target
        app.addItem(.separator())
        app.addItem(
            withTitle: "Hide loafcat", action: #selector(NSApplication.hide(_:)),
            keyEquivalent: "h")
        app.addItem(.separator())
        app.addItem(withTitle: "Quit loafcat", action: quit, keyEquivalent: "q")
            .target = target
        appItem.submenu = app
        main.addItem(appItem)

        let windowItem = NSMenuItem()
        let window = NSMenu(title: "Window")
        window.addItem(
            withTitle: "Close", action: #selector(NSWindow.performClose(_:)),
            keyEquivalent: "w")
        window.addItem(
            withTitle: "Minimise", action: #selector(NSWindow.performMiniaturize(_:)),
            keyEquivalent: "m")
        windowItem.submenu = window
        main.addItem(windowItem)
        NSApp.windowsMenu = window

        NSApp.mainMenu = main
    }
}
