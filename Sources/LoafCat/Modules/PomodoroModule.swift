import AppKit

/// Focus blocks with a countdown the cat carries beside it.
///
/// The plate is drawn from the same 9-slice and the same pixel font as the speech
/// bubble, which is the only reason a live-updating timer can sit next to pixel art
/// without looking pasted on: it is made of the same pixels, at the same 1x, and
/// magnified by the same integer factor.
final class PomodoroModule: CatModule {
    let id = "pomodoro"

    private let atlas: Atlas
    private var view: CatView
    private let bus: WellnessBus

    enum Mode { case stopped, focus, rest, done }
    private(set) var mode: Mode = .stopped
    private var running = false
    private var remaining: Double = 0
    private var lastTick = CFAbsoluteTimeGetCurrent()
    private var round = 0

    private var flourishStart: CFAbsoluteTime = -1

    /// Only re-rasterise when the visible digits change, not 120 times a second.
    private var shownLabel: String?

    var isRunning: Bool { running && (mode == .focus || mode == .rest) }

    init(atlas: Atlas, view: CatView, bus: WellnessBus) {
        self.atlas = atlas
        self.view = view
        self.bus = bus
    }

    func rebind(view: CatView) {
        self.view = view
        shownLabel = nil
    }

    // MARK: - control

    func start() {
        if mode == .stopped || mode == .done {
            round = 0
            beginFocus()
        }
        running = true
        lastTick = CFAbsoluteTimeGetCurrent()
        bus.log("pomodoro START round 1/\(rounds) focus \(Int(remaining))s")
    }

    func pause() {
        running = false
        bus.log("pomodoro PAUSE at \(label(remaining))")
    }

    func reset() {
        running = false
        mode = .stopped
        round = 0
        remaining = 0
        clearPlate()
        bus.log("pomodoro RESET")
    }

    /// A duration change mid-block would be confusing; it applies from the next one.
    func settingsChanged() {
        if mode == .stopped || mode == .done { clearPlate() }
    }

    private var focusSeconds: Double {
        bus.isDemo ? 10 : Double(bus.settings.focusMinutes) * 60
    }
    private var restSeconds: Double {
        bus.isDemo ? 6 : Double(bus.settings.breakMinutes) * 60
    }
    private var rounds: Int { bus.isDemo ? 2 : max(bus.settings.rounds, 1) }

    private func beginFocus() {
        round += 1
        mode = .focus
        remaining = focusSeconds
        flourishStart = CFAbsoluteTimeGetCurrent()
        bus.bubble?.say("Round \(round) of \(rounds). Focus!", for: 3)
    }

    private func beginRest(away: Bool) {
        mode = .rest
        remaining = restSeconds
        if away {
            // The break still runs down, but there is nobody to stretch at.
            bus.log("pomodoro break: skipping the stretch, user away")
        } else {
            bus.stretch?.trigger(reason: "pomodoro break")
        }
        bus.chime()
    }

    // MARK: - tick

    func update(_ ctx: TickContext) -> ModuleOutput {
        let now = CFAbsoluteTimeGetCurrent()
        // Wall-clock, not accumulated dt: dt is clamped to 0.1s per frame, so a
        // sleeping laptop would leave a 25-minute block hours behind.
        let step = min(max(now - lastTick, 0), 5)
        lastTick = now

        if running, mode == .focus || mode == .rest {
            remaining -= step
            if remaining <= 0 {
                if mode == .focus {
                    bus.log("pomodoro focus \(round)/\(rounds) done -> break")
                    beginRest(away: ctx.secondsSinceKey > bus.awaySeconds)
                } else if round >= rounds {
                    mode = .done
                    running = false
                    remaining = 0
                    bus.bubble?.say("\(rounds) rounds done. Nice.", for: 6)
                    bus.log("pomodoro COMPLETE")
                } else {
                    bus.log("pomodoro break done -> focus \(round + 1)/\(rounds)")
                    beginFocus()
                }
            }
        }

        renderPlate()

        // The plate belongs to the cat's chrome, not to the stretch break's screen —
        // hidden wholesale by CatView while a stretch is running.
        guard mode == .focus, flourishStart >= 0 else { return .none }
        let d = atlas.wellness.flourishDuration
        let p = (now - flourishStart) / d
        guard p < 1 else { flourishStart = -1; return .none }

        // "Getting to work": one decisive crouch-and-up, not a wiggle.
        var out = ModuleOutput()
        let t = CGFloat(p)
        let envelope = (1 - t) * (1 - t)
        out.squash = 1 - envelope * 0.10 * cos(.pi * 3 * t)
        out.offset.y = -envelope * sin(.pi * 2 * t) * atlas.wellness.bobHeight * 0.7
        return out
    }

    // MARK: - the plate

    private func label(_ seconds: Double) -> String {
        let s = max(Int(seconds.rounded(.up)), 0)
        return String(format: "%02d:%02d", s / 60, s % 60)
    }

    private func renderPlate() {
        let want: String?
        switch mode {
        case .focus: want = label(remaining)
        case .rest:  want = label(remaining)
        case .stopped, .done: want = nil
        }
        guard want != shownLabel else { return }
        shownLabel = want

        guard let text = want, let bubble = atlas.bubble else { clearPlate(); return }
        guard let r = bubble.render(text, withTail: false), let cg = r.image.cgImage() else {
            clearPlate(); return
        }
        let w = atlas.wellness
        // Right-aligned against the cat so the plate grows leftwards into the
        // margin, keeping its inner edge still while the digits change width.
        let origin = CGPoint(
            x: (w.timerRight - CGFloat(r.image.width)).rounded(),
            y: (w.timerCY - CGFloat(r.image.height) / 2).rounded())
        view.setAux("pomodoro", image: cg, atlasOrigin: origin,
                    size: CGSize(width: r.image.width, height: r.image.height))
    }

    private func clearPlate() {
        shownLabel = nil
        view.setAux("pomodoro", image: nil, atlasOrigin: .zero, size: .zero)
    }
}
