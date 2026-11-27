using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Forms = System.Windows.Forms;

namespace BigBlueFish.Windows;

// Interaction feel, after luweiyabo/dsh-whale-pet: she turns to the side the cursor is on and leans
// towards it, answers clicks on her head, body or tail differently with a squash-and-stretch pop,
// stretches while dragged, turns into a ball when flung and bounces off the screen edges, and hides
// at an edge when dragged off the screen (peeking out when the cursor comes near).
public partial class MainWindow
{
    private Grid? spriteHost;
    private readonly ScaleTransform fxFlip = new(), fxPop = new();
    private readonly MatrixTransform fxStretch = new();
    private readonly RotateTransform fxTilt = new();
    private readonly TranslateTransform fxShift = new();
    private const double FeetY = 330.0 / 360, BodyCentreY = 197.0 / 360;
    private DateTime animationStartedUtc = DateTime.MinValue;
    private bool hoverRight, hoverInside, fxRendering;
    private DateTime hoverLeftAt = DateTime.MinValue;
    private double tilt, tiltVelocity, tiltTarget;
    private readonly Stopwatch fxClock = Stopwatch.StartNew();
    private double fxLastMs;

    // Windows' "show animations" setting off: no inertia, lean or pops (like prefers-reduced-motion).
    private static bool ReducedMotion => !SystemParameters.ClientAreaAnimation;
    private bool FxEnabled => preferences.InteractionFx && !ReducedMotion;

    // Clips with writing in them are never mirrored for hover facing.
    private static readonly HashSet<string> NonMirroredClips = ["深度思考碎碎念", "原地小憩沉眠", "写福字", "是啊，吃什么", "吃Token"];

    // The sprite sits in a host whose transforms (origin at her feet) carry all these effects, separate
    // from the clip's own mirroring.
    private FrameworkElement CreateSpriteHost(Image image)
    {
        var group = new TransformGroup();
        group.Children.Add(fxFlip);
        group.Children.Add(fxPop);
        group.Children.Add(fxStretch);
        group.Children.Add(fxTilt);
        group.Children.Add(fxShift);
        spriteHost = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            RenderTransformOrigin = new Point(.5, FeetY), RenderTransform = group };
        spriteHost.Children.Add(image);
        DpiChanged += (_, _) => LeaveEdgeHide(); // the monitor rectangles changed scale
        return spriteHost;
    }

    // PlayAnimation hook.
    private void NoteAnimationStarted()
    {
        animationStartedUtc = DateTime.UtcNow;
        if (targetMove != null && animationPhase is not ("TargetRun" or "Arrive")) StopTargetRun(); // something else took over the run
        fxFlip.ScaleX = 1; // the hover check sets it again for the new clip within one tick
    }

    private bool ClipMirrored => videoImage?.RenderTransform is ScaleTransform { ScaleX: < 0 };

    private bool FacingAllowed => preferences.HoverFacing && companionReady && !dragging && coast == null && targetMove == null
        && movementRule == null && !Crawling && !mimicActive && !PuppetActive && edgeHide == null && !exitAnimation
        && animationPhase is "Idle" or "Event" or "Intent" or "Rule" or "Click" or "Interact" or "Daily" or "Preview" or "Arrive"
        && !NonMirroredClips.Contains(currentAction);

    private bool TiltAllowed => FxEnabled && companionReady && !dragging && coast == null && targetMove == null && !Crawling
        && !mimicActive && !PuppetActive && edgeHide == null && !exitAnimation && IsVisible;

    // CompanionTick (every 40 ms).
    private void InteractionTick(DateTime now)
    {
        if (!companionReady || companionClosed || exitAnimation || videoImage == null || PresentationSource.FromVisual(this) == null) return;
        if (PuppetActive) // the Agent drives her now
        {
            StopTargetRun(); EndTargetSelection(); LeaveEdgeHide(); EndCoast(false);
            if (fxFlip.ScaleX != 1) fxFlip.ScaleX = 1;
            tiltTarget = 0;
            return;
        }
        CompanionNative.GetCursorPos(out var cursor);
        var p = PointFromScreen(new Point(cursor.X, cursor.Y)); // window DIP
        var body = UprightVisibleBounds;
        double cx = body.X + body.Width / 2;

        // Hover facing: right half → face right, left half → face left; back to normal a moment after leaving.
        bool inside = IsVisible && p.X >= body.Left - 4 && p.X <= body.Right + 4 && p.Y >= body.Top - 4 && p.Y <= body.Bottom + 4;
        // Over her head the side is kept, so stroking her head (a pat) never flips her mid-stroke.
        double unit = SpriteSize / 360, spriteLeft = (Width - SpriteSize * 640 / 360) / 2, spriteTop = Height - SpriteSize;
        bool overHead = p.X >= spriteLeft + 230 * unit && p.X <= spriteLeft + 410 * unit && p.Y >= spriteTop + 48 * unit && p.Y <= spriteTop + 170 * unit;
        if (inside && overHead) hoverInside = true;
        else if (inside)
        {
            if (p.X > cx + body.Width * .08) hoverRight = true;
            else if (p.X < cx - body.Width * .08) hoverRight = false;
            hoverInside = true;
        }
        else if (hoverInside) { hoverInside = false; hoverLeftAt = now; }
        bool facing = FacingAllowed && (hoverInside || (now - hoverLeftAt).TotalSeconds < .4);
        double flip = facing && (hoverRight != ClipMirrored) ? -1 : 1;
        if (fxFlip.ScaleX != flip) fxFlip.ScaleX = flip;

        // Lean towards a cursor that is near her (spring-smoothed in the render loop).
        double radius = 140 * preferences.Scale;
        bool near = p.X >= body.Left - radius && p.X <= body.Right + radius && p.Y >= body.Top - 30 && p.Y <= body.Bottom + 30;
        tiltTarget = TiltAllowed && near ? Math.Clamp((p.X - cx) / radius, -1, 1) : 0;
        if (Math.Abs(tiltTarget - tilt) > .002 || Math.Abs(tiltVelocity) > .002) StartFxRendering();

        // Held still while dragging: the stretch relaxes.
        if (dragging && dragSamples.Count > 0 && fxClock.Elapsed.TotalMilliseconds - dragSamples[^1].Ms > 100) fxStretch.Matrix = Matrix.Identity;
        EdgeTick(now, cursor);
        TargetSelectionTick(now);
    }

    private void StartFxRendering()
    {
        if (fxRendering) return;
        fxRendering = true;
        fxLastMs = fxClock.Elapsed.TotalMilliseconds;
        CompositionTarget.Rendering += FxFrame;
    }

    private void FxFrame(object? sender, EventArgs e)
    {
        double nowMs = fxClock.Elapsed.TotalMilliseconds;
        double dt = Math.Clamp(nowMs - fxLastMs, 0, 50);
        fxLastMs = nowMs;
        if (companionClosed) { CompositionTarget.Rendering -= FxFrame; fxRendering = false; return; }
        bool busy = false;
        if (coast != null) { CoastFrame(dt); busy = true; }
        // Critically damped spring towards the lean target.
        double omega = 12, s = dt / 1000;
        tiltVelocity += (omega * omega * (tiltTarget - tilt) - 2 * omega * tiltVelocity) * s;
        tilt += tiltVelocity * s;
        if (Math.Abs(tiltTarget - tilt) < .001 && Math.Abs(tiltVelocity) < .001) { tilt = tiltTarget; tiltVelocity = 0; }
        else busy = true;
        fxTilt.Angle = tilt * 3.5;
        fxShift.X = tilt * 5 * preferences.Scale;
        if (!busy) { CompositionTarget.Rendering -= FxFrame; fxRendering = false; }
    }

    // ---------------------------------------------------------------- squash & stretch
    private int popStreak;
    private DateTime lastPopUtc;

    // A click pop: squash down, spring up, settle. Quick repeated clicks make it bouncier.
    private void Pop()
    {
        if (!FxEnabled) return;
        var now = DateTime.UtcNow;
        popStreak = (now - lastPopUtc).TotalSeconds < .6 ? Math.Min(popStreak + 1, 4) : 0;
        lastPopUtc = now;
        Squash(.13 * (1 + popStreak * .15), .42, false);
    }

    // amount > 0 squashes vertically (or horizontally for a side wall) and springs back.
    private void Squash(double amount, double seconds, bool sideways)
    {
        DoubleAnimationUsingKeyFrames Keys(params double[] values)
        {
            var frames = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(seconds), FillBehavior = FillBehavior.Stop };
            double[] at = [0, .2, .48, .75, 1];
            for (int i = 0; i < values.Length; i++)
                frames.KeyFrames.Add(new SplineDoubleKeyFrame(values[i], KeyTime.FromPercent(at[i]), new KeySpline(.3, 0, .3, 1)));
            return frames;
        }
        double a = amount;
        var squashed = Keys(1, 1 - a, 1 + a * .45, 1 - a * .15, 1);
        var bulged = Keys(1, 1 + a * .6, 1 - a * .3, 1 + a * .08, 1);
        fxPop.BeginAnimation(sideways ? ScaleTransform.ScaleXProperty : ScaleTransform.ScaleYProperty, squashed);
        fxPop.BeginAnimation(sideways ? ScaleTransform.ScaleYProperty : ScaleTransform.ScaleXProperty, bulged);
    }

    // Stretch along the direction of travel (speed in DIP/ms), centred on her body.
    private void StretchAlong(double vx, double vy)
    {
        double speed = Math.Sqrt(vx * vx + vy * vy);
        if (!FxEnabled || speed < .05) { fxStretch.Matrix = Matrix.Identity; return; }
        double amount = Math.Min(.12, speed * .045), angle = Math.Atan2(vy, vx) * 180 / Math.PI;
        double centre = (BodyCentreY - FeetY) * SpriteSize; // body centre relative to the feet origin
        var m = Matrix.Identity;
        m.Translate(0, -centre);
        m.Rotate(-angle);
        m.Scale(1 + amount, 1 / (1 + amount));
        m.Rotate(angle);
        m.Translate(0, centre);
        fxStretch.Matrix = m;
    }

    // ---------------------------------------------------------------- dragging & flinging
    private readonly List<(double Ms, double X, double Y)> dragSamples = new();
    // Velocities in DIP per second; Elapsed in milliseconds.
    private sealed class CoastState { public double Vx, Vy, Elapsed; public Rect Area; }
    private CoastState? coast;

    // CompanionDown: grabbing her stops a flight (a fast one is a catch worth points).
    private void OnCompanionPressed()
    {
        if (coast != null)
        {
            CatchFlyingPet(Math.Sqrt(coast.Vx * coast.Vx + coast.Vy * coast.Vy));
            EndCoast(false);
        }
        StopTargetRun();
        dragSamples.Clear();
        EndTargetSelection();
    }

    // CompanionMove, once the press became a drag.
    private void OnDragStarted()
    {
        StopTargetRun();
        if (edgeHide is { Seek: true }) SeekFound(true); // 捉迷藏: dragged out of her hiding place
        else if (edgeHide != null) edgeHide = null; // pulled back from the edge
        tiltTarget = 0;
        ApplyGrabCursor(true);
    }

    private void OnDragMoved()
    {
        if (!preferences.EdgeHide || KeyMouseActive) KeepDragOnScreen();
        double ms = fxClock.Elapsed.TotalMilliseconds;
        dragSamples.Add((ms, Left, Top));
        if (dragSamples.Count > 10) dragSamples.RemoveAt(0);
        var (vx, vy) = DragVelocity();
        StretchAlong(vx, vy);
    }

    // Release speed from the last ~120 ms of movement (DIP per ms).
    private (double Vx, double Vy) DragVelocity()
    {
        if (dragSamples.Count < 2) return (0, 0);
        var last = dragSamples[^1];
        if (fxClock.Elapsed.TotalMilliseconds - last.Ms > 80) return (0, 0); // held still before letting go
        var first = last;
        for (int i = dragSamples.Count - 1; i >= 0 && last.Ms - dragSamples[i].Ms <= 120; i--) first = dragSamples[i];
        double dt = last.Ms - first.Ms;
        return dt <= 0 ? (0, 0) : ((last.X - first.X) / dt, (last.Y - first.Y) / dt);
    }

    // Without the edge hide she cannot be dragged off the screens: her visible body stays inside the desktop.
    private void KeepDragOnScreen()
    {
        var body = CompanionVisibleBounds;
        double left = SystemParameters.VirtualScreenLeft, top = SystemParameters.VirtualScreenTop;
        double right = left + SystemParameters.VirtualScreenWidth, bottom = top + SystemParameters.VirtualScreenHeight;
        double x = Math.Clamp(Left, left - body.Left, Math.Max(left - body.Left, right - body.Right));
        double y = Math.Clamp(Top, top - body.Top, Math.Max(top - body.Top, bottom - body.Bottom));
        if (x != Left) Left = x;
        if (y != Top) Top = y;
    }

    // CompanionUp after a drag. True when a fling or the edge hide took over the drop.
    private bool DropWithInteraction()
    {
        ApplyGrabCursor(false);
        var (vx, vy) = DragVelocity();
        dragSamples.Clear();
        fxStretch.Matrix = Matrix.Identity;
        double speed = Math.Sqrt(vx * vx + vy * vy);
        if (KeyMouseActive) return false; // 键鼠陪伴: she stays at her desk, no flight and no hiding at an edge
        if (TryEdgeHide()) // dropped (or flung) off every screen
        {
            PublishMcpInteraction("drag_end", new { Left, Top, cancelled = false });
            FinishDragAnimation();
            return true;
        }
        if (preferences.FlingBall && !ReducedMotion && !PuppetActive && speed >= .6)
        {
            PublishMcpInteraction("drag_end", new { Left, Top, cancelled = false });
            crawlResumeAt = DateTime.UtcNow.AddSeconds(8);
            // release speed in DIP/s, softly limited (dsh-pet), times the throw power of the settings
            double perSecond = speed * 1000, scale = CompanionDshPet.SoftClamp(perSecond) * preferences.PhysicsPower / perSecond;
            StartCoast(vx * 1000 * scale, vy * 1000 * scale);
            return true;
        }
        if (FxEnabled) Squash(.07, .32, false); // a small settle where she lands
        return false;
    }

    private void StartCoast(double vx, double vy)
    {
        coast = new CoastState { Vx = vx, Vy = vy, Area = CompanionEdgeLayout.WorkArea(this) };
        CancelMovement();
        if (ClipOr("变成球") is { } ball) PlayAnimation(ball, "Fling", startSeconds: IsForeignClip(ball) ? 0 : 2);
        else PlayIdle();
        StartFxRendering();
    }

    // One frame of the flight (dt in ms): dsh-pet's physics — gravity, walls, ceiling, the floor of the work area with
    // friction — with the settings of 甩出手感. Gravity 0 is the old weightless slide.
    private void CoastFrame(double dt)
    {
        var c = coast!;
        if (dragging || !IsVisible || companionClosed) { EndCoast(false); return; }
        var body = CompanionVisibleBounds;
        double minLeft = c.Area.Left - body.Left, maxLeft = c.Area.Right - body.Right;
        double minTop = c.Area.Top - body.Top, maxTop = c.Area.Bottom - body.Bottom;
        double beforeVx = c.Vx;
        var (state, bounced, impact, floor, rest) = CompanionDshPet.Step(new CompanionDshPet.ThrowState(Left, Top, c.Vx, c.Vy), dt / 1000,
            minLeft, minTop, Math.Max(minLeft, maxLeft), Math.Max(minTop, maxTop), CurrentPhysics);
        if (bounced && FxEnabled && CompanionDshPet.ImpactSquash(impact) is var amount and > 0)
            Squash(amount, .3, Math.Sign(state.Vx) != Math.Sign(beforeVx) && Math.Abs(beforeVx) > 1 && !floor);
        c.Vx = state.Vx; c.Vy = state.Vy; c.Elapsed += dt;
        Left = state.X; Top = state.Y;
        StretchAlong(c.Vx / 1000, c.Vy / 1000);
        if (rest || c.Elapsed > 30000) EndCoast(true);
    }

    // The ball clip goes on to its natural end, where she stands up again.
    private void EndCoast(bool settle)
    {
        if (coast == null) return;
        coast = null;
        fxStretch.Matrix = Matrix.Identity;
        if (settle && FxEnabled) Squash(.08, .34, false);
        ClampCompanion();
        anchor = new Point(Left, Top);
        SaveCompanion();
    }

    // ---------------------------------------------------------------- click zones
    // Where the click landed on her body: 0 head, 1 body, 2 tail (from the last click's screen position).
    private int ClickZone()
    {
        if (videoImage == null || videoImage.ActualHeight <= 0 || PresentationSource.FromVisual(videoImage) == null) return 1;
        var p = videoImage.PointFromScreen(lastClickPoint);
        double y = p.Y * 360 / videoImage.ActualHeight;
        double fraction = (y - 58) / (336 - 58);
        return fraction < .36 ? 0 : fraction < .68 ? 1 : 2;
    }

    private static readonly string[][] ZoneClips =
    [
        ["点击回应-害羞惊讶", "点击回应-傲娇生气"],
        ["点击回应-元气挥手", "点击回应-开心跃动"],
        ["点击回应-挠痒咯咯笑", "用鲸鱼尾巴拍打地面"],
    ];
    internal static readonly string[] ZoneNames = ["头", "身体", "尾巴"];

    // The reply to a click: clips of the click pool that suit the zone (clips the owner added to the pool
    // fit every zone); a tail click may also get the tail slap. Falls back to the whole click pool.
    private CompanionAnimationRule ClickRule(out int zone)
    {
        zone = ClickZone();
        var context = AnimationContext();
        if (preferences.ClickZones)
        {
            var zoned = ZoneClips.SelectMany(z => z).ToHashSet();
            var options = new List<(CompanionAnimationRule Rule, double Weight)>();
            foreach (var rule in animationPolicy.Rules)
            {
                bool own = ZoneClips[zone].Contains(rule.Id);
                if (rule.Pool == "Click" && (own || !zoned.Contains(rule.Id)))
                    options.Add((rule, animationPolicy.Weight(rule, context)));
                else if (zone == 2 && rule.Id == "用鲸鱼尾巴拍打地面" && rule.Pool != "Disabled")
                    options.Add((rule, 40));
            }
            options.RemoveAll(o => o.Weight <= 0);
            if (options.Count > 0)
            {
                double point = companionRandom.NextDouble() * options.Sum(o => o.Weight);
                foreach (var (rule, weight) in options) { point -= weight; if (point < 0) return rule; }
                return options[^1].Rule;
            }
        }
        try { return animationPolicy.Choose("Click", context, companionRandom); }
        catch (InvalidOperationException) { return animationPolicy.Find(CompanionSkins.IdleId); }
    }

    // ---------------------------------------------------------------- hiding at a screen edge
    private sealed class EdgeHideState
    {
        public int Edge;          // 0 left, 1 top, 2 right, 3 bottom
        public Rect Screen;       // that monitor's bounds, in the pet's DIP
        public double Along;      // Top (left/right edges) or Left (top/bottom edges)
        public double Reveal;     // 0 hidden … 1 peeking
        public DateTime AwayAt = DateTime.MaxValue;
        public bool Seek;         // 捉迷藏: hiding on purpose, to be found
        public DateTime SeekStartUtc, HintUntil;
    }
    private EdgeHideState? edgeHide;
    internal bool EdgeHidden => edgeHide != null;

    // A monitor rectangle (physical pixels) in the pet's own coordinates (DIP, like Left/Top).
    private Rect MonitorRect(System.Drawing.Rectangle pixels)
    {
        var topLeft = PointFromScreen(new Point(pixels.Left, pixels.Top));
        var bottomRight = PointFromScreen(new Point(pixels.Right, pixels.Bottom));
        return new Rect(Left + topLeft.X, Top + topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
    }

    // After a drop: if her body ended up outside every monitor, tuck her just beyond the nearest edge.
    private bool TryEdgeHide()
    {
        if (!preferences.EdgeHide || PuppetActive || PresentationSource.FromVisual(this) == null) return false;
        var body = UprightVisibleBounds;
        var centre = PointToScreen(new Point(body.X + body.Width / 2, body.Y + body.Height / 2));
        var point = new System.Drawing.Point((int)centre.X, (int)centre.Y);
        if (Forms.Screen.AllScreens.Any(s => s.Bounds.Contains(point))) return false;
        var screen = Forms.Screen.FromPoint(point);
        var b = screen.Bounds;
        var overshoot = new[] { b.Left - centre.X, b.Top - centre.Y, centre.X - b.Right, centre.Y - b.Bottom };
        int edge = Array.IndexOf(overshoot, overshoot.Max());
        var rect = MonitorRect(screen.Bounds);
        edgeHide = new EdgeHideState
        {
            Edge = edge, Screen = rect,
            Along = edge is 0 or 2 ? Math.Clamp(Top, rect.Top - body.Top, rect.Bottom - body.Bottom)
                                   : Math.Clamp(Left, rect.Left - body.Left, rect.Right - body.Right)
        };
        EndCrawl(true);
        PlaceAtEdge();
        anchor = new Point(Left, Top);
        return true;
    }

    private void PlaceAtEdge()
    {
        var e = edgeHide!;
        var body = UprightVisibleBounds;
        double peekX = body.Width * .4, peekY = body.Height * .35;
        switch (e.Edge)
        {
            case 0: Left = e.Screen.Left - body.Right + peekX * e.Reveal; Top = e.Along; break;
            case 2: Left = e.Screen.Right - body.Left - peekX * e.Reveal; Top = e.Along; break;
            case 1: Top = e.Screen.Top - body.Bottom + peekY * e.Reveal; Left = e.Along; break;
            default: Top = e.Screen.Bottom - body.Top - peekY * e.Reveal; Left = e.Along; break;
        }
        CompanionEdgeLayout.Refresh(this);
    }

    // Peek out while the cursor is at that edge near her (or while she has something to say).
    private void EdgeTick(DateTime now, CompanionNative.Point cursor)
    {
        var e = edgeHide;
        if (e == null || dragging) return;
        var local = PointFromScreen(new Point(cursor.X, cursor.Y));
        double x = Left + local.X, y = Top + local.Y;
        var body = UprightVisibleBounds;
        double zone = 16 + (e.Reveal > 0 ? (e.Edge is 0 or 2 ? body.Width * .4 : body.Height * .35) * e.Reveal : 0);
        bool near = e.Edge switch
        {
            0 => x <= e.Screen.Left + zone && y >= Top + body.Top - 40 && y <= Top + body.Bottom + 40,
            2 => x >= e.Screen.Right - zone && y >= Top + body.Top - 40 && y <= Top + body.Bottom + 40,
            1 => y <= e.Screen.Top + zone && x >= Left + body.Left - 40 && x <= Left + body.Right + 40,
            _ => y >= e.Screen.Bottom - zone && x >= Left + body.Left - 40 && x <= Left + body.Right + 40,
        };
        if (near || bubble.Visibility == Visibility.Visible) e.AwayAt = DateTime.MaxValue;
        else if (e.AwayAt == DateTime.MaxValue) e.AwayAt = now.AddSeconds(.6);
        double target = now < e.AwayAt ? 1 : e.Seek && now < e.HintUntil ? .35 : 0; // 捉迷藏: a little peek as a hint
        if (Math.Abs(target - e.Reveal) < .001) return;
        e.Reveal = target > e.Reveal ? Math.Min(target, e.Reveal + .2) : Math.Max(target, e.Reveal - .2);
        PlaceAtEdge();
    }

    // A click while she peeks out: she comes back onto the screen and waves.
    private bool EdgeClick()
    {
        if (edgeHide == null) return false;
        if (edgeHide.Seek) { SeekFound(false); return true; } // 捉迷藏: found her
        edgeHide = null;
        ClampCompanion();
        anchor = new Point(Left, Top);
        SaveCompanion();
        if (TryFindRule("点击回应-元气挥手") is { } wave) PlayAnimation(wave, "Click");
        return true;
    }

    // Brought back by other means (tray, settings): leave the edge.
    private void LeaveEdgeHide()
    {
        if (edgeHide == null) return;
        edgeHide = null;
        ClampCompanion();
        anchor = new Point(Left, Top);
    }
}
