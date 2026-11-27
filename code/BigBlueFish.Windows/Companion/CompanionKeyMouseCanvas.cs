using System;
using System.Collections.Generic;
using System.Globalization;

namespace BigBlueFish.Windows;

// 键鼠陪伴 draws through this small canvas-like layer (paths, gradients, save / restore, clip, alpha) so the scene code
// stays close to the HTML canvas code it comes from (KuroCat, MIT) and can be drawn by WPF in the app and by GDI+ in
// a preview harness. Everything is in the scene's logical units; curves are kept as cubic Béziers.
internal readonly struct KmColor
{
    public readonly byte R, G, B;
    public readonly double A;
    public KmColor(byte r, byte g, byte b, double a = 1) { R = r; G = g; B = b; A = a; }

    // "#rrggbb" or "rgba(r, g, b, a)"
    public static KmColor Parse(string text)
    {
        text = text.Trim();
        if (text.StartsWith("#", StringComparison.Ordinal))
        {
            uint v = uint.Parse(text.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new KmColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }
        int open = text.IndexOf('('), close = text.LastIndexOf(')');
        var parts = text.Substring(open + 1, close - open - 1).Split(',');
        byte P(int i) => (byte)Math.Round(double.Parse(parts[i], CultureInfo.InvariantCulture));
        return new KmColor(P(0), P(1), P(2), parts.Length > 3 ? double.Parse(parts[3], CultureInfo.InvariantCulture) : 1);
    }

    public KmColor WithAlpha(double a) => new(R, G, B, a);
}

internal sealed class KmPaint
{
    public KmColor Color;
    public int Kind; // 0 solid, 1 linear, 2 radial
    public double X0, Y0, R0, X1, Y1, R1;
    public readonly List<(double Offset, KmColor Color)> Stops = new();

    public static KmPaint Solid(KmColor c) => new() { Color = c };
    public static KmPaint Solid(string c) => Solid(KmColor.Parse(c));

    public static KmPaint Linear(double x0, double y0, double x1, double y1)
        => new() { Kind = 1, X0 = x0, Y0 = y0, X1 = x1, Y1 = y1 };

    // The canvas form: from the circle (x0, y0, r0) to the circle (x1, y1, r1).
    public static KmPaint Radial(double x0, double y0, double r0, double x1, double y1, double r1)
        => new() { Kind = 2, X0 = x0, Y0 = y0, R0 = r0, X1 = x1, Y1 = y1, R1 = r1 };

    public KmPaint Stop(double offset, string color) { Stops.Add((offset, KmColor.Parse(color))); return this; }
    public KmPaint Stop(double offset, KmColor color) { Stops.Add((offset, color)); return this; }
}

internal sealed class KmPath
{
    internal sealed class Figure
    {
        public double StartX, StartY;
        public bool Closed;
        public readonly List<double[]> Segments = new(); // [x, y] a line, [c1x, c1y, c2x, c2y, x, y] a cubic
    }

    public readonly List<Figure> Figures = new();
    private Figure? current;
    private double lastX, lastY;

    public KmPath MoveTo(double x, double y)
    {
        current = new Figure { StartX = x, StartY = y };
        Figures.Add(current);
        lastX = x; lastY = y;
        return this;
    }

    private Figure Open() => current ?? MoveTo(lastX, lastY).current!;

    public KmPath LineTo(double x, double y)
    {
        Open().Segments.Add([x, y]);
        lastX = x; lastY = y;
        return this;
    }

    public KmPath BezierTo(double c1x, double c1y, double c2x, double c2y, double x, double y)
    {
        Open().Segments.Add([c1x, c1y, c2x, c2y, x, y]);
        lastX = x; lastY = y;
        return this;
    }

    public KmPath QuadTo(double cx, double cy, double x, double y)
        => BezierTo(lastX + 2.0 / 3 * (cx - lastX), lastY + 2.0 / 3 * (cy - lastY), x + 2.0 / 3 * (cx - x), y + 2.0 / 3 * (cy - y), x, y);

    public KmPath Close()
    {
        if (current != null) { current.Closed = true; lastX = current.StartX; lastY = current.StartY; current = null; }
        return this;
    }

    // A whole ellipse (canvas ellipse(x, y, rx, ry, rotation, 0, 2π)) as its own closed figure.
    public KmPath Ellipse(double cx, double cy, double rx, double ry, double rotation = 0)
    {
        const double k = 0.5522847498;
        double c = Math.Cos(rotation), s = Math.Sin(rotation);
        double X(double u, double v) => cx + u * c - v * s;
        double Y(double u, double v) => cy + u * s + v * c;
        MoveTo(X(rx, 0), Y(rx, 0));
        BezierTo(X(rx, k * ry), Y(rx, k * ry), X(k * rx, ry), Y(k * rx, ry), X(0, ry), Y(0, ry));
        BezierTo(X(-k * rx, ry), Y(-k * rx, ry), X(-rx, k * ry), Y(-rx, k * ry), X(-rx, 0), Y(-rx, 0));
        BezierTo(X(-rx, -k * ry), Y(-rx, -k * ry), X(-k * rx, -ry), Y(-k * rx, -ry), X(0, -ry), Y(0, -ry));
        BezierTo(X(k * rx, -ry), Y(k * rx, -ry), X(rx, -k * ry), Y(rx, -k * ry), X(rx, 0), Y(rx, 0));
        return Close();
    }

    public KmPath Circle(double cx, double cy, double r) => Ellipse(cx, cy, r, r);

    public KmPath Rect(double x, double y, double w, double h)
        => MoveTo(x, y).LineTo(x + w, y).LineTo(x + w, y + h).LineTo(x, y + h).Close();

    // KuroCat's roundRect (arcTo corners), with quarter-circle corners.
    public KmPath RoundRect(double x, double y, double w, double h, double r)
    {
        double rad = Math.Min(r, Math.Min(w / 2, h / 2));
        if (rad <= 0) return Rect(x, y, w, h);
        const double k = 0.5522847498;
        double q = rad * (1 - k);
        MoveTo(x + rad, y);
        LineTo(x + w - rad, y);
        BezierTo(x + w - q, y, x + w, y + q, x + w, y + rad);
        LineTo(x + w, y + h - rad);
        BezierTo(x + w, y + h - q, x + w - q, y + h, x + w - rad, y + h);
        LineTo(x + rad, y + h);
        BezierTo(x + q, y + h, x, y + h - q, x, y + h - rad);
        LineTo(x, y + rad);
        BezierTo(x, y + q, x + q, y, x + rad, y);
        return Close();
    }
}

internal sealed class KmPen
{
    public KmPaint Paint;
    public double Width;
    public bool Round; // round caps and joins

    public KmPen(KmPaint paint, double width, bool round = false) { Paint = paint; Width = width; Round = round; }
    public KmPen(string color, double width, bool round = false) : this(KmPaint.Solid(color), width, round) { }
    public KmPen(KmColor color, double width, bool round = false) : this(KmPaint.Solid(color), width, round) { }
}

// The drawing surface: the scene calls these exactly where the canvas code called ctx.save(), ctx.translate() …
internal interface IKmCanvas
{
    void Save();
    void Restore();
    void Translate(double x, double y);
    void Rotate(double radians);
    void Scale(double sx, double sy);
    void Alpha(double alpha);      // multiplies the current opacity until the matching Restore
    void Clip(KmPath path);        // intersects the clip until the matching Restore
    void Fill(KmPath path, KmPaint paint);
    void Stroke(KmPath path, KmPen pen);
    void DrawImage(object image, double x, double y, double w, double h);
    void Text(string text, double x, double y, double size, bool bold, KmColor color); // centred on (x, y)
    object Record(Action<IKmCanvas> draw); // a drawing kept for replaying (the keyboard without pressed keys)
    void Replay(object recording);
}

internal static class Km
{
    public static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;
    public static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);
    public static KmColor Rgba(int r, int g, int b, double a) => new((byte)r, (byte)g, (byte)b, Clamp(a, 0, 1));
}
