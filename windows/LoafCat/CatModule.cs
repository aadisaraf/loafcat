namespace LoafCat;

/// Everything a module is allowed to know about the world, gathered once per tick.
///
/// Passing a snapshot rather than letting modules reach into Win32 keeps them
/// testable and keeps the expensive calls (cursor position, input counters) to one
/// per frame no matter how many modules want them.
public readonly struct TickContext
{
    /// Seconds since the previous tick, already clamped so a stalled frame cannot
    /// fling a spring across the screen.
    public required double Dt { get; init; }

    /// Cursor position relative to the cat's centre, in LOGICAL pixels, y-down to
    /// match the atlas. Modules should never need screen coordinates.
    public required Pt Cursor { get; init; }

    /// Cursor velocity in logical px/sec, y-down. Already smoothed.
    public required Pt CursorVelocity { get; init; }

    /// True when the cursor is over the cat's dilated silhouette.
    public required bool CursorOnCat { get; init; }

    /// Keystrokes in the last window, as a rate. Never key identity — see
    /// CLAUDE.md, and InputTelemetry.cs for why this is structural on Windows too.
    public required double KeysPerSecond { get; init; }

    /// Scroll wheel events observed since the previous tick.
    public required uint ScrollDelta { get; init; }

    /// Seconds since the user last pressed any key, system-wide.
    public required double SecondsSinceKey { get; init; }

    /// The cat's window rectangle in screen pixels, y-down, for modules that move it.
    public required Rect Frame { get; init; }

    /// Logical-pixels-per-device-pixel, so modules can convert if they must.
    public required double Scale { get; init; }
}

/// Named states the cat can be in. Modules request them; the coordinator resolves
/// conflicts by priority so two modules cannot fight over the same frame.
public enum CatState
{
    Idle,
    Kneading,       // typing
    Overheat,       // typing fast
    Hunting,        // fast, reversing cursor
    Purring,        // being petted
    Dragging,
    Scrolling,
    Thinking,       // agent working
    Celebrating,    // agent finished
    Errored,        // agent failed
    Sleeping,
    Stretching,
}

public static class CatStateExtensions
{
    /// Higher wins when two modules want the cat at the same time. Direct physical
    /// manipulation always beats an ambient reaction — being picked up should
    /// interrupt a stretch, never the other way round.
    public static int Priority(this CatState s) => s switch
    {
        CatState.Idle => 0,
        CatState.Sleeping => 1,
        CatState.Thinking => 2,
        CatState.Kneading or CatState.Scrolling => 3,
        CatState.Overheat => 4,
        CatState.Purring or CatState.Hunting => 5,
        CatState.Celebrating or CatState.Errored => 6,
        CatState.Stretching => 7,
        CatState.Dragging => 10,
        _ => 0,
    };

    /// Lower-case, matching the Swift enum's raw values, so `--debug-state` output is
    /// comparable line for line between the two builds.
    public static string Name(this CatState s) => s.ToString().ToLowerInvariant();
}

/// What a module wants to happen this frame. Everything is optional; a module that
/// has nothing to say returns `ModuleOutput.None`.
public struct ModuleOutput()
{
    /// The state this module is requesting, if any.
    public CatState? State = null;

    /// Extra vertical squash, multiplied into the rig's. 1.0 is neutral.
    public double Squash = 1.0;

    /// Offset applied to the whole cat, in logical pixels.
    public Pt Offset = Pt.Zero;

    /// A short-lived overlay to show above the cat (steam, hearts, zzz, a bubble).
    public string? Overlay = null;

    /// When set AND this module wins the state contest, every other module's squash,
    /// offset and overlay is discarded for the frame — and so is everything any
    /// module posted to `CatStage`'s per-part channels, which is why an exclusive
    /// module must express itself through this struct alone.
    ///
    /// Priority alone only decides which *state* wins; the numbers still blend. A
    /// stretch break needs the stronger guarantee — the cat is the size of the screen
    /// and any leftover kneading or hunting squash reads as a glitch, not as a second
    /// opinion.
    public bool Exclusive = false;

    public static ModuleOutput None => new();
}

/// One feature, in one file.
///
/// Modules are registered in `Program.cs` and are otherwise independent, which is
/// what lets several be developed in parallel without conflicting. A module can be
/// removed by deleting its file and its one registration line.
public interface ICatModule
{
    /// Stable identifier, used in logs and the debug overlay.
    string Id { get; }

    /// Called once per tick at 120Hz. Must not block — anything slow belongs on a
    /// background thread with its result read here.
    ModuleOutput Update(in TickContext ctx);

    /// Called when the user clicks the cat's body. Return true to consume.
    bool MouseDown(Pt point) => false;

    /// Called when the user releases. Only sent to whoever consumed the down.
    void MouseUp(Pt point) { }

    /// Called on drag, in logical pixels of movement since the last event.
    void MouseDragged(Pt delta) { }
}

/// Runs the registered modules and resolves what they collectively want.
public sealed class ModuleRegistry
{
    private readonly List<ICatModule> _modules = [];
    private ICatModule? _dragOwner;

    public IReadOnlyList<ICatModule> Modules => _modules;

    /// The winning state this frame, and which module asked for it.
    public CatState State { get; private set; } = CatState.Idle;
    public string StateOwner { get; private set; } = "-";
    public List<string> Overlays { get; } = [];

    public void Register(ICatModule m) => _modules.Add(m);

    /// Combined output for this tick. Squash multiplies (so two modules both
    /// compressing the cat compound), offsets add, and the highest-priority state
    /// wins outright rather than blending — a cat cannot be half-dragged.
    ///
    /// Unless the winner asked to be exclusive, in which case it is the only module
    /// that contributes anything this frame.
    ///
    /// The shared stage is cleared before the modules run and sealed after, so a
    /// module reading `CatStage.Shared.State` during its own update sees the PREVIOUS
    /// frame's winner. That one-frame lag is what lets a module yield to dragging
    /// without knowing which module owns dragging.
    public ModuleOutput Update(in TickContext ctx)
    {
        var stage = CatStage.Shared;
        stage.BeginFrame();

        var outputs = new ModuleOutput[_modules.Count];
        int best = -1;
        ModuleOutput? winner = null;
        State = CatState.Idle;
        StateOwner = "-";
        Overlays.Clear();

        // Every module is still ticked, even when one is exclusive: a module that
        // stops being called mid-gesture loses its own timers and comes back wrong.
        for (int i = 0; i < _modules.Count; i++)
        {
            var outv = _modules[i].Update(in ctx);
            outputs[i] = outv;
            if (outv.State is { } s && s.Priority() > best)
            {
                best = s.Priority();
                State = s;
                StateOwner = _modules[i].Id;
                winner = outv;
            }
        }

        var combined = new ModuleOutput();
        if (winner is { Exclusive: true } w)
        {
            combined.Squash = w.Squash;
            combined.Offset = w.Offset;
            if (w.Overlay is { } o) Overlays.Add(o);
            // Same rule, applied to the other channel modules write on. Priority
            // decided the state; `Exclusive` is the stronger claim that nothing else
            // is on screen, and a stray paw offset or puff of steam would break it
            // exactly as visibly as a stray squash.
            stage.DiscardPartChannels();
        }
        else
        {
            foreach (var outv in outputs)
            {
                combined.Squash *= outv.Squash;
                combined.Offset.X += outv.Offset.X;
                combined.Offset.Y += outv.Offset.Y;
                if (outv.Overlay is { } o) Overlays.Add(o);
            }
        }
        combined.State = State;
        stage.EndFrame(State, combined.Offset);
        LogState(in ctx);
        return combined;
    }

    // --- --debug-state ------------------------------------------------------
    // Prints the winning state and every metric the modules publish, so behaviour can
    // be checked from a log rather than by a human watching a cat. Off unless asked
    // for, and rate-limited, because this runs on the 120Hz tick.

    private readonly bool _debugging =
        Environment.GetCommandLineArgs().Contains("--debug-state");
    private readonly double _launched = Clock.Now;
    private double _lastLog;
    private CatState? _lastLoggedState;

    private void LogState(in TickContext ctx)
    {
        if (!_debugging) return;
        double now = Clock.Now;
        bool changed = _lastLoggedState != State;
        if (!changed && now - _lastLog < 0.1) return;
        _lastLog = now;
        _lastLoggedState = State;

        var stage = CatStage.Shared;
        var keys = stage.Metrics.Keys.ToList();
        keys.Sort(StringComparer.Ordinal);
        string metrics = string.Join(" ", keys.Select(k => $"{k}={N(stage.Metrics[k])}"));

        string line = $"[state] t={N(now - _launched)}"
            + $" {Pad(State.Name(), 11)} by={Pad(StateOwner, 8)}"
            + $" kps={N(ctx.KeysPerSecond)} heat={N(stage.Heat)} {metrics}";
        if (Overlays.Count > 0) line += " fx=" + string.Join(",", Overlays);
        if (changed) line += "  <-";
        Log.Warn(line);
    }

    private static string N(double v) => v.ToString("0.00");
    private static string Pad(string s, int w) => s.Length >= w ? s : s.PadRight(w);

    public bool MouseDown(Pt point)
    {
        foreach (var m in _modules)
        {
            if (!m.MouseDown(point)) continue;
            _dragOwner = m;
            return true;
        }
        return false;
    }

    public void MouseUp(Pt point)
    {
        _dragOwner?.MouseUp(point);
        _dragOwner = null;
    }

    public void MouseDragged(Pt delta) => _dragOwner?.MouseDragged(delta);
}
