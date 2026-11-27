using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BigBlueFish.Windows;

// 键鼠陪伴 on screen: the scene drawn with WPF. The view fills the pet window; Place() lays the scene's sprite over
// the pet's own body and keeps the keyboard inside the window.
internal sealed class CompanionKeyMouseView : FrameworkElement
{
    public readonly CompanionKeyMouseScene Scene = new();
    public double SceneScale = 1, OffsetX, OffsetY;
    public double Now;
    public Exception? Failure; // drawing failed: the host switches the mode off

    public CompanionKeyMouseView()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Focusable = false;
    }

    // The four sprites (from KuroCat, local-only: assets\dshpet\kurocat). False when they are missing.
    public bool LoadSprites(string folder)
    {
        BitmapSource? Load(string name)
        {
            string path = Path.Combine(folder, name);
            if (!File.Exists(path)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        try
        {
            var idle = Load("notail_idle.png");
            var blink = Load("notail_blink.png");
            var shy = Load("notail_shy.png");
            var happy = Load("notail_happy2.png");
            if (idle == null || blink == null || shy == null || happy == null || idle.PixelWidth != 224 || idle.PixelHeight != 278) return false;
            BitmapSource? bangs = null;
            try
            {
                var pixels = new FormatConvertedBitmap(idle, PixelFormats.Bgra32, null, 0);
                var bytes = new byte[224 * 4 * 278];
                pixels.CopyPixels(bytes, 224 * 4, 0);
                bangs = BitmapSource.Create(102, 42, 96, 96, PixelFormats.Bgra32, null, CompanionKeyMouseScene.ExtractBangs(bytes, 224 * 4), 102 * 4);
                bangs.Freeze();
            }
            catch { bangs = null; } // the eyes still move, only the bangs are then behind them
            Scene.SetSprites(idle, blink, shy, happy, bangs);
            return true;
        }
        catch { return false; }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!Scene.HasSprites || Failure != null) return;
        dc.PushTransform(new MatrixTransform(SceneScale, 0, 0, SceneScale, OffsetX, OffsetY));
        var canvas = new CompanionKeyMouseCanvas(dc, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        try { Scene.Render(canvas, Now); }
        catch (Exception error) { Failure = error; }
        canvas.PopAll();
        dc.Pop();
    }
}

// IKmCanvas on a WPF DrawingContext: save / restore become counted pushes and pops.
internal sealed class CompanionKeyMouseCanvas : IKmCanvas
{
    private readonly DrawingContext dc;
    private readonly double pixelsPerDip;
    private readonly Stack<int> saves = new();
    private int pushes;
    private static readonly Dictionary<(byte, byte, byte, int), SolidColorBrush> SolidBrushes = new();
    private static readonly Typeface Face = new(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface FaceRegular = new(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public CompanionKeyMouseCanvas(DrawingContext dc, double pixelsPerDip) { this.dc = dc; this.pixelsPerDip = pixelsPerDip; }

    public void PopAll() { while (pushes > 0) { dc.Pop(); pushes--; } saves.Clear(); }

    public void Save() => saves.Push(pushes);

    public void Restore()
    {
        int target = saves.Count > 0 ? saves.Pop() : 0;
        while (pushes > target) { dc.Pop(); pushes--; }
    }

    public void Translate(double x, double y) { dc.PushTransform(new TranslateTransform(x, y)); pushes++; }
    public void Rotate(double radians) { dc.PushTransform(new RotateTransform(radians * 180 / Math.PI)); pushes++; }
    public void Scale(double sx, double sy) { dc.PushTransform(new ScaleTransform(sx, sy)); pushes++; }
    public void Alpha(double alpha) { dc.PushOpacity(Km.Clamp(alpha, 0, 1)); pushes++; }
    public void Clip(KmPath path) { dc.PushClip(Geometry(path)); pushes++; }
    public void Fill(KmPath path, KmPaint paint) => dc.DrawGeometry(Brush(paint), null, Geometry(path));
    public void Stroke(KmPath path, KmPen pen) => dc.DrawGeometry(null, Pen(pen), Geometry(path));
    public void DrawImage(object image, double x, double y, double w, double h) => dc.DrawImage((ImageSource)image, new Rect(x, y, w, h));

    public void Text(string text, double x, double y, double size, bool bold, KmColor color)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? Face : FaceRegular,
            Math.Max(1, size), Solid(color), pixelsPerDip);
        dc.DrawText(formatted, new Point(x - formatted.Width / 2, y - formatted.Height / 2));
    }

    public object Record(Action<IKmCanvas> draw)
    {
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            var canvas = new CompanionKeyMouseCanvas(context, pixelsPerDip);
            draw(canvas);
            canvas.PopAll();
        }
        group.Freeze();
        return group;
    }

    public void Replay(object recording) => dc.DrawDrawing((Drawing)recording);

    private static Geometry Geometry(KmPath path)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var g = geometry.Open())
        {
            foreach (var figure in path.Figures)
            {
                g.BeginFigure(new Point(figure.StartX, figure.StartY), true, figure.Closed);
                foreach (var s in figure.Segments)
                {
                    if (s.Length == 2) g.LineTo(new Point(s[0], s[1]), true, true);
                    else g.BezierTo(new Point(s[0], s[1]), new Point(s[2], s[3]), new Point(s[4], s[5]), true, true);
                }
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static Color ToColor(KmColor c) => Color.FromArgb((byte)Math.Round(Km.Clamp(c.A, 0, 1) * 255), c.R, c.G, c.B);

    private static SolidColorBrush Solid(KmColor c)
    {
        var key = (c.R, c.G, c.B, (int)Math.Round(Km.Clamp(c.A, 0, 1) * 255));
        lock (SolidBrushes)
        {
            if (SolidBrushes.TryGetValue(key, out var brush)) return brush;
            brush = new SolidColorBrush(ToColor(c));
            brush.Freeze();
            if (SolidBrushes.Count < 4096) SolidBrushes[key] = brush; // alpha fades make new colours; keep the cache bounded
            return brush;
        }
    }

    private static Brush Brush(KmPaint p)
    {
        if (p.Kind == 0 || p.Stops.Count == 0) return Solid(p.Kind == 0 ? p.Color : p.Stops[0].Color);
        GradientBrush brush;
        if (p.Kind == 1)
        {
            brush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(p.X0, p.Y0), EndPoint = new Point(p.X1, p.Y1) };
            foreach (var (offset, color) in p.Stops) brush.GradientStops.Add(new GradientStop(ToColor(color), offset));
        }
        else
        {
            // canvas radial gradients run from an inner circle (r0) to the outer one (r1); WPF's from the origin to r1
            brush = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute, GradientOrigin = new Point(p.X0, p.Y0),
                Center = new Point(p.X1, p.Y1), RadiusX = p.R1, RadiusY = p.R1 };
            foreach (var (offset, color) in p.Stops)
                brush.GradientStops.Add(new GradientStop(ToColor(color), p.R1 > 0 ? (p.R0 + offset * (p.R1 - p.R0)) / p.R1 : offset));
        }
        brush.Freeze();
        return brush;
    }

    private static Pen Pen(KmPen pen)
    {
        var result = new Pen(Brush(pen.Paint), pen.Width);
        if (pen.Round) { result.StartLineCap = result.EndLineCap = PenLineCap.Round; result.LineJoin = PenLineJoin.Round; }
        result.Freeze();
        return result;
    }
}
