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
public sealed class KeyInference
{
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

    public KeyInference(double now) => _lastKeyAt = now;

    public long Keys => Interlocked.Read(ref _keys);
    public double LastKeyAt => _lastKeyAt;
    public int Pending => _pending.Count;

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
        int found = 0;
        while (_pending.Count > 0 && now - _pending.Peek().At >= ResolveAfter)
        {
            var p = _pending.Dequeue();
            if (WasMouse(p.Tick, p.At)) continue;
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
