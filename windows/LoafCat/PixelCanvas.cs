using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.Versioning;

namespace LoafCat;

/// An RGBA colour in 8-bit channels, straight from the atlas palette.
public struct Rgba(byte r, byte g, byte b, byte a)
{
    public byte R = r, G = g, B = b, A = a;

    public static readonly Rgba Clear = new(0, 0, 0, 0);

    /// Parses `#RRGGBBAA` (or `#RRGGBB`). The atlas writes the 8-digit form.
    public static Rgba? FromHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        ReadOnlySpan<char> s = hex.AsSpan();
        if (s.Length > 0 && s[0] == '#') s = s[1..];
        if (s.Length != 6 && s.Length != 8) return null;
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
            return null;

        return s.Length == 6
            ? new Rgba((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255)
            : new Rgba((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}

/// A surface of whole LOGICAL pixels, composed at 1x and magnified by an integer
/// factor with nearest-neighbour at draw time.
///
/// Composing UI art at 1x and magnifying is the only way to keep it in the same
/// visual language as the cat. Anything drawn at device resolution — a `Graphics`
/// scaled by 2, a `DrawString`, a WinForms label — lands on half-pixels and
/// anti-aliases its own edges, and no amount of downstream care gets that back.
///
/// Straight (non-premultiplied) RGBA, row 0 at the TOP, matching both the atlas and
/// the macOS port byte for byte. Premultiplication happens once, in the compositor,
/// because that is the only place that needs it.
public sealed class PixelBitmap
{
    public int Width { get; }
    public int Height { get; }

    /// Row-major RGBA. Public because the compositor's inner loop indexes it
    /// directly and a property accessor per pixel at 120Hz is not free.
    public readonly byte[] Pixels;

    public PixelBitmap(int width, int height)
    {
        Width = Math.Max(width, 0);
        Height = Math.Max(height, 0);
        Pixels = new byte[Width * Height * 4];
    }

    private PixelBitmap(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        Pixels = rgba;
    }

    /// Reads a PNG into logical pixels. Only ever called at load time.
    ///
    /// GDI+ hands back BGRA in memory for Format32bppArgb (little-endian "ARGB"),
    /// so the channel swap below is a fact about the platform rather than a choice.
    /// Getting it backwards is invisible on the mono theme, whose coat is grey —
    /// which is exactly how a bug like that survives to the first colour theme.
    [SupportedOSPlatform("windows")]
    public static PixelBitmap? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            // Loaded through a MemoryStream, not Image.FromFile: that keeps a lock on
            // the file for the lifetime of the Bitmap, and the theme directory has to
            // stay replaceable while the app is running.
            byte[] raw = File.ReadAllBytes(path);
            using var ms = new MemoryStream(raw, writable: false);
            using var src = new Bitmap(ms);
            return FromBitmap(src);
        }
        catch (Exception)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    public static PixelBitmap? FromBitmap(Bitmap src)
    {
        int w = src.Width, h = src.Height;
        if (w <= 0 || h <= 0) return null;

        var buf = new byte[w * h * 4];
        // Clone into a known format first: the source may be paletted, 24-bit, or
        // carry a colour profile, and LockBits will convert only if asked to.
        using var norm = src.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format32bppArgb);
        var data = norm.LockBits(
            new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* base_ = (byte*)data.Scan0;
                for (int y = 0; y < h; y++)
                {
                    byte* row = base_ + (long)y * data.Stride;
                    int o = y * w * 4;
                    for (int x = 0; x < w; x++)
                    {
                        buf[o + 0] = row[x * 4 + 2];   // R <- B
                        buf[o + 1] = row[x * 4 + 1];   // G
                        buf[o + 2] = row[x * 4 + 0];   // B <- R
                        buf[o + 3] = row[x * 4 + 3];   // A
                        o += 4;
                    }
                }
            }
        }
        finally
        {
            norm.UnlockBits(data);
        }
        return new PixelBitmap(w, h, buf);
    }

    public Rgba this[int x, int y]
    {
        get
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return Rgba.Clear;
            int i = (y * Width + x) * 4;
            return new Rgba(Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
        }
        set
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
            int i = (y * Width + x) * 4;
            Pixels[i] = value.R;
            Pixels[i + 1] = value.G;
            Pixels[i + 2] = value.B;
            Pixels[i + 3] = value.A;
        }
    }

    /// Copies every non-transparent pixel of `src`. Opaque-or-clear art only, so a
    /// straight copy is the correct blend and there is nothing to round.
    public void Blit(PixelBitmap src, int x, int y)
    {
        for (int sy = 0; sy < src.Height; sy++)
        {
            for (int sx = 0; sx < src.Width; sx++)
            {
                var c = src[sx, sy];
                if (c.A > 0) this[x + sx, y + sy] = c;
            }
        }
    }

    /// Inks a sub-rectangle of a coverage mask (the font sheet) in a flat colour.
    public void Stamp(PixelBitmap mask, int fromX, int fromY, int w, int h,
                      int x, int y, Rgba color)
    {
        for (int sy = 0; sy < h; sy++)
        {
            for (int sx = 0; sx < w; sx++)
            {
                if (mask[fromX + sx, fromY + sy].A > 128) this[x + sx, y + sy] = color;
            }
        }
    }

    /// True when nothing in here would draw. Used to skip empty overlay slots.
    public bool IsBlank()
    {
        for (int i = 3; i < Pixels.Length; i += 4)
        {
            if (Pixels[i] != 0) return false;
        }
        return true;
    }

    /// A GDI+ bitmap of the same pixels, magnified by an integer factor with
    /// nearest-neighbour. Only used for chrome — the tray icon and the theme
    /// thumbnails in Settings. The cat itself never goes through GDI+; see CatView.
    [SupportedOSPlatform("windows")]
    public Bitmap ToBitmap(int magnify = 1)
    {
        magnify = Math.Max(magnify, 1);
        var outBmp = new Bitmap(Width * magnify, Height * magnify, PixelFormat.Format32bppArgb);
        var data = outBmp.LockBits(
            new Rectangle(0, 0, outBmp.Width, outBmp.Height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* base_ = (byte*)data.Scan0;
                for (int y = 0; y < outBmp.Height; y++)
                {
                    byte* row = base_ + (long)y * data.Stride;
                    int sy = y / magnify;
                    for (int x = 0; x < outBmp.Width; x++)
                    {
                        int i = (sy * Width + x / magnify) * 4;
                        row[x * 4 + 0] = Pixels[i + 2];   // B
                        row[x * 4 + 1] = Pixels[i + 1];   // G
                        row[x * 4 + 2] = Pixels[i + 0];   // R
                        row[x * 4 + 3] = Pixels[i + 3];   // A
                    }
                }
            }
        }
        finally
        {
            outBmp.UnlockBits(data);
        }
        return outBmp;
    }
}
