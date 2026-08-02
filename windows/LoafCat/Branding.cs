using System.Drawing;
using System.Reflection;
using System.Runtime.Versioning;

namespace LoafCat;

/// Where the generated assets are, and the two images that are chrome rather than cat.
///
/// The app icon and the tray glyph come out of `tools/generate_icon.py`, which
/// composites the *actual* mono theme parts. That is the point: the thing in Explorer
/// and the thing on the desktop cannot drift apart, because they are the same pixels
/// run through the same generator — and it is the same generator the macOS build uses.
[SupportedOSPlatform("windows")]
public static class Assets
{
    /// Assets sit next to the executable in an installed copy, and at the repo root
    /// during development. Checking both keeps `dotnet run` from the source tree
    /// working, which is the counterpart of the macOS build's two candidates.
    public static string Root()
    {
        string exeDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(exeDir, "assets"),
            // ...\windows\LoafCat\bin\Release\net8.0-windows\win-x64\  ->  repo root
            Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", "..", "assets")),
            Path.Combine(Directory.GetCurrentDirectory(), "assets"),
        };
        foreach (string c in candidates)
        {
            if (Directory.Exists(c)) return c;
        }
        return candidates[0];
    }

    /// Every theme is a self-contained directory of parts plus a cat.json. Swapping
    /// themes is therefore a directory swap — no code knows anything about a specific
    /// cat, which is what makes community themes possible later.
    public static string ThemeDir(string name) => Path.Combine(Root(), "themes", name);

    public static List<string> Themes()
    {
        string dir = Path.Combine(Root(), "themes");
        if (!Directory.Exists(dir)) return [];
        var names = new List<string>();
        foreach (string d in Directory.GetDirectories(dir))
        {
            string name = Path.GetFileName(d);
            if (name.StartsWith('.')) continue;
            // A directory without a cat.json is not a theme, and offering it in the
            // picker would give the user a button that can only fail.
            if (!File.Exists(Path.Combine(d, "cat.json"))) continue;
            names.Add(name);
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }
}

[SupportedOSPlatform("windows")]
public static class Branding
{
    private static Icon? _trayIcon;
    private static Icon? _appIcon;

    /// The tray cat.
    ///
    /// This is where the two platforms genuinely have to differ. macOS takes a
    /// *template* image — an alpha mask that AppKit tints to match the menu bar — so
    /// one asset survives light mode, dark mode and a tinted wallpaper.
    ///
    /// Windows has no template concept: a notification-area icon is drawn as-is on a
    /// taskbar the user may have set to light or dark. So the generator emits a real
    /// two-tone icon, and the mid-grey coat of the mono cat is chosen to hold contrast
    /// against both. Reading the theme from the registry and swapping icons was the
    /// alternative and is worse: it needs a registry watcher, and it is wrong for the
    /// third case, where the taskbar is showing the user's wallpaper.
    public static Icon TrayIcon()
    {
        if (_trayIcon is not null) return _trayIcon;
        string path = Path.Combine(Assets.Root(), "icon", "loafcat.ico");
        try
        {
            if (File.Exists(path))
            {
                // Loaded through a stream so the file is not locked for the lifetime
                // of the process — the installer replaces it in place on upgrade.
                using var fs = File.OpenRead(path);
                _trayIcon = new Icon(fs, new Size(16, 16));
                return _trayIcon;
            }
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
            Log.Warn($"branding: could not load {path} ({e.Message})");
        }
        // A visible fallback beats none: without an icon the tray item is an invisible
        // gap and the only way back into the app is gone.
        _trayIcon = SystemIcons.Application;
        return _trayIcon;
    }

    /// The full-colour cat, for the Settings header and the About pane.
    public static Icon AppIcon()
    {
        if (_appIcon is not null) return _appIcon;
        string path = Path.Combine(Assets.Root(), "icon", "loafcat.ico");
        try
        {
            if (File.Exists(path))
            {
                using var fs = File.OpenRead(path);
                _appIcon = new Icon(fs, new Size(256, 256));
                return _appIcon;
            }
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
            Log.Warn($"branding: could not load {path} ({e.Message})");
        }
        _appIcon = SystemIcons.Application;
        return _appIcon;
    }

    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "dev";
            // The SDK appends "+<commit sha>" to the informational version.
            int plus = v.IndexOf('+');
            return plus > 0 ? v[..plus] : v;
        }
    }

    public const string Repo = "https://github.com/aadisaraf/loafcat";
}

/// Renders a theme's default pose, for the picker in Settings.
///
/// Composited from the theme's own atlas rather than from the `preview.png` the
/// generator writes: the preview is a contact sheet on two backgrounds, and a
/// thumbnail has to be the cat alone on transparency. Doing it from the atlas also
/// means a community theme dropped into `assets/themes/` gets a correct thumbnail with
/// nothing to generate.
[SupportedOSPlatform("windows")]
public static class ThemeThumbnail
{
    private static readonly Dictionary<string, Bitmap> Cache = [];

    public static Bitmap? Image(string theme, int scale)
    {
        string key = $"{theme}@{scale}";
        if (Cache.TryGetValue(key, out var hit)) return hit;

        Atlas atlas;
        try
        {
            atlas = Atlas.Load(Assets.ThemeDir(theme));
        }
        catch (Exception e) when (e is Atlas.LoadException or IOException)
        {
            return null;
        }

        int side = (int)atlas.Canvas;
        var canvas = new PixelBitmap(side, side);
        foreach (string name in atlas.Order)
        {
            // Lids are the blink frame, not the default pose.
            if (name.StartsWith("lid_", StringComparison.Ordinal)) continue;
            if (!atlas.Parts.TryGetValue(name, out var part)) continue;
            canvas.Blit(part.Image, (int)part.Origin.X, (int)part.Origin.Y);
        }

        // Magnified by a whole number with nearest-neighbour, like everything else. A
        // thumbnail that was smoothed would misrepresent the thing it is advertising.
        var image = canvas.ToBitmap(Math.Max(scale, 1));
        Cache[key] = image;
        return image;
    }

    /// Dropped when the theme list is re-read, so a theme edited on disk shows its new
    /// art without a restart.
    public static void Clear()
    {
        foreach (var (_, b) in Cache) b.Dispose();
        Cache.Clear();
    }
}
