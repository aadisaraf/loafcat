import AppKit

/// The same idea as the stretch break, at a hundredth of the volume: the cat bobs
/// up, says something about water, and goes back to what it was doing.
///
/// No window resize on purpose. A drink of water takes four seconds; taking over the
/// middle of the screen for it would be out of proportion, and the whole reason the
/// stretch break can afford to be dramatic is that nothing else is.
final class HydrationModule: CatModule {
    let id = "hydration"

    private let bus: WellnessBus
    private var nextFire: CFAbsoluteTime = 0
    private var bobUntil: CFAbsoluteTime = 0
    private var bobStart: CFAbsoluteTime = 0
    private var nextLine = 0

    private let bobDuration: Double = 1.1
    private let holdSeconds: Double = 5

    /// Copy, not geometry, so it stays in Swift where it can be localised — the
    /// atlas describes the cat's body, not its vocabulary.
    private let lines = [
        "Water break!",
        "Drink something.",
        "Hydrate, human.",
        "Refill your glass?",
    ]

    init(bus: WellnessBus) {
        self.bus = bus
        settingsChanged()
    }

    func settingsChanged() {
        guard let iv = interval else { nextFire = .greatestFiniteMagnitude; return }
        nextFire = CFAbsoluteTimeGetCurrent() + iv
    }

    private var interval: Double? {
        bus.interval(userMinutes: bus.settings.hydrationMinutes,
                     demoSeconds: bus.isDemo ? 30 : 0)
    }

    func update(_ ctx: TickContext) -> ModuleOutput {
        let now = CFAbsoluteTimeGetCurrent()

        if now >= nextFire, let iv = interval {
            if bus.busy {
                // Never interrupt a stretch break; try again shortly.
                nextFire = now + 5
            } else if ctx.secondsSinceKey > bus.awaySeconds {
                nextFire = now + iv
                bus.log(String(format: "hydration SKIPPED, away %.0fs", ctx.secondsSinceKey))
            } else {
                nextFire = now + iv
                let line = lines[nextLine % lines.count]
                nextLine += 1
                bus.bubble?.say(line, for: holdSeconds)
                bus.chime()
                bobStart = now
                bobUntil = now + bobDuration
                bus.log("hydration \"\(line)\"")
            }
        }

        guard now < bobUntil else { return .none }
        var out = ModuleOutput()
        let p = CGFloat((now - bobStart) / bobDuration)
        // Two quick hops, decaying — enough to draw the eye without asking for a
        // priority high enough to interrupt anything.
        let envelope = (1 - p) * (1 - p)
        out.offset.y = -abs(sin(.pi * 2 * p)) * envelope * bus.atlas.wellness.bobHeight
        out.squash = 1 + envelope * 0.04 * cos(.pi * 2 * p)
        return out
    }
}
