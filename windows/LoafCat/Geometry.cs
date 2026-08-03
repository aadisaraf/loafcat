namespace LoafCat;

/// A point in LOGICAL pixels, y-down, matching the atlas convention everywhere.
///
/// Exists because the Swift port gets `CGPoint` and `CGFloat` from CoreGraphics and
/// there is no equivalent here that is both double-precision and mutable in place.
/// `System.Drawing.PointF` is single-precision, and the springs integrate at 120Hz —
/// float there accumulates visible drift over a long session.
///
/// A mutable struct with public fields on purpose: modules write
/// `stage.PawOffsetL.Y -= lift`, which only compiles when both the field it lives in
/// and the members here are fields rather than properties.
public struct Pt(double x, double y)
{
    public double X = x;
    public double Y = y;

    public static readonly Pt Zero = new(0, 0);

    public static Pt operator +(Pt a, Pt b) => new(a.X + b.X, a.Y + b.Y);
    public static Pt operator -(Pt a, Pt b) => new(a.X - b.X, a.Y - b.Y);
    public static Pt operator *(Pt a, double k) => new(a.X * k, a.Y * k);

    public readonly bool IsZero => X == 0 && Y == 0;
    public readonly double Length => Math.Sqrt(X * X + Y * Y);

    public override readonly string ToString() => $"({X:0.##}, {Y:0.##})";
}

/// A size in logical pixels. Same reasoning as <see cref="Pt"/>.
public struct Sz(double w, double h)
{
    public double W = w;
    public double H = h;

    public static readonly Sz One = new(1, 1);

    public override readonly string ToString() => $"{W:0.##}x{H:0.##}";
}

/// A rectangle in whatever space the caller is working in — screen pixels for the
/// window, logical pixels for the atlas. Y-down in both, which is the one convention
/// this port gets to keep consistent throughout (AppKit forced the macOS side to
/// flip between y-up screen space and y-down atlas space in three separate places).
public struct Rect(double x, double y, double w, double h)
{
    public double X = x;
    public double Y = y;
    public double W = w;
    public double H = h;

    public static readonly Rect Zero = new(0, 0, 0, 0);

    public readonly double MinX => X;
    public readonly double MinY => Y;
    public readonly double MaxX => X + W;
    public readonly double MaxY => Y + H;
    public readonly double MidX => X + W / 2;
    public readonly double MidY => Y + H / 2;

    public readonly bool Contains(double px, double py) =>
        px >= X && px < X + W && py >= Y && py < Y + H;

    public readonly Rect Inset(double dx, double dy) =>
        new(X + dx, Y + dy, W - dx * 2, H - dy * 2);

    /// Zero when they do not overlap, so it can be compared directly.
    public readonly double IntersectionArea(Rect o)
    {
        double w = Math.Min(MaxX, o.MaxX) - Math.Max(MinX, o.MinX);
        double h = Math.Min(MaxY, o.MaxY) - Math.Max(MinY, o.MinY);
        return w > 0 && h > 0 ? w * h : 0;
    }

    public override readonly string ToString() => $"{W:0}x{H:0} @ ({X:0}, {Y:0})";
}

/// Monotonic seconds since the app started.
///
/// Replaces both `CFAbsoluteTimeGetCurrent` and `CACurrentMediaTime`. `DateTime.Now`
/// would have been the obvious translation and is wrong: it steps when the clock is
/// corrected or daylight saving changes, and a backwards step inside a 120Hz loop
/// makes every timer in the app fire at once. `Stopwatch` cannot go backwards.
///
/// Wall-clock time is needed in exactly one place — the daily reminder — and that
/// asks for it explicitly.
public static class Clock
{
    private static readonly long Origin = System.Diagnostics.Stopwatch.GetTimestamp();
    private static readonly double Scale = 1.0 / System.Diagnostics.Stopwatch.Frequency;

    public static double Now => (System.Diagnostics.Stopwatch.GetTimestamp() - Origin) * Scale;

    /// Stands in for Swift's `.greatestFiniteMagnitude` deadline idiom, which means
    /// "never" for a timer that is switched off.
    public const double Never = double.MaxValue;
}

public static class MathX
{
    public static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
    public static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

    /// Swift's `.rounded()` is round-half-away-from-zero. C#'s `Math.Round` defaults
    /// to banker's rounding, which would put a part on a different pixel from the
    /// macOS build one time in two at exactly .5 — so it is spelled out here rather
    /// than left to the default.
    public static double Round(double v) => Math.Round(v, MidpointRounding.AwayFromZero);

    public static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);
}
