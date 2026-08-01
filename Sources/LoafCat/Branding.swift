import AppKit

/// Where the generated assets are, and the two images that are chrome rather than cat.
///
/// The app icon and the menu bar glyph come out of `tools/generate_icon.py`, which
/// composites the *actual* mono theme parts. That is the point: the thing in Finder
/// and the thing on the desktop cannot drift apart, because they are the same
/// pixels run through the same generator.
enum Assets {
    /// Assets live next to the executable in a packaged app, and at the repo root
    /// during development. Checking both keeps a plain `swiftc` build runnable.
    static func root() -> URL {
        let candidates = [
            Bundle.main.bundleURL.appendingPathComponent("Contents/Resources/assets"),
            URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
                .appendingPathComponent("assets"),
        ]
        for c in candidates where FileManager.default.fileExists(atPath: c.path) { return c }
        return candidates[1]
    }

    /// Every theme is a self-contained directory of parts plus a cat.json. Swapping
    /// themes is therefore a directory swap -- no code knows anything about a
    /// specific cat, which is what makes community themes possible later.
    static func themeDir(_ name: String) -> URL {
        root().appendingPathComponent("themes/\(name)")
    }

    static func themes() -> [String] {
        let dir = root().appendingPathComponent("themes")
        let names = (try? FileManager.default.contentsOfDirectory(atPath: dir.path)) ?? []
        return names.filter { !$0.hasPrefix(".") }.sorted()
    }
}

enum Branding {
    /// The menu bar cat, as a template image.
    ///
    /// Template means AppKit discards the colour and tints the alpha mask to match
    /// the menu bar, which is the only way one asset survives light mode, dark mode
    /// and a tinted wallpaper. The generator punches the eyes out for exactly this
    /// reason -- in a mask, a white eye is indistinguishable from a filled head.
    ///
    /// Both scales are loaded so the 1x rep is crisp on a non-retina display rather
    /// than a downsample of the 2x.
    static func trayImage() -> NSImage? {
        let dir = Assets.root().appendingPathComponent("icon")
        let image = NSImage(size: NSSize(width: 16, height: 16))
        var found = false
        for (file, points) in [("tray.png", 16.0), ("tray@2x.png", 16.0)] {
            let url = dir.appendingPathComponent(file)
            guard let data = try? Data(contentsOf: url),
                  let rep = NSBitmapImageRep(data: data) else { continue }
            // Setting the rep's size in POINTS is what marks it as 1x or 2x: the
            // 32px bitmap declared as 16pt is a 2x rep. Without this AppKit treats
            // both as 1x and picks whichever it saw last.
            rep.size = NSSize(width: points, height: points)
            image.addRepresentation(rep)
            found = true
        }
        guard found else { return nil }
        image.isTemplate = true
        return image
    }

    /// The full-colour cat, for places a template mask would be wrong — the settings
    /// window header and the About panel. Falls back to the running app's icon.
    static func appIcon() -> NSImage {
        let url = Assets.root()
            .appendingPathComponent("icon/AppIcon.iconset/icon_256x256.png")
        if let img = NSImage(contentsOf: url) { return img }
        return NSApp.applicationIconImage ?? NSImage()
    }

    static var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
            ?? "dev"
    }
}

/// Renders a theme's default pose, for the picker in Settings.
///
/// Composited from the theme's own atlas rather than from the `preview.png` the
/// generator writes: the preview is a contact sheet on two backgrounds, and a
/// thumbnail has to be the cat alone on transparency. Doing it from the atlas also
/// means a community theme dropped into `assets/themes/` gets a correct thumbnail
/// with nothing to generate.
enum ThemeThumbnail {
    private static var cache: [String: NSImage] = [:]

    static func image(theme: String, scale: CGFloat) -> NSImage? {
        let key = "\(theme)@\(Int(scale))"
        if let hit = cache[key] { return hit }
        guard let atlas = try? Atlas.load(from: Assets.themeDir(theme)) else { return nil }

        let side = Int(atlas.canvas * scale)
        guard let ctx = CGContext(
            data: nil, width: side, height: side, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
        else { return nil }
        // Nearest, always. Any smoothing here turns the outline the art pipeline
        // exists to keep crisp into a grey haze.
        ctx.interpolationQuality = .none

        for name in atlas.order {
            // Lids are the blink frame, not the default pose.
            guard !name.hasPrefix("lid_"), let part = atlas.parts[name] else { continue }
            // The atlas measures from the top-left, y-down; CGContext is y-up.
            let y = atlas.canvas - part.origin.y - part.size.height
            ctx.draw(part.image, in: CGRect(
                x: part.origin.x * scale, y: y * scale,
                width: part.size.width * scale, height: part.size.height * scale))
        }

        guard let cg = ctx.makeImage() else { return nil }
        let image = NSImage(cgImage: cg, size: NSSize(width: side, height: side))
        cache[key] = image
        return image
    }
}
