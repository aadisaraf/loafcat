import AppKit

/// A small reaction to the scroll wheel: the cat bobs and paddles a paw.
///
/// Deliberately the least ambitious module here. Scrolling is constant and mostly
/// incidental — it happens while reading, not as an interaction with the cat — so
/// anything bigger than a bob would be in the way all day. It also sits at the same
/// low priority as kneading, below anything the user aimed at the cat on purpose.
final class ScrollModule: CatModule, AtlasTuned {
    let id = "scroll"
    var tunedGeneration = -1

    private var hold: Double = 0
    private var bob: CGFloat = 0
    private var bobHz: CGFloat = 0
    private var paw: CGFloat = 0
    private var attack: CGFloat = 0.05
    private var decay: CGFloat = 0.2

    private var activeUntil: CFAbsoluteTime = 0
    private var amp: CGFloat = 0
    private var phase: CGFloat = 0

    func retune(_ atlas: Atlas) {
        let b = atlas.behaviour
        hold = Double(b.f("scroll.hold"))
        bob = b.f("scroll.bob")
        bobHz = b.f("scroll.bob_hz")
        paw = b.f("scroll.paw")
        attack = max(b.f("scroll.attack"), 0.001)
        decay = max(b.f("scroll.decay"), 0.001)
    }

    func update(_ ctx: TickContext) -> ModuleOutput {
        guard tunedAtlas() != nil else { return .none }
        let stage = CatStage.shared
        let now = CFAbsoluteTimeGetCurrent()

        // Every wheel event re-arms the timer, so continuous scrolling holds the
        // reaction and a single flick decays out of it.
        if ctx.scrollDelta > 0 { activeUntil = now + hold }
        let active = now < activeUntil

        amp += ((active ? 1 : 0) - amp) * (1 - exp(-ctx.dt / (active ? attack : decay)))
        stage.metric("scroll.amp", amp)
        guard amp > 0.002 else {
            phase = 0
            return .none
        }

        phase += ctx.dt * bobHz
        while phase >= 1 { phase -= 1 }

        var out = ModuleOutput()
        let swing = sin(phase * 2 * .pi)
        out.offset.y = -abs(swing) * bob * amp
        // The paws paddle in antiphase, which reads as the cat riding the scroll.
        stage.pawOffsetL.y -= max(swing, 0) * paw * amp
        stage.pawOffsetR.y -= max(-swing, 0) * paw * amp
        // The STATE ends with the timer, not with the pose. The last few frames of
        // the bob easing out are still motion, but the cat is no longer reacting to
        // anything, and a state that outlives its cause by a second reads as stuck.
        if active { out.state = .scrolling }
        return out
    }
}
