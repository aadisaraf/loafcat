using System.Runtime.Versioning;

namespace LoafCat.Modules;

/// The same idea as the stretch break, at a hundredth of the volume: the cat bobs up,
/// says something about water, and goes back to what it was doing.
///
/// No window resize on purpose. A drink of water takes four seconds; taking over the
/// middle of the screen for it would be out of proportion, and the whole reason the
/// stretch break can afford to be dramatic is that nothing else is.
[SupportedOSPlatform("windows")]
public sealed class HydrationModule : ICatModule
{
    public string Id => "hydration";

    private readonly WellnessBus _bus;
    private double _nextFire;
    private double _bobUntil;
    private double _bobStart;
    private int _nextLine;

    private const double BobDuration = 1.1;
    private const double HoldSeconds = 5;

    /// Copy, not geometry, so it stays in code where it can be localised — the atlas
    /// describes the cat's body, not its vocabulary.
    private static readonly string[] Lines =
    [
        "Water break!",
        "Drink something.",
        "Hydrate, human.",
        "Refill your glass?",
    ];

    public HydrationModule(WellnessBus bus)
    {
        _bus = bus;
        SettingsChanged();
    }

    public void SettingsChanged()
    {
        if (Interval is not { } iv) { _nextFire = Clock.Never; return; }
        _nextFire = _bus.FirstFire(14, iv);
    }

    private double? Interval => _bus.Interval(
        _bus.Settings.HydrationMinutes, _bus.IsDemo ? 30 : 0);

    public ModuleOutput Update(in TickContext ctx)
    {
        double now = Clock.Now;

        if (now >= _nextFire && Interval is { } iv)
        {
            if (_bus.Busy)
            {
                // Never interrupt a stretch break; try again shortly.
                _nextFire = now + 5;
            }
            else if (ctx.SecondsSinceKey > _bus.AwaySeconds)
            {
                _nextFire = now + iv;
                _bus.LogDemo($"hydration SKIPPED, away {ctx.SecondsSinceKey:0}s");
            }
            else
            {
                _nextFire = now + iv;
                string line = Lines[_nextLine % Lines.Length];
                _nextLine++;
                _bus.Bubble?.Say(line, HoldSeconds);
                _bus.Chime();
                _bobStart = now;
                _bobUntil = now + BobDuration;
                _bus.LogDemo($"hydration \"{line}\"");
            }
        }

        if (now >= _bobUntil) return ModuleOutput.None;

        var outv = new ModuleOutput();
        double p = (now - _bobStart) / BobDuration;
        // Two quick hops, decaying — enough to draw the eye without asking for a
        // priority high enough to interrupt anything.
        double envelope = (1 - p) * (1 - p);
        outv.Offset.Y = -Math.Abs(Math.Sin(Math.PI * 2 * p)) * envelope
                        * _bus.Atlas.Wellness.BobHeight;
        outv.Squash = 1 + envelope * 0.04 * Math.Cos(Math.PI * 2 * p);
        return outv;
    }
}
