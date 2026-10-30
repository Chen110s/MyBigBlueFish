using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace BigBlueFish.Windows;

internal sealed record CompanionWindowImage(string DataUrl, int Width, int Height)
{
    internal void SaveJpeg(string path)
    {
        var comma = DataUrl.IndexOf(',');
        if (comma < 0)
            throw new InvalidDataException("窗口图像数据格式无效");
        File.WriteAllBytes(path, Convert.FromBase64String(DataUrl[(comma + 1)..]));
    }
}

internal static class CompanionScreenCapture
{
    private const uint RasterOperationSourceCopy = 0x00CC0020;
    private const uint PrintWindowRenderFullContent = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    private sealed class CaptureDpiScope : IDisposable
    {
        private readonly IntPtr previous;
        internal CaptureDpiScope()
        {
            // Window rectangles and PrintWindow/BitBlt must use the same physical
            // pixel coordinates, including captures made from a worker thread.
            try { previous = SetThreadDpiAwarenessContext(new IntPtr(-4)); }
            catch (EntryPointNotFoundException) { }
        }
        public void Dispose()
        {
            if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    internal static CompanionWindowImage? Capture(IntPtr handle, int maxDimension = 768, bool lossless = false)
    {
        using var dpi = new CaptureDpiScope();
        if (!CompanionNative.IsVisibleCaptureTarget(handle) || !CompanionNative.GetWindowRect(handle, out var rect))
            return null;

        var windowRect = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        var captureRect = windowRect;
        // The shell window spans the virtual desktop, including gaps between monitors.
        // Crop its render to the pointer's monitor before scaling, preserving icon text.
        if (CompanionNative.IsDesktopWindow(handle))
        {
            var monitor = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            captureRect = Rectangle.Intersect(windowRect, monitor.Bounds);
        }
        var width = windowRect.Width;
        var height = windowRect.Height;
        if (captureRect.Width < 32 || captureRect.Height < 32 || width > 12000 || height > 12000)
            return null;

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var target = graphics.GetHdc();
            var captured = PrintWindow(handle, target, PrintWindowRenderFullContent);
            graphics.ReleaseHdc(target);
            if (!captured)
            {
                using var screen = Graphics.FromImage(bitmap);
                var screenHdc = screen.GetHdc();
                var desktop = GetDC(IntPtr.Zero);
                var copied = BitBlt(screenHdc, 0, 0, width, height, desktop, windowRect.Left, windowRect.Top, RasterOperationSourceCopy);
                ReleaseDC(IntPtr.Zero, desktop);
                screen.ReleaseHdc(screenHdc);
                if (!copied)
                    return null;
            }
        }

        // 1280px screenshots make the Qwen vision prefill disproportionately slow on
        // the Intel Vulkan path. 768px preserves the application chrome and readable
        // large text while keeping the first-token wait substantially shorter.
        // PrintWindow can reset the destination DC origin, so crop the rendered pixels
        // afterwards rather than relying on a translated GDI viewport.
        using var cropped = captureRect == windowRect ? null : bitmap.Clone(new Rectangle(
            captureRect.Left - windowRect.Left, captureRect.Top - windowRect.Top,
            captureRect.Width, captureRect.Height), PixelFormat.Format32bppArgb);
        using var normalized = Resize(cropped ?? bitmap, maxDimension);
        using var output = new MemoryStream();
        normalized.Save(output, lossless ? ImageFormat.Png : ImageFormat.Jpeg);
        return new CompanionWindowImage(
            (lossless ? "data:image/png;base64," : "data:image/jpeg;base64,") + Convert.ToBase64String(output.ToArray()),
            normalized.Width,
            normalized.Height);
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);

    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr state);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY,
        int sourceWidth, int sourceHeight, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr hdc, int mode);

    // A game as it is shown on screen (DirectX games often render black through PrintWindow).
    // The pet's own windows sit above the game, so they are blacked out and her words are never read back.
    internal static CompanionWindowImage? CaptureGame(IntPtr handle, int maxDimension)
    {
        using var dpi = new CaptureDpiScope();
        if (!CompanionNative.IsVisibleCaptureTarget(handle) || !CompanionNative.GetWindowRect(handle, out var rect))
            return null;
        var windowRect = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        var area = Rectangle.Intersect(windowRect, System.Windows.Forms.Screen.FromHandle(handle).Bounds);
        if (area.Width < 64 || area.Height < 64)
            return null;
        using var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size, CopyPixelOperation.SourceCopy);
            foreach (var own in OwnWindowRectangles())
            {
                var overlap = Rectangle.Intersect(own, area);
                // A window covering most of the screen is a transparent overlay (menu dismissal), not the pet.
                if (overlap.Width > 0 && overlap.Height > 0 && (long)overlap.Width * overlap.Height < (long)area.Width * area.Height / 3)
                    graphics.FillRectangle(Brushes.Black, overlap.Left - area.Left, overlap.Top - area.Top, overlap.Width, overlap.Height);
            }
        }
        // Exclusive fullscreen and similar give a pure black copy: try the window's own render instead, at most
        // twice a minute (PrintWindow makes the game redraw). A dark scene with any light text is kept as it is.
        if (Black(bitmap) && (DateTime.UtcNow - lastRenderFallbackUtc).TotalSeconds >= 30)
        {
            lastRenderFallbackUtc = DateTime.UtcNow;
            if (Capture(handle, maxDimension, true) is { } rendered)
                return rendered;
        }
        using var normalized = Resize(bitmap, maxDimension);
        using var output = new MemoryStream();
        normalized.Save(output, ImageFormat.Png);
        return new("data:image/png;base64," + Convert.ToBase64String(output.ToArray()), normalized.Width, normalized.Height);
    }

    // Share of strongly red pixels along the screen edges and around the crosshair, where a game shows
    // damage (red vignette, hit direction markers). The pet's own windows are skipped. -1: no picture.
    internal static double RedShare(IntPtr handle)
    {
        using var dpi = new CaptureDpiScope();
        if (!CompanionNative.IsVisibleCaptureTarget(handle) || !CompanionNative.GetWindowRect(handle, out var rect))
            return -1;
        var area = Rectangle.Intersect(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom),
            System.Windows.Forms.Screen.FromHandle(handle).Bounds);
        if (area.Width < 200 || area.Height < 200)
            return -1;
        // One small, point-sampled copy of the whole game picture per call keeps this cheap for the game.
        const int W = 192, H = 108;
        using var bitmap = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var target = graphics.GetHdc();
            var screen = GetDC(IntPtr.Zero);
            try
            {
                SetStretchBltMode(target, 3 /* COLORONCOLOR */);
                if (!StretchBlt(target, 0, 0, W, H, screen, area.Left, area.Top, area.Width, area.Height, RasterOperationSourceCopy))
                    return -1;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
                graphics.ReleaseHdc(target);
            }
        }
        var own = OwnWindowRectangles();
        long red = 0, total = 0;
        var data = bitmap.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[data.Stride];
            for (int y = 0; y < H; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                for (int x = 0; x < W; x++)
                {
                    // Screen edges (vignette) and a ring around the crosshair (hit direction markers).
                    double fx = (x + .5) / W, fy = (y + .5) / H;
                    bool edge = fx < .06 || fx > .94 || fy < .08 || fy > .92;
                    double ring = Math.Max(Math.Abs(fx - .5) / .15, Math.Abs(fy - .5) / .2);
                    if (!edge && ring is < .7 or > 1.0) continue;
                    int screenX = area.Left + (int)(fx * area.Width), screenY = area.Top + (int)(fy * area.Height);
                    bool mine = false;
                    foreach (var window in own) if (window.Contains(screenX, screenY)) { mine = true; break; }
                    if (mine) continue;
                    int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                    total++;
                    if (r >= 110 && r >= g * 2 && r >= b * 2) red++;
                }
            }
        }
        finally { bitmap.UnlockBits(data); }
        return total == 0 ? -1 : (double)red / total;
    }

    private static List<Rectangle> OwnWindowRectangles()
    {
        var result = new List<Rectangle>();
        uint self = (uint)Environment.ProcessId;
        EnumWindows((window, _) =>
        {
            CompanionNative.GetWindowThreadProcessId(window, out var owner);
            if (owner == self && IsWindowVisible(window) && CompanionNative.GetWindowRect(window, out var r))
                result.Add(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static DateTime lastRenderFallbackUtc = DateTime.MinValue;

    private static bool Black(Bitmap bitmap)
    {
        for (int y = 1; y < 32; y++)
            for (int x = 1; x < 32; x++)
            {
                var pixel = bitmap.GetPixel(bitmap.Width * x / 32, bitmap.Height * y / 32);
                if (Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) > 8) return false;
            }
        return true;
    }

    private static Bitmap Resize(Bitmap source, int maxDimension)
    {
        var scale = Math.Min(1d, Math.Min((double)maxDimension / source.Width, (double)maxDimension / source.Height));
        if (scale >= 1d)
            return new Bitmap(source);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(result);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        return result;
    }

    internal static CompanionWindowImage? CaptureScreen(int maxDimension = 4096)
    {
        using var dpi = new CaptureDpiScope();
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0 || (long)bounds.Width * bounds.Height > 150_000_000)
            throw new IOException("屏幕尺寸无效或超过可用截图范围。");
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        using var normalized = Resize(bitmap, maxDimension);
        using var output = new MemoryStream();
        normalized.Save(output, ImageFormat.Png);
        return new("data:image/png;base64," + Convert.ToBase64String(output.ToArray()), normalized.Width, normalized.Height);
    }
}
