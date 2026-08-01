import AppKit

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

    /// Eye geometry, needed for pupil tracking. `maxOffset` is how far a pupil may
    /// travel from centre before it would clip out of the sclera.
    struct Eye {
        let scleraRadius: CGFloat
        let pupilRadius: CGFloat
        let maxOffset: CGFloat
        let centers: [String: CGPoint]
    }
    let eye: Eye

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

            parts[name] = Part(
                name: name, image: img,
                origin: CGPoint(x: x, y: y), size: CGSize(width: w, height: h))
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

        return Atlas(
            canvas: canvas, order: order, parts: parts, pivots: pivots,
            layout: layout,
            bubble: loadBubble(root, dir: dir),
            wellness: loadWellness(root),
            eye: eye)
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
