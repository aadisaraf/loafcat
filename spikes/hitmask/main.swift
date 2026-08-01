import AppKit

// Regression check for the click-through hit mask after the panel grew.
//
// The panel used to be exactly the cat; it is now the cat plus a transparent
// margin for the speech bubble, and the stretch break magnifies it to fill the
// screen. `isOnCat` is the single input to `ignoresMouseEvents`, so if it is wrong
// by even a few pixels the app either swallows clicks meant for the window below
// or stops responding to its own cat -- and both look like "nothing happened".
//
// Drives the SAME CatView the app uses, over the exact geometry main.swift feeds
// it: viewPoint = mouseLocation - panel.frame.origin.
//
// Build: swiftc -o /tmp/hitmask spikes/hitmask/main.swift \
//          Sources/LoafCat/{Atlas,CatStage,CatModule,CatView,Rig,PixelCanvas,SpeechBubble}.swift

// AppKit needs a window-server connection before NSView will behave.
_ = NSApplication.shared

let root = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
let theme = CommandLine.arguments.dropFirst().first ?? "mono"
let atlas = try! Atlas.load(from: root.appendingPathComponent("assets/themes/\(theme)"))

var failures = 0
func check(_ name: String, _ condition: Bool, _ detail: String = "") {
    print("  \(condition ? "ok  " : "FAIL") \(name)\(detail.isEmpty ? "" : "  — \(detail)")")
    if !condition { failures += 1 }
}

/// Every point of the cat canvas, asked the way the app asks it.
/// Returns (hits, mismatchesAgainstTheRawMask).
func sweep(_ view: CatView, size: NSSize, zoom: CGFloat) -> (Int, Int, Int) {
    let side = Int(atlas.canvas)
    let s = view.scale * zoom
    var hits = 0, mismatches = 0, outside = 0

    // Walk the whole window in one-point steps, not just the cat, so anything the
    // padding made clickable by accident shows up.
    var y = 0.5
    while y < size.height {
        var x = 0.5
        while x < size.width {
            let onCat = view.isOnCat(viewPoint: CGPoint(x: x, y: y))
            // Reference: convert to cat-canvas coords independently of CatView.
            let lx = Int(floor((x - size.width / 2) / s + atlas.canvas / 2))
            let lyUp = (y - size.height / 2) / s + atlas.canvas / 2
            let ly = side - 1 - Int(floor(lyUp))
            let inCanvas = lx >= 0 && lx < side && ly >= 0 && ly < side
            let expect = inCanvas ? view.hitMask[ly * side + lx] : false
            if onCat { hits += 1 } else if !inCanvas { outside += 1 }
            if onCat != expect { mismatches += 1 }
            x += 1
        }
        y += 1
    }
    return (hits, mismatches, outside)
}

print("theme \(theme): canvas \(Int(atlas.canvas)), pad \(atlas.layout.padX)x\(atlas.layout.padY)")

for scale in [CGFloat(2), 3, 4] {
    let rig = Rig(atlas: atlas)
    let view = CatView(atlas: atlas, rig: rig, scale: scale)
    let size = CatView.panelSize(atlas: atlas, scale: scale)
    view.setFrameSize(size)
    print("\n@\(Int(scale))x  panel \(Int(size.width))x\(Int(size.height))pt")

    let (hits, mismatch, _) = sweep(view, size: size, zoom: 1)
    check("mask agrees with an independent conversion", mismatch == 0,
          "\(mismatch) mismatched points")
    check("the cat is clickable", hits > 0, "\(hits) interactive points")

    // The whole point of the padding: it must be click-through.
    let corners = [
        CGPoint(x: 2, y: 2), CGPoint(x: size.width - 2, y: 2),
        CGPoint(x: 2, y: size.height - 2),
        CGPoint(x: size.width - 2, y: size.height - 2),
        // Where the bubble sits: dead centre of the top margin.
        CGPoint(x: size.width / 2, y: size.height - CGFloat(atlas.layout.padY) * scale / 2),
    ]
    check("the bubble margin passes clicks through",
          corners.allSatisfy { !view.isOnCat(viewPoint: $0) })

    // Dead centre of the cat is always the cat.
    check("the cat's centre is interactive",
          view.isOnCat(viewPoint: CGPoint(x: size.width / 2, y: size.height / 2)))

    // The real regression check: reproduce the PRE-PADDING formula from the
    // original CatView and require the new one to agree on every point, once the
    // margin is added to the coordinate. If these ever diverge, click-through has
    // moved relative to the cat and the spike S1 result no longer holds.
    var drift = 0
    let side = Int(atlas.canvas)
    let catSide = atlas.canvas * scale
    var oy = 0.5
    while oy < catSide {
        var ox = 0.5
        while ox < catSide {
            let lx = Int(ox / scale)
            let ly = side - 1 - Int(oy / scale)
            let old = lx >= 0 && lx < side && ly >= 0 && ly < side
                ? view.hitMask[ly * side + lx] : false
            let new = view.isOnCat(viewPoint: CGPoint(
                x: ox + CGFloat(atlas.layout.padX) * scale,
                y: oy + CGFloat(atlas.layout.padY) * scale))
            if old != new { drift += 1 }
            ox += 1
        }
        oy += 1
    }
    check("identical to the pre-padding formula", drift == 0, "\(drift) points moved")

    // The interactive region must be the SAME cat-relative shape at every scale,
    // which is the property a padded window is most likely to break.
    let catPoints = hits
    let expectedCells = view.hitMask.filter { $0 }.count
    let ratio = Double(catPoints) / (Double(expectedCells) * Double(scale * scale))
    check("interactive area tracks the scale", abs(ratio - 1) < 0.06,
          String(format: "%.3f of the mask's area", ratio))

    // Now the stretch break: magnified, in a window sized to the cat alone.
    for zoom in [CGFloat(4), 9] {
        let grown = NSSize(width: atlas.canvas * scale * zoom,
                           height: atlas.canvas * scale * zoom)
        view.setZoom(zoom)
        view.setFrameSize(grown)
        let (h2, m2, _) = sweep(view, size: grown, zoom: zoom)
        check("zoom \(Int(zoom))x: mask still agrees", m2 == 0, "\(m2) mismatched points")
        let ratio2 = Double(h2) / (Double(expectedCells) * Double(scale * zoom) * Double(scale * zoom))
        check("zoom \(Int(zoom))x: interactive area scales", abs(ratio2 - 1) < 0.06,
              String(format: "%.3f", ratio2))
    }

    // Back to rest, exactly as StretchBreakModule.finish() does it.
    view.setZoom(1)
    view.setFrameSize(size)
    let (h3, m3, _) = sweep(view, size: size, zoom: 1)
    check("restored after zoom", m3 == 0 && h3 == hits,
          "\(h3) vs \(hits) interactive points")
}

// ---------------------------------------------------------------------------
// Shimmer: 60 seconds of idle at 120Hz, checking that every layer lands on a whole
// LOGICAL pixel. Crawl is not something you can reliably see in a screenshot diff
// -- breathing and blinking change the picture legitimately -- but it has exactly
// one cause, and that cause is checkable: a position that is not a whole multiple
// of the render scale.
print("\nshimmer: 60s idle at 120Hz")
for scale in [CGFloat(2), 3, 4] {
    let rig = Rig(atlas: atlas)
    let view = CatView(atlas: atlas, rig: rig, scale: scale)
    view.setFrameSize(CatView.panelSize(atlas: atlas, scale: scale))
    if let b = atlas.bubble, let r = b.render("Water break!"),
       let cg = r.image.cgImage() {
        view.setAux("bubble", image: cg, atlasOrigin: b.origin(for: r),
                    size: CGSize(width: r.image.width, height: r.image.height))
    }

    var offenders: [String: CGPoint] = [:]
    let dt: CGFloat = 1.0 / 120.0
    var t: CGFloat = 0
    for frame in 0..<(120 * 60) {
        t += dt
        // A slowly wandering cursor, so the springs never settle and every
        // intermediate value gets exercised rather than just the resting pose.
        let cursor = CGPoint(x: sin(t * 0.7) * 260, y: cos(t * 0.43) * 190)
        rig.setSquash(1 + sin(t * 1.9) * 0.09)
        rig.update(dt: dt, cursor: cursor)
        view.sync()
        if frame % 7 != 0 { continue }      // sampling; positions are deterministic
        for (name, p) in view.debugLayerPositions() {
            let onGrid = (p.x / scale) == (p.x / scale).rounded()
                && (p.y / scale) == (p.y / scale).rounded()
            if !onGrid { offenders[name] = p }
        }
    }
    check("@\(Int(scale))x every layer on a whole logical pixel", offenders.isEmpty,
          offenders.isEmpty ? "" : "\(offenders)")
}

print(failures == 0 ? "\nPASS" : "\n\(failures) FAILURES")
exit(failures == 0 ? 0 : 1)
