import AppKit

/// An RGBA colour in 8-bit channels, straight from the atlas palette.
struct RGBA {
    var r: UInt8, g: UInt8, b: UInt8, a: UInt8

    static let clear = RGBA(r: 0, g: 0, b: 0, a: 0)

    /// Parses `#RRGGBBAA` (or `#RRGGBB`). The atlas writes the 8-digit form.
    init?(hex: String) {
        var s = Substring(hex)
        if s.hasPrefix("#") { s = s.dropFirst() }
        guard s.count == 6 || s.count == 8, let v = UInt32(s, radix: 16) else { return nil }
        if s.count == 6 {
            r = UInt8((v >> 16) & 0xFF); g = UInt8((v >> 8) & 0xFF)
            b = UInt8(v & 0xFF); a = 255
        } else {
            r = UInt8((v >> 24) & 0xFF); g = UInt8((v >> 16) & 0xFF)
            b = UInt8((v >> 8) & 0xFF); a = UInt8(v & 0xFF)
        }
    }

    init(r: UInt8, g: UInt8, b: UInt8, a: UInt8) {
        self.r = r; self.g = g; self.b = b; self.a = a
    }

    var cgColor: CGColor {
        CGColor(red: CGFloat(r) / 255, green: CGFloat(g) / 255,
                blue: CGFloat(b) / 255, alpha: CGFloat(a) / 255)
    }
}

/// A surface of whole LOGICAL pixels, composed at 1x and magnified by an integer
/// factor with nearest-neighbour at draw time.
///
/// Composing UI art at 1x and magnifying is the only way to keep it in the same
/// visual language as the cat. Anything drawn at device resolution — a
/// `CGContext` at 2x, a `CATextLayer`, an `NSAttributedString` — lands on
/// half-pixels and anti-aliases its own edges, and no amount of downstream care
/// gets that back.
struct PixelBitmap {
    private(set) var width: Int
    private(set) var height: Int
    /// Row-major RGBA, row 0 at the TOP, matching atlas coordinates.
    private(set) var rgba: [UInt8]

    init(width: Int, height: Int) {
        self.width = max(width, 0)
        self.height = max(height, 0)
        self.rgba = [UInt8](repeating: 0, count: self.width * self.height * 4)
    }

    /// Reads a PNG into logical pixels. Only ever called at load time.
    init?(contentsOf url: URL) {
        guard
            let src = CGImageSourceCreateWithURL(url as CFURL, nil),
            let img = CGImageSourceCreateImageAtIndex(src, 0, nil)
        else { return nil }
        self.init(cgImage: img)
    }

    init?(cgImage img: CGImage) {
        let w = img.width, h = img.height
        guard w > 0, h > 0 else { return nil }
        var buf = [UInt8](repeating: 0, count: w * h * 4)
        let ok: Bool = buf.withUnsafeMutableBytes { raw -> Bool in
            guard let ctx = CGContext(
                data: raw.baseAddress, width: w, height: h, bitsPerComponent: 8,
                bytesPerRow: w * 4, space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
            else { return false }
            ctx.interpolationQuality = .none
            ctx.draw(img, in: CGRect(x: 0, y: 0, width: w, height: h))
            return true
        }
        guard ok else { return nil }

        // No flip. A CGBitmapContext's USER SPACE is y-up, but its backing store is
        // top-down: buffer row 0 is the image's top row, which is already what the
        // atlas means by y. Measured with an asymmetric part (see spikes/hitmask),
        // because getting this backwards silently mirrors every glyph.
        self.width = w
        self.height = h
        self.rgba = buf
    }

    subscript(x: Int, y: Int) -> RGBA {
        get {
            guard x >= 0, x < width, y >= 0, y < height else { return .clear }
            let i = (y * width + x) * 4
            return RGBA(r: rgba[i], g: rgba[i + 1], b: rgba[i + 2], a: rgba[i + 3])
        }
        set {
            guard x >= 0, x < width, y >= 0, y < height else { return }
            let i = (y * width + x) * 4
            rgba[i] = newValue.r; rgba[i + 1] = newValue.g
            rgba[i + 2] = newValue.b; rgba[i + 3] = newValue.a
        }
    }

    /// Copies every non-transparent pixel of `src`. Opaque-or-clear art only, so a
    /// straight copy is the correct blend and there is nothing to round.
    mutating func blit(_ src: PixelBitmap, x: Int, y: Int) {
        for sy in 0..<src.height {
            for sx in 0..<src.width {
                let c = src[sx, sy]
                if c.a > 0 { self[x + sx, y + sy] = c }
            }
        }
    }

    /// Inks a sub-rectangle of a coverage mask (the font sheet) in a flat colour.
    mutating func stamp(mask: PixelBitmap, from: CGRect, x: Int, y: Int, color: RGBA) {
        let mx = Int(from.origin.x), my = Int(from.origin.y)
        for sy in 0..<Int(from.height) {
            for sx in 0..<Int(from.width) where mask[mx + sx, my + sy].a > 128 {
                self[x + sx, y + sy] = color
            }
        }
    }

    func cgImage() -> CGImage? {
        guard width > 0, height > 0,
              let provider = CGDataProvider(data: Data(rgba) as CFData) else { return nil }
        return CGImage(
            width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32,
            bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
            provider: provider, decode: nil, shouldInterpolate: false,
            intent: .defaultIntent)
    }
}
