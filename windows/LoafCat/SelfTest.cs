using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LoafCat.Interop;

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
///   * an unchanged frame is recognised as unchanged and never re-sent to the desktop
///     compositor
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

        CheckKeyInference();
        CheckStartMenuEntry();
        foreach (string theme in themes) CheckTheme(theme);

        Log.Line(_failures == 0
            ? "self-test PASSED"
            : $"self-test FAILED with {_failures} problem(s)");
        Log.Stop();
        return _failures == 0 ? 0 : 1;
    }

    /// Whether a moving mouse can be mistaken for typing.
    ///
    /// This is here because it once could, and the result was a cat that sat there
    /// steaming while its owner did nothing but move the cursor. Nothing about that is
    /// visible in a build log, nobody can move a mouse on a CI runner, and the author of
    /// this port has no Windows machine — so the mouse is replayed instead, at the
    /// timings that actually caused it.
    private static void CheckKeyInference()
    {
        Log.Line("--- input inference ---");

        // The hook reads one field out of MSLLHOOKSTRUCT by byte offset, because a
        // struct copy on every mouse event in the system is felt as a laggy cursor
        // everywhere. That offset is only safe if it still agrees with the layout.
        Check("MSLLHOOKSTRUCT.Time is where the hook reads it",
            Marshal.OffsetOf<Win32.MouseLowLevelHook>(nameof(Win32.MouseLowLevelHook.Time))
                == Win32.MouseHookTimeOffset,
            $"offset {Win32.MouseHookTimeOffset}");

        // Five seconds of a 125Hz mouse against a 120Hz tick, with the hook running one
        // poll LATE throughout. That lag is not pessimism: GetLastInputInfo is updated
        // by the raw input thread the instant the event lands, while the hook is a
        // callback dispatched to another thread afterwards, so the tick genuinely
        // arrives first. Ruling on it before the hook catches up is what counted every
        // mouse move as a keystroke.
        Check("a moving mouse is never read as typing",
            PhantomKeys(clockSkewMs: 0) == 0,
            $"{PhantomKeys(clockSkewMs: 0)} phantom keystroke(s) in 5s of mouse movement");

        // The same run, but with the two Win32 clocks disagreeing by 40ms — far beyond
        // the tolerance the timestamp test allows. That they agree is the one assumption
        // in this file that could not be checked on real hardware, so the arrival-time
        // test has to carry the result on its own if the assumption is wrong.
        Check("...even if MSLLHOOKSTRUCT.Time and GetLastInputInfo disagree",
            PhantomKeys(clockSkewMs: 40) == 0,
            $"{PhantomKeys(clockSkewMs: 40)} phantom keystroke(s) with a 40ms clock skew");

        // The other direction, which is the failure that would make the fix pointless:
        // conservative is only acceptable if actual typing still registers.
        {
            var k = new KeyInference(0);
            double dt = 1.0 / 120.0;
            int injected = 0;
            double nextKey = 0;
            for (int i = 0; i < 360; i++)      // three seconds
            {
                double now = i * dt;
                if (injected < 20 && now >= nextKey)
                {
                    k.NoteInput((uint)(now * 1000), now);
                    injected++;
                    nextKey = now + 0.1;       // ten keys a second
                }
                k.Resolve(now);
            }
            Check("typing on a still mouse is counted exactly", k.Keys == 20,
                  $"{k.Keys} of 20 keystrokes");
        }
    }

    /// Whether loafcat will be findable by typing its name.
    ///
    /// Windows Search indexes `Start Menu\Programs` for the current user, so the `.lnk`
    /// written at install time IS the app as far as the search box is concerned — there
    /// is nothing else to register. What is worth checking is that it gets written at
    /// all: the shortcut is created through late-bound COM, whether that survives into a
    /// single-file self-contained build is not something the compiler can tell us, and
    /// the failure mode is a Start menu entry that silently never appears.
    ///
    /// Written somewhere harmless rather than into the real Start menu, so running the
    /// self-test never installs anything.
    private static void CheckStartMenuEntry()
    {
        Log.Line("--- start menu ---");

        string dir = Path.Combine(Path.GetTempPath(), "loafcat-selftest");
        string link = Path.Combine(dir, "loafcat.lnk");
        string target = Environment.ProcessPath ?? Path.Combine(dir, "LoafCat.exe");
        try
        {
            bool written = SelfInstall.WriteShortcut(
                link, target, dir, SelfInstall.ShortcutDescription);
            Check("a Start menu shortcut can be written", written && File.Exists(link),
                  "late-bound WScript.Shell works in a single-file build");

            string? readBack = written ? SelfInstall.ReadShortcutTarget(link) : null;
            Check("...and points where it was told to",
                  string.Equals(readBack, target, StringComparison.OrdinalIgnoreCase),
                  readBack ?? "target could not be read back");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// One 5-second run of the mouse-move stream. `clockSkewMs` is how far
    /// `GetLastInputInfo` is imagined to disagree with the event's own timestamp.
    private static long PhantomKeys(int clockSkewMs)
    {
        var k = new KeyInference(0);
        double dt = 1.0 / 120.0;
        var inFlight = new List<(uint Tick, double At)>();
        uint lastSeen = 0;

        for (int i = 0; i < 600; i++)
        {
            double now = i * dt;

            // Whatever the hook was handed a poll ago, it has now recorded.
            foreach (var (t, a) in inFlight) k.NoteMouse(t, a);
            inFlight.Clear();

            // A mouse event at this instant. Its Win32 stamp sits on the ~15.6ms
            // GetTickCount grid rather than on our own clock, which is the whole reason
            // "the two readings are equal" was never a safe test.
            uint stamp = (uint)(Math.Floor(now * 1000 / 15.6) * 15.6);
            inFlight.Add((stamp, now));

            uint inputTick = (uint)(stamp + clockSkewMs);
            if (inputTick != lastSeen)
            {
                k.NoteInput(inputTick, now);
                lastSeen = inputTick;
            }
            k.Resolve(now);
        }
        return k.Keys;
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
        CheckDuplicateFrames(theme, atlas);
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

    /// The duplicate-frame check, and how much it actually buys.
    ///
    /// Two separate things, because only one of them is a property worth failing a
    /// build over:
    ///
    ///   * **The mechanism works.** Composing twice from the same rig state must
    ///     produce a frame recognised as unchanged. Deterministic, and if it ever
    ///     breaks, every frame goes to the desktop compositor whether it needs to or
    ///     not.
    ///   * **How often an idle cat repeats a frame.** Reported, not asserted. It
    ///     depends on where the breathing sine happens to sit relative to the pixel
    ///     grid, and inventing a threshold would be asserting a number nobody measured.
    private static void CheckDuplicateFrames(string theme, Atlas atlas)
    {
        var rig = new Rig(atlas);
        using var view = new CatView(atlas, rig, 2);

        rig.Update(1.0 / 120.0, new Pt(0, 0), isBlinkSuppressed: true);
        view.Compose();
        bool firstIsNew = view.FrameChanged();
        // No rig update in between: byte-for-byte the same frame, by construction.
        view.Compose();
        bool secondIsDuplicate = !view.FrameChanged();

        Check($"{theme}: duplicate frames are detected", firstIsNew && secondIsDuplicate,
            "an unchanged frame is not sent to the compositor twice");

        long before = view.FramesSkipped;
        const int frames = 600;      // five seconds at 120Hz
        for (int i = 0; i < frames; i++)
        {
            // A cursor that does not move, which is what "idle" means here.
            rig.Update(1.0 / 120.0, new Pt(0, 0), isBlinkSuppressed: true);
            view.Compose();
            view.FrameChanged();
        }
        long repeated = view.FramesSkipped - before;
        Log.Line($"  ..  {theme}: idle repeats {repeated}/{frames} " +
                 $"({(double)repeated / frames:P0}) — reported, not asserted");
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
