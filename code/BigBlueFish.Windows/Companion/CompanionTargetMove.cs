using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace BigBlueFish.Windows;

// Click her once, then click anywhere on the screen: she runs there (预备姿势奔跑) and waves when she
// arrives (原地挥手打招呼). While choosing, a faint layer over each monitor takes that one click so it
// does not reach the window underneath; a right click or 5 seconds without a click cancel.
// The lively activity level also uses the run for short wanders.
public partial class MainWindow
{
    private sealed class TargetMoveState
    {
        public Point From, To;
        public double Lead, End;
        public bool Mirror, Wander, Arrived;
    }
    private TargetMoveState? targetMove;
    private List<Window>? targetOverlays;
    private DateTime targetSelectUntil;
    private Rectangle? selectionRing;
    private bool targetHintShown;

    // ClickAnimation hook: a plain click arms the choice of a destination.
    private void BeginTargetSelection()
    {
        if (!preferences.ClickToMove || targetOverlays != null || PuppetActive || !companionReady || exitAnimation || hiddenByUser
            || edgeHide != null || Crawling || mimicActive || FocusActive || GameActive || chatWindow != null) return;
        targetSelectUntil = DateTime.UtcNow.AddSeconds(5);
        targetOverlays = new List<Window>();
        foreach (var screen in Forms.Screen.AllScreens) targetOverlays.Add(CreateTargetOverlay(screen.Bounds));
        // She stays above the layer, so clicking, dragging or double-clicking her still reaches her.
        if (!Topmost) Topmost = true;
        RaiseAboveOverlays();
        ShowSelectionRing(true);
        if (!targetHintShown)
        {
            targetHintShown = true;
            ShowCompanionNotice("点一下屏幕上的任意位置，鱼鱼就跑过去～（右键取消）");
        }
    }

    private Window CreateTargetOverlay(System.Drawing.Rectangle bounds)
    {
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(230, 255, 251, 244)), CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(global::BigBlueFish.Windows.CompanionBubble.HairBlue), BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 3, 8, 3), IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            Child = new TextBlock { Text = "点这里，鱼鱼跑过来 · 右键取消", FontSize = 12,
                Foreground = new SolidColorBrush(global::BigBlueFish.Windows.CompanionBubble.HairBlue) }
        };
        var canvas = new Canvas { Background = new SolidColorBrush(Color.FromArgb(14, 80, 96, 152)) };
        canvas.Children.Add(hint);
        // Never activated (like the pet herself), so the keyboard focus stays in the owner's window.
        var overlay = new Window
        {
            Title = "蓝色大肥鱼 · 选一个位置", WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, ShowActivated = false, Cursor = Cursors.Cross,
            WindowStartupLocation = WindowStartupLocation.Manual, Content = canvas
        };
        void Cover(IntPtr handle) => CompanionNative.SetWindowPos(handle, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010 | 0x0040);
        overlay.SourceInitialized += (_, _) =>
        {
            // Physical pixels, so the layer covers its monitor exactly whatever that monitor's scaling.
            var handle = new WindowInteropHelper(overlay).Handle;
            CompanionNative.SetStyle(handle, -20, new IntPtr(CompanionNative.GetStyle(handle, -20).ToInt64() | 0x08000000L | 0x80L));
            HwndSource.FromHwnd(handle)?.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == 0x21) { handled = true; return new IntPtr(3); } // WM_MOUSEACTIVATE → MA_NOACTIVATE
                if (msg == 0x02E0) { handled = true; Cover(hwnd); }      // WM_DPICHANGED: keep the monitor's own rectangle
                return IntPtr.Zero;
            });
            Cover(handle);
        };
        // She and her bubble stay above the layer, so clicking, dragging or double-clicking her still reaches her.
        overlay.Loaded += (_, _) => RaiseAboveOverlays();
        overlay.MouseMove += (_, e) =>
        {
            var p = e.GetPosition(canvas);
            Canvas.SetLeft(hint, p.X + 18);
            Canvas.SetTop(hint, p.Y + 20);
            hint.Visibility = Visibility.Visible;
        };
        overlay.MouseLeave += (_, _) => hint.Visibility = Visibility.Collapsed;
        // Act on the button's release (and keep the press captured), so the release does not reach the window below.
        overlay.MouseLeftButtonDown += (_, e) => { e.Handled = true; overlay.CaptureMouse(); };
        overlay.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!overlay.IsMouseCaptured) return;
            overlay.ReleaseMouseCapture();
            CompanionNative.GetCursorPos(out var cursor);
            EndTargetSelection();
            ChooseTarget(cursor);
        };
        overlay.MouseRightButtonDown += (_, e) => { e.Handled = true; overlay.CaptureMouse(); };
        overlay.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            overlay.ReleaseMouseCapture();
            EndTargetSelection();
        };
        overlay.Show();
        return overlay;
    }

    private void RaiseAboveOverlays()
    {
        if (targetOverlays == null) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) CompanionNative.SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
    }

    private void EndTargetSelection()
    {
        if (targetOverlays == null) return;
        var overlays = targetOverlays;
        targetOverlays = null;
        foreach (var overlay in overlays) overlay.Close();
        ShowSelectionRing(false);
        ApplyTopmost();
    }

    private void TargetSelectionTick(DateTime now)
    {
        if (targetOverlays != null && (now > targetSelectUntil || dragging || hiddenByUser || !IsVisible)) EndTargetSelection();
    }

    // A dashed ring around her while she waits for the destination.
    private void ShowSelectionRing(bool show)
    {
        if (show && selectionRing == null)
        {
            var body = UprightVisibleBounds;
            selectionRing = new Rectangle
            {
                Width = body.Width + 16, Height = body.Height + 12, RadiusX = 22, RadiusY = 22, IsHitTestVisible = false,
                Stroke = new SolidColorBrush(Color.FromArgb(170, 80, 96, 152)), StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 },
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(body.X - 8, body.Y - 6, 0, 0)
            };
            companionLayout.Children.Add(selectionRing);
        }
        else if (!show && selectionRing != null)
        {
            companionLayout.Children.Remove(selectionRing);
            selectionRing = null;
        }
    }

    // The clicked point (physical pixels) becomes the destination of her body centre.
    private void ChooseTarget(CompanionNative.Point cursor)
    {
        if (!companionReady || exitAnimation || PuppetActive || PresentationSource.FromVisual(this) == null) return;
        var p = PointFromScreen(new Point(cursor.X, cursor.Y));
        var body = UprightVisibleBounds;
        double dx = p.X - (body.X + body.Width / 2), dy = p.Y - (body.Y + body.Height / 2);
        if (Math.Sqrt(dx * dx + dy * dy) < 24) return; // clicked on herself
        // She stays on her own monitor (a run across monitors with another scaling would jump); a click on
        // another monitor sends her to the nearest point of hers.
        StartTargetRun(new Point(Left + dx, Top + dy), false);
    }

    // Runs to the window position `to`, kept inside `area` (default: her monitor's work area).
    // Returns false when there is nowhere to go.
    private bool StartTargetRun(Point to, bool wander, Rect? area = null)
    {
        if (!companionReady || exitAnimation || PuppetActive || dragging) return false;
        var limits = area ?? CompanionEdgeLayout.WorkArea(this);
        var body = UprightVisibleBounds;
        to = new Point(Math.Clamp(to.X, limits.Left - body.Left, Math.Max(limits.Left - body.Left, limits.Right - body.Right)),
            Math.Clamp(to.Y, limits.Top - body.Top, Math.Max(limits.Top - body.Top, limits.Bottom - body.Bottom)));
        var from = new Point(Left, Top);
        if ((to - from).Length < 12) return false;
        if (Crawling) StopCrawlNow(false);
        CancelMovement();
        var rule = ClipOr("预备姿势奔跑", "螃蟹走路");
        if (rule == null) return false;
        // Where the clip runs: the dsh-whale-pet run from 1.5 s to 7.8 s; the crab walk 2–8 s; an unknown pack clip throughout.
        var (lead, end) = IsForeignClip(rule) ? (.6, 6.0) : rule.Id == "预备姿势奔跑" ? (1.5, 7.8) : (2.0, 8.0);
        bool mirror = to.X > from.X; // the clip runs to the left
        targetMove = new TargetMoveState { From = from, To = to, Lead = lead, End = end, Mirror = mirror, Wander = wander };
        PlayAnimation(rule, "TargetRun", mirror);
        return true;
    }

    // FramePresented: move along with the run.
    private void TargetRunFrame(double seconds)
    {
        var t = targetMove;
        if (t == null || t.Arrived || animationPhase != "TargetRun") return;
        double fraction = Math.Clamp((seconds - t.Lead) / (t.End - t.Lead), 0, 1);
        double eased = fraction * fraction * (3 - 2 * fraction);
        Left = t.From.X + (t.To.X - t.From.X) * eased;
        Top = t.From.Y + (t.To.Y - t.From.Y) * eased;
        if (fraction >= 1) ArriveTarget();
    }

    private void ArriveTarget()
    {
        var t = targetMove;
        if (t == null || t.Arrived) return;
        t.Arrived = true;
        Left = t.To.X; Top = t.To.Y;
        anchor = new Point(Left, Top);
        SaveCompanion();
    }

    // Dragged, clicked or told to do something else while running: she stops where she is.
    private void StopTargetRun()
    {
        if (targetMove == null) return;
        targetMove = null;
        anchor = new Point(Left, Top);
        SaveCompanion();
    }

    // AnimationFinished for the run and the wave.
    private bool TargetAnimationFinished()
    {
        if (animationPhase == "TargetRun")
        {
            var t = targetMove;
            if (t != null) { ArriveTarget(); targetMove = null; }
            if (t is { Wander: false } && ClipOr("原地挥手打招呼", "点击回应-元气挥手") is { } wave) { PlayAnimation(wave, "Arrive"); return true; }
            AnimationBoundary();
            return true;
        }
        if (animationPhase == "Arrive") { AnimationBoundary(); return true; }
        return false;
    }

    // 活泼: now and then she runs a little way along the screen on her own.
    private bool ActivityWander()
    {
        if (preferences.ActivityLevel != 2 || companionRandom.NextDouble() > .3 || edgeHide != null || mimicActive || FocusActive) return false;
        double distance = (60 + companionRandom.NextDouble() * 180) * preferences.Scale;
        double direction = companionRandom.NextDouble() < .5 ? -1 : 1;
        var area = CompanionEdgeLayout.WorkArea(this);
        var body = UprightVisibleBounds;
        double centre = Left + body.X + body.Width / 2;
        if (centre + direction * distance < area.Left + body.Width / 2 + 20 || centre + direction * distance > area.Right - body.Width / 2 - 20)
            direction = -direction;
        return StartTargetRun(new Point(Left + direction * distance, Top), true);
    }
}
