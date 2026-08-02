using System.Runtime.Versioning;

namespace LoafCat.Interop;

/// Reads system-wide input activity without asking for a single permission, and
/// without any code path by which the identity of a key could reach this process.
///
/// =============================================================================
/// WHY THIS LOOKS DIFFERENT FROM THE macOS VERSION
/// =============================================================================
/// macOS hands over `CGEventSource.counterForEventType(.combinedSessionState,
/// .keyDown)` — an integer count of key events, per type, permission-free. Windows
/// has no equivalent, so the same guarantee has to be rebuilt from two APIs that are
/// each individually incapable of leaking anything:
///
///   1. `GetLastInputInfo` returns a `LASTINPUTINFO`, whose entire payload is a
///      `DWORD` tick count of the last input event of ANY kind. It cannot report what
///      the input was, let alone which key. It needs no permission and no manifest
///      capability. This is the load-bearing one.
///
///   2. A low-level MOUSE hook (`WH_MOUSE_LL`) counts mouse events and records when
///      the last one happened. Its callback receives `MSLLHOOKSTRUCT` — a cursor
///      position, a wheel delta, a timestamp and flags. There is no field in that
///      structure that could carry a keystroke.
///
/// A keystroke is then inferred, never observed: if the last-input tick advanced and
/// it is not the tick the mouse hook recorded, the input was not the mouse. The app
/// learns that *a* key was pressed and when. It cannot learn which, because neither
/// API it called was ever told.
///
/// That is strictly LESS information than the macOS build gets — which distinguishes
/// key events from scroll events at the source — and it is the same shape of
/// guarantee: structural, not a promise.
///
/// =============================================================================
/// WHAT MUST NEVER BE ADDED HERE
/// =============================================================================
/// `WH_KEYBOARD_LL`, `GetAsyncKeyState`/`GetKeyState` on a keyboard virtual-key,
/// `GetKeyboardState`, `RegisterRawInputDevices` with a keyboard usage page, and
/// `WH_JOURNALRECORD` would all report key identity. A low-level keyboard hook is
/// additionally an active filter that can suppress every keystroke system-wide — the
/// exact Windows counterpart of the `CGEventTap` that CLAUDE.md bans, and the reason
/// keyboard-hooking apps are what antivirus heuristics are looking for.
///
/// `scripts/check-privacy.sh` blocks every one of them at build time. If a feature
/// seems to require one, it is the wrong design. Say so.
/// =============================================================================
[SupportedOSPlatform("windows")]
public static class InputTelemetry
{
    // The hook's delegate must be held in a static field. If it is only a local, the
    // GC collects it while Windows still holds the function pointer, and the process
    // dies inside user32 at the next mouse move — with a stack that points nowhere
    // useful.
    private static Win32.HookProc? _hookProc;
    private static IntPtr _hook;
    private static Thread? _hookThread;
    private static uint _hookThreadId;

    private static long _mouseEvents;
    private static long _scrollEvents;
    private static int _lastMouseTick;
    private static int _leftDown;

    private static uint _prevInputTick;
    private static long _keyCount;
    private static double _lastKeyAt;

    // Watchdog. Windows silently unhooks a low-level hook whose thread does not
    // respond within LowLevelHooksTimeout (300ms by default). Our hook thread does
    // nothing but pump messages, so this should never fire — but "should never" and a
    // cat that stops reacting to the scroll wheel forever are not the same thing.
    private static long _mouseEventsAtLastCheck;
    private static int _silentMoves;

    public static bool HookInstalled => _hook != IntPtr.Zero;

    /// The struct's own size, which `GetLastInputInfo` requires as its first field and
    /// fails outright without.
    private static readonly uint LastInputInfoSize =
        (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.LastInputInfo>();

    public static void Start()
    {
        _lastKeyAt = Clock.Now;
        var info = new Win32.LastInputInfo { Size = LastInputInfoSize };
        if (Win32.GetLastInputInfo(ref info)) _prevInputTick = info.Time;

        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "loafcat.mouse",
            // Above normal: this thread has a 300ms deadline enforced by the OS and
            // does almost nothing, so it costs nothing to make sure it is scheduled.
            Priority = ThreadPriority.AboveNormal,
        };
        _hookThread.Start();
    }

    public static void Stop()
    {
        if (_hookThreadId != 0)
        {
            // Ends GetMessage, which unhooks on the way out — on the same thread that
            // installed the hook, which UnhookWindowsHookEx requires.
            Win32.PostThreadMessage(_hookThreadId, Win32.WmQuit, IntPtr.Zero, IntPtr.Zero);
        }
        _hookThread?.Join(500);
    }

    private static void HookThreadMain()
    {
        _hookThreadId = Win32.GetCurrentThreadId();
        _hookProc = MouseHookProc;
        _hook = Win32.SetWindowsHookEx(Win32.WhMouseLl, _hookProc, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero)
        {
            // Not fatal, and not worth retrying in a loop: the cat simply stops
            // reacting to the scroll wheel, and every keystroke-driven reaction still
            // works because those come from GetLastInputInfo.
            Log.Warn("input: could not install the mouse hook — scroll reactions are off");
            return;
        }

        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }

        Win32.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    /// Runs on every mouse event in the system, so it does the least work that is
    /// possible: three interlocked writes and a tail call. Anything slower here is
    /// felt by the user as a laggy mouse in every application, not just this one.
    private static IntPtr MouseHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            Interlocked.Increment(ref _mouseEvents);
            Interlocked.Exchange(ref _lastMouseTick, (int)Win32.GetTickCount());
            int message = (int)wParam;
            if (message == Win32.WmMouseWheelLl || message == Win32.WmMouseHWheelLl)
            {
                Interlocked.Increment(ref _scrollEvents);
            }
            // Button state comes from here rather than from GetAsyncKeyState, which is
            // banned: this hook is structurally mouse-only, whereas that call would
            // read key state the moment someone passed it a different constant.
            else if (message == Win32.WmLButtonDownLl)
            {
                Interlocked.Exchange(ref _leftDown, 1);
            }
            else if (message == Win32.WmLButtonUpLl)
            {
                Interlocked.Exchange(ref _leftDown, 0);
            }
        }
        // Always chained, never swallowed. This hook observes; it must never be able
        // to change what any other application receives.
        return Win32.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// Called once per tick, before anything reads the counters.
    ///
    /// The inference is the whole design: the last-input tick moved, and it is not the
    /// tick the mouse last fired on, therefore the input was not the mouse. What it
    /// actually was, this process has no way to find out.
    public static void Poll()
    {
        var info = new Win32.LastInputInfo { Size = LastInputInfoSize };
        if (!Win32.GetLastInputInfo(ref info)) return;

        uint tick = info.Time;
        if (tick == _prevInputTick) return;
        _prevInputTick = tick;

        uint mouseTick = (uint)Interlocked.CompareExchange(ref _lastMouseTick, 0, 0);
        // GetLastInputInfo and the hook both read GetTickCount, so a mouse event and
        // the input tick it caused agree exactly. A one-tick tolerance covers the case
        // where the counter rolls over between the two reads.
        uint gap = tick - mouseTick;
        if (gap > 1)
        {
            _keyCount++;
            _lastKeyAt = Clock.Now;
        }
    }

    /// Monotonic count of inferred keystrokes. A rate is all any caller wants; the
    /// count exists so the caller can difference it exactly like the macOS build does.
    public static long KeyCount => _keyCount;

    /// Monotonic count of scroll wheel events.
    public static long ScrollCount => Interlocked.Read(ref _scrollEvents);

    /// Whether the left mouse button is currently held, observed rather than queried.
    ///
    /// Exists for one purpose: a drag whose mouse-up never arrived — the window manager
    /// drops one if the window is reconfigured under a held button — would otherwise
    /// strand the cat stretched and mid-swing with no way back. Unknowable until the
    /// hook has seen at least one button event, which is fine: the safety net only has
    /// to work for a drag that already started, and starting one requires a press the
    /// hook saw.
    public static bool LeftButtonDown => Interlocked.CompareExchange(ref _leftDown, 0, 0) != 0;

    /// Seconds since the last inferred keystroke.
    public static double SecondsSinceKey => Clock.Now - _lastKeyAt;

    /// Called from the tick when the cursor position changed, so the watchdog can tell
    /// "the mouse is not moving" from "the hook stopped being called".
    public static void NoteCursorMoved()
    {
        long now = Interlocked.Read(ref _mouseEvents);
        if (now != _mouseEventsAtLastCheck)
        {
            _mouseEventsAtLastCheck = now;
            _silentMoves = 0;
            return;
        }
        // The cursor moved but no hook callback ran. One or two of these is a
        // scheduling artefact; a few hundred in a row means Windows dropped us.
        if (++_silentMoves < 240) return;

        _silentMoves = 0;
        Log.Warn("input: the mouse hook stopped firing — reinstalling");
        Stop();
        _hookThreadId = 0;
        Start();
    }
}
