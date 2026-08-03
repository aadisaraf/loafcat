using System.Runtime.Versioning;

namespace LoafCat.Modules;

/// Draws whatever the cat has to say, above its head.
///
/// One module owns the bubble so there is exactly one of them on screen: without that,
/// a hydration nudge landing on top of a pinned note would produce two overlapping
/// bubbles and no way to tell which is which.
///
/// Registered LAST, so by the time it runs every other module has already said what it
/// wants for this frame.
[SupportedOSPlatform("windows")]
public sealed class BubbleModule : ICatModule
{
    public string Id => "bubble";

    private readonly Atlas _atlas;
    private CatView _view;
    private readonly WellnessBus _bus;

    private string? _transientText;
    private double _transientUntil;
    private string? _pinnedText;

    /// Set while the stretch break owns the screen — a bubble magnified 10x would be a
    /// wall of text.
    private bool _suppressed;

    /// What is currently on the surface, so we only re-render when it changes.
    private string? _shown;

    /// Bubbles are laid out pixel by pixel, which is cheap but not free; the same
    /// hydration nudge recurs for the life of the process.
    private readonly Dictionary<string, SpeechBubble.Rendered> _cache = [];

    public BubbleModule(Atlas atlas, CatView view, WellnessBus bus)
    {
        _atlas = atlas;
        _view = view;
        _bus = bus;
        string note = bus.Settings.PinnedNote;
        _pinnedText = note.Length == 0 ? null : note;
    }

    public void Rebind(CatView view)
    {
        _view = view;
        _cache.Clear();
        _shown = null;
    }

    /// Says something for a few seconds, replacing anything already transient.
    public void Say(string text, double seconds)
    {
        _transientText = text;
        _transientUntil = Clock.Now + seconds;
    }

    /// A note that stays until it is dismissed. Null clears it.
    public void Pin(string? text)
    {
        string? trimmed = text?.Trim();
        _pinnedText = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        _bus.Settings.PinnedNote = _pinnedText ?? "";
    }

    public bool HasPinnedNote => _pinnedText is not null;

    public void Suppress(bool on) => _suppressed = on;

    public ModuleOutput Update(in TickContext ctx)
    {
        double now = Clock.Now;
        if (_transientText is not null && now >= _transientUntil) _transientText = null;

        // A transient message wins: it was triggered by something happening now, and
        // the pinned note is by definition not urgent.
        string? want = _suppressed ? null : (_transientText ?? _pinnedText);
        if (want != _shown)
        {
            _shown = want;
            Present(want);
        }
        return ModuleOutput.None;
    }

    /// Clicking the cat dismisses whatever it is holding up. The bubble itself is
    /// deliberately NOT clickable — it lives in the transparent margin, and making it
    /// interactive would put a click-swallowing rectangle over the user's work. (On
    /// Windows it could not be clickable even if we wanted it: the layered window
    /// hit-tests the composed alpha, and the margin around the bubble is empty.)
    ///
    /// Never consumes the click: dismissing a note is a side effect of petting the
    /// cat, not a reason to stop whoever else wanted the gesture.
    public bool MouseDown(Pt point)
    {
        if (_transientText is not null) _transientText = null;
        else if (_pinnedText is not null) Pin(null);
        return false;
    }

    private void Present(string? text)
    {
        if (_atlas.Bubble is not { } bubble || string.IsNullOrEmpty(text))
        {
            _view.SetAux("bubble", null, Pt.Zero);
            _bus.LogDemo("bubble   cleared");
            return;
        }

        if (!_cache.TryGetValue(text, out var rendered))
        {
            if (bubble.Render(text) is not { } made) return;
            _cache[text] = made;
            rendered = made;
        }

        var origin = bubble.Origin(rendered);
        _view.SetAux("bubble", rendered.Image, origin);
        _bus.LogDemo($"bubble   \"{text}\" {rendered.Image.Width}x{rendered.Image.Height}px " +
                     $"at atlas ({(int)origin.X}, {(int)origin.Y})");
    }
}
