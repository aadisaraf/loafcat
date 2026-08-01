import AppKit

/// The tuning table from `cat.json`.
///
/// Modules read every threshold, duration and gain they use from here rather than
/// declaring it, which is what makes rule 1 ("no behaviour constant in Swift") true
/// of features and not only of geometry. Retuning the cat is then a JSON diff, and a
/// theme can ship a lazier or a twitchier one without a rebuild.
///
/// A class, not a struct, only so the missing-key warning can dedupe without a
/// global.
final class Behaviour {
    private let values: [String: CGFloat]
    private var warned = Set<String>()

    init(_ raw: [String: Any]) {
        var v: [String: CGFloat] = [:]
        for (k, n) in raw {
            if let d = n as? Double { v[k] = CGFloat(d) }
        }
        values = v
    }

    /// A missing key means the theme's art predates the module asking for it. Warn
    /// loudly, once — a silent zero would make the cat behave bizarrely for a reason
    /// nobody could find.
    func f(_ key: String) -> CGFloat {
        if let v = values[key] { return v }
        if warned.insert(key).inserted {
            FileHandle.standardError.write(
                "atlas: behaviour key '\(key)' missing — rerun tools/generate_art.py\n"
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

    /// Every threshold and duration the modules run on.
    let behaviour: Behaviour

    /// Base part names that ship an `<name>_hot` overheat variant — the same pixels
    /// with the coat palette remapped, so the two crop identically and can be
    /// cross-faded in place.
    let hotParts: Set<String>

    /// Overlay part name -> how many may be on screen at once. The view preallocates
    /// exactly this many layers, so an overlay never allocates during a frame.
    let overlays: [String: Int]

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

        var overlays: [String: Int] = [:]
        for (name, def) in (root["overlays"] as? [String: [String: Any]] ?? [:]) {
            // Only advertise an overlay whose art actually loaded, or the view would
            // build layers for an image that is not there.
            guard parts[name] != nil else { continue }
            overlays[name] = (def["slots"] as? Int) ?? 1
        }
        let hot = Set((root["hot"] as? [String] ?? []).filter { parts["\($0)_hot"] != nil })

        return Atlas(
            canvas: canvas, order: order, parts: parts, pivots: pivots,
            behaviour: Behaviour(root["behaviour"] as? [String: Any] ?? [:]),
            hotParts: hot, overlays: overlays, eye: eye)
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
