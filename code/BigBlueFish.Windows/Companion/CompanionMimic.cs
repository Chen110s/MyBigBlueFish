using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace BigBlueFish.Windows;

// Mimics the owner in Delta Force: she raises her water gun and fires with every shot, shows a
// crosshair while aiming, flinches when hit, and bandages / injects / takes a pill when the owner
// heals. Input is only observed (GetAsyncKeyState, nothing is injected and no game memory is read);
// shots and aiming count only while the mouse pointer is hidden, i.e. during actual play.
public partial class MainWindow
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size, Flags;
        public IntPtr Cursor;
        public CompanionNative.Point Position;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    // Keys offered in settings (virtual-key codes).
    internal static readonly (string Name, int Key)[] MimicKeyChoices =
    [
        ("无", 0), ("1", 0x31), ("2", 0x32), ("3", 0x33), ("4", 0x34), ("5", 0x35), ("6", 0x36), ("7", 0x37), ("8", 0x38),
        ("9", 0x39), ("0", 0x30), ("Q", 0x51), ("E", 0x45), ("R", 0x52), ("T", 0x54), ("F", 0x46), ("G", 0x47), ("H", 0x48),
        ("Z", 0x5A), ("X", 0x58), ("C", 0x43), ("V", 0x56), ("B", 0x42), ("Tab", 0x09), ("鼠标侧键 1", 0x05), ("鼠标侧键 2", 0x06)
    ];

    private const string GunClip = "玩水枪";
    // In the water-gun clip she holds the gun out to the left from 1.9 s and sprays from about 3.0 s.
    private const double AimClipStart = 1.9, AimClipEnd = 2.85, FireClipStart = 2.85, FireClipEnd = 3.9;
    private static readonly Point GunMuzzle = new(216, 218);               // in the 640×360 animation frame
    // Keys that end a toggled aim in Delta Force (sprint, crouch, jump, reload, weapon swap, interact).
    private static readonly int[] AimBreakKeys = [0x10, 0x11, 0x20, 0x52, 0x31, 0x32, 0x33, 0x46];
    private static readonly Point HealMouth = new(312, 212), HealHead = new(312, 48), HealBody = new(310, 250), HealArm = new(392, 246);

    private DispatcherTimer? mimicTimer;
    private Viewbox? mimicView;
    private Canvas? mimicLayer;
    private IntPtr mimicGame;
    private bool mimicActive, rmbWasDown, aimToggled, aimToggleBefore, mimicAiming, redSampling, redLatched, gunFiringSegment;
    private double crosshairDirection, lastRedShare = -1;
    private DateTime rmbDownAt, mimicNextShot, mimicLastGunUse = DateTime.MinValue, mimicLastShot = DateTime.MinValue, mimicNextRedSample, mimicHurtCooldown;
    private DateTime mimicScaredCooldown, mimicQuipCooldown;
    private readonly Dictionary<int, bool> mimicKeysDown = new();
    private FrameworkElement? mimicCrosshair, mimicHealProp;
    private int mimicHealVersion;
    private double redBaseline = -1;

    private bool MimicReady => mimicActive && companionReady && !companionClosed && !exitAnimation && !PuppetActive && !dragging && IsVisible;

    // Called from UpdateGame every two seconds: run the fast input loop only while Delta Force is in front.
    private void UpdateMimic(IntPtr hwnd)
    {
        bool active = preferences.GameMimic && preferences.GameCompanion && deltaForeground && !PuppetActive;
        if (active)
        {
            mimicGame = hwnd;
            if (!mimicActive)
            {
                mimicActive = true;
                mimicTimer ??= CreateMimicTimer();
                mimicTimer.Start();
            }
        }
        else if (mimicActive) StopMimic();
    }

    private DispatcherTimer CreateMimicTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += MimicTick;
        return timer;
    }

    private void StopMimic()
    {
        mimicActive = false;
        mimicTimer?.Stop();
        ResetMimicInput();
        redBaseline = lastRedShare = -1;
        redLatched = false;
    }

    private void ResetMimicInput()
    {
        aimToggled = rmbWasDown = false;
        mimicKeysDown.Clear();
        mimicNextShot = DateTime.MinValue;
        SetMimicAim(false);
    }

    private static bool IsKeyHeld(int key) => key != 0 && (GetAsyncKeyState(key) & 0x8000) != 0;

    private static bool PointerHidden()
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        // Games hide the pointer either with ShowCursor (flag cleared) or by setting no cursor at all.
        return GetCursorInfo(ref info) && ((info.Flags & 1) == 0 || info.Cursor == IntPtr.Zero);
    }

    private void MimicTick(object? sender, EventArgs e)
    {
        if (!MimicReady) return;
        if (CompanionNative.GetForegroundWindow() != mimicGame) { ResetMimicInput(); return; } // alt-tabbed away
        var now = DateTime.UtcNow;
        bool playing = PointerHidden();

        // Aim: hold the right button, or tap it to toggle (Delta Force offers both). A toggled aim ends with the
        // next tap, with keys that drop the sights in game, or after a while without shooting.
        bool rmb = IsKeyHeld(0x02);
        if (rmb && !rmbWasDown) { rmbDownAt = now; aimToggleBefore = aimToggled; }
        if (!rmb && rmbWasDown) aimToggled = playing && (now - rmbDownAt).TotalMilliseconds < 250 && !aimToggleBefore;
        rmbWasDown = rmb;
        if (!playing || AimBreakKeys.Any(IsKeyHeld) || aimToggled && (now - mimicLastShot).TotalSeconds > 8 && (now - rmbDownAt).TotalSeconds > 8)
            aimToggled = false;
        bool aim = playing && (rmb || aimToggled);

        // Fire: one shot right away, then about nine a second while the button is held.
        bool fire = playing && IsKeyHeld(0x01);
        if (fire || aim)
        {
            mimicLastGunUse = now;
            EnsureGunClip(fire);
        }
        SetMimicAim(aim);
        if (fire && now >= mimicNextShot)
        {
            mimicNextShot = now.AddMilliseconds(110);
            mimicLastShot = now;
            MimicShot();
        }
        else if (!fire) mimicNextShot = DateTime.MinValue;

        // Heal keys act when released (the heal key opens a wheel while it is held); only presses made in play count.
        CheckHealKey(preferences.HealKey, DeltaForceHeal.None, playing);
        CheckHealKey(preferences.BandageKey, DeltaForceHeal.Bandage, playing);
        CheckHealKey(preferences.InjectKey, DeltaForceHeal.Injection, playing);
        CheckHealKey(preferences.PillKey, DeltaForceHeal.Pill, playing);

        if (preferences.MimicHurt && playing && now >= mimicNextRedSample && !redSampling)
        {
            mimicNextRedSample = now.AddMilliseconds(350);
            _ = SampleHurt(mimicGame);
        }
    }

    private void CheckHealKey(int key, DeltaForceHeal kind, bool playing)
    {
        if (key == 0) return;
        bool down = IsKeyHeld(key);
        mimicKeysDown.TryGetValue(key, out var was);
        if (down && !was && !playing) return; // typed in chat or pressed in a menu: not a heal
        mimicKeysDown[key] = down;
        if (!was || down) return;
        StartMimicHeal(kind);
#if COMPANION_OCR
        if (kind == DeltaForceHeal.None) _ = IdentifyHeal(mimicHealVersion, mimicGame);
#endif
    }

    // ---------------------------------------------------------------- layer
    private Canvas? MimicLayer()
    {
        if (mimicLayer != null) return mimicLayer;
        if (videoImage == null) return null;
        mimicLayer = new Canvas { Width = CompanionVideoPlayer.VideoWidth, Height = CompanionVideoPlayer.VideoHeight, ClipToBounds = false };
        mimicView = new Viewbox { Stretch = Stretch.Fill, Child = mimicLayer, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
        mimicView.SetBinding(WidthProperty, new Binding(nameof(Width)) { Source = videoImage });
        mimicView.SetBinding(HeightProperty, new Binding(nameof(Height)) { Source = videoImage });
        companionLayout.Children.Add(mimicView);
        return mimicLayer;
    }

    private bool GunMirrored => currentAction == GunClip && videoImage?.RenderTransform is ScaleTransform { ScaleX: < 0 };

    private Point MuzzlePoint(out double direction)
    {
        direction = GunMirrored ? 1 : -1;
        return GunMirrored ? new Point(CompanionVideoPlayer.VideoWidth - GunMuzzle.X, GunMuzzle.Y) : GunMuzzle;
    }

    // Adds an element to the layer at (x, y) and removes it after the given time.
    private T Place<T>(T element, double x, double y, double seconds) where T : FrameworkElement
    {
        var layer = MimicLayer();
        if (layer == null) return element;
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        layer.Children.Add(element);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => { timer.Stop(); layer.Children.Remove(element); };
        timer.Start();
        return element;
    }

    private static void Animate(UIElement target, DependencyProperty property, double from, double to, double seconds, double delay = 0)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds)) { BeginTime = TimeSpan.FromSeconds(delay) };
        if (delay > 0) target.SetValue(property, from);
        target.BeginAnimation(property, animation);
    }

    // Opacity 0 → 1 → 0: visible from `start` for `inSeconds`, fading out from `outStart` over `outSeconds`.
    private static void FadeInOut(UIElement target, double start, double inSeconds, double outStart, double outSeconds)
    {
        var frames = new DoubleAnimationUsingKeyFrames();
        frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(start))));
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(start + inSeconds))));
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(outStart))));
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(outStart + outSeconds))));
        target.BeginAnimation(UIElement.OpacityProperty, frames);
    }

    private static TransformGroup Movable(FrameworkElement element, out ScaleTransform scale, out RotateTransform rotate, out TranslateTransform move)
    {
        scale = new ScaleTransform();
        rotate = new RotateTransform();
        move = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(rotate);
        group.Children.Add(move);
        element.RenderTransformOrigin = new Point(.5, .5);
        element.RenderTransform = group;
        return group;
    }

    // Recoil and flinches move the sprite and the layer together for a moment.
    private void Jolt(double dx, double dy, double seconds)
    {
        if (videoImage == null) return;
        var shifted = new Thickness(dx, dy, -dx, -dy);
        foreach (var element in new FrameworkElement?[] { videoImage, mimicView })
            element?.BeginAnimation(MarginProperty, new ThicknessAnimation(shifted, new Thickness(0), TimeSpan.FromSeconds(seconds)) { FillBehavior = FillBehavior.Stop });
    }

    private void Shake(double strength, double seconds)
    {
        if (videoImage == null) return;
        var frames = new ThicknessAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(seconds), FillBehavior = FillBehavior.Stop };
        for (int i = 0; i < 6; i++)
        {
            double dx = (i % 2 == 0 ? 1 : -1) * strength * (1 - i / 6.0);
            frames.KeyFrames.Add(new LinearThicknessKeyFrame(new Thickness(dx, 0, -dx, 0), KeyTime.FromPercent((i + 1) / 7.0)));
        }
        frames.KeyFrames.Add(new LinearThicknessKeyFrame(new Thickness(0), KeyTime.FromPercent(1)));
        videoImage.BeginAnimation(MarginProperty, frames);
        mimicView?.BeginAnimation(MarginProperty, frames.Clone());
    }

    // ---------------------------------------------------------------- shooting & aiming
    // She keeps the water-gun pose while the owner shoots or aims; reactions that are already playing finish first.
    private void EnsureGunClip(bool firing)
    {
        if (currentAction == GunClip && animationPhase == "Mimic" && (gunFiringSegment || !firing)) return;
        if (animationPhase is not ("Idle" or "Event" or "Daily" or "Mimic" or "MimicHeal")) return;
        if (TryFindRule(GunClip) is not { } rule) return;
        CancelMovement();
        // Point the gun towards the middle of the screen.
        var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        double centre = (Left + Width / 2) * dpi.DpiScaleX;
        bool mirror = currentAction == GunClip && animationPhase == "Mimic" ? GunMirrored : centre < screen.Left + screen.Width / 2.0;
        PlayGunSegment(rule, mirror, firing);
    }

    private void PlayGunSegment(CompanionAnimationRule rule, bool mirror, bool firing)
    {
        gunFiringSegment = firing;
        PlayAnimation(rule, "Mimic", mirror, firing ? FireClipStart : AimClipStart, firing ? FireClipEnd : AimClipEnd);
    }

    // AnimationFinished: keep the pose while the owner is still shooting or aiming.
    // In between she stands idle rather than starting a random event, so she is ready for the next shot.
    private bool MimicAnimationFinished()
    {
        if (animationPhase is not ("Mimic" or "MimicHeal")) return false;
        var now = DateTime.UtcNow;
        if (mimicActive && animationPhase == "Mimic" && (now - mimicLastGunUse).TotalSeconds <= .6 && TryFindRule(GunClip) is { } rule)
        {
            PlayGunSegment(rule, GunMirrored, (now - mimicLastShot).TotalSeconds <= .5);
            return true;
        }
        if (!mimicActive) return false;
        PlayIdle();
        return true;
    }

    private void MimicShot()
    {
        // The flash is placed for the original water gun; with an appearance pack she only recoils.
        if (currentAction != GunClip || animationPhase != "Mimic" || videoPlayer?.Frames == 0 || IsForeignClip(animationPolicy.Find(GunClip)))
        {
            Jolt(0, 2, .08);
            return;
        }
        var muzzle = MuzzlePoint(out double direction);
        // Muzzle flash: a bright star that pops and fades in under a tenth of a second.
        var glow = new RadialGradientBrush();
        glow.GradientStops.Add(new GradientStop(Color.FromRgb(255, 255, 235), 0));
        glow.GradientStops.Add(new GradientStop(Color.FromRgb(255, 214, 64), .45));
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 120, 0), 1));
        var flash = new Polygon { Fill = glow };
        for (int i = 0; i < 16; i++)
        {
            double angle = i * Math.PI / 8 + (companionRandom.NextDouble() - .5) * .2;
            double radius = i % 2 == 0 ? 20 + companionRandom.NextDouble() * 8 : 7;
            flash.Points.Add(new Point(26 + Math.Cos(angle) * radius * (Math.Cos(angle) * direction > 0 ? 1.6 : 1), 26 + Math.Sin(angle) * radius));
        }
        Movable(flash, out var scale, out _, out _);
        Place(flash, muzzle.X - 26 + direction * 18, muzzle.Y - 26, .12);
        Animate(flash, OpacityProperty, 1, 0, .1);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.6, 1.25, TimeSpan.FromSeconds(.09)));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.6, 1.25, TimeSpan.FromSeconds(.09)));
        // A spent casing jumps out now and then.
        if (companionRandom.NextDouble() < .35)
        {
            var casing = new Rectangle { Width = 7, Height = 3, RadiusX = 1, RadiusY = 1, Fill = new SolidColorBrush(Color.FromRgb(214, 168, 60)) };
            Movable(casing, out _, out var spin, out var fly);
            Place(casing, muzzle.X - direction * 40, muzzle.Y - 4, .5);
            fly.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -direction * (20 + companionRandom.NextDouble() * 15), TimeSpan.FromSeconds(.45)));
            fly.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 26, TimeSpan.FromSeconds(.45)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseIn, Amplitude = 1.2 } });
            spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 540, TimeSpan.FromSeconds(.45)));
            Animate(casing, OpacityProperty, 1, 0, .2, .3);
        }
        Jolt(-direction * 5, -1.5, .09); // recoil pushes her back a little
    }

    private void SetMimicAim(bool aim)
    {
        if (aim && mimicAiming && mimicCrosshair != null)
        {
            // Follow the gun when the pose switches sides.
            var muzzle = MuzzlePoint(out double direction);
            if (direction != crosshairDirection)
            {
                crosshairDirection = direction;
                Canvas.SetLeft(mimicCrosshair, muzzle.X + direction * 95 - 30);
            }
            return;
        }
        if (aim == mimicAiming) return;
        mimicAiming = aim;
        if (aim)
        {
            var muzzle = MuzzlePoint(out double direction);
            var crosshair = new Canvas { Width = 60, Height = 60 };
            var red = new SolidColorBrush(Color.FromRgb(255, 64, 64));
            var white = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
            crosshair.Children.Add(new Ellipse { Width = 46, Height = 46, Stroke = white, StrokeThickness = 5, Margin = new Thickness(7) });
            crosshair.Children.Add(new Ellipse { Width = 46, Height = 46, Stroke = red, StrokeThickness = 2.5, Margin = new Thickness(7) });
            foreach (var (x1, y1, x2, y2) in new[] { (30.0, 0.0, 30.0, 16.0), (30.0, 44.0, 30.0, 60.0), (0.0, 30.0, 16.0, 30.0), (44.0, 30.0, 60.0, 30.0) })
                crosshair.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = red, StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            var dot = new Ellipse { Width = 6, Height = 6, Fill = red };
            Canvas.SetLeft(dot, 27);
            Canvas.SetTop(dot, 27);
            crosshair.Children.Add(dot);
            Movable(crosshair, out var scale, out var rotate, out _);
            var layer = MimicLayer();
            if (layer == null) return;
            crosshairDirection = direction;
            Canvas.SetLeft(crosshair, muzzle.X + direction * 95 - 30);
            Canvas.SetTop(crosshair, muzzle.Y - 38);
            layer.Children.Add(crosshair);
            mimicCrosshair = crosshair;
            Animate(crosshair, OpacityProperty, 0, .95, .15);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.9, 1, TimeSpan.FromSeconds(.18)) { EasingFunction = new CubicEase() });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.9, 1, TimeSpan.FromSeconds(.18)) { EasingFunction = new CubicEase() });
            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-45, 0, TimeSpan.FromSeconds(.18)));
        }
        else if (mimicCrosshair is { } old)
        {
            mimicCrosshair = null;
            var fade = new DoubleAnimation(old.Opacity, 0, TimeSpan.FromSeconds(.12));
            fade.Completed += (_, _) => mimicLayer?.Children.Remove(old);
            old.BeginAnimation(OpacityProperty, fade);
        }
    }

    // ---------------------------------------------------------------- getting hurt
    private async Task SampleHurt(IntPtr game)
    {
        redSampling = true;
        try
        {
            double share = await Task.Run(() => CompanionScreenCapture.RedShare(game));
            if (share < 0 || !mimicActive) return;
            var now = DateTime.UtcNow;
            double previous = lastRedShare;
            lastRedShare = share;
            if (redBaseline < 0) { redBaseline = share; return; }
            // A hit is a sudden rise; a screen that stays red (low health, a red-lit room) counts once,
            // and only after it has calmed down again can the next hit count.
            if (redLatched)
            {
                if (share <= redBaseline + .015) redLatched = false;
                return;
            }
            bool hit = share >= .04 && share - redBaseline >= .03 && share - previous >= .02;
            if (!hit) { redBaseline += (share - redBaseline) * .1; return; }
            redLatched = true;
            if (now < mimicHurtCooldown) return;
            mimicHurtCooldown = now.AddMilliseconds(800);
            if (preferences.DeltaForceLog) LogMimic($"受伤识别：画面红色比例 {redBaseline:P1} → {share:P1}");
            MimicHurt(false);
        }
        catch { } // the screen could not be read this time
        finally { redSampling = false; }
    }

    // A red flash over her body, a shake, and (now and then) a startled jump.
    private void MimicHurt(bool severe)
    {
        if (!MimicReady) return;
        var layer = MimicLayer();
        if (layer == null || videoImage?.Source is not ImageSource frame) return;
        var now = DateTime.UtcNow;
        bool busyWithGun = (now - mimicLastGunUse).TotalSeconds < 1;
        if ((severe || !busyWithGun) && now >= mimicScaredCooldown && TryFindRule("被吓一跳") is { } scared && !dragging)
        {
            mimicScaredCooldown = now.AddSeconds(10);
            CancelMovement();
            PlayAnimation(scared, "MimicHeal"); // first, so the red silhouette below matches her new pose
        }
        var mask = new ImageBrush(frame) { Stretch = Stretch.Fill };
        if (videoImage.RenderTransform is ScaleTransform { ScaleX: < 0 })
            mask.RelativeTransform = new ScaleTransform(-1, 1, .5, .5);
        var tint = new Rectangle { Width = layer.Width, Height = layer.Height, Fill = new SolidColorBrush(Color.FromRgb(255, 30, 30)), OpacityMask = mask };
        Place(tint, 0, 0, .6);
        Animate(tint, OpacityProperty, severe ? .85 : .65, 0, .5);
        // Little red strike marks beside her head.
        var marks = new Canvas { Width = 50, Height = 50 };
        foreach (var angle in new[] { -60.0, -20.0, 20.0 })
        {
            double radians = angle * Math.PI / 180;
            marks.Children.Add(new Line { X1 = 25 + Math.Cos(radians) * 8, Y1 = 25 + Math.Sin(radians) * 8, X2 = 25 + Math.Cos(radians) * 22, Y2 = 25 + Math.Sin(radians) * 22,
                Stroke = Brushes.Red, StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        }
        Movable(marks, out var scale, out _, out _);
        Place(marks, 390, 60, .6);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.5, 1.2, TimeSpan.FromSeconds(.15)));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.5, 1.2, TimeSpan.FromSeconds(.15)));
        Animate(marks, OpacityProperty, 1, 0, .3, .3);
        Shake(severe ? 9 : 6, .3);
        if (!severe) MimicQuip(["哎呀！{0}小心子弹！", "疼疼疼……快找掩体！", "被打了！反击反击！"], .25);
    }

    // ---------------------------------------------------------------- healing
    private void StartMimicHeal(DeltaForceHeal kind)
    {
        if (!MimicReady) return;
        int version = ++mimicHealVersion;
        if (mimicHealProp is { } old) { mimicHealProp = null; mimicLayer?.Children.Remove(old); }
        SetMimicAim(false);
        aimToggled = false;
        switch (kind)
        {
            case DeltaForceHeal.Bandage: MimicBandage(); MimicQuip(["缠好绷带，不疼不疼～", "包扎一下，马上又是一条好汉！"], .3); break;
            case DeltaForceHeal.Injection: MimicInjection(); MimicQuip(["打一针，满血复活！", "肾上腺素来一针！冲！"], .3); break;
            case DeltaForceHeal.Pill: MimicPill(); MimicQuip(["咕嘟～药效上来了！", "吃颗药，稳住稳住～"], .3); break;
            default: MimicMedkit(); break;
        }
        HealSparkles(kind == DeltaForceHeal.Injection ? 1.0 : kind == DeltaForceHeal.Pill ? .9 : kind == DeltaForceHeal.Bandage ? 2.4 : .5, version);
    }

#if COMPANION_OCR
    // The heal key opens a wheel; which item was used shows as text near the middle of the screen.
    private async Task IdentifyHeal(int version, IntPtr game)
    {
        foreach (var delay in new[] { 450, 1000 })
        {
            await Task.Delay(delay);
            if (version != mimicHealVersion || !mimicActive) return;
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var snapshot = await Task.Run(async () =>
                {
                    var image = CompanionScreenCapture.CaptureGame(game, 1600) ?? throw new System.IO.IOException("没有截到游戏画面");
                    return await CompanionOcr.RecognizeAsync(image, "", 0, limit.Token).ConfigureAwait(false);
                }, limit.Token);
                if (version != mimicHealVersion || !mimicActive) return;
                var kind = DeltaForceScreen.HealItem(snapshot.Words, snapshot.Width, snapshot.Height);
                if (kind is DeltaForceHeal.Bandage or DeltaForceHeal.Injection or DeltaForceHeal.Pill)
                {
                    StartMimicHeal(kind);
                    return;
                }
                if (kind == DeltaForceHeal.Medkit) return; // the generic first-aid look already fits
            }
            catch { return; }
        }
    }
#endif

    private void MimicMedkit()
    {
        var box = new Canvas { Width = 40, Height = 32 };
        box.Children.Add(new Rectangle { Width = 40, Height = 32, RadiusX = 6, RadiusY = 6, Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromRgb(200, 40, 40)), StrokeThickness = 2.5 });
        var cross1 = new Rectangle { Width = 6, Height = 18, Fill = new SolidColorBrush(Color.FromRgb(220, 40, 40)) };
        var cross2 = new Rectangle { Width = 18, Height = 6, Fill = new SolidColorBrush(Color.FromRgb(220, 40, 40)) };
        Canvas.SetLeft(cross1, 17); Canvas.SetTop(cross1, 7);
        Canvas.SetLeft(cross2, 11); Canvas.SetTop(cross2, 13);
        box.Children.Add(cross1);
        box.Children.Add(cross2);
        Movable(box, out var scale, out _, out var move);
        mimicHealProp = Place(box, HealHead.X - 20, HealHead.Y - 34, 2.4);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(.25)) { EasingFunction = new BackEase { Amplitude = .6 } });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(.25)) { EasingFunction = new BackEase { Amplitude = .6 } });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -6, TimeSpan.FromSeconds(.5)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(2) });
        Animate(box, OpacityProperty, 1, 0, .3, 2.0);
    }

    // Three white wraps around her middle, one after another.
    private void MimicBandage()
    {
        var group = new Canvas { Width = 180, Height = 90 };
        for (int i = 0; i < 3; i++)
        {
            var wrap = new Ellipse { Width = 150, Height = 24, Stroke = Brushes.White, StrokeThickness = 9,
                StrokeDashArray = new DoubleCollection { 3, .6 } };
            var shade = new Ellipse { Width = 150, Height = 24, Stroke = new SolidColorBrush(Color.FromArgb(120, 200, 200, 210)), StrokeThickness = 2 };
            Canvas.SetLeft(wrap, 15); Canvas.SetTop(wrap, 12 + i * 22);
            Canvas.SetLeft(shade, 15); Canvas.SetTop(shade, 16 + i * 22);
            group.Children.Add(shade);
            group.Children.Add(wrap);
            foreach (var part in new UIElement[] { wrap, shade })
            {
                part.RenderTransformOrigin = new Point(.5, .5);
                var scale = new ScaleTransform(0, 1);
                part.RenderTransform = scale;
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(.45)) { BeginTime = TimeSpan.FromSeconds(i * .6), EasingFunction = new QuadraticEase() });
            }
        }
        // The roll of bandage circling round.
        var roll = new Ellipse { Width = 16, Height = 16, Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromRgb(190, 190, 200)), StrokeThickness = 2 };
        Movable(roll, out _, out _, out var move);
        Canvas.SetTop(roll, 30);
        group.Children.Add(roll);
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(10, 160, TimeSpan.FromSeconds(.6)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(1.5) });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 44, TimeSpan.FromSeconds(1.8)));
        Animate(roll, OpacityProperty, 1, 0, .2, 1.8);
        mimicHealProp = Place(group, HealBody.X - 90, HealBody.Y - 45, 3.4);
        Animate(group, OpacityProperty, 1, 0, .4, 2.9);
    }

    // A syringe slides in to her arm, the plunger goes down, then it pulls away.
    private void MimicInjection()
    {
        var syringe = new Canvas { Width = 92, Height = 26 };
        var barrel = new Rectangle { Width = 46, Height = 16, RadiusX = 3, RadiusY = 3, Fill = new SolidColorBrush(Color.FromArgb(200, 235, 245, 255)), Stroke = new SolidColorBrush(Color.FromRgb(120, 140, 170)), StrokeThickness = 2 };
        var liquid = new Rectangle { Width = 38, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(70, 210, 120)) };
        var plunger = new Rectangle { Width = 26, Height = 5, Fill = new SolidColorBrush(Color.FromRgb(120, 140, 170)) };
        var cap = new Rectangle { Width = 5, Height = 20, RadiusX = 2, RadiusY = 2, Fill = new SolidColorBrush(Color.FromRgb(120, 140, 170)) };
        var needle = new Line { X1 = 0, Y1 = 13, X2 = 18, Y2 = 13, Stroke = new SolidColorBrush(Color.FromRgb(150, 160, 175)), StrokeThickness = 2 };
        Canvas.SetLeft(barrel, 18); Canvas.SetTop(barrel, 5);
        Canvas.SetLeft(liquid, 22); Canvas.SetTop(liquid, 8);
        Canvas.SetLeft(plunger, 64); Canvas.SetTop(plunger, 10.5);
        Canvas.SetLeft(cap, 88); Canvas.SetTop(cap, 3);
        syringe.Children.Add(needle);
        syringe.Children.Add(barrel);
        syringe.Children.Add(liquid);
        syringe.Children.Add(plunger);
        syringe.Children.Add(cap);
        var plungerMove = new TranslateTransform();
        plunger.RenderTransform = plungerMove;
        var capMove = new TranslateTransform();
        cap.RenderTransform = capMove;
        var liquidScale = new ScaleTransform(1, 1);
        liquid.RenderTransform = liquidScale;
        Movable(syringe, out _, out var rotate, out var move);
        rotate.Angle = -15;
        mimicHealProp = Place(syringe, HealArm.X - 4, HealArm.Y - 20, 2.0);
        var slide = new DoubleAnimationUsingKeyFrames();
        slide.KeyFrames.Add(new DiscreteDoubleKeyFrame(70, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        slide.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(.35)), new CubicEase()));
        slide.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.4))));
        slide.KeyFrames.Add(new LinearDoubleKeyFrame(60, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.7))));
        move.BeginAnimation(TranslateTransform.XProperty, slide);
        FadeInOut(syringe, 0, .2, 1.45, .25);
        plungerMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -34, TimeSpan.FromSeconds(.8)) { BeginTime = TimeSpan.FromSeconds(.4) });
        capMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -34, TimeSpan.FromSeconds(.8)) { BeginTime = TimeSpan.FromSeconds(.4) });
        liquidScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, .08, TimeSpan.FromSeconds(.8)) { BeginTime = TimeSpan.FromSeconds(.4) });
    }

    // A capsule drops into her mouth; she chews it down.
    private void MimicPill()
    {
        var pill = new Canvas { Width = 28, Height = 12 };
        pill.Children.Add(new Rectangle { Width = 28, Height = 12, RadiusX = 6, RadiusY = 6, Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromRgb(200, 60, 70)), StrokeThickness = 1.5 });
        var half = new Rectangle { Width = 14, Height = 12, Fill = new SolidColorBrush(Color.FromRgb(230, 70, 80)) };
        half.Clip = new RectangleGeometry(new Rect(0, 0, 14, 12), 6, 6);
        pill.Children.Add(half);
        Movable(pill, out var scale, out var rotate, out var move);
        mimicHealProp = Place(pill, HealMouth.X - 14, HealMouth.Y - 6, 1.6);
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-170, 0, TimeSpan.FromSeconds(.45)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
        rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 220, TimeSpan.FromSeconds(.45)));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(.15)) { BeginTime = TimeSpan.FromSeconds(.5) });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(.15)) { BeginTime = TimeSpan.FromSeconds(.5) });
        var gulp = new TextBlock { Text = "咕嘟～", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(80, 110, 190)) };
        Movable(gulp, out _, out _, out var rise);
        Place(gulp, HealMouth.X + 26, HealMouth.Y - 30, 1.5);
        FadeInOut(gulp, .6, .15, 1.15, .3);
        rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -24, TimeSpan.FromSeconds(.8)) { BeginTime = TimeSpan.FromSeconds(.6) });
        // Chewing with a happy face, from her snack animation (sitting part), when she is not busy.
        if ((DateTime.UtcNow - mimicLastGunUse).TotalSeconds > 1 && !dragging && TryFindRule("大口吃零食") is { } chew
            && animationPhase is "Idle" or "Event" or "Daily" or "MimicHeal" or "Mimic")
        {
            CancelMovement();
            PlayAnimation(chew, "MimicHeal", false, 4.6, 6.6);
        }
    }

    // Green "+" marks rising around her as the heal takes effect.
    private void HealSparkles(double delay, int version)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (version != mimicHealVersion || MimicLayer() == null) return;
            for (int i = 0; i < 6; i++)
            {
                var plus = new TextBlock { Text = "+", FontSize = 26 + companionRandom.Next(10), FontWeight = FontWeights.Black,
                    Foreground = new SolidColorBrush(Color.FromRgb(60, 205, 110)) };
                Movable(plus, out _, out _, out var rise);
                double begin = i * .12;
                Place(plus, 240 + companionRandom.NextDouble() * 140, 150 + companionRandom.NextDouble() * 140, 1.4 + begin);
                FadeInOut(plus, begin, .15, begin + .8, .4);
                rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -45, TimeSpan.FromSeconds(1)) { BeginTime = TimeSpan.FromSeconds(begin) });
            }
        };
        timer.Start();
    }

    private void MimicQuip(string[] lines, double chance)
    {
        var now = DateTime.UtcNow;
        if (now < mimicQuipCooldown || companionRandom.NextDouble() >= chance || bubble.Visibility == Visibility.Visible) return;
        mimicQuipCooldown = now.AddSeconds(75);
        DeltaSay(Fill(Pick(lines)), null);
    }

    private void LogMimic(string line)
    {
        try
        {
            const string file = "deltaforce-ocr.log";
            var info = new System.IO.FileInfo(file);
            if (info.Exists && info.Length > 256 * 1024) System.IO.File.Move(file, "deltaforce-ocr.old.log", true);
            System.IO.File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [模仿] {line}{Environment.NewLine}", System.Text.Encoding.UTF8);
        }
        catch { }
    }
}
