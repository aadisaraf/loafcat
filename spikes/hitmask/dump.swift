import AppKit

// Dumps what the Swift bubble compositor actually produces, so it can be compared
// against tools/generate_art.py's preview_bubble(). Two independent implementations
// of the same layout: if the PNGs differ, one of them is wrong.
//
// Build: swiftc -o /tmp/bubbledump spikes/hitmask/dump.swift \
//          Sources/LoafCat/{Atlas,CatView,Rig,PixelCanvas,SpeechBubble}.swift

_ = NSApplication.shared

let root = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
let theme = CommandLine.arguments.dropFirst().first ?? "mono"
let atlas = try! Atlas.load(from: root.appendingPathComponent("assets/themes/\(theme)"))
guard let bubble = atlas.bubble else { fatalError("theme has no bubble") }

func write(_ bmp: PixelBitmap, _ name: String, scale: Int = 4) {
    guard let cg = bmp.cgImage() else { return }
    let big = NSImage(size: NSSize(width: bmp.width * scale, height: bmp.height * scale))
    big.lockFocus()
    NSGraphicsContext.current?.imageInterpolation = .none
    NSGraphicsContext.current?.cgContext.draw(
        cg, in: CGRect(x: 0, y: 0, width: bmp.width * scale, height: bmp.height * scale))
    big.unlockFocus()
    let tiff = big.tiffRepresentation!
    let rep = NSBitmapImageRep(data: tiff)!
    try! rep.representation(using: .png, properties: [:])!
        .write(to: URL(fileURLWithPath: "/tmp/\(name).png"))
    print("/tmp/\(name).png  \(bmp.width)x\(bmp.height)")
}

func ascii(_ bmp: PixelBitmap, _ title: String) {
    print("--- \(title) \(bmp.width)x\(bmp.height)")
    for y in 0..<bmp.height {
        var row = ""
        for x in 0..<bmp.width {
            let c = bmp[x, y]
            row += c.a == 0 ? "." : (Int(c.r) + Int(c.g) + Int(c.b) < 400 ? "#" : "o")
        }
        print(String(format: "%2d %@", y, row))
    }
}

if let r = bubble.render("Time to stretch! Stand up and roll your shoulders.") {
    write(r.image, "swift-bubble")
    print("tipOffset \(r.tipOffset)  origin \(bubble.origin(for: r))")
    ascii(r.image, "bubble as composed (row 0 should be the TOP outline)")
}
if let tail = bubble.slices["tl"] { ascii(tail, "slice tl") }
ascii(bubble.tail, "tail")
if let r = bubble.render("25:00 focus", withTail: false) {
    write(r.image, "swift-plate")
}

// Is the hit mask the right way up? The cat is narrow at the top (ear tips) and
// wide across the head, so the row-occupancy profile is asymmetric enough to tell.
do {
    let view = CatView(atlas: atlas, rig: Rig(atlas: atlas), scale: 2)
    let side = Int(atlas.canvas)
    print("--- hitMask row occupancy (atlas y-down; ears at the top)")
    for y in 0..<side {
        let n = (0..<side).filter { view.hitMask[y * side + $0] }.count
        print(String(format: "%2d %@ %d", y, String(repeating: "#", count: n), n))
    }
}

// Also dump a body part through PixelBitmap, to prove the PNG loader keeps row 0
// at the top the way the atlas assumes.
if let head = atlas.parts["head"],
   let bmp = PixelBitmap(cgImage: head.image) {
    var probe = PixelBitmap(width: bmp.width, height: bmp.height)
    for y in 0..<bmp.height {
        for x in 0..<bmp.width where bmp[x, y].a > 0 {
            probe[x, y] = RGBA(r: 255, g: 0, b: 0, a: 255)
        }
    }
    // Top row vs bottom row occupancy: an ear-less head crop is wider at the top.
    func filled(_ row: Int) -> Int {
        (0..<bmp.width).filter { bmp[$0, row].a > 0 }.count
    }
    print("head \(bmp.width)x\(bmp.height) filled row0=\(filled(0)) " +
          "rowLast=\(filled(bmp.height - 1))")
    write(probe, "swift-head-silhouette", scale: 4)
}
