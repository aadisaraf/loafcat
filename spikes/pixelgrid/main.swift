import AppKit

// Checks the pixel-grid rule directly, on the thing the rule is actually about:
// every layer's position must be a whole number of LOGICAL pixels times the render
// scale. Anything else lands the art off the device grid, and at 2x or 3x that reads
// as pixels crawling.
//
// It walks `view.layer!.sublayers` after each of 60 simulated seconds' worth of
// frames, which covers every layer the compositor owns -- body parts, the overheat
// coat variants and the overlay slots alike -- without the check having to know
// which is which.
//
// Two routes were tried first and abandoned, both worth recording:
//
//   * Screen-grabbing the running app. Several cats from parallel sessions sit at
//     the same default position on this desktop, so every capture is a composite of
//     all of them.
//   * Rendering the layer tree offscreen and asserting every pixel is on-palette.
//     Sound in principle, but it measures Core Graphics' colour management and edge
//     anti-aliasing as much as it measures our geometry, and the rig's breathing is
//     a deliberately fractional scale that blends edges by design. Too noisy to
//     conclude anything from.
//
// Reproduce: ./spikes/pixelgrid/build.sh && ./spikes/pixelgrid/build/PixelGridSpike

let app = NSApplication.shared
app.setActivationPolicy(.accessory)

let root = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
var failures = 0

/// Every layer under `layer`, itself included. Depth-first, and deliberately
/// unaware of which layer is a body part, an overheat coat, an overlay slot or a
/// wellness tint — the invariant is the same for all of them.
func descendants(of layer: CALayer?) -> [CALayer] {
    guard let layer else { return [] }
    var out = [layer]
    for sub in layer.sublayers ?? [] { out += descendants(of: sub) }
    return out
}

for theme in ["mono", "tuxedo", "cream"] {
    let atlas = try! Atlas.load(from: root.appendingPathComponent("assets/themes/\(theme)"))

    for scale in [CGFloat(2), CGFloat(3), CGFloat(4)] {
        let rig = Rig(atlas: atlas)
        let view = CatView(atlas: atlas, rig: rig, scale: scale)
        // The panel the app actually builds: the cat plus the transparent margin
        // the atlas asks for. Sizing it to the bare canvas would test a window
        // geometry that no longer exists.
        view.frame = NSRect(origin: .zero, size: CatView.panelSize(atlas: atlas, scale: scale))

        let dt: CGFloat = 1.0 / 120.0
        let stage = CatStage.shared
        var offGrid = 0
        var worst: CGFloat = 0
        var checked = 0
        var minPos = CGFloat.greatestFiniteMagnitude
        var maxPos = -CGFloat.greatestFiniteMagnitude

        // 60 simulated seconds. The first half is a pure idle soak -- breathing,
        // blinking, tail sway and cursor tracking. The second half drives every
        // channel the reaction modules use, at deliberately awkward fractional
        // amplitudes, because a rounding bug hides completely at integer offsets.
        for i in 0..<(60 * 120) {
            let t = CGFloat(i) * dt
            stage.beginFrame()
            var squash: CGFloat = 1
            if t > 30 {
                let p = sin(t * 3.1) * 2.37 + 0.41       // never a whole number
                stage.headOffset = CGPoint(x: p, y: p * 0.6)
                stage.pawOffsetL = CGPoint(x: -p * 0.5, y: p)
                stage.pawOffsetR = CGPoint(x: p * 0.5, y: -p)
                stage.tailOffset = CGPoint(x: p * 0.3, y: 0)
                stage.heat = 0.5 + 0.5 * sin(t)          // both coats live at once
                stage.overlays = [
                    OverlayInstance(part: "heart", offset: CGPoint(x: p, y: -p * 3), alpha: 1),
                    OverlayInstance(part: "steam", offset: CGPoint(x: -p, y: p * 2), alpha: 1),
                ]
                squash = 1 + sin(t * 2.3) * 0.06
                stage.endFrame(state: .purring, bodyOffset: CGPoint(x: p * 0.7, y: -p))
            } else {
                stage.endFrame(state: .idle, bodyOffset: .zero)
            }
            rig.setSquash(squash)
            // A cursor well off to one side, so the tracking springs sit at full
            // travel rather than parked on zero where rounding is trivially right.
            rig.update(dt: dt, cursor: CGPoint(x: 260, y: -140))
            view.sync()

            // The WHOLE tree, not just the root's children: every part now lives
            // inside a centred container layer, so walking one level would check
            // the container's position and nothing else — and pass vacuously.
            for l in descendants(of: view.layer) where !l.isHidden {
                for v in [l.position.x, l.position.y] {
                    checked += 1
                    minPos = min(minPos, v)
                    maxPos = max(maxPos, v)
                    // The invariant: a whole number of logical pixels, scaled.
                    let err = abs((v / scale).rounded() - v / scale)
                    if err > 1e-9 { offGrid += 1; worst = max(worst, err) }
                }
                for v in [l.bounds.width, l.bounds.height] {
                    checked += 1
                    let err = abs((v / scale).rounded() - v / scale)
                    if err > 1e-9 { offGrid += 1; worst = max(worst, err) }
                }
            }
        }

        let ok = offGrid == 0
        if !ok { failures += 1 }
        print(String(
            format: "%@ %-7@@%dx  %d coordinates over 7200 frames, %d off-grid (worst %.6f lpx), range %.0f..%.0f pt",
            (ok ? "PASS" : "FAIL") as NSString, theme as NSString, Int(scale),
            checked, offGrid, Double(worst), Double(minPos), Double(maxPos)))
    }
}

// The one fractional transform in the pipeline is the squash/breathe SCALE on the
// body and the shadow, which is how the rig has always breathed and is applied about
// a pivot rather than by translation. Reported so nobody reads the PASS above as a
// claim that nothing anywhere is fractional.
print("""

note: `transform` on body/shadow is a deliberate fractional scale (breathing and
squash). Positions -- the thing that makes art crawl -- are exact.
""")
print(failures == 0 ? "pixel grid intact" : "\(failures) configuration(s) failed")
exit(failures == 0 ? 0 : 1)
