import AppKit

/// Two ways to make the cat carry a message: one that arrives at a time, and one
/// that stays until it is taken down.
///
/// Both go through the same bubble, so a note and a reminder can never overlap; the
/// reminder wins while it is up and the note comes back underneath it.
final class MessageModule: CatModule {
    let id = "message"

    private let bus: WellnessBus

    /// The minute the reminder last fired in, so a 120Hz tick cannot fire it 7200
    /// times during the minute it is due.
    private var firedOnDay: Int = -1

    private var demoFireAt: CFAbsoluteTime = .greatestFiniteMagnitude
    private var meowStart: CFAbsoluteTime = -1
    private let meowDuration: Double = 1.4
    private let showSeconds: Double = 8

    init(bus: WellnessBus) {
        self.bus = bus
    }

    /// `--demo-timers` cannot wait for a wall-clock minute to roll around.
    func armDemoReminder(after seconds: Double) {
        demoFireAt = CFAbsoluteTimeGetCurrent() + seconds
        if bus.settings.reminderText.isEmpty {
            bus.settings.reminderText = "Stand up and look out of a window."
        }
    }

    func update(_ ctx: TickContext) -> ModuleOutput {
        let now = CFAbsoluteTimeGetCurrent()

        if now >= demoFireAt {
            demoFireAt = .greatestFiniteMagnitude
            fire(bus.settings.reminderText, why: "demo")
        } else if bus.settings.reminderEnabled, !bus.busy {
            checkClock(now: now, secondsSinceKey: ctx.secondsSinceKey)
        }

        guard meowStart >= 0 else { return .none }
        let p = (now - meowStart) / meowDuration
        guard p < 1 else { meowStart = -1; return .none }

        // The meow: a head-forward lunge and a wobble. There is no open-mouth cell
        // in the rig and inventing one would need art in three themes, so the
        // gesture carries it.
        var out = ModuleOutput()
        let t = CGFloat(p)
        let envelope = (1 - t) * (1 - t)
        out.squash = 1 + envelope * 0.07 * sin(.pi * 4 * t)
        out.offset.y = -envelope * bus.atlas.wellness.bobHeight * 0.8 * sin(.pi * 2 * t)
        return out
    }

    private func checkClock(now: CFAbsoluteTime, secondsSinceKey: Double) {
        let parts = bus.settings.reminderTime.split(separator: ":")
        guard parts.count == 2, let hh = Int(parts[0]), let mm = Int(parts[1]) else { return }

        let cal = Calendar.current
        let date = Date()
        let c = cal.dateComponents([.hour, .minute, .dayOfYear, .year], from: date)
        guard let hour = c.hour, let minute = c.minute, let day = c.dayOfYear,
              let year = c.year else { return }
        let dayKey = year * 1000 + day
        guard hour == hh, minute == mm, firedOnDay != dayKey else { return }
        firedOnDay = dayKey

        if secondsSinceKey > bus.awaySeconds {
            // Marked as fired, then dropped: a reminder is about a moment, and
            // replaying it an hour later is worse than not having it.
            bus.log(String(format: "reminder SKIPPED, away %.0fs", secondsSinceKey))
            return
        }
        fire(bus.settings.reminderText, why: "clock \(bus.settings.reminderTime)")
    }

    private func fire(_ text: String, why: String) {
        let message = text.isEmpty ? "Reminder!" : text
        bus.bubble?.say(message, for: showSeconds)
        bus.chime()
        meowStart = CFAbsoluteTimeGetCurrent()
        bus.log("reminder (\(why)) \"\(message)\"")
    }

    // MARK: - pinned note

    func pin(_ text: String?) {
        bus.bubble?.pin(text)
        bus.log("note \(text == nil ? "cleared" : "pinned: \"\(text!)\"")")
    }

    func clearReminder() {
        bus.settings.reminderEnabled = false
        bus.settings.reminderTime = ""
    }

    // MARK: - dialogs

    func promptForReminder() {
        // An .accessory app has no menu bar focus of its own; without this the alert
        // opens behind whatever the user is working in.
        NSApp.activate(ignoringOtherApps: true)

        let alert = NSAlert()
        alert.messageText = "Scheduled reminder"
        alert.informativeText = "The cat will meow and hold this up at the time you set."
        alert.addButton(withTitle: "Save")
        alert.addButton(withTitle: "Cancel")

        let time = NSTextField(string: bus.settings.reminderTime.isEmpty
            ? Self.defaultTimeString() : bus.settings.reminderTime)
        time.placeholderString = "HH:MM (24-hour)"
        let body = NSTextField(string: bus.settings.reminderText)
        body.placeholderString = "Message"

        let stack = NSStackView(views: [labelled("Time", time), labelled("Message", body)])
        stack.orientation = .vertical
        stack.spacing = 8
        stack.frame = NSRect(x: 0, y: 0, width: 280, height: 64)
        alert.accessoryView = stack
        alert.window.initialFirstResponder = time

        guard alert.runModal() == .alertFirstButtonReturn else { return }
        guard Self.parse(time.stringValue) != nil else {
            warn("That time did not parse. Use HH:MM, 24-hour — for example 14:30.")
            return
        }
        bus.settings.reminderTime = Self.normalise(time.stringValue)
        bus.settings.reminderText = body.stringValue
        bus.settings.reminderEnabled = true
        firedOnDay = -1
    }

    func promptForNote() {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = "Pin a note"
        alert.informativeText = "It stays above the cat until you unpin it."
        alert.addButton(withTitle: "Pin")
        alert.addButton(withTitle: "Cancel")

        let field = NSTextField(string: bus.bubble?.hasPinnedNote == true
            ? bus.settings.pinnedNote : "")
        field.placeholderString = "Note"
        field.frame = NSRect(x: 0, y: 0, width: 280, height: 24)
        alert.accessoryView = field
        alert.window.initialFirstResponder = field

        guard alert.runModal() == .alertFirstButtonReturn else { return }
        pin(field.stringValue)
    }

    private func labelled(_ title: String, _ field: NSTextField) -> NSView {
        let label = NSTextField(labelWithString: title)
        label.setContentHuggingPriority(.required, for: .horizontal)
        label.alignment = .right
        label.setFrameSize(NSSize(width: 60, height: 20))
        let row = NSStackView(views: [label, field])
        row.orientation = .horizontal
        row.spacing = 8
        return row
    }

    private func warn(_ text: String) {
        let a = NSAlert()
        a.messageText = text
        a.alertStyle = .warning
        a.runModal()
    }

    private static func parse(_ s: String) -> (Int, Int)? {
        let parts = s.trimmingCharacters(in: .whitespaces).split(separator: ":")
        guard parts.count == 2, let h = Int(parts[0]), let m = Int(parts[1]),
              (0...23).contains(h), (0...59).contains(m) else { return nil }
        return (h, m)
    }

    private static func normalise(_ s: String) -> String {
        guard let (h, m) = parse(s) else { return s }
        return String(format: "%02d:%02d", h, m)
    }

    private static func defaultTimeString() -> String {
        let c = Calendar.current.dateComponents([.hour, .minute], from: Date())
        return String(format: "%02d:%02d", c.hour ?? 9, c.minute ?? 0)
    }
}
