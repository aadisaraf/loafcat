using System.Runtime.InteropServices;

namespace LoafCat.Interop;

/// Every Win32 entry point the app uses, in one file.
///
/// Kept together rather than scattered next to their callers so that "what does
/// loafcat actually ask the operating system for" is one file somebody can read —
/// the same reason `scripts/check-privacy.sh` exists. Nothing in here needs a
/// permission, an elevation or a capability declaration.
internal static class Win32
{
    // --- window styles ------------------------------------------------------

    public const int GwlExStyle = -20;
    public const int GwlStyle = -16;

    public const uint WsPopup = 0x8000_0000;
    public const uint WsVisible = 0x1000_0000;

    /// Per-pixel alpha, and — the part that matters — per-pixel HIT TESTING. See the
    /// long note in CatWindow.cs: this is the one place Windows is simply better than
    /// macOS for this app.
    public const uint WsExLayered = 0x0008_0000;

    /// Keeps the cat out of Alt-Tab and off the taskbar. The counterpart to macOS's
    /// `.ignoresCycle` plus `LSUIElement`.
    public const uint WsExToolWindow = 0x0000_0080;

    /// Clicking the cat must not take focus away from whatever the user is typing in.
    /// The counterpart to `NSPanel(.nonactivatingPanel)`.
    public const uint WsExNoActivate = 0x0800_0000;

    public const uint WsExTopmost = 0x0000_0008;

    /// Set while the cat is off, so an invisible window cannot swallow clicks in its
    /// own rectangle. Not used for ordinary click-through — the layered window's alpha
    /// already does that, exactly.
    public const uint WsExTransparent = 0x0000_0020;

    // --- messages -----------------------------------------------------------

    public const uint WmDestroy = 0x0002;
    public const uint WmClose = 0x0010;
    public const uint WmQuit = 0x0012;
    public const uint WmMouseMove = 0x0200;
    public const uint WmLButtonDown = 0x0201;
    public const uint WmLButtonUp = 0x0202;
    public const uint WmRButtonDown = 0x0204;
    public const uint WmRButtonUp = 0x0205;
    public const uint WmMouseWheel = 0x020A;
    public const uint WmDisplayChange = 0x007E;
    public const uint WmDpiChanged = 0x02E0;
    public const uint WmTimer = 0x0113;
    public const uint WmApp = 0x8000;

    /// Posted to the cat window from the tray/menu thread when the app must quit.
    public const uint WmLoafcatQuit = WmApp + 1;

    // --- SetWindowPos -------------------------------------------------------

    public static readonly IntPtr HwndTopmost = new(-1);
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;
    public const uint SwpNoZOrder = 0x0004;

    public const int SwHide = 0;
    public const int SwShowNoActivate = 4;

    // --- structures ---------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
        public Point(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Size
    {
        public int Cx;
        public int Cy;
        public Size(int cx, int cy) { Cx = cx; Cy = cy; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RectL
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    public const byte AcSrcOver = 0x00;
    public const byte AcSrcAlpha = 0x01;
    public const uint UlwAlpha = 0x0000_0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    public const uint BiRgb = 0;
    public const uint DibRgbColors = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public uint Size;
        public RectL Monitor;
        /// The work area — excludes the taskbar and any docked appbar. The direct
        /// counterpart to `NSScreen.visibleFrame`, and the reason the cat never starts
        /// life underneath the taskbar.
        public RectL Work;
        public uint Flags;
    }

    public const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct LastInputInfo
    {
        public uint Size;
        /// Tick count of the last input event. An unsigned integer, and the ONLY
        /// thing this API returns — see InputTelemetry.cs.
        public uint Time;
    }

    // --- user32 -------------------------------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr newLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst,
        ref Point pptDst, ref Size psize, IntPtr hdcSrc, ref Point pptSrc,
        uint crKey, ref BlendFunction pblend, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetCursorPos(out Point p);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr MonitorFromPoint(Point pt, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetMonitorInfoW")]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip,
        MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern short GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// Ends the hook thread's message loop from another thread. A low-level hook has
    /// to be removed on the thread that installed it, so this is how that thread is
    /// asked to fall out of GetMessage and unhook itself on the way.
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostThreadMessage(uint threadId, uint msg,
        IntPtr wParam, IntPtr lParam);

    // GetAsyncKeyState and GetKeyState are deliberately ABSENT. The one thing this app
    // wanted from them — "is the left mouse button still held?", as a safety net for a
    // drag whose mouse-up went missing — is tracked by the mouse hook instead, which
    // cannot be pointed at a keyboard virtual-key by a later edit. See
    // InputTelemetry.LeftButtonDown, and scripts/check-privacy.sh, which now blocks
    // both functions outright rather than having to reason about their argument.

    // --- gdi32 --------------------------------------------------------------

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader bmi,
        uint usage, out IntPtr bits, IntPtr section, uint offset);

    // --- hooks (MOUSE ONLY — see InputTelemetry.cs) -------------------------

    /// A low-level MOUSE hook. Permission-free, and structurally incapable of
    /// observing the keyboard: the only payload it can deliver is MSLLHOOKSTRUCT,
    /// which carries a cursor position, a wheel delta and a timestamp.
    ///
    /// WH_KEYBOARD_LL, its keyboard sibling, is banned by scripts/check-privacy.sh.
    public const int WhMouseLl = 14;

    public const int WmLButtonDownLl = 0x0201;
    public const int WmLButtonUpLl = 0x0202;
    public const int WmMouseWheelLl = 0x020A;
    public const int WmMouseHWheelLl = 0x020E;

    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn,
        IntPtr hMod, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int code,
        IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetMessage(out Msg msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref Msg msg);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    public static extern uint GetTickCount();

    // --- the frame clock ----------------------------------------------------

    /// Sub-millisecond timer resolution WITHOUT `timeBeginPeriod`.
    ///
    /// The classic way to hit 120Hz on Windows is `timeBeginPeriod(1)`, which raises the
    /// timer resolution for the WHOLE SYSTEM and measurably costs battery on every other
    /// process. A high-resolution waitable timer (Windows 10 1803+) gets the same
    /// accuracy for this thread alone, which is the right trade for a desktop pet that
    /// is meant to be running all day.
    public const uint CreateWaitableTimerHighResolution = 0x0000_0002;
    public const uint TimerAllAccess = 0x1F_0003;
    public const uint WaitObject0 = 0;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, string? name,
        uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period,
        IntPtr routine, IntPtr arg, bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);
}
