namespace LoafCat;

/// One overlay sprite, placed for one frame.
///
/// `Offset` is in LOGICAL pixels from the sprite's anchor in the atlas, y-down, and
/// is rounded to a whole logical pixel by the view before scaling — the same path
/// every body part takes, so an overlay cannot be the thing that crawls.
public struct OverlayInstance(string part, Pt offset, double alpha)
{
    public string Part = part;
    public Pt Offset = offset;
    public double Alpha = alpha;
}

/// The one-frame mailbox between the modules and the rig, and the clock for the
/// keyframed animations they ask to play.
///
/// `ModuleOutput` carries what the coordinator has to *arbitrate* — a requested
/// state, a squash, a whole-cat offset. This carries the two things that need no
/// arbitration: what a module wants to do to individual parts (offsets simply add),
/// and which named animation it wants running (only one module drives each).
///
/// It is a shared mailbox rather than a reference each module holds because both the
/// rig and the view are rebuilt when the theme or the scale changes: a module that
/// captured either at registration would spend the rest of the session driving a dead
/// object. Publishing through here means modules never hold anything that can go
/// stale, and it is why registering a feature stays a single line in `Program.cs`.
///
/// UI thread only. Modules may take input on a background thread — the agent listener
/// does — but everything below is touched from `Update`, which is the tick.
public sealed class CatStage
{
    public static readonly CatStage Shared = new();

    private CatStage() { }

    // --- published by the runtime, read by modules --------------------------

    /// The atlas currently on screen. Modules read their geometry and their tuning
    /// constants from it; nothing about the cat's body or behaviour is declared in C#.
    public Atlas? Atlas { get; private set; }

    /// Bumped every time a new atlas is published, so a module knows to re-read the
    /// constants it cached. Cheaper than a dictionary lookup per constant per frame,
    /// and correct across a theme switch.
    public int AtlasGeneration { get; private set; }

    /// The winning state as of the PREVIOUS frame. Modules read it to yield to direct
    /// manipulation ("stop purring the instant a drag starts") without having to know
    /// which module owns dragging.
    public CatState State { get; private set; } = CatState.Idle;

    public void Publish(Atlas atlas)
    {
        Atlas = atlas;
        AtlasGeneration++;
    }

    // --- written by modules, read by the rig and the view -------------------

    /// Whole-cat offset. Written by the registry from the summed `ModuleOutput`s,
    /// never by a module directly, so there is exactly one way to move the cat.
    public Pt BodyOffset { get; private set; } = Pt.Zero;

    /// Offset for the head and everything parented to it — ears, face, eyes, pupils,
    /// lids. A module that moved `head` alone would tear the face off.
    ///
    /// Fields rather than properties, all the way down: modules write
    /// `stage.HeadOffset.X += …`, which does not compile against a property.
    public Pt HeadOffset = Pt.Zero;

    public Pt PawOffsetL = Pt.Zero;
    public Pt PawOffsetR = Pt.Zero;
    public Pt TailOffset = Pt.Zero;

    /// 0 = normal coat, 1 = fully overheated. The view cross-fades the `_hot`
    /// palette-remapped variant of each coat part by this much.
    public double Heat;

    /// Every sprite the modules want drawn above the cat this frame — steam, hearts,
    /// and the agent's status glyph, which resolves itself through `OverlayFrame()`
    /// and posts here like anything else. One list, so the view has one overlay path.
    public readonly List<OverlayInstance> Overlays = [];

    /// Free-form numbers for `--debug-state`. Never read by the rig or the view, so a
    /// module can publish whatever makes its behaviour checkable from a log.
    /// Deliberately not cleared per frame: a metric keeps its last value, which is
    /// what makes a 10Hz log readable.
    public readonly Dictionary<string, double> Metrics = [];

    public void BeginFrame()
    {
        BodyOffset = Pt.Zero;
        DiscardPartChannels();
    }

    /// Throws away everything the modules posted about individual parts.
    ///
    /// Called at the top of every frame, and again when the winning module asked to
    /// be `Exclusive` — that flag promises the winner is the ONLY thing affecting the
    /// cat this frame, and a leftover paw offset or a puff of steam would break the
    /// promise just as visibly as a leftover squash would. The corollary is that an
    /// exclusive module must express itself entirely through `ModuleOutput`.
    public void DiscardPartChannels()
    {
        HeadOffset = Pt.Zero;
        PawOffsetL = Pt.Zero;
        PawOffsetR = Pt.Zero;
        TailOffset = Pt.Zero;
        Heat = 0;
        Overlays.Clear();
    }

    public void EndFrame(CatState state, Pt bodyOffset)
    {
        State = state;
        BodyOffset = bodyOffset;
    }

    public void Metric(string key, double value) => Metrics[key] = value;

    // --- the keyframe clock -------------------------------------------------
    // Whole-body reactions authored as keyframes in `cat.json` rather than computed
    // per frame: the celebratory hop, the error slump, the thinking bob. The clock
    // lives here for the same reason the mailbox does — it has to survive the rig and
    // the view being rebuilt underneath a running animation.

    private string? _current;
    private double _startedAt;
    private string? _overlayName;

    /// How long a named animation runs, straight from the atlas. Modules ask this
    /// rather than hardcoding a duration, so "how long is the hop" has exactly one
    /// answer and it is in `cat.json`.
    public double Duration(string name) =>
        Atlas is { } a && a.Animations.TryGetValue(name, out var anim) ? anim.Duration : 0;

    /// Asks for an animation and its overlay flipbook. Restarting only on a *change*
    /// is what makes this callable every frame — a module can state what it wants 120
    /// times a second without ever resetting the clock.
    public void Request(string? name, string? overlay)
    {
        if (name != _current)
        {
            _current = name;
            _startedAt = Clock.Now;
        }
        _overlayName = overlay;
    }

    /// Whole-body offset (logical px, y-down) and squash for this instant.
    public (Pt Offset, double Squash) Sample()
    {
        if (_current is null || Atlas is not { } a ||
            !a.Animations.TryGetValue(_current, out var anim))
        {
            return (Pt.Zero, 1.0);
        }
        return anim.Sample(Clock.Now - _startedAt);
    }

    /// The overlay SPRITE to draw right now, resolved through the flipbook, or null.
    public string? OverlayFrame()
    {
        if (_overlayName is null || Atlas is not { } a ||
            !a.OverlayAnimations.TryGetValue(_overlayName, out var anim))
        {
            return null;
        }
        return anim.Frame(Clock.Now - _startedAt);
    }
}

/// Boilerplate for the one thing every module has to get right: its constants come
/// from `cat.json`, and `cat.json` changes when the user picks another cat.
///
/// Caching them and re-reading on a generation bump keeps the 120Hz path free of
/// dictionary lookups while staying correct across a theme switch.
public interface IAtlasTuned
{
    int TunedGeneration { get; set; }

    /// Re-read every constant. Called once at startup and again on a theme change.
    void Retune(Atlas atlas);
}

public static class AtlasTunedExtensions
{
    /// The live atlas, retuned first if the theme changed. `null` before the first
    /// atlas is published, which no module should assume cannot happen.
    public static Atlas? TunedAtlas(this IAtlasTuned self)
    {
        var stage = CatStage.Shared;
        if (stage.Atlas is not { } atlas) return null;
        if (self.TunedGeneration != stage.AtlasGeneration)
        {
            self.TunedGeneration = stage.AtlasGeneration;
            self.Retune(atlas);
        }
        return atlas;
    }
}
