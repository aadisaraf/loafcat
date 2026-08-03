namespace LoafCat.Interop;

/// Decides whether an input event Windows will only describe as "something happened at
/// tick T" was a keystroke rather than a mouse event.
///
/// Split out of `InputTelemetry` so it can be driven with synthetic timings by
/// `--selftest`. That is not tidiness: the bug this class exists to prevent — the cat
/// overheating while its owner only moved the mouse — is invisible without a test that
/// can replay a mouse stream, and there is no way to move a real mouse on CI.
///
/// It has no counterpart in the macOS build, which is handed a key-event count directly
/// and never has to infer anything. See `windows/README.md`.
///
/// -------------------------------------------------------------------------------
/// "NOT THE MOUSE" IS NOT THE SAME AS "A KEY"
/// -------------------------------------------------------------------------------
/// The first version of this class assumed the machine has exactly two input devices,
/// so ruling out one proved the other. It does not. `GetLastInputInfo` is reset by any
/// raw input the session receives — a finger resting on a precision touchpad reports at
/// ~125Hz whether or not it moves, a hand resting on a high-polling-rate mouse reports
/// with no displacement to deliver, a connected game controller reports forever, a pen
/// digitizer reports while it hovers. None of those produce a `WM_MOUSEMOVE`, so the
/// mouse hook never sees them, so every one of them used to arrive here as typing.
///
/// It reproduced as the exact inverse of the bug before it: the cat overheated while the
/// cursor sat still and cooled down the moment the mouse actually moved, because moving
/// it filled the ring with events that explained the ticks away.
///
/// There is no permission-free way to ask what the input actually was, and asking is
/// banned regardless. So the inference is no longer "not the mouse, therefore a key" but
/// "not the mouse, AND shaped like something a person did" — see `Resolve`. Both shape
/// tests are about the timing of the stream, which is all this class is ever told.
public sealed class KeyInference
{
    /// The closest together two keystrokes can be and still be two keystrokes.
    ///
    /// A device reporting on its own schedule lands on the `GetTickCount` grid, so its
    /// ticks arrive ~15.6ms apart — or one poll apart, ~8.3ms, when something else on the
    /// machine has raised the timer resolution to 1ms, which Chrome and most games do.
    /// Human typing is nowhere near either: 25ms between keys is 40 a second, roughly
    /// twice the fastest sustained typing ever recorded, and well past `overheat.kps_max`.
    ///
    /// This is checked in BOTH directions, which is only possible because a verdict is
    /// already deferred by `ResolveAfter` — 50ms is longer than this gap, so by the time
    /// anything is ruled on, its successor has already arrived and can be looked at.
    ///
    /// Measured against jittered typing from 3 to 18 keys a second, at +-15% and +-30%
    /// wander, 40 seeds each: worst case one keystroke lost in a hundred. Widening it to
    /// 40ms would close more of the band below, and starts eating real ones.
    public const double KeyGap = 0.025;

    /// The backstop, for a stream slow enough to pass `KeyGap` and still not be a person:
    /// keystrokes per second, sustained across `ChatterWindow`, that nobody reaches.
    ///
    /// Sustained records are around 14-15 characters a second and `overheat.kps_max` is
    /// 14, so this leaves the whole of real typing — including the part that is supposed
    /// to redden the cat — comfortably below it.
    public const double HumanMaxKps = 22;
    public const double ChatterWindow = 1.0;

    /// How long a stream stays written off after it stops looking inhuman. Long enough
    /// that a continuous one is judged once rather than repeatedly: the window is still
    /// full when the hold expires, so it re-trips immediately and credits nothing.
    public const double ChatterHold = 2.0;

    // A third test was written and thrown away, and it is worth saying why so nobody
    // adds it back. Between roughly 3 and 22 reports a second, an idle device is inside
    // human typing range and spaced too far apart to trip `KeyGap`, so neither test above
    // can reach it. Evenness looks like the answer — a clock repeats its interval exactly
    // and hands never do — but this stream has already been through an 8.3ms poll, and
    // that quantisation destroys the very jitter the test needs: at 18 keys a second with
    // a generous +-15% wander, real typing lands in one or two poll bins and reads as
    // perfectly even. Measured, it discarded 45 of 159 genuine keystrokes across 24 gaps.
    // Two runs of it, at two window lengths, said the same thing.
    //
    // So that band is left open, deliberately. Nothing is known to sit in it: every device
    // that reports while idle — touchpad, controller, pen, mouse — runs at 60Hz or faster,
    // and anything above 64Hz lands on the GetTickCount grid, which `KeyGap` closes
    // outright. A slower one would need a real report to fix properly, not a guess here.

    /// How long to wait before ruling on an observed input tick.
    ///
    /// `GetLastInputInfo` is updated by the raw input thread the instant an event
    /// arrives. Our mouse hook is a callback dispatched to a different thread
    /// afterwards. So a poll can — and routinely does — see the tick of a mouse event
    /// BEFORE the hook has recorded that event, and a verdict passed at that instant
    /// blames the keyboard for a mouse move. Waiting for the hook to catch up is the
    /// whole difference between this working and not.
    ///
    /// 50ms is far longer than the hook needs (it runs at above-normal priority and
    /// does nothing else) and far shorter than anything the cat reacts to: the kneading
    /// gate is a 2-second window and the release delay is 180ms.
    public const double ResolveAfter = 0.05;

    /// How far apart two readings of the same event may be and still be one event.
    ///
    /// They *should* be identical — `MSLLHOOKSTRUCT.time` and `LASTINPUTINFO.dwTime`
    /// are both the tick the event was stamped with. The tolerance is here because that
    /// is an assumption about two Win32 APIs that could not be verified on real hardware
    /// from the machine this port was written on, and because `GetTickCount` advances in
    /// ~15.6ms steps, so "equal" would be the wrong test even for two honest readings.
    public const int TickTolerance = 20;

    /// How long after a mouse event, on our OWN clock, an input tick is still assumed to
    /// be that mouse event. The second, independent test — see `WasMouse`.
    public const double MouseShadow = 0.03;

    private const int RingSize = 256;   // a quarter-second of history at a 1000Hz mouse
    private const int RingMask = RingSize - 1;

    // Written by the hook thread, read by the tick. Plain arrays with a volatile write
    // index rather than a lock: this is touched from a system-wide mouse hook, where a
    // contended lock would be felt as a laggy cursor in every application on the
    // machine. A torn read can only ever affect the newest slot, which is never
    // consulted until it is ResolveAfter old and long since written.
    private readonly uint[] _mouseTick = new uint[RingSize];
    private readonly double[] _mouseAt = new double[RingSize];
    private long _head;

    private readonly Queue<(uint Tick, double At)> _pending = new();
    private long _keys;
    private double _lastKeyAt;

    // The stream of input the mouse could not account for, whether or not it was
    // believed. Recorded even while it is being written off, so the rate test keeps
    // seeing a chattering device for as long as it chatters.
    private readonly Queue<double> _unexplained = new();
    private double _lastUnexplainedAt = double.NegativeInfinity;
    private double _chatterUntil = double.NegativeInfinity;
    private long _ignored;

    public KeyInference(double now) => _lastKeyAt = now;

    public long Keys => Interlocked.Read(ref _keys);
    public double LastKeyAt => _lastKeyAt;
    public int Pending => _pending.Count;

    /// Unexplained input this refused to call typing. Only the log and `--selftest`
    /// read it — but a user whose cat overheats can send a log that says which of the
    /// two failure modes they are in, which is the whole reason it is counted.
    public long Ignored => Interlocked.Read(ref _ignored);

    /// Whether something on this machine is currently producing input faster than a
    /// person types. Edge-logged by `InputTelemetry`.
    public bool Chattering => _lastResolveAt < _chatterUntil;
    private double _lastResolveAt;

    /// From the mouse hook, once per mouse event.
    ///
    /// `eventTick` is the event's OWN timestamp — `MSLLHOOKSTRUCT.time` — which is the
    /// same quantity `GetLastInputInfo` reports for that same event. It is emphatically
    /// NOT the clock read at the moment the callback happened to run: that is a
    /// different quantity, later by however long the callback took to be dispatched, and
    /// mistaking one for the other is what made every mouse move look like typing.
    public void NoteMouse(uint eventTick, double at)
    {
        long head = Volatile.Read(ref _head);
        int slot = (int)(head & RingMask);
        _mouseTick[slot] = eventTick;
        _mouseAt[slot] = at;
        Volatile.Write(ref _head, head + 1);
    }

    /// From the tick, when `GetLastInputInfo` reported a tick it had not reported before.
    public void NoteInput(uint tick, double at) => _pending.Enqueue((tick, at));

    /// Rules on everything old enough to rule on. Returns how many keystrokes it has
    /// just become sure of, which is only ever used by the test.
    public int Resolve(double now)
    {
        _lastResolveAt = now;
        int found = 0;
        while (_pending.Count > 0 && now - _pending.Peek().At >= ResolveAfter)
        {
            var p = _pending.Dequeue();
            if (WasMouse(p.Tick, p.At)) continue;

            // Past here, all that is known is that input arrived and the mouse cannot
            // account for it. Record it before deciding anything: the rate test has to
            // watch a chattering device for as long as it chatters, including through
            // the hold in which nothing it produces is being believed.
            double sincePrevious = p.At - _lastUnexplainedAt;
            _lastUnexplainedAt = p.At;
            _unexplained.Enqueue(p.At);
            while (_unexplained.Count > 0 && p.At - _unexplained.Peek() > ChatterWindow)
                _unexplained.Dequeue();

            if (_unexplained.Count / ChatterWindow > HumanMaxKps)
                _chatterUntil = p.At + ChatterHold;

            // One: is it on its own? A device reporting on a schedule always has a
            // neighbour within one tick of the system clock. The successor is knowable
            // because this verdict was already held back for longer than the gap.
            double untilNext = _pending.Count > 0
                ? _pending.Peek().At - p.At
                : double.PositiveInfinity;
            bool isolated = sincePrevious >= KeyGap && untilNext >= KeyGap;

            // Two: is the stream it belongs to one a person could produce at all?
            bool chattering = p.At < _chatterUntil;

            if (!isolated || chattering)
            {
                Interlocked.Increment(ref _ignored);
                continue;
            }

            Interlocked.Increment(ref _keys);
            _lastKeyAt = p.At;
            found++;
        }
        return found;
    }

    /// Two independent tests, either of which is enough to clear the mouse of it.
    ///
    /// Both point the same way — towards blaming the mouse — and that asymmetry is
    /// deliberate. Under-counting a keystroke costs a moment of kneading nobody notices;
    /// over-counting one drives a rate through `overheat.kps_min` and reddens a cat whose
    /// owner is not typing at all. Given a Windows machine was unavailable to measure on,
    /// the failure that is merely disappointing is the one to choose.
    ///
    /// The cost is that typing *while* actively moving the mouse is not seen. That is
    /// acceptable: it is a real thing to be unable to distinguish, not an approximation
    /// of one, and the alternative is guessing.
    private bool WasMouse(uint tick, double at)
    {
        long head = Volatile.Read(ref _head);
        long n = Math.Min(head, RingSize);
        for (long i = 1; i <= n; i++)
        {
            int slot = (int)((head - i) & RingMask);
            uint t = _mouseTick[slot];
            double a = _mouseAt[slot];

            // One: the event's own timestamp, which is exact when the two Win32 clocks
            // agree about the event they both saw.
            if (Math.Abs(unchecked((int)(tick - t))) <= TickTolerance) return true;

            // Two: when the hook actually fired, measured on OUR clock and compared
            // only against itself. This test assumes nothing about Win32 timestamps
            // agreeing about anything, so a mouse that is moving cannot be read as
            // typing even if the first test is somehow wrong on hardware.
            if (a >= at - MouseShadow && a <= at + ResolveAfter) return true;

            // The ring runs backwards in time, so once it is well past the window there
            // is nothing left in it to find.
            if (a < at - 1.0) break;
        }
        return false;
    }
}
