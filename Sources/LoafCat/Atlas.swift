import AppKit

/// The tuning table from `cat.json`.
///
/// Modules read every threshold, duration and gain they use from here rather than
/// declaring it, which is what makes rule 1 ("no behaviour constant in Swift") true
/// of features and not only of geometry. Retuning the cat is then a JSON diff, and a
/// theme can ship a lazier or a twitchier one without a rebuild.
///
/// Keyed by module id and then by constant name — `behaviour.drag.hold_time`,
/// `behaviour.hunt.trigger` — which groups a feature's tuning the way the generator
/// writes it and the way a theme would override it.
///
/// A class, not a struct, only so the missing-key warning can dedupe without a
/// global.
final class Behaviour {
    private let values: [String: [String: CGFloat]]
    private var warned = Set<String>()

    init(_ raw: [String: Any]) {
        // Parsed value by value rather than with one big cast: JSONSerialization
        // hands back NSNumber, and a whole-dictionary cast to [String: Double]
        // fails silently for any theme that writes an integer where a float was
        // expected — which would drop a module's whole tuning block without a word.
        var out: [String: [String: CGFloat]] = [:]
        for (module, consts) in (raw as? [String: [String: Any]] ?? [:]) {
            var parsed: [String: CGFloat] = [:]
            for (key, value) in consts {
                if let n = value as? NSNumber { parsed[key] = CGFloat(n.doubleValue) }
            }
            out[module] = parsed
        }
        values = out
    }

    /// One constant, or nil when this theme does not carry it.
    func value(_ module: String, _ key: String) -> CGFloat? { values[module]?[key] }

    /// A REQUIRED constant, addressed as `"module.key"`.
    ///
    /// A missing key means the theme's art predates the module asking for it. Warn
    /// loudly, once — a silent zero would make the cat behave bizarrely for a reason
    /// nobody could find. Callers read these in `retune`, once per theme, so the
    /// string split never happens on the 120Hz path.
    func f(_ dotted: String) -> CGFloat {
        let parts = dotted.split(separator: ".", maxSplits: 1)
        if parts.count == 2, let v = value(String(parts[0]), String(parts[1])) { return v }
        if warned.insert(dotted).inserted {
            FileHandle.standardError.write(
                "atlas: behaviour key '\(dotted)' missing — rerun tools/generate_art.py\n"
                    .data(using: .utf8)!)
        }
        return 0
    }
}

/// The atlas is the contract between the art pipeline and the runtime.
///
/// Everything the cat knows about its own body comes from `cat.json` — part
/// rectangles, draw order, pivots, eye geometry, palette. No geometry is hard-coded
/// in Swift, which is what lets the art be regenerated or swapped for a community
/// theme without touching a line of code, and what would let a future Windows app
/// reuse the same data.
struct Atlas {
    struct Part {
        let name: String
        let image: CGImage
        /// Position of this part's top-left within the logical canvas.
        let origin: CGPoint
        let size: CGSize
    }

    let canvas: CGFloat
    let order: [String]
    let parts: [String: Part]
    let pivots: [String: CGPoint]

    /// Transparent margin the window carries around the cat, in logical pixels, so
    /// a speech bubble has somewhere to live. Symmetric, which is what keeps the
    /// window's centre and the cat's centre the same point.
    struct Layout {
        let padX: Int
        let padY: Int
        static let none = Layout(padX: 0, padY: 0)
    }
    let layout: Layout

    /// Absent when a theme hides the bubble; the runtime then never shows one.
    let bubble: SpeechBubble?

    /// Timing and staging for the wellness modules. In the atlas rather than in
    /// Swift for the same reason the pivots are: it is the cat's behaviour, and a
    /// theme or a port should be able to change it without a compiler.
    struct Wellness {
        var growDuration: Double = 0.4
        var stretchDuration: Double = 3.0
        var restoreDelay: Double = 0.2
        var screenFraction: CGFloat = 0.9
        var tint = RGBA(r: 127, g: 199, b: 154, a: 255)
        var tintPeak: CGFloat = 0.55
        var tintReleaseAt: Double = 0.7
        var tintParts: [String] = []
        var bobHeight: CGFloat = 3
        var flourishDuration: Double = 0.8
        var awaySeconds: Double = 600
        var timerRight: CGFloat = -4
        var timerCY: CGFloat = 26
    }
    let wellness: Wellness

    /// Everything drawn above the cat that is not part of its body: the status
    /// glyphs (thinking dots, sparkles, sweat drop, exclamation mark) and the
    /// reaction sprites (steam, hearts).
    ///
    /// Deliberately a separate table from `parts`, and absent from `order`, so an
    /// overlay enters neither the draw stack nor the click-through hit mask — a
    /// thought bubble must never make an empty corner pixel clickable.
    struct Overlay {
        let part: Part
        /// How many of this sprite may be on screen at once. The view preallocates
        /// exactly this many layers, so an overlay never allocates during a frame.
        let slots: Int
        /// The body part whose offset this sprite rides, if any. A status glyph
        /// pinned to the head drifts with the head turn instead of sitting rigidly
        /// in the corner; steam and hearts carry their own motion and follow
        /// nothing. Named in `cat.json`, so which is which is not a Swift decision.
        let follow: String?
    }
    let overlays: [String: Overlay]

    /// Which glyph sequence plays for which named state, and how fast.
    let overlayAnimations: [String: OverlayAnimation]

    /// Keyframed whole-body motion: the celebratory hop, the error slump and the
    /// two looping ambiences. In the atlas rather than in Swift so a theme can
    /// restyle the reaction set without a rebuild.
    let animations: [String: Animation]

    /// A keyframed whole-body animation. Offsets are logical pixels, y-DOWN like
    /// every other coordinate in the atlas; squash is a multiplier where 1.0 is
    /// neutral and below 1.0 is compressed.
    struct Animation {
        let duration: Double
        let loop: Bool
        let offset: [(t: Double, p: CGPoint)]
        let squash: [(t: Double, v: CGFloat)]

        /// Linear interpolation between keyframes. Linear rather than eased
        /// because the easing is already baked into the keyframe spacing — the
        /// hop's keys bunch up at the apex, which is where the motion slows.
        func sample(at time: Double) -> (offset: CGPoint, squash: CGFloat) {
            let t = loop && duration > 0
                ? time.truncatingRemainder(dividingBy: duration)
                : min(time, duration)
            var p = CGPoint.zero
            if let first = offset.first {
                p = first.p
                for i in 1..<max(offset.count, 1) where offset[i].t >= t {
                    let a = offset[i - 1], b = offset[i]
                    let span = b.t - a.t
                    let u = span > 0 ? CGFloat((t - a.t) / span) : 0
                    p = CGPoint(x: a.p.x + (b.p.x - a.p.x) * u,
                                y: a.p.y + (b.p.y - a.p.y) * u)
                    break
                }
                if let last = offset.last, t >= last.t { p = last.p }
            }
            var s: CGFloat = 1
            if let first = squash.first {
                s = first.v
                for i in 1..<max(squash.count, 1) where squash[i].t >= t {
                    let a = squash[i - 1], b = squash[i]
                    let span = b.t - a.t
                    let u = span > 0 ? CGFloat((t - a.t) / span) : 0
                    s = a.v + (b.v - a.v) * u
                    break
                }
                if let last = squash.last, t >= last.t { s = last.v }
            }
            return (p, s)
        }
    }

    /// A flipbook over overlay parts.
    struct OverlayAnimation {
        let frames: [String]
        let fps: Double
        let loop: Bool

        func frame(at time: Double) -> String? {
            guard !frames.isEmpty, fps > 0 else { return frames.first }
            let i = Int(time * fps)
            if loop { return frames[((i % frames.count) + frames.count) % frames.count] }
            return frames[min(max(i, 0), frames.count - 1)]
        }
    }

    /// Base part names that ship an `<name>_hot` overheat variant — the same pixels
    /// with the coat palette remapped, so the two crop identically and can be
    /// cross-faded in place.
    let hotParts: Set<String>

    /// Alternative part sets that REPLACE the cat rather than move it, keyed by
    /// pose name and listed in draw order.
    ///
    /// The peek pose is the reason this exists. A cat looking round a screen edge
    /// is a side-on drawing — one eye, one near ear, the muzzle leading — and no
    /// arrangement of the front-facing parts is that drawing. Sliding the standing
    /// cat behind the edge bisects its face, and rotating it 90° (which is lossless
    /// on a pixel grid, so it was tried) reads as a cat that has fallen over.
    ///
    /// While a pose is active the view draws these parts and no others, which is
    /// what lets a pose be a different drawing rather than a rearrangement.
    let poses: [String: [String]]

    /// Every part belonging to any pose. Hidden unless its own pose is the one
    /// running, so the peek head does not sit on top of the standing cat.
    let posedParts: Set<String>

    /// Eye geometry, needed for pupil tracking. `maxOffset` is how far a pupil may
    /// travel from centre before it would clip out of the sclera.
    struct Eye {
        let scleraRadius: CGFloat
        let pupilRadius: CGFloat
        let maxOffset: CGFloat
        let centers: [String: CGPoint]
    }
    let eye: Eye

    /// Every threshold, duration and magnitude the modules run on, keyed by module
    /// id then by constant name.
    ///
    /// The atlas does not interpret any of it — it is a passthrough so that a
    /// module's timings and magnitudes live in `cat.json` beside the geometry they
    /// act on, rather than as literals in Swift. A theme with a heavier cat can
    /// therefore hang and swing differently without a recompile.
    let behaviour: Behaviour

    /// One tuning constant, with the fallback used when a theme does not override it.
    func tune(_ module: String, _ key: String, _ fallback: CGFloat) -> CGFloat {
        behaviour.value(module, key) ?? fallback
    }

    enum LoadError: Error, CustomStringConvertible {
        case missing(String)
        case badJSON(String)

        var description: String {
            switch self {
            case .missing(let p): return "atlas: missing file \(p)"
            case .badJSON(let m): return "atlas: \(m)"
            }
        }
    }

    static func load(from dir: URL) throws -> Atlas {
        let jsonURL = dir.appendingPathComponent("cat.json")
        guard let data = try? Data(contentsOf: jsonURL) else {
            throw LoadError.missing(jsonURL.path)
        }
        guard
            let root = try JSONSerialization.jsonObject(with: data) as? [String: Any],
            let canvas = root["canvas"] as? Double,
            let order = root["order"] as? [String],
            let partDefs = root["parts"] as? [String: [String: Any]]
        else {
            throw LoadError.badJSON("cat.json is missing canvas/order/parts")
        }

        var parts: [String: Part] = [:]
        for (name, def) in partDefs {
            parts[name] = try loadPart(named: name, def: def, in: dir)
        }

        // Overlays are optional: a theme generated before status glyphs existed
        // still loads, it just never shows one.
        var overlays: [String: Overlay] = [:]
        var overlayAnims: [String: OverlayAnimation] = [:]
        if let ov = root["overlays"] as? [String: Any] {
            for (name, def) in (ov["parts"] as? [String: [String: Any]] ?? [:]) {
                overlays[name] = Overlay(
                    part: try loadPart(named: name, def: def, in: dir),
                    slots: max(def["slots"] as? Int ?? 1, 1),
                    follow: def["follow"] as? String)
            }
            for (name, def) in (ov["anims"] as? [String: [String: Any]] ?? [:]) {
                overlayAnims[name] = OverlayAnimation(
                    frames: def["frames"] as? [String] ?? [],
                    fps: def["fps"] as? Double ?? 4,
                    loop: def["loop"] as? Bool ?? true)
            }
        }

        var animations: [String: Animation] = [:]
        for (name, def) in (root["anim"] as? [String: [String: Any]] ?? [:]) {
            let offs = (def["offset"] as? [[Double]] ?? []).compactMap {
                $0.count == 3 ? (t: $0[0], p: CGPoint(x: $0[1], y: $0[2])) : nil
            }
            let sq = (def["squash"] as? [[Double]] ?? []).compactMap {
                $0.count == 2 ? (t: $0[0], v: CGFloat($0[1])) : nil
            }
            animations[name] = Animation(
                duration: def["duration"] as? Double ?? 0,
                loop: def["loop"] as? Bool ?? false,
                offset: offs.sorted { $0.t < $1.t },
                squash: sq.sorted { $0.t < $1.t })
        }

        var pivots: [String: CGPoint] = [:]
        for (name, p) in (root["pivots"] as? [String: [Double]] ?? [:]) where p.count == 2 {
            pivots[name] = CGPoint(x: p[0], y: p[1])
        }

        let eyeDef = root["eye"] as? [String: Any] ?? [:]
        var centers: [String: CGPoint] = [:]
        for (k, v) in (eyeDef["centers"] as? [String: [Double]] ?? [:]) where v.count == 2 {
            centers[k] = CGPoint(x: v[0], y: v[1])
        }
        let eye = Eye(
            scleraRadius: eyeDef["sclera_r"] as? Double ?? 4,
            pupilRadius: eyeDef["pupil_r"] as? Double ?? 3,
            maxOffset: eyeDef["max_offset"] as? Double ?? 1,
            centers: centers)

        let layoutDef = root["layout"] as? [String: Any] ?? [:]
        let layout = Layout(
            padX: layoutDef["pad_x"] as? Int ?? 0,
            padY: layoutDef["pad_y"] as? Int ?? 0)

        // Only advertise a hot variant whose art actually loaded, or the view would
        // build a cross-fade layer for an image that is not there.
        let hot = Set((root["hot"] as? [String] ?? []).filter { parts["\($0)_hot"] != nil })

        // Same guard as the hot variants: only advertise a pose part whose art
        // actually loaded. A theme that drops the whiskers drops `peek_r_face` with
        // them, and a pose naming a part the atlas does not carry would be a hole
        // in the cat rather than an error anyone would see.
        var poses: [String: [String]] = [:]
        for (name, list) in (root["poses"] as? [String: [String]] ?? [:]) {
            poses[name] = list.filter { parts[$0] != nil }
        }
        let posed = Set(poses.values.flatMap { $0 })

        return Atlas(
            canvas: canvas, order: order, parts: parts, pivots: pivots,
            layout: layout,
            bubble: loadBubble(root, dir: dir),
            wellness: loadWellness(root),
            overlays: overlays, overlayAnimations: overlayAnims,
            animations: animations,
            hotParts: hot,
            poses: poses, posedParts: posed,
            eye: eye,
            behaviour: Behaviour(root["behaviour"] as? [String: Any] ?? [:]))
    }

    private static func loadPart(
        named name: String, def: [String: Any], in dir: URL
    ) throws -> Part {
        guard
            let file = def["file"] as? String,
            let x = def["x"] as? Double, let y = def["y"] as? Double,
            let w = def["w"] as? Double, let h = def["h"] as? Double
        else { throw LoadError.badJSON("part \(name) has a malformed entry") }

        let url = dir.appendingPathComponent(file)
        guard
            let src = CGImageSourceCreateWithURL(url as CFURL, nil),
            let img = CGImageSourceCreateImageAtIndex(src, 0, nil)
        else { throw LoadError.missing(url.path) }

        return Part(
            name: name, image: img,
            origin: CGPoint(x: x, y: y), size: CGSize(width: w, height: h))
    }

    private static func loadBubble(_ root: [String: Any], dir: URL) -> SpeechBubble? {
        guard
            let b = root["bubble"] as? [String: Any],
            let f = root["font"] as? [String: Any],
            let sheetFile = f["file"] as? String,
            let sheet = PixelBitmap(contentsOf: dir.appendingPathComponent(sheetFile)),
            let glyphDefs = f["glyphs"] as? [String: [String: Int]],
            let sliceDefs = b["slices"] as? [String: [String: Any]],
            let tailDef = b["tail"] as? [String: Any],
            let tailFile = tailDef["file"] as? String,
            let tail = PixelBitmap(contentsOf: dir.appendingPathComponent(tailFile))
        else { return nil }

        var glyphs: [Character: PixelFont.Glyph] = [:]
        for (k, v) in glyphDefs {
            guard let ch = k.first, k.count == 1,
                  let x = v["x"], let w = v["w"] else { continue }
            glyphs[ch] = PixelFont.Glyph(x: x, w: w)
        }
        let font = PixelFont(
            sheet: sheet,
            cellHeight: f["cell_h"] as? Int ?? 8,
            baseline: f["baseline"] as? Int ?? 6,
            tracking: f["tracking"] as? Int ?? 1,
            spaceWidth: f["space"] as? Int ?? 3,
            lineGap: f["line_gap"] as? Int ?? 1,
            fallback: (f["fallback"] as? String)?.first ?? "?",
            glyphs: glyphs)

        var slices: [String: PixelBitmap] = [:]
        for (name, def) in sliceDefs {
            guard let file = def["file"] as? String,
                  let img = PixelBitmap(contentsOf: dir.appendingPathComponent(file))
            else { return nil }
            slices[name] = img
        }

        let pad = b["text_pad"] as? [Int] ?? [4, 3]
        let anchor = b["anchor"] as? [Double] ?? [24, 1]
        return SpeechBubble(
            corner: b["corner"] as? Int ?? 3,
            slices: slices,
            tail: tail,
            tailOverlap: tailDef["overlap"] as? Int ?? 1,
            tailTipX: tailDef["tip_x"] as? Int ?? (tail.width / 2),
            padX: pad.first ?? 4,
            padY: pad.count > 1 ? pad[1] : 3,
            lineGap: b["line_gap"] as? Int ?? 1,
            maxWidth: b["max_width"] as? Int ?? 96,
            minWidth: b["min_width"] as? Int ?? 7,
            maxLines: b["max_lines"] as? Int ?? 3,
            anchor: CGPoint(x: anchor.first ?? 24, y: anchor.count > 1 ? anchor[1] : 1),
            gap: b["gap"] as? Int ?? 1,
            textColor: RGBA(hex: b["text_color"] as? String ?? "")
                ?? RGBA(r: 40, g: 40, b: 44, a: 255),
            font: font)
    }

    private static func loadWellness(_ root: [String: Any]) -> Wellness {
        var w = Wellness()
        guard let d = root["wellness"] as? [String: Any] else { return w }
        if let v = d["grow_duration"] as? Double { w.growDuration = v }
        if let v = d["stretch_duration"] as? Double { w.stretchDuration = v }
        if let v = d["restore_delay"] as? Double { w.restoreDelay = v }
        if let v = d["screen_fraction"] as? Double { w.screenFraction = CGFloat(v) }
        if let v = d["tint"] as? String, let c = RGBA(hex: v) { w.tint = c }
        if let v = d["tint_peak"] as? Double { w.tintPeak = CGFloat(v) }
        if let v = d["tint_release_at"] as? Double { w.tintReleaseAt = v }
        if let v = d["tint_parts"] as? [String] { w.tintParts = v }
        if let v = d["bob_height"] as? Double { w.bobHeight = CGFloat(v) }
        if let v = d["flourish_duration"] as? Double { w.flourishDuration = v }
        if let v = d["away_seconds"] as? Double { w.awaySeconds = v }
        if let t = d["timer"] as? [String: Any] {
            if let v = t["right"] as? Double { w.timerRight = CGFloat(v) }
            if let v = t["cy"] as? Double { w.timerCY = CGFloat(v) }
        }
        return w
    }

    /// Pivot for a part, defaulting to its centre when the atlas does not name one.
    func pivot(for name: String) -> CGPoint {
        if let p = pivots[name] { return p }
        guard let part = parts[name] else { return .zero }
        return CGPoint(
            x: part.origin.x + part.size.width / 2,
            y: part.origin.y + part.size.height / 2)
    }
}
