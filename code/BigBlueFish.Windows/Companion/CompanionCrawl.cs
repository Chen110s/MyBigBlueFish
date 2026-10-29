using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace BigBlueFish.Windows;

// Edge crawl: the pet walks sideways (螃蟹走路) around the work area of her monitor and
// rotates at each corner so her feet stay against the edge — upright on the bottom,
// ±90° on the side walls and upside down along the top. A lap starts and ends at the
// edge point nearest to her; patrol mode chains laps until stopped or dragged away.
public partial class MainWindow
{
    internal static readonly string[] CrawlModeLabels = ["关闭", "偶尔", "经常", "一直爬（巡游）"];
    internal static readonly string[] CrawlSpeedLabels = ["慢", "中", "快"];
    private static readonly double[] CrawlSpeeds = [60, 100, 160]; // DIP per second at 100% scale
    private static readonly double[] CrawlEventChances = [0, .15, .4, 0];
    private const int CrawlPatrolMode = 3;
    private const string CrawlRuleId = "螃蟹走路";
    // The clip turns sideways during its first ~1.4 s, walks until ~7.3 s, then turns back to the front.
    private const double CrawlWalkStart = 1.4, CrawlLoopStart = 1.5, CrawlLoopEnd = 7.3;
    private const double CrawlTurnSeconds = .45;

    private sealed class CrawlSegment
    {
        public Point From, To;
        public double AngleFrom, AngleTo;
        public bool Turn;
        public bool? Mirror;
        public double Length => (To - From).Length;
    }

    private sealed class CrawlState
    {
        public readonly List<CrawlSegment> Segments = new();
        public int Index, LapStart, LapEnd; // LapEnd is exclusive
        public double Progress;
        public Point Origin; // window Left/Top before the crawl started
        public double Scale;
        public Rect Area;           // work area the path was built for
        public double HalfWidth;    // upright body half extents
        public double HalfHeight;
        public bool Mirror, StopRequested, Finished;
        public long LastTick;
    }

    private CrawlState? crawl;
    private double crawlAngle;
    private readonly RotateTransform crawlRotation = new();
    private bool crawlSuppressed;
    private DateTime crawlResumeAt = DateTime.MinValue;

    internal bool Crawling => crawl is { Finished: false };
    private static bool IsCrawlPhase(string phase) => phase is "CrawlIntro" or "Crawl";
    private int CrawlMode => Math.Clamp(preferences.CrawlMode, 0, CrawlPatrolMode);
    private bool CrawlPatrolActive => CrawlMode == CrawlPatrolMode && !crawlSuppressed && !CrawlBlocked;
    private bool CanCrawl => companionReady && !companionClosed && !exitAnimation && !dragging && !PuppetActive
        && !hiddenByUser && !fullscreenHidden && videoPlayer != null;

    private Rect UprightVisibleBounds => new(
        (Width - SpriteSize * 640 / 360) / 2 + SpriteSize * 208 / 360,
        Height - SpriteSize + SpriteSize * 58 / 360,
        SpriteSize * 220 / 360, SpriteSize * 278 / 360);

    // Clamping and the bubble use these bounds, so they follow the current crawl rotation.
    internal Rect CompanionVisibleBounds
    {
        get
        {
            var upright = UprightVisibleBounds;
            if (crawlAngle == 0) return upright;
            double radians = crawlAngle * Math.PI / 180, cos = Math.Abs(Math.Cos(radians)), sin = Math.Abs(Math.Sin(radians));
            double width = upright.Width * cos + upright.Height * sin, height = upright.Width * sin + upright.Height * cos;
            return new(upright.X + (upright.Width - width) / 2, upright.Y + (upright.Height - height) / 2, width, height);
        }
    }

    private Point CrawlPivot
    {
        get
        {
            var body = UprightVisibleBounds;
            return new(body.X + body.Width / 2, body.Y + body.Height / 2);
        }
    }

    private static double NormalizeAngle(double angle)
    {
        angle %= 360;
        if (angle <= -180) angle += 360;
        else if (angle > 180) angle -= 360;
        return angle;
    }

    private static bool IsUpright(double angle) => Math.Abs(NormalizeAngle(angle)) < .5;
    private static double SmoothStep(double t) => t * t * (3 - 2 * t);

    // Upright movement: the clip faces left, so moving right needs the mirrored clip.
    private static bool? UprightMirror(Vector direction) => direction.X > .5 ? true : direction.X < -.5 ? false : (bool?)null;

    private static Point Closest(Point point, Point start, Point end)
    {
        var line = end - start;
        double lengthSquared = line.LengthSquared;
        double t = lengthSquared <= 0 ? 0 : Math.Clamp((point - start) * line / lengthSquared, 0, 1);
        return start + line * t;
    }

    private CrawlState? BuildCrawl()
    {
        var area = CompanionEdgeLayout.WorkArea(this);
        var body = UprightVisibleBounds;
        double a = body.Width / 2, b = body.Height / 2;
        double left = area.Left, right = area.Right, top = area.Top, bottom = area.Bottom;
        if (!double.IsFinite(left + right + top + bottom) || right - left < 2 * (a + b) || bottom - top < 2 * (a + b)) return null;
        // Body-centre tracks in counter-clockwise order; clockwise laps walk them backwards.
        var edges = new (Point Start, Point End, double Angle)[]
        {
            (new Point(left + a, bottom - b), new Point(right - a, bottom - b), 0),     // bottom, moving right
            (new Point(right - b, bottom - a), new Point(right - b, top + a), -90),     // right wall, moving up
            (new Point(right - a, top + b), new Point(left + a, top + b), -180),        // top, upside down, moving left
            (new Point(left + b, top + a), new Point(left + b, bottom - a), -270),      // left wall, moving down
        };
        var pivot = CrawlPivot;
        var center = new Point(Left + pivot.X, Top + pivot.Y);
        int nearest = 0;
        var entry = Closest(center, edges[0].Start, edges[0].End);
        for (int i = 1; i < edges.Length; i++)
        {
            var candidate = Closest(center, edges[i].Start, edges[i].End);
            if ((candidate - center).Length < (entry - center).Length) { nearest = i; entry = candidate; }
        }
        bool clockwise = companionRandom.NextDouble() < .5;
        bool lapMirror = !clockwise;
        var state = new CrawlState { Origin = new Point(Left, Top), Scale = preferences.Scale, Area = area, HalfWidth = a, HalfHeight = b };
        var position = center;
        double angle = 0;
        void Add(Point to, double toAngle, bool turn, bool? mirror)
        {
            // Sub-pixel pieces are absorbed so the lap still closes exactly on itself.
            if ((to - position).Length >= .5 || Math.Abs(toAngle - angle) >= .01)
                state.Segments.Add(new CrawlSegment { From = position, To = to, AngleFrom = angle, AngleTo = toAngle, Turn = turn, Mirror = mirror });
            else if (state.Segments.Count > 0)
                state.Segments[^1].To = to;
            position = to;
            angle = toAngle;
        }
        // Approach the nearest edge upright, then turn so the feet face it.
        Add(entry, 0, false, UprightMirror(entry - center));
        Add(entry, NormalizeAngle(edges[nearest].Angle), true, null);
        state.LapStart = state.Segments.Count;
        int step = clockwise ? -1 : 1;
        for (int i = 0; i <= edges.Length; i++)
        {
            var edge = edges[((nearest + step * i) % edges.Length + edges.Length) % edges.Length];
            Point start = clockwise ? edge.End : edge.Start, end = clockwise ? edge.Start : edge.End;
            if (i > 0) Add(start, angle + (clockwise ? 90 : -90), true, null);
            Add(i == edges.Length ? entry : end, angle, false, lapMirror);
        }
        state.LapEnd = state.Segments.Count;
        // After a single lap: turn upright and walk back to where she was.
        Add(entry, Math.Round(angle / 360) * 360, true, null);
        Add(center, angle, false, UprightMirror(center - entry));
        if (state.LapEnd == state.LapStart || state.Segments.Count == 0) return null;
        state.Mirror = false;
        foreach (var segment in state.Segments)
            if (segment.Mirror is bool mirror) { state.Mirror = mirror; break; }
        return state;
    }

    private bool StartCrawl(bool manual = false)
    {
        if (manual) crawlSuppressed = false;
        if (crawl != null)
        {
            if (manual && !crawl.Finished) crawl.StopRequested = false;
            return !crawl.Finished;
        }
        if (!CanCrawl) return false;
        var state = BuildCrawl();
        if (state == null) return false;
        CancelMovement();
        crawl = state;
        companionLayout.RenderTransform = crawlRotation;
        ApplyCrawlPose(state.Segments[0].From, state.Segments[0].AngleFrom);
        PlayCrawlClip(true);
        return true;
    }

    private void PlayCrawlClip(bool intro)
    {
        if (crawl is not { Finished: false } state || companionClosed || exitAnimation) return;
        state.LastTick = 0;
        var rule = animationPolicy.Find(CrawlRuleId);
        if (intro) PlayAnimation(rule, "CrawlIntro", state.Mirror, 0, CrawlLoopEnd);
        else PlayAnimation(rule, "Crawl", state.Mirror, CrawlLoopStart, CrawlLoopEnd);
    }

    private void PlayCrawlOutro()
    {
        if (crawl is not { Finished: true } state || animationPhase == "CrawlOutro" || companionClosed || exitAnimation) return;
        PlayAnimation(animationPolicy.Find(CrawlRuleId), "CrawlOutro", state.Mirror, CrawlLoopEnd);
    }

    private void ApplyCrawlPose(Point center, double angle)
    {
        crawlAngle = Math.Abs(angle % 360) < 1e-6 ? 0 : angle;
        var pivot = CrawlPivot;
        crawlRotation.CenterX = pivot.X;
        crawlRotation.CenterY = pivot.Y;
        crawlRotation.Angle = crawlAngle;
        Left = center.X - pivot.X;
        Top = center.Y - pivot.Y;
    }

    // Called for every presented video frame; movement uses real elapsed time.
    private void CrawlFrame(double seconds)
    {
        if (crawl is not { Finished: false } state || !IsCrawlPhase(animationPhase)) return;
        long now = Stopwatch.GetTimestamp();
        double dt = state.LastTick == 0 ? 1.0 / CompanionVideoPlayer.Fps
            : Math.Min(.1, (now - state.LastTick) / (double)Stopwatch.Frequency);
        state.LastTick = now;
        if (dragging || animationPhase == "CrawlIntro" && seconds < CrawlWalkStart) return;
        AdvanceCrawl(state, dt);
    }

    private void AdvanceCrawl(CrawlState state, double dt)
    {
        int speedIndex = Math.Clamp(preferences.CrawlSpeedIndex, 0, CrawlSpeeds.Length - 1);
        double speed = CrawlSpeeds[speedIndex] * preferences.Scale * (state.StopRequested ? 3 : 1);
        while (dt > 0)
        {
            var segment = state.Segments[state.Index];
            if (state.StopRequested && IsUpright(segment.AngleFrom + (segment.AngleTo - segment.AngleFrom) * state.Progress))
            {
                FinishCrawl();
                return;
            }
            if (PauseCrawlForEvent(state, segment)) return;
            double rate = segment.Turn ? 1 / CrawlTurnSeconds : speed / Math.Max(.001, segment.Length);
            double remaining = (1 - state.Progress) / rate;
            if (dt < remaining)
            {
                state.Progress += dt * rate;
                dt = 0;
                continue;
            }
            dt -= remaining;
            state.Progress = 0;
            state.Index++;
            if (state.Index == state.LapEnd && CrawlPatrolActive && !state.StopRequested) state.Index = state.LapStart;
            if (state.Index >= state.Segments.Count)
            {
                ApplyCrawlPose(segment.To, segment.AngleTo);
                FinishCrawl();
                return;
            }
            var next = state.Segments[state.Index];
            if (next.Mirror is bool mirror && mirror != state.Mirror)
            {
                state.Mirror = mirror;
                Dispatcher.BeginInvoke(() => { if (ReferenceEquals(crawl, state) && IsCrawlPhase(animationPhase)) PlayCrawlClip(false); });
            }
        }
        var current = state.Segments[state.Index];
        double eased = current.Turn ? SmoothStep(state.Progress) : state.Progress;
        double poseAngle = current.AngleFrom + (current.AngleTo - current.AngleFrom) * eased;
        var poseCenter = current.From + (current.To - current.From) * eased;
        ApplyCrawlPose(current.Turn ? KeepInside(state, poseCenter, poseAngle) : poseCenter, poseAngle);
    }

    // While turning, the rotated body's bounding box grows (largest at 45°). Keeping its centre
    // inside the shrunken work area makes a corner turn hug both edges instead of poking out.
    private static Point KeepInside(CrawlState state, Point center, double angle)
    {
        double radians = angle * Math.PI / 180, cos = Math.Abs(Math.Cos(radians)), sin = Math.Abs(Math.Sin(radians));
        double halfWidth = state.HalfWidth * cos + state.HalfHeight * sin, halfHeight = state.HalfWidth * sin + state.HalfHeight * cos;
        var area = state.Area;
        return new Point(
            Math.Clamp(center.X, area.Left + halfWidth, Math.Max(area.Left + halfWidth, area.Right - halfWidth)),
            Math.Clamp(center.Y, area.Top + halfHeight, Math.Max(area.Top + halfHeight, area.Bottom - halfHeight)));
    }

    // Patrol keeps her daily life: when an ordinary event is due while she is on the bottom
    // edge (upright), she stops there for it and resumes crawling afterwards.
    private bool PauseCrawlForEvent(CrawlState state, CrawlSegment segment)
    {
        if (!CrawlPatrolActive || state.StopRequested || segment.Turn || !IsUpright(segment.AngleFrom)
            || state.Index < state.LapStart || state.Index >= state.LapEnd || DateTime.UtcNow < nextAction) return false;
        nextAction = DateTime.UtcNow.AddSeconds(preferences.AnimationIntervalSeconds);
        var local = DateTime.Now;
        var date = local.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        CompanionAnimationRule rule;
        if (local.Hour == 23 && preferences.LastBrushDate != date)
        {
            preferences.LastBrushDate = date;
            SaveCompanion();
            rule = animationPolicy.Find("晨间刷牙");
        }
        else
        {
            rule = animationPolicy.Choose("Event", AnimationContext(), companionRandom);
            if (rule.MoveKind != null) return false; // she is already walking
        }
        string phase = rule.Pool == "Daily" ? "Daily" : "Event";
        Dispatcher.BeginInvoke(() => { if (ReferenceEquals(crawl, state) && IsCrawlPhase(animationPhase)) PlayAnimation(rule, phase); });
        return true;
    }

    private void FinishCrawl()
    {
        if (crawl is not { Finished: false } state) return;
        var pivot = CrawlPivot;
        ApplyCrawlPose(new Point(Left + pivot.X, Top + pivot.Y), 0);
        state.Finished = true;
        nextAction = DateTime.UtcNow.AddSeconds(preferences.AnimationIntervalSeconds);
        // Frame callbacks run inside the decoder's dispatch; switch clips afterwards.
        Dispatcher.BeginInvoke(() => PlayCrawlOutro());
    }

    private void EndCrawl(bool returnHome, bool clamp = true)
    {
        if (crawl == null) return;
        var origin = crawl.Origin;
        crawl = null;
        crawlAngle = 0;
        crawlRotation.Angle = 0;
        companionLayout.RenderTransform = Transform.Identity;
        if (returnHome) { Left = origin.X; Top = origin.Y; }
        if (clamp) ClampCompanion();
        anchor = new Point(Left, Top);
    }

    // Ends the crawl right away and swaps the walking clip for an idle one.
    private void StopCrawlNow(bool returnHome, bool clamp = true)
    {
        if (crawl == null) return;
        EndCrawl(returnHome, clamp);
        if (IsCrawlPhase(animationPhase) && companionReady && !exitAnimation && !dragging) PlayIdle();
    }

    private void RequestCrawlStop()
    {
        crawlSuppressed = true;
        if (crawl is { Finished: false } state) state.StopRequested = true;
    }

    private void SetCrawlMode(int mode)
    {
        preferences.CrawlMode = Math.Clamp(mode, 0, CrawlPatrolMode);
        crawlSuppressed = false;
        crawlResumeAt = DateTime.MinValue;
        if (preferences.CrawlMode == 0) RequestCrawlStop();
        else if (CrawlPatrolActive && crawl == null) StartCrawl();
    }

    private string CrawlMenuHeader => crawl is { Finished: false, StopRequested: false }
        ? (CrawlMode == CrawlPatrolMode ? "停止巡游" : "停止爬行")
        : (CrawlMode == CrawlPatrolMode ? "开始巡游" : "沿屏幕爬一圈");

    private void ToggleCrawlFromMenu()
    {
        if (crawl is { Finished: false, StopRequested: false }) { RequestCrawlStop(); return; }
        if (crawl is { Finished: true }) EndCrawl(false); // still turning back to the front: start again from here
        if (hiddenByUser || fullscreenHidden) RestoreCompanion();
        if (!StartCrawl(true) && CanCrawl && crawl == null) ShowCompanionNotice("主人，这块屏幕太小啦，鱼鱼爬不开～");
    }

    // AnimationFinished hook. Returns true when the next clip has been chosen.
    private bool CrawlAnimationFinished()
    {
        if (animationPhase == "CrawlOutro") { EndCrawl(false); return false; }
        if (!IsCrawlPhase(animationPhase) || crawl == null) return false;
        if (crawl.Finished) PlayCrawlOutro(); else PlayCrawlClip(false);
        return true;
    }

    // AnimationBoundary hook for a crawl interrupted by a click or an external action.
    private bool ContinueCrawlAtBoundary()
    {
        if (crawl == null) return false;
        if (crawl.Finished) { EndCrawl(false); return false; }
        if (exitAnimation || dragging) return false;
        PlayCrawlClip(true);
        return true;
    }

    // AnimationBoundary hook: patrol always crawls, other modes sometimes replace an event.
    private bool TryStartCrawlAtBoundary(bool eventDue)
    {
        if (crawl != null || !CanCrawl || CrawlBlocked || DateTime.UtcNow < crawlResumeAt) return false;
        if (CrawlPatrolActive) return StartCrawl();
        return eventDue && companionRandom.NextDouble() < CrawlEventChances[CrawlMode] && StartCrawl();
    }
}
