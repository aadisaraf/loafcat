import AppKit

/// Draws whatever the cat has to say, above its head.
///
/// One module owns the bubble so there is exactly one of them on screen: without
/// that, a hydration nudge landing on top of a pinned note would produce two
/// overlapping bubbles and no way to tell which is which.
///
/// Registered LAST, so by the time it runs every other module has already said what
/// it wants for this frame.
final class BubbleModule: CatModule {
    let id = "bubble"

    private let atlas: Atlas
    private var view: CatView
    private let bus: WellnessBus

    private var transientText: String?
    private var transientUntil: CFAbsoluteTime = 0
    private var pinnedText: String?

    /// Set while the stretch break owns the screen — a bubble magnified 10x would
    /// be a wall of text.
    private var suppressed = false

    /// What is currently on the layer, so we only re-render when it changes.
    private var shown: String?

    /// Bubbles are laid out pixel by pixel, which is cheap but not free; the same
    /// hydration nudge recurs for the life of the process.
    private var cache: [String: SpeechBubble.Rendered] = [:]

    init(atlas: Atlas, view: CatView, bus: WellnessBus) {
        self.atlas = atlas
        self.view = view
        self.bus = bus
        let note = bus.settings.pinnedNote
        self.pinnedText = note.isEmpty ? nil : note
    }

    func rebind(view: CatView) {
        self.view = view
        cache.removeAll()
        shown = nil
    }

    /// Says something for a few seconds, replacing anything already transient.
    func say(_ text: String, for seconds: Double) {
        transientText = text
        transientUntil = CFAbsoluteTimeGetCurrent() + seconds
    }

    /// A note that stays until it is dismissed. `nil` clears it.
    func pin(_ text: String?) {
        let trimmed = text?.trimmingCharacters(in: .whitespacesAndNewlines)
        pinnedText = (trimmed?.isEmpty ?? true) ? nil : trimmed
        bus.settings.pinnedNote = pinnedText ?? ""
    }

    var hasPinnedNote: Bool { pinnedText != nil }

    func suppress(_ on: Bool) { suppressed = on }

    func update(_ ctx: TickContext) -> ModuleOutput {
        let now = CFAbsoluteTimeGetCurrent()
        if let _ = transientText, now >= transientUntil { transientText = nil }

        // A transient message wins: it was triggered by something happening now,
        // and the pinned note is by definition not urgent.
        let want = suppressed ? nil : (transientText ?? pinnedText)
        if want != shown {
            shown = want
            present(want)
        }
        return .none
    }

    /// Clicking the cat dismisses whatever it is holding up. The bubble itself is
    /// deliberately NOT clickable — it lives in the transparent margin, and making
    /// it interactive would put a click-swallowing rectangle over the user's work.
    ///
    /// Never consumes the click: dismissing a note is a side effect of petting the
    /// cat, not a reason to stop whoever else wanted the gesture. Dormant until
    /// something in `main.swift` routes mouse events into the registry.
    func mouseDown(at point: CGPoint) -> Bool {
        if transientText != nil { transientText = nil }
        else if pinnedText != nil { pin(nil) }
        return false
    }

    private func present(_ text: String?) {
        guard let bubble = atlas.bubble, let text, !text.isEmpty else {
            view.setAux("bubble", image: nil, atlasOrigin: .zero, size: .zero)
            return
        }
        let rendered: SpeechBubble.Rendered
        if let hit = cache[text] {
            rendered = hit
        } else if let made = bubble.render(text) {
            cache[text] = made
            rendered = made
        } else {
            return
        }
        guard let cg = rendered.image.cgImage() else { return }
        view.setAux(
            "bubble", image: cg,
            atlasOrigin: bubble.origin(for: rendered),
            size: CGSize(width: rendered.image.width, height: rendered.image.height))
    }
}
