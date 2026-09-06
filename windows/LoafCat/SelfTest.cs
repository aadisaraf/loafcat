using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LoafCat.Interop;

namespace LoafCat;

/// `loafcat.exe --selftest` — everything that can be checked without a desktop.
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
        CheckInstallPlan();
        CheckPeekPlan();
        CheckUpdater();
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

        // A device reporting while nobody touches anything: a finger resting on a
        // precision touchpad, a hand on a mouse with nothing to report, a controller
        // left plugged in. Every one of these advances GetLastInputInfo and produces no
        // mouse message at all, so the hook has nothing to clear them with and the
        // original inference called all of them typing. It reproduced as a cat that
        // overheated while the cursor sat still and cooled down when the mouse moved.
        //
        // 15.6ms is the GetTickCount grid, which is where the ticks land whatever the
        // device's own rate is.
        Check("input from a device the hook cannot see is not typing",
            IdleChatter(spacingMs: 15.6) == 0,
            $"{IdleChatter(spacingMs: 15.6)} phantom keystroke(s) in 5s of a resting touchpad");

        // The same, on a machine where something has raised the system timer to 1ms —
        // Chrome and most games do, and it moves the whole stream onto the poll rate.
        Check("...at 1ms timer resolution too",
            IdleChatter(spacingMs: 8.4) == 0,
            $"{IdleChatter(spacingMs: 8.4)} phantom keystroke(s) with a 1ms system timer");

        // And at 50 a second, which is the slowest a device can report and still form
        // an unbroken run. One event of it lands on the far side of a poll boundary and
        // starts a run of its own; a single keystroke in five seconds is 0.2 a second
        // against a kneading gate of 2.5, so it is below anything the cat can see.
        Check("...and at the edge of what the run test reaches",
            IdleChatter(spacingMs: 20) <= 1,
            $"{IdleChatter(spacingMs: 20)} phantom keystroke(s) from a 50Hz stream");

        // Slow enough to leave a gap between every pair of events, and therefore only
        // reachable by the sustained-rate backstop. That backstop now sits at 44 input
        // events a second rather than 22, because a keystroke is a press AND a release
        // and 22 keystrokes a second is 44 events — so a 30Hz device is inside the band
        // this class deliberately leaves open, exactly as a person typing at fifteen
        // characters a second is. They are the same stream; nothing here can tell them
        // apart, and the note above `KeyGap` says so at length.
        //
        // Asserted anyway, because "it is in the open band" and "it is unbounded" are
        // different claims: 151 unfiltered events become 74 keystrokes and not 151.
        Check("a 30Hz device is halved but not written off",
            IdleChatter(spacingMs: 33) < 90,
            $"{IdleChatter(spacingMs: 33)} in 5s, against 151 unfiltered — the open band");

        // The other direction, which is the failure that would make all of it pointless:
        // conservative is only acceptable if actual typing still registers. Every one of
        // these presses a key AND releases it, which is what a keyboard does and what
        // these tests used to leave out. Modelling one event per character is what hid
        // the bug this whole rewrite is about: judged one at a time, a release and the
        // next press disqualified each other for being 10ms apart, and above ten
        // characters a second between 91% and 98% of real typing was thrown away.
        Check("typing on a still mouse is counted exactly", Typing(0.1, 20, 3.0) == 20,
              $"{Typing(0.1, 20, 3.0)} of 20 keystrokes at ten a second");
        Check("...and at a gentler pace", Typing(0.2, 25, 6.0) == 25,
              $"{Typing(0.2, 25, 6.0)} of 25 keystrokes at five a second");
        // The two that used to fail, and the reason this file was reported as broken:
        // fourteen characters a second is `overheat.kps_max`, so a cat that cannot see
        // it cannot reach the state the art was drawn for.
        Check("...and at the pace that is supposed to redden the cat",
              Typing(0.07, 30, 3.0) == 30,
              $"{Typing(0.07, 30, 3.0)} of 30 keystrokes at fourteen a second");
        Check("...and faster than anyone sustains", Typing(0.055, 40, 3.0) == 40,
              $"{Typing(0.055, 40, 3.0)} of 40 keystrokes at eighteen a second");

        // Every check above is about the count. This one is about when it arrives:
        // the cat has to react to typing while it is still happening.
        Check("...and the first keystroke is credited promptly",
              TypingLatency(0.16) < 0.12,
              $"{TypingLatency(0.16) * 1000:0} ms from key-down to credit "
              + "— 158 ms when the pair was credited on the release");

        // Suppression has to end when the device does, or one controller left plugged in
        // would switch the cat's typing reactions off for the rest of the session.
        {
            var k = new KeyInference(0);
            double dt = 1.0 / 120.0;
            int typed = 0;
            double nextKey = 5.0;
            double? release = null;
            for (int i = 0; i < 1260; i++)     // ten and a half seconds
            {
                double now = i * dt;
                // Two seconds of chatter, then three seconds of nothing, then typing.
                if (now < 2.0 && i % 2 == 0)
                {
                    k.NoteInput((uint)(now * 1000), now);
                }
                else if (release is { } up && now >= up)
                {
                    k.NoteInput((uint)(now * 1000), now);
                    release = null;
                }
                else if (typed < 25 && now >= nextKey)
                {
                    k.NoteInput((uint)(now * 1000), now);
                    typed++;
                    release = now + 0.09;      // a key is held for a moment, then let go
                    nextKey = now + 0.2;
                }
                k.Resolve(now);
            }
            // 25 keys in the five seconds after it stops. Exact rather than approximate:
            // the hold is 2s and the typing starts 3s after the last chatter, so nothing
            // about this is meant to be near a boundary. The loop runs half a second past
            // the last release so the final run has a gap after it to be settled by —
            // a run is only over once nothing has followed it for `KeyGap`.
            Check("typing works again once the device stops", k.Keys == 25,
                  $"{k.Keys} of 25 keystrokes after the chatter ended, {k.Ignored} ignored");
        }
    }

    /// Unexplained input arriving every `spacingMs`, for five seconds, with no mouse
    /// event anywhere — which is exactly what the hook sees while a finger rests on a
    /// touchpad. Returns how much of it was believed to be typing.
    private static long IdleChatter(double spacingMs)
    {
        var k = new KeyInference(0);
        double dt = 1.0 / 120.0;
        uint lastSeen = 0;
        for (int i = 0; i < 600; i++)
        {
            double now = i * dt;
            // The poll only ever notices a CHANGE in the tick, so the device's rate is
            // modelled where it actually shows up: in the value, not in the polling.
            uint tick = (uint)(Math.Floor(now * 1000 / spacingMs) * spacingMs);
            if (tick != lastSeen)
            {
                lastSeen = tick;
                k.NoteInput(tick, now);
            }
            k.Resolve(now);
        }
        return k.Keys;
    }

    /// `count` keystrokes `gap` seconds apart, on a still mouse, over `seconds`.
    ///
    /// Each one is TWO input events — the key going down and the key coming back up,
    /// both of which reset `GetLastInputInfo`. Modelling a character as one event is
    /// what let the original inference pass this test while failing on a real keyboard:
    /// once the hold and the gap to the next key are comparable, the release and the
    /// following press land inside `KeyGap` of each other, and judged individually they
    /// used to cancel out.
    private static long Typing(double gap, int count, double seconds, double hold = 0.09)
    {
        // Presses on a schedule, each release one hold later, INTERLEAVED — a release
        // must never be allowed to postpone the next press. Past about twelve characters
        // a second the hold outlasts the gap and a hand genuinely is still holding one
        // key as it presses the next, and that overlap is exactly the shape whose two
        // halves used to cancel each other out.
        var events = new List<double>();
        for (int n = 0; n < count; n++)
        {
            events.Add(n * gap);
            events.Add(n * gap + hold);
        }
        events.Sort();

        var k = new KeyInference(0);
        double dt = 1.0 / 120.0;
        int next = 0;
        for (int i = 0; i < (int)(seconds * 120); i++)
        {
            double now = i * dt;
            // `GetLastInputInfo` reports one tick per poll, so everything inside the
            // same 8.3ms frame is indistinguishable from a single event.
            bool any = false;
            while (next < events.Count && events[next] <= now) { next++; any = true; }
            if (any) k.NoteInput((uint)(now * 1000), now);
            k.Resolve(now);
        }
        return k.Keys;
    }

    /// How long after the first key goes DOWN the first keystroke is credited.
    ///
    /// The count being right says nothing about WHEN it arrives, and the two came
    /// apart once already: crediting the release rather than the press put every burst
    /// 158ms late, which reads as the cat noticing your typing only once you have
    /// stopped. `NaN` if nothing was ever credited.
    private static double TypingLatency(double gap, double hold = 0.09,
                                        double seconds = 2.0)
    {
        var events = new List<double>();
        for (int n = 0; n < 12; n++) { events.Add(n * gap); events.Add(n * gap + hold); }
        events.Sort();

        var k = new KeyInference(0);
        double dt = 1.0 / 120.0;
        int next = 0;
        for (int i = 0; i < (int)(seconds * 120); i++)
        {
            double now = i * dt;
            bool any = false;
            while (next < events.Count && events[next] <= now) { next++; any = true; }
            if (any) k.NoteInput((uint)(now * 1000), now);
            k.Resolve(now);
            if (k.Keys > 0) return now;   // the first press is at t = 0
        }
        return double.NaN;
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
        string target = Environment.ProcessPath ?? Path.Combine(dir, "loafcat.exe");
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

    /// What a downloaded executable decides to do about itself.
    ///
    /// Every branch but the first used to collapse into the same silent nothing, and
    /// three of the four are reached only on a machine that already has loafcat on it —
    /// which no test machine does and every user's does. So they are checked here, with
    /// the versions passed in, rather than left to be discovered by the person the
    /// answer was wrong for.
    private static void CheckInstallPlan()
    {
        Log.Line("--- install plan ---");

        Check("nothing installed means install it",
            SelfInstall.Decide(null, "0.2.0") == InstallPlan.Fresh, "no target on disk");

        Check("an older copy is replaced",
            SelfInstall.Decide("0.1.0", "0.2.0") == InstallPlan.Replace,
            "0.1.0 installed, 0.2.0 downloaded");

        // The one that reads as "the installer did nothing". It genuinely has nothing
        // to do, and the difference between that and failing is a sentence on screen.
        Check("the same version is not reinstalled",
            SelfInstall.Decide("0.2.0", "0.2.0") == InstallPlan.Same, "0.2.0 either way");

        // FileVersionInfo reports four components where Branding reports three.
        Check("...however many components each side spells it with",
            SelfInstall.Decide("0.2.0.0", "0.2.0") == InstallPlan.Same, "0.2.0.0 vs 0.2.0");

        Check("a downgrade is offered rather than performed",
            SelfInstall.Decide("0.3.0", "0.2.0") == InstallPlan.Older,
            "0.3.0 installed, 0.2.0 downloaded");

        // The self-test is a real run of the real executable, so the guard that keeps it
        // from installing anything on the machine testing it has to hold.
        Check("a test run never installs anything",
            SelfInstall.Plan(["--portable"]) == InstallPlan.None
                && SelfInstall.Plan(["--demo-drag"]) == InstallPlan.None
                && SelfInstall.Plan(["--demo-peek"]) == InstallPlan.None,
            "--portable, --demo-drag and --demo-peek all opt out");
    }

    /// The edge-snap gesture, which nobody on a CI runner can perform by hand.
    ///
    /// `--demo-peek` covers the same ground in more detail against a real display; this
    /// is the part that must hold on a headless runner too, and it is here so that a
    /// change to the thresholds fails a build rather than being noticed by a user.
    private static void CheckPeekPlan()
    {
        Log.Line("--- peek ---");

        // 1000pt wide screen, a 40pt band, 320ms to arm.
        var a = new Arming { ArmMs = 320, DisarmMs = 80 };
        void Hold(ref Arming arm, PeekEdge? e, double from, double to)
        {
            for (double t = from; t < to; t += 1.0 / 120) arm.Step(e, t);
        }

        Hold(ref a, PeekEdge.Right, 0, 0.15);
        Check("brushing the edge does not arm", a.Armed is null, "150ms < 320ms");

        Hold(ref a, PeekEdge.Right, 0.15, 0.40);
        Check("dwelling on it does", a.Armed == PeekEdge.Right, "400ms > 320ms");

        Hold(ref a, null, 0.40, 0.44);
        Check("a 40ms wobble out of the band is forgiven",
            a.Armed == PeekEdge.Right, "40ms < 80ms");

        Hold(ref a, null, 0.44, 0.60);
        Check("leaving it properly disarms", a.Armed is null, "160ms > 80ms");

        var b = new Arming { ArmMs = 320, DisarmMs = 80 };
        Hold(ref b, PeekEdge.Left, 0, 0.40);
        Check("the left edge arms too", b.Armed == PeekEdge.Left, "");

        // Parked geometry, on invented numbers rather than this theme's, because the
        // arithmetic is what is under test and it should not change when the art does.
        // Ink spanning 9..39 inside a 4px-margin canvas at 2x, revealing 28: its left
        // edge must land exactly 28 logical px inside the right screen edge.
        double x = PeekModule.ParkedX(PeekEdge.Right, 0, 1000, padX: 4,
                                      inkMinX: 9, inkMaxX: 39, revealPx: 28, scale: 2);
        Check("a right park leaves exactly the reveal on screen",
            Math.Abs(1000 - (x + (4 + 9) * 2) - 56) < 1e-9, $"x={x}");

        // And it must not send the cat somewhere a monitor change would drag it back
        // from, which is the one thing that can silently undo a park on Windows.
        Check("a parked cat still overlaps the screen",
            new Rect(x, 0, (48 + 8) * 2, (48 + 6) * 2)
                .IntersectionArea(new Rect(0, 0, 1000, 1000)) > 0, "");

        var screen = new Rect(0, 0, 1000, 1000);
        Check("auto-peek picks the nearer edge",
            PeekModule.NearerEdge(new Rect(10, 0, 100, 100), screen) == PeekEdge.Left
            && PeekModule.NearerEdge(new Rect(880, 0, 100, 100), screen) == PeekEdge.Right,
            "so a cat living on the left is not flung across the display");

        Check("a dead-centre cat goes right",
            PeekModule.NearerEdge(new Rect(450, 0, 100, 100), screen) == PeekEdge.Right,
            "midX 500 of 1000 — the tie the user asked to break rightwards");
    }

    /// The two pure decisions the updater makes, which between them decide whether a
    /// downloaded executable gets to run.
    private static void CheckUpdater()
    {
        Log.Line("--- updater ---");

        Check("a newer version is recognised",
            Updater.IsNewer("0.3.0", "0.2.0") && Updater.IsNewer("0.2.1", "0.2.0")
            && Updater.IsNewer("1.0.0", "0.9.9"), "");

        // The one that matters: never downgrade, and never update to yourself. Either
        // would be a loop that reinstalls on every launch for ever.
        Check("an older or identical version is not",
            !Updater.IsNewer("0.1.0", "0.2.0") && !Updater.IsNewer("0.2.0", "0.2.0")
            && !Updater.IsNewer("0.2.0-rc.1", "0.2.0"), "");

        Check("a compiled-in signing key is present",
            Updater.UpdateKey.Length > 0,
            "an empty key would mean nothing is ever installed automatically");

        // Not a signature. Verification must say so rather than throw, because the
        // thing on the other end of that call is a network download.
        Check("garbage is not a valid signature",
            !Updater.VerifySignature([1, 2, 3], [4, 5, 6])
            && !Updater.VerifySignature([1, 2, 3], []), "");

        // A release with nothing this platform can install. This is the shape of the
        // real v0.1.0 — published months before there was a Windows build, so its only
        // assets are a .dmg and its checksum — and `/releases/latest` resolves to it for
        // as long as every newer release is a prerelease.
        //
        // Reported by a user as "could not reach GitHub" on a machine whose network was
        // fine, because a release carrying no matching asset and a request that never
        // completed were the same `null` by the time anyone looked. Nothing about that
        // is visible from inside the app: it simply never updates, and says the wrong
        // reason forever.
        const string olderRelease = """
            {"tag_name":"v0.1.0","assets":[
              {"name":"loafcat-0.1.0.dmg","browser_download_url":"https://x/a.dmg"},
              {"name":"loafcat-0.1.0.dmg.sha256","browser_download_url":"https://x/a.dmg.sha256"}
            ]}
            """;
        var parsed = Updater.ParseRelease(olderRelease);
        Check("a release with no Windows download still parses",
            parsed is { Version: "0.1.0", AssetUrl: null },
            parsed is null ? "it came back as nothing at all"
                           : $"version {parsed.Version}, asset {parsed.AssetUrl ?? "none"}");

        // And the answer the app then gives is about versions, not about the network.
        Check("...and is correctly judged older than what is installed",
            parsed is not null && !Updater.IsNewer(parsed.Version, "0.2.0"),
            "so the honest answer is that this build is the latest");

        // The shape that should actually install.
        var full = Updater.ParseRelease("""
            {"tag_name":"v9.9.9","assets":[
              {"name":"loafcat.exe","browser_download_url":"https://x/loafcat.exe"},
              {"name":"loafcat.exe.sha256","browser_download_url":"https://x/loafcat.exe.sha256"},
              {"name":"loafcat.exe.sig","browser_download_url":"https://x/loafcat.exe.sig"},
              {"name":"loafcat-9.9.9-macos.zip","browser_download_url":"https://x/m.zip"}
            ]}
            """);
        Check("a Windows release resolves to all three files",
            full is { Version: "9.9.9", AssetUrl: not null, ChecksumUrl: not null,
                      SignatureUrl: not null }
            && full.AssetUrl.EndsWith("loafcat.exe", StringComparison.Ordinal),
            full is null ? "nothing parsed" : $"asset {full.AssetUrl}");
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

        // The pose swap, on the composed surface rather than in the plan. This is the
        // Windows stand-in for looking at the cat, and it is aimed at the two ways a
        // pose fails invisibly: the standing cat stays on and you get a body behind a
        // peeking head, or one hide too many and the window goes empty. Neither shows
        // up in a build log and neither is reachable from `CheckPeekPlan`, which only
        // ever sees numbers.
        int standingPx = OpaqueCount(view);
        foreach (string pose in atlas.Poses.Keys)
        {
            CatStage.Shared.Pose = pose;
            view.Compose();
            int posePx = OpaqueCount(view);
            Check($"{theme}: {pose} draws something", posePx > side,
                $"{posePx} opaque px — an empty window is what hiding one part too many looks like");
            Check($"{theme}: {pose} is less cat than the standing pose", posePx < standingPx,
                $"{posePx} vs {standingPx} — a pose that is not SMALLER is the standing "
                + "cat still being drawn underneath it");
        }
        CatStage.Shared.Pose = null;
        view.Compose();

        CheckThumbnail(theme, atlas);
        CheckIntegerMagnification(theme, atlas);
        CheckDuplicateFrames(theme, atlas);
    }

    /// The theme picker's thumbnail, checked on its pixels rather than on its plan.
    ///
    /// `atlas.Order` is every part a theme ships, poses included, and a pose is a
    /// second drawing of the WHOLE cat rather than a rearrangement of this one — so a
    /// thumbnail that walked the raw order drew the peek cat lying against its edge
    /// beside the standing one. It shipped that way, in Settings and in the installer
    /// window, and nothing about it is visible from a build log.
    ///
    /// Asserted against the standing cat's own bounding box rather than against a
    /// second composite, so the check does not simply re-run the code it is checking.
    /// Both poses reach outside that box by construction — each is parked against the
    /// screen edge it peeks round — so ink outside it is a pose and nothing else.
    private static void CheckThumbnail(string theme, Atlas atlas)
    {
        var standing = atlas.Standing.Select(kv => kv.Value).ToList();
        if (standing.Count == 0 || atlas.PosedParts.Count == 0) return;

        double left = standing.Min(p => p.Origin.X);
        double top = standing.Min(p => p.Origin.Y);
        double right = standing.Max(p => p.Origin.X + p.Size.W);
        double bottom = standing.Max(p => p.Origin.Y + p.Size.H);

        // Not disposed here: the thumbnail is cached and handed out again, so
        // `ThemeThumbnail.Clear()` at the bottom is what owns it.
        var thumb = ThemeThumbnail.Image(theme, 1);
        if (thumb is null)
        {
            Check($"{theme}: thumbnail renders", false, "no image");
            return;
        }

        int stray = 0, ink = 0;
        for (int y = 0; y < thumb.Height; y++)
        {
            for (int x = 0; x < thumb.Width; x++)
            {
                if (thumb.GetPixel(x, y).A <= 40) continue;
                ink++;
                if (x < left || x >= right || y < top || y >= bottom) stray++;
            }
        }

        Check($"{theme}: thumbnail draws the cat", ink > (int)atlas.Canvas,
            $"{ink} opaque px");
        Check($"{theme}: thumbnail is the standing cat alone", stray == 0,
            $"{stray} opaque px outside the standing cat's "
            + $"{(int)left},{(int)top}-{(int)right},{(int)bottom} box — that is a pose, "
            + "which is a second cat");
        ThemeThumbnail.Clear();
    }

    /// Opaque pixels on the composed surface. The cat is the only thing drawn into it,
    /// so this is "how much cat is there" without needing to know what shape it is.
    private static int OpaqueCount(CatView view)
    {
        int n = 0;
        for (int y = 0; y < view.HeightPx; y++)
        {
            for (int x = 0; x < view.WidthPx; x++)
            {
                if (view.AlphaAt(x, y) > 40) n++;
            }
        }
        return n;
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
