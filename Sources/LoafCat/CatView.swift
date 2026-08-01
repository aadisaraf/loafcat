import AppKit
import QuartzCore

/// Composites the rig into one CALayer per body part.
///
/// A layer per part (rather than redrawing a canvas each frame) means the GPU does
/// the compositing and our per-frame work is just setting ~15 positions. It also
/// makes the pixel-art rules easy to hold: nearest-neighbour magnification, integer
/// scale factor, and every position rounded to a whole logical pixel before it
/// reaches Core Animation.
///
/// The window is LARGER than the cat: `atlas.layout` adds a transparent margin so a
/// speech bubble and a countdown plate have somewhere to live. That margin is
/// symmetric on purpose — it keeps the view's centre and the cat's centre the same
/// point, so every cursor-relative calculation elsewhere is unaffected by it, and
/// the hit mask stays indexable in plain cat-canvas coordinates.
final class CatView: NSView {
    // Readable by modules: the view outlives a theme change and both of these are
    // replaced with it, so reaching them through the panel's content view is how a
    // module stays correct across a reload without main.swift re-wiring it.
    let atlas: Atlas
    let rig: Rig
    private var layers: [String: CALayer] = [:]
    private var tintLayers: [CALayer] = []
    private var auxLayers: [String: CALayer] = [:]

    /// Everything the cat is made of lives in here, anchored at its centre, so the
    /// stretch break can magnify the whole rig with one transform.
    private let container = CALayer()

    /// One layer per status glyph, all hidden until something asks for one. They
    /// are added after every body layer, so they always composite on top.
    private var overlayLayers: [String: CALayer] = [:]

    /// Integer only. A fractional scale is the fastest way to make pixel art look
    /// like mush, and it cannot be fixed downstream.
    let scale: CGFloat

    /// Transient magnification on top of `scale`, driven by the stretch break.
    /// Always lands on an integer *effective* scale at rest.
    private(set) var zoom: CGFloat = 1

    /// Logical-pixels-per-point actually in force this frame.
    var effectiveScale: CGFloat { scale * zoom }

    /// Alpha mask of the composited silhouette, in logical pixels, dilated for
    /// hysteresis. Indexed every poll tick to decide click-through, so it must be a
    /// flat array lookup and never an image sample. Still in CAT-canvas coordinates,
    /// unchanged by the padding — `isOnCat` does the conversion.
    private(set) var hitMask: [Bool]
    private let maskDilation = 6

    private var padX: CGFloat { CGFloat(atlas.layout.padX) }
    private var padY: CGFloat { CGFloat(atlas.layout.padY) }
    /// The padded canvas, in logical pixels.
    private var canvasW: CGFloat { atlas.canvas + padX * 2 }
    private var canvasH: CGFloat { atlas.canvas + padY * 2 }

    /// The window size this atlas and scale need. The cat itself is only
    /// `atlas.canvas * scale` of it; the rest is the bubble's margin.
    static func panelSize(atlas: Atlas, scale: CGFloat) -> NSSize {
        NSSize(width: (atlas.canvas + CGFloat(atlas.layout.padX) * 2) * scale,
               height: (atlas.canvas + CGFloat(atlas.layout.padY) * 2) * scale)
    }


    /// Where mouse events go. Set by whichever module handles them, so that
    /// main.swift does not have to know that dragging exists.
    weak var modules: ModuleRegistry?

    /// Screen position of the last mouse event, for computing drag deltas.
    /// Taken from the screen rather than the window because the window MOVES
    /// during a drag, which makes locationInWindow deltas meaningless.
    private var lastMouseScreen: CGPoint?
    init(atlas: Atlas, rig: Rig, scale: CGFloat) {
        self.atlas = atlas
        self.rig = rig
        self.scale = scale
        let side = Int(atlas.canvas)
        self.hitMask = [Bool](repeating: false, count: side * side)

        super.init(frame: NSRect(origin: .zero, size: Self.panelSize(atlas: atlas, scale: scale)))

        wantsLayer = true
        layer?.masksToBounds = false
        autoresizingMask = [.width, .height]

        container.anchorPoint = CGPoint(x: 0.5, y: 0.5)
        container.bounds = CGRect(x: 0, y: 0, width: canvasW * scale, height: canvasH * scale)
        container.masksToBounds = false
        container.actions = ["position": NSNull(), "bounds": NSNull(),
                             "transform": NSNull(), "sublayers": NSNull()]
        layer?.addSublayer(container)

        buildLayers()
        buildHitMask()
        recentre()
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    // MARK: - geometry

    /// The container's centre must track the view's, because that identity is what
    /// makes the cat stay centred when the stretch break resizes the window.
    private func recentre() {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        container.position = CGPoint(x: bounds.midX.rounded(), y: bounds.midY.rounded())
        CATransaction.commit()
    }

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        recentre()
    }

    override func layout() {
        super.layout()
        recentre()
    }

    /// Magnifies the whole rig about its own centre. `1` is rest.
    func setZoom(_ z: CGFloat) {
        guard z != zoom else { return }
        zoom = max(z, 0.01)
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        container.transform = CATransform3DMakeScale(zoom, zoom, 1)
        CATransaction.commit()
    }

    // MARK: - layers

    private func buildLayers() {
        for name in atlas.order {
            guard let part = atlas.parts[name] else { continue }
            let l = addLayer(for: part)
            layers[name] = l

            if atlas.wellness.tintParts.contains(name) {
                // A colour wash masked by the part's own alpha. Cheaper and more
                // predictable than a compositing filter, and — because the mask is
                // the same nearest-filtered bitmap — it cannot introduce a soft
                // edge the rest of the art does not have.
                let tint = CALayer()
                tint.anchorPoint = .zero
                tint.frame = CGRect(origin: .zero, size: l.bounds.size)
                tint.backgroundColor = atlas.wellness.tint.cgColor
                tint.opacity = 0
                tint.actions = ["opacity": NSNull(), "hidden": NSNull(),
                                "backgroundColor": NSNull()]
                let mask = CALayer()
                mask.anchorPoint = .zero
                mask.frame = tint.bounds
                mask.contents = part.image
                mask.magnificationFilter = .nearest
                mask.minificationFilter = .nearest
                mask.contentsGravity = .resize
                tint.mask = mask
                l.addSublayer(tint)
                tintLayers.append(tint)
            }
        }
        // Overlays last: draw order is sublayer order, and a thought bubble that
        // slid behind an ear would look like a rendering fault.
        for (name, part) in atlas.overlays.sorted(by: { $0.key < $1.key }) {
            let l = addLayer(for: part)
            l.isHidden = true
            overlayLayers[name] = l
        }
    }

    private func addLayer(for part: Atlas.Part) -> CALayer {
        let l = CALayer()
        l.contents = part.image
        l.magnificationFilter = .nearest   // crisp pixels, never smoothed
        l.minificationFilter = .nearest
        l.contentsGravity = .resize
        l.anchorPoint = .zero
        l.bounds = CGRect(
            x: 0, y: 0,
            width: part.size.width * scale, height: part.size.height * scale)
        l.position = containerPosition(for: part, offset: .zero)
        l.actions = ["position": NSNull(), "bounds": NSNull(),
                     "opacity": NSNull(), "hidden": NSNull(), "transform": NSNull()]
        container.addSublayer(l)
        return l
    }

    /// Fades the coat toward the atlas's calm colour. 0 is the cat's own colours.
    func setTint(_ amount: CGFloat) {
        let a = Float(min(max(amount, 0), 1))
        guard let first = tintLayers.first, first.opacity != a else { return }
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        for t in tintLayers { t.opacity = a }
        CATransaction.commit()
    }

    /// Places a 1x pixel bitmap in the canvas, above the cat. `atlasOrigin` is its
    /// top-left in CAT-canvas coordinates, y-down; negative values reach into the
    /// transparent margin, which is exactly where the bubble goes.
    ///
    /// Passing `nil` removes it.
    func setAux(_ key: String, image: CGImage?, atlasOrigin: CGPoint,
                size: CGSize, zPosition: CGFloat = 10) {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        defer { CATransaction.commit() }

        guard let image else {
            auxLayers.removeValue(forKey: key)?.removeFromSuperlayer()
            return
        }
        let l = auxLayers[key] ?? {
            let n = CALayer()
            n.anchorPoint = .zero
            n.magnificationFilter = .nearest
            n.minificationFilter = .nearest
            n.contentsGravity = .resize
            n.actions = ["position": NSNull(), "bounds": NSNull(),
                         "contents": NSNull(), "opacity": NSNull(), "hidden": NSNull()]
            container.addSublayer(n)
            auxLayers[key] = n
            return n
        }()
        l.zPosition = zPosition
        l.contents = image
        l.bounds = CGRect(x: 0, y: 0, width: size.width * scale, height: size.height * scale)
        l.position = canvasPosition(atlasX: atlasOrigin.x, atlasY: atlasOrigin.y,
                                    height: size.height)
    }

    func setAuxHidden(_ hidden: Bool) {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        for (_, l) in auxLayers { l.isHidden = hidden }
        CATransaction.commit()
    }

    /// Rasterises the default-pose silhouette once, then dilates it.
    ///
    /// Dilation is what makes click-through feel solid: we become interactive a few
    /// pixels *before* the cursor reaches the cat, so a click at the boundary is
    /// already ours. Measured in the S1 spike as the difference between 88% and 97%.
    ///
    /// Deliberately still in cat-canvas space and unaffected by the panel padding —
    /// the padding is empty, so a mask that covered it would only make transparent
    /// pixels clickable.
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
                    // A CGBitmapContext's user space is y-up, but its backing store
                    // is top-down: buffer row 0 is the image's TOP row, which is
                    // already what the atlas means by y. Measured, not assumed —
                    // see spikes/hitmask. This used to read (h - 1 - py), which
                    // mirrored every part inside its own crop box; the 6px dilation
                    // hid it on the round parts and got the ear tips wrong.
                    let alpha = buf[(py * w + pxi) * 4 + 3]
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
    ///
    /// Measured out from the view's CENTRE rather than its bottom-left, because the
    /// centre is the one point the padding and the stretch zoom both leave alone.
    /// Reduces to the old bottom-left formula exactly when padding is 0 and zoom 1.
    func isOnCat(viewPoint: CGPoint) -> Bool {
        return atlasPoint(viewPoint: viewPoint) != nil
    }

    /// Atlas coordinates are y-down from the top-left; AppKit view coordinates are
    /// y-up from the bottom-left. Converting here, once, keeps every other file able
    /// to think purely in atlas space.
    private func canvasPosition(atlasX: CGFloat, atlasY: CGFloat, height: CGFloat) -> CGPoint {
        // Round on LOGICAL pixels, then add the (integer) padding, then scale.
        // Rounding after scaling would still land on fractional logical positions
        // and make the art crawl at 2x/3x.
        let ax = atlasX.rounded() + padX
        let ay = atlasY.rounded() + padY
        let flippedY = canvasH - ay - height
        return CGPoint(x: ax * scale, y: flippedY * scale)
    }

    private func containerPosition(for part: Atlas.Part, offset: CGPoint) -> CGPoint {
        canvasPosition(atlasX: part.origin.x + offset.x,
                       atlasY: part.origin.y + offset.y,
                       height: part.size.height)
    }

    /// A point in view coordinates as ATLAS coordinates -- logical pixels, y-down --
    /// or nil when it is not on the cat. Modules reason in the same space as the rig
    /// and cat.json, and never have to know about scale, padding or zoom.
    func atlasPoint(viewPoint: CGPoint) -> CGPoint? {
        let side = Int(atlas.canvas)
        let sc = effectiveScale
        guard sc > 0 else { return nil }
        let half = atlas.canvas / 2
        let lx = Int((((viewPoint.x - bounds.midX) / sc) + half).rounded(.down))
        let lyUp = ((viewPoint.y - bounds.midY) / sc) + half
        let ly = side - 1 - Int(lyUp.rounded(.down))
        guard lx >= 0, lx < side, ly >= 0, ly < side else { return nil }
        guard hitMask[ly * side + lx] else { return nil }
        return CGPoint(x: CGFloat(lx), y: CGFloat(ly))
    }

    // MARK: - Mouse

    // The panel is non-activating, so without this the first click on the cat is
    // spent bringing the app forward instead of reaching the module.
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func mouseDown(with event: NSEvent) {
        lastMouseScreen = NSEvent.mouseLocation
        guard let p = atlasPoint(viewPoint: convert(event.locationInWindow, from: nil)),
              modules?.mouseDown(at: p) == true
        else { return }
    }

    override func mouseDragged(with event: NSEvent) {
        let now = NSEvent.mouseLocation
        defer { lastMouseScreen = now }
        guard let prev = lastMouseScreen else { return }
        // Logical pixels, y-down, to match the atlas convention every module uses.
        let sc = effectiveScale
        modules?.mouseDragged(by: CGPoint(
            x: (now.x - prev.x) / sc,
            y: -(now.y - prev.y) / sc))
    }

    override func mouseUp(with event: NSEvent) {
        lastMouseScreen = nil
        // Deliberately not gated on hit-testing: a release outside the silhouette
        // is still a release, and swallowing it would strand the cat mid-drag.
        let p = atlasPoint(viewPoint: convert(event.locationInWindow, from: nil))
        modules?.mouseUp(at: p ?? .zero)
    }

    /// Every layer's position in points, for the shimmer check in spikes/hitmask.
    /// If any of these is not an exact multiple of `scale`, a fractional logical
    /// offset has leaked in and the art will crawl at 2x and 3x.
    func debugLayerPositions() -> [String: CGPoint] {
        var out: [String: CGPoint] = [:]
        for (name, l) in layers { out[name] = l.position }
        for (name, l) in auxLayers { out["aux:" + name] = l.position }
        for (name, l) in overlayLayers { out["overlay:" + name] = l.position }
        out["#container"] = container.position
        return out
    }

    /// Moves the whole panel to follow the stage's keyframed offset.
    ///
    /// The hop has to move the *window*, not the layers: the cat fills the 48x48
    /// canvas edge to edge and the window is exactly the canvas, so a -26px leap
    /// drawn inside it would simply have its head cut off. Applied as a delta
    /// against a remembered value rather than as an absolute origin, so it
    /// composes with anything else that moves the panel (a drag, "Centre on
    /// screen") instead of fighting it.
    ///
    /// The contact shadow rides along, which is not what a shadow does. At this
    /// size the alternative — a shadow that stays behind while the body leaves —
    /// reads as two sprites rather than one leaping cat, so the rig sells the
    /// lift through the inverse shadow scale in `squash` instead.
    private func applyStageOffset() {
        guard let win = window else { return }
        let want = Stage.shared.sample().offset
        // Rounded on LOGICAL pixels before scaling, the same rule as every part
        // position. Rounding after the multiply still lands on fractional logical
        // pixels and makes the art crawl at 2x and 3x.
        let target = CGPoint(x: want.x.rounded() * scale, y: want.y.rounded() * scale)
        let applied = Stage.shared.appliedOffset
        let dx = target.x - applied.x
        // The atlas thinks y-down; the screen is y-up.
        let dy = -(target.y - applied.y)
        guard dx != 0 || dy != 0 else { return }
        win.setFrameOrigin(NSPoint(x: win.frame.origin.x + dx, y: win.frame.origin.y + dy))
        Stage.shared.appliedOffset = target
    }

    /// Shows at most one status glyph, carried by the head so it drifts with the
    /// head turn instead of sitting rigidly in the corner.
    private func syncOverlays() {
        let active = Stage.shared.overlayFrame()
        let headOffset = rig.transforms["head"]?.offset ?? .zero
        for (name, l) in overlayLayers {
            let on = (name == active)
            l.isHidden = !on
            guard on, let part = atlas.overlays[name] else { continue }
            l.position = containerPosition(for: part, offset: headOffset)
        }
    }

    /// Pushes the rig's transforms onto the layers. Called once per frame.
    func sync() {
        applyStageOffset()
        CATransaction.begin()
        CATransaction.setDisableActions(true)   // no implicit animation; we drive it
        for (name, l) in layers {
            guard let part = atlas.parts[name] else { continue }
            let t = rig.transforms[name] ?? Rig.Transform()
            l.isHidden = t.hidden

            l.position = containerPosition(for: part, offset: t.offset)

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
        syncOverlays()
        CATransaction.commit()
    }
}
