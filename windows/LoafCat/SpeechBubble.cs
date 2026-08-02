namespace LoafCat;

/// The pixel font baked by `tools/generate_art.py`.
///
/// One strip PNG of white-on-transparent coverage plus per-glyph `{x, w}`. White
/// because it is a mask, not art: the runtime inks it in whatever colour the theme
/// asks for, so bubble text and countdown digits share one sheet.
public sealed class PixelFont
{
    public readonly record struct Glyph(int X, int W);

    public required PixelBitmap Sheet { get; init; }
    public required int CellHeight { get; init; }
    public required int Baseline { get; init; }
    public required int Tracking { get; init; }
    public required int SpaceWidth { get; init; }
    public required int LineGap { get; init; }
    public required char Fallback { get; init; }
    public required Dictionary<char, Glyph> Glyphs { get; init; }

    public Glyph? GlyphFor(char c)
    {
        if (c == ' ') return null;
        if (Glyphs.TryGetValue(c, out var g)) return g;
        if (Glyphs.TryGetValue(Fallback, out var f)) return f;
        return null;
    }

    public int Advance(char c) => c == ' ' ? SpaceWidth : (GlyphFor(c)?.W ?? SpaceWidth);

    public int WidthOf(string s)
    {
        if (s.Length == 0) return 0;
        int total = 0;
        foreach (char c in s) total += Advance(c);
        return total + Tracking * (s.Length - 1);
    }

    /// Greedy word wrap, hard-breaking any single word that cannot fit — a URL or a
    /// long word must never make the bubble wider than the panel it lives in.
    public List<string> Wrap(string text, int maxWidth)
    {
        var lines = new List<string>();
        foreach (string paragraph in text.Split('\n'))
        {
            string current = "";
            foreach (string rawWord in paragraph.Split(' ',
                         StringSplitOptions.RemoveEmptyEntries))
            {
                string word = rawWord;
                while (WidthOf(word) > maxWidth && word.Length > 1)
                {
                    string head = "";
                    foreach (char ch in word)
                    {
                        if (WidthOf(head + ch) > maxWidth) break;
                        head += ch;
                    }
                    if (head.Length == 0) head = word[..1];
                    if (current.Length != 0) { lines.Add(current); current = ""; }
                    lines.Add(head);
                    word = word[head.Length..];
                }
                string trial = current.Length == 0 ? word : current + " " + word;
                if (WidthOf(trial) <= maxWidth || current.Length == 0)
                {
                    current = trial;
                }
                else
                {
                    lines.Add(current);
                    current = word;
                }
            }
            lines.Add(current);
        }
        // A trailing empty line would add a blank row of padding for nothing.
        while (lines.Count > 1 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines.Count == 0 ? [""] : lines;
    }

    public void Draw(string line, PixelBitmap bmp, int x, int y, Rgba color)
    {
        int cx = x;
        foreach (char ch in line)
        {
            var g = GlyphFor(ch);
            if (g is { } glyph)
            {
                bmp.Stamp(Sheet, glyph.X, 0, glyph.W, CellHeight, cx, y, color);
                cx += glyph.W + Tracking;
            }
            else
            {
                cx += SpaceWidth + Tracking;
            }
        }
    }
}

/// The 9-slice speech bubble, assembled to fit its text.
///
/// Every piece is a whole number of logical pixels and every position below is an
/// `int`, so there is no place for a fractional offset to enter. The bitmap this
/// produces goes through the compositor's nearest-neighbour blit like any body part,
/// which is what keeps it crisp at 2x, 3x and 4x.
public sealed class SpeechBubble
{
    public required int Corner { get; init; }
    public required Dictionary<string, PixelBitmap> Slices { get; init; }
    public required PixelBitmap Tail { get; init; }
    public required int TailOverlap { get; init; }
    public required int TailTipX { get; init; }
    public required int PadX { get; init; }
    public required int PadY { get; init; }
    public required int LineGap { get; init; }
    public required int MaxWidth { get; init; }
    public required int MinWidth { get; init; }

    /// How many lines fit between the cat's ears and the top of the padded window.
    /// Derived by the art pipeline from the same padding the runtime uses, so a long
    /// note is truncated rather than drawn outside the panel and clipped.
    public required int MaxLines { get; init; }

    /// Cat-canvas point the tail tip should touch, and how far above it to sit.
    public required Pt Anchor { get; init; }
    public required int Gap { get; init; }
    public required Rgba TextColor { get; init; }
    public required PixelFont Font { get; init; }

    /// `TipOffset` is the top-left of `Image` relative to the tail tip, in logical px.
    public readonly record struct Rendered(PixelBitmap Image, Pt TipOffset);

    /// `withTail: false` gives the plain plate the pomodoro countdown sits in.
    public Rendered? Render(string text, bool withTail = true)
    {
        if (!TryGet("tl", out var tl) || !TryGet("t", out var t) || !TryGet("tr", out var tr) ||
            !TryGet("l", out var l) || !TryGet("c", out var c) || !TryGet("r", out var r) ||
            !TryGet("bl", out var bl) || !TryGet("b", out var b) || !TryGet("br", out var br))
        {
            return null;
        }

        int limit = Math.Max(MaxWidth - 2 * PadX, Font.SpaceWidth);
        var lines = Font.Wrap(text, limit);
        if (withTail && lines.Count > MaxLines && MaxLines > 0)
        {
            lines = lines.GetRange(0, MaxLines);
            string last = lines[MaxLines - 1];
            while (last.Length > 0 && Font.WidthOf(last + "...") > limit)
            {
                last = last[..^1];
            }
            lines[MaxLines - 1] = last + "...";
        }

        int textW = 0;
        foreach (string line in lines) textW = Math.Max(textW, Font.WidthOf(line));
        int textH = lines.Count * Font.CellHeight + (lines.Count - 1) * LineGap;

        int w = Math.Max(textW + 2 * PadX, MinWidth);
        int h = Math.Max(textH + 2 * PadY, 2 * Corner + 1);
        int tailDrop = withTail ? Tail.Height - TailOverlap : 0;

        var img = new PixelBitmap(w, h + tailDrop);

        img.Blit(tl, 0, 0);
        img.Blit(tr, w - Corner, 0);
        img.Blit(bl, 0, h - Corner);
        img.Blit(br, w - Corner, h - Corner);
        if (w > 2 * Corner)
        {
            for (int x = Corner; x < w - Corner; x++)
            {
                img.Blit(t, x, 0);
                img.Blit(b, x, h - Corner);
            }
        }
        if (h > 2 * Corner)
        {
            for (int y = Corner; y < h - Corner; y++)
            {
                img.Blit(l, 0, y);
                img.Blit(r, w - Corner, y);
                if (w > 2 * Corner)
                {
                    for (int x = Corner; x < w - Corner; x++) img.Blit(c, x, y);
                }
            }
        }

        int tipX = w / 2;
        if (withTail)
        {
            int tx = (w - Tail.Width) / 2;
            img.Blit(Tail, tx, h - TailOverlap);
            tipX = tx + TailTipX;
        }

        for (int i = 0; i < lines.Count; i++)
        {
            // Centred, and rounded DOWN so the whole block stays on the grid — a
            // half-pixel here is exactly the kind of thing that makes text shimmer.
            int lx = PadX + (textW - Font.WidthOf(lines[i])) / 2;
            int ly = PadY + i * (Font.CellHeight + LineGap);
            Font.Draw(lines[i], img, lx, ly, TextColor);
        }

        int tipY = img.Height - 1;
        return new Rendered(img, new Pt(-tipX, -tipY));

        bool TryGet(string key, out PixelBitmap value) => Slices.TryGetValue(key, out value!);
    }

    /// Where the bitmap's top-left goes, in cat-canvas coordinates (y-down, and
    /// negative above the cat, which is where the padding lives).
    public Pt Origin(Rendered r) => new(
        MathX.Round(Anchor.X + r.TipOffset.X),
        MathX.Round(Anchor.Y - Gap + r.TipOffset.Y));
}
