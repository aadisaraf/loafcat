import AppKit

/// The pixel font baked by `tools/generate_art.py`.
///
/// One strip PNG of white-on-transparent coverage plus per-glyph `{x, w}`. White
/// because it is a mask, not art: the runtime inks it in whatever colour the theme
/// asks for, so bubble text and countdown digits share one sheet.
struct PixelFont {
    struct Glyph { let x: Int; let w: Int }

    let sheet: PixelBitmap
    let cellHeight: Int
    let baseline: Int
    let tracking: Int
    let spaceWidth: Int
    let lineGap: Int
    let fallback: Character
    let glyphs: [Character: Glyph]

    func glyph(_ c: Character) -> Glyph? {
        if c == " " { return nil }
        return glyphs[c] ?? glyphs[fallback]
    }

    func advance(_ c: Character) -> Int {
        c == " " ? spaceWidth : (glyph(c)?.w ?? spaceWidth)
    }

    func width(of s: String) -> Int {
        guard !s.isEmpty else { return 0 }
        return s.reduce(0) { $0 + advance($1) } + tracking * (s.count - 1)
    }

    /// Greedy word wrap, hard-breaking any single word that cannot fit — a URL or a
    /// long word must never make the bubble wider than the panel it lives in.
    func wrap(_ text: String, maxWidth: Int) -> [String] {
        var lines: [String] = []
        for paragraph in text.split(separator: "\n", omittingEmptySubsequences: false) {
            var current = ""
            for word in paragraph.split(separator: " ") {
                var word = String(word)
                while width(of: word) > maxWidth && word.count > 1 {
                    var head = ""
                    for ch in word {
                        if width(of: head + String(ch)) > maxWidth { break }
                        head.append(ch)
                    }
                    if head.isEmpty { head = String(word.first!) }
                    if !current.isEmpty { lines.append(current); current = "" }
                    lines.append(head)
                    word = String(word.dropFirst(head.count))
                }
                let trial = current.isEmpty ? word : current + " " + word
                if width(of: trial) <= maxWidth || current.isEmpty {
                    current = trial
                } else {
                    lines.append(current)
                    current = word
                }
            }
            lines.append(current)
        }
        // A trailing empty line would add a blank row of padding for nothing.
        while lines.count > 1, lines.last?.isEmpty == true { lines.removeLast() }
        return lines.isEmpty ? [""] : lines
    }

    func draw(_ line: String, into bmp: inout PixelBitmap, x: Int, y: Int, color: RGBA) {
        var cx = x
        for ch in line {
            if let g = glyph(ch) {
                bmp.stamp(
                    mask: sheet,
                    from: CGRect(x: g.x, y: 0, width: g.w, height: cellHeight),
                    x: cx, y: y, color: color)
                cx += g.w + tracking
            } else {
                cx += spaceWidth + tracking
            }
        }
    }
}

/// The 9-slice speech bubble, assembled to fit its text.
///
/// Every piece is a whole number of logical pixels and every position below is an
/// `Int`, so there is no place for a fractional offset to enter. The bitmap this
/// produces is handed to a `CALayer` with nearest-neighbour magnification, which is
/// what keeps it crisp at 2x, 3x and 4x.
struct SpeechBubble {
    let corner: Int
    let slices: [String: PixelBitmap]
    let tail: PixelBitmap
    let tailOverlap: Int
    let tailTipX: Int
    let padX: Int
    let padY: Int
    let lineGap: Int
    let maxWidth: Int
    let minWidth: Int
    /// How many lines fit between the cat's ears and the top of the padded window.
    /// Derived by the art pipeline from the same padding the runtime uses, so a
    /// long note is truncated rather than drawn outside the panel and clipped.
    let maxLines: Int
    /// Cat-canvas point the tail tip should touch, and how far above it to sit.
    let anchor: CGPoint
    let gap: Int
    let textColor: RGBA
    let font: PixelFont

    struct Rendered {
        let image: PixelBitmap
        /// Top-left of `image` relative to the tail tip, in logical pixels.
        let tipOffset: CGPoint
    }

    /// `withTail: false` gives the plain plate the pomodoro countdown sits in.
    func render(_ text: String, withTail: Bool = true) -> Rendered? {
        guard let tl = slices["tl"], let t = slices["t"], let tr = slices["tr"],
              let l = slices["l"], let c = slices["c"], let r = slices["r"],
              let bl = slices["bl"], let b = slices["b"], let br = slices["br"]
        else { return nil }

        let limit = max(maxWidth - 2 * padX, font.spaceWidth)
        var lines = font.wrap(text, maxWidth: limit)
        if withTail && lines.count > maxLines && maxLines > 0 {
            lines = Array(lines.prefix(maxLines))
            var last = lines[maxLines - 1]
            while !last.isEmpty && font.width(of: last + "...") > limit {
                last.removeLast()
            }
            lines[maxLines - 1] = last + "..."
        }
        let textW = lines.map { font.width(of: $0) }.max() ?? 0
        let textH = lines.count * font.cellHeight + (lines.count - 1) * lineGap

        let w = max(textW + 2 * padX, minWidth)
        let h = max(textH + 2 * padY, 2 * corner + 1)
        let tailDrop = withTail ? tail.height - tailOverlap : 0

        var img = PixelBitmap(width: w, height: h + tailDrop)

        img.blit(tl, x: 0, y: 0)
        img.blit(tr, x: w - corner, y: 0)
        img.blit(bl, x: 0, y: h - corner)
        img.blit(br, x: w - corner, y: h - corner)
        if w > 2 * corner {
            for x in corner..<(w - corner) {
                img.blit(t, x: x, y: 0)
                img.blit(b, x: x, y: h - corner)
            }
        }
        if h > 2 * corner {
            for y in corner..<(h - corner) {
                img.blit(l, x: 0, y: y)
                img.blit(r, x: w - corner, y: y)
                if w > 2 * corner {
                    for x in corner..<(w - corner) { img.blit(c, x: x, y: y) }
                }
            }
        }

        var tipX = w / 2
        if withTail {
            let tx = (w - tail.width) / 2
            img.blit(tail, x: tx, y: h - tailOverlap)
            tipX = tx + tailTipX
        }

        for (i, line) in lines.enumerated() {
            // Centred, and rounded DOWN so the whole block stays on the grid — a
            // half-pixel here is exactly the kind of thing that makes text shimmer.
            let lx = padX + (textW - font.width(of: line)) / 2
            let ly = padY + i * (font.cellHeight + lineGap)
            font.draw(line, into: &img, x: lx, y: ly, color: textColor)
        }

        let tipY = img.height - 1
        return Rendered(image: img, tipOffset: CGPoint(x: -tipX, y: -tipY))
    }

    /// Where the bitmap's top-left goes, in cat-canvas coordinates (y-down, and
    /// negative above the cat, which is where the padding lives).
    func origin(for r: Rendered) -> CGPoint {
        CGPoint(x: (anchor.x + r.tipOffset.x).rounded(),
                y: (anchor.y - CGFloat(gap) + r.tipOffset.y).rounded())
    }
}
