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

    /// Eye geometry, needed for pupil tracking. `maxOffset` is how far a pupil may
    /// travel from centre before it would clip out of the sclera.
    struct Eye {
        let scleraRadius: CGFloat
        let pupilRadius: CGFloat
        let maxOffset: CGFloat
        let centers: [String: CGPoint]
    }
    let eye: Eye

    /// Per-module behaviour tuning, keyed by module id then by constant name.
    ///
    /// The atlas does not interpret any of it — it is a passthrough so that a
    /// module's timings and magnitudes live in `cat.json` beside the geometry they
    /// act on, rather than as literals in Swift. A theme with a heavier cat can
    /// therefore hang and swing differently without a recompile.
    let behaviour: [String: [String: CGFloat]]

    /// One tuning constant, with the fallback used when a theme does not override it.
    func tune(_ module: String, _ key: String, _ fallback: CGFloat) -> CGFloat {
        behaviour[module]?[key] ?? fallback
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

        // Parsed value by value rather than with one big cast: JSONSerialization
        // hands back NSNumber, and a whole-dictionary cast to [String: Double]
        // fails silently for any theme that writes an integer where a float was
        // expected — which would drop a module's whole tuning block without a word.
        var behaviour: [String: [String: CGFloat]] = [:]
        for (module, consts) in (root["behaviour"] as? [String: [String: Any]] ?? [:]) {
            var parsed: [String: CGFloat] = [:]
            for (key, value) in consts {
                if let n = value as? NSNumber { parsed[key] = CGFloat(n.doubleValue) }
            }
            behaviour[module] = parsed
        }

        return Atlas(
            canvas: canvas, order: order, parts: parts, pivots: pivots, eye: eye,
            behaviour: behaviour)
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
