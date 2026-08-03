namespace LoafCat.Modules;

/// A small reaction to the scroll wheel: the cat bobs and paddles a paw.
///
/// Deliberately the least ambitious module here. Scrolling is constant and mostly
/// incidental — it happens while reading, not as an interaction with the cat — so
/// anything bigger than a bob would be in the way all day. It also sits at the same
/// low priority as kneading, below anything the user aimed at the cat on purpose.
public sealed class ScrollModule : ICatModule, IAtlasTuned
{
    public string Id => "scroll";
    public int TunedGeneration { get; set; } = -1;

    private double _hold;
    private double _bob;
    private double _bobHz;
    private double _paw;
    private double _attack = 0.05;
    private double _decay = 0.2;

    private double _activeUntil;
    private double _amp;
    private double _phase;

    public void Retune(Atlas atlas)
    {
        var b = atlas.Behaviour;
        _hold = b.F("scroll.hold");
        _bob = b.F("scroll.bob");
        _bobHz = b.F("scroll.bob_hz");
        _paw = b.F("scroll.paw");
        _attack = Math.Max(b.F("scroll.attack"), 0.001);
        _decay = Math.Max(b.F("scroll.decay"), 0.001);
    }

    public ModuleOutput Update(in TickContext ctx)
    {
        if (this.TunedAtlas() is null) return ModuleOutput.None;
        var stage = CatStage.Shared;
        double now = Clock.Now;

        // Every wheel event re-arms the timer, so continuous scrolling holds the
        // reaction and a single flick decays out of it.
        if (ctx.ScrollDelta > 0) _activeUntil = now + _hold;
        bool active = now < _activeUntil;

        _amp += ((active ? 1 : 0) - _amp) * (1 - Math.Exp(-ctx.Dt / (active ? _attack : _decay)));
        stage.Metric("scroll.amp", _amp);
        if (_amp <= 0.002)
        {
            _phase = 0;
            return ModuleOutput.None;
        }

        _phase += ctx.Dt * _bobHz;
        while (_phase >= 1) _phase -= 1;

        var outv = new ModuleOutput();
        double swing = Math.Sin(_phase * 2 * Math.PI);
        outv.Offset.Y = -Math.Abs(swing) * _bob * _amp;
        // The paws paddle in antiphase, which reads as the cat riding the scroll.
        stage.PawOffsetL.Y -= Math.Max(swing, 0) * _paw * _amp;
        stage.PawOffsetR.Y -= Math.Max(-swing, 0) * _paw * _amp;
        // The STATE ends with the timer, not with the pose. The last few frames of the
        // bob easing out are still motion, but the cat is no longer reacting to
        // anything, and a state that outlives its cause by a second reads as stuck.
        if (active) outv.State = CatState.Scrolling;
        return outv;
    }
}
