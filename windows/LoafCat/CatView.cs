using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LoafCat.Interop;

namespace LoafCat;

/// Composites the rig into one layered window, once per frame.
///
/// The macOS build gives each body part its own `CALayer` and lets Core Animation do
/// the compositing on the GPU. Windows has no equivalent that also gives per-pixel
/// window alpha, so this draws the frame itself, in software, into the DIB section
/// that `UpdateLayeredWindow` presents.
///
/// That turns out to be the better trade here rather than a compromise:
///
///   * It is exact. Every part is blitted with an explicit nearest-neighbour inverse
///     map, so there is no interpolation mode to get wrong and no chance of GDI+
///     quietly resampling a layer. Pixel art has exactly one correct magnification
///     and this is the shortest path to it.
///   * It is cheap. The cat covers ~20k device pixels at 3x. Clearing and compositing
///     that costs microseconds; the expensive part of the frame is handing the buffer
///     to the compositor, which is why identical frames are dropped (see `Present`).
///
/// The window is LARGER than the cat: `atlas.Layout` adds a transparent margin so a
/// speech bubble and a countdown plate have somewhere to live. That margin is
/// symmetric on purpose — it keeps the surface's centre and the cat's centre the same
/// point, so every cursor-relative calculation elsewhere is unaffected by it, and the
/// hit mask stays indexable in plain cat-canvas coordinates.
[SupportedOSPlatform("windows")]
public sealed class CatView : IDisposable
{
    // Readable by modules: the view outlives a theme change and both of these are
    // replaced with it, so reaching them through the window is how a module stays
    // correct across a reload without Program.cs re-wiring it.
    public Atlas Atlas { get; }
    public Rig Rig { get; }

    /// Integer only. A fractional scale is the fastest way to make pixel art look like
    /// mush, and it cannot be fixed downstream.
    public double Scale { get; }

    /// Transient magnification on top of `Scale`, driven by the stretch break. Always
    /// lands on an integer *effective* scale at rest.
    public double Zoom { get; private set; } = 1;

    /// Logical-pixels-per-device-pixel actually in force this frame.
    public double EffectiveScale => Scale * Zoom;

    /// Where mouse events go. Set by whichever module handles them, so that Program.cs
    /// does not have to know that dragging exists.
    public ModuleRegistry? Modules { get; set; }

    /// Alpha mask of the composited silhouette, in logical pixels, dilated for
    /// hysteresis. Indexed every tick to decide `CursorOnCat`, so it must be a flat
    /// array lookup and never an image sample. Still in CAT-canvas coordinates,
    /// unchanged by the padding — `AtlasPoint` does the conversion.
    private bool[] _hitMask;
    private const int MaskDilation = 6;

    private double PadX => Atlas.Layout.PadX;
    private double PadY => Atlas.Layout.PadY;
    /// The padded canvas, in logical pixels.
    private double CanvasW => Atlas.Canvas + PadX * 2;
    private double CanvasH => Atlas.Canvas + PadY * 2;

    /// The window size this atlas and scale need, in device pixels. The cat itself is
    /// only `Atlas.Canvas * scale` of it; the rest is the bubble's margin.
    public static (int W, int H) PanelSize(Atlas atlas, double scale) => (
        (int)MathX.Round((atlas.Canvas + atlas.Layout.PadX * 2) * scale),
        (int)MathX.Round((atlas.Canvas + atlas.Layout.PadY * 2) * scale));

    // --- the surface --------------------------------------------------------

    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _oldBitmap;
    private IntPtr _bits;
    private int _widthPx;
    private int _heightPx;
    private uint[] _previous = [];
    private bool _everPresented;

    public int WidthPx => _widthPx;
    public int HeightPx => _heightPx;

    /// How many frames were composed but identical to the one already on screen, so
    /// the expensive present was skipped. Reported by `--selftest`.
    public long FramesSkipped { get; private set; }
    public long FramesPresented { get; private set; }

    // --- per-frame module channels -----------------------------------------

    private double _tint;
    private bool _auxHidden;

    private sealed class Aux
    {
        public required PixelBitmap Image;
        public required Pt AtlasOrigin;
    }

    private readonly Dictionary<string, Aux> _aux = [];

    public CatView(Atlas atlas, Rig rig, double scale)
    {
        Atlas = atlas;
        Rig = rig;
        Scale = scale;
        int side = (int)atlas.Canvas;
        _hitMask = new bool[side * side];

        var (w, h) = PanelSize(atlas, scale);
        CreateSurface(w, h);
        BuildHitMask();
    }

    // MARK: - geometry

    /// Magnifies the whole rig about its own centre. `1` is rest.
    public void SetZoom(double z) => Zoom = Math.Max(z, 0.01);

    /// The stretch break grows the window around the cat. Everything stays centred, so
    /// there is nothing to re-lay-out — only the surface has to be the new size.
    public void Resize(int widthPx, int heightPx)
    {
        if (widthPx == _widthPx && heightPx == _heightPx) return;
        CreateSurface(widthPx, heightPx);
    }

    private void CreateSurface(int widthPx, int heightPx)
    {
        DestroySurface();
        _widthPx = Math.Max(widthPx, 1);
        _heightPx = Math.Max(heightPx, 1);

        _memDc = Win32.CreateCompatibleDC(IntPtr.Zero);
        var header = new Win32.BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<Win32.BitmapInfoHeader>(),
            Width = _widthPx,
            // Negative height: a top-down DIB, so row 0 is the top row and the
            // compositor's y matches the atlas's y with nothing to flip.
            Height = -_heightPx,
            Planes = 1,
            BitCount = 32,
            Compression = Win32.BiRgb,
        };
        _dib = Win32.CreateDIBSection(
            _memDc, ref header, Win32.DibRgbColors, out _bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateDIBSection failed for {_widthPx}x{_heightPx} " +
                $"(error {Marshal.GetLastWin32Error()})");
        }
        _oldBitmap = Win32.SelectObject(_memDc, _dib);
        _previous = new uint[_widthPx * _heightPx];
        _everPresented = false;
    }

    private void DestroySurface()
    {
        if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero)
        {
            Win32.SelectObject(_memDc, _oldBitmap);
            _oldBitmap = IntPtr.Zero;
        }
        if (_dib != IntPtr.Zero) { Win32.DeleteObject(_dib); _dib = IntPtr.Zero; }
        if (_memDc != IntPtr.Zero) { Win32.DeleteDC(_memDc); _memDc = IntPtr.Zero; }
        _bits = IntPtr.Zero;
    }

    public void Dispose() => DestroySurface();

    // MARK: - module channels

    /// Fades the coat toward the atlas's calm colour. 0 is the cat's own colours.
    public void SetTint(double amount) => _tint = MathX.Clamp(amount, 0, 1);

    /// Places a 1x pixel bitmap in the canvas, above the cat. `atlasOrigin` is its
    /// top-left in CAT-canvas coordinates, y-down; negative values reach into the
    /// transparent margin, which is exactly where the bubble goes.
    ///
    /// Passing null removes it.
    public void SetAux(string key, PixelBitmap? image, Pt atlasOrigin)
    {
        if (image is null) { _aux.Remove(key); return; }
        _aux[key] = new Aux { Image = image, AtlasOrigin = atlasOrigin };
    }

    public void SetAuxHidden(bool hidden) => _auxHidden = hidden;

    // MARK: - hit mask

    /// Rasterises the default-pose silhouette once, then dilates it.
    ///
    /// On macOS the dilation is what makes click-through feel solid: that build polls
    /// the cursor at 120Hz and toggles a whole-window flag, so becoming interactive a
    /// few pixels early is what stops a click at the boundary being lost to the race.
    ///
    /// Windows has no such race — the window manager hit-tests the layered window's
    /// actual alpha, synchronously, before the click is delivered. So here the dilated
    /// mask does NOT decide clicks. It survives as the definition of `CursorOnCat`,
    /// which is a proximity question ("is the cursor near enough to count as petting,
    /// or as having noticed an alert") and wants to stay generous on both platforms.
    private void BuildHitMask()
    {
        int side = (int)Atlas.Canvas;
        var raw = new bool[side * side];

        foreach (string name in Atlas.Order)
        {
            if (name.StartsWith("lid_", StringComparison.Ordinal) || name == "shadow") continue;
            if (!Atlas.Parts.TryGetValue(name, out var part)) continue;
            int w = (int)part.Size.W, h = (int)part.Size.H;
            if (w <= 0 || h <= 0) continue;

            var img = part.Image;
            for (int py = 0; py < h; py++)
            {
                for (int px = 0; px < w; px++)
                {
                    if (img[px, py].A <= 40) continue;
                    int gx = (int)part.Origin.X + px;
                    int gy = (int)part.Origin.Y + py;
                    if (gx >= 0 && gx < side && gy >= 0 && gy < side) raw[gy * side + gx] = true;
                }
            }
        }

        // Square dilation. Cheap, and at this size indistinguishable from circular.
        var outMask = (bool[])raw.Clone();
        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                if (raw[y * side + x]) continue;
                bool near = false;
                for (int dy = -MaskDilation; dy <= MaskDilation && !near; dy++)
                {
                    for (int dx = -MaskDilation; dx <= MaskDilation && !near; dx++)
                    {
                        int ny = y + dy, nx = x + dx;
                        if (ny >= 0 && ny < side && nx >= 0 && nx < side && raw[ny * side + nx])
                        {
                            near = true;
                        }
                    }
                }
                if (near) outMask[y * side + x] = true;
            }
        }
        _hitMask = outMask;
    }

    /// True when a point in client coordinates lands on (or near) the cat.
    public bool IsOnCat(Pt clientPoint) => AtlasPoint(clientPoint) is not null;

    /// A point in CLIENT coordinates (device pixels, y-down from the window's
    /// top-left) as ATLAS coordinates — logical pixels, y-down — or null when it is
    /// not on the cat. Modules reason in the same space as the rig and cat.json, and
    /// never have to know about scale, padding or zoom.
    ///
    /// Measured out from the surface's CENTRE rather than a corner, because the centre
    /// is the one point the padding and the stretch zoom both leave alone.
    public Pt? AtlasPoint(Pt clientPoint)
    {
        int side = (int)Atlas.Canvas;
        double sc = EffectiveScale;
        if (sc <= 0) return null;
        double half = Atlas.Canvas / 2;
        double midX = _widthPx / 2.0;
        double midY = _heightPx / 2.0;

        int lx = (int)Math.Floor((clientPoint.X - midX) / sc + half);
        // The macOS build works in a y-up view, so its formula reads
        // `side - 1 - floor((y - midY)/sc + half)`. Client coordinates here are
        // y-DOWN, which flips the sign of the centre-relative term; keeping the
        // `side - 1 - floor(...)` shape rather than simplifying it means both builds
        // round the same way, including at the exact centre where they differ by one.
        double lyUp = -((clientPoint.Y - midY) / sc) + half;
        int ly = side - 1 - (int)Math.Floor(lyUp);

        if (lx < 0 || lx >= side || ly < 0 || ly >= side) return null;
        if (!_hitMask[ly * side + lx]) return null;
        return new Pt(lx, ly);
    }

    // MARK: - compositing

    /// Composes this frame into the DIB. Called once per tick.
    ///
    /// Draw order matches the macOS layer tree exactly: for each body part, the part,
    /// then its tint wash, then its overheat coat; then every overlay sprite; then the
    /// aux bitmaps (bubble, countdown plate), which sit above everything.
    public unsafe void Compose()
    {
        if (_bits == IntPtr.Zero) return;
        var stage = CatStage.Shared;
        double heat = MathX.Clamp(stage.Heat, 0, 1);

        uint* buf = (uint*)_bits;
        // A DIB section is not guaranteed to come back zeroed after the first frame.
        new Span<uint>(buf, _widthPx * _heightPx).Clear();

        double sc = EffectiveScale;
        // The container is centred on the surface, so growing the window for a stretch
        // break moves nothing relative to the cat.
        double originX = MathX.Round(_widthPx / 2.0) - CanvasW * sc / 2;
        double originY = MathX.Round(_heightPx / 2.0) - CanvasH * sc / 2;

        foreach (string name in Atlas.Order)
        {
            if (!Atlas.Parts.TryGetValue(name, out var part)) continue;
            var t = Rig.TransformFor(name);
            if (t.Hidden) continue;

            var pivot = Atlas.Pivot(name);
            var placement = Place(part, t, pivot, originX, originY, sc);

            Blit(buf, part.Image, placement, 1.0, null);

            if (_tint > 0.002 && Atlas.Wellness.TintParts.Contains(name))
            {
                // A colour wash masked by the part's own alpha. Because the mask is
                // the same nearest-sampled bitmap, it cannot introduce a soft edge the
                // rest of the art does not have.
                Blit(buf, part.Image, placement, _tint, Atlas.Wellness.Tint);
            }

            // The hot coat is the same art on the same grid, so it reuses the base
            // part's placement outright rather than recomputing it.
            if (heat >= 0.004 && Atlas.HotParts.Contains(name) &&
                Atlas.Parts.TryGetValue($"{name}_hot", out var hot))
            {
                Blit(buf, hot.Image, placement, heat, null);
            }
        }

        ComposeOverlays(buf, originX, originY, sc);

        if (!_auxHidden)
        {
            foreach (var (_, aux) in _aux)
            {
                var placement = PlaceRaw(
                    aux.AtlasOrigin, aux.Image.Width, aux.Image.Height,
                    originX, originY, sc);
                Blit(buf, aux.Image, placement, 1.0, null);
            }
        }
    }

    /// Places whatever the modules asked for into the sprite's slots. Anything beyond
    /// a sprite's slot count is dropped.
    ///
    /// One path for every overlay: the reaction sprites, which carry their own motion,
    /// and the agent's status glyphs, which name a body part to `follow` in the atlas
    /// and so drift with the head turn instead of sitting rigidly in the corner. Which
    /// of the two a sprite is, is data rather than a branch here.
    private unsafe void ComposeOverlays(uint* buf, double originX, double originY, double sc)
    {
        var stage = CatStage.Shared;
        if (stage.Overlays.Count == 0) return;

        Dictionary<string, int>? used = null;
        foreach (var inst in stage.Overlays)
        {
            if (!Atlas.Overlays.TryGetValue(inst.Part, out var overlay)) continue;

            used ??= [];
            used.TryGetValue(inst.Part, out int i);
            if (i >= overlay.Slots) continue;
            used[inst.Part] = i + 1;

            double a = MathX.Clamp(inst.Alpha, 0, 1);
            if (a < 0.004) continue;

            var offset = inst.Offset;
            if (overlay.Follow is { } follow)
            {
                var t = Rig.TransformFor(follow);
                offset.X += t.Offset.X;
                offset.Y += t.Offset.Y;
            }

            // Straight through the same rounding as every body part, so an overlay is
            // never the thing that shimmers.
            var part = overlay.Part;
            var placement = PlaceRaw(
                new Pt(part.Origin.X + offset.X, part.Origin.Y + offset.Y),
                (int)part.Size.W, (int)part.Size.H, originX, originY, sc);
            Blit(buf, part.Image, placement, a, null);
        }
    }

    /// Where a part lands and how its source pixels map there.
    ///
    /// `StepX`/`StepY` are how far the source advances per device pixel — the inverse
    /// of the transform, precomputed so the blit's inner loop is two adds and a lookup.
    private readonly record struct Placement(
        int X0, int Y0, int X1, int Y1,
        double SrcX0, double SrcY0, double StepX, double StepY,
        int SrcW, int SrcH);

    private Placement Place(Atlas.Part part, Rig.Transform t, Pt pivot,
                            double originX, double originY, double sc)
    {
        // Round on LOGICAL pixels, then add the (integer) padding, then scale.
        // Rounding after scaling would still land on fractional logical positions and
        // make the art crawl at 2x and 3x.
        double x0 = MathX.Round(part.Origin.X + t.Offset.X) + PadX;
        double y0 = MathX.Round(part.Origin.Y + t.Offset.Y) + PadY;

        double sw = t.Scale.W, sh = t.Scale.H;
        if (sw == 0) sw = 0.0001;
        if (sh == 0) sh = 0.0001;

        // Pivot expressed inside the part, in logical pixels from its top-left.
        double pvx = pivot.X - part.Origin.X;
        double pvy = pivot.Y - part.Origin.Y;

        int w = (int)part.Size.W, h = (int)part.Size.H;

        // The transformed extent, in logical container coordinates.
        double left = x0 + pvx * (1 - sw);
        double top = y0 + pvy * (1 - sh);
        double right = left + w * sw;
        double bottom = top + h * sh;

        return Bounds(left, top, right, bottom, x0, y0, pvx, pvy, sw, sh,
                      w, h, originX, originY, sc);
    }

    /// The same placement for something with no pivot and no scale — an overlay
    /// sprite or an aux bitmap.
    private Placement PlaceRaw(Pt atlasOrigin, int w, int h,
                               double originX, double originY, double sc)
    {
        double x0 = MathX.Round(atlasOrigin.X) + PadX;
        double y0 = MathX.Round(atlasOrigin.Y) + PadY;
        return Bounds(x0, y0, x0 + w, y0 + h, x0, y0, 0, 0, 1, 1,
                      w, h, originX, originY, sc);
    }

    private Placement Bounds(double left, double top, double right, double bottom,
                             double x0, double y0, double pvx, double pvy,
                             double sw, double sh, int w, int h,
                             double originX, double originY, double sc)
    {
        int px0 = Math.Max((int)Math.Floor(originX + left * sc), 0);
        int py0 = Math.Max((int)Math.Floor(originY + top * sc), 0);
        int px1 = Math.Min((int)Math.Ceiling(originX + right * sc), _widthPx);
        int py1 = Math.Min((int)Math.Ceiling(originY + bottom * sc), _heightPx);

        // Sample at device-pixel CENTRES. At an integer scale with an integer origin
        // this puts every source pixel on exactly `sc` device pixels, which is the
        // whole point; at the fractional vertical scale a drag produces, it is a plain
        // nearest-neighbour resample with no bias toward either edge.
        double stepX = 1.0 / (sc * sw);
        double stepY = 1.0 / (sc * sh);
        double srcX0 = pvx + ((px0 + 0.5 - originX) / sc - x0 - pvx) / sw;
        double srcY0 = pvy + ((py0 + 0.5 - originY) / sc - y0 - pvy) / sh;

        return new Placement(px0, py0, px1, py1, srcX0, srcY0, stepX, stepY, w, h);
    }

    /// The inner loop. Nearest-neighbour, straight-alpha source, premultiplied
    /// destination.
    ///
    /// `tint` replaces the source colour with a flat one while keeping the source's
    /// alpha as a coverage mask — that is exactly what the macOS build's masked tint
    /// layer does, and doing it here costs one branch instead of a second surface.
    private unsafe void Blit(uint* dst, PixelBitmap src, Placement p, double alpha, Rgba? tint)
    {
        if (alpha <= 0 || p.X1 <= p.X0 || p.Y1 <= p.Y0) return;
        int layerAlpha = (int)(MathX.Clamp(alpha, 0, 1) * 255 + 0.5);
        if (layerAlpha == 0) return;

        byte[] pixels = src.Pixels;
        int srcW = src.Width, srcH = src.Height;
        // The atlas can declare a part larger than its PNG if the two ever drift; clamp
        // rather than trusting the declared size, because reading past the array here
        // would be an access violation rather than a wrong pixel.
        int limitW = Math.Min(p.SrcW, srcW);
        int limitH = Math.Min(p.SrcH, srcH);

        bool flat = tint.HasValue;
        byte tr = 0, tg = 0, tb = 0;
        int tintA = 255;
        if (tint is { } t)
        {
            tr = t.R; tg = t.G; tb = t.B; tintA = t.A;
        }

        double sy = p.SrcY0;
        for (int y = p.Y0; y < p.Y1; y++, sy += p.StepY)
        {
            int iy = (int)Math.Floor(sy);
            if ((uint)iy >= (uint)limitH) continue;

            uint* row = dst + (long)y * _widthPx;
            int srcRow = iy * srcW * 4;
            double sx = p.SrcX0;

            for (int x = p.X0; x < p.X1; x++, sx += p.StepX)
            {
                int ix = (int)Math.Floor(sx);
                if ((uint)ix >= (uint)limitW) continue;

                int i = srcRow + ix * 4;
                int a = pixels[i + 3];
                if (a == 0) continue;

                byte r, g, b;
                if (flat)
                {
                    r = tr; g = tg; b = tb;
                    a = a * tintA / 255;
                }
                else
                {
                    r = pixels[i]; g = pixels[i + 1]; b = pixels[i + 2];
                }

                int ea = a * layerAlpha / 255;
                if (ea <= 0) continue;

                if (ea >= 255)
                {
                    row[x] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
                    continue;
                }

                uint d = row[x];
                int inv = 255 - ea;
                int dr = (int)((d >> 16) & 0xFF);
                int dg = (int)((d >> 8) & 0xFF);
                int db = (int)(d & 0xFF);
                int da = (int)((d >> 24) & 0xFF);

                uint nr = (uint)((r * ea + dr * inv + 127) / 255);
                uint ng = (uint)((g * ea + dg * inv + 127) / 255);
                uint nb = (uint)((b * ea + db * inv + 127) / 255);
                uint na = (uint)((255 * ea + da * inv + 127) / 255);
                row[x] = (na << 24) | (nr << 16) | (ng << 8) | nb;
            }
        }
    }

    // MARK: - presenting

    /// Hands the composed frame to the window manager, unless it is byte-for-byte the
    /// frame already on screen.
    ///
    /// That check is worth its cost specifically because this is pixel art. Every
    /// position is quantised to a whole logical pixel before it is scaled, so the
    /// continuous ambient motion — the breathing sine, the tail spring — spends most
    /// of its time producing the SAME frame twice. Comparing 600KB is a SIMD memcmp;
    /// `UpdateLayeredWindow` is a trip through the desktop compositor. Skipping the
    /// second is measurably the larger saving, and it is why an idle cat costs almost
    /// nothing.
    public unsafe bool Present(IntPtr hwnd, int screenX, int screenY)
    {
        if (_bits == IntPtr.Zero || hwnd == IntPtr.Zero) return false;

        var composed = new ReadOnlySpan<uint>((void*)_bits, _widthPx * _heightPx);
        if (_everPresented && composed.SequenceEqual(_previous))
        {
            FramesSkipped++;
            return false;
        }
        composed.CopyTo(_previous);
        _everPresented = true;

        var dst = new Win32.Point(screenX, screenY);
        var src = new Win32.Point(0, 0);
        var size = new Win32.Size(_widthPx, _heightPx);
        var blend = new Win32.BlendFunction
        {
            BlendOp = Win32.AcSrcOver,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            // The buffer above is premultiplied, which is what this flag promises.
            AlphaFormat = Win32.AcSrcAlpha,
        };

        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        try
        {
            bool ok = Win32.UpdateLayeredWindow(
                hwnd, screenDc, ref dst, ref size, _memDc, ref src, 0,
                ref blend, Win32.UlwAlpha);
            if (ok) FramesPresented++;
            return ok;
        }
        finally
        {
            Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// The composed alpha at a client point, for the self-test. This is what the
    /// window manager hit-tests against, so checking it is how the click-through claim
    /// gets verified without a human clicking anything.
    public unsafe byte AlphaAt(int x, int y)
    {
        if (_bits == IntPtr.Zero) return 0;
        if ((uint)x >= (uint)_widthPx || (uint)y >= (uint)_heightPx) return 0;
        uint* buf = (uint*)_bits;
        return (byte)((buf[(long)y * _widthPx + x] >> 24) & 0xFF);
    }

    /// How many logical pixels the dilated silhouette covers. Used by `--selftest` to
    /// prove the mask is neither empty nor the whole canvas.
    public int HitMaskCount()
    {
        int n = 0;
        foreach (bool b in _hitMask)
        {
            if (b) n++;
        }
        return n;
    }
}
