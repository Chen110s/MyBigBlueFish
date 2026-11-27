using System;

namespace BigBlueFish.Windows;

// 键鼠陪伴: drawing (KuroCat's renderBody / renderForeground / drawKeyboard / drawMiniMouse / key bubbles, MIT).
// Layers: tail root and her sprite with the moving eyes and the blush; the mouse and the keyboard; ripples, the tail
// when it slaps, her arms and hands, drops and hearts; keycap bubbles and click badges.
internal sealed partial class CompanionKeyMouseScene
{
    private object? spriteIdle, spriteBlink, spriteShy, spriteHappy, spriteBangs;
    private object? keyboardCache;

    public bool HasSprites => spriteIdle != null;

    // Images made by the host's canvas backend; bangs: the hair over her eyes (see ExtractBangs).
    public void SetSprites(object idle, object blink, object shy, object happy, object? bangs)
    {
        spriteIdle = idle; spriteBlink = blink; spriteShy = shy; spriteHappy = happy; spriteBangs = bangs;
    }

    // KuroCat's hair-bangs mask: from the 224 x 278 idle sprite (BGRA, premultiplied or not) the 102 x 42 strip at
    // (60, 105) keeping only hair pixels in the centre bangs and the side hair, so the bangs stay in front of the eyes.
    public static byte[] ExtractBangs(byte[] bgra, int stride)
    {
        const int cropX = 60, cropY = 105, w = 102, h = 42;
        var output = new byte[w * h * 4];
        for (int ly = 0; ly < h; ly++)
        {
            int y = cropY + ly;
            for (int lx = 0; lx < w; lx++)
            {
                int x = cropX + lx, i = y * stride + x * 4, o = (ly * w + lx) * 4;
                byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2], a = bgra[i + 3];
                bool centre = x >= 96 && x <= 122 && y <= 137 || x >= 100 && x <= 118 && y <= 141;
                bool outer = (x <= 69 || x >= 149) && y <= 145;
                bool hair = a > 90 && (r < 175 && b >= r - 6 || r < 125 && g < 120 && b < 165);
                if ((centre || outer) && hair) { output[o] = b; output[o + 1] = g; output[o + 2] = r; output[o + 3] = a; }
            }
        }
        return output;
    }

    public void Render(IKmCanvas c, double now)
    {
        RenderBody(c, now);
        DrawMiniMouse(c);
        DrawKeyboard(c, now);
        RenderForeground(c, now);
        if (ShowBubbles && !Privacy)
        {
            DrawKeyBubbles(c, now);
            DrawMouseBadges(c, now);
        }
    }

    // ---------------------------------------------------------------- her body
    private void RenderBody(IKmCanvas c, double now)
    {
        var g = ComputeGeom(now);
        var root = g.SpritePoint(.56, .785);
        DrawWhaleTail(c, root.X, root.Y, root.X + tail.RelX, root.Y + tail.RelY, tail.Phase, tail.ActiveWag, false);

        object? image = spriteIdle;
        bool idle = false;
        if (Privacy) image = spriteBlink;
        else if (Happy || moodTier == 2) image = spriteHappy;
        else if (moodTier == 1) image = spriteShy;
        else if (now < blinkUntil) image = spriteBlink;
        else idle = true;
        if (image == null) return;

        c.Save();
        c.Clip(new KmPath().Rect(0, 0, LogicalW, kbCenterY - kbHeight * .36)); // her lower half is behind the keyboard
        c.Translate(g.CharCenterX, g.PivotY);
        c.Rotate(bodyTilt);
        c.Scale(1 + bodyBob * .022, 1 - bodyBob * .018);
        c.DrawImage(image, -g.DrawW * .5, -g.DrawH + g.PivotOff, g.DrawW, g.DrawH);
        if (idle)
        {
            c.Save();
            c.Translate(-g.DrawW * .5, -g.DrawH + g.PivotOff);
            c.Scale(g.DrawW / 224, g.DrawH / 278);
            RenderLiveEyes(c);
            c.Restore();
        }
        if (blushLevel > .08)
        {
            foreach (double nx in new[] { .372, .616 })
            {
                double x = (nx - .5) * g.DrawW, y = .562 * g.DrawH - g.DrawH + g.PivotOff;
                double rx = 11.5 + blushLevel * 4.5, ry = 6.2 + blushLevel * 2.2;
                var paint = KmPaint.Radial(x, y, 1, x, y, rx)
                    .Stop(0, Km.Rgba(255, 110, 148, blushLevel * .46))
                    .Stop(.55, Km.Rgba(255, 138, 170, blushLevel * .22))
                    .Stop(1, Km.Rgba(255, 140, 170, 0));
                c.Fill(new KmPath().Ellipse(x, y, rx, ry), paint);
                if (blushLevel > .52)
                {
                    double a = Math.Min(1, (blushLevel - .52) / .40) * .36;
                    var lines = new KmPath();
                    foreach (double ox in new[] { -3.4, 0, 3.4 }) lines.MoveTo(x + ox - .9, y + 1.8).LineTo(x + ox + 1.0, y - 1.5);
                    c.Stroke(lines, new KmPen(Km.Rgba(235, 88, 132, a), 1.05, true));
                }
            }
        }
        c.Restore();
    }

    private static readonly (double Ix, double Iy, double Irx, double Iry, int Side, double[] Pts)[] Eyes =
    [
        (84.5, 137.4, 9.7, 10.2, -1, [69.8, 132.8, 76.0, 128.0, 85.0, 127.4, 93.5, 129.4, 97.2, 134.5, 95.5, 143.5, 89.0, 147.2, 80.0, 147.2, 72.5, 143.8, 69.8, 138.0]),
        (133.5, 137.2, 9.8, 10.2, 1, [120.8, 134.5, 124.5, 129.2, 133.5, 127.2, 142.5, 128.0, 148.2, 132.8, 148.2, 138.0, 145.5, 143.8, 138.0, 147.2, 129.0, 147.2, 122.5, 143.5]),
    ];

    // A closed Catmull-Rom style spline through the points (KuroCat's traceClosedSpline).
    private static KmPath ClosedSpline(double[] p)
    {
        int n = p.Length / 2;
        var path = new KmPath().MoveTo(p[0], p[1]);
        for (int i = 0; i < n; i++)
        {
            int a = (i - 1 + n) % n, b = i, d = (i + 1) % n, e = (i + 2) % n;
            path.BezierTo(p[b * 2] + (p[d * 2] - p[a * 2]) / 6, p[b * 2 + 1] + (p[d * 2 + 1] - p[a * 2 + 1]) / 6,
                p[d * 2] - (p[e * 2] - p[b * 2]) / 6, p[d * 2 + 1] - (p[e * 2 + 1] - p[b * 2 + 1]) / 6, p[d * 2], p[d * 2 + 1]);
        }
        return path.Close();
    }

    // Moving sapphire eyes drawn over the sprite (in its own 224 x 278 pixels), faded out when she looks straight ahead.
    private void RenderLiveEyes(IKmCanvas c)
    {
        double gx = eyeGazeX, gy = eyeGazeY, alpha = Math.Min(1, Km.Hypot(gx, gy) / .55);
        if (alpha <= .02) return;
        c.Save();
        c.Alpha(alpha);
        foreach (var e in Eyes)
        {
            c.Save();
            c.Clip(ClosedSpline(e.Pts));
            c.Fill(new KmPath().Rect(e.Ix - 18, 125, 36, 25), KmPaint.Linear(0, 127, 0, 146)
                .Stop(0, "#c0b4c8").Stop(.28, "#e8dce2").Stop(.58, "#f6ecf0").Stop(1, "#faf3f5"));
            double icx = e.Ix + gx, icy = e.Iy + gy, irx = e.Irx, iry = e.Iry;
            c.Fill(new KmPath().Ellipse(icx, icy, irx, iry), KmPaint.Solid("#1a2446"));
            c.Fill(new KmPath().Ellipse(icx, icy, irx * .91, iry * .91), KmPaint.Linear(icx, icy - iry * .92, icx, icy + iry * .92)
                .Stop(0, "#162654").Stop(.36, "#28407c").Stop(.66, "#5898ca").Stop(.88, "#b6e4f6").Stop(1, "#90b8cc"));
            c.Fill(new KmPath().Ellipse(icx, icy + iry * .44, irx * .62, iry * .30), KmPaint.Solid(Km.Rgba(164, 226, 248, .62)));
            double pcx = icx + gx * .22, pcy = icy + gy * .22 - .4, prx = irx * .42, pry = iry * .46;
            c.Fill(new KmPath().Ellipse(pcx, pcy, prx, pry), KmPaint.Solid(Km.Rgba(10, 30, 74, .92)));
            c.Fill(new KmPath().Ellipse(pcx, pcy, prx * .52, pry * .52), KmPaint.Solid(Km.Rgba(4, 18, 52, .96)));
            c.Fill(new KmPath().Ellipse(icx, icy - iry * .72, irx * .95, iry * .48), KmPaint.Solid(Km.Rgba(18, 28, 64, .40)));
            c.Fill(new KmPath().Circle(icx + (e.Side == -1 ? .6 : -.3), icy - iry * .50, 2.1), KmPaint.Solid(Km.Rgba(244, 250, 255, .97)));
            c.Fill(new KmPath().Circle(icx + (e.Side == -1 ? -3.4 : 3.4), icy + iry * .30, .85), KmPaint.Solid(Km.Rgba(235, 250, 255, .78)));
            c.Restore();
        }
        if (spriteBangs != null) c.DrawImage(spriteBangs, 60, 105, 102, 42);
        c.Restore();
    }

    // ---------------------------------------------------------------- ripples, tail, arms, drops
    private void RenderForeground(IKmCanvas c, double now)
    {
        var g = ComputeGeom(now);
        var shL = g.SpritePoint(.39, .653);
        var shR = g.SpritePoint(.61, .653);
        var root = g.SpritePoint(.56, .785);
        bool mouseMode = MouseMode(now);

        foreach (var r in ripples)
        {
            c.Save();
            c.Alpha(Math.Max(0, r.Life) * .82);
            c.Stroke(new KmPath().Ellipse(r.X, r.Y, r.R, r.R * .55), new KmPen("#67d8ff", 2.4));
            c.Restore();
        }
        if (tail.Phase > .12)
            DrawWhaleTail(c, root.X, root.Y, root.X + tail.RelX, root.Y + tail.RelY, tail.Phase, tail.ActiveWag, true);

        DrawChubbyArm(c, shL.X, shL.Y, leftHand.X, leftHand.Y, false, leftHand.Squash, mouseMode ? (Is108 ? 96 : 88) : 72);
        DrawChubbyArm(c, shR.X, shR.Y, rightHand.X, rightHand.Y, true, rightHand.Squash, mouseMode ? 86 : 72);

        foreach (var p in particles)
        {
            c.Save();
            c.Alpha(Math.Max(0, p.Life));
            // KuroCat draws 💙 / 💧 at 14 px; here a small heart and a drop around the same spot
            double x = p.X + 7, y = p.Y - 5;
            if (p.Heart)
                c.Fill(new KmPath().MoveTo(x, y + 5).BezierTo(x - 7, y, x - 5.5, y - 6, x, y - 3).BezierTo(x + 5.5, y - 6, x + 7, y, x, y + 5).Close(),
                    KmPaint.Solid("#3b82f6"));
            else
            {
                c.Fill(new KmPath().MoveTo(x, y - 6).BezierTo(x + 1.5, y - 3, x + 4.5, y, x + 4.5, y + 2).BezierTo(x + 4.5, y + 5, x + 2.2, y + 6.5, x, y + 6.5)
                    .BezierTo(x - 2.2, y + 6.5, x - 4.5, y + 5, x - 4.5, y + 2).BezierTo(x - 4.5, y, x - 1.5, y - 3, x, y - 6).Close(), KmPaint.Solid("#4fb3f6"));
                c.Fill(new KmPath().Ellipse(x - 1.6, y + 2.2, 1.1, 1.8), KmPaint.Solid(Km.Rgba(255, 255, 255, .7)));
            }
            c.Restore();
        }
    }

    private void DrawWhaleTail(IKmCanvas c, double rootX, double rootY, double tipX, double tipY, double slapPhase, double wagAngle, bool front)
    {
        double idleCtrlX = rootX + 52, idleCtrlY = rootY - 6;
        // wrap round the right hip and sweep over the keyboard for keys far to the left, never across her face
        double crossLeft = Km.Clamp((rootX + 24 - tipX) / 145, 0, 1);
        double nearX = Math.Min(LogicalW - 18, Math.Max(rootX + 94, tipX + 42)), nearY = Math.Min(rootY - 42, (rootY + tipY) * .5 - 64);
        double farX = Math.Min(LogicalW - 16, rootX + 104), farY = Math.Max(rootY + 28, tipY - 14);
        double slapX = nearX + (farX - nearX) * crossLeft, slapY = nearY + (farY - nearY) * crossLeft;
        double ctrlX = idleCtrlX + (slapX - idleCtrlX) * slapPhase, ctrlY = idleCtrlY + (slapY - idleCtrlY) * slapPhase;

        const int steps = 14;
        var pts = new (double X, double Y, double Nx, double Ny, double R)[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps * .90, omt = 1 - t;
            double x = omt * omt * rootX + 2 * omt * t * ctrlX + t * t * tipX, y = omt * omt * rootY + 2 * omt * t * ctrlY + t * t * tipY;
            double tx = 2 * omt * (ctrlX - rootX) + 2 * t * (tipX - ctrlX), ty = 2 * omt * (ctrlY - rootY) + 2 * t * (tipY - ctrlY);
            double len = Math.Max(.001, Km.Hypot(tx, ty));
            double r = t < .14 ? 7.2 + t / .14 * 2.8 : 10.0 * (1 - (t - .14) / .86 * .56);
            pts[i] = (x, y, -ty / len, tx / len, r);
        }
        int start = front ? (int)Math.Floor(steps * .42) : 0;
        bool fluke = front || slapPhase <= .12;
        double tipAngle = Math.Atan2(tipY - ctrlY, tipX - ctrlX) + wagAngle;

        if (front && slapPhase > .25) c.Fill(new KmPath().Ellipse(tipX, tipY + 6, 22, 13), KmPaint.Solid(Km.Rgba(20, 40, 88, .22)));

        var stem = new KmPath().MoveTo(pts[start].X + pts[start].Nx * pts[start].R, pts[start].Y + pts[start].Ny * pts[start].R);
        for (int i = start + 1; i <= steps; i++) stem.LineTo(pts[i].X + pts[i].Nx * pts[i].R, pts[i].Y + pts[i].Ny * pts[i].R);
        for (int i = steps; i >= start; i--) stem.LineTo(pts[i].X - pts[i].Nx * pts[i].R, pts[i].Y - pts[i].Ny * pts[i].R);
        stem.Close();
        c.Fill(stem, KmPaint.Linear(rootX, rootY, tipX, tipY).Stop(0, Km.Rgba(24, 34, 68, .92)).Stop(.18, "#1d3269").Stop(.55, "#2957a4").Stop(1, "#52b2eb"));

        int edgeStart = Math.Max(1, start);
        var edges = new KmPath();
        for (int side = 1; side >= -1; side -= 2)
        {
            edges.MoveTo(pts[edgeStart].X + side * pts[edgeStart].Nx * pts[edgeStart].R, pts[edgeStart].Y + side * pts[edgeStart].Ny * pts[edgeStart].R);
            for (int i = edgeStart + 1; i <= steps; i++) edges.LineTo(pts[i].X + side * pts[i].Nx * pts[i].R, pts[i].Y + side * pts[i].Ny * pts[i].R);
        }
        c.Stroke(edges, new KmPen("#16203d", 2.4, true));
        int hlStart = Math.Max(2, start);
        var highlight = new KmPath().MoveTo(pts[hlStart].X + pts[hlStart].Nx * pts[hlStart].R * .35, pts[hlStart].Y + pts[hlStart].Ny * pts[hlStart].R * .35);
        for (int i = hlStart + 1; i <= steps; i++) highlight.LineTo(pts[i].X + pts[i].Nx * pts[i].R * .35, pts[i].Y + pts[i].Ny * pts[i].R * .35);
        c.Stroke(highlight, new KmPen(Km.Rgba(170, 232, 255, .48), 3.0, true));
        if (!fluke) return;

        c.Save();
        c.Translate(tipX, tipY);
        c.Rotate(tipAngle);
        double sq = tail.Squash, scale = 1.05 + slapPhase * .10;
        c.Scale(scale * (1 + sq * .16), scale * (1 - sq * .10));
        var shape = new KmPath().MoveTo(-13, -4).BezierTo(-8, -19, 8, -30, 19, -23).BezierTo(22, -14, 14, -4, 3, 0)
            .BezierTo(14, 4, 22, 14, 19, 23).BezierTo(8, 30, -8, 19, -13, 4).Close();
        c.Fill(shape, KmPaint.Radial(-6, 0, 2, 4, 0, 28).Stop(0, "#96ecff").Stop(.38, "#4ba8e6").Stop(.78, "#26539e").Stop(1, "#182f68"));
        c.Stroke(shape, new KmPen("#151f3b", 2.4, true));
        var veins = new KmPath().MoveTo(-8, -2).QuadTo(2, -11, 13, -17).MoveTo(-8, 2).QuadTo(2, 11, 13, 17)
            .MoveTo(-6, -1).QuadTo(1, -6, 8, -9).MoveTo(-6, 1).QuadTo(1, 6, 8, 9);
        c.Stroke(veins, new KmPen(Km.Rgba(215, 248, 255, .65), 1.6, true));
        c.Fill(new KmPath().Ellipse(-4, -7, 4.2, 2.2, -.45), KmPaint.Solid(Km.Rgba(230, 252, 255, .55)));
        c.Restore();
    }

    private static void DrawChubbyArm(IKmCanvas c, double sx, double sy, double targetX, double targetY, bool right, double squash, double maxLen)
    {
        double dx = targetX - sx, dy = targetY - sy, rawLen = Math.Max(1, Km.Hypot(dx, dy)), len = Math.Min(rawLen, maxLen);
        double ux = dx / rawLen, uy = dy / rawLen, px = sx + ux * len, py = sy + uy * len, nx = -uy, ny = ux;
        double ang = Math.Atan2(dy, dx), sgn = right ? 1 : -1;
        double wristDist = Math.Max(14, len - 9), wx = sx + ux * wristDist, wy = sy + uy * wristDist;
        const double rSh = 9.2, rCuff = 12.0;
        double midX = (sx + wx) * .5 + nx * sgn * 2.5, midY = (sy + wy) * .5 + ny * sgn * 2.5;

        var sleevePaint = KmPaint.Linear(sx, sy, wx, wy).Stop(0, "#1f223b").Stop(.65, "#272b4b").Stop(1, "#31365e");
        var sleeve = new KmPath().MoveTo(sx + nx * rSh, sy + ny * rSh)
            .QuadTo(midX + nx * 11, midY + ny * 11, wx + nx * rCuff, wy + ny * rCuff)
            .LineTo(wx - nx * rCuff, wy - ny * rCuff)
            .QuadTo(midX - nx * 11, midY - ny * 11, sx - nx * rSh, sy - ny * rSh).Close();
        c.Fill(sleeve, sleevePaint);
        c.Fill(new KmPath().Circle(sx, sy, rSh), sleevePaint);
        var outline = new KmPath().MoveTo(sx + nx * rSh, sy + ny * rSh).QuadTo(midX + nx * 11, midY + ny * 11, wx + nx * rCuff, wy + ny * rCuff)
            .MoveTo(sx - nx * rSh, sy - ny * rSh).QuadTo(midX - nx * 11, midY - ny * 11, wx - nx * rCuff, wy - ny * rCuff);
        c.Stroke(outline, new KmPen("#181a2e", 2.4, true));

        double trim = Math.Max(8, wristDist - 5.5);
        c.Save();
        c.Translate(sx + ux * trim, sy + uy * trim);
        c.Rotate(ang + Math.PI / 2);
        c.Stroke(new KmPath().MoveTo(-rCuff + 2, 0).LineTo(rCuff - 2, 0), new KmPen(Km.Rgba(205, 212, 232, .85), 1.6));
        c.Fill(new KmPath().Circle(0, 0, 1.5), KmPaint.Solid("#e7c67b"));
        c.Restore();

        // the white lace cuff
        c.Save();
        c.Translate(wx, wy);
        c.Rotate(ang + Math.PI / 2);
        var lacePaint = KmPaint.Solid("#f8f9fe");
        var lacePen = new KmPen("#252844", 1.8);
        for (int i = -2; i <= 2; i++)
        {
            var scallop = new KmPath().Circle(i * 4.4, 0, 4.7);
            c.Fill(scallop, lacePaint);
            c.Stroke(scallop, lacePen);
        }
        c.Fill(new KmPath().Ellipse(0, -.5, 10.2, 3.6), lacePaint);
        c.Restore();

        // the round chibi hand
        c.Save();
        c.Translate(px, py);
        c.Rotate(ang);
        var hand = new KmPath().Ellipse(0, 0, 8.4 * (1 + squash * .14), 8.2 * (1 - squash * .10));
        c.Fill(hand, KmPaint.Solid("#fde7df"));
        c.Stroke(hand, new KmPen("#3d2c35", 2.0));
        c.Fill(new KmPath().Circle(-2.2, -2.2, 2.3), KmPaint.Solid(Km.Rgba(255, 255, 255, .72)));
        c.Restore();
    }

    // ---------------------------------------------------------------- the mouse
    private void DrawMiniMouse(IKmCanvas c)
    {
        double mx = mouseBaseX + mouseOffsetX, my = mouseBaseY + mouseOffsetY;
        const double rx = 18.5, ry = 23.0;
        double l = mouseLeftPress, r = mouseRightPress, m = mouseMiddlePress;
        c.Save();
        c.Translate(mx, my);
        c.Rotate(-.06);
        c.Fill(new KmPath().Ellipse(0, 1.8 + 3.5, rx, ry), KmPaint.Solid(Km.Rgba(0, 0, 0, .28))); // its shadow
        c.Fill(new KmPath().Ellipse(0, 1.8, rx, ry), KmPaint.Solid("#c8cac4"));
        c.Fill(new KmPath().Ellipse(0, 0, rx, ry), KmPaint.Linear(0, -ry, 0, 2).Stop(0, "#ecece7").Stop(1, "#f4f4f0"));

        // the palm is on her side (top), the buttons point away from her: left button on the inner (+x) half
        c.Save();
        c.Clip(new KmPath().Ellipse(0, 0, rx - .5, ry - .5));
        c.Fill(new KmPath().MoveTo(0, .5).QuadTo(rx * .52, .4, rx + 2, -.8).LineTo(rx + 2, ry + 2).LineTo(0, ry + 2).Close(),
            l > .08 ? KmPaint.Solid(MouseLeftColor) : KmPaint.Solid("#fafaf7"));
        if (l > .08)
        {
            var shade = KmPaint.Solid(Km.Rgba(18, 22, 28, .18 * l));
            c.Fill(new KmPath().Rect(0, -1, rx + 2, 3.8), shade);
            c.Fill(new KmPath().Rect(0, -1, 2.2, ry + 2), shade);
        }
        c.Fill(new KmPath().MoveTo(-rx - 2, -.8).QuadTo(-rx * .52, .4, 0, .5).LineTo(0, ry + 2).LineTo(-rx - 2, ry + 2).Close(),
            r > .08 ? KmPaint.Solid(MouseRightColor) : KmPaint.Solid("#fafaf7"));
        if (r > .08)
        {
            var shade = KmPaint.Solid(Km.Rgba(18, 22, 28, .18 * r));
            c.Fill(new KmPath().Rect(-rx - 2, -1, rx + 2, 3.8), shade);
            c.Fill(new KmPath().Rect(-2.2, -1, 2.2, ry + 2), shade);
        }
        c.Stroke(new KmPath().MoveTo(-rx - 2, -.8).QuadTo(0, 1.8, rx + 2, -.8), new KmPen(Km.Rgba(42, 45, 52, .56), 1.25));
        c.Stroke(new KmPath().MoveTo(0, .6).LineTo(0, ry + 1), new KmPen(Km.Rgba(42, 45, 52, .66), 1.3));
        c.Fill(new KmPath().RoundRect(-2.5, 4.2, 5.0, 9.4, 2.5), KmPaint.Solid("#2d2f36"));
        c.Fill(new KmPath().RoundRect(-1.7, 5.0 + m * .8, 3.4, 7.6, 1.7), m > .12 ? KmPaint.Solid(MouseMiddleColor) : KmPaint.Solid("#6b6f78"));
        c.Stroke(new KmPath().MoveTo(-1.0, 8.8 + m * .8).LineTo(1.0, 8.8 + m * .8), new KmPen(Km.Rgba(255, 255, 255, .45), .8));
        c.Text("L", 7.4, 14.0 + l * .9, 7.6, true, l > .12 ? KmColor.Parse("#14161a") : Km.Rgba(72, 76, 86, .56));
        c.Text("R", -7.4, 14.0 + r * .9, 7.6, true, r > .12 ? KmColor.Parse("#ffffff") : Km.Rgba(72, 76, 86, .56));
        c.Restore();

        c.Stroke(new KmPath().Ellipse(0, 0, rx, ry), new KmPen("#2b2d34", 1.5));
        c.Restore();
    }

    // ---------------------------------------------------------------- the keyboard
    private static KmPath Trapezoid(double topHalfW, double botHalfW, double topY, double botY, double r)
        => new KmPath().MoveTo(-topHalfW + r, topY).LineTo(topHalfW - r, topY).QuadTo(topHalfW, topY, topHalfW + 1, topY + r)
            .LineTo(botHalfW, botY - r).QuadTo(botHalfW, botY, botHalfW - r, botY).LineTo(-botHalfW + r, botY)
            .QuadTo(-botHalfW, botY, -botHalfW, botY - r).LineTo(-topHalfW - 1, topY + r).QuadTo(-topHalfW, topY, -topHalfW + r, topY).Close();

    private void DrawSingleKey(IKmCanvas c, CompanionKey key, double press)
    {
        double innerW = kbWidth - 14, innerH = kbHeight - 12;
        double rowH = innerH / Board.TotalRows, unitW = innerW / Board.TotalUnits, gap = Is108 ? .85 : .9;
        double screenRowTop = Board.TotalRows - (key.Row + key.RowSpan), screenU = Board.TotalUnits - (key.UStart + key.UWidth);
        double depth = .94 + (screenRowTop + key.RowSpan * .5) / Board.TotalRows * .09;
        double x = (-innerW / 2 + screenU * unitW + gap * .5) * depth, w = (key.UWidth * unitW - gap) * depth;
        double y = -innerH / 2 + screenRowTop * rowH + gap * .5 + press * 2.0, h = key.RowSpan * rowH - gap - .8;

        string top = "#f8f8f5", side = "#d0d2cc";
        if (press > .14) { top = "#5cc8ff"; side = "#2563eb"; }
        else if (key.Accent == 'o') { top = "#f4a524"; side = "#c27a10"; }
        else if (key.Accent == 'm') { top = "#efefeb"; side = "#c7c9c3"; }
        if (press > .01) // the keybed behind a key drawn again over the recorded board
            c.Fill(new KmPath().Rect(x - .4, -innerH / 2 + screenRowTop * rowH + gap * .5 - .4, w + .8, h + 3.8), KmPaint.Solid("#d4d6d1"));
        c.Fill(new KmPath().RoundRect(x, y + 1.4, w, h, 1.8), KmPaint.Solid(side));
        c.Fill(new KmPath().RoundRect(x, y, w, h - .9, 1.8), KmPaint.Solid(top));

        string legend = key.Id == "space" ? "" : key.Id == "esc" ? "Esc" : key.Label;
        if (legend.Length == 0) return;
        bool wide = legend.Length >= 3;
        double size = Is108 ? (wide ? 5.0 : legend.Length == 2 ? 5.8 : 7.2) : (wide ? 5.6 : legend.Length == 2 ? 6.4 : 7.8);
        c.Save();
        c.Translate(x + w * .5, y + (h - .9) * .5);
        c.Rotate(Math.PI); // the board faces her
        c.Text(legend, 0, 0, size, true, KmColor.Parse(press > .14 ? "#111214" : key.Accent == 'o' ? "#ffffff" : "#2c2f36"));
        c.Restore();
    }

    private void DrawKeyboard(IKmCanvas c, double now)
    {
        double halfH = kbHeight / 2, topHalfW = kbWidth * .955 / 2, botHalfW = kbWidth * 1.035 / 2;
        keyboardCache ??= c.Record(k =>
        {
            // a soft shadow (KuroCat blurs it; two offset layers here), the case, the keybed and every key at rest
            k.Fill(Trapezoid(topHalfW + 2, botHalfW + 2, -halfH + 5.5, halfH + 8.5, 8), KmPaint.Solid(Km.Rgba(0, 0, 0, .12)));
            k.Fill(Trapezoid(topHalfW, botHalfW, -halfH + 3.5, halfH + 5.5, 6.5), KmPaint.Solid("#191a1e"));
            var chassis = Trapezoid(topHalfW, botHalfW, -halfH, halfH, 6.5);
            k.Fill(chassis, KmPaint.Solid("#24262c"));
            k.Stroke(chassis, new KmPen("#363840", 1.1));
            k.Fill(Trapezoid(topHalfW - 5, botHalfW - 5, -halfH + 4, halfH - 4, 3.5), KmPaint.Solid("#d4d6d1"));
            foreach (var key in Board.Keys) DrawSingleKey(k, key, 0);
        });

        c.Save();
        c.Translate(kbCenterX, kbCenterY + kbBounce * 1.1);
        c.Rotate(kbTilt);
        c.Replay(keyboardCache);
        foreach (var key in Board.Keys)
            if (keyPress.TryGetValue(key.Id, out double press) && press > .01) DrawSingleKey(c, key, press);
        if (privacyClose > .08)
        {
            c.Save();
            c.Alpha(privacyClose);
            var mask = Trapezoid(topHalfW - 2, botHalfW - 2, -halfH + 1.5, halfH - 1.5, 5.5);
            c.Fill(mask, KmPaint.Solid(Km.Rgba(15, 20, 32, .74)));
            c.Stroke(mask, new KmPen(Km.Rgba(244, 114, 182, .45), 1.4));
            c.Text("非礼勿视 · 已隐藏按键" + (now - privacyTypingAt < 350 ? " • • •" : ""), 0, 0, 11.5, true, KmColor.Parse("#fbcfe8"));
            c.Restore();
        }
        c.Restore();
    }

    // ---------------------------------------------------------------- keycap bubbles and click badges
    private void DrawKeyBubbles(IKmCanvas c, double now)
    {
        foreach (var b in bubbles)
        {
            double t = Math.Min(1, (now - b.CreatedAt) / b.Lifetime);
            double scale = t < .11 ? .62 + Math.Sin(t / .11 * Math.PI * .65) * .44 : t > .76 ? 1 - (t - .76) / .24 * .1 : 1;
            double alpha = t < .72 ? 1 : Math.Max(0, 1 - (t - .72) / .28);
            c.Save();
            c.Alpha(alpha);
            c.Translate(b.X, b.Y);
            c.Rotate(b.Rot);
            c.Scale(scale, scale);
            double boxW = b.IsSpace ? 40 : Math.Max(24, b.Label.Length * 7.2 + 13), boxH = 24, bx = -boxW / 2, by = -boxH / 2;
            c.Fill(new KmPath().RoundRect(bx, by + 2.2 + 2, boxW, boxH, 5.5), KmPaint.Solid(Km.Rgba(0, 0, 0, .22)));
            c.Fill(new KmPath().RoundRect(bx, by + 2.2, boxW, boxH, 5.5), KmPaint.Solid("#c8cac6"));
            var face = new KmPath().RoundRect(bx, by, boxW, boxH - 1.3, 5.5);
            c.Fill(face, KmPaint.Solid("#ffffff"));
            c.Stroke(face, new KmPen(Km.Rgba(38, 40, 46, .42), 1.05));
            if (b.IsSpace)
                c.Stroke(new KmPath().MoveTo(-9, -1).LineTo(-9, 2).LineTo(9, 2).LineTo(9, -1), new KmPen("#222429", 1.55, true));
            else c.Text(b.Label, 0, -.5, 12, true, KmColor.Parse("#1e2025"));
            c.Restore();
        }
    }

    private void DrawMouseBadges(IKmCanvas c, double now)
    {
        for (int i = 0; i < badges.Count; i++)
        {
            var b = badges[i];
            double t = Math.Min(1, (now - b.CreatedAt) / b.Lifetime);
            double scale = t < .14 ? .65 + Math.Sin(t / .14 * Math.PI * .65) * .42 : t > .72 ? 1 - (t - .72) / .28 * .12 : 1;
            double alpha = t < .68 ? 1 : Math.Max(0, 1 - (t - .68) / .32);
            double lift = t * 14 + (badges.Count - 1 - i) * 18;
            c.Save();
            c.Alpha(alpha);
            c.Translate(b.X, b.Y - lift);
            c.Scale(scale, scale);
            const double boxW = 34, boxH = 19, bx = -boxW / 2, by = -boxH / 2;
            c.Fill(new KmPath().RoundRect(bx, by + 1.6 + 1.5, boxW, boxH, 5), KmPaint.Solid(Km.Rgba(0, 0, 0, .24)));
            c.Fill(new KmPath().RoundRect(bx, by + 1.6, boxW, boxH, 5), KmPaint.Solid("#22242a"));
            var face = new KmPath().RoundRect(bx, by, boxW, boxH - 1.0, 5);
            c.Fill(face, KmPaint.Solid(b.Color));
            c.Stroke(face, new KmPen(Km.Rgba(20, 22, 28, .45), 1.0));
            c.Text(b.Label, 0, -.3, 9.5, true, KmColor.Parse(b.Label == "RMB" ? "#ffffff" : "#14161a"));
            c.Restore();
        }
    }
}
