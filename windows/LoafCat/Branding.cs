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
    private static string? _root;
    private static string? _payload;

    /// Where the cat's art actually is.
    ///
    /// Three places, in order, and the order is the design:
    ///
    ///  1. `assets\` next to the executable — how the .zip ships. First, so that
    ///     dropping a community theme in there works and so a user can edit the art
    ///     of an installed copy.
    ///  2. The repo, for `dotnet run` out of a source tree.
    ///  3. Extracted from inside the executable, once. This is what makes a bare
    ///     `LoafCat.exe` a complete app rather than half of one.
    public static string Root()
    {
        if (_root is not null) return _root;

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
            // A directory with no themes in it is not an assets directory — an empty
            // folder left behind by a failed extraction would otherwise shadow the
            // embedded copy for ever.
            if (Directory.Exists(Path.Combine(c, "themes"))) { _root = c; return _root; }
        }

        _root = Payload() is { } p ? Path.Combine(p, "assets") : candidates[0];
        return _root;
    }

    /// The hook script Claude Code runs, wherever it turned out to be.
    public static string? HookScript()
    {
        string exeDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(exeDir, "hooks", "loafcat-hook.ps1"),
            Path.GetFullPath(Path.Combine(
                exeDir, "..", "..", "..", "..", "..", "hooks", "loafcat-hook.ps1")),
            Path.Combine(Directory.GetCurrentDirectory(), "hooks", "loafcat-hook.ps1"),
        };
        if (Payload() is { } p) candidates.Add(Path.Combine(p, "hooks", "loafcat-hook.ps1"));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// Unpacks the embedded copy of everything, once per version.
    ///
    /// Version-stamped so an upgrade cannot keep serving the old art out of a stale
    /// directory, and gated on a marker file written LAST so a half-finished
    /// extraction — a full disk, a process killed mid-write — is redone rather than
    /// treated as complete.
    private static string? Payload()
    {
        if (_payload is not null) return _payload;

        var asm = Assembly.GetExecutingAssembly();
        const string prefix = "loafcat.payload/";
        var names = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        // Nothing embedded: a build configured without the payload. Callers fall back
        // to their on-disk guess and fail with a path in the message, which is a far
        // better error than a silent empty directory.
        if (names.Count == 0) return null;

        string dir = Path.Combine(Paths.AppData, $"payload-{Branding.Version}");
        string marker = Path.Combine(dir, ".complete");
        if (File.Exists(marker)) { _payload = dir; return _payload; }

        try
        {
            foreach (string name in names)
            {
                // MSBuild writes %(RecursiveDir) with the build machine's separator,
                // so a payload built on macOS carries '/' and one built on Windows
                // carries '\'. Normalising here means either produces the same tree.
                string relative = name[prefix.Length..].Replace('\\', '/');
                string target = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                using var src = asm.GetManifestResourceStream(name);
                if (src is null) continue;
                using var dst = File.Create(target);
                src.CopyTo(dst);
            }
            File.WriteAllText(marker, Branding.Version);
            Log.Line($"assets  unpacked {names.Count} files to {dir}");
            _payload = dir;
            return _payload;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"assets: could not unpack to {dir} ({e.Message})");
            return null;
        }
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
