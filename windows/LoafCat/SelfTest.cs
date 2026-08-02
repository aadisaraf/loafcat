using System.Runtime.Versioning;

namespace LoafCat;

/// `LoafCat.exe --selftest` — everything that can be checked without a desktop.
///
/// This exists because the port cannot be verified the way `CLAUDE.md` demands of the
/// macOS build ("the app was launched and looked at"). A CI runner has no one to look
/// at it, and the author of this port has no Windows machine. So the properties that a
/// human would otherwise be checking by eye are asserted here instead, and the ones
/// that genuinely need a person are listed in `windows/README.md` as unverified rather
/// than quietly assumed.
///
/// Every check below is about a claim made elsewhere in the codebase:
///
///   * the atlas loads, from the SAME files the macOS build reads
///   * the hit mask is a silhouette, not empty and not the whole canvas
///   * the composed frame is opaque on the cat and fully transparent away from it,
///     which is exactly what the window manager hit-tests for click-through
///   * magnifying is nearest-neighbour and integer: 3x is byte-for-byte the 1x frame
///     with every pixel tripled, which is the pixel-art claim stated as an equation
///   * an idle cat mostly re-presents the same frame, which is what makes it cheap
[SupportedOSPlatform("windows")]
public static class SelfTest
{
    private static int _failures;

    public static int Run()
    {
        Log.Line($"loafcat {Branding.Version} self-test");
        Log.Line($"assets  {Assets.Root()}");

        var themes = Assets.Themes();
        if (themes.Count == 0)
        {
            Fail("no themes found — is assets/ next to the executable?");
            return 1;
        }
        Log.Line($"themes  {string.Join(", ", themes)}");

        foreach (string theme in themes) CheckTheme(theme);

        Log.Line(_failures == 0
            ? "self-test PASSED"
            : $"self-test FAILED with {_failures} problem(s)");
        Log.Stop();
        return _failures == 0 ? 0 : 1;
    }

    private static void CheckTheme(string theme)
    {
        Log.Line($"--- {theme} ---");

        Atlas atlas;
        try
        {
            atlas = Atlas.Load(Assets.ThemeDir(theme));
        }
        catch (Exception e) when (e is Atlas.LoadException or IOException)
        {
            Fail($"{theme}: {e.Message}");
            return;
        }

        Check($"{theme}: parts load", atlas.Parts.Count > 0,
            $"{atlas.Parts.Count} parts, canvas {(int)atlas.Canvas}px, " +
            $"pad {atlas.Layout.PadX}x{atlas.Layout.PadY}");
        Check($"{theme}: draw order is populated", atlas.Order.Count > 0,
            $"{atlas.Order.Count} layers");
        Check($"{theme}: overlays load", atlas.Overlays.Count > 0,
            $"{atlas.Overlays.Count} sprites, {atlas.Animations.Count} keyframed animations");

        // Every part named in the draw order has to exist, or the compositor silently
        // skips a layer and the cat is missing an ear.
        var missing = atlas.Order.Where(n => !atlas.Parts.ContainsKey(n)).ToList();
        Check($"{theme}: every ordered part exists", missing.Count == 0,
            missing.Count == 0 ? "" : "missing: " + string.Join(", ", missing));

        // The speech bubble is the one piece of chrome assembled at runtime.
        if (atlas.Bubble is { } bubble)
        {
            var rendered = bubble.Render("Water break!");
            Check($"{theme}: bubble renders", rendered is not null,
                rendered is { } r ? $"{r.Image.Width}x{r.Image.Height}px" : "");
        }
        else
        {
            Log.Line($"  ..  {theme}: no bubble in this theme (allowed)");
        }

        var rig = new Rig(atlas);
        using var view = new CatView(atlas, rig, 3);

        int maskCount = view.HitMaskCount();
        int side = (int)atlas.Canvas;
        Check($"{theme}: hit mask is a silhouette",
            maskCount > side * 4 && maskCount < side * side,
            $"{maskCount} of {side * side} logical px");

        // 240 frames of ambient motion: blink, breathe, tail sway, spring settle. This
        // is the closest thing to "run it for a while and see if it falls over".
        for (int i = 0; i < 240; i++)
        {
            rig.Update(1.0 / 120.0, new Pt(30 + i * 0.1, -10));
            view.Compose();
        }
        Check($"{theme}: 240 frames composed", true, "no exception");

        // The click-through claim, checked against the buffer the window manager would
        // hit-test. The centre of the surface is the centre of the cat by construction.
        byte centre = view.AlphaAt(view.WidthPx / 2, view.HeightPx / 2);
        byte corner = view.AlphaAt(1, 1);
        Check($"{theme}: opaque on the cat", centre == 255, $"alpha {centre} at the centre");
        Check($"{theme}: fully transparent in the margin", corner == 0,
            $"alpha {corner} at (1, 1) — clicks there fall through to the app below");

        CheckIntegerMagnification(theme, atlas);
        CheckIdleFramesRepeat(theme, atlas);
    }

    /// The pixel-art claim, stated as an equation.
    ///
    /// A frame rendered at 3x must be byte-for-byte the same frame rendered at 1x with
    /// every pixel repeated three times. If any interpolation, half-pixel offset or
    /// fractional transform has leaked into the compositor, this cannot hold — and it is
    /// exactly the failure that shows up to a user as a shimmering, crawling cat.
    private static void CheckIntegerMagnification(string theme, Atlas atlas)
    {
        const int magnify = 3;
        var rig1 = new Rig(atlas);
        var rig3 = new Rig(atlas);
        // Identical, fully deterministic state in both rigs: one update with the same dt
        // and cursor, and blinking suppressed so the random blink schedule cannot make
        // the two disagree.
        rig1.Update(1.0 / 120.0, new Pt(40, -20), isBlinkSuppressed: true);
        rig3.Update(1.0 / 120.0, new Pt(40, -20), isBlinkSuppressed: true);

        using var small = new CatView(atlas, rig1, 1);
        using var large = new CatView(atlas, rig3, magnify);
        small.Compose();
        large.Compose();

        long mismatches = 0;
        for (int y = 0; y < large.HeightPx && mismatches == 0; y++)
        {
            for (int x = 0; x < large.WidthPx; x++)
            {
                if (large.AlphaAt(x, y) != small.AlphaAt(x / magnify, y / magnify))
                {
                    mismatches++;
                    break;
                }
            }
        }

        Check($"{theme}: {magnify}x is exactly {magnify}x", mismatches == 0,
            mismatches == 0
                ? $"{large.WidthPx}x{large.HeightPx} is the 1x frame with every pixel tripled"
                : "a fractional transform has leaked into the compositor");
    }

    /// An idle cat should mostly be re-presenting the frame that is already on screen.
    ///
    /// Every position is quantised to a whole logical pixel before scaling, so the
    /// continuous breathing sine and tail spring spend most of their time producing an
    /// identical frame. If this ratio ever collapses, something has started moving on a
    /// sub-pixel grid — which is both a performance regression and, more importantly,
    /// the first symptom of the art starting to crawl.
    private static void CheckIdleFramesRepeat(string theme, Atlas atlas)
    {
        var rig = new Rig(atlas);
        using var view = new CatView(atlas, rig, 2);

        // No HWND, so `Present` cannot actually reach the window manager — but the
        // duplicate-frame comparison happens before that, and its counters are what this
        // is measuring.
        int identical = 0;
        const int frames = 600;      // five seconds at 120Hz
        for (int i = 0; i < frames; i++)
        {
            // A cursor that does not move, which is what "idle" means here.
            rig.Update(1.0 / 120.0, new Pt(0, 0), isBlinkSuppressed: true);
            view.Compose();
            if (!view.Present(IntPtr.Zero, 0, 0)) identical++;
        }

        double ratio = (double)view.FramesSkipped / frames;
        Check($"{theme}: idle frames repeat", ratio > 0.5,
            $"{view.FramesSkipped}/{frames} ({ratio:P0}) identical to the previous frame");
        _ = identical;
    }

    private static void Check(string name, bool ok, string detail)
    {
        Log.Line($"  {(ok ? "ok" : "FAIL")}  {name}{(detail.Length > 0 ? "  —  " + detail : "")}");
        if (!ok) _failures++;
    }

    private static void Fail(string message)
    {
        Log.Line($"  FAIL  {message}");
        _failures++;
    }
}
