using System.Runtime.Versioning;
using System.Windows.Forms;
using LoafCat.Interop;

namespace LoafCat;

/// The window the cat lives in.
///
/// A borderless, layered, topmost tool window — the closest Windows has to the macOS
/// build's non-activating `NSPanel` at level 101. Four extended styles carry almost all
/// of the behaviour; each is load-bearing and none is decoration:
///
///   WS_EX_LAYERED     per-pixel alpha, and per-pixel hit testing. See below.
///   WS_EX_TOOLWINDOW  keeps the cat out of Alt-Tab and off the taskbar.
///   WS_EX_NOACTIVATE  clicking the cat does not steal focus from the editor.
///   WS_EX_TOPMOST     above ordinary windows, including the taskbar.
///
/// =============================================================================
/// CLICK-THROUGH: THE ONE PLACE WINDOWS IS SIMPLY BETTER
/// =============================================================================
/// `spikes/RESULTS.md` records that macOS has no free per-pixel click-through: a
/// transparent NSWindow takes every click regardless of alpha, overriding `hitTest`
/// makes the event reach nothing at all, and the only thing that works is polling the
/// cursor at 120Hz, sampling a dilated alpha mask, and toggling `ignoresMouseEvents` on
/// boolean transitions. That measured 97% against 88% for the event-driven version.
///
/// None of that applies here. A layered window created with `UpdateLayeredWindow` is
/// hit-tested by the window manager against the alpha channel we just handed it,
/// synchronously, before the click is delivered to anyone. Transparent pixels fall
/// through to the window underneath for free and without a race.
///
/// So this port deliberately does NOT reproduce the polling toggle, and the 6px
/// dilation does not apply to clicks — an exact-alpha test cannot lose a boundary
/// click, because there is no window between the test and the delivery. The dilated
/// mask stays as the definition of `CursorOnCat`, which is a different question
/// (proximity, for petting and for noticing an alert) and wants to stay generous.
///
/// `WS_EX_TRANSPARENT` is used for exactly one thing: while the cat is switched off,
/// so an invisible window cannot swallow clicks in its own rectangle.
/// =============================================================================
[SupportedOSPlatform("windows")]
public sealed class CatWindow : Form
{
    public CatView? View { get; private set; }

    private Rect _frame;
    private bool _forcePresent = true;
    private bool _catVisible = true;

    /// Screen position of the last mouse event, for computing drag deltas. Taken from
    /// the screen rather than the window because the window MOVES during a drag, which
    /// makes client-relative deltas meaningless.
    private Pt? _lastMouseScreen;

    public ModuleRegistry? Modules { get; set; }

    public CatWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        // Never painted by WinForms: every pixel comes from UpdateLayeredWindow. Without
        // this the form would clear itself to the control colour first, which on a
        // layered window shows up as a grey box for one frame at startup.
        SetStyle(ControlStyles.Opaque, true);
        Text = "loafcat";
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)(Win32.WsExLayered | Win32.WsExToolWindow |
                                Win32.WsExNoActivate | Win32.WsExTopmost);
            return cp;
        }
    }

    /// Never take focus, whatever is clicked. The counterpart of `.nonactivatingPanel`.
    protected override bool ShowWithoutActivation => true;

    // MARK: - geometry

    public Rect Frame => _frame;

    public void SetFrame(Rect r)
    {
        _frame = r;
        View?.Resize((int)MathX.Round(r.W), (int)MathX.Round(r.H));
        _forcePresent = true;
    }

    public void SetOrigin(double x, double y)
    {
        _frame.X = x;
        _frame.Y = y;
        _forcePresent = true;
    }

    /// Rebuilds the surface for a new theme or scale, keeping the cat where it stands.
    /// Anchored on the BOTTOM-CENTRE so a resize looks like the cat growing from the
    /// floor rather than teleporting.
    public void Adopt(CatView view)
    {
        var old = _frame;
        View?.Dispose();
        View = view;
        view.Modules = Modules;

        double w = view.WidthPx, h = view.HeightPx;
        _frame = old.W > 0
            ? new Rect(MathX.Round(old.MidX - w / 2), MathX.Round(old.MaxY - h), w, h)
            : new Rect(0, 0, w, h);
        _forcePresent = true;
    }

    /// Places the cat where the macOS build places it on first launch: horizontally
    /// centred on the work area, 120 logical pixels up from its bottom edge.
    public void PlaceForFirstRun()
    {
        var work = Screens.Primary().Work;
        double w = View?.WidthPx ?? 256;
        double h = View?.HeightPx ?? 268;
        _frame = new Rect(
            MathX.Round(work.MidX - w / 2),
            MathX.Round(work.MaxY - 120 - h),
            w, h);
        _forcePresent = true;
    }

    public void Centre()
    {
        var work = Screens.Holding(_frame).Work;
        double w = View?.WidthPx ?? _frame.W;
        double h = View?.HeightPx ?? _frame.H;
        _frame = new Rect(
            MathX.Round(work.MidX - w / 2), MathX.Round(work.MidY - h / 2), w, h);
        _forcePresent = true;
    }

    // MARK: - visibility

    public void SetCatVisible(bool visible)
    {
        _catVisible = visible;
        if (visible)
        {
            SetClickThroughEverywhere(false);
            Win32.ShowWindow(Handle, Win32.SwShowNoActivate);
            _forcePresent = true;
        }
        else
        {
            Win32.ShowWindow(Handle, Win32.SwHide);
            // Or the hidden window keeps swallowing clicks in its own rectangle. Hiding
            // alone is enough on Windows, but this is belt and braces for the case where
            // a shell extension keeps a hidden topmost window in the hit-test order.
            SetClickThroughEverywhere(true);
        }
    }

    private void SetClickThroughEverywhere(bool on)
    {
        if (!IsHandleCreated) return;
        var style = (uint)Win32.GetWindowLongPtr(Handle, Win32.GwlExStyle);
        uint updated = on ? style | Win32.WsExTransparent : style & ~Win32.WsExTransparent;
        if (updated != style)
        {
            Win32.SetWindowLongPtr(Handle, Win32.GwlExStyle, (IntPtr)updated);
        }
    }

    /// Reasserts topmost. A full-screen application that comes and goes can leave any
    /// topmost window behind it in the z-order; nothing about this window makes it
    /// immune, and a cat that has quietly slipped behind the taskbar looks like a crash.
    public void ReassertTopmost()
    {
        if (!IsHandleCreated || !_catVisible) return;
        Win32.SetWindowPos(Handle, Win32.HwndTopmost, 0, 0, 0, 0,
            Win32.SwpNoMove | Win32.SwpNoSize | Win32.SwpNoActivate);
    }

    // MARK: - presenting

    public void Present()
    {
        if (View is not { } view || !IsHandleCreated || !_catVisible) return;
        view.Compose();
        if (_forcePresent)
        {
            // A moved or resized window has to be handed to UpdateLayeredWindow even
            // when the pixels are identical, because that call is what carries the new
            // position and size.
            Win32.SetWindowPos(Handle, IntPtr.Zero,
                (int)MathX.Round(_frame.X), (int)MathX.Round(_frame.Y),
                view.WidthPx, view.HeightPx,
                Win32.SwpNoZOrder | Win32.SwpNoActivate);
            _forcePresent = false;
        }
        view.Present(Handle, (int)MathX.Round(_frame.X), (int)MathX.Round(_frame.Y));
    }

    // MARK: - mouse

    /// Client coordinates of a mouse message, in device pixels, y-down.
    private static Pt ClientPointOf(Message m)
    {
        int lp = (int)m.LParam;
        return new Pt((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
    }

    private static Pt CursorScreen()
    {
        Win32.GetCursorPos(out var p);
        return new Pt(p.X, p.Y);
    }

    protected override void WndProc(ref Message m)
    {
        switch ((uint)m.Msg)
        {
            case Win32.WmLButtonDown:
            {
                _lastMouseScreen = CursorScreen();
                // Capture, so a fast drag that outruns the silhouette keeps delivering
                // moves here instead of to whatever is underneath. The macOS build gets
                // this from the view having taken the mouse on mouseDown.
                Win32.SetCapture(Handle);
                if (View?.AtlasPoint(ClientPointOf(m)) is { } p)
                {
                    Modules?.MouseDown(p);
                }
                return;
            }

            case Win32.WmMouseMove:
            {
                if (_lastMouseScreen is not { } prev) break;
                var now = CursorScreen();
                _lastMouseScreen = now;
                double sc = View?.EffectiveScale ?? 1;
                if (sc <= 0) break;
                // Logical pixels, y-down, to match the atlas convention every module
                // uses. Both axes are a plain division here — Windows screen space is
                // already y-down, so unlike the macOS build there is no flip.
                Modules?.MouseDragged(new Pt((now.X - prev.X) / sc, (now.Y - prev.Y) / sc));
                return;
            }

            case Win32.WmLButtonUp:
            {
                _lastMouseScreen = null;
                Win32.ReleaseCapture();
                // Deliberately not gated on hit-testing: a release outside the
                // silhouette is still a release, and swallowing it would strand the cat
                // mid-drag.
                Modules?.MouseUp(View?.AtlasPoint(ClientPointOf(m)) ?? Pt.Zero);
                return;
            }

            case Win32.WmDisplayChange:
                // A monitor was added, removed or re-arranged. The cat may now be at
                // coordinates that no longer exist.
                ClampIntoView();
                break;
        }
        base.WndProc(ref m);
    }

    /// Drags the cat back onto a real display.
    ///
    /// Unplugging a second monitor leaves any window that was on it at coordinates the
    /// desktop no longer covers, and a window with no title bar cannot be dragged back
    /// by the user. Keeping a corner on-screen is the difference between "my cat moved"
    /// and "my cat is gone and I have to reinstall".
    public void ClampIntoView()
    {
        var work = Screens.Holding(_frame).Work;
        if (_frame.IntersectionArea(work) > 0) return;

        _frame.X = MathX.Round(MathX.Clamp(_frame.X, work.MinX, work.MaxX - _frame.W));
        _frame.Y = MathX.Round(MathX.Clamp(_frame.Y, work.MinY, work.MaxY - _frame.H));
        _forcePresent = true;
        Log.Line($"window  display change — moved back into view at " +
                 $"({(int)_frame.X}, {(int)_frame.Y})");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) View?.Dispose();
        base.Dispose(disposing);
    }
}
