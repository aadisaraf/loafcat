import AppKit
import QuartzCore

/// Composites the rig into one CALayer per body part.
///
/// A layer per part (rather than redrawing a canvas each frame) means the GPU does
/// the compositing and our per-frame work is just setting ~15 positions. It also
/// makes the pixel-art rules easy to hold: nearest-neighbour magnification, integer
/// scale factor, and every position rounded to a whole logical pixel before it
/// reaches Core Animation.
final class CatView: NSView {
    private let atlas: Atlas
    private let rig: Rig
    private var layers: [String: CALayer] = [:]

    /// Integer only. A fractional scale is the fastest way to make pixel art look
    /// like mush, and it cannot be fixed downstream.
    let scale: CGFloat

    /// Alpha mask of the composited silhouette, in logical pixels, dilated for
    /// hysteresis. Indexed every poll tick to decide click-through, so it must be a
    /// flat array lookup and never an image sample.
    private(set) var hitMask: [Bool]
    private let maskDilation = 6

    init(atlas: Atlas, rig: Rig, scale: CGFloat) {
        self.atlas = atlas
        self.rig = rig
        self.scale = scale
        let side = Int(atlas.canvas)
        self.hitMask = [Bool](repeating: false, count: side * side)

        super.init(frame: NSRect(
            x: 0, y: 0, width: atlas.canvas * scale, height: atlas.canvas * scale))

        wantsLayer = true
        layer?.masksToBounds = false

        buildLayers()
        buildHitMask()
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    private func buildLayers() {
        for name in atlas.order {
            guard let part = atlas.parts[name] else { continue }
            let l = CALayer()
            l.contents = part.image
            l.magnificationFilter = .nearest   // crisp pixels, never smoothed
            l.minificationFilter = .nearest
            l.contentsGravity = .resize
            l.anchorPoint = .zero
            l.bounds = CGRect(
                x: 0, y: 0,
                width: part.size.width * scale, height: part.size.height * scale)
            l.position = viewPosition(for: part, offset: .zero)
            l.actions = ["position": NSNull(), "bounds": NSNull(),
                         "opacity": NSNull(), "hidden": NSNull(), "transform": NSNull()]
            layer?.addSublayer(l)
            layers[name] = l
        }
    }

    /// Rasterises the default-pose silhouette once, then dilates it.
    ///
    /// Dilation is what makes click-through feel solid: we become interactive a few
    /// pixels *before* the cursor reaches the cat, so a click at the boundary is
    /// already ours. Measured in the S1 spike as the difference between 88% and 97%.
    private func buildHitMask() {
        let side = Int(atlas.canvas)
        var raw = [Bool](repeating: false, count: side * side)

        for name in atlas.order where !name.hasPrefix("lid_") && name != "shadow" {
            guard let part = atlas.parts[name] else { continue }
            let w = Int(part.size.width), h = Int(part.size.height)
            guard w > 0, h > 0 else { continue }

            var buf = [UInt8](repeating: 0, count: w * h * 4)
            guard let ctx = CGContext(
                data: &buf, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
            else { continue }
            ctx.draw(part.image, in: CGRect(x: 0, y: 0, width: w, height: h))

            for py in 0..<h {
                for pxi in 0..<w {
                    // CGContext origin is bottom-left; the atlas is top-left.
                    let alpha = buf[((h - 1 - py) * w + pxi) * 4 + 3]
                    guard alpha > 40 else { continue }
                    let gx = Int(part.origin.x) + pxi
                    let gy = Int(part.origin.y) + py
                    if gx >= 0, gx < side, gy >= 0, gy < side { raw[gy * side + gx] = true }
                }
            }
        }

        // Square dilation. Cheap, and at this size indistinguishable from circular.
        var out = raw
        for y in 0..<side {
            for x in 0..<side where !raw[y * side + x] {
                var near = false
                var dy = -maskDilation
                while dy <= maskDilation && !near {
                    var dx = -maskDilation
                    while dx <= maskDilation && !near {
                        let ny = y + dy, nx = x + dx
                        if ny >= 0, ny < side, nx >= 0, nx < side, raw[ny * side + nx] {
                            near = true
                        }
                        dx += 1
                    }
                    dy += 1
                }
                if near { out[y * side + x] = true }
            }
        }
        hitMask = out
    }

    /// True when a point in view coordinates lands on (or near) the cat.
    func isOnCat(viewPoint: CGPoint) -> Bool {
        let side = Int(atlas.canvas)
        let lx = Int(viewPoint.x / scale)
        // View coords are y-up; the mask is y-down.
        let ly = side - 1 - Int(viewPoint.y / scale)
        guard lx >= 0, lx < side, ly >= 0, ly < side else { return false }
        return hitMask[ly * side + lx]
    }

    /// Atlas coordinates are y-down from the top-left; AppKit view coordinates are
    /// y-up from the bottom-left. Converting here, once, keeps every other file able
    /// to think purely in atlas space.
    private func viewPosition(for part: Atlas.Part, offset: CGPoint) -> CGPoint {
        let ax = (part.origin.x + offset.x).rounded()
        let ay = (part.origin.y + offset.y).rounded()
        let flippedY = atlas.canvas - ay - part.size.height
        return CGPoint(x: ax * scale, y: flippedY * scale)
    }

    /// Pushes the rig's transforms onto the layers. Called once per frame.
    func sync() {
        CATransaction.begin()
        CATransaction.setDisableActions(true)   // no implicit animation; we drive it
        for (name, l) in layers {
            guard let part = atlas.parts[name] else { continue }
            let t = rig.transforms[name] ?? Rig.Transform()
            l.isHidden = t.hidden

            // Rounding happens inside viewPosition, on LOGICAL pixels before
            // scaling. Rounding after scaling would still land on fractional
            // logical positions and make the art crawl at 2x/3x.
            l.position = viewPosition(for: part, offset: t.offset)

            if t.scale.width != 1 || t.scale.height != 1 {
                let pivot = atlas.pivot(for: name)
                let px = (pivot.x - part.origin.x) * scale
                let py = (part.origin.y + part.size.height - pivot.y) * scale
                var m = CATransform3DIdentity
                m = CATransform3DTranslate(m, px, py, 0)
                m = CATransform3DScale(m, t.scale.width, t.scale.height, 1)
                m = CATransform3DTranslate(m, -px, -py, 0)
                l.transform = m
            } else if !CATransform3DIsIdentity(l.transform) {
                l.transform = CATransform3DIdentity
            }
        }
        CATransaction.commit()
    }
}
