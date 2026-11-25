using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace BigBlueFish.Windows;

// What came over from dsh-pet into her window: the local 碎碎念, memes in the bubble (碎碎念 and chat), clicking the
// bubble away, the soft-candy bubble font, the grab cursors, catching her in flight for points, and the settings.
public partial class MainWindow
{
    private IReadOnlyList<CompanionMeme> dshMemes = Array.Empty<CompanionMeme>();
    private Cursor? grabCursor, grabbingCursor;
    private FontFamily? defaultBubbleFont, candyFont;
    private DateTime nextWhisper = DateTime.MaxValue;
    private string? lastWhisperLine;

    private CompanionDshPet.Physics CurrentPhysics => new(preferences.PhysicsGravity, preferences.PhysicsRestitution,
        preferences.PhysicsFriction, preferences.PhysicsCeiling, preferences.PhysicsPower);

    // InitializeCare.
    private void InitializeDshPet()
    {
        string root = CompanionDshPet.Root;
        dshMemes = CompanionDshPet.LoadMemes(root);
        try
        {
            string grab = Path.Combine(root, "cursors", "grab.cur"), grabbing = Path.Combine(root, "cursors", "grabbing.cur");
            if (File.Exists(grab)) grabCursor = new Cursor(grab);
            if (File.Exists(grabbing)) grabbingCursor = new Cursor(grabbing);
        }
        catch { grabCursor = grabbingCursor = null; }
        try
        {
            string fonts = Path.Combine(root, "fonts");
            if (File.Exists(Path.Combine(fonts, "上首软糖体.ttf")))
                candyFont = new FontFamily(new Uri(fonts + Path.DirectorySeparatorChar), "./#SSRuanTangTi");
        }
        catch { candyFont = null; }
        defaultBubbleFont = bubbleText.FontFamily;
        ApplyBubbleFont();
        ApplyGrabCursor(false);
        bubble.MouseLeftButtonUp += BubbleClicked;
        ApplyBubbleClickClose();
        ScheduleWhisper(DateTime.UtcNow);
    }

    private bool MemeOn(CompanionMeme meme) => preferences.MemesOff == null ? meme.DefaultOn : !preferences.MemesOff.Contains(meme.Name);
    private IEnumerable<CompanionMeme> EnabledMemes => dshMemes.Where(MemeOn);

    // ---------------------------------------------------------------- bubble: font, click to close, pictures
    private void ApplyBubbleFont()
    {
        var font = preferences.BubbleFont == "candy" && candyFont != null ? candyFont : defaultBubbleFont;
        if (font != null && bubbleText.FontFamily != font) bubbleText.FontFamily = font;
        CompanionEdgeLayout.Refresh(this);
    }

    private void ApplyBubbleClickClose() => bubble.Cursor = preferences.BubbleClickClose ? Cursors.Hand : null;

    // A click anywhere on the bubble (not on one of its buttons) closes it.
    private void BubbleClicked(object sender, MouseButtonEventArgs e)
    {
        if (!preferences.BubbleClickClose || e.Handled || bubble.Visibility != Visibility.Visible) return;
        for (var d = e.OriginalSource as DependencyObject; d != null && d != bubble; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ButtonBase) return;
        e.Handled = true;
        speech.Cancel();
        ClearBubbleActions();
        bubble.Visibility = Visibility.Collapsed;
    }

    private void ShowMemeInBubble(CompanionMeme meme) => ShowBubbleImage(meme.Path, 132, meme.Name);

    private void ShowStatusIcon(string name)
    {
        if (preferences.WorkStatus && CompanionDshPet.IconPath(name) is { } path) ShowBubbleImage(path, 30, null);
    }

    // ---------------------------------------------------------------- 碎碎念 (local, no model)
    private void ScheduleWhisper(DateTime now)
    {
        double seconds = Math.Clamp(preferences.WhisperSeconds, 60, 7200);
        nextWhisper = now.AddSeconds(seconds * (.85 + companionRandom.NextDouble() * .3));
    }

    // CareTick: every few minutes a line from the pool; waits while she is busy, talking or you are away.
    private void WhisperTick(DateTime now)
    {
        if (!preferences.WhisperEnabled || now < nextWhisper) return;
        if (!CanChatter || SpeechPaused || GameActive || bubble.Visibility == Visibility.Visible || speechRequestPending || weatherRequestPending
            || coast != null || targetMove != null || edgeHide != null || clickThrough)
        {
            nextWhisper = now.AddSeconds(30);
            return;
        }
        if (CompanionNative.IdleSeconds > 600) { nextWhisper = now.AddSeconds(60); return; } // nobody at the computer
        Whisper(null);
        ScheduleWhisper(now);
    }

    private string PickWhisperLine() =>
        CompanionDshPet.PickLine(CompanionDshPet.WhisperPool(preferences.WhisperLines), lastWhisperLine, companionRandom);

    private CompanionMeme? RandomMeme()
    {
        var memes = EnabledMemes.ToList();
        return memes.Count == 0 ? null : memes[companionRandom.Next(memes.Count)];
    }

    // Says a 碎碎念 (the given line, or one from the pool), with one of her three 碎碎念 clips and, about every other time,
    // a meme. Returns false when she cannot talk right now.
    private bool Whisper(string? text)
    {
        if (!companionReady || companionClosed || exitAnimation || PuppetActive || hiddenByUser || !IsVisible) return false;
        string line = text ?? PickWhisperLine();
        if (line.Length == 0) return false;
        lastWhisperLine = line;
        string? clip = null;
        if (!dragging && ActionAllowed(20))
        {
            var clips = CompanionDshPet.WhisperClips.Where(c => TryFindRule(c) != null).ToList();
            if (clips.Count > 0) clip = Pick(clips);
        }
        Say(line, clip);
        if (preferences.WhisperImages && companionRandom.NextDouble() < .5 && RandomMeme() is { } meme) ShowMemeInBubble(meme);
        PetEvent("whisper", line, said: line);
        return true;
    }

    // ---------------------------------------------------------------- memes in the chat
    private IEnumerable<CompanionMeme> OfferedMemes => preferences.ChatMemeLimit > 0 ? EnabledMemes.Take(preferences.ChatMemeLimit) : EnabledMemes;

    private string ChatMemeGuidance() => preferences.ChatMemes ? CompanionDshPet.MemeGuidance(OfferedMemes, 0) : "";

    // After a chat reply: the meme the model picked, if it is one it was offered.
    private void AttachChatMeme(string? name)
    {
        if (!preferences.ChatMemes || string.IsNullOrWhiteSpace(name) || bubble.Visibility != Visibility.Visible) return;
        if (OfferedMemes.FirstOrDefault(m => m.Name == name.Trim()) is { } meme) ShowMemeInBubble(meme);
    }

    // ---------------------------------------------------------------- cursors
    private void ApplyGrabCursor(bool grabbing)
    {
        var cursor = !preferences.GrabCursor ? null : grabbing ? grabbingCursor ?? grabCursor : grabCursor;
        if (Cursor != cursor) Cursor = cursor;
    }

    // ---------------------------------------------------------------- catching her in flight
    // OnCompanionPressed while she flies: points for the catch, sparks and a small card.
    private void CatchFlyingPet(double speed)
    {
        if (!preferences.CatchScore || speed < CompanionDshPet.ScoreMinSpeed) return;
        int score = CompanionDshPet.CatchScore(speed, SpriteSize * 640 / 360);
        if (score <= 0) return;
        string today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (preferences.ScoreDate != today) { preferences.ScoreDate = today; preferences.ScoreToday = 0; }
        preferences.ScoreToday += score;
        bool best = score > preferences.ScoreBest;
        if (best) preferences.ScoreBest = score;
        SaveCompanion();
        CompanionNative.GetCursorPos(out var p);
        var dpi = VisualTreeHelper.GetDpi(this);
        ShowScoreBurst(new Point(p.X / dpi.DpiScaleX, p.Y / dpi.DpiScaleY), score, speed, best);
        PetEvent("catch", best ? "新纪录" : "接住", score);
    }

    private void ShowScoreBurst(Point at, int score, double speed, bool best)
    {
        const double width = 300, height = 230, originX = 150, originY = 160;
        var canvas = new Canvas { Width = width, Height = height, IsHitTestVisible = false };
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(248, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 179, 0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 8, 16, 9),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = .22 },
            RenderTransformOrigin = new Point(.5, .5),
            RenderTransform = new ScaleTransform(.6, .6)
        };
        var font = candyFont ?? bubbleText.FontFamily;
        var lines = new StackPanel();
        lines.Children.Add(new TextBlock { Text = $"+{score} 分", FontSize = 22, FontWeight = FontWeights.Bold, FontFamily = font,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 143, 0)), HorizontalAlignment = HorizontalAlignment.Center });
        lines.Children.Add(new TextBlock { Text = $"速度 {speed:0} · 今天 {preferences.ScoreToday} 分{(best ? " · 新纪录！" : $" · 最高 {preferences.ScoreBest}")}",
            FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(160, 43, 43, 43)), HorizontalAlignment = HorizontalAlignment.Center });
        card.Child = lines;
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(card, originX - card.DesiredSize.Width / 2);
        Canvas.SetTop(card, Math.Max(0, originY - card.DesiredSize.Height - 34));
        canvas.Children.Add(card);
        var popup = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false,
            ShowActivated = false, Topmost = true, ResizeMode = ResizeMode.NoResize, Width = width, Height = height,
            Left = at.X - originX, Top = at.Y - originY, Content = canvas, Focusable = false
        };
        popup.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(popup).Handle;
            long style = CompanionNative.GetStyle(handle, -20).ToInt64();
            CompanionNative.SetStyle(handle, -20, new IntPtr(style | 0x20 | 0x80 | 0x08000000)); // transparent, tool window, no activate
        };
        // sparks: 20 warm dots thrown out from the click, falling and fading (skipped without animations)
        var sparks = new List<(Ellipse Dot, double Vx, double Vy, double Life)>();
        if (!ReducedMotion)
        {
            string[] colors = ["#ffb300", "#ff8f00", "#ff7043", "#f4511e", "#ffc400", "#ffd54f", "#ef5350"];
            for (int i = 0; i < 20; i++)
            {
                double angle = companionRandom.NextDouble() * Math.PI * 2, v = 120 + companionRandom.NextDouble() * 340, r = 3 + companionRandom.NextDouble() * 4;
                var dot = new Ellipse { Width = r * 2, Height = r * 2, Fill = (Brush)new BrushConverter().ConvertFromString(colors[i % colors.Length])! };
                Canvas.SetLeft(dot, originX - r);
                Canvas.SetTop(dot, originY - r);
                canvas.Children.Add(dot);
                sparks.Add((dot, Math.Cos(angle) * v, Math.Sin(angle) * v - 120, .5 + companionRandom.NextDouble() * .4));
            }
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Frame(object? sender, EventArgs e)
        {
            double t = clock.Elapsed.TotalSeconds;
            foreach (var (dot, vx, vy, life) in sparks)
            {
                if (t >= life) { dot.Visibility = Visibility.Collapsed; continue; }
                double r = dot.Width / 2;
                Canvas.SetLeft(dot, originX - r + vx * t);
                Canvas.SetTop(dot, originY - r + vy * t + 350 * t * t);
                dot.Opacity = 1 - t / life;
            }
            if (t > 2.2) { CompositionTarget.Rendering -= Frame; popup.Close(); }
            else if (t > 1.9) card.Opacity = Math.Max(0, (2.2 - t) / .3);
        }
        popup.Show();
        var pop = new DoubleAnimation(.6, 1, TimeSpan.FromSeconds(.28)) { EasingFunction = new BackEase { Amplitude = .4, EasingMode = EasingMode.EaseOut } };
        ((ScaleTransform)card.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        ((ScaleTransform)card.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        CompositionTarget.Rendering += Frame;
    }

    // ---------------------------------------------------------------- settings: 陪伴与外观
    private void AddDshPetLookSettings(Panel page)
    {
        page.Children.Add(new TextBlock { Text = "甩出手感与气泡", FontSize = 16, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(53, 76, 129)), Margin = new Thickness(0, 18, 0, 6) });
        var muted = Brushes.SlateGray;
        void Toggle(string label, bool value, Action<bool> change)
        {
            var box = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 4, 0, 4) };
            box.Click += (_, _) => { change(box.IsChecked == true); SaveCompanion(); };
            page.Children.Add(box);
        }
        var sliders = new List<(Slider Slider, double Default)>();
        void Number(string label, double min, double max, double step, double value, double fallback, Func<double, string> show, Action<double> change)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(new TextBlock { Text = label, Width = 80, VerticalAlignment = VerticalAlignment.Center });
            var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Width = 220, TickFrequency = step,
                IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(slider, label);
            var text = new TextBlock { Text = show(slider.Value), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 120 };
            slider.ValueChanged += (_, _) => { text.Text = show(slider.Value); change(slider.Value); SaveCompanion(); };
            row.Children.Add(slider);
            row.Children.Add(text);
            page.Children.Add(row);
            sliders.Add((slider, fallback));
        }
        var d = CompanionDshPet.Physics.Default;
        Number("重力", 0, 4000, 100, preferences.PhysicsGravity, d.Gravity, v => v <= 0 ? "无重力（慢慢漂停）" : $"{v:0}（默认 1400）", v => preferences.PhysicsGravity = v);
        Number("反弹力度", 0, 1, .02, preferences.PhysicsRestitution, d.Restitution, v => $"{v:0.00}（默认 0.78）", v => preferences.PhysicsRestitution = v);
        Number("地面摩擦", 0, 10, .5, preferences.PhysicsFriction, d.Friction, v => v <= 0 ? "冰面（一直滑）" : $"{v:0.0}（默认 2.5）", v => preferences.PhysicsFriction = v);
        Number("甩出力度", .3, 3, .1, preferences.PhysicsPower, d.Power, v => $"{v:0.0} 倍", v => preferences.PhysicsPower = v);
        var ceiling = new CheckBox { Content = "碰到屏幕顶部反弹（关掉则会飞出顶边，再靠重力落回来）", IsChecked = preferences.PhysicsCeiling, Margin = new Thickness(0, 4, 0, 4) };
        ceiling.Click += (_, _) => { preferences.PhysicsCeiling = ceiling.IsChecked == true; SaveCompanion(); };
        page.Children.Add(ceiling);
        var reset = BigBlueFish.Deployment.ModernUI.Button("恢复默认手感", () =>
        {
            foreach (var (slider, fallback) in sliders) slider.Value = fallback;
            ceiling.IsChecked = preferences.PhysicsCeiling = true;
            SaveCompanion();
        });
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        reset.Margin = new Thickness(0, 4, 0, 6);
        page.Children.Add(reset);
        Toggle("接住飞行中的鱼鱼得分：她被甩出去时点中她，爆出小火花并加分（越快、越小分越高）", preferences.CatchScore, v => preferences.CatchScore = v);
        Toggle("原地不动：她不自己走动、爬行和跑来跑去（点她、拖她、甩她和菜单里点播照常）", preferences.StayPut, v => preferences.StayPut = v);
        if (grabCursor != null)
            Toggle("抓手光标：鼠标放在鱼鱼身上时变成小手，拖动时握住", preferences.GrabCursor, v => { preferences.GrabCursor = v; ApplyGrabCursor(false); });
        Toggle("点一下气泡就收起（气泡上的按钮照常）", preferences.BubbleClickClose, v => { preferences.BubbleClickClose = v; ApplyBubbleClickClose(); });
        if (candyFont != null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 4) };
            row.Children.Add(new TextBlock { Text = "气泡字体", Width = 80, VerticalAlignment = VerticalAlignment.Center });
            var fonts = new ComboBox { MinWidth = 200 };
            System.Windows.Automation.AutomationProperties.SetName(fonts, "气泡字体");
            fonts.Items.Add(new ComboBoxItem { Content = "乐米小奶泡体（默认）" });
            fonts.Items.Add(new ComboBoxItem { Content = "上首软糖体", FontFamily = candyFont });
            fonts.SelectedIndex = preferences.BubbleFont == "candy" ? 1 : 0;
            fonts.SelectionChanged += (_, _) => { preferences.BubbleFont = fonts.SelectedIndex == 1 ? "candy" : ""; ApplyBubbleFont(); SaveCompanion(); };
            row.Children.Add(fonts);
            page.Children.Add(row);
        }
        page.Children.Add(new TextBlock { FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12),
            Text = "甩出手感参考了 dsh-pet：有重力时沿弧线飞、撞边反弹、落到屏幕底边后靠摩擦停下；重力调到 0 就是以前那样慢慢漂停。"
                + "要先在上面勾上「快速甩出」。表情包、状态小图标、抓手光标和软糖体来自 dsh-pet 的素材，只在本机使用。" });
    }

    // ---------------------------------------------------------------- settings: 互动与关怀
    private void AddDshPetCareSettings(StackPanel page, Action<string> section, Action<string> note, Func<string, bool, Action<bool>, CheckBox> toggle)
    {
        section("碎碎念与表情包");
        toggle("碎碎念：时不时自己说一句（从下面的语句池里抽，不调用模型）", preferences.WhisperEnabled, v => { preferences.WhisperEnabled = v; ScheduleWhisper(DateTime.UtcNow); });
        int[] minutes = [1, 2, 5, 10, 15, 20, 30, 60];
        var every = new ComboBox { MinWidth = 110, Margin = new Thickness(0, 2, 0, 6), HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetName(every, "碎碎念间隔");
        foreach (var m in minutes) every.Items.Add($"每 {m} 分钟左右");
        every.SelectedIndex = Array.IndexOf(minutes, minutes.OrderBy(m => Math.Abs(m * 60 - preferences.WhisperSeconds)).First());
        every.SelectionChanged += (_, _) =>
        {
            if (every.SelectedIndex < 0) return;
            preferences.WhisperSeconds = minutes[every.SelectedIndex] * 60;
            ScheduleWhisper(DateTime.UtcNow);
            SaveCompanion();
        };
        page.Children.Add(every);
        toggle("碎碎念时大约一半配一张表情包", preferences.WhisperImages, v => preferences.WhisperImages = v);
        page.Children.Add(new TextBlock { Text = "碎碎念语句池（一行一句；空着就用内置的 40 句）", Margin = new Thickness(0, 6, 0, 4) });
        var pool = new TextBox { Text = preferences.WhisperLines ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(4, 3, 4, 3) };
        System.Windows.Automation.AutomationProperties.SetName(pool, "碎碎念语句池");
        var count = new TextBlock { FontSize = 11, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 2, 0, 4) };
        void Count()
        {
            var lines = CompanionDshPet.WhisperPool(pool.Text);
            count.Text = ReferenceEquals(lines, CompanionDshPet.BuiltinWhisperLines) ? "现在用内置的 40 句。" : $"共 {lines.Count} 句。";
        }
        pool.TextChanged += (_, _) => Count();
        pool.LostFocus += (_, _) =>
        {
            string text = pool.Text.Replace("\r", "");
            preferences.WhisperLines = text.Length > 20000 ? text[..20000] : text;
            SaveCompanion();
        };
        Count();
        page.Children.Add(pool);
        page.Children.Add(count);
        if (string.IsNullOrWhiteSpace(pool.Text))
        {
            var fill = BigBlueFish.Deployment.ModernUI.Button("把内置的 40 句放进来改", () => pool.Text = string.Join("\n", CompanionDshPet.BuiltinWhisperLines));
            fill.HorizontalAlignment = HorizontalAlignment.Left;
            fill.Margin = new Thickness(0, 0, 0, 4);
            page.Children.Add(fill);
        }
        var preview = new Border { Background = new SolidColorBrush(Color.FromRgb(246, 249, 255)), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 4, 0, 4), Visibility = Visibility.Collapsed };
        var previewText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = bubbleText.FontFamily,
            Foreground = new SolidColorBrush(global::BigBlueFish.Windows.CompanionBubble.HairBlue) };
        var previewImage = new Image { Width = 96, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        var previewStack = new StackPanel();
        previewStack.Children.Add(previewText);
        previewStack.Children.Add(previewImage);
        preview.Child = previewStack;
        string? previewLine = null;
        CompanionMeme? previewMeme = null;
        var buttons = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        var tryOne = BigBlueFish.Deployment.ModernUI.Button("随机试一条", () =>
        {
            preferences.WhisperLines = pool.Text.Replace("\r", "");
            previewLine = CompanionDshPet.PickLine(CompanionDshPet.WhisperPool(preferences.WhisperLines), previewLine, companionRandom);
            previewMeme = preferences.WhisperImages && companionRandom.NextDouble() < .5 ? RandomMeme() : null;
            previewText.Text = previewLine;
            previewText.FontFamily = bubbleText.FontFamily;
            previewImage.Source = previewMeme != null ? LoadBubbleBitmap(previewMeme.Path) : null;
            previewImage.Visibility = previewImage.Source != null ? Visibility.Visible : Visibility.Collapsed;
            preview.Visibility = Visibility.Visible;
        });
        tryOne.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(tryOne);
        buttons.Children.Add(BigBlueFish.Deployment.ModernUI.Button("让鱼鱼说这句", () =>
        {
            if (previewLine == null) { previewLine = PickWhisperLine(); previewText.Text = previewLine; preview.Visibility = Visibility.Visible; }
            if (!Whisper(previewLine)) { previewText.Text = previewLine + "\n（鱼鱼现在不在桌面上，说不了）"; return; }
            if (previewMeme != null && bubble.Visibility == Visibility.Visible) ShowMemeInBubble(previewMeme);
        }));
        page.Children.Add(buttons);
        page.Children.Add(preview);

        toggle("聊天配图：让聊天模型按语境挑一张表情包（只给它前几张的名字和描述）", preferences.ChatMemes, v => preferences.ChatMemes = v);
        var limitRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        limitRow.Children.Add(new TextBlock { Text = "最多给模型", VerticalAlignment = VerticalAlignment.Center });
        int[] limits = [5, 10, 15, 20, 0];
        var limit = new ComboBox { MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(limit, "聊天配图张数");
        foreach (var l in limits) limit.Items.Add(l == 0 ? "全部" : $"{l} 张");
        limit.SelectedIndex = Math.Max(0, Array.IndexOf(limits, preferences.ChatMemeLimit));
        limit.SelectionChanged += (_, _) => { if (limit.SelectedIndex >= 0) { preferences.ChatMemeLimit = limits[limit.SelectedIndex]; SaveCompanion(); } };
        limitRow.Children.Add(limit);
        page.Children.Add(limitRow);
        if (dshMemes.Count > 0)
        {
            page.Children.Add(new TextBlock { Text = "用哪些表情包（鼠标停在名字上看图；排在前面的先给聊天模型）", Margin = new Thickness(0, 6, 0, 4) });
            var grid = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            foreach (var meme in dshMemes)
            {
                var image = new Image { Width = 140, Source = LoadBubbleBitmap(meme.Path) };
                var tip = new StackPanel { MaxWidth = 220 };
                tip.Children.Add(image);
                if (meme.Description.Length > 0) tip.Children.Add(new TextBlock { Text = meme.Description, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) });
                var box = new CheckBox { Content = meme.Name, IsChecked = MemeOn(meme), Width = 190, Margin = new Thickness(0, 2, 0, 2), ToolTip = tip };
                box.Click += (_, _) =>
                {
                    var off = (preferences.MemesOff ?? dshMemes.Where(m => !m.DefaultOn).Select(m => m.Name).ToList()).ToHashSet();
                    if (box.IsChecked == true) off.Remove(meme.Name); else off.Add(meme.Name);
                    preferences.MemesOff = dshMemes.Where(m => off.Contains(m.Name)).Select(m => m.Name).ToList();
                    SaveCompanion();
                };
                grid.Children.Add(box);
            }
            page.Children.Add(grid);
        }
        else note("没有找到表情包素材（BigBlueFish\\assets\\dshpet\\memes）：用部署文件夹里的 Build-EdgeCrawl.bat 装一次陪伴增强包就有了。");

        toggle("工作状态联动：她想回复时、帮我做事执行中 / 每做完一步 / 等你点执行 / 做完 / 失败时，演对应的动作并配上小图标", preferences.WorkStatus, v => preferences.WorkStatus = v);
        note("碎碎念参考 dsh-pet：从本地语句池随机抽一句，不连着说同一句，不联网、不调用模型；你离开电脑、在打游戏、专注或聊天时不说。"
            + "气泡停留多久、字多大跟「陪伴与外观」里的气泡设置走；也可以右键鱼鱼「碎碎念一句」马上听一句。"
            + "聊天配图会把这些表情包的名字和一句描述发给聊天模型（联网时发给所选的服务），张数越多，每句话越长。");
    }
}
