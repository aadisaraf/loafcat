import AppKit

/// Every so often the cat swells to fill the middle of the screen and stretches,
/// which is an invitation to do the same.
///
/// The window has to grow because the cat does: the panel is sized to the sprite, so
/// magnifying inside a fixed frame would just clip. The frame, the magnification and
/// the pose all animate off one wall-clock phase machine rather than accumulated
/// `dt`, so a stalled frame or a lid close cannot leave the cat stuck at 20x.
final class StretchBreakModule: CatModule {
    let id = "stretch"

    private let atlas: Atlas
    private var view: CatView
    private let panel: NSPanel
    private let bus: WellnessBus

    private enum Phase { case waiting, growing, stretching, settling, shrinking }
    private var phase: Phase = .waiting
    private var phaseStart: CFAbsoluteTime = 0
    private var nextFire: CFAbsoluteTime = 0

    /// Restored verbatim when the break ends. Saved before anything moves.
    private var savedFrame = NSRect.zero
    private var targetFrame = NSRect.zero
    private var targetZoom: CGFloat = 1

    var isRunning: Bool { phase != .waiting }

    init(atlas: Atlas, view: CatView, panel: NSPanel, bus: WellnessBus) {
        self.atlas = atlas
        self.view = view
        self.panel = panel
        self.bus = bus
        settingsChanged()
    }

    func rebind(view: CatView) { self.view = view }

    /// Restarts the countdown. Called when the menu changes the interval, so a user
    /// picking "10 min" waits ten minutes rather than inheriting an old deadline.
    func settingsChanged() {
        guard let iv = interval else { nextFire = .greatestFiniteMagnitude; return }
        nextFire = bus.firstFire(demoDelay: 5, interval: iv)
    }

    private var interval: Double? {
        bus.interval(userMinutes: bus.settings.stretchMinutes, demoSeconds: bus.isDemo ? 45 : 0)
    }

    /// Starts a break now. Safe to call from a menu or from the pomodoro.
    func trigger(reason: String) {
        guard !isRunning else { return }
        begin(reason: reason)
    }

    func update(_ ctx: TickContext) -> ModuleOutput {
        let now = CFAbsoluteTimeGetCurrent()
        let w = atlas.wellness

        if phase == .waiting {
            guard let iv = interval, now >= nextFire else { return .none }
            if ctx.secondsSinceKey > bus.awaySeconds {
                // Skip rather than bank it. Coming back from lunch to six queued
                // stretch breaks firing in a row is worse than missing all six.
                nextFire = now + iv
                bus.log(String(format: "stretch SKIPPED, away %.0fs", ctx.secondsSinceKey))
                return .none
            }
            begin(reason: "timer")
            return claim
        }

        let elapsed = now - phaseStart
        switch phase {
        case .waiting:
            return .none

        case .growing:
            let p = min(elapsed / max(w.growDuration, 0.001), 1)
            let e = Self.easeInOut(CGFloat(p))
            panel.setFrame(Self.lerp(savedFrame, targetFrame, e), display: false)
            view.setZoom(1 + (targetZoom - 1) * e)
            if p >= 1 {
                panel.setFrame(targetFrame, display: false)
                view.setZoom(targetZoom)
                phase = .stretching
                phaseStart = now
                bus.log("stretch  full size \(bus.describe(panel.frame)) zoom \(targetZoom)x")
            }
            return claim

        case .stretching:
            let p = min(elapsed / max(w.stretchDuration, 0.001), 1)
            view.setTint(tint(at: p))
            if p >= 1 {
                phase = .settling
                phaseStart = now
                view.setTint(0)
                bus.log("stretch  pose done, holding \(w.restoreDelay)s")
            }
            var out = claim
            out.squash = Self.pose(at: CGFloat(p))
            // Rises onto its toes through the middle of the stretch. Fractional on
            // purpose: CatView rounds it to a whole logical pixel before scaling.
            out.offset.y = -sin(.pi * CGFloat(p)) * w.bobHeight
            return out

        case .settling:
            if elapsed >= w.restoreDelay {
                phase = .shrinking
                phaseStart = now
            }
            return claim

        case .shrinking:
            let p = min(elapsed / max(w.growDuration, 0.001), 1)
            let e = Self.easeInOut(CGFloat(p))
            panel.setFrame(Self.lerp(targetFrame, savedFrame, e), display: false)
            view.setZoom(targetZoom + (1 - targetZoom) * e)
            if p >= 1 { finish() }
            return claim
        }
    }

    // MARK: - phases

    private func begin(reason: String) {
        savedFrame = panel.frame
        let screen = Self.screenHolding(savedFrame)
        let vf = screen.visibleFrame

        // Integer logical scale only. A stretch that settles on 19.25x would make
        // every pixel of the cat a different size, which at this magnification is
        // not subtle. Snapping down keeps it just under the requested fraction.
        let cap = vf.height * atlas.wellness.screenFraction
        let target = max(view.scale, (cap / atlas.canvas).rounded(.down))
        targetZoom = target / view.scale
        let side = (atlas.canvas * target).rounded()
        targetFrame = NSRect(
            x: (vf.midX - side / 2).rounded(), y: (vf.midY - side / 2).rounded(),
            width: side, height: side)

        bus.bubble?.suppress(true)
        view.setAuxHidden(true)
        phase = .growing
        phaseStart = CFAbsoluteTimeGetCurrent()
        bus.log("stretch  BEGIN (\(reason)) saved \(bus.describe(savedFrame)) " +
                "-> \(bus.describe(targetFrame)) on \(Self.name(of: screen))")
    }

    private func finish() {
        // Snap, do not settle: the whole point of saving the frame is that the cat
        // ends up exactly where it was, not a rounding error away from it.
        panel.setFrame(savedFrame, display: true)
        view.setZoom(1)
        view.setTint(0)
        view.setAuxHidden(false)
        bus.bubble?.suppress(false)
        phase = .waiting
        if let iv = interval { nextFire = CFAbsoluteTimeGetCurrent() + iv }
        bus.log("stretch  END restored \(bus.describe(panel.frame)) " +
                "(match=\(panel.frame == savedFrame))")
    }

    /// Claims the frame. `.stretching` outranks everything but a drag, and
    /// `exclusive` stops the losers' squash and offset blending in underneath —
    /// nothing else gets a say while the cat is the size of a window.
    private var claim: ModuleOutput {
        var out = ModuleOutput()
        out.state = .stretching
        out.exclusive = true
        return out
    }

    // MARK: - curves

    /// The stretch arc: up onto the toes, hold, fold down, settle. The rig clamps to
    /// ±14%, so these are the extremes rather than suggestions.
    private static func pose(at p: CGFloat) -> CGFloat {
        if p < 0.22 { return 1.0 + 0.14 * easeInOut(p / 0.22) }
        if p < 0.55 { return 1.14 }
        if p < 0.78 { return 1.14 - 0.24 * easeInOut((p - 0.55) / 0.23) }
        return 0.90 + 0.10 * easeInOut((p - 0.78) / 0.22)
    }

    /// Ramps in, holds, then eases back from `tint_release_at` so the cat is its own
    /// colour again before it starts shrinking — the release is the cue that the
    /// break is ending, and it has to land before the window moves.
    private func tint(at p: Double) -> CGFloat {
        let w = atlas.wellness
        let release = min(max(w.tintReleaseAt, 0.05), 0.99)
        if p < 0.3 { return w.tintPeak * CGFloat(p / 0.3) }
        if p < release { return w.tintPeak }
        return w.tintPeak * CGFloat(1 - (p - release) / (1 - release))
    }

    private static func easeInOut(_ t: CGFloat) -> CGFloat {
        let x = min(max(t, 0), 1)
        return x < 0.5 ? 4 * x * x * x : 1 - pow(-2 * x + 2, 3) / 2
    }

    private static func lerp(_ a: NSRect, _ b: NSRect, _ t: CGFloat) -> NSRect {
        // Whole points: the container centres itself on the view's midpoint, and a
        // half-point frame would put that midpoint between two device pixels.
        NSRect(x: (a.origin.x + (b.origin.x - a.origin.x) * t).rounded(),
               y: (a.origin.y + (b.origin.y - a.origin.y) * t).rounded(),
               width: (a.width + (b.width - a.width) * t).rounded(),
               height: (a.height + (b.height - a.height) * t).rounded())
    }

    // MARK: - displays

    /// The display the CAT is on, by area of overlap — deliberately not
    /// `NSScreen.main`, which follows the key window or the cursor. An automatic
    /// break must never yank the cat onto whichever monitor the user happens to be
    /// pointing at; it would look like the app moved their window.
    private static func screenHolding(_ frame: NSRect) -> NSScreen {
        var best: NSScreen?
        var bestArea: CGFloat = -1
        for s in NSScreen.screens {
            let i = s.frame.intersection(frame)
            let area = i.isNull ? 0 : i.width * i.height
            if area > bestArea { bestArea = area; best = s }
        }
        return best ?? NSScreen.screens.first ?? NSScreen.main!
    }

    private static func name(of screen: NSScreen) -> String {
        let f = screen.frame
        return String(format: "%@ [%.0fx%.0f @ %.0f,%.0f]",
                      screen.localizedName, f.width, f.height, f.origin.x, f.origin.y)
    }
}
