using System.Runtime.Versioning;
using System.Text.Json;

namespace LoafCat;

/// The tuning table from `cat.json`.
///
/// Modules read every threshold, duration and gain they use from here rather than
/// declaring it, which is what makes rule 1 ("no behaviour constant in code") true of
/// features and not only of geometry. Retuning the cat is then a JSON diff, and a
/// theme can ship a lazier or a twitchier one without a rebuild.
///
/// Keyed by module id and then by constant name — `behaviour.drag.hold_time`,
/// `behaviour.hunt.trigger` — which groups a feature's tuning the way the generator
/// writes it and the way a theme would override it.
public sealed class Behaviour
{
    private readonly Dictionary<string, Dictionary<string, double>> _values = new();
    private readonly HashSet<string> _warned = new();

    public Behaviour(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) return;
        foreach (var module in raw.EnumerateObject())
        {
            if (module.Value.ValueKind != JsonValueKind.Object) continue;
            var parsed = new Dictionary<string, double>();
            foreach (var constant in module.Value.EnumerateObject())
            {
                // Value by value rather than one whole-object cast: a theme that
                // writes an integer where a float was expected must not silently
                // drop a module's entire tuning block.
                if (constant.Value.ValueKind == JsonValueKind.Number &&
                    constant.Value.TryGetDouble(out double d))
                {
                    parsed[constant.Name] = d;
                }
            }
            _values[module.Name] = parsed;
        }
    }

    /// One constant, or null when this theme does not carry it.
    public double? Value(string module, string key) =>
        _values.TryGetValue(module, out var consts) && consts.TryGetValue(key, out double v)
            ? v
            : null;

    /// A REQUIRED constant, addressed as `"module.key"`.
    ///
    /// A missing key means the theme's art predates the module asking for it. Warn
    /// loudly, once — a silent zero would make the cat behave bizarrely for a reason
    /// nobody could find. Callers read these in `Retune`, once per theme, so the
    /// string split never happens on the 120Hz path.
    public double F(string dotted)
    {
        int dot = dotted.IndexOf('.');
        if (dot > 0)
        {
            double? v = Value(dotted[..dot], dotted[(dot + 1)..]);
            if (v is { } found) return found;
        }
        if (_warned.Add(dotted))
        {
            Log.Warn($"atlas: behaviour key '{dotted}' missing — rerun tools/generate_art.py");
        }
        return 0;
    }
}

/// The atlas is the contract between the art pipeline and the runtime.
///
/// Everything the cat knows about its own body comes from `cat.json` — part
/// rectangles, draw order, pivots, eye geometry, palette. No geometry is hard-coded
/// here, which is what lets the art be regenerated or swapped for a community theme
/// without touching a line of code — and it is the reason this Windows port reads the
/// SAME files the macOS app does rather than a converted copy of them.
public sealed class Atlas
{
    public sealed class Part
    {
        public required string Name { get; init; }
        public required PixelBitmap Image { get; init; }
        /// Position of this part's top-left within the logical canvas.
        public required Pt Origin { get; init; }
        public required Sz Size { get; init; }
    }

    public required double Canvas { get; init; }
    public required List<string> Order { get; init; }
    public required Dictionary<string, Part> Parts { get; init; }
    public required Dictionary<string, Pt> Pivots { get; init; }

    /// Transparent margin the window carries around the cat, in logical pixels, so a
    /// speech bubble has somewhere to live. Symmetric, which is what keeps the
    /// window's centre and the cat's centre the same point.
    public readonly record struct LayoutInfo(int PadX, int PadY);
    public required LayoutInfo Layout { get; init; }

    /// Absent when a theme hides the bubble; the runtime then never shows one.
    public SpeechBubble? Bubble { get; init; }

    /// Timing and staging for the wellness modules. In the atlas rather than in code
    /// for the same reason the pivots are: it is the cat's behaviour, and a theme or
    /// a port should be able to change it without a compiler.
    public sealed class WellnessInfo
    {
        public double GrowDuration = 0.4;
        public double StretchDuration = 3.0;
        public double RestoreDelay = 0.2;
        public double ScreenFraction = 0.9;
        public Rgba Tint = new(127, 199, 154, 255);
        public double TintPeak = 0.55;
        public double TintReleaseAt = 0.7;
        public List<string> TintParts = [];
        public double BobHeight = 3;
        public double FlourishDuration = 0.8;
        public double AwaySeconds = 600;
        public double TimerRight = -4;
        public double TimerCY = 26;
    }
    public required WellnessInfo Wellness { get; init; }

    /// Everything drawn above the cat that is not part of its body: the status glyphs
    /// (thinking dots, sparkles, sweat drop, exclamation mark) and the reaction
    /// sprites (steam, hearts).
    ///
    /// Deliberately a separate table from `Parts`, and absent from `Order`, so an
    /// overlay enters neither the draw stack nor the click-through hit mask — a
    /// thought bubble must never make an empty corner pixel clickable.
    public sealed class Overlay
    {
        public required Part Part { get; init; }
        /// How many of this sprite may be on screen at once.
        public required int Slots { get; init; }
        /// The body part whose offset this sprite rides, if any. A status glyph
        /// pinned to the head drifts with the head turn instead of sitting rigidly in
        /// the corner; steam and hearts carry their own motion and follow nothing.
        /// Named in `cat.json`, so which is which is not a decision made in code.
        public string? Follow { get; init; }
    }
    public required Dictionary<string, Overlay> Overlays { get; init; }

    /// Which glyph sequence plays for which named state, and how fast.
    public required Dictionary<string, OverlayAnimation> OverlayAnimations { get; init; }

    /// Keyframed whole-body motion: the celebratory hop, the error slump and the two
    /// looping ambiences. In the atlas rather than in code so a theme can restyle the
    /// reaction set without a rebuild.
    public required Dictionary<string, Animation> Animations { get; init; }

    /// A keyframed whole-body animation. Offsets are logical pixels, y-DOWN like
    /// every other coordinate in the atlas; squash is a multiplier where 1.0 is
    /// neutral and below 1.0 is compressed.
    public sealed class Animation
    {
        public required double Duration { get; init; }
        public required bool Loop { get; init; }
        public required List<(double T, Pt P)> Offset { get; init; }
        public required List<(double T, double V)> Squash { get; init; }

        /// Linear interpolation between keyframes. Linear rather than eased because
        /// the easing is already baked into the keyframe spacing — the hop's keys
        /// bunch up at the apex, which is where the motion slows.
        public (Pt Offset, double Squash) Sample(double time)
        {
            double t = Loop && Duration > 0 ? time % Duration : Math.Min(time, Duration);

            var p = Pt.Zero;
            if (Offset.Count > 0)
            {
                p = Offset[0].P;
                for (int i = 1; i < Offset.Count; i++)
                {
                    if (Offset[i].T < t) continue;
                    var a = Offset[i - 1];
                    var b = Offset[i];
                    double span = b.T - a.T;
                    double u = span > 0 ? (t - a.T) / span : 0;
                    p = new Pt(a.P.X + (b.P.X - a.P.X) * u, a.P.Y + (b.P.Y - a.P.Y) * u);
                    break;
                }
                if (t >= Offset[^1].T) p = Offset[^1].P;
            }

            double s = 1;
            if (Squash.Count > 0)
            {
                s = Squash[0].V;
                for (int i = 1; i < Squash.Count; i++)
                {
                    if (Squash[i].T < t) continue;
                    var a = Squash[i - 1];
                    var b = Squash[i];
                    double span = b.T - a.T;
                    double u = span > 0 ? (t - a.T) / span : 0;
                    s = a.V + (b.V - a.V) * u;
                    break;
                }
                if (t >= Squash[^1].T) s = Squash[^1].V;
            }
            return (p, s);
        }
    }

    /// A flipbook over overlay parts.
    public sealed class OverlayAnimation
    {
        public required List<string> Frames { get; init; }
        public required double Fps { get; init; }
        public required bool Loop { get; init; }

        public string? Frame(double time)
        {
            if (Frames.Count == 0) return null;
            if (Fps <= 0) return Frames[0];
            int i = (int)(time * Fps);
            if (Loop) return Frames[((i % Frames.Count) + Frames.Count) % Frames.Count];
            return Frames[MathX.Clamp(i, 0, Frames.Count - 1)];
        }
    }

    /// Base part names that ship an `<name>_hot` overheat variant — the same pixels
    /// with the coat palette remapped, so the two crop identically and can be
    /// cross-faded in place.
    public required HashSet<string> HotParts { get; init; }

    /// Alternative part sets that REPLACE the cat rather than move it, keyed by pose
    /// name and listed in draw order.
    ///
    /// The peek pose is the reason this exists. A cat looking round a screen edge is
    /// a side-on drawing — one eye, one near ear, the muzzle leading — and no
    /// arrangement of the front-facing parts is that drawing. Sliding the standing
    /// cat behind the edge bisects its face, and rotating it 90° (which is lossless
    /// on a pixel grid, so it was tried) reads as a cat that has fallen over.
    ///
    /// While a pose is active the view draws these parts and no others, which is what
    /// lets a pose be a different drawing rather than a rearrangement.
    public required Dictionary<string, List<string>> Poses { get; init; }

    /// Every part belonging to any pose. Hidden unless its own pose is the one
    /// running, so the peek head does not sit on top of the standing cat.
    public required HashSet<string> PosedParts { get; init; }

    /// Eye geometry, needed for pupil tracking. `MaxOffset` is how far a pupil may
    /// travel from centre before it would clip out of the sclera.
    public sealed class EyeInfo
    {
        public required double ScleraRadius { get; init; }
        public required double PupilRadius { get; init; }
        public required double MaxOffset { get; init; }
        public required Dictionary<string, Pt> Centers { get; init; }
    }
    public required EyeInfo Eye { get; init; }

    /// Every threshold, duration and magnitude the modules run on, keyed by module id
    /// then by constant name. The atlas does not interpret any of it.
    public required Behaviour Behaviour { get; init; }

    /// One tuning constant, with the fallback used when a theme does not override it.
    public double Tune(string module, string key, double fallback) =>
        Behaviour.Value(module, key) ?? fallback;

    public sealed class LoadException(string message) : Exception(message);

    [SupportedOSPlatform("windows")]
    public static Atlas Load(string dir)
    {
        string jsonPath = Path.Combine(dir, "cat.json");
        if (!File.Exists(jsonPath)) throw new LoadException($"atlas: missing file {jsonPath}");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllBytes(jsonPath));
        }
        catch (JsonException e)
        {
            throw new LoadException($"atlas: cat.json is not valid JSON — {e.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("canvas", out var canvasEl) ||
                !root.TryGetProperty("order", out var orderEl) ||
                !root.TryGetProperty("parts", out var partsEl))
            {
                throw new LoadException("atlas: cat.json is missing canvas/order/parts");
            }

            var order = new List<string>();
            foreach (var e in orderEl.EnumerateArray())
            {
                if (e.GetString() is { } s) order.Add(s);
            }

            var parts = new Dictionary<string, Part>();
            foreach (var p in partsEl.EnumerateObject())
            {
                parts[p.Name] = LoadPart(p.Name, p.Value, dir);
            }

            // Overlays are optional: a theme generated before status glyphs existed
            // still loads, it just never shows one.
            var overlays = new Dictionary<string, Overlay>();
            var overlayAnims = new Dictionary<string, OverlayAnimation>();
            if (root.TryGetProperty("overlays", out var ov) &&
                ov.ValueKind == JsonValueKind.Object)
            {
                if (ov.TryGetProperty("parts", out var ovParts) &&
                    ovParts.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in ovParts.EnumerateObject())
                    {
                        overlays[p.Name] = new Overlay
                        {
                            Part = LoadPart(p.Name, p.Value, dir),
                            Slots = Math.Max(Json.Int(p.Value, "slots", 1), 1),
                            Follow = Json.Str(p.Value, "follow"),
                        };
                    }
                }
                if (ov.TryGetProperty("anims", out var ovAnims) &&
                    ovAnims.ValueKind == JsonValueKind.Object)
                {
                    foreach (var a in ovAnims.EnumerateObject())
                    {
                        var frames = new List<string>();
                        if (a.Value.TryGetProperty("frames", out var fr) &&
                            fr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var f in fr.EnumerateArray())
                            {
                                if (f.GetString() is { } s) frames.Add(s);
                            }
                        }
                        overlayAnims[a.Name] = new OverlayAnimation
                        {
                            Frames = frames,
                            Fps = Json.Num(a.Value, "fps", 4),
                            Loop = Json.Bool(a.Value, "loop", true),
                        };
                    }
                }
            }

            var animations = new Dictionary<string, Animation>();
            if (root.TryGetProperty("anim", out var animEl) &&
                animEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var a in animEl.EnumerateObject())
                {
                    var offs = new List<(double, Pt)>();
                    if (a.Value.TryGetProperty("offset", out var offEl) &&
                        offEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var k in offEl.EnumerateArray())
                        {
                            var v = Json.Doubles(k);
                            if (v.Count == 3) offs.Add((v[0], new Pt(v[1], v[2])));
                        }
                    }
                    var sq = new List<(double, double)>();
                    if (a.Value.TryGetProperty("squash", out var sqEl) &&
                        sqEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var k in sqEl.EnumerateArray())
                        {
                            var v = Json.Doubles(k);
                            if (v.Count == 2) sq.Add((v[0], v[1]));
                        }
                    }
                    offs.Sort((x, y) => x.Item1.CompareTo(y.Item1));
                    sq.Sort((x, y) => x.Item1.CompareTo(y.Item1));
                    animations[a.Name] = new Animation
                    {
                        Duration = Json.Num(a.Value, "duration", 0),
                        Loop = Json.Bool(a.Value, "loop", false),
                        Offset = offs,
                        Squash = sq,
                    };
                }
            }

            var pivots = new Dictionary<string, Pt>();
            if (root.TryGetProperty("pivots", out var pivEl) &&
                pivEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in pivEl.EnumerateObject())
                {
                    var v = Json.Doubles(p.Value);
                    if (v.Count == 2) pivots[p.Name] = new Pt(v[0], v[1]);
                }
            }

            root.TryGetProperty("eye", out var eyeEl);
            var centers = new Dictionary<string, Pt>();
            if (eyeEl.ValueKind == JsonValueKind.Object &&
                eyeEl.TryGetProperty("centers", out var cEl) &&
                cEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var c in cEl.EnumerateObject())
                {
                    var v = Json.Doubles(c.Value);
                    if (v.Count == 2) centers[c.Name] = new Pt(v[0], v[1]);
                }
            }
            var eye = new EyeInfo
            {
                ScleraRadius = Json.Num(eyeEl, "sclera_r", 4),
                PupilRadius = Json.Num(eyeEl, "pupil_r", 3),
                MaxOffset = Json.Num(eyeEl, "max_offset", 1),
                Centers = centers,
            };

            root.TryGetProperty("layout", out var layoutEl);
            var layout = new LayoutInfo(
                Json.Int(layoutEl, "pad_x", 0), Json.Int(layoutEl, "pad_y", 0));

            // Only advertise a hot variant whose art actually loaded, or the view
            // would build a cross-fade layer for an image that is not there.
            var hot = new HashSet<string>();
            if (root.TryGetProperty("hot", out var hotEl) &&
                hotEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in hotEl.EnumerateArray())
                {
                    if (h.GetString() is { } s && parts.ContainsKey($"{s}_hot")) hot.Add(s);
                }
            }

            // Same guard as the hot variants: only advertise a pose part whose art
            // actually loaded. A theme that drops the whiskers drops `peek_r_face`
            // with them, and a pose naming a part the atlas does not carry would be
            // a hole in the cat rather than an error anyone would see.
            var poses = new Dictionary<string, List<string>>();
            if (root.TryGetProperty("poses", out var posesEl) &&
                posesEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in posesEl.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.Array) continue;
                    var list = new List<string>();
                    foreach (var n in p.Value.EnumerateArray())
                    {
                        if (n.GetString() is { } s && parts.ContainsKey(s)) list.Add(s);
                    }
                    poses[p.Name] = list;
                }
            }
            var posed = new HashSet<string>(poses.Values.SelectMany(v => v));

            root.TryGetProperty("behaviour", out var behaviourEl);

            return new Atlas
            {
                Canvas = canvasEl.GetDouble(),
                Order = order,
                Parts = parts,
                Pivots = pivots,
                Layout = layout,
                Bubble = LoadBubble(root, dir),
                Wellness = LoadWellness(root),
                Overlays = overlays,
                OverlayAnimations = overlayAnims,
                Animations = animations,
                HotParts = hot,
                Poses = poses,
                PosedParts = posed,
                Eye = eye,
                Behaviour = new Behaviour(behaviourEl),
            };
        }
    }

    [SupportedOSPlatform("windows")]
    private static Part LoadPart(string name, JsonElement def, string dir)
    {
        string? file = Json.Str(def, "file");
        if (file is null ||
            !def.TryGetProperty("x", out var xE) || !def.TryGetProperty("y", out var yE) ||
            !def.TryGetProperty("w", out var wE) || !def.TryGetProperty("h", out var hE))
        {
            throw new LoadException($"atlas: part {name} has a malformed entry");
        }

        string path = Path.Combine(dir, file.Replace('/', Path.DirectorySeparatorChar));
        var img = PixelBitmap.Load(path)
            ?? throw new LoadException($"atlas: missing file {path}");

        return new Part
        {
            Name = name,
            Image = img,
            Origin = new Pt(xE.GetDouble(), yE.GetDouble()),
            Size = new Sz(wE.GetDouble(), hE.GetDouble()),
        };
    }

    [SupportedOSPlatform("windows")]
    private static SpeechBubble? LoadBubble(JsonElement root, string dir)
    {
        if (!root.TryGetProperty("bubble", out var b) || b.ValueKind != JsonValueKind.Object)
            return null;
        if (!root.TryGetProperty("font", out var f) || f.ValueKind != JsonValueKind.Object)
            return null;

        string? sheetFile = Json.Str(f, "file");
        if (sheetFile is null) return null;
        var sheet = PixelBitmap.Load(Path.Combine(dir, sheetFile.Replace('/', Path.DirectorySeparatorChar)));
        if (sheet is null) return null;

        if (!f.TryGetProperty("glyphs", out var glyphDefs) ||
            glyphDefs.ValueKind != JsonValueKind.Object) return null;
        if (!b.TryGetProperty("slices", out var sliceDefs) ||
            sliceDefs.ValueKind != JsonValueKind.Object) return null;
        if (!b.TryGetProperty("tail", out var tailDef) ||
            tailDef.ValueKind != JsonValueKind.Object) return null;

        string? tailFile = Json.Str(tailDef, "file");
        if (tailFile is null) return null;
        var tail = PixelBitmap.Load(Path.Combine(dir, tailFile.Replace('/', Path.DirectorySeparatorChar)));
        if (tail is null) return null;

        var glyphs = new Dictionary<char, PixelFont.Glyph>();
        foreach (var g in glyphDefs.EnumerateObject())
        {
            if (g.Name.Length != 1) continue;
            if (!g.Value.TryGetProperty("x", out var gx)) continue;
            if (!g.Value.TryGetProperty("w", out var gw)) continue;
            glyphs[g.Name[0]] = new PixelFont.Glyph(gx.GetInt32(), gw.GetInt32());
        }

        string fallbackStr = Json.Str(f, "fallback") ?? "?";
        var font = new PixelFont
        {
            Sheet = sheet,
            CellHeight = Json.Int(f, "cell_h", 8),
            Baseline = Json.Int(f, "baseline", 6),
            Tracking = Json.Int(f, "tracking", 1),
            SpaceWidth = Json.Int(f, "space", 3),
            LineGap = Json.Int(f, "line_gap", 1),
            Fallback = fallbackStr.Length > 0 ? fallbackStr[0] : '?',
            Glyphs = glyphs,
        };

        var slices = new Dictionary<string, PixelBitmap>();
        foreach (var s in sliceDefs.EnumerateObject())
        {
            string? file = Json.Str(s.Value, "file");
            if (file is null) return null;
            var img = PixelBitmap.Load(Path.Combine(dir, file.Replace('/', Path.DirectorySeparatorChar)));
            if (img is null) return null;
            slices[s.Name] = img;
        }

        var pad = b.TryGetProperty("text_pad", out var padEl) ? Json.Doubles(padEl) : [];
        var anchor = b.TryGetProperty("anchor", out var anchorEl) ? Json.Doubles(anchorEl) : [];

        return new SpeechBubble
        {
            Corner = Json.Int(b, "corner", 3),
            Slices = slices,
            Tail = tail,
            TailOverlap = Json.Int(tailDef, "overlap", 1),
            TailTipX = Json.Int(tailDef, "tip_x", tail.Width / 2),
            PadX = pad.Count > 0 ? (int)pad[0] : 4,
            PadY = pad.Count > 1 ? (int)pad[1] : 3,
            LineGap = Json.Int(b, "line_gap", 1),
            MaxWidth = Json.Int(b, "max_width", 96),
            MinWidth = Json.Int(b, "min_width", 7),
            MaxLines = Json.Int(b, "max_lines", 3),
            Anchor = new Pt(anchor.Count > 0 ? anchor[0] : 24, anchor.Count > 1 ? anchor[1] : 1),
            Gap = Json.Int(b, "gap", 1),
            TextColor = Rgba.FromHex(Json.Str(b, "text_color")) ?? new Rgba(40, 40, 44, 255),
            Font = font,
        };
    }

    private static WellnessInfo LoadWellness(JsonElement root)
    {
        var w = new WellnessInfo();
        if (!root.TryGetProperty("wellness", out var d) || d.ValueKind != JsonValueKind.Object)
            return w;

        w.GrowDuration = Json.Num(d, "grow_duration", w.GrowDuration);
        w.StretchDuration = Json.Num(d, "stretch_duration", w.StretchDuration);
        w.RestoreDelay = Json.Num(d, "restore_delay", w.RestoreDelay);
        w.ScreenFraction = Json.Num(d, "screen_fraction", w.ScreenFraction);
        if (Rgba.FromHex(Json.Str(d, "tint")) is { } tint) w.Tint = tint;
        w.TintPeak = Json.Num(d, "tint_peak", w.TintPeak);
        w.TintReleaseAt = Json.Num(d, "tint_release_at", w.TintReleaseAt);
        if (d.TryGetProperty("tint_parts", out var tp) && tp.ValueKind == JsonValueKind.Array)
        {
            w.TintParts = [];
            foreach (var e in tp.EnumerateArray())
            {
                if (e.GetString() is { } s) w.TintParts.Add(s);
            }
        }
        w.BobHeight = Json.Num(d, "bob_height", w.BobHeight);
        w.FlourishDuration = Json.Num(d, "flourish_duration", w.FlourishDuration);
        w.AwaySeconds = Json.Num(d, "away_seconds", w.AwaySeconds);
        if (d.TryGetProperty("timer", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            w.TimerRight = Json.Num(t, "right", w.TimerRight);
            w.TimerCY = Json.Num(t, "cy", w.TimerCY);
        }
        return w;
    }

    /// Pivot for a part, defaulting to its centre when the atlas does not name one.
    public Pt Pivot(string name)
    {
        if (Pivots.TryGetValue(name, out var p)) return p;
        if (!Parts.TryGetValue(name, out var part)) return Pt.Zero;
        return new Pt(part.Origin.X + part.Size.W / 2, part.Origin.Y + part.Size.H / 2);
    }
}

/// Small readers over `JsonElement`, so the loader above reads like the Swift one
/// rather than like three lines of `TryGetProperty` per field.
internal static class Json
{
    public static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static double Num(JsonElement e, string key, double fallback) =>
        e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetDouble(out double d)
            ? d
            : fallback;

    public static int Int(JsonElement e, string key, int fallback) =>
        e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetInt32(out int i)
            ? i
            : fallback;

    public static bool Bool(JsonElement e, string key, bool fallback)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v))
            return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    public static List<double> Doubles(JsonElement e)
    {
        var outv = new List<double>();
        if (e.ValueKind != JsonValueKind.Array) return outv;
        foreach (var v in e.EnumerateArray())
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)) outv.Add(d);
        }
        return outv;
    }
}
